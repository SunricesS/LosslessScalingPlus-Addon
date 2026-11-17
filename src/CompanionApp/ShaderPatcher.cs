// ShaderPatcher.cs — Lossless Scaling Plus local shader optimizer (no Python required).
//
// Clean-room BYOB tool: contains ZERO proprietary code, shaders or weights. It reads the user's own
// Lossless.dll, disassembles the RT_RCDATA compute shaders that were compiled with `skipOptimization`
// using Windows' own D3DCompiler_47.dll, transposes 4x4 Conv2D dp4 blocks into column-major
// multiply-add chains, folds BatchNorm affine constants, recompiles with /O3 and writes the result
// back into the user's Lossless.dll (original kept as Lossless.dll.bak).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LosslessPlusCompanion;

internal static unsafe class D3DNative
{
    public const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 0x8000;

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile(
        byte[] pSrcData, nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string pSourceName,
        IntPtr pDefines, IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
        [MarshalAs(UnmanagedType.LPStr)] string pTarget,
        uint flags1, uint flags2, out IntPtr ppCode, out IntPtr ppErrorMsgs);

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DDisassemble(
        byte[] pSrcData, nuint srcDataSize, uint flags, IntPtr szComments, out IntPtr ppDisassembly);

    private static byte[] TakeBlob(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return Array.Empty<byte>();
        void** vtbl = *(void***)blob;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        var getPtr = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtbl[3];
        var getSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtbl[4];
        IntPtr p = getPtr(blob);
        int size = checked((int)getSize(blob));
        byte[] data = new byte[size];
        if (size > 0) Marshal.Copy(p, data, 0, size);
        release(blob);
        return data;
    }

    private static string BlobText(byte[] bytes)
    {
        int len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0) len--;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    public static string Disassemble(byte[] dxbc)
    {
        int hr = D3DDisassemble(dxbc, (nuint)dxbc.Length, 0, IntPtr.Zero, out IntPtr blob);
        if (hr != 0) throw new InvalidOperationException($"D3DDisassemble failed (hr=0x{hr:X8})");
        return BlobText(TakeBlob(blob));
    }

    public static byte[] Compile(string hlsl)
    {
        byte[] src = Encoding.UTF8.GetBytes(hlsl);
        int hr = D3DCompile(src, (nuint)src.Length, "lsfg3_opt.hlsl", IntPtr.Zero, IntPtr.Zero,
            "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out IntPtr code, out IntPtr errors);
        string err = BlobText(TakeBlob(errors));
        if (hr != 0)
        {
            TakeBlob(code);
            throw new InvalidOperationException($"D3DCompile failed (hr=0x{hr:X8}): {err}");
        }
        return TakeBlob(code);
    }
}

internal static class PeResources
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private static readonly IntPtr RT_RCDATA = (IntPtr)10;

    private delegate bool EnumResNameProc(IntPtr hModule, IntPtr type, IntPtr name, IntPtr lParam);
    private delegate bool EnumResLangProc(IntPtr hModule, IntPtr type, IntPtr name, ushort lang, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr hFile, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EnumResourceNamesW(IntPtr h, IntPtr type, EnumResNameProc cb, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EnumResourceLanguagesW(IntPtr h, IntPtr type, IntPtr name, EnumResLangProc cb, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResourceExW(IntPtr h, IntPtr type, IntPtr name, ushort lang);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr g);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string file, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateResourceW(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint cb);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResourceW(IntPtr h, bool discard);
    [DllImport("imagehlp.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapFileAndCheckSumW(string file, out uint headerSum, out uint checkSum);

    public sealed record RcData(int Id, ushort Lang, byte[] Data);

    /// <summary>Reads every integer-named RT_RCDATA resource (with its real language id).</summary>
    public static List<RcData> ReadAllRcData(string path)
    {
        IntPtr h = LoadLibraryExW(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (h == IntPtr.Zero)
            throw new IOException($"{Path.GetFileName(path)} açılamadı (Win32 hata {Marshal.GetLastWin32Error()}).");
        var result = new List<RcData>();
        try
        {
            var ids = new List<IntPtr>();
            EnumResourceNamesW(h, RT_RCDATA, (m, t, name, l) =>
            {
                if (((ulong)name >> 16) == 0) ids.Add(name);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr name in ids)
            {
                var langs = new List<ushort>();
                EnumResourceLanguagesW(h, RT_RCDATA, name, (m, t, n, lang, l) => { langs.Add(lang); return true; }, IntPtr.Zero);
                foreach (ushort lang in langs)
                {
                    IntPtr res = FindResourceExW(h, RT_RCDATA, name, lang);
                    if (res == IntPtr.Zero) continue;
                    uint size = SizeofResource(h, res);
                    IntPtr ptr = LockResource(LoadResource(h, res));
                    if (ptr == IntPtr.Zero) continue;
                    byte[] data = new byte[size];
                    Marshal.Copy(ptr, data, 0, (int)size);
                    result.Add(new RcData((int)(long)name, lang, data));
                }
            }
        }
        finally
        {
            FreeLibrary(h);
        }
        return result;
    }

    public static void UpdateRcData(string path, IReadOnlyList<RcData> updates)
    {
        IntPtr h = BeginUpdateResourceW(path, false);
        if (h == IntPtr.Zero)
            throw new IOException($"BeginUpdateResource başarısız (Win32 hata {Marshal.GetLastWin32Error()}).");
        bool ok = false;
        try
        {
            foreach (var u in updates)
            {
                if (!UpdateResourceW(h, RT_RCDATA, (IntPtr)u.Id, u.Lang, u.Data, (uint)u.Data.Length))
                    throw new IOException($"UpdateResource başarısız (ID {u.Id}, Win32 hata {Marshal.GetLastWin32Error()}).");
            }
            ok = true;
        }
        finally
        {
            if (!EndUpdateResourceW(h, !ok) && ok)
                throw new IOException($"EndUpdateResource başarısız (Win32 hata {Marshal.GetLastWin32Error()}).");
        }
    }

    public static void ApplyWaitableTimerSpinUpgrade(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (fs.Length < 0x44000) return;
            // Verify signature at file offset 0x13fbb (RVA 0x14bbb: mulsd xmm0, qword ptr [rip + 0x2fe4d])
            byte[] sig = new byte[8];
            fs.Position = 0x13FBB;
            if (fs.Read(sig, 0, 8) != 8) return;
            ReadOnlySpan<byte> expectedSig = stackalloc byte[] { 0xF2, 0x0F, 0x59, 0x05, 0x4D, 0xFE, 0x02, 0x00 };
            if (!sig.AsSpan().SequenceEqual(expectedSig)) return;

            // File offset 0x43810 (RVA 0x44a10): replace -2.0ms cushion with -3.0ms QPC spin-wait cushion
            byte[] curVal = new byte[8];
            fs.Position = 0x43810;
            if (fs.Read(curVal, 0, 8) != 8) return;
            double d = BitConverter.ToDouble(curVal, 0);
            if (Math.Abs(d - (-2.0)) < 0.001)
            {
                fs.Position = 0x43810;
                fs.Write(BitConverter.GetBytes(-3.0), 0, 8);
            }
        }
        catch { }
    }

    public static void UpdatePeChecksum(string path)
    {
        try
        {
            if (MapFileAndCheckSumW(path, out _, out uint sum) != 0) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
            fs.Position = 0x3C;
            int peOff = br.ReadInt32();
            fs.Position = peOff + 24 + 64;
            fs.Write(BitConverter.GetBytes(sum), 0, 4);
        }
        catch (DllNotFoundException) { }
    }

    /// <summary>
    /// SHA-256 of the first code section. Resource updates only rewrite .rsrc/.reloc, so two files with the
    /// same code hash are the same Lossless.dll build (original or patched); a different hash means a different build.
    /// </summary>
    public static string CodeSectionHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt16() != 0x5A4D) throw new InvalidDataException("PE değil (MZ yok).");
        fs.Position = 0x3C;
        int peOff = br.ReadInt32();
        fs.Position = peOff;
        if (br.ReadUInt32() != 0x00004550) throw new InvalidDataException("PE imzası yok.");
        br.ReadUInt16(); // machine
        int numSections = br.ReadUInt16();
        fs.Position = peOff + 20;
        int optSize = br.ReadUInt16();
        long secTable = peOff + 24 + optSize;
        for (int i = 0; i < numSections; i++)
        {
            fs.Position = secTable + i * 40 + 16;
            uint rawSize = br.ReadUInt32();
            uint rawPtr = br.ReadUInt32();
            fs.Position = secTable + i * 40 + 36;
            uint characteristics = br.ReadUInt32();
            if ((characteristics & 0x20) != 0) // IMAGE_SCN_CNT_CODE
            {
                fs.Position = rawPtr;
                byte[] data = br.ReadBytes((int)rawSize);
                return Convert.ToHexString(SHA256.HashData(data));
            }
        }
        throw new InvalidDataException("Kod bölümü bulunamadı.");
    }
}

/// <summary>DXBC assembly -> optimizable HLSL transpiler (port of the reference Python implementation).</summary>
internal sealed class LsfgTranspiler
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex ModAllRx = new(@"\s*\{[^}]+\}", RegexOptions.Compiled);
    private static readonly Regex ModFirstRx = new(@"\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex DstRegRx = new(@"^r(\d+)\.([xyzw]+)$", RegexOptions.Compiled);
    private static readonly Regex RegRxI = new(@"^r(\d+)\.([xyzw]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CbRxI = new(@"^cb0\[(\d+)\]\.([xyzw]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MovLitRx = new(@"^mov (r\d+\.xyzw) \{min16f\}, l\(([^)]+)\) \{def32 as min16f\}$", RegexOptions.Compiled);
    private static readonly Regex AddRx = new(@"^add (r\d+\.xyzw) \{min16f\}, (r\d+\.xyzw) \{min16f\}, (r\d+\.xyzw) \{min16f\}$", RegexOptions.Compiled);
    private static readonly Regex TMovRx = new(@"^mov (r\d+)\.xyzw \{min16f\}, l\(([^)]+)\) \{def32 as min16f\}$", RegexOptions.Compiled);
    private static readonly Regex TDp4Rx = new(@"^dp4 (r\d+)\.([xyzw]) \{min16f\}, (r\d+)\.xyzw \{min16f\}, (r\d+)\.xyzw \{min16f\}$", RegexOptions.Compiled);
    private static readonly Regex TAddRx = new(@"^add (r\d+)\.xyzw \{min16f\}, (r\d+)\.xyzw \{min16f\}, (r\d+)\.xyzw \{min16f\}$", RegexOptions.Compiled);
    private static readonly Regex OffsetRx = new(@"\((-?\d+),(-?\d+),(-?\d+)\)", RegexOptions.Compiled);

    public static readonly HashSet<int> UltraPerfShaderIds = new() { 293, 299, 300, 301 };

    private readonly Dictionary<string, string> _regType = new();

    private static string F6(double v) => v.ToString("F6", Inv);
    private static double ParseD(string s) => double.Parse(s.Trim(), NumberStyles.Float, Inv);
    private static List<double> ParseVec(string s) => s.Split(',').Select(ParseD).ToList();
    private static string StripMods(string s) => ModAllRx.Replace(s, "").Trim();
    private static int Ch(char c) => c switch { 'x' => 0, 'y' => 1, 'z' => 2, 'w' => 3, _ => throw new FormatException($"Bad component {c}") };
    private static string[] Ws(string s) => s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
    private static string VecT(string baseName, int n) => n > 1 ? $"{baseName}{n}" : baseName;

    public static List<string> SplitOperands(string s)
    {
        var res = new List<string>();
        int depth = 0;
        var cur = new StringBuilder();
        foreach (char ch in s)
        {
            if (ch is '(' or '{' or '[') { depth++; cur.Append(ch); }
            else if (ch is ')' or '}' or ']') { depth--; cur.Append(ch); }
            else if (ch == ',' && depth == 0) { res.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(ch);
        }
        if (cur.Length > 0) res.Add(cur.ToString().Trim());
        return res;
    }

    private static bool IsIntLiteral(string op)
    {
        op = StripMods(op);
        if (op.StartsWith('-')) op = op.Substring(1).Trim();
        if (op.StartsWith("l(") && op.EndsWith(')'))
        {
            string inner = op[2..^1];
            return !inner.Contains('.') && !inner.ToLowerInvariant().Contains('e');
        }
        return false;
    }

    private static string SelectSwizzle(string swiz, int[] mask)
    {
        if (swiz.Length == 1) return swiz;
        var sb = new StringBuilder(mask.Length);
        foreach (int i in mask) sb.Append(swiz[i]);
        return sb.ToString();
    }

    public static List<string> TransposeDp4Blocks(List<string> body)
    {
        var outp = new List<string>();
        int i = 0, n = body.Count;
        string[] swz = { "xxxx", "yyyy", "zzzz", "wwww" };
        while (i < n)
        {
            if (i + 7 < n)
            {
                var m0 = TMovRx.Match(body[i]); var m1 = TMovRx.Match(body[i + 1]);
                var m2 = TMovRx.Match(body[i + 2]); var m3 = TMovRx.Match(body[i + 3]);
                var d0 = TDp4Rx.Match(body[i + 4]); var d1 = TDp4Rx.Match(body[i + 5]);
                var d2 = TDp4Rx.Match(body[i + 6]); var d3 = TDp4Rx.Match(body[i + 7]);
                if (m0.Success && m1.Success && m2.Success && m3.Success && d0.Success && d1.Success && d2.Success && d3.Success)
                {
                    string rA = m0.Groups[1].Value, rB = m1.Groups[1].Value, rC = m2.Groups[1].Value, rD = m3.Groups[1].Value;
                    bool compsOk = d0.Groups[2].Value == "x" && d1.Groups[2].Value == "y" && d2.Groups[2].Value == "z" && d3.Groups[2].Value == "w";
                    bool weightsOk = d0.Groups[4].Value == rA && d1.Groups[4].Value == rB && d2.Groups[4].Value == rC && d3.Groups[4].Value == rD;
                    bool outOk = d0.Groups[1].Value == d1.Groups[1].Value && d1.Groups[1].Value == d2.Groups[1].Value && d2.Groups[1].Value == d3.Groups[1].Value;
                    bool inOk = d0.Groups[3].Value == d1.Groups[3].Value && d1.Groups[3].Value == d2.Groups[3].Value && d2.Groups[3].Value == d3.Groups[3].Value;
                    if (compsOk && weightsOk && outOk && inOk)
                    {
                        string rOut = d0.Groups[1].Value, rIn = d0.Groups[3].Value;
                        var rows = new[] { ParseVec(m0.Groups[2].Value), ParseVec(m1.Groups[2].Value), ParseVec(m2.Groups[2].Value), ParseVec(m3.Groups[2].Value) };
                        var cStrs = new string[4];
                        for (int c = 0; c < 4; c++)
                            cStrs[c] = string.Join(", ", Enumerable.Range(0, 4).Select(r => F6(rows[r][c])));

                        bool hasAdd = false; string rDst = null, rAcc = null;
                        if (i + 8 < n)
                        {
                            var ma = TAddRx.Match(body[i + 8]);
                            if (ma.Success && (ma.Groups[2].Value == rOut || ma.Groups[3].Value == rOut))
                            {
                                hasAdd = true;
                                rDst = ma.Groups[1].Value;
                                rAcc = ma.Groups[2].Value == rOut ? ma.Groups[3].Value : ma.Groups[2].Value;
                            }
                        }
                        if (hasAdd)
                        {
                            outp.Add($"mul {rOut}.xyzw {{min16f}}, {rIn}.xxxx {{min16f}}, l({cStrs[0]}) {{def32 as min16f}}");
                            outp.Add($"add {rDst}.xyzw {{min16f}}, {rAcc}.xyzw {{min16f}}, {rOut}.xyzw {{min16f}}");
                            for (int c = 1; c < 4; c++)
                            {
                                outp.Add($"mul {rOut}.xyzw {{min16f}}, {rIn}.{swz[c]} {{min16f}}, l({cStrs[c]}) {{def32 as min16f}}");
                                outp.Add($"add {rDst}.xyzw {{min16f}}, {rDst}.xyzw {{min16f}}, {rOut}.xyzw {{min16f}}");
                            }
                            i += 9;
                            continue;
                        }
                        outp.Add($"mul {rOut}.xyzw {{min16f}}, {rIn}.xxxx {{min16f}}, l({cStrs[0]}) {{def32 as min16f}}");
                        for (int c = 1; c < 4; c++)
                        {
                            outp.Add($"mul {rB}.xyzw {{min16f}}, {rIn}.{swz[c]} {{min16f}}, l({cStrs[c]}) {{def32 as min16f}}");
                            outp.Add($"add {rOut}.xyzw {{min16f}}, {rOut}.xyzw {{min16f}}, {rB}.xyzw {{min16f}}");
                        }
                        i += 8;
                        continue;
                    }
                }
            }
            outp.Add(body[i]);
            i++;
        }
        return outp;
    }

    public static List<string> FoldBatchNorm(List<string> body)
    {
        var newLines = new List<string>();
        int i = 0, n = body.Count;
        while (i < n)
        {
            int k = 0;
            var triplets = new List<(string rout, string racc, List<double> c1)>();
            while (i + k * 3 + 2 < n)
            {
                string l0 = body[i + k * 3], l1 = body[i + k * 3 + 1], l2 = body[i + k * 3 + 2];
                var m0 = MovLitRx.Match(l0);
                if (!m0.Success) break;
                string rt = m0.Groups[1].Value, c1Str = m0.Groups[2].Value;
                if (l1 != $"mov {rt} {{min16f}}, -{rt} {{min16f}}") break;
                var m2 = AddRx.Match(l2);
                if (!m2.Success) break;
                string rout = m2.Groups[1].Value, ra = m2.Groups[2].Value, rb = m2.Groups[3].Value;
                string racc = rb == rt && ra != rt ? null : null;
                racc = ra == rt ? rb : (rb == rt ? ra : null);
                if (racc == null) break;
                triplets.Add((rout, racc, ParseVec(c1Str)));
                k++;
            }

            if (k > 0 && i + k * 3 + k * 4 <= n)
            {
                var scales = new List<List<double>>();
                var biases = new List<List<double>>();
                int baseS = i + k * 3, baseB = baseS + k * 2;
                bool ok = true;
                for (int j = 0; j < k; j++)
                {
                    string rout = triplets[j].rout;
                    string ls0 = body[baseS + j * 2], ls1 = body[baseS + j * 2 + 1];
                    string lb0 = body[baseB + j * 2], lb1 = body[baseB + j * 2 + 1];
                    var ms0 = MovLitRx.Match(ls0);
                    var mb0 = MovLitRx.Match(lb0);
                    if (!ms0.Success || !mb0.Success) { ok = false; break; }
                    string rs = ms0.Groups[1].Value, rbReg = mb0.Groups[1].Value;
                    if (ls1 != $"mul {rout} {{min16f}}, {rout} {{min16f}}, {rs} {{min16f}}") { ok = false; break; }
                    if (lb1 != $"add {rout} {{min16f}}, {rout} {{min16f}}, {rbReg} {{min16f}}") { ok = false; break; }
                    scales.Add(ParseVec(ms0.Groups[2].Value));
                    biases.Add(ParseVec(mb0.Groups[2].Value));
                }
                if (ok)
                {
                    for (int j = 0; j < k; j++)
                    {
                        var (rout, racc, _) = triplets[j];
                        newLines.Add($"mul {rout} {{min16f}}, {racc} {{min16f}}, l({string.Join(", ", scales[j].Select(F6))}) {{def32 as min16f}}");
                    }
                    for (int j = 0; j < k; j++)
                    {
                        var (rout, _, c1) = triplets[j];
                        var c2 = scales[j]; var c3 = biases[j];
                        var folded = Enumerable.Range(0, 4).Select(m => c3[m] - c1[m] * c2[m]);
                        newLines.Add($"add {rout} {{min16f}}, {rout} {{min16f}}, l({string.Join(", ", folded.Select(F6))}) {{def32 as min16f}}");
                    }
                    i += k * 7;
                    continue;
                }
            }
            newLines.Add(body[i]);
            i++;
        }
        return newLines;
    }

    private void SetRegType(string rnum, string mask, string t)
    {
        foreach (char c in mask) _regType[rnum + c] = t;
    }

    private string GetRegType(string rnum, string swiz) =>
        _regType.TryGetValue(rnum + swiz[0], out var t) ? t : "float";

    private static (int[] mask, bool isMin16, string rnum, string maskStr) GetDstInfo(string dst)
    {
        dst = dst.Trim();
        if (dst == "null") return (Array.Empty<int>(), false, null, null);
        bool isMin16 = dst.Contains("{min16f}");
        string clean = StripMods(dst);
        var m = DstRegRx.Match(clean);
        if (!m.Success) throw new FormatException($"Unsupported destination: {dst}");
        string rnum = m.Groups[1].Value, mask = m.Groups[2].Value;
        return (mask.Select(Ch).ToArray(), isMin16, rnum, mask);
    }

    private string FormatOp(string op, int[] mask, string want)
    {
        op = op.Trim();
        int maskLen = mask.Length;
        string mod = null;
        var mMod = ModFirstRx.Match(op);
        if (mMod.Success)
        {
            mod = mMod.Groups[1].Value.Trim();
            op = StripMods(op);
        }
        bool neg = false;
        if (op.StartsWith('-')) { neg = true; op = op.Substring(1).Trim(); }
        string Neg(string e) => neg ? $"(-{e})" : e;

        if (op.StartsWith("l(") && op.EndsWith(')'))
        {
            var vals = op[2..^1].Split(',').Select(v => v.Trim()).ToList();
            string expr;
            static string FloatLit(string v) => (!v.Contains('.') && !v.ToLowerInvariant().Contains('e')) ? v + ".0" : v;
            if (vals.Count == 1)
            {
                string v = vals[0];
                expr = want switch { "uint" => $"uint({v})", "int" => $"int({v})", _ => $"float({FloatLit(v)})" };
            }
            else
            {
                var sel = mask.Select(i => vals[i]).ToList();
                if (want == "uint") expr = $"{VecT("uint", sel.Count)}({string.Join(", ", sel)})";
                else if (want == "int") expr = $"{VecT("int", sel.Count)}({string.Join(", ", sel)})";
                else
                {
                    var fvals = sel.Select(FloatLit).ToList();
                    expr = $"{VecT("float", fvals.Count)}({string.Join(", ", fvals)})";
                }
            }
            if (mod is "min16f" or "def32 as min16f" || want == "min16f")
                expr = $"{VecT("min16float", maskLen)}({expr})";
            return Neg(expr);
        }

        if (op.StartsWith("vThreadIDInGroup") || op.StartsWith("vThreadID"))
        {
            var parts = op.Split('.');
            string name = parts[0];
            string raw = parts.Length > 1 ? parts[1] : "xyzw";
            string expr = $"{name}.{SelectSwizzle(raw, mask)}";
            if (want == "int") expr = $"{VecT("int", maskLen)}({expr})";
            else if (want == "float") expr = $"asfloat({expr})";
            return Neg(expr);
        }

        var mCb = CbRxI.Match(op);
        if (mCb.Success)
        {
            string expr = $"cb0[{mCb.Groups[1].Value}].{SelectSwizzle(mCb.Groups[2].Value, mask)}";
            if (want == "uint") expr = $"asuint({expr})";
            else if (want == "int") expr = $"asint({expr})";
            else if (mod is "min16f" or "def32 as min16f" || want == "min16f") expr = $"{VecT("min16float", maskLen)}({expr})";
            return Neg(expr);
        }

        var mReg = RegRxI.Match(op);
        if (mReg.Success)
        {
            string rnum = mReg.Groups[1].Value;
            string swiz = SelectSwizzle(mReg.Groups[2].Value, mask);
            string expr;
            if (mod == "min16f")
            {
                expr = $"h{rnum}.{swiz}";
                if (want == "float") expr = $"{VecT("float", maskLen)}({expr})";
            }
            else if (mod == "min16f as def32")
            {
                expr = $"{VecT("float", maskLen)}(h{rnum}.{swiz})";
            }
            else if (mod == "def32 as min16f")
            {
                string b = GetRegType(rnum, swiz) == "float" ? $"r{rnum}.{swiz}" : $"asfloat(ur{rnum}.{swiz})";
                expr = $"{VecT("min16float", maskLen)}({b})";
            }
            else
            {
                string curT = GetRegType(rnum, swiz);
                if (want is "uint" or "int")
                {
                    expr = curT == "uint" ? $"ur{rnum}.{swiz}" : $"asuint(r{rnum}.{swiz})";
                    if (want == "int") expr = $"{VecT("int", maskLen)}({expr})";
                }
                else
                {
                    expr = curT == "float" ? $"r{rnum}.{swiz}" : $"asfloat(ur{rnum}.{swiz})";
                    if (want == "min16f") expr = $"{VecT("min16float", maskLen)}({expr})";
                }
            }
            return Neg(expr);
        }
        throw new FormatException($"Unknown operand: {op}");
    }

    private string CommitDst(string dst, string setT)
    {
        var (_, isMin16, rnum, mask) = GetDstInfo(dst);
        if (rnum == null) return "None";
        if (isMin16) return $"h{rnum}.{mask}";
        SetRegType(rnum, mask, setT);
        return setT == "uint" ? $"ur{rnum}.{mask}" : $"r{rnum}.{mask}";
    }

    private static (string tex, string swiz) SplitTex(string s)
    {
        var p = s.Split('.');
        if (p.Length != 2) throw new FormatException($"Bad texture operand: {s}");
        return (p[0], p[1]);
    }

    public static string Transpile(string asmText, bool ultraPerf, int shaderId) =>
        new LsfgTranspiler().TranspileCore(asmText, ultraPerf, shaderId);

    private string TranspileCore(string asmText, bool ultraPerf, int shaderId)
    {
        var lines = asmText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//")).ToList();

        int cbSize = 2;
        var samplers = new List<string>(); var textures = new List<string>(); var uavs = new List<string>();
        int numTemps = 16;
        int[] tg = { 8, 8, 1 };
        bool hasTgsm = false;
        int tgsmStride = 128, tgsmCount = 32;
        var body = new List<string>();

        foreach (string l in lines)
        {
            if (l.StartsWith("cs_")) continue;
            if (l.StartsWith("dcl_"))
            {
                if (l.StartsWith("dcl_constantbuffer"))
                {
                    var m = Regex.Match(l, @"CB0\[(\d+)\]", RegexOptions.IgnoreCase);
                    if (m.Success) cbSize = int.Parse(m.Groups[1].Value, Inv);
                }
                else if (l.StartsWith("dcl_sampler"))
                {
                    var m = Regex.Match(l, @"\b(s\d+)\b"); if (m.Success) samplers.Add(m.Groups[1].Value);
                }
                else if (l.StartsWith("dcl_resource_texture2d"))
                {
                    var m = Regex.Match(l, @"\b(t\d+)\b"); if (m.Success) textures.Add(m.Groups[1].Value);
                }
                else if (l.StartsWith("dcl_uav_typed_texture2d"))
                {
                    var m = Regex.Match(l, @"\b(u\d+)\b"); if (m.Success) uavs.Add(m.Groups[1].Value);
                }
                else if (l.StartsWith("dcl_temps")) numTemps = int.Parse(Ws(l)[1], Inv);
                else if (l.StartsWith("dcl_thread_group")) tg = Ws(l).Skip(1).Select(x => int.Parse(x.Trim(','), Inv)).ToArray();
                else if (l.StartsWith("dcl_tgsm_structured"))
                {
                    hasTgsm = true;
                    var parts = Ws(l).Skip(1).Select(x => x.Trim(',')).ToArray();
                    tgsmStride = int.Parse(parts[1], Inv);
                    tgsmCount = int.Parse(parts[2], Inv);
                }
                continue;
            }
            body.Add(l);
        }

        body = TransposeDp4Blocks(body);
        body = FoldBatchNorm(body);

        var h = new List<string> { $"cbuffer cb : register(b0) {{ float4 cb0[{cbSize}]; }};" };
        foreach (var s in samplers) h.Add($"SamplerState {s} : register({s});");
        foreach (var t in textures) h.Add($"Texture2D<float4> {t} : register({t});");
        foreach (var u in uavs) h.Add($"RWTexture2D<float4> {u} : register({u});");
        if (hasTgsm) h.Add($"groupshared float g0[{tgsmCount}][{tgsmStride / 4}];");
        h.Add($"[numthreads({tg[0]}, {tg[1]}, {tg[2]})]");
        h.Add("void main(uint3 vThreadID : SV_DispatchThreadID, uint3 vThreadIDInGroup : SV_GroupThreadID) {");
        for (int i = 0; i < numTemps; i++)
            h.Add($"    float4 r{i} = 0; uint4 ur{i} = 0; min16float4 h{i} = 0;");

        string indent = "    ";
        void Dedent() => indent = indent.Length >= 4 ? indent[..^4] : "";
        int[] xy = { 0, 1 }, x0 = { 0 }, xyzw = { 0, 1, 2, 3 }, xyz = { 0, 1, 2 };

        foreach (string l in body)
        {
            if (l == "ret") continue;
            if (l == "else") { Dedent(); h.Add($"{indent}}} else {{"); indent += "    "; continue; }
            if (l == "endif") { Dedent(); h.Add($"{indent}}}"); continue; }
            if (l == "sync_g_t") { h.Add($"{indent}GroupMemoryBarrierWithGroupSync();"); continue; }

            int sp = l.IndexOfAny(new[] { ' ', '\t' });
            string op = sp < 0 ? l : l[..sp];
            var ops = SplitOperands(sp < 0 ? "" : l[sp..].TrimStart());

            switch (op)
            {
                case "if_z":
                    h.Add($"{indent}if ({FormatOp(ops[0], x0, "uint")} == 0u) {{"); indent += "    "; break;
                case "if_nz":
                    h.Add($"{indent}if ({FormatOp(ops[0], x0, "uint")} != 0u) {{"); indent += "    "; break;
                case "mov":
                {
                    string dstStr = ops[0], srcStr = ops[1];
                    var (mask, isMin16Dst, _, _) = GetDstInfo(dstStr);
                    if (isMin16Dst)
                    {
                        string src = FormatOp(srcStr, mask, "min16f");
                        string target = CommitDst(dstStr, "min16f");
                        h.Add($"{indent}{target} = {src};");
                    }
                    else
                    {
                        string srcClean = StripMods(srcStr);
                        bool srcIsUint = false;
                        if (srcClean.StartsWith("vThreadID") || IsIntLiteral(srcStr))
                        {
                            if (!srcStr.Contains('{')) srcIsUint = true;
                        }
                        else
                        {
                            var mr = DstRegRx.Match(srcClean);
                            if (mr.Success && !srcStr.Contains('{'))
                                srcIsUint = GetRegType(mr.Groups[1].Value, SelectSwizzle(mr.Groups[2].Value, mask)) == "uint";
                        }
                        string tKind = srcIsUint ? "uint" : "float";
                        string src = FormatOp(srcStr, mask, tKind);
                        string target = CommitDst(dstStr, tKind);
                        h.Add($"{indent}{target} = {src};");
                    }
                    break;
                }
                case "add": case "mul": case "div":
                {
                    var (mask, isMin16, _, _) = GetDstInfo(ops[0]);
                    string want = isMin16 ? "min16f" : "float";
                    string a = FormatOp(ops[1], mask, want), b = FormatOp(ops[2], mask, want);
                    string target = CommitDst(ops[0], want);
                    string sym = op == "add" ? "+" : op == "mul" ? "*" : "/";
                    h.Add($"{indent}{target} = {a} {sym} {b};");
                    break;
                }
                case "max": case "min": case "dp4": case "dp3":
                {
                    var (mask, isMin16, _, _) = GetDstInfo(ops[0]);
                    string want = isMin16 ? "min16f" : "float";
                    int[] srcIdx = op == "dp4" ? xyzw : op == "dp3" ? xyz : mask;
                    string a = FormatOp(ops[1], srcIdx, want), b = FormatOp(ops[2], srcIdx, want);
                    string target = CommitDst(ops[0], want);
                    string fn = op is "dp4" or "dp3" ? "dot" : op;
                    h.Add($"{indent}{target} = {fn}({a}, {b});");
                    break;
                }
                case "exp":
                {
                    var (mask, isMin16, _, _) = GetDstInfo(ops[0]);
                    string want = isMin16 ? "min16f" : "float";
                    string a = FormatOp(ops[1], mask, want);
                    string target = CommitDst(ops[0], want);
                    h.Add($"{indent}{target} = exp2({a});");
                    break;
                }
                case "utof": case "itof":
                {
                    var (mask, _, _, _) = GetDstInfo(ops[0]);
                    string a = FormatOp(ops[1], mask, op == "utof" ? "uint" : "int");
                    string target = CommitDst(ops[0], "float");
                    h.Add($"{indent}{target} = {VecT("float", mask.Length)}({a});");
                    break;
                }
                case "iadd": case "and":
                {
                    var (mask, _, _, _) = GetDstInfo(ops[0]);
                    string a = FormatOp(ops[1], mask, "uint"), b = FormatOp(ops[2], mask, "uint");
                    string target = CommitDst(ops[0], "uint");
                    h.Add($"{indent}{target} = {a} {(op == "iadd" ? "+" : "&")} {b};");
                    break;
                }
                case "ine": case "ieq":
                {
                    var (mask, _, _, _) = GetDstInfo(ops[0]);
                    string a = FormatOp(ops[1], mask, "uint"), b = FormatOp(ops[2], mask, "uint");
                    string target = CommitDst(ops[0], "uint");
                    h.Add($"{indent}{target} = ({a} {(op == "ine" ? "!=" : "==")} {b}) ? 0xFFFFFFFFu : 0u;");
                    break;
                }
                case "ge":
                {
                    var (mask, _, _, _) = GetDstInfo(ops[0]);
                    string wantSrc = (ops[1].Contains("{min16f}") || ops[2].Contains("{min16f}")) ? "min16f" : "float";
                    string a = FormatOp(ops[1], mask, wantSrc), b = FormatOp(ops[2], mask, wantSrc);
                    string target = CommitDst(ops[0], "uint");
                    h.Add($"{indent}{target} = ({a} >= {b}) ? 0xFFFFFFFFu : 0u;");
                    break;
                }
                case "movc":
                {
                    var (mask, isMin16, _, _) = GetDstInfo(ops[0]);
                    string want = isMin16 ? "min16f" : "float";
                    string cond = FormatOp(ops[1], mask, "uint");
                    string tv = FormatOp(ops[2], mask, want), fv = FormatOp(ops[3], mask, want);
                    string target = CommitDst(ops[0], want);
                    h.Add($"{indent}{target} = ({cond} != 0u) ? {tv} : {fv};");
                    break;
                }
                case "imul":
                {
                    var (mask, _, _, _) = GetDstInfo(ops[1]);
                    string a = FormatOp(ops[2], mask, "uint"), b = FormatOp(ops[3], mask, "uint");
                    string target = CommitDst(ops[1], "uint");
                    h.Add($"{indent}{target} = {a} * {b};");
                    break;
                }
                case "udiv":
                {
                    string dq = ops[0], dr = ops[1], s0 = ops[2], s1 = ops[3];
                    if (dq != "null")
                    {
                        var (mask, _, _, _) = GetDstInfo(dq);
                        string t = CommitDst(dq, "uint");
                        h.Add($"{indent}{t} = {FormatOp(s0, mask, "uint")} / {FormatOp(s1, mask, "uint")};");
                    }
                    if (dr != "null")
                    {
                        var (mask, _, _, _) = GetDstInfo(dr);
                        string t = CommitDst(dr, "uint");
                        h.Add($"{indent}{t} = {FormatOp(s0, mask, "uint")} % {FormatOp(s1, mask, "uint")};");
                    }
                    break;
                }
                case "store_uav_typed":
                {
                    string uav = ops[0].Split('.')[0];
                    h.Add($"{indent}{uav}[{FormatOp(ops[1], xy, "uint")}] = {FormatOp(ops[2], xyzw, "float")};");
                    break;
                }
                case "store_structured":
                {
                    string idx = FormatOp(ops[1], x0, "uint"), boff = FormatOp(ops[2], x0, "uint"), val = FormatOp(ops[3], x0, "float");
                    h.Add($"{indent}g0[({idx}) & 31u][(({boff}) >> 2u) & 31u] = {val};");
                    break;
                }
                case "ld_structured":
                {
                    string idx = FormatOp(ops[1], x0, "uint"), boff = FormatOp(ops[2], x0, "uint");
                    var (_, isMin16, _, _) = GetDstInfo(ops[0]);
                    string target = CommitDst(ops[0], isMin16 ? "min16f" : "float");
                    string valExpr = $"g0[({idx}) & 31u][(({boff}) >> 2u) & 31u]";
                    if (isMin16) valExpr = $"min16float({valExpr})";
                    h.Add($"{indent}{target} = {valExpr};");
                    break;
                }
                default:
                    if (op.StartsWith("resinfo_indexable"))
                    {
                        var (mask, _, _, _) = GetDstInfo(ops[0]);
                        var (tex, raw) = SplitTex(ops[2]);
                        string texSwiz = SelectSwizzle(raw, mask);
                        string target = CommitDst(ops[0], "float");
                        h.Add($"{indent}{{ uint _w, _h; {tex}.GetDimensions(_w, _h); {target} = float4(_w, _h, 0, 0).{texSwiz}; }}");
                    }
                    else if (op.StartsWith("sample_l_indexable"))
                    {
                        var (mask, _, _, _) = GetDstInfo(ops[0]);
                        string uv = FormatOp(ops[1], xy, "float");
                        var (tex, raw) = SplitTex(ops[2]);
                        string texSwiz = SelectSwizzle(raw, mask);
                        string samp = ops[3], lod = FormatOp(ops[4], x0, "float");
                        string target = CommitDst(ops[0], "float");
                        h.Add($"{indent}{target} = {tex}.SampleLevel({samp}, {uv}, {lod}).{texSwiz};");
                    }
                    else if (op.StartsWith("sample_l_aoffimmi_indexable"))
                    {
                        var mOff = OffsetRx.Match(op);
                        int ox = int.Parse(mOff.Groups[1].Value, Inv), oy = int.Parse(mOff.Groups[2].Value, Inv);
                        var (mask, _, _, _) = GetDstInfo(ops[0]);
                        string uv = FormatOp(ops[1], xy, "float");
                        var (tex, raw) = SplitTex(ops[2]);
                        string texSwiz = SelectSwizzle(raw, mask);
                        string samp = ops[3], lod = FormatOp(ops[4], x0, "float");
                        string target = CommitDst(ops[0], "float");
                        if (ultraPerf && UltraPerfShaderIds.Contains(shaderId) && Math.Abs(ox) == 1 && Math.Abs(oy) == 1)
                            h.Add($"{indent}{target} = ({tex}.SampleLevel({samp}, {uv}, {lod}, int2({ox}, {oy})).{texSwiz}) * 0.0;");
                        else
                            h.Add($"{indent}{target} = {tex}.SampleLevel({samp}, {uv}, {lod}, int2({ox}, {oy})).{texSwiz};");
                    }
                    else
                    {
                        throw new NotSupportedException($"Unhandled opcode: {op}");
                    }
                    break;
            }
        }
        h.Add("}");
        return string.Join("\n", h);
    }
}

public sealed record PatchReport(
    int Candidates, int Optimized, List<string> Skipped,
    long OrigSlots, long OptSlots, TimeSpan Elapsed, string SourceNote, string UltraPerfPath);

public enum InstallState { NotFound, Original, Patched, NoCandidates, BackupFromOtherBuild }

/// <summary>Owns Lossless.dll / Lossless.dll.bak lifecycle: backup, patch, restore and status.</summary>
public static class ShaderPatchService
{
    public const string DllName = "Lossless.dll";
    public const string BakName = "Lossless.dll.bak";
    public const string UltraName = "Lossless_UltraPerf.dll";
    private static readonly Regex SlotsRx = new(@"Approximately (\d+) instruction slots used", RegexOptions.Compiled);

    private sealed record Candidate(int Id, ushort Lang, string Asm, int Slots);
    private sealed record Optimized(int Id, ushort Lang, byte[] Dxbc, int OrigSlots, int OptSlots);

    public static string ResolveInstallDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        if (Directory.Exists(path))
        {
            if (File.Exists(Path.Combine(path, DllName))) return Path.GetFullPath(path);
            string steamSub = Path.Combine(path, "steamapps", "common", "Lossless Scaling");
            if (File.Exists(Path.Combine(steamSub, DllName))) return Path.GetFullPath(steamSub);
            string commonSub = Path.Combine(path, "Lossless Scaling");
            if (File.Exists(Path.Combine(commonSub, DllName))) return Path.GetFullPath(commonSub);
            return null;
        }
        if (File.Exists(path))
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir != null && File.Exists(Path.Combine(dir, DllName))) return dir;
        }
        return null;
    }

    private static void EnsureLosslessScalingClosed()
    {
        var running = Process.GetProcessesByName("LosslessScaling");
        foreach (var p in running) p.Dispose();
        if (running.Length > 0)
            throw new InvalidOperationException("Lossless Scaling şu anda açık. Lütfen önce Lossless Scaling'i (sistem tepsisi dahil) tamamen kapatın.");
    }

    private static int Slots(string asm)
    {
        var m = SlotsRx.Match(asm);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    private static bool IsSkipOptimizedComputeShader(string asm)
    {
        var head = asm.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//")).Take(2).ToList();
        return head.Count == 2 && head[0] == "cs_5_0" && head[1].StartsWith("dcl_globalFlags") && head[1].Contains("skipOptimization");
    }

    private static List<Candidate> FindCandidates(string dllPath, out int dxbcCount)
    {
        var all = PeResources.ReadAllRcData(dllPath);
        var result = new List<Candidate>();
        int dxbc = 0;
        foreach (var r in all)
        {
            if (r.Data.Length < 4 || r.Data[0] != 'D' || r.Data[1] != 'X' || r.Data[2] != 'B' || r.Data[3] != 'C') continue;
            dxbc++;
            string asm;
            try { asm = D3DNative.Disassemble(r.Data); } catch { continue; }
            if (IsSkipOptimizedComputeShader(asm)) result.Add(new Candidate(r.Id, r.Lang, asm, Slots(asm)));
        }
        dxbcCount = dxbc;
        return result.OrderBy(c => c.Id).ToList();
    }

    /// <summary>Binding-signature guard: the optimized shader may drop unused bindings but never add or change one.</summary>
    private static string ValidateSignature(string origAsm, string optAsm)
    {
        static List<string> Dcls(string asm) => asm.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("dcl_")).ToList();
        var o = Dcls(origAsm); var n = Dcls(optAsm);
        string Tg(List<string> d) => d.FirstOrDefault(l => l.StartsWith("dcl_thread_group")) ?? "";
        if (Tg(o) != Tg(n)) return $"thread group farklı ({Tg(o)} / {Tg(n)})";
        var bindKinds = new[] { "dcl_sampler", "dcl_resource_", "dcl_uav_" };
        var origBind = new HashSet<string>(o.Where(l => bindKinds.Any(l.StartsWith)));
        foreach (var l in n.Where(l => bindKinds.Any(l.StartsWith)))
            if (!origBind.Contains(l)) return $"bağlama imzası farklı ({l})";
        int Cb(List<string> d)
        {
            var c = d.FirstOrDefault(l => l.StartsWith("dcl_constantbuffer"));
            if (c == null) return 0;
            var m = Regex.Match(c, @"CB0\[(\d+)\]", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }
        if (Cb(n) > Cb(o)) return "sabit tampon boyutu büyüdü";
        return null;
    }

    private static List<Optimized> OptimizeAll(IReadOnlyList<Candidate> cands, bool ultraPerf, ConcurrentBag<string> skipped,
        Action<int, int> progress, CancellationToken ct)
    {
        var results = new ConcurrentBag<Optimized>();
        int done = 0;
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = ct };
        Parallel.ForEach(cands, po, c =>
        {
            try
            {
                string hlsl = LsfgTranspiler.Transpile(c.Asm, ultraPerf, c.Id);
                byte[] dxbc = D3DNative.Compile(hlsl);
                string optAsm = D3DNative.Disassemble(dxbc);
                string sigErr = ValidateSignature(c.Asm, optAsm);
                if (sigErr != null) skipped.Add($"ID {c.Id}: {sigErr}");
                else results.Add(new Optimized(c.Id, c.Lang, dxbc, c.Slots, Slots(optAsm)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add($"ID {c.Id}: {ex.Message.Split('\n')[0]}");
            }
            progress?.Invoke(Interlocked.Increment(ref done), cands.Count);
        });
        return results.OrderBy(r => r.Id).ToList();
    }

    private static void WriteVariant(string sourcePath, string destPath, IReadOnlyList<Optimized> opt)
    {
        string tmp = destPath + ".lsplus.tmp";
        File.Copy(sourcePath, tmp, true);
        try
        {
            PeResources.UpdateRcData(tmp, opt.Select(o => new PeResources.RcData(o.Id, o.Lang, o.Dxbc)).ToList());
            PeResources.ApplyWaitableTimerSpinUpgrade(tmp);
            PeResources.UpdatePeChecksum(tmp);

            // Read back and verify every replaced resource before touching the live file.
            var readBack = PeResources.ReadAllRcData(tmp).ToDictionary(r => (r.Id, r.Lang), r => r.Data);
            foreach (var o in opt)
                if (!readBack.TryGetValue((o.Id, o.Lang), out var d) || !d.AsSpan().SequenceEqual(o.Dxbc))
                    throw new IOException($"Doğrulama başarısız: ID {o.Id} yazılan veriyle eşleşmiyor.");

            File.Move(tmp, destPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Selects the original (unpatched) source for this Lossless.dll build, refreshing a stale backup after a Steam update.</summary>
    private static string PrepareBackup(string dir, Action<string> log)
    {
        string dll = Path.Combine(dir, DllName), bak = Path.Combine(dir, BakName);
        if (!File.Exists(bak))
        {
            File.Copy(dll, bak, false);
            log?.Invoke($"Yedek oluşturuldu: {bak}");
            return "Mevcut Lossless.dll yedeklendi ve kaynak olarak kullanıldı.";
        }
        if (PeResources.CodeSectionHash(bak) == PeResources.CodeSectionHash(dll))
            return "Aynı sürüme ait Lossless.dll.bak yedeği kaynak olarak kullanıldı.";

        // Different build: Lossless Scaling was updated by Steam, current Lossless.dll is the new original.
        string old = bak + ".old";
        File.Copy(bak, old, true);
        File.Copy(dll, bak, true);
        log?.Invoke($"Lossless Scaling güncellenmiş: eski yedek {Path.GetFileName(old)} olarak saklandı, yeni Lossless.dll yedeklendi.");
        return "Steam güncellemesi algılandı; yeni sürüm yedeklenip kaynak olarak kullanıldı.";
    }

    public static PatchReport Patch(string dir, bool buildUltraPerf, Action<string> log, Action<int, int> progress, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string dll = Path.Combine(dir, DllName);
        if (!File.Exists(dll)) throw new FileNotFoundException($"{DllName} bulunamadı: {dir}");
        EnsureLosslessScalingClosed();

        string note = PrepareBackup(dir, log);
        string source = Path.Combine(dir, BakName);

        log?.Invoke("Shader kaynakları taranıyor (D3DCompiler_47.dll)...");
        var cands = FindCandidates(source, out int dxbcCount);
        log?.Invoke($"{dxbcCount} DXBC shader bulundu; optimize edilmemiş (skipOptimization) aday: {cands.Count}");
        if (cands.Count == 0)
            return new PatchReport(0, 0, new List<string>(), 0, 0, sw.Elapsed, note + " Optimize edilecek shader bulunamadı.", null);

        var skipped = new ConcurrentBag<string>();
        var opt = OptimizeAll(cands, false, skipped, progress, ct);
        ct.ThrowIfCancellationRequested();
        if (opt.Count == 0)
            return new PatchReport(cands.Count, 0, skipped.OrderBy(s => s).ToList(), 0, 0, sw.Elapsed, note + " Hiçbir shader güvenle optimize edilemedi; dosya değiştirilmedi.", null);

        EnsureLosslessScalingClosed();
        WriteVariant(source, dll, opt);

        string ultraPath = null;
        if (buildUltraPerf)
        {
            var upCands = cands.Where(c => LsfgTranspiler.UltraPerfShaderIds.Contains(c.Id)).ToList();
            var upSkipped = new ConcurrentBag<string>();
            var upOpt = OptimizeAll(upCands, true, upSkipped, null, ct).ToDictionary(o => o.Id);
            var merged = opt.Select(o => upOpt.TryGetValue(o.Id, out var u) ? u : o).ToList();
            ultraPath = Path.Combine(dir, UltraName);
            WriteVariant(source, ultraPath, merged);
        }

        return new PatchReport(cands.Count, opt.Count, skipped.OrderBy(s => s).ToList(),
            opt.Sum(o => (long)o.OrigSlots), opt.Sum(o => (long)o.OptSlots), sw.Elapsed, note, ultraPath);
    }

    public static string Restore(string dir)
    {
        string dll = Path.Combine(dir, DllName), bak = Path.Combine(dir, BakName);
        if (!File.Exists(bak)) throw new FileNotFoundException("Lossless.dll.bak yedeği yok; geri yüklenecek bir şey bulunmuyor.");
        if (File.Exists(dll) && PeResources.CodeSectionHash(bak) != PeResources.CodeSectionHash(dll))
            throw new InvalidOperationException("Yedek, kurulu Lossless.dll ile farklı bir sürüme ait (Steam güncellemesi). Geri yükleme sürümü düşüreceği için yapılmadı. Gerekirse Steam > Özellikler > Yüklü Dosyalar > 'Oyun dosyalarının bütünlüğünü doğrula' kullanın.");
        EnsureLosslessScalingClosed();
        File.Copy(bak, dll, true);

        string ultra = Path.Combine(dir, UltraName);
        if (File.Exists(ultra) && PeResources.CodeSectionHash(ultra) == PeResources.CodeSectionHash(bak))
            File.Delete(ultra);
        return "Orijinal Lossless.dll geri yüklendi.";
    }

    public static (InstallState state, string text) GetStatus(string dir)
    {
        try
        {
            string dll = Path.Combine(dir ?? "", DllName), bak = Path.Combine(dir ?? "", BakName);
            if (string.IsNullOrWhiteSpace(dir) || !File.Exists(dll)) return (InstallState.NotFound, "Lossless.dll bulunamadı");
            int pending = FindCandidates(dll, out _).Count;
            bool bakSameBuild = File.Exists(bak) && PeResources.CodeSectionHash(bak) == PeResources.CodeSectionHash(dll);
            if (pending > 0)
                return (InstallState.Original, $"Orijinal — {pending} shader optimize edilebilir");
            if (bakSameBuild)
                return (InstallState.Patched, "Lossless Plus yaması uygulanmış ✔");
            if (File.Exists(bak))
                return (InstallState.BackupFromOtherBuild, "Optimize edilecek shader yok; yedek farklı sürüme ait");
            return (InstallState.NoCandidates, "Optimize edilecek shader bulunamadı");
        }
        catch (Exception ex)
        {
            return (InstallState.NotFound, "Durum okunamadı: " + ex.Message);
        }
    }
}
