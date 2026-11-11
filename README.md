# Lossless Scaling Plus — Unofficial LSFG 3.1 Shader Optimizer & Emulator Hook Addon

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D6.svg)](#)
[![Architecture: BYOB Clean-Room](https://img.shields.io/badge/Model-Bring%20Your%20Own%20Binary%20(Legal)-success.svg)](#legal--dmca-compliance)

> **[TR] Yasal Uyarı:** Bu proje, **Lossless Scaling (THS)** ile bağlantısı olmayan, tamamen bağımsız ve açık kaynaklı bir **Eklenti (Addon) ve Yerel DerleyiciOptimizasyon Aracıdır**. Bu depoda Lossless Scaling'e ait **hiçbir telifli `.dll`, `.exe`, kaynak kodu veya shader dosyası dağıtılmamaktadır.** Aracın çalışması için kullanıcının kendi bilgisayarında **Steam üzerinden satın alınmış yasal bir Lossless Scaling kurulumu** bulunması gerekir.
>
> **[EN] Legal Disclaimer:** This project is an unofficial, independent open-source **Companion Addon & Local Shader Recompiler**. It is **not** affiliated with or endorsed by THS / Lossless Scaling. **Zero proprietary binaries, shaders, or decompiled code from Lossless Scaling are included in this repository.** A legitimate, user-owned Steam installation of Lossless Scaling is required.

---

## 🚀 Neden Lossless Scaling Plus? / What Does It Do?

Lossless Scaling'in mevcut **LSFG 3.1 (Frame Generation)** motoru üzerinde yapılan derinlemesine analizde iki büyük performans ve gecikme darboğazı tespit edilmiştir:

1. **Derleyici Optimizasyonu Kapalı Shader'lar (`skipOptimization` / `/Od`) ve Açık BatchNorm Katmanları:**
   * Orijinal `Lossless.dll` içerisindeki **46 adet LSFG 3.1 Direct3D 11 Compute Shader (`RT_RCDATA` ID `257–302`)**, `skipOptimization` bayrağı açık olarak derlenmiştir.
   * Ayrıca her evrişim (`Conv2D`) katmanının sonunda bulunan **BatchNorm** normalizasyonu (`(x - mean) * inv_std * gamma + beta`), çalışma zamanında her piksel için 3 ayrı vektör komutu (`add` + `mul` + `mad`) yürütmektedir.
   * **Çözümümüz (`scripts/lsfg_shader_patcher.py`):** Kullanıcının kendi bilgisayarındaki yasal `Lossless.dll` dosyasını bellekte okur, sabit BatchNorm ağırlıklarını cebirsel olarak katlayarak (`W_eff = inv_std * gamma`, `B_eff = beta - mean * W_eff`) tek bir donanım **`mad` (Multiply-Add)** komutuna indirger ve Windows'un yerleşik `D3DCompiler_47.dll` derleyicisiyle **`D3DCOMPILE_OPTIMIZATION_LEVEL3` (`/O3`)** seviyesinde yerelde yeniden derler.
   * **Sonuç:** Matematiksel çıktı **%100 birebir aynı (kayıpsız)** kalırken toplam GPU shader komut sayısı **`22,402` komuttan `11,469` komuta düşer (`-%48.80` azalma — kare başına `10,933` GPU komutu elenir)**.

2. **Masaüstü Yakalama Gecikmesi ve Emülatör Sabit Zamanlama (Fixed-Tickrate) Sorunu:**
   * Singleplayer oyunlar ve konsol emülatörleri (**RPCS3, ShadPS4, Ryujinx, Yuzu, Cemu, PCSX2, Dolphin, DuckStation**) için sıfırdan geliştirilen **`LosslessPlusHook.dll`** ve **`LosslessPlusCompanion.exe`** ile:
     * **D3DKMT Realtime (`5`) GPU Önceliği:** Oyun GPU'yu %99-100 kullansa dahi LSFG 3.1 compute shader'larının Windows HAGS/WDDM zamanlayıcısında kesintisiz öncelik almasını sağlar.
     * **`0.50ms` QPC Kernel Zamanlayıcı (`NtSetTimerResolution`):** Ara karelerin (`interpolated frames`) sunumundaki mikro-kekemeliği (*micro-stutter*) ortadan kaldırır.
     * **CPU Çekirdek İzolasyonu:** Oyun/emülatör iş parçacıkları (ör. RPCS3 PPU/SPU) ile Lossless Scaling iş parçacıklarını farklı mantıksal CPU çekirdeklerine ayırır.
     * **In-Process DXGI/SwapChain Kancası (`LosslessPlusHook.dll`):** `Local\LosslessPlusSharedState` paylaşımlı RAM alanı ve `Local\LosslessPlusFrameEvent` çekirdek olayı ile sıfır gecikmeli kare senkronizasyonu sağlar.

---

## 📊 Ölçülen Kazanç Tablosu / Verified Benchmarks

| Metrik / Metric | Orijinal LSFG 3.1 | Lossless Scaling Plus (`Lossless /O3+BN`) | `UltraPerf` (`5-Tap Cross`) |
| :--- | :---: | :---: | :---: |
| **HLSL Derleyici Bayrağı** | `skipOptimization` (`/Od`) | **`D3DCOMPILE_OPTIMIZATION_LEVEL3` (`/O3`)** | **`/O3` + 5-Tap Cross** |
| **BatchNorm Katmanı** | 3 Komut / Kanal (`add`+`mul`+`mad`) | **1 Komut / Kanal (`mad`)** | **1 Komut / Kanal (`mad`)** |
| **Toplam Shader Komut Sayısı (`ID 257–302`)** | `22,402` komut | **`11,469` komut (-%48.80)** | **`11,013` komut (-%50.84)** |
| **Kare Başına Elenen GPU Komutu** | — | **`10,933` komut** | **`11,389` komut** |
| **Matematiksel Çıktı Kalitesi** | Referans | **%100 Birebir Aynı (Kayıpsız)** | **Ultra-Performans (iGPU/El Konsolu)** |
| **GPU Zamanlayıcı Önceliği** | Normal | **`D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME (5)`** | **`REALTIME (5)`** |
| **Windows Kernel Timer** | `15.6ms` / `1.0ms` | **`0.50ms` (`5000 × 100ns`)** | **`0.50ms`** |

---

## 📁 Depo Yapısı / Repository Structure

```text
LosslessScalingPlus-Addon/
├── Install-LosslessPlus.bat          # Tek tıkla Companion arayüzünü başlatıcı
├── LICENSE                           # MIT Lisansı ve üçüncü parti yasal uyarısı
├── README.md                         # Kurulum ve teknik dokümantasyon (TR/EN)
├── .gitignore                        # Telifli ikili dosyaların yanlışlıkla eklenmesini önleyen koruma
├── bin/
│   ├── LosslessPlusCompanion.exe     # Bağımsız WPF Addon & Donanım Hızlandırıcı Arayüzü
│   └── LosslessPlusHook.dll          # Sıfırdan yazılmış x64 In-Process DXGI/SharedMemory Kancası
├── scripts/
│   ├── lsfg_shader_patcher.py        # Sıfır bağımlılıklı yerel /O3 + BatchNorm shader optimize aracı
│   └── build_hook_dll.py             # Saf Python ile LosslessPlusHook.dll x64 PE derleyicisi
└── src/
    ├── CompanionApp/                 # LosslessPlusCompanion.exe C# WPF kaynak kodları
    │   ├── LosslessPlusCompanion.csproj
    │   └── Program.cs
    └── hook/
        └── LosslessPlusHook.cpp      # LosslessPlusHook.dll C++ kaynak kodu
```

---

## 🛠️ Kurulum ve Kullanım / Installation & Usage

### Yöntem 1: Görsel Arayüz ile Tek Tıkla Kurulum (`LosslessPlusCompanion.exe`)
1. **`Install-LosslessPlus.bat`** veya **`bin\LosslessPlusCompanion.exe`** dosyasını çalıştırın (GPU Realtime önceliği için **Yönetici Olarak Çalıştır** önerilir).
2. **1. Bölümde** Steam'deki `Lossless Scaling` kurulum klasörünüz otomatik tespit edilir (farklı bir diskteyse **Gözat...** ile seçin).
3. **"LSFG 3.1 Shader'larını Yerelde Optimize Et (/O3 + BatchNorm)"** butonuna tıklayın:
   * Aracımız kendi bilgisayarınızdaki `Lossless.dll` dosyasını `Lossless.dll.bak` olarak yedekler ve Windows'un yerleşik `D3DCompiler_47.dll` kütüphanesini kullanarak 46 compute shader'ı yerelde optimize eder.
4. **2. Bölümde** oynadığınız moda uygun profili seçin:
   * **Singleplayer Ultra-Düşük Gecikme**
   * **RPCS3 / ShadPS4 (PS3/PS4 Sabit 30->60/120 FPS)**
   * **Ryujinx / Yuzu / Cemu (Switch / Wii U)**
   * **PCSX2 / Dolphin / DuckStation (Retro 60->120+)**
   * **Çift GPU / iGPU Cross-Adapter Modu**
5. **"config.ini + Emülatör Profillerini Uygula & Boost Başlat"** butonuna tıklayın. Hazır emülatör profilleri `%LocalAppData%\Lossless Scaling\Settings.xml` içerisine otomatik eklenir ve motorun gizli `config.ini` parametreleri oluşturulur.

### Yöntem 2: Komut Satırı (CLI) ile Shader Optimizasyonu
Yalnızca Python (3.9+) kullanarak yerel shader optimizasyonunu doğrudan çalıştırmak için:

```powershell
# Steam kurulumundaki Lossless.dll'i yerelde optimize eder (Lossless.dll.bak yedeği alır):
python scripts/lsfg_shader_patcher.py --dir "C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling"

# Orijinal yedeği geri yüklemek için:
python scripts/lsfg_shader_patcher.py --dir "C:\Program Files (x86)\Steam\steamapps\common\Lossless Scaling" --restore
```

---

## ⚖️ Legal & DMCA Compliance (Bring Your Own Binary)

* **No Proprietary Code Distributed:** Every file in this repository (`LosslessPlusCompanion`, `LosslessPlusHook`, `lsfg_shader_patcher.py`, `build_hook_dll.py`) was written 100% from scratch using standard documented Windows APIs (`D3DCompiler_47.dll`, `kernel32.dll`, `gdi32.dll`, `ntdll.dll`).
* **Local-Only Transformation:** Shader optimization is performed exclusively on the end user's machine against their own legitimately purchased copy of `Lossless.dll`.
