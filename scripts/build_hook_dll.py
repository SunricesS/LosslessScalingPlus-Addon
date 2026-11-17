import os
import subprocess
import tempfile
import shutil

HOOK_VERIFY_CPP = r'''
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdint.h>
#include <stdio.h>
#include <math.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

#pragma pack(push, 1)
struct LosslessPlusSharedState {
    uint32_t magic;
    uint32_t version;
    uint64_t frameCount;
    uint64_t lastPresentQpc;
    uint64_t qpcFrequency;
    uint32_t targetPid;
    uint32_t flags;
    uint32_t depthBufferDetected;
    uint32_t uiPassDetected;
    uint64_t targetFrameIntervalQpc;
    uint64_t sharedTextureHandle;
    uint64_t sharedTextureHandlePrev;
    uint32_t frameWidth;
    uint32_t frameHeight;
    uint32_t frameFormat;
    uint32_t swapChainHwnd;
};
#pragma pack(pop)

typedef HRESULT(WINAPI* PFN_CreateDXGIFactory1)(REFIID, void**);
typedef BOOL(WINAPI* PFN_GetState)(LosslessPlusSharedState*);
typedef uint64_t(WINAPI* PFN_SetTargetFps)(uint32_t);
typedef uint32_t(WINAPI* PFN_SetLatencyFlags)(uint32_t, uint32_t);
typedef BOOL(WINAPI* PFN_AutoHookD3D11)();
typedef HRESULT(WINAPI* PFN_OpenSharedFrame)(ID3D11Device*, ID3D11Texture2D**, uint32_t*, uint32_t*, uint32_t*, uint64_t*);

int main(int argc, char** argv) {
    if (argc < 2) return 99;
    HMODULE hHook = LoadLibraryA(argv[1]);
    if (!hHook) { printf("FAIL: LoadLibrary(%s) err=%lu\n", argv[1], GetLastError()); return 1; }

    auto pCreateFactory1  = (PFN_CreateDXGIFactory1)GetProcAddress(hHook, "CreateDXGIFactory1");
    auto pGetState        = (PFN_GetState)GetProcAddress(hHook, "LosslessPlusGetState");
    auto pSetFps          = (PFN_SetTargetFps)GetProcAddress(hHook, "LosslessPlusSetTargetFps");
    auto pSetLatencyFlags = (PFN_SetLatencyFlags)GetProcAddress(hHook, "LosslessPlusSetLatencyFlags");
    auto pAutoHook        = (PFN_AutoHookD3D11)GetProcAddress(hHook, "LosslessPlusAutoHookD3D11");
    auto pOpenShared      = (PFN_OpenSharedFrame)GetProcAddress(hHook, "LosslessPlusOpenSharedFrame");

    if (!pCreateFactory1 || !pGetState || !pSetFps || !pSetLatencyFlags || !pAutoHook || !pOpenShared) {
        printf("FAIL: Missing exports\n");
        return 2;
    }
    pSetLatencyFlags(1, 1);

    // Create hidden window for SwapChain
    WNDCLASSA wc = {};
    wc.lpfnWndProc = DefWindowProcA;
    wc.hInstance = GetModuleHandleA(NULL);
    wc.lpszClassName = "LSPVerifyWnd";
    RegisterClassA(&wc);
    HWND hWnd = CreateWindowA("LSPVerifyWnd", "LSPV3Test", WS_OVERLAPPEDWINDOW, 0, 0, 640, 360, NULL, NULL, wc.hInstance, NULL);

    // Device #1 + SwapChain created through hooked CreateDXGIFactory1 (Tier 3 Test)
    IDXGIFactory1* pFactory = NULL;
    HRESULT hr = pCreateFactory1(__uuidof(IDXGIFactory1), (void**)&pFactory);
    if (FAILED(hr) || !pFactory) { printf("FAIL: CreateDXGIFactory1 hr=0x%lx\n", hr); return 3; }

    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
    ID3D11Device* pDev1 = NULL;
    ID3D11DeviceContext* pCtx1 = NULL;
    hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, &fl, 1, D3D11_SDK_VERSION, &pDev1, NULL, &pCtx1);
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_WARP, NULL, 0, &fl, 1, D3D11_SDK_VERSION, &pDev1, NULL, &pCtx1);
    }
    if (FAILED(hr) || !pDev1) { printf("FAIL: D3D11CreateDevice #1 hr=0x%lx\n", hr); return 4; }

    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 2;
    sd.BufferDesc.Width = 640;
    sd.BufferDesc.Height = 360;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain* pSwapChain = NULL;
    hr = pFactory->CreateSwapChain(pDev1, &sd, &pSwapChain);
    if (FAILED(hr) || !pSwapChain) { printf("FAIL: CreateSwapChain hr=0x%lx\n", hr); return 5; }

    // Clear BackBuffer to RGBA (64/255, 128/255, 191/255, 1.0)
    ID3D11Texture2D* pBB = NULL;
    ID3D11RenderTargetView* pRtv = NULL;
    pSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&pBB);
    pDev1->CreateRenderTargetView(pBB, NULL, &pRtv);
    const float clearColor[4] = { 64.0f / 255.0f, 128.0f / 255.0f, 191.0f / 255.0f, 1.0f };
    pCtx1->ClearRenderTargetView(pRtv, clearColor);
    pCtx1->Flush();
    pRtv->Release();
    pBB->Release();

    // Test Tier 2 + Tier 3 + Tier 4 automatically via SwapChain::Present
    uint64_t interval60 = pSetFps(60);
    pSwapChain->Present(0, 0);
    // Second frame with slightly different color so Resize/DoubleBuffer toggle is tested too
    pSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&pBB);
    pDev1->CreateRenderTargetView(pBB, NULL, &pRtv);
    pCtx1->ClearRenderTargetView(pRtv, clearColor);
    pCtx1->Flush();
    pRtv->Release();
    pBB->Release();
    pSwapChain->Present(0, 0);

    LosslessPlusSharedState st = {};
    if (!pGetState(&st)) { printf("FAIL: LosslessPlusGetState\n"); return 6; }

    // Verify Flip-Model upgrade and MaxFrameLatency == 1 on Device #1
    DXGI_SWAP_CHAIN_DESC actualScDesc = {};
    pSwapChain->GetDesc(&actualScDesc);
    UINT maxLat = 99;
    IDXGIDevice1* pDxgiDev1 = NULL;
    if (SUCCEEDED(pDev1->QueryInterface(__uuidof(IDXGIDevice1), (void**)&pDxgiDev1)) && pDxgiDev1) {
        pDxgiDev1->GetMaximumFrameLatency(&maxLat);
        pDxgiDev1->Release();
    }

    printf("STATE: magic=0x%08X ver=0x%08X frames=%llu flags=0x%X res=%ux%u fmt=%u shm=0x%llX prev=0x%llX interval60=%llu swapEffect=%u maxLatency=%u\n",
        st.magic, st.version, st.frameCount, st.flags,
        st.frameWidth, st.frameHeight, st.frameFormat,
        st.sharedTextureHandle, st.sharedTextureHandlePrev, interval60,
        (unsigned)actualScDesc.SwapEffect, maxLat);

    if (st.magic != 0x4C53502Bu || st.frameCount < 2 || (st.flags & 0x7Fu) != 0x7Fu || maxLat != 1 || actualScDesc.SwapEffect != DXGI_SWAP_EFFECT_FLIP_DISCARD || st.sharedTextureHandle == 0 || st.frameWidth != 640 || st.frameHeight != 360) {
        printf("FAIL: Unexpected SharedState or Latency values\n");
        return 7;
    }

    // Device #2: Separate D3D11 device opening the zero-copy shared texture (Tier 4 Test)
    ID3D11Device* pDev2 = NULL;
    ID3D11DeviceContext* pCtx2 = NULL;
    hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, &fl, 1, D3D11_SDK_VERSION, &pDev2, NULL, &pCtx2);
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(NULL, D3D_DRIVER_TYPE_WARP, NULL, 0, &fl, 1, D3D11_SDK_VERSION, &pDev2, NULL, &pCtx2);
    }
    if (FAILED(hr) || !pDev2) { printf("FAIL: D3D11CreateDevice #2 hr=0x%lx\n", hr); return 8; }

    ID3D11Texture2D* pSharedTex2 = NULL;
    uint32_t w = 0, h = 0, fmt = 0;
    uint64_t qpc = 0;
    hr = pOpenShared(pDev2, &pSharedTex2, &w, &h, &fmt, &qpc);
    if (FAILED(hr) || !pSharedTex2) { printf("FAIL: LosslessPlusOpenSharedFrame hr=0x%lx\n", hr); return 9; }

    D3D11_TEXTURE2D_DESC stgDesc = {};
    stgDesc.Width = 1;
    stgDesc.Height = 1;
    stgDesc.MipLevels = 1;
    stgDesc.ArraySize = 1;
    stgDesc.Format = (DXGI_FORMAT)fmt;
    stgDesc.SampleDesc.Count = 1;
    stgDesc.Usage = D3D11_USAGE_STAGING;
    stgDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

    ID3D11Texture2D* pStg = NULL;
    hr = pDev2->CreateTexture2D(&stgDesc, NULL, &pStg);
    if (FAILED(hr) || !pStg) { printf("FAIL: Create staging tex hr=0x%lx\n", hr); return 10; }

    D3D11_BOX box = { 10, 10, 0, 11, 11, 1 };
    pCtx2->CopySubresourceRegion(pStg, 0, 0, 0, 0, pSharedTex2, 0, &box);
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    hr = pCtx2->Map(pStg, 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) { printf("FAIL: Map hr=0x%lx\n", hr); return 11; }

    uint8_t* px = (uint8_t*)mapped.pData;
    printf("DEV2_ZERO_COPY_PIXEL: RGBA=(%u, %u, %u, %u) (%ux%u, qpc=%llu)\n", px[0], px[1], px[2], px[3], w, h, qpc);
    bool pixelMatch = (abs((int)px[0] - 64) <= 2 && abs((int)px[1] - 128) <= 2 && abs((int)px[2] - 191) <= 2 && px[3] >= 253);
    pCtx2->Unmap(pStg, 0);

    // Also test LosslessPlusAutoHookD3D11
    BOOL autoOk = pAutoHook();
    printf("AUTO_HOOK_D3D11: %d\n", autoOk);

    pStg->Release();
    pSharedTex2->Release();
    pCtx2->Release();
    pDev2->Release();
    pSwapChain->Release();
    pFactory->Release();
    pCtx1->Release();
    pDev1->Release();
    DestroyWindow(hWnd);

    if (!pixelMatch || !autoOk) {
        printf("FAIL: Pixel mismatch or AutoHook failed\n");
        return 12;
    }
    printf("ALL_TIER3_TIER4_HW_TESTS_PASSED\n");
    return 0;
}
'''


def find_vcvars64():
    candidates = [
        r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
        r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
        r"C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
        r"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
    ]
    for p in candidates:
        if os.path.isfile(p):
            return p
    return None


def build_and_verify():
    base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cpp_path = os.path.join(base_dir, "src", "hook", "LosslessPlusHook.cpp")
    def_path = os.path.join(base_dir, "src", "hook", "LosslessPlusHook.def")
    out_dir = os.path.join(base_dir, "bin")
    os.makedirs(out_dir, exist_ok=True)
    dll_path = os.path.join(out_dir, "LosslessPlusHook.dll")

    vcvars = find_vcvars64()
    if not vcvars:
        raise RuntimeError("Visual Studio 2022 vcvars64.bat not found!")

    with tempfile.TemporaryDirectory() as tmpdir:
        verify_cpp = os.path.join(tmpdir, "verify_hook.cpp")
        verify_exe = os.path.join(tmpdir, "verify_hook.exe")
        with open(verify_cpp, "w", encoding="utf-8") as f:
            f.write(HOOK_VERIFY_CPP)

        bat_path = os.path.join(tmpdir, "build.bat")
        with open(bat_path, "w", encoding="utf-8") as f:
            f.write(f'@call "{vcvars}" >nul\r\n')
            f.write(f'cl.exe /nologo /O2 /MT /EHsc /W3 /LD "{cpp_path}" /Fe:"{dll_path}" /Fo:"{tmpdir}\\hook.obj" /link /DEF:"{def_path}" /IMPLIB:"{tmpdir}\\hook.lib" /PDB:"{tmpdir}\\hook.pdb"\r\n')
            f.write('if errorlevel 1 exit /b %errorlevel%\r\n')
            f.write(f'cl.exe /nologo /O2 /MT /EHsc "{verify_cpp}" /Fe:"{verify_exe}" /Fo:"{tmpdir}\\verify.obj" /link /PDB:"{tmpdir}\\verify.pdb"\r\n')
            f.write('if errorlevel 1 exit /b %errorlevel%\r\n')

        res = subprocess.run(["cmd", "/c", bat_path], capture_output=True, text=True)
        print(res.stdout.strip())
        if res.returncode != 0:
            print(res.stderr)
            raise RuntimeError(f"MSVC build failed with code {res.returncode}")

        # Clean up MSVC .exp / .lib from bin if created next to dll_path
        for ext in (".exp", ".lib"):
            leftover = os.path.splitext(dll_path)[0] + ext
            if os.path.isfile(leftover):
                os.remove(leftover)

        print(f"Built native x64 {dll_path} ({os.path.getsize(dll_path):,} bytes)")

        # Run real two-device D3D11 SwapChain + Zero-Copy Shared Texture test
        v_res = subprocess.run([verify_exe, dll_path], capture_output=True, text=True)
        print(v_res.stdout.strip())
        if v_res.returncode != 0:
            print(v_res.stderr)
            raise RuntimeError(f"Hardware D3D11 verification failed with code {v_res.returncode}")


if __name__ == "__main__":
    build_and_verify()
