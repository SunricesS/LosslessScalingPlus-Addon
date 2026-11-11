#!/usr/bin/env python3
"""
Lossless Scaling Plus — Local LSFG 3.1 Compute Shader Optimizer & Patcher
=========================================================================
100% Clean-Room BYOB (Bring Your Own Binary) Local Optimizer.
Contains ZERO proprietary shader code or weights.

Reads the user's own legitimate Steam installation of `Lossless.dll`,
extracts RT_RCDATA compute shaders (IDs 257..302) in memory, folds unfolded
BatchNorm affine layers into single hardware `mad` instructions, strips
`skipOptimization`, recompiles locally via Windows `D3DCompiler_47.dll` with
`D3DCOMPILE_OPTIMIZATION_LEVEL3` (/O3), and updates `Lossless.dll` (preserving
`Lossless.dll.bak`).
"""

import os
import re
import sys
import shutil
import ctypes
import argparse
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)
d3dcompiler = ctypes.WinDLL("D3DCompiler_47.dll")
try:
    imagehlp = ctypes.WinDLL("imagehlp.dll")
except OSError:
    imagehlp = None

LOAD_LIBRARY_AS_DATAFILE = 0x00000002
RT_RCDATA = 10
LANG_EN_US = 1033  # MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US)

LoadLibraryExW = kernel32.LoadLibraryExW
LoadLibraryExW.argtypes = [wintypes.LPCWSTR, wintypes.HANDLE, wintypes.DWORD]
LoadLibraryExW.restype = wintypes.HMODULE

FreeLibrary = kernel32.FreeLibrary
FreeLibrary.argtypes = [wintypes.HMODULE]
FreeLibrary.restype = wintypes.BOOL

FindResourceW = kernel32.FindResourceW
FindResourceW.argtypes = [wintypes.HMODULE, wintypes.LPCWSTR, wintypes.LPCWSTR]
FindResourceW.restype = wintypes.HRSRC

LoadResource = kernel32.LoadResource
LoadResource.argtypes = [wintypes.HMODULE, wintypes.HRSRC]
LoadResource.restype = wintypes.HGLOBAL

LockResource = kernel32.LockResource
LockResource.argtypes = [wintypes.HGLOBAL]
LockResource.restype = wintypes.LPVOID

SizeofResource = kernel32.SizeofResource
SizeofResource.argtypes = [wintypes.HMODULE, wintypes.HRSRC]
SizeofResource.restype = wintypes.DWORD

BeginUpdateResourceW = kernel32.BeginUpdateResourceW
BeginUpdateResourceW.argtypes = [wintypes.LPCWSTR, wintypes.BOOL]
BeginUpdateResourceW.restype = wintypes.HANDLE

UpdateResourceW = kernel32.UpdateResourceW
UpdateResourceW.argtypes = [
    wintypes.HANDLE, wintypes.LPCWSTR, wintypes.LPCWSTR,
    wintypes.WORD, wintypes.LPVOID, wintypes.DWORD
]
UpdateResourceW.restype = wintypes.BOOL

EndUpdateResourceW = kernel32.EndUpdateResourceW
EndUpdateResourceW.argtypes = [wintypes.HANDLE, wintypes.BOOL]
EndUpdateResourceW.restype = wintypes.BOOL

D3DCompile = d3dcompiler.D3DCompile
D3DCompile.argtypes = [
    ctypes.c_void_p, ctypes.c_size_t, ctypes.c_char_p,
    ctypes.c_void_p, ctypes.c_void_p, ctypes.c_char_p,
    ctypes.c_char_p, ctypes.c_uint, ctypes.c_uint,
    ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p)
]
D3DCompile.restype = ctypes.c_long

D3DDisassemble = d3dcompiler.D3DDisassemble
D3DDisassemble.argtypes = [
    ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint, ctypes.c_char_p,
    ctypes.POINTER(ctypes.c_void_p)
]
D3DDisassemble.restype = ctypes.c_long

CH_MAP = {'x': 0, 'y': 1, 'z': 2, 'w': 3}


def make_int_resource(val):
    return ctypes.cast(ctypes.c_void_p(val & 0xFFFF), wintypes.LPCWSTR)


def get_blob_bytes(blob_ptr):
    if not blob_ptr:
        return b""
    vtbl = ctypes.cast(blob_ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    get_ptr = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vtbl[3])
    get_size = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vtbl[4])
    release = ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtbl[2])
    ptr = get_ptr(blob_ptr)
    size = get_size(blob_ptr)
    data = ctypes.string_at(ptr, size)
    release(blob_ptr)
    return data


def read_rcdata_resources(dll_path, resource_ids):
    h_mod = LoadLibraryExW(dll_path, None, LOAD_LIBRARY_AS_DATAFILE)
    if not h_mod:
        raise RuntimeError(f"Failed to open {dll_path} (error {ctypes.get_last_error()})")
    resources = {}
    try:
        for rid in resource_ids:
            h_res = FindResourceW(h_mod, make_int_resource(rid), make_int_resource(RT_RCDATA))
            if not h_res:
                raise RuntimeError(f"Resource ID {rid} not found in {dll_path}")
            size = SizeofResource(h_mod, h_res)
            h_glob = LoadResource(h_mod, h_res)
            ptr = LockResource(h_glob)
            resources[rid] = ctypes.string_at(ptr, size)
    finally:
        FreeLibrary(h_mod)
    return resources


def disassemble_dxbc(dxbc_bytes):
    dis_blob = ctypes.c_void_p()
    hr = D3DDisassemble(dxbc_bytes, len(dxbc_bytes), 0, None, ctypes.byref(dis_blob))
    if hr != 0:
        raise RuntimeError(f"D3DDisassemble failed (hr={hr:#x})")
    return get_blob_bytes(dis_blob).rstrip(b'\x00').decode('utf-8', errors='replace')


def split_operands(s):
    res, depth, cur = [], 0, []
    for ch in s:
        if ch in '({[':
            depth += 1
            cur.append(ch)
        elif ch in ')}]':
            depth -= 1
            cur.append(ch)
        elif ch == ',' and depth == 0:
            res.append(''.join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    if cur:
        res.append(''.join(cur).strip())
    return res


def is_int_literal(op_str):
    op_str = re.sub(r'\s*\{[^}]+\}', '', op_str).strip()
    if op_str.startswith('-'):
        op_str = op_str[1:].strip()
    if op_str.startswith('l(') and op_str.endswith(')'):
        inner = op_str[2:-1]
        return '.' not in inner and 'e' not in inner.lower()
    return False


def select_swizzle(swiz, mask_indices):
    if len(swiz) == 1:
        return swiz
    return ''.join(swiz[i] for i in mask_indices)


def fold_batchnorm_instructions(body_lines):
    new_lines = []
    i = 0
    n = len(body_lines)
    while i < n:
        k = 0
        triplets = []
        while i + k * 3 + 2 < n:
            l0 = body_lines[i + k * 3]
            l1 = body_lines[i + k * 3 + 1]
            l2 = body_lines[i + k * 3 + 2]
            m0 = re.match(r'^mov (r\d+\.xyzw) \{min16f\}, l\(([^)]+)\) \{def32 as min16f\}$', l0)
            if not m0:
                break
            rt, c1_str = m0.group(1), m0.group(2)
            if l1 != f"mov {rt} {{min16f}}, -{rt} {{min16f}}":
                break
            m2 = re.match(r'^add (r\d+\.xyzw) \{min16f\}, (r\d+\.xyzw) \{min16f\}, (r\d+\.xyzw) \{min16f\}$', l2)
            if not m2:
                break
            rout, ra, rb = m2.group(1), m2.group(2), m2.group(3)
            racc = rb if ra == rt else (ra if rb == rt else None)
            if not racc:
                break
            c1 = [float(x.strip()) for x in c1_str.split(',')]
            triplets.append((rout, racc, c1))
            k += 1

        if k > 0 and i + k * 3 + k * 4 <= n:
            scales, biases = [], []
            base_s = i + k * 3
            base_b = base_s + k * 2
            ok = True
            for j in range(k):
                rout = triplets[j][0]
                ls0 = body_lines[base_s + j * 2]
                ls1 = body_lines[base_s + j * 2 + 1]
                lb0 = body_lines[base_b + j * 2]
                lb1 = body_lines[base_b + j * 2 + 1]
                ms0 = re.match(r'^mov (r\d+\.xyzw) \{min16f\}, l\(([^)]+)\) \{def32 as min16f\}$', ls0)
                mb0 = re.match(r'^mov (r\d+\.xyzw) \{min16f\}, l\(([^)]+)\) \{def32 as min16f\}$', lb0)
                if not ms0 or not mb0:
                    ok = False
                    break
                rs, c2_str = ms0.group(1), ms0.group(2)
                rb_reg, c3_str = mb0.group(1), mb0.group(2)
                if ls1 != f"mul {rout} {{min16f}}, {rout} {{min16f}}, {rs} {{min16f}}":
                    ok = False
                    break
                if lb1 != f"add {rout} {{min16f}}, {rout} {{min16f}}, {rb_reg} {{min16f}}":
                    ok = False
                    break
                c2 = [float(x.strip()) for x in c2_str.split(',')]
                c3 = [float(x.strip()) for x in c3_str.split(',')]
                scales.append((rs, c2))
                biases.append((rb_reg, c3))
            if ok:
                for j in range(k):
                    rout, racc, c1 = triplets[j]
                    rs, c2 = scales[j]
                    c2_fmt = ", ".join(f"{v:.6f}" for v in c2)
                    new_lines.append(f"mul {rout} {{min16f}}, {racc} {{min16f}}, l({c2_fmt}) {{def32 as min16f}}")
                for j in range(k):
                    rout, racc, c1 = triplets[j]
                    rs, c2 = scales[j]
                    rb_reg, c3 = biases[j]
                    folded_bias = [c3[m] - c1[m] * c2[m] for m in range(4)]
                    b_fmt = ", ".join(f"{v:.6f}" for v in folded_bias)
                    new_lines.append(f"add {rout} {{min16f}}, {rout} {{min16f}}, l({b_fmt}) {{def32 as min16f}}")
                i += k * 7
                continue

        new_lines.append(body_lines[i])
        i += 1
    return new_lines


def transpile_asm_to_hlsl(asm_text, ultra_perf=False, shader_id=0):
    lines = [l.strip() for l in asm_text.splitlines() if l.strip() and not l.strip().startswith('//')]
    cb_size = 2
    samplers, textures, uavs = [], [], []
    num_temps = 16
    thread_group = (8, 8, 1)
    has_tgsm = False
    tgsm_stride, tgsm_count = 128, 32
    body_lines = []

    for l in lines:
        if l.startswith('cs_'):
            continue
        if l.startswith('dcl_'):
            if l.startswith('dcl_constantbuffer'):
                m = re.search(r'CB0\[(\d+)\]', l, re.I)
                if m:
                    cb_size = int(m.group(1))
            elif l.startswith('dcl_sampler'):
                m = re.search(r'\b(s\d+)\b', l)
                if m:
                    samplers.append(m.group(1))
            elif l.startswith('dcl_resource_texture2d'):
                m = re.search(r'\b(t\d+)\b', l)
                if m:
                    textures.append(m.group(1))
            elif l.startswith('dcl_uav_typed_texture2d'):
                m = re.search(r'\b(u\d+)\b', l)
                if m:
                    uavs.append(m.group(1))
            elif l.startswith('dcl_temps'):
                num_temps = int(l.split()[1])
            elif l.startswith('dcl_thread_group'):
                thread_group = tuple(int(x.strip(',')) for x in l.split()[1:])
            elif l.startswith('dcl_tgsm_structured'):
                has_tgsm = True
                parts = [x.strip(',') for x in l.split()[1:]]
                tgsm_stride = int(parts[1])
                tgsm_count = int(parts[2])
            continue
        body_lines.append(l)

    body_lines = fold_batchnorm_instructions(body_lines)

    reg_type = {(str(i), c): 'float' for i in range(num_temps) for c in 'xyzw'}

    def set_reg_type(rnum, mask, t):
        for c in mask:
            reg_type[(str(rnum), c)] = t

    def get_reg_type(rnum, swiz):
        return reg_type.get((str(rnum), swiz[0]), 'float')

    def get_dst_info(dst_str):
        dst_str = dst_str.strip()
        if dst_str == 'null':
            return None, [], False, None, None
        is_min16 = '{min16f}' in dst_str
        dst_clean = re.sub(r'\s*\{[^}]+\}', '', dst_str).strip()
        m = re.match(r'^r(\d+)\.([xyzw]+)$', dst_clean)
        rnum, mask = m.group(1), m.group(2)
        mask_indices = [CH_MAP[c] for c in mask]
        return dst_clean, mask_indices, is_min16, rnum, mask

    def format_op(op_str, mask_indices, want_type='float'):
        op_str = op_str.strip()
        mask_len = len(mask_indices)
        mod = None
        m_mod = re.search(r'\{([^}]+)\}', op_str)
        if m_mod:
            mod = m_mod.group(1).strip()
            op_str = re.sub(r'\s*\{[^}]+\}', '', op_str).strip()

        neg = False
        if op_str.startswith('-'):
            neg = True
            op_str = op_str[1:].strip()

        if op_str.startswith('l(') and op_str.endswith(')'):
            vals = [v.strip() for v in op_str[2:-1].split(',')]
            if len(vals) == 1:
                v = vals[0]
                if want_type == 'uint':
                    expr = f"uint({v})"
                elif want_type == 'int':
                    expr = f"int({v})"
                else:
                    if '.' not in v and 'e' not in v.lower():
                        v = v + ".0"
                    expr = f"float({v})"
            else:
                sel_vals = [vals[i] for i in mask_indices]
                if want_type == 'uint':
                    t = f"uint{len(sel_vals)}" if len(sel_vals) > 1 else "uint"
                    expr = f"{t}({', '.join(sel_vals)})"
                elif want_type == 'int':
                    t = f"int{len(sel_vals)}" if len(sel_vals) > 1 else "int"
                    expr = f"{t}({', '.join(sel_vals)})"
                else:
                    fvals = [v + ".0" if ('.' not in v and 'e' not in v.lower()) else v for v in sel_vals]
                    t = f"float{len(fvals)}" if len(fvals) > 1 else "float"
                    expr = f"{t}({', '.join(fvals)})"
            if mod in ('min16f', 'def32 as min16f') or want_type == 'min16f':
                t = f"min16float{mask_len}" if mask_len > 1 else "min16float"
                expr = f"{t}({expr})"
            return f"(-{expr})" if neg else expr

        if op_str.startswith('vThreadIDInGroup') or op_str.startswith('vThreadID'):
            parts = op_str.split('.')
            name = parts[0]
            raw_swiz = parts[1] if len(parts) > 1 else "xyzw"
            swiz = select_swizzle(raw_swiz, mask_indices)
            expr = f"{name}.{swiz}"
            if want_type == 'int':
                t = f"int{mask_len}" if mask_len > 1 else "int"
                expr = f"{t}({expr})"
            elif want_type == 'float':
                expr = f"asfloat({expr})"
            return f"(-{expr})" if neg else expr

        m_cb = re.match(r'^cb0\[(\d+)\]\.([xyzw]+)$', op_str, re.I)
        if m_cb:
            idx = m_cb.group(1)
            swiz = select_swizzle(m_cb.group(2), mask_indices)
            expr = f"cb0[{idx}].{swiz}"
            if want_type == 'uint':
                expr = f"asuint({expr})"
            elif want_type == 'int':
                expr = f"asint({expr})"
            elif mod in ('min16f', 'def32 as min16f') or want_type == 'min16f':
                t = f"min16float{mask_len}" if mask_len > 1 else "min16float"
                expr = f"{t}({expr})"
            return f"(-{expr})" if neg else expr

        m_reg = re.match(r'^r(\d+)\.([xyzw]+)$', op_str, re.I)
        if m_reg:
            rnum = m_reg.group(1)
            swiz = select_swizzle(m_reg.group(2), mask_indices)
            if mod == 'min16f':
                expr = f"h{rnum}.{swiz}"
                if want_type == 'float':
                    t = f"float{mask_len}" if mask_len > 1 else "float"
                    expr = f"{t}({expr})"
            elif mod == 'min16f as def32':
                t = f"float{mask_len}" if mask_len > 1 else "float"
                expr = f"{t}(h{rnum}.{swiz})"
            elif mod == 'def32 as min16f':
                t = f"min16float{mask_len}" if mask_len > 1 else "min16float"
                cur_t = get_reg_type(rnum, swiz)
                base = f"r{rnum}.{swiz}" if cur_t == 'float' else f"asfloat(ur{rnum}.{swiz})"
                expr = f"{t}({base})"
            else:
                cur_t = get_reg_type(rnum, swiz)
                if want_type in ('uint', 'int'):
                    expr = f"ur{rnum}.{swiz}" if cur_t == 'uint' else f"asuint(r{rnum}.{swiz})"
                    if want_type == 'int':
                        t = f"int{mask_len}" if mask_len > 1 else "int"
                        expr = f"{t}({expr})"
                else:
                    expr = f"r{rnum}.{swiz}" if cur_t == 'float' else f"asfloat(ur{rnum}.{swiz})"
                    if want_type == 'min16f':
                        t = f"min16float{mask_len}" if mask_len > 1 else "min16float"
                        expr = f"{t}({expr})"
            return f"(-{expr})" if neg else expr

        raise ValueError(f"Unknown operand: {op_str}")

    def commit_dst(dst_str, set_t='float'):
        _, mask_indices, is_min16, rnum, mask = get_dst_info(dst_str)
        if rnum is None:
            return None
        if is_min16:
            return f"h{rnum}.{mask}"
        set_reg_type(rnum, mask, set_t)
        return f"ur{rnum}.{mask}" if set_t == 'uint' else f"r{rnum}.{mask}"

    hlsl = [f"cbuffer cb : register(b0) {{ float4 cb0[{cb_size}]; }};"]
    for s in samplers:
        hlsl.append(f"SamplerState {s} : register({s});")
    for t in textures:
        hlsl.append(f"Texture2D<float4> {t} : register({t});")
    for u in uavs:
        hlsl.append(f"RWTexture2D<float4> {u} : register({u});")
    if has_tgsm:
        hlsl.append(f"groupshared float g0[{tgsm_count}][{tgsm_stride // 4}];")

    hlsl.append(f"[numthreads({thread_group[0]}, {thread_group[1]}, {thread_group[2]})]")
    hlsl.append("void main(uint3 vThreadID : SV_DispatchThreadID, uint3 vThreadIDInGroup : SV_GroupThreadID) {")
    for i in range(num_temps):
        hlsl.append(f"    float4 r{i} = 0; uint4 ur{i} = 0; min16float4 h{i} = 0;")

    indent = "    "
    for l in body_lines:
        if l == 'ret':
            continue
        if l == 'else':
            indent = indent[:-4]
            hlsl.append(f"{indent}}} else {{")
            indent += "    "
            continue
        if l == 'endif':
            indent = indent[:-4]
            hlsl.append(f"{indent}}}")
            continue
        if l == 'sync_g_t':
            hlsl.append(f"{indent}GroupMemoryBarrierWithGroupSync();")
            continue

        parts = l.split(None, 1)
        op = parts[0]
        ops = split_operands(parts[1] if len(parts) > 1 else "")

        if op == 'if_z':
            hlsl.append(f"{indent}if ({format_op(ops[0], [0], 'uint')} == 0u) {{")
            indent += "    "
        elif op == 'if_nz':
            hlsl.append(f"{indent}if ({format_op(ops[0], [0], 'uint')} != 0u) {{")
            indent += "    "
        elif op == 'mov':
            dst_str, src_str = ops[0], ops[1]
            _, mask_indices, is_min16_dst, _, _ = get_dst_info(dst_str)
            if is_min16_dst:
                src = format_op(src_str, mask_indices, 'min16f')
                target = commit_dst(dst_str, 'min16f')
                hlsl.append(f"{indent}{target} = {src};")
            else:
                src_clean = re.sub(r'\s*\{[^}]+\}', '', src_str).strip()
                src_is_uint = False
                if src_clean.startswith('vThreadID') or is_int_literal(src_str):
                    if '{' not in src_str:
                        src_is_uint = True
                else:
                    m_r = re.match(r'^r(\d+)\.([xyzw]+)$', src_clean)
                    if m_r and '{' not in src_str:
                        sw = select_swizzle(m_r.group(2), mask_indices)
                        src_is_uint = (get_reg_type(m_r.group(1), sw) == 'uint')
                t_kind = 'uint' if src_is_uint else 'float'
                src = format_op(src_str, mask_indices, t_kind)
                target = commit_dst(dst_str, t_kind)
                hlsl.append(f"{indent}{target} = {src};")
        elif op in ('add', 'mul', 'div'):
            _, mask_indices, is_min16, _, _ = get_dst_info(ops[0])
            want = 'min16f' if is_min16 else 'float'
            a = format_op(ops[1], mask_indices, want)
            b = format_op(ops[2], mask_indices, want)
            target = commit_dst(ops[0], want)
            sym = {'add': '+', 'mul': '*', 'div': '/'}[op]
            hlsl.append(f"{indent}{target} = {a} {sym} {b};")
        elif op in ('max', 'min', 'dp4'):
            _, mask_indices, is_min16, _, _ = get_dst_info(ops[0])
            want = 'min16f' if is_min16 else 'float'
            src_indices = [0, 1, 2, 3] if op == 'dp4' else mask_indices
            a = format_op(ops[1], src_indices, want)
            b = format_op(ops[2], src_indices, want)
            target = commit_dst(ops[0], want)
            fn = 'dot' if op == 'dp4' else op
            hlsl.append(f"{indent}{target} = {fn}({a}, {b});")
        elif op == 'exp':
            _, mask_indices, is_min16, _, _ = get_dst_info(ops[0])
            want = 'min16f' if is_min16 else 'float'
            a = format_op(ops[1], mask_indices, want)
            target = commit_dst(ops[0], want)
            hlsl.append(f"{indent}{target} = exp2({a});")
        elif op == 'utof':
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            a = format_op(ops[1], mask_indices, 'uint')
            target = commit_dst(ops[0], 'float')
            t = f"float{len(mask_indices)}" if len(mask_indices) > 1 else "float"
            hlsl.append(f"{indent}{target} = {t}({a});")
        elif op == 'itof':
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            a = format_op(ops[1], mask_indices, 'int')
            target = commit_dst(ops[0], 'float')
            t = f"float{len(mask_indices)}" if len(mask_indices) > 1 else "float"
            hlsl.append(f"{indent}{target} = {t}({a});")
        elif op == 'iadd':
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            a = format_op(ops[1], mask_indices, 'uint')
            b = format_op(ops[2], mask_indices, 'uint')
            target = commit_dst(ops[0], 'uint')
            hlsl.append(f"{indent}{target} = {a} + {b};")
        elif op == 'and':
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            a = format_op(ops[1], mask_indices, 'uint')
            b = format_op(ops[2], mask_indices, 'uint')
            target = commit_dst(ops[0], 'uint')
            hlsl.append(f"{indent}{target} = {a} & {b};")
        elif op in ('ine', 'ieq'):
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            a = format_op(ops[1], mask_indices, 'uint')
            b = format_op(ops[2], mask_indices, 'uint')
            target = commit_dst(ops[0], 'uint')
            sym = '!=' if op == 'ine' else '=='
            hlsl.append(f"{indent}{target} = ({a} {sym} {b}) ? 0xFFFFFFFFu : 0u;")
        elif op == 'ge':
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            want_src = 'min16f' if ('{min16f}' in ops[1] or '{min16f}' in ops[2]) else 'float'
            a = format_op(ops[1], mask_indices, want_src)
            b = format_op(ops[2], mask_indices, want_src)
            target = commit_dst(ops[0], 'uint')
            hlsl.append(f"{indent}{target} = ({a} >= {b}) ? 0xFFFFFFFFu : 0u;")
        elif op == 'movc':
            _, mask_indices, is_min16, _, _ = get_dst_info(ops[0])
            want = 'min16f' if is_min16 else 'float'
            cond = format_op(ops[1], mask_indices, 'uint')
            tval = format_op(ops[2], mask_indices, want)
            fval = format_op(ops[3], mask_indices, want)
            target = commit_dst(ops[0], want)
            hlsl.append(f"{indent}{target} = ({cond} != 0u) ? {tval} : {fval};")
        elif op == 'imul':
            _, mask_indices, _, _, _ = get_dst_info(ops[1])
            a = format_op(ops[2], mask_indices, 'uint')
            b = format_op(ops[3], mask_indices, 'uint')
            target = commit_dst(ops[1], 'uint')
            hlsl.append(f"{indent}{target} = {a} * {b};")
        elif op == 'udiv':
            dst_q, dst_r, src0, src1 = ops[0], ops[1], ops[2], ops[3]
            if dst_q != 'null':
                _, mask_indices, _, _, _ = get_dst_info(dst_q)
                hlsl.append(f"{indent}{commit_dst(dst_q, 'uint')} = {format_op(src0, mask_indices, 'uint')} / {format_op(src1, mask_indices, 'uint')};")
            if dst_r != 'null':
                _, mask_indices, _, _, _ = get_dst_info(dst_r)
                hlsl.append(f"{indent}{commit_dst(dst_r, 'uint')} = {format_op(src0, mask_indices, 'uint')} % {format_op(src1, mask_indices, 'uint')};")
        elif op.startswith('resinfo_indexable'):
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            tex, raw_swiz = ops[2].split('.')
            tex_swiz = select_swizzle(raw_swiz, mask_indices)
            target = commit_dst(ops[0], 'float')
            hlsl.append(f"{indent}{{ uint _w, _h; {tex}.GetDimensions(_w, _h); {target} = float4(_w, _h, 0, 0).{tex_swiz}; }}")
        elif op.startswith('sample_l_indexable'):
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            uv = format_op(ops[1], [0, 1], 'float')
            tex, raw_swiz = ops[2].split('.')
            tex_swiz = select_swizzle(raw_swiz, mask_indices)
            samp, lod = ops[3], format_op(ops[4], [0], 'float')
            target = commit_dst(ops[0], 'float')
            hlsl.append(f"{indent}{target} = {tex}.SampleLevel({samp}, {uv}, {lod}).{tex_swiz};")
        elif op.startswith('sample_l_aoffimmi_indexable'):
            m_off = re.search(r'\((-?\d+),(-?\d+),(-?\d+)\)', op)
            ox, oy = int(m_off.group(1)), int(m_off.group(2))
            _, mask_indices, _, _, _ = get_dst_info(ops[0])
            uv = format_op(ops[1], [0, 1], 'float')
            tex, raw_swiz = ops[2].split('.')
            tex_swiz = select_swizzle(raw_swiz, mask_indices)
            samp, lod = ops[3], format_op(ops[4], [0], 'float')
            target = commit_dst(ops[0], 'float')
            if ultra_perf and shader_id in (293, 299, 300, 301) and abs(ox) == 1 and abs(oy) == 1:
                hlsl.append(f"{indent}{target} = ({tex}.SampleLevel({samp}, {uv}, {lod}, int2({ox}, {oy})).{tex_swiz}) * 0.0;")
            else:
                hlsl.append(f"{indent}{target} = {tex}.SampleLevel({samp}, {uv}, {lod}, int2({ox}, {oy})).{tex_swiz};")
        elif op == 'store_uav_typed':
            uav = ops[0].split('.')[0]
            hlsl.append(f"{indent}{uav}[{format_op(ops[1], [0, 1], 'uint')}] = {format_op(ops[2], [0, 1, 2, 3], 'float')};")
        elif op == 'store_structured':
            idx = format_op(ops[1], [0], 'uint')
            boff = format_op(ops[2], [0], 'uint')
            val = format_op(ops[3], [0], 'float')
            hlsl.append(f"{indent}g0[({idx}) & 31u][(({boff}) >> 2u) & 31u] = {val};")
        elif op == 'ld_structured':
            idx = format_op(ops[1], [0], 'uint')
            boff = format_op(ops[2], [0], 'uint')
            _, mask_indices, is_min16, _, _ = get_dst_info(ops[0])
            target = commit_dst(ops[0], 'min16f' if is_min16 else 'float')
            val_expr = f"g0[({idx}) & 31u][(({boff}) >> 2u) & 31u]"
            if is_min16:
                val_expr = f"min16float({val_expr})"
            hlsl.append(f"{indent}{target} = {val_expr};")
        else:
            raise ValueError(f"Unhandled opcode: {op} in line: {l}")

    hlsl.append("}")
    return "\n".join(hlsl)


def compile_hlsl(hlsl_code):
    src_bytes = hlsl_code.encode('utf-8')
    code_blob = ctypes.c_void_p()
    err_blob = ctypes.c_void_p()
    flags = 0x8000  # D3DCOMPILE_OPTIMIZATION_LEVEL3
    hr = D3DCompile(
        src_bytes, len(src_bytes), b"lsfg3_opt.hlsl",
        None, None, b"main", b"cs_5_0", flags, 0,
        ctypes.byref(code_blob), ctypes.byref(err_blob)
    )
    err_msg = get_blob_bytes(err_blob).decode('utf-8', errors='replace')
    if hr != 0:
        raise RuntimeError(f"D3DCompile failed (hr={hr:#x}):\n{err_msg}")
    dxbc = get_blob_bytes(code_blob)
    dis_text = disassemble_dxbc(dxbc)
    return dxbc, dis_text


def update_pe_checksum(dll_path):
    if not imagehlp:
        return
    try:
        header_sum = wintypes.DWORD()
        check_sum = wintypes.DWORD()
        if imagehlp.MapFileAndCheckSumW(wintypes.LPCWSTR(dll_path), ctypes.byref(header_sum), ctypes.byref(check_sum)) == 0:
            with open(dll_path, "r+b") as f:
                data = f.read(4096)
                pe_off = int.from_bytes(data[0x3C:0x40], "little")
                f.seek(pe_off + 24 + 64)
                f.write(int(check_sum.value).to_bytes(4, "little"))
    except Exception:
        pass


def patch_lossless_installation(install_dir, build_ultra_perf=True):
    dll_path = os.path.join(install_dir, "Lossless.dll")
    bak_path = os.path.join(install_dir, "Lossless.dll.bak")
    ultra_path = os.path.join(install_dir, "Lossless_UltraPerf.dll")

    if not os.path.isfile(dll_path):
        raise FileNotFoundError(f"Lossless.dll not found in: {install_dir}")

    if not os.path.isfile(bak_path):
        print(f"[*] Creating clean backup: {bak_path}")
        shutil.copy2(dll_path, bak_path)
    else:
        print(f"[*] Reading original shaders from backup: {bak_path}")

    shader_ids = list(range(257, 303))
    raw_resources = read_rcdata_resources(bak_path, shader_ids)

    def build_variant(dst_file, ultra_perf=False):
        shutil.copy2(bak_path, dst_file)
        h_upd = BeginUpdateResourceW(dst_file, False)
        if not h_upd:
            raise RuntimeError(f"BeginUpdateResourceW failed on {dst_file}: {ctypes.get_last_error()}")

        orig_slots_total = 0
        opt_slots_total = 0
        bytes_after = 0

        for sid in shader_ids:
            orig_asm = disassemble_dxbc(raw_resources[sid])
            m_o = re.search(r'Approximately (\d+) instruction slots used', orig_asm)
            orig_slots_total += int(m_o.group(1)) if m_o else 0

            hlsl = transpile_asm_to_hlsl(orig_asm, ultra_perf=ultra_perf, shader_id=sid)
            dxbc, opt_asm = compile_hlsl(hlsl)
            m_n = re.search(r'Approximately (\d+) instruction slots used', opt_asm)
            opt_slots_total += int(m_n.group(1)) if m_n else 0

            buf = ctypes.create_string_buffer(dxbc, len(dxbc))
            ok = UpdateResourceW(
                h_upd,
                make_int_resource(RT_RCDATA),
                make_int_resource(sid),
                LANG_EN_US,
                ctypes.cast(buf, wintypes.LPVOID),
                len(dxbc)
            )
            if not ok:
                raise RuntimeError(f"UpdateResourceW failed on shader {sid}: {ctypes.get_last_error()}")
            bytes_after += len(dxbc)

        if not EndUpdateResourceW(h_upd, False):
            raise RuntimeError(f"EndUpdateResourceW failed: {ctypes.get_last_error()}")

        update_pe_checksum(dst_file)
        red = (1.0 - (opt_slots_total / max(orig_slots_total, 1))) * 100.0
        mode_name = "UltraPerf (/O3 + BN + 5-Tap)" if ultra_perf else "Lossless (/O3 + BatchNorm Folded)"
        print(f"[+] {os.path.basename(dst_file)} [{mode_name}]: {orig_slots_total:,} -> {opt_slots_total:,} instruction slots (-{red:.2f}%, {bytes_after:,} bytes)")

    build_variant(dll_path, ultra_perf=False)
    if build_ultra_perf:
        build_variant(ultra_path, ultra_perf=True)


def find_default_steam_dir():
    candidates = [
        r"C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling",
        r"C:\Program Files\Steam\steamapps\common\Lossless Scaling",
        r"D:\SteamLibrary\steamapps\common\Lossless Scaling",
        r"E:\SteamLibrary\steamapps\common\Lossless Scaling",
        os.getcwd(),
    ]
    for c in candidates:
        if os.path.isfile(os.path.join(c, "Lossless.dll")):
            return c
    return None


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Lossless Scaling Plus — Local LSFG 3.1 Compute Shader Optimizer")
    parser.add_argument("--dir", help="Path to Lossless Scaling installation directory containing Lossless.dll")
    parser.add_argument("--restore", action="store_true", help="Restore Lossless.dll from Lossless.dll.bak")
    args = parser.parse_args()

    target_dir = args.dir or find_default_steam_dir()
    if not target_dir or not os.path.isfile(os.path.join(target_dir, "Lossless.dll")):
        print("[!] Lossless.dll bulunamadi. Lutfen --dir \"C:\\...\\Lossless Scaling\" parametresi ile klasoru belirtin.")
        sys.exit(1)

    if args.restore:
        bak = os.path.join(target_dir, "Lossless.dll.bak")
        if os.path.isfile(bak):
            shutil.copy2(bak, os.path.join(target_dir, "Lossless.dll"))
            print(f"[+] Orijinal Lossless.dll yedekten geri yuklendi: {target_dir}")
        else:
            print("[!] Lossless.dll.bak bulunamadi.")
        sys.exit(0)

    print(f"[*] Hedef Klasor: {target_dir}")
    patch_lossless_installation(target_dir, build_ultra_perf=True)
    print("[+] Tamamlandi! Lossless Scaling Plus shader optimizasyonu basariyla uygulandi.")
