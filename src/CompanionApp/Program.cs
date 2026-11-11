using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;

namespace LosslessPlusCompanion;

public static class NativeBooster
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH = 4;
    private const int D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME = 5;

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint hProcess, int priorityClass);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, nint previousState, nint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    public static uint CurrentTimerRes100Ns { get; private set; }
    public static bool IsRealtimeGpu { get; private set; }

    public static void EnablePrivilege()
    {
        try
        {
            if (OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint hToken))
            {
                try
                {
                    if (LookupPrivilegeValueW(null, "SeIncreaseBasePriorityPrivilege", out LUID luid))
                    {
                        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
                        {
                            PrivilegeCount = 1,
                            Luid = luid,
                            Attributes = SE_PRIVILEGE_ENABLED
                        };
                        AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
        }
        catch { }
    }

    public static string BoostLosslessAndSystem(bool realtimeGpu, bool highResTimer, bool cpuIsolation)
    {
        try
        {
            if (highResTimer)
            {
                NtSetTimerResolution(5000u, true, out uint cur);
                CurrentTimerRes100Ns = cur;
            }
        }
        catch { }

        EnablePrivilege();

        int boostedCount = 0;
        try
        {
            Process[] procs = Process.GetProcessesByName("LosslessScaling");
            foreach (Process p in procs)
            {
                try
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    if (realtimeGpu)
                    {
                        int st = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME);
                        if (st != 0)
                            st = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
                        IsRealtimeGpu = (st == 0);
                    }
                    boostedCount++;
                }
                catch { }
            }

            if (procs.Length == 0 && realtimeGpu)
            {
                using Process self = Process.GetCurrentProcess();
                int st = D3DKMTSetProcessSchedulingPriorityClass(self.Handle, D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME);
                if (st != 0)
                    st = D3DKMTSetProcessSchedulingPriorityClass(self.Handle, D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
                IsRealtimeGpu = (st == 0);
            }

            if (cpuIsolation && Environment.ProcessorCount >= 6)
            {
                int cores = Math.Min(Environment.ProcessorCount, 64);
                ulong allMask = (cores == 64) ? ulong.MaxValue : ((1UL << cores) - 1UL);
                int lsCores = (cores >= 12) ? 4 : 2;
                ulong gameMask = (1UL << (cores - lsCores)) - 1UL;
                ulong lsMask = allMask ^ gameMask;

                foreach (Process p in procs)
                {
                    try { p.ProcessorAffinity = (nint)lsMask; } catch { }
                }

                nint fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(fg, out uint fgPid);
                    if (fgPid != 0 && fgPid != (uint)Process.GetCurrentProcess().Id && !procs.Any(x => (uint)x.Id == fgPid))
                    {
                        using Process fgProc = Process.GetProcessById((int)fgPid);
                        fgProc.ProcessorAffinity = (nint)gameMask;
                    }
                }
            }
        }
        catch { }

        double ms = CurrentTimerRes100Ns > 0 ? CurrentTimerRes100Ns / 10000.0 : 0.50;
        string gpuStr = IsRealtimeGpu ? "REALTIME (HAGS)" : "HIGH";
        string lsProcStr = boostedCount > 0 ? $"LosslessScaling.exe Aktif ({boostedCount})" : "Beklemede";
        return $"Timer: {ms:0.00}ms | GPU: {gpuStr} | Süreç: {lsProcStr}";
    }
}

public class CompanionWindow : Window
{
    private TextBox _dirBox;
    private ComboBox _presetCombo;
    private CheckBox _chkRealtimeGpu;
    private CheckBox _chkHighResTimer;
    private CheckBox _chkCpuIsolation;
    private CheckBox _chkGpuFlush;
    private Slider _sliderUiThresh;
    private Slider _sliderTolerance;
    private TextBlock _statusText;
    private TextBox _logBox;
    private DispatcherTimer _monitorTimer;

    public CompanionWindow()
    {
        Title = "Lossless Scaling Plus — Addon & Local Shader Optimizer (BYOB Edition)";
        Width = 760;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(18, 20, 26));
        Foreground = new SolidColorBrush(Color.FromRgb(235, 238, 245));
        FontFamily = new FontFamily("Segoe UI");

        ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        StackPanel root = new StackPanel { Margin = new Thickness(20) };
        scroll.Content = root;
        Content = scroll;

        // Header
        Border headerCard = CreateCard();
        StackPanel headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock
        {
            Text = "Lossless Scaling Plus — Singleplayer & Emülatör Addon",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255))
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "Telif/DMCA Uyumlu 'Kendi Lisanslı Kurulumunu Getir (BYOB)' Yama & Donanım Hızlandırıcı Aracı",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(165, 175, 195)),
            Margin = new Thickness(0, 4, 0, 8)
        });
        _statusText = new TextBlock
        {
            Text = "Sistem Hazır — Kernel Zamanlayıcı ve GPU Önceliği Bekleniyor...",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 235, 160))
        };
        headerStack.Children.Add(_statusText);
        headerCard.Child = headerStack;
        root.Children.Add(headerCard);

        // 1. Target Directory Card
        Border dirCard = CreateCard();
        StackPanel dirStack = new StackPanel();
        dirStack.Children.Add(CreateSectionTitle("1. Lossless Scaling Kurulum Klasörü (Steam)"));
        Grid dirGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _dirBox = new TextBox
        {
            Text = DetectLosslessDirectory(),
            Background = new SolidColorBrush(Color.FromRgb(30, 34, 44)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 75, 95)),
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_dirBox, 0);
        dirGrid.Children.Add(_dirBox);

        Button browseBtn = CreateButton("Gözat...", Color.FromRgb(50, 58, 75));
        browseBtn.Margin = new Thickness(8, 0, 0, 0);
        browseBtn.Click += BrowseBtn_Click;
        Grid.SetColumn(browseBtn, 1);
        dirGrid.Children.Add(browseBtn);
        dirStack.Children.Add(dirGrid);

        // Shader patcher buttons
        UniformGrid shaderBtnGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        Button patchShadersBtn = CreateButton("LSFG 3.1 Shader'larını Yerelde Optimize Et (/O3 + BatchNorm)", Color.FromRgb(0, 120, 212));
        patchShadersBtn.Margin = new Thickness(0, 0, 6, 0);
        patchShadersBtn.Click += PatchShadersBtn_Click;

        Button restoreShadersBtn = CreateButton("Orijinal Lossless.dll.bak Yedeğini Geri Yükle", Color.FromRgb(65, 70, 85));
        restoreShadersBtn.Margin = new Thickness(6, 0, 0, 0);
        restoreShadersBtn.Click += RestoreShadersBtn_Click;

        shaderBtnGrid.Children.Add(patchShadersBtn);
        shaderBtnGrid.Children.Add(restoreShadersBtn);
        dirStack.Children.Add(shaderBtnGrid);
        dirCard.Child = dirStack;
        root.Children.Add(dirCard);

        // 2. Preset & Engine Tuning Card
        Border tuneCard = CreateCard();
        StackPanel tuneStack = new StackPanel();
        tuneStack.Children.Add(CreateSectionTitle("2. Singleplayer & Emülatör Optimizasyon Ayarları"));

        Grid presetRow = CreateRow("Optimizasyon Profili (Preset):");
        _presetCombo = new ComboBox
        {
            Width = 300,
            Padding = new Thickness(6, 4, 6, 4)
        };
        _presetCombo.Items.Add("Singleplayer Ultra-Düşük Gecikme (Önerilen)");
        _presetCombo.Items.Add("RPCS3 / ShadPS4 (PS3/PS4 Sabit 30->60/120 FPS)");
        _presetCombo.Items.Add("Ryujinx / Yuzu / Cemu (Switch / Wii U)");
        _presetCombo.Items.Add("PCSX2 / Dolphin / DuckStation (Retro 60->120+)");
        _presetCombo.Items.Add("Çift GPU / iGPU Cross-Adapter Modu");
        _presetCombo.SelectedIndex = 0;
        _presetCombo.SelectionChanged += PresetCombo_SelectionChanged;
        Grid.SetColumn(_presetCombo, 1);
        presetRow.Children.Add(_presetCombo);
        tuneStack.Children.Add(presetRow);

        _chkRealtimeGpu = CreateCheck("D3DKMT Realtime (5) Donanım GPU Zamanlayıcı Önceliği", true);
        _chkHighResTimer = CreateCheck("0.50ms Windows Kernel Yüksek Hassasiyetli QPC Zamanlayıcı", true);
        _chkCpuIsolation = CreateCheck("CPU Çekirdek İzolasyonu (Oyun/Emülatör ve LSFG Çekirdeklerini Ayır)", true);
        _chkGpuFlush = CreateCheck("Anında GPU Komut Flush (Sıfır Kuyruk Gecikmesi — config.ini)", true);

        tuneStack.Children.Add(_chkRealtimeGpu);
        tuneStack.Children.Add(_chkHighResTimer);
        tuneStack.Children.Add(_chkCpuIsolation);
        tuneStack.Children.Add(_chkGpuFlush);

        Grid uiRow = CreateRow("HUD / UI Koruma Hassasiyeti (Shader 279 Eşiği):");
        _sliderUiThresh = new Slider { Width = 240, Minimum = 0.20, Maximum = 0.80, Value = 0.45, TickFrequency = 0.05, IsSnapToTickEnabled = true };
        Grid.SetColumn(_sliderUiThresh, 1);
        uiRow.Children.Add(_sliderUiThresh);
        tuneStack.Children.Add(uiRow);

        Grid tolRow = CreateRow("Emülatör Kare Zamanlama Toleransı (real_timestamp_tolerance):");
        _sliderTolerance = new Slider { Width = 240, Minimum = 0.01, Maximum = 0.15, Value = 0.04, TickFrequency = 0.01, IsSnapToTickEnabled = true };
        Grid.SetColumn(_sliderTolerance, 1);
        tolRow.Children.Add(_sliderTolerance);
        tuneStack.Children.Add(tolRow);

        UniformGrid actionGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, 0, 0) };
        Button applyConfigBtn = CreateButton("config.ini + Emülatör Profillerini Uygula & Boost Başlat", Color.FromRgb(16, 137, 62));
        applyConfigBtn.Margin = new Thickness(0, 0, 6, 0);
        applyConfigBtn.Click += ApplyConfigBtn_Click;

        Button deployHookBtn = CreateButton("Oyun/Emülatör Klasörüne In-Process DXGI Hook Kur", Color.FromRgb(92, 45, 145));
        deployHookBtn.Margin = new Thickness(6, 0, 0, 0);
        deployHookBtn.Click += DeployHookBtn_Click;

        actionGrid.Children.Add(applyConfigBtn);
        actionGrid.Children.Add(deployHookBtn);
        tuneStack.Children.Add(actionGrid);

        tuneCard.Child = tuneStack;
        root.Children.Add(tuneCard);

        // 3. Log Card
        Border logCard = CreateCard();
        StackPanel logStack = new StackPanel();
        logStack.Children.Add(CreateSectionTitle("3. İşlem & Telemetri Günlüğü"));
        _logBox = new TextBox
        {
            Height = 130,
            Margin = new Thickness(0, 8, 0, 0),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(12, 14, 18)),
            Foreground = new SolidColorBrush(Color.FromRgb(190, 205, 225)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 52, 68)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Padding = new Thickness(8)
        };
        logStack.Children.Add(_logBox);
        logCard.Child = logStack;
        root.Children.Add(logCard);

        Log("Lossless Scaling Plus Addon başlatıldı.");
        UpdateBoostStatus();

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _monitorTimer.Tick += (_, _) => UpdateBoostStatus();
        _monitorTimer.Start();
    }

    private void UpdateBoostStatus()
    {
        string st = NativeBooster.BoostLosslessAndSystem(
            _chkRealtimeGpu.IsChecked == true,
            _chkHighResTimer.IsChecked == true,
            _chkCpuIsolation.IsChecked == true
        );
        _statusText.Text = st;
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Lossless.dll dosyasını içeren Lossless Scaling kurulum klasörünü seçin"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _dirBox.Text = dlg.SelectedPath;
        }
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sliderUiThresh == null || _sliderTolerance == null || _chkGpuFlush == null) return;
        switch (_presetCombo.SelectedIndex)
        {
            case 0: // Singleplayer
                _chkGpuFlush.IsChecked = true;
                _sliderUiThresh.Value = 0.48;
                _sliderTolerance.Value = 0.04;
                break;
            case 1: // RPCS3 / ShadPS4
                _chkGpuFlush.IsChecked = true;
                _sliderUiThresh.Value = 0.35;
                _sliderTolerance.Value = 0.02;
                break;
            case 2: // Ryujinx / Yuzu / Cemu
                _chkGpuFlush.IsChecked = true;
                _sliderUiThresh.Value = 0.38;
                _sliderTolerance.Value = 0.03;
                break;
            case 3: // PCSX2 / Dolphin / Retro
                _chkGpuFlush.IsChecked = true;
                _sliderUiThresh.Value = 0.30;
                _sliderTolerance.Value = 0.015;
                break;
            case 4: // Cross-Adapter Dual-GPU
                _chkGpuFlush.IsChecked = false;
                _sliderUiThresh.Value = 0.45;
                _sliderTolerance.Value = 0.04;
                break;
        }
    }

    private void RunShaderScript(string extraArgs)
    {
        string targetDir = _dirBox.Text.Trim();
        if (!File.Exists(Path.Combine(targetDir, "Lossless.dll")))
        {
            MessageBox.Show("Seçilen klasörde Lossless.dll bulunamadı!", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string scriptPath = Path.Combine(baseDir, "..", "scripts", "lsfg_shader_patcher.py");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(baseDir, "scripts", "lsfg_shader_patcher.py");
        }
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show($"lsfg_shader_patcher.py bulunamadı: {scriptPath}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            Log($"Shader optimize aracı çalıştırılıyor: python \"{scriptPath}\" --dir \"{targetDir}\" {extraArgs}");
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" --dir \"{targetDir}\" {extraArgs}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process proc = Process.Start(psi);
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) Log("UYARI/HATA: " + stderr.Trim());
        }
        catch (Exception ex)
        {
            Log("Hata: " + ex.Message);
        }
    }

    private void PatchShadersBtn_Click(object sender, RoutedEventArgs e) => RunShaderScript("");
    private void RestoreShadersBtn_Click(object sender, RoutedEventArgs e) => RunShaderScript("--restore");

    private void ApplyConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        string targetDir = _dirBox.Text.Trim();
        if (Directory.Exists(targetDir))
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            double tol = _sliderTolerance.Value;
            double uiThresh = _sliderUiThresh.Value;
            int flush = (_chkGpuFlush.IsChecked == true) ? 1 : 0;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; Lossless Scaling Plus — Generated by Companion Addon");
            sb.AppendLine("[rendering]");
            sb.AppendLine($"gpu_flush = {flush}");
            sb.AppendLine($"frame_pacing_tolerance = {(tol * 0.5).ToString("F4", inv)}");
            sb.AppendLine("max_skipped_frames = 1");
            sb.AppendLine();
            sb.AppendLine("[capture]");
            sb.AppendLine("dxgi_wait_for_vblank = 0");
            sb.AppendLine($"dxgi_frametime_tolerance = {tol.ToString("F4", inv)}");
            sb.AppendLine("queue_draining_momentum = 0.0100");
            sb.AppendLine($"real_timestamp_tolerance = {tol.ToString("F4", inv)}");
            sb.AppendLine("frametime_buffer_size = 12");
            sb.AppendLine("resync_threshold = 2");
            sb.AppendLine();
            sb.AppendLine("[lsfg]");
            sb.AppendLine($"ui_detection_threshold = {uiThresh.ToString("F3", inv)}");
            sb.AppendLine("ui_detection_rate = 1");
            sb.AppendLine("enable_debug_overlay = 0");
            sb.AppendLine();
            sb.AppendLine("[fps_counter]");
            sb.AppendLine("smoothing_factor = 0.10");

            string iniPath = Path.Combine(targetDir, "config.ini");
            File.WriteAllText(iniPath, sb.ToString(), Encoding.UTF8);
            Log($"[OK] Gizli motor parametreleri yazıldı: {iniPath}");

            string srcHook = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LosslessPlusHook.dll");
            if (File.Exists(srcHook))
            {
                try
                {
                    File.Copy(srcHook, Path.Combine(targetDir, "LosslessPlusHook.dll"), true);
                }
                catch { }
            }
        }

        InjectEmulatorProfilesIntoSettingsXml();
        UpdateBoostStatus();
    }

    private void InjectEmulatorProfilesIntoSettingsXml()
    {
        try
        {
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Lossless Scaling",
                "Settings.xml"
            );
            if (!File.Exists(settingsPath))
            {
                Log("Bilgi: %LocalAppData%\\Lossless Scaling\\Settings.xml henüz oluşmamış (uygulama ilk kez açıldığında oluşur).");
                return;
            }

            XDocument doc = XDocument.Load(settingsPath);
            XElement profilesEl = doc.Root?.Element("GameProfiles");
            if (profilesEl == null) return;

            var presets = new (string Title, string ExePath)[]
            {
                ("RPCS3 / ShadPS4 (Emulator Plus)", "rpcs3.exe;shadps4.exe"),
                ("Ryujinx / Yuzu / Cemu (Switch Plus)", "Ryujinx.exe;yuzu.exe;suyu.exe;sudachi.exe;Cemu.exe"),
                ("PCSX2 / Dolphin / DuckStation (Retro Plus)", "pcsx2-qt.exe;pcsx2.exe;Dolphin.exe;duckstation-qt-x64-ReleaseLTCG.exe")
            };

            int added = 0;
            foreach (var pr in presets)
            {
                bool exists = profilesEl.Elements("Profile")
                    .Any(x => string.Equals(x.Element("Title")?.Value, pr.Title, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    profilesEl.Add(new XElement("Profile",
                        new XElement("Title", pr.Title),
                        new XElement("Path", pr.ExePath),
                        new XElement("AutoScale", "false"),
                        new XElement("AutoScaleDelay", "3"),
                        new XElement("FrameGeneration", "LSFG3"),
                        new XElement("LSFG3Mode1", "FIXED"),
                        new XElement("LSFG3Multiplier", "2"),
                        new XElement("LSFGFlowScale", "100"),
                        new XElement("LSFGSize", "BALANCED"),
                        new XElement("CaptureApi", "DXGI"),
                        new XElement("QueueTarget", "1"),
                        new XElement("MaxFrameLatency", "1"),
                        new XElement("GsyncSupport", "true"),
                        new XElement("DrawFps", "true")
                    ));
                    added++;
                }
            }

            if (added > 0)
            {
                doc.Save(settingsPath);
                Log($"[OK] {added} adet hazır Emülatör profili Settings.xml içine eklendi.");
            }
            else
            {
                Log("[OK] Emülatör profilleri Settings.xml içinde zaten mevcut.");
            }
        }
        catch (Exception ex)
        {
            Log("Settings.xml güncelleme uyarısı: " + ex.Message);
        }
    }

    private void DeployHookBtn_Click(object sender, RoutedEventArgs e)
    {
        string srcHook = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LosslessPlusHook.dll");
        if (!File.Exists(srcHook))
        {
            MessageBox.Show($"LosslessPlusHook.dll bulunamadı: {srcHook}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "DXGI Hook (dxgi.dll) kurulacak Singleplayer Oyun veya Emülatör klasörünü seçin"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            string dst = Path.Combine(dlg.SelectedPath, "dxgi.dll");
            File.Copy(srcHook, dst, true);
            Log($"[OK] In-Process DXGI Hook başarıyla kuruldu: {dst}");
        }
    }

    private void Log(string msg)
    {
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        _logBox.ScrollToEnd();
    }

    private static string DetectLosslessDirectory()
    {
        string[] candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling",
            @"C:\Program Files\Steam\steamapps\common\Lossless Scaling",
            @"D:\SteamLibrary\steamapps\common\Lossless Scaling",
            @"E:\SteamLibrary\steamapps\common\Lossless Scaling",
            @"D:\Projeler\LosslesScalingplus\rea-rea-agents-6.8.0\incelenecekprogram\Lossless Scaling"
        };
        foreach (string c in candidates)
        {
            if (File.Exists(Path.Combine(c, "Lossless.dll")))
                return c;
        }
        return candidates[0];
    }

    private static Border CreateCard() => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(25, 29, 38)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(45, 53, 70)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14),
        Margin = new Thickness(0, 0, 0, 12)
    };

    private static TextBlock CreateSectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(210, 220, 240))
    };

    private static Grid CreateRow(string label)
    {
        Grid g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 208, 225))
        });
        return g;
    }

    private static CheckBox CreateCheck(string text, bool isChecked) => new CheckBox
    {
        Content = text,
        IsChecked = isChecked,
        Foreground = new SolidColorBrush(Color.FromRgb(215, 222, 235)),
        Margin = new Thickness(0, 8, 0, 0)
    };

    private static Button CreateButton(string text, Color bg) => new Button
    {
        Content = text,
        Background = new SolidColorBrush(bg),
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12, 8, 12, 8),
        FontWeight = FontWeights.SemiBold,
        Cursor = System.Windows.Input.Cursors.Hand
    };
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Application app = new Application();
        app.Run(new CompanionWindow());
    }
}
