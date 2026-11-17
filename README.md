# Lossless Scaling Plus — LSFG 3.1/2.3 Shader Optimizer, In-Process DXGI Hook & Ultra-Low Latency Addon

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows 10/11 x64](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D6.svg)](#)
[![GPU Support: Universal](https://img.shields.io/badge/GPU-NVIDIA%20%7C%20AMD%20%7C%20Intel-success.svg)](#)
[![Architecture: BYOB Clean-Room](https://img.shields.io/badge/Model-Bring%20Your%20Own%20Binary%20(Legal)-success.svg)](#legal--dmca-compliance)

> **Legal & DMCA Notice:** This project is an independent, unofficial open-source **Companion Addon & Local Shader Recompiler**. It is **not** affiliated with, endorsed by, or sponsored by THS / Lossless Scaling. **Zero proprietary binaries, shaders, weights, or decompiled code from Lossless Scaling are distributed in this repository.** All optimizations run locally on the end user's machine against their own legitimately purchased Steam copy of Lossless Scaling (*Bring Your Own Binary*).

---

## Key Features & Innovations

Built to work universally across **all GPUs (NVIDIA, AMD, Intel)** and **all Direct3D 11 singleplayer games & emulators** (**RPCS3, ShadPS4, Ryujinx, Yuzu, Cemu, PCSX2, Dolphin, DuckStation**), with zero game-specific memory offsets or vendor lock-in:

### 1. Tier 1 — Lossless Local Recompiler for All 109 CNN Compute Shaders (`-54.94%` GPU Instructions)
* Deep binary analysis of `Lossless.dll` revealed **109 Direct3D 11 CNN Compute Shaders** (`63` high-capacity 24-channel 6-UAV shaders in `ID 172–239` and `46` 16-channel 4-UAV LSFG 3.1 shaders in `ID 257–302`) compiled with `skipOptimization` (`/Od`), unfolded `BatchNorm` affine chains, and row-major `dp4` dot products.
* **Built-in Native C# Recompiler (`src/CompanionApp/ShaderPatcher.cs` — Zero Python Required):**
  1. **Column-Major `4×mad` Matrix Transposition:** Transposes $4\times 4$ Conv2D weight matrices at compile time from `4×dp4 + add` (5 instruction slots) into broadcast column-major `4×mad` chains (4 instruction slots, `-20%` Conv2D ALU overhead).
  2. **Algebraic BatchNorm Folding:** Folds static `BatchNorm` constants (`W_eff = inv_std * gamma`, `B_eff = beta - mean * W_eff`) directly into hardware `mad` instructions.
  3. **Full `/O3` Recompilation:** Recompiles all 109 shaders locally via Windows' built-in `D3DCompiler_47.dll` using `D3DCOMPILE_OPTIMIZATION_LEVEL3` (`/O3`) and verifies every binding signature before touching `Lossless.dll` (preserving `Lossless.dll.bak`).
  4. **WaitableTimer QPC Spin-Wait Cushion (`-3.0ms`):** Upgrades the WaitableTimer spin cushion inside `Lossless.dll` from `-2.0 ms` to `-3.0 ms` to prevent OS scheduler wake-up jitter.
* **Result:** **100% mathematically identical (lossless) output** while eliminating **57,040 GPU instructions** (`103,816` $\rightarrow$ **`46,776` instruction slots, `-54.94%`**). Also generates an optional `Lossless_UltraPerf.dll` variant (`46,488` slots, `-55.22%`) for handhelds/iGPUs.

### 2. Tier 2 & 3 & 4 — Universal In-Process DXGI Hook & Zero-Copy Shared Bridge (`LosslessPlusHook.dll`)
Designed strictly for **singleplayer games and emulators** (do **not** deploy `dxgi.dll` into anti-cheat protected online games):
* **Full 20-Export System32 `dxgi.dll` Proxy + Auto VTable Hooking (Tier 3):** Automatically intercepts `IDXGIFactory::CreateSwapChain`, `IDXGIFactory2::CreateSwapChainForHwnd`, `CreateSwapChainForComposition`, `IDXGISwapChain::Present` (`VTable[8]`), `ResizeBuffers` (`VTable[13]`), and `IDXGISwapChain1::Present1` (`VTable[22]`), plus `LosslessPlusAutoHookD3D11`.
* **Double-Buffered Cross-Device Zero-Copy Bridge (Tier 4):** On every `Present()`, resolves/copies the BackBuffer GPU texture into double-buffered `D3D11_RESOURCE_MISC_SHARED` textures (`IDXGIResource::GetSharedHandle`) and exposes handles, dimensions, and exact `QueryPerformanceCounter` timestamps in `Local\LosslessPlusSharedState` + `Local\LosslessPlusFrameEvent` (`LosslessPlusOpenSharedFrame`).
* **QPC Spin-Wait Micro-Frame-Pacer (Tier 2):** Enforces rock-solid `30 / 40 / 48 / 60 FPS` pacing using a CPU `pause` (`YieldProcessor`) spin-wait loop for fixed-tickrate emulators.

### 3. 4-Layer Ultra-Low Input Lag Package (Vendor-Agnostic: NVIDIA / AMD / Intel)
1. **In-Process "Open-Reflex / Anti-Lag" (`LSP_FLAG_OPEN_REFLEX`):** Forces `IDXGIDevice1::SetMaximumFrameLatency(1)` on the game/emulator's D3D11 device (cutting Windows' default 3-frame driver queue down to 1) and runs a 2-slot `D3D11_QUERY_EVENT` hardware GPU completion fence after `Present()` so input sampling waits until the GPU finishes frame $N-1$.
2. **Automatic `DXGI_SWAP_EFFECT_FLIP_DISCARD` Upgrade & DirectFlip (`LSP_FLAG_UPGRADE_FLIP`):** Upgrades legacy windowed Blt-model DX11 SwapChains (`DISCARD` / `SEQUENTIAL`) to `FLIP_DISCARD` (`BufferCount = 2`), bypassing DWM's 1-frame (`+16.6 ms`) composition buffer, and enables Windows `SwapEffectUpgradeEnable=1`.
3. **Zero-Queue LSFG (`QueueTarget = 0`) + Allow Tearing (`0x200`):** Configures all profiles in `Settings.xml` for `QueueTarget = 0` (immediate interpolation path), `MaxFrameLatency = 1`, `Vsync = false`, and `GsyncSupport = true`, plus writes binary-verified `config.ini` tuning (`[rendering] flush = 1`).
4. **Kernel, GPU & Input Thread Boost:** Enables `SeIncreaseBasePriorityPrivilege` + `D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME (5)`, locks Windows kernel timer resolution to **`0.50 ms`** (`NtSetTimerResolution(5000)`), isolates game vs. Lossless Scaling CPU cores, and elevates the foreground game's `WM_INPUT` thread priority to `THREAD_PRIORITY_HIGHEST`.

### 4. Drag-and-Drop Zero-Python Installer & Built-in Proof Verification
* **Zero Python Needed:** Runs standalone using .NET 8 Desktop Runtime and Windows' native `D3DCompiler_47.dll`.
* **Drag-and-Drop Setup:** Drag your `Lossless Scaling` Steam folder (or `Lossless.dll`) directly onto the `LosslessPlusCompanion.exe` window or `Install-LosslessPlus.bat` to patch and configure in one step. Drop a singleplayer game or emulator folder to deploy `dxgi.dll`.
* **1-Click Proof & Visual Test Mode:** Includes **"Verify & Show Proof"** (disassembles all 202 DXBC shaders on disk to verify `0` remaining `skipOptimization` shaders and exact instruction savings) and a **"Visual Test Mode"** toggle (`ui_detection_debug = 1` + `show_captured = 1` in `config.ini`).

---

## Verified Benchmarks

| Metric | Original `Lossless.dll` | Lossless Scaling Plus V3.0 (`Lossless`) | `UltraPerf V3.0` (`+5-Tap Cross`) |
| :--- | :---: | :---: | :---: |
| **HLSL Compiler Flag** | `skipOptimization` (`/Od`) | **`D3DCOMPILE_OPTIMIZATION_LEVEL3` (`/O3`)** | **`/O3` + 5-Tap Cross** |
| **4×4 Conv2D Matrix Layout** | `4×dp4 + add` (5 Slots) | **Column-Major `4×mad` (4 Slots)** | **Column-Major `4×mad` (4 Slots)** |
| **BatchNorm Overhead** | 3 Ops / Channel | **1 Op / Channel (`mad`)** | **1 Op / Channel (`mad`)** |
| **6-UAV Shaders (`ID 172–239`, 63)** | `81,414` slots | **`36,895` slots (`-54.68%`)** | **`36,895` slots (`-54.68%`)** |
| **4-UAV Shaders (`ID 257–302`, 46)** | `22,402` slots | **`9,881` slots (`-55.89%`)** | **`9,593` slots (`-57.18%`)** |
| **Total (109 CNN Shaders)** | `103,816` slots | **`46,776` slots (`-54.94%`)** | **`46,488` slots (`-55.22%`)** |
| **GPU Instructions Eliminated** | — | **`57,040` instructions / pass** | **`57,328` instructions / pass** |
| **Visual / Math Fidelity** | Reference | **100% Exact (Lossless)** | **Ultra-Performance (iGPU/Handheld)** |
| **In-Process Hardware Test** | — | **`swapEffect=4` (`FLIP_DISCARD`), `maxLatency=1`, `RGBA=(64,128,191,255)` Cross-Device Zero-Copy** | **Same** |

---

## Repository Structure

```text
LosslessScalingPlus-Addon/
├── Install-LosslessPlus.bat          # One-click / Drag-and-Drop Companion launcher
├── LICENSE                           # MIT License + BYOB Third-Party Disclaimer
├── README.md                         # Documentation
├── .gitignore                        # Prevents proprietary binaries or build caches from being committed
├── bin/
│   ├── LosslessPlusCompanion.exe     # Standalone WPF Addon, Drag-Drop Patcher & Kernel/Latency Booster
│   ├── LosslessPlusCompanion.dll
│   ├── LosslessPlusCompanion.deps.json
│   ├── LosslessPlusCompanion.runtimeconfig.json
│   └── LosslessPlusHook.dll          # Clean-room x64 DXGI Proxy, Open-Reflex & Zero-Copy Shared Bridge
├── scripts/
│   ├── lsfg_shader_patcher.py        # Optional CLI shader optimizer (for headless automation)
│   └── build_hook_dll.py             # MSVC builder & two-device D3D11 hardware verification harness
└── src/
    ├── CompanionApp/
    │   ├── LosslessPlusCompanion.csproj
    │   ├── Program.cs                # WPF UI, Kernel Booster, DirectFlip, QueueTarget=0 & IPC Telemetry
    │   └── ShaderPatcher.cs          # Native C# DXBC Disassembler, Transpiler, /O3 Compiler & PE Updater
    └── hook/
        ├── LosslessPlusHook.cpp      # Tier 2-4 C++ DXGI VTable Hook, Open-Reflex & Zero-Copy Bridge
        └── LosslessPlusHook.def      # Complete 20-export DXGI proxy + LosslessPlus API definitions
```

---

## Installation & Usage

### Method 1: Drag-and-Drop / GUI Setup (No Python Required)
1. **Close Lossless Scaling completely** (including the system tray icon).
2. Run **`Install-LosslessPlus.bat`** or **`bin\LosslessPlusCompanion.exe`** (*Run as Administrator* is recommended for `D3DKMT REALTIME` GPU scheduling priority and `SeIncreaseBasePriorityPrivilege`).
3. Your Steam `Lossless Scaling` folder is auto-detected across all drives (or simply **drag and drop** your `Lossless Scaling` folder / `Lossless.dll` directly onto the window).
4. Click **"⚡ Tek Tıkla Kur & 109 Shader Optimize Et"** to back up `Lossless.dll` as `Lossless.dll.bak`, recompile all 109 shaders locally, and tune `config.ini` + `Settings.xml` (`QueueTarget=0`, `MaxFrameLatency=1`, `AllowTearing`, prebuilt RPCS3 / ShadPS4 / Ryujinx / Yuzu / Cemu / PCSX2 / Dolphin / DuckStation profiles).

### Method 2: Deploy In-Process Open-Reflex & DXGI Hook (`dxgi.dll`)
> **Singleplayer & Emulators Only:** Do not place `dxgi.dll` inside anti-cheat protected multiplayer games.
1. In **LosslessPlusCompanion**, click **"Oyun/Emülatör Klasörüne In-Process DXGI Hook Kur"** (or drag and drop the game/emulator folder onto the window) and select the folder containing the game/emulator `.exe`.
2. This copies `LosslessPlusHook.dll` as `dxgi.dll` next to the `.exe`, enabling **Open-Reflex (`MaxFrameLatency=1` + `D3D11_QUERY_EVENT`)**, **Flip-Discard DWM Bypass**, **Zero-Copy Shared Texture capture**, and optional **30/40/48/60 FPS QPC Micro-Pacing**.

### Method 3: How to Verify & Test
1. **Numeric Binary Proof:** Click **"🔍 Çalıştığını Doğrula & Kanıt Göster"** inside `LosslessPlusCompanion.exe`. It disassembles every shader in both `Lossless.dll` and `Lossless.dll.bak` on the fly and verifies `0` remaining `skipOptimization` shaders and the exact `-54.94%` (`57,040` instruction) reduction.
2. **Visual In-Game Verification:** Enable **"👁️ Görsel Test Modu (`ui_detection_debug=1`)"**, click **"config.ini + Emülatör Profillerini Uygula"**, and enable `Draw FPS` in Lossless Scaling:
   * Top-left FPS counter shows **`Captured / Generated`** dual FPS (`show_captured = 1`).
   * Static HUD elements highlight live on screen (`ui_detection_debug = 1`). Uncheck and re-apply to restore normal rendering.

### Optional CLI Usage (Python 3.9+)
```powershell
python scripts/lsfg_shader_patcher.py --dir "C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling"
python scripts/lsfg_shader_patcher.py --dir "C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling" --restore
```

---

## Building from Source
* **Companion App (.NET 8 Windows x64 SDK):**
  ```powershell
  dotnet build src/CompanionApp/LosslessPlusCompanion.csproj -c Release -o bin
  ```
* **Native Hook DLL + Hardware Verification Test (Visual Studio 2022 C++ Build Tools + Python):**
  ```powershell
  python scripts/build_hook_dll.py
  ```

---

## Legal & DMCA Compliance (Bring Your Own Binary)
* **100% Clean-Room Implementation:** Every source file (`Program.cs`, `ShaderPatcher.cs`, `LosslessPlusHook.cpp`, `LosslessPlusHook.def`, `lsfg_shader_patcher.py`, `build_hook_dll.py`) is authored from scratch against documented Windows APIs (`D3DCompiler_47.dll`, `d3d11.dll`, `dxgi.dll`, `kernel32.dll`, `gdi32.dll`, `ntdll.dll`).
* **Local-Only Transformation:** Shader recompilation happens strictly on the user's machine using their own Steam-purchased `Lossless.dll`.
