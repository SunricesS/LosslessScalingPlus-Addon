// LosslessPlusHook.cpp — V3.0 In-Process DXGI Factory/SwapChain VTable Hook & Zero-Copy Shared Texture Bridge
// Target: Universal Windows x64 Singleplayer Games & Emulators (Works across NVIDIA, AMD, and Intel D3D11 GPUs)
// Tier 1-2: Shared QPC Timing, Event Signaling, Spin-Wait Micro-Frame-Pacer
// Tier 3:   Automatic IDXGIFactory / IDXGIFactory2 / IDXGISwapChain::Present / Present1 / ResizeBuffers VTable Hooking
// Tier 4:   Double-Buffered D3D11_RESOURCE_MISC_SHARED Cross-Device/Cross-Process Zero-Copy BackBuffer Bridge

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdint.h>
#include <stdio.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

#define LSP_MAGIC 0x4C53502Bu // 'LSP+'
#define LSP_VERSION_V3 0x00030000u

enum LspFlags : uint32_t {
    LSP_FLAG_HOOK_ACTIVE      = 0x01u,
    LSP_FLAG_MICRO_PACING     = 0x02u,
    LSP_FLAG_VTABLE_HOOKED    = 0x04u,
    LSP_FLAG_ZEROCOPY_READY   = 0x08u,
    LSP_FLAG_OPEN_REFLEX      = 0x10u, // In-Process SetMaximumFrameLatency(1) + D3D11_QUERY_EVENT GPU Fence
    LSP_FLAG_UPGRADE_FLIP     = 0x20u, // Upgrade legacy Blt SwapChains to DXGI_SWAP_EFFECT_FLIP_DISCARD
    LSP_FLAG_LATENCY_1_ACTIVE = 0x40u, // Confirmed MaxFrameLatency=1 & GPU Fence active on device
};

#pragma pack(push, 1)
struct LosslessPlusSharedState {
    uint32_t magic;                   // 0x00: 'LSP+' (0x4C53502B)
    uint32_t version;                 // 0x04: 0x00030000
    uint64_t frameCount;              // 0x08: Monotonic Present() counter
    uint64_t lastPresentQpc;          // 0x10: Exact QueryPerformanceCounter at Present()
    uint64_t qpcFrequency;            // 0x18: QueryPerformanceFrequency
    uint32_t targetPid;               // 0x20: Hooked Game/Emulator Process ID
    uint32_t flags;                   // 0x24: LspFlags bitmask
    uint32_t depthBufferDetected;     // 0x28: Reserved / compatibility
    uint32_t uiPassDetected;          // 0x2C: Reserved / compatibility
    uint64_t targetFrameIntervalQpc;  // 0x30: Spin-wait interval in QPC ticks (0 = off)
    uint64_t sharedTextureHandle;     // 0x38: Current frame global DXGI KMT shared HANDLE
    uint64_t sharedTextureHandlePrev; // 0x40: Previous frame global DXGI KMT shared HANDLE
    uint32_t frameWidth;              // 0x48: BackBuffer width
    uint32_t frameHeight;             // 0x4C: BackBuffer height
    uint32_t frameFormat;             // 0x50: Concrete DXGI_FORMAT
    uint32_t swapChainHwnd;           // 0x54: Lower 32 bits of output HWND
};
#pragma pack(pop)

static_assert(sizeof(LosslessPlusSharedState) == 88, "LosslessPlusSharedState must be 88 bytes");

// Shared IPC globals
static HANDLE g_hMapFile = NULL;
static LosslessPlusSharedState* g_pState = NULL;
static HANDLE g_hFrameEvent = NULL;
static CRITICAL_SECTION g_cs;
static volatile LONG g_csInitialized = 0;

// Real system DXGI module and exports
static HMODULE g_hRealDxgi = NULL;
typedef HRESULT(WINAPI* PFN_CreateDXGIFactory)(REFIID, void**);
typedef HRESULT(WINAPI* PFN_CreateDXGIFactory1)(REFIID, void**);
typedef HRESULT(WINAPI* PFN_CreateDXGIFactory2)(UINT, REFIID, void**);
static PFN_CreateDXGIFactory  g_realCreateFactory  = NULL;
static PFN_CreateDXGIFactory1 g_realCreateFactory1 = NULL;
static PFN_CreateDXGIFactory2 g_realCreateFactory2 = NULL;

// Original VTable pointers
typedef HRESULT(STDMETHODCALLTYPE* PFN_CreateSwapChain)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
typedef HRESULT(STDMETHODCALLTYPE* PFN_CreateSwapChainForHwnd)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);
typedef HRESULT(STDMETHODCALLTYPE* PFN_CreateSwapChainForComposition)(IDXGIFactory2*, IUnknown*, const DXGI_SWAP_CHAIN_DESC1*, IDXGIOutput*, IDXGISwapChain1**);
typedef HRESULT(STDMETHODCALLTYPE* PFN_Present)(IDXGISwapChain*, UINT, UINT);
typedef HRESULT(STDMETHODCALLTYPE* PFN_ResizeBuffers)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
typedef HRESULT(STDMETHODCALLTYPE* PFN_Present1)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

static PFN_CreateSwapChain               g_origCreateSwapChain = NULL;
static PFN_CreateSwapChainForHwnd        g_origCreateSwapChainForHwnd = NULL;
static PFN_CreateSwapChainForComposition g_origCreateSwapChainForComposition = NULL;
static PFN_Present                       g_origPresent = NULL;
static PFN_ResizeBuffers                 g_origResizeBuffers = NULL;
static PFN_Present1                      g_origPresent1 = NULL;

// Tier 4 Double-Buffered Shared Texture state & Open-Reflex GPU Fence state
static ID3D11Device*      g_pTrackedDevice = NULL;
static ID3D11Texture2D*   g_pSharedTex[2]  = { NULL, NULL };
static HANDLE             g_hSharedHandle[2] = { NULL, NULL };
static ID3D11Query*       g_pLatencyQuery[2] = { NULL, NULL };
static bool               g_queryIssued[2]   = { false, false };
static uint32_t           g_fenceSlot = 0;
static uint32_t           g_sharedWidth = 0;
static uint32_t           g_sharedHeight = 0;
static DXGI_FORMAT        g_sharedFormat = DXGI_FORMAT_UNKNOWN;
static uint32_t           g_slotIndex = 0;
static volatile LONG      g_inHook = 0;

static void EnsureCsInit() {
    if (InterlockedCompareExchange(&g_csInitialized, 1, 0) == 0) {
        InitializeCriticalSection(&g_cs);
    }
}

static bool PatchVTableEntry(void* pInterface, size_t index, void* pHookFn, void** ppOrigFn) {
    if (!pInterface || !pHookFn) return false;
    void** vtable = *(void***)pInterface;
    if (!vtable) return false;
    if (vtable[index] == pHookFn) return true;

    DWORD oldProt = 0;
    if (!VirtualProtect(&vtable[index], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProt)) {
        return false;
    }
    if (ppOrigFn && *ppOrigFn == NULL) {
        *ppOrigFn = vtable[index];
    }
    vtable[index] = pHookFn;
    VirtualProtect(&vtable[index], sizeof(void*), oldProt, &oldProt);
    return true;
}

static DXGI_FORMAT NormalizeSharedFormat(DXGI_FORMAT fmt) {
    switch (fmt) {
        case DXGI_FORMAT_R8G8B8A8_TYPELESS:   return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_B8G8R8A8_TYPELESS:   return DXGI_FORMAT_B8G8R8A8_UNORM;
        case DXGI_FORMAT_R10G10B10A2_TYPELESS:return DXGI_FORMAT_R10G10B10A2_UNORM;
        case DXGI_FORMAT_R16G16B16A16_TYPELESS:return DXGI_FORMAT_R16G16B16A16_FLOAT;
        default: return fmt;
    }
}

static void ReleaseSharedTexturesLocked() {
    for (int i = 0; i < 2; ++i) {
        if (g_pSharedTex[i]) {
            g_pSharedTex[i]->Release();
            g_pSharedTex[i] = NULL;
        }
        g_hSharedHandle[i] = NULL;
        if (g_pLatencyQuery[i]) {
            g_pLatencyQuery[i]->Release();
            g_pLatencyQuery[i] = NULL;
        }
        g_queryIssued[i] = false;
    }
    g_fenceSlot = 0;
    g_sharedWidth = 0;
    g_sharedHeight = 0;
    g_sharedFormat = DXGI_FORMAT_UNKNOWN;
    if (g_pTrackedDevice) {
        g_pTrackedDevice->Release();
        g_pTrackedDevice = NULL;
    }
    if (g_pState) {
        g_pState->sharedTextureHandle = 0;
        g_pState->sharedTextureHandlePrev = 0;
        g_pState->flags &= ~(LSP_FLAG_ZEROCOPY_READY | LSP_FLAG_LATENCY_1_ACTIVE);
    }
}

extern "C" BOOL WINAPI LosslessPlusInitShared() {
    if (g_pState != NULL) return TRUE;
    EnsureCsInit();
    EnterCriticalSection(&g_cs);
    if (g_pState != NULL) {
        LeaveCriticalSection(&g_cs);
        return TRUE;
    }

    g_hMapFile = CreateFileMappingW(
        INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0,
        sizeof(LosslessPlusSharedState), L"Local\\LosslessPlusSharedState");
    if (!g_hMapFile) {
        LeaveCriticalSection(&g_cs);
        return FALSE;
    }

    bool alreadyExists = (GetLastError() == ERROR_ALREADY_EXISTS);
    g_pState = (LosslessPlusSharedState*)MapViewOfFile(
        g_hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LosslessPlusSharedState));
    if (!g_pState) {
        LeaveCriticalSection(&g_cs);
        return FALSE;
    }

    LARGE_INTEGER freq, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&now);

    if (!alreadyExists || g_pState->magic != LSP_MAGIC) {
        ZeroMemory(g_pState, sizeof(LosslessPlusSharedState));
        g_pState->magic = LSP_MAGIC;
        g_pState->version = LSP_VERSION_V3;
        g_pState->qpcFrequency = (uint64_t)freq.QuadPart;
        g_pState->lastPresentQpc = (uint64_t)now.QuadPart;
        g_pState->flags = LSP_FLAG_HOOK_ACTIVE | LSP_FLAG_OPEN_REFLEX | LSP_FLAG_UPGRADE_FLIP;
    } else {
        g_pState->version = LSP_VERSION_V3;
        g_pState->qpcFrequency = (uint64_t)freq.QuadPart;
        g_pState->flags |= LSP_FLAG_HOOK_ACTIVE;
    }
    g_pState->targetPid = GetCurrentProcessId();

    if (!g_hFrameEvent) {
        g_hFrameEvent = CreateEventW(NULL, FALSE, FALSE, L"Local\\LosslessPlusFrameEvent");
    }
    LeaveCriticalSection(&g_cs);
    return TRUE;
}

extern "C" uint32_t WINAPI LosslessPlusSetLatencyFlags(uint32_t enableOpenReflex, uint32_t enableFlipUpgrade) {
    if (!g_pState && !LosslessPlusInitShared()) return 0;
    uint32_t f = g_pState->flags;
    if (enableOpenReflex) f |= LSP_FLAG_OPEN_REFLEX;
    else f &= ~LSP_FLAG_OPEN_REFLEX;
    if (enableFlipUpgrade) f |= LSP_FLAG_UPGRADE_FLIP;
    else f &= ~LSP_FLAG_UPGRADE_FLIP;
    g_pState->flags = f;
    return f;
}

extern "C" uint64_t WINAPI LosslessPlusSetTargetFps(uint32_t targetFps) {
    if (!g_pState && !LosslessPlusInitShared()) return 0;
    if (targetFps == 0) {
        g_pState->targetFrameIntervalQpc = 0;
        g_pState->flags &= ~LSP_FLAG_MICRO_PACING;
        return 0;
    }
    uint64_t interval = g_pState->qpcFrequency / targetFps;
    g_pState->targetFrameIntervalQpc = interval;
    g_pState->flags |= LSP_FLAG_MICRO_PACING;
    return interval;
}

static void RunPacerAndSignalLockedOrUnlocked() {
    if (!g_pState && !LosslessPlusInitShared()) return;

    LARGE_INTEGER now;
    if ((g_pState->flags & LSP_FLAG_MICRO_PACING) && g_pState->targetFrameIntervalQpc > 0) {
        uint64_t deadline = g_pState->lastPresentQpc + g_pState->targetFrameIntervalQpc;
        QueryPerformanceCounter(&now);
        // Only pace if deadline is reasonably in the future (guard against stall/pause)
        if ((uint64_t)now.QuadPart < deadline && (deadline - (uint64_t)now.QuadPart) <= (g_pState->qpcFrequency / 5)) {
            while ((uint64_t)now.QuadPart < deadline) {
                YieldProcessor();
                QueryPerformanceCounter(&now);
            }
        }
    } else {
        QueryPerformanceCounter(&now);
    }

    g_pState->lastPresentQpc = (uint64_t)now.QuadPart;
    InterlockedIncrement64((volatile LONG64*)&g_pState->frameCount);
    if (g_hFrameEvent) SetEvent(g_hFrameEvent);
}

extern "C" uint64_t WINAPI LosslessPlusNotifyPresent(uint32_t depthDetected, uint32_t uiDetected) {
    if (!g_pState && !LosslessPlusInitShared()) return 0;
    g_pState->depthBufferDetected = depthDetected;
    g_pState->uiPassDetected = uiDetected;
    RunPacerAndSignalLockedOrUnlocked();
    return g_pState->frameCount;
}

extern "C" BOOL WINAPI LosslessPlusGetState(LosslessPlusSharedState* outState) {
    if (!outState) return FALSE;
    if (!g_pState && !LosslessPlusInitShared()) return FALSE;
    *outState = *g_pState;
    return TRUE;
}

// Tier 4 Capture Bridge executed inside hooked Present/Present1
static void CaptureBackBufferZeroCopy(IDXGISwapChain* pSwapChain) {
    if (!pSwapChain) return;
    if (!g_pState && !LosslessPlusInitShared()) return;
    if (InterlockedCompareExchange(&g_inHook, 1, 0) != 0) return;

    ID3D11Texture2D* pBackBuffer = NULL;
    if (SUCCEEDED(pSwapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&pBackBuffer)) && pBackBuffer) {
        D3D11_TEXTURE2D_DESC bbDesc = {};
        pBackBuffer->GetDesc(&bbDesc);

        ID3D11Device* pDevice = NULL;
        pBackBuffer->GetDevice(&pDevice);
        if (pDevice) {
            DXGI_FORMAT targetFmt = NormalizeSharedFormat(bbDesc.Format);

            EnterCriticalSection(&g_cs);
            bool needRecreate = (g_pTrackedDevice != pDevice) ||
                                (g_sharedWidth != bbDesc.Width) ||
                                (g_sharedHeight != bbDesc.Height) ||
                                (g_sharedFormat != targetFmt) ||
                                (g_pSharedTex[0] == NULL || g_pSharedTex[1] == NULL);

            if (needRecreate) {
                ReleaseSharedTexturesLocked();
                D3D11_TEXTURE2D_DESC sd = {};
                sd.Width = bbDesc.Width;
                sd.Height = bbDesc.Height;
                sd.MipLevels = 1;
                sd.ArraySize = 1;
                sd.Format = targetFmt;
                sd.SampleDesc.Count = 1;
                sd.SampleDesc.Quality = 0;
                sd.Usage = D3D11_USAGE_DEFAULT;
                sd.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                sd.CPUAccessFlags = 0;
                sd.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

                bool createdAll = true;
                for (int i = 0; i < 2; ++i) {
                    if (FAILED(pDevice->CreateTexture2D(&sd, NULL, &g_pSharedTex[i])) || !g_pSharedTex[i]) {
                        createdAll = false;
                        break;
                    }
                    IDXGIResource* pRes = NULL;
                    if (SUCCEEDED(g_pSharedTex[i]->QueryInterface(__uuidof(IDXGIResource), (void**)&pRes)) && pRes) {
                        HANDLE hShared = NULL;
                        HRESULT hrSh = pRes->GetSharedHandle(&hShared);
                        pRes->Release();
                        if (FAILED(hrSh) || !hShared) {
                            createdAll = false;
                            break;
                        }
                        g_hSharedHandle[i] = hShared;
                    } else {
                        createdAll = false;
                        break;
                    }
                }

                if (createdAll) {
                    pDevice->AddRef();
                    g_pTrackedDevice = pDevice;
                    g_sharedWidth = bbDesc.Width;
                    g_sharedHeight = bbDesc.Height;
                    g_sharedFormat = targetFmt;

                    // Create 2-slot D3D11_QUERY_EVENT ring for Open-Reflex GPU Completion Fence
                    D3D11_QUERY_DESC qd = {};
                    qd.Query = D3D11_QUERY_EVENT;
                    qd.MiscFlags = 0;
                    for (int i = 0; i < 2; ++i) {
                        pDevice->CreateQuery(&qd, &g_pLatencyQuery[i]);
                        g_queryIssued[i] = false;
                    }

                    // Enforce MaximumFrameLatency = 1 on the Game's DXGI Device (eliminates 3-frame driver queue)
                    if (g_pState && (g_pState->flags & LSP_FLAG_OPEN_REFLEX)) {
                        IDXGIDevice1* pDxgiDev1 = NULL;
                        if (SUCCEEDED(pDevice->QueryInterface(__uuidof(IDXGIDevice1), (void**)&pDxgiDev1)) && pDxgiDev1) {
                            if (SUCCEEDED(pDxgiDev1->SetMaximumFrameLatency(1))) {
                                g_pState->flags |= LSP_FLAG_LATENCY_1_ACTIVE;
                            }
                            pDxgiDev1->Release();
                        }
                    }
                } else {
                    ReleaseSharedTexturesLocked();
                }
            }

            uint32_t currSlot = g_slotIndex & 1u;
            uint32_t prevSlot = (g_slotIndex ^ 1u) & 1u;
            ID3D11Texture2D* dstTex = g_pSharedTex[currSlot];
            HANDLE currHandle = g_hSharedHandle[currSlot];
            HANDLE prevHandle = g_hSharedHandle[prevSlot];
            if (dstTex && currHandle) {
                dstTex->AddRef();
            }
            LeaveCriticalSection(&g_cs);

            if (dstTex && currHandle) {
                ID3D11DeviceContext* pCtx = NULL;
                pDevice->GetImmediateContext(&pCtx);
                if (pCtx) {
                    if (bbDesc.SampleDesc.Count > 1) {
                        pCtx->ResolveSubresource(dstTex, 0, pBackBuffer, 0, targetFmt);
                    } else {
                        pCtx->CopyResource(dstTex, pBackBuffer);
                    }
                    pCtx->Flush();
                    pCtx->Release();
                }

                DXGI_SWAP_CHAIN_DESC scDesc = {};
                if (SUCCEEDED(pSwapChain->GetDesc(&scDesc))) {
                    g_pState->swapChainHwnd = (uint32_t)(uintptr_t)scDesc.OutputWindow;
                }
                g_pState->frameWidth = bbDesc.Width;
                g_pState->frameHeight = bbDesc.Height;
                g_pState->frameFormat = (uint32_t)targetFmt;
                g_pState->sharedTextureHandlePrev = (uint64_t)(uintptr_t)(prevHandle ? prevHandle : currHandle);
                g_pState->sharedTextureHandle = (uint64_t)(uintptr_t)currHandle;
                g_pState->flags |= LSP_FLAG_ZEROCOPY_READY;
                g_slotIndex ^= 1u;

                dstTex->Release();
            }
            pDevice->Release();
        }
        pBackBuffer->Release();
    }

    RunPacerAndSignalLockedOrUnlocked();
    InterlockedExchange(&g_inHook, 0);
}

// Post-Present Open-Reflex GPU Completion Fence:
// Synchronizes the game's CPU input-reading thread with GPU completion of Frame N-1
// so that mouse/controller input for Frame N+1 is sampled right when the GPU is ready.
static void SyncOpenReflexFenceAfterPresent() {
    if (!g_pState || (g_pState->flags & LSP_FLAG_OPEN_REFLEX) == 0) return;

    EnterCriticalSection(&g_cs);
    ID3D11Device* pDev = g_pTrackedDevice;
    uint32_t curr = g_fenceSlot & 1u;
    uint32_t prev = (g_fenceSlot ^ 1u) & 1u;
    ID3D11Query* qCurr = g_pLatencyQuery[curr];
    ID3D11Query* qPrev = g_pLatencyQuery[prev];
    bool waitPrev = g_queryIssued[prev] && (qPrev != NULL);
    if (pDev && qCurr) {
        pDev->AddRef();
        qCurr->AddRef();
        if (waitPrev) qPrev->AddRef();
        g_queryIssued[curr] = true;
        g_fenceSlot ^= 1u;
    } else {
        pDev = NULL;
    }
    LeaveCriticalSection(&g_cs);

    if (!pDev) return;

    ID3D11DeviceContext* pCtx = NULL;
    pDev->GetImmediateContext(&pCtx);
    if (pCtx) {
        pCtx->End(qCurr);
        pCtx->Flush();
        if (waitPrev) {
            LARGE_INTEGER startQpc, nowQpc;
            QueryPerformanceCounter(&startQpc);
            uint64_t maxWaitTicks = (g_pState->qpcFrequency > 0) ? (g_pState->qpcFrequency / 20) : 500000; // 50ms safety cap
            BOOL done = FALSE;
            while (pCtx->GetData(qPrev, &done, sizeof(BOOL), 0) == S_FALSE) {
                YieldProcessor();
                QueryPerformanceCounter(&nowQpc);
                if ((uint64_t)(nowQpc.QuadPart - startQpc.QuadPart) >= maxWaitTicks) break;
            }
        }
        pCtx->Release();
    }
    if (waitPrev) qPrev->Release();
    qCurr->Release();
    pDev->Release();
}

static HRESULT STDMETHODCALLTYPE Hooked_Present(IDXGISwapChain* pSwapChain, UINT SyncInterval, UINT Flags) {
    // Skip DXGI_PRESENT_TEST (0x00000001)
    if ((Flags & DXGI_PRESENT_TEST) == 0) {
        CaptureBackBufferZeroCopy(pSwapChain);
    }
    HRESULT hr = g_origPresent(pSwapChain, SyncInterval, Flags);
    if ((Flags & DXGI_PRESENT_TEST) == 0 && SUCCEEDED(hr)) {
        SyncOpenReflexFenceAfterPresent();
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hooked_Present1(
    IDXGISwapChain1* pSwapChain, UINT SyncInterval, UINT PresentFlags, const DXGI_PRESENT_PARAMETERS* pPresentParameters)
{
    if ((PresentFlags & DXGI_PRESENT_TEST) == 0) {
        CaptureBackBufferZeroCopy(pSwapChain);
    }
    HRESULT hr = g_origPresent1(pSwapChain, SyncInterval, PresentFlags, pPresentParameters);
    if ((PresentFlags & DXGI_PRESENT_TEST) == 0 && SUCCEEDED(hr)) {
        SyncOpenReflexFenceAfterPresent();
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hooked_ResizeBuffers(
    IDXGISwapChain* pSwapChain, UINT BufferCount, UINT Width, UINT Height, DXGI_FORMAT NewFormat, UINT SwapChainFlags)
{
    EnsureCsInit();
    EnterCriticalSection(&g_cs);
    ReleaseSharedTexturesLocked();
    LeaveCriticalSection(&g_cs);
    return g_origResizeBuffers(pSwapChain, BufferCount, Width, Height, NewFormat, SwapChainFlags);
}

extern "C" BOOL WINAPI LosslessPlusInstallSwapChainHook(IDXGISwapChain* pSwapChain) {
    if (!pSwapChain) return FALSE;
    if (!g_pState && !LosslessPlusInitShared()) return FALSE;

    EnsureCsInit();
    EnterCriticalSection(&g_cs);
    bool ok = true;
    ok &= PatchVTableEntry(pSwapChain, 8,  (void*)&Hooked_Present,       (void**)&g_origPresent);
    ok &= PatchVTableEntry(pSwapChain, 13, (void*)&Hooked_ResizeBuffers, (void**)&g_origResizeBuffers);

    IDXGISwapChain1* pSc1 = NULL;
    if (SUCCEEDED(pSwapChain->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&pSc1)) && pSc1) {
        PatchVTableEntry(pSc1, 22, (void*)&Hooked_Present1, (void**)&g_origPresent1);
        pSc1->Release();
    }
    if (ok && g_pState) {
        g_pState->flags |= LSP_FLAG_VTABLE_HOOKED;
    }
    LeaveCriticalSection(&g_cs);
    return ok ? TRUE : FALSE;
}

static HRESULT STDMETHODCALLTYPE Hooked_CreateSwapChain(
    IDXGIFactory* pFactory, IUnknown* pDevice, DXGI_SWAP_CHAIN_DESC* pDesc, IDXGISwapChain** ppSwapChain)
{
    HRESULT hr = E_FAIL;
    // Automatic Flip-Model Upgrade (bypasses 1-frame DWM composition buffer in windowed/borderless games)
    if (pDesc && g_pState && (g_pState->flags & LSP_FLAG_UPGRADE_FLIP) &&
        pDesc->Windowed && pDesc->SampleDesc.Count == 1 &&
        (pDesc->SwapEffect == DXGI_SWAP_EFFECT_DISCARD || pDesc->SwapEffect == DXGI_SWAP_EFFECT_SEQUENTIAL) &&
        (pDesc->BufferDesc.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
         pDesc->BufferDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
         pDesc->BufferDesc.Format == DXGI_FORMAT_R10G10B10A2_UNORM ||
         pDesc->BufferDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT))
    {
        DXGI_SWAP_CHAIN_DESC flipDesc = *pDesc;
        flipDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        if (flipDesc.BufferCount < 2) flipDesc.BufferCount = 2;
        hr = g_origCreateSwapChain(pFactory, pDevice, &flipDesc, ppSwapChain);
    }
    if (FAILED(hr)) {
        hr = g_origCreateSwapChain(pFactory, pDevice, pDesc, ppSwapChain);
    }
    if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain) {
        LosslessPlusInstallSwapChainHook(*ppSwapChain);
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hooked_CreateSwapChainForHwnd(
    IDXGIFactory2* pFactory, IUnknown* pDevice, HWND hWnd, const DXGI_SWAP_CHAIN_DESC1* pDesc,
    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* pFullscreenDesc, IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain)
{
    HRESULT hr = g_origCreateSwapChainForHwnd(pFactory, pDevice, hWnd, pDesc, pFullscreenDesc, pRestrictToOutput, ppSwapChain);
    if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain) {
        LosslessPlusInstallSwapChainHook(*ppSwapChain);
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE Hooked_CreateSwapChainForComposition(
    IDXGIFactory2* pFactory, IUnknown* pDevice, const DXGI_SWAP_CHAIN_DESC1* pDesc,
    IDXGIOutput* pRestrictToOutput, IDXGISwapChain1** ppSwapChain)
{
    HRESULT hr = g_origCreateSwapChainForComposition(pFactory, pDevice, pDesc, pRestrictToOutput, ppSwapChain);
    if (SUCCEEDED(hr) && ppSwapChain && *ppSwapChain) {
        LosslessPlusInstallSwapChainHook(*ppSwapChain);
    }
    return hr;
}

static void HookFactoryInstance(void* pUnknownFactory) {
    if (!pUnknownFactory) return;
    IUnknown* pUnk = (IUnknown*)pUnknownFactory;

    EnsureCsInit();
    EnterCriticalSection(&g_cs);
    IDXGIFactory* pFactory = NULL;
    if (SUCCEEDED(pUnk->QueryInterface(__uuidof(IDXGIFactory), (void**)&pFactory)) && pFactory) {
        PatchVTableEntry(pFactory, 10, (void*)&Hooked_CreateSwapChain, (void**)&g_origCreateSwapChain);
        pFactory->Release();
    }

    IDXGIFactory2* pFactory2 = NULL;
    if (SUCCEEDED(pUnk->QueryInterface(__uuidof(IDXGIFactory2), (void**)&pFactory2)) && pFactory2) {
        PatchVTableEntry(pFactory2, 15, (void*)&Hooked_CreateSwapChainForHwnd, (void**)&g_origCreateSwapChainForHwnd);
        PatchVTableEntry(pFactory2, 24, (void*)&Hooked_CreateSwapChainForComposition, (void**)&g_origCreateSwapChainForComposition);
        pFactory2->Release();
    }
    if (g_pState) {
        g_pState->flags |= LSP_FLAG_VTABLE_HOOKED;
    }
    LeaveCriticalSection(&g_cs);
}

static bool EnsureRealDxgiLoaded() {
    if (g_hRealDxgi) return true;
    EnsureCsInit();
    EnterCriticalSection(&g_cs);
    if (!g_hRealDxgi) {
        WCHAR sysDir[MAX_PATH] = {};
        UINT len = GetSystemDirectoryW(sysDir, MAX_PATH);
        if (len > 0 && len < MAX_PATH - 16) {
            lstrcatW(sysDir, L"\\dxgi.dll");
            g_hRealDxgi = LoadLibraryW(sysDir);
            if (g_hRealDxgi) {
                g_realCreateFactory  = (PFN_CreateDXGIFactory)GetProcAddress(g_hRealDxgi, "CreateDXGIFactory");
                g_realCreateFactory1 = (PFN_CreateDXGIFactory1)GetProcAddress(g_hRealDxgi, "CreateDXGIFactory1");
                g_realCreateFactory2 = (PFN_CreateDXGIFactory2)GetProcAddress(g_hRealDxgi, "CreateDXGIFactory2");
            }
        }
    }
    LeaveCriticalSection(&g_cs);
    return g_hRealDxgi != NULL;
}

extern "C" HRESULT WINAPI CreateDXGIFactory(REFIID riid, void** ppFactory) {
    LosslessPlusInitShared();
    if (!EnsureRealDxgiLoaded() || !g_realCreateFactory) return E_FAIL;
    HRESULT hr = g_realCreateFactory(riid, ppFactory);
    if (SUCCEEDED(hr) && ppFactory && *ppFactory) {
        HookFactoryInstance(*ppFactory);
    }
    return hr;
}

extern "C" HRESULT WINAPI CreateDXGIFactory1(REFIID riid, void** ppFactory) {
    LosslessPlusInitShared();
    if (!EnsureRealDxgiLoaded() || !g_realCreateFactory1) return E_FAIL;
    HRESULT hr = g_realCreateFactory1(riid, ppFactory);
    if (SUCCEEDED(hr) && ppFactory && *ppFactory) {
        HookFactoryInstance(*ppFactory);
    }
    return hr;
}

extern "C" HRESULT WINAPI CreateDXGIFactory2(UINT Flags, REFIID riid, void** ppFactory) {
    LosslessPlusInitShared();
    if (!EnsureRealDxgiLoaded() || !g_realCreateFactory2) return E_FAIL;
    HRESULT hr = g_realCreateFactory2(Flags, riid, ppFactory);
    if (SUCCEEDED(hr) && ppFactory && *ppFactory) {
        HookFactoryInstance(*ppFactory);
    }
    return hr;
}

// Universal D3D11 SwapChain Auto-Hooker (hooks the process-wide DXGI SwapChain vtable directly,
// even when d3d11.dll bypasses proxy dxgi.dll via KnownDLLs)
extern "C" BOOL WINAPI LosslessPlusAutoHookD3D11() {
    if (!LosslessPlusInitShared()) return FALSE;

    WNDCLASSEXW wc = { sizeof(WNDCLASSEXW), CS_CLASSDC, DefWindowProcW, 0, 0, GetModuleHandleW(NULL), NULL, NULL, NULL, NULL, L"LSP_DummyWnd", NULL };
    RegisterClassExW(&wc);
    HWND hWnd = CreateWindowExW(0, L"LSP_DummyWnd", L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, NULL, NULL, wc.hInstance, NULL);
    if (!hWnd) return FALSE;

    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 1;
    sd.BufferDesc.Width = 64;
    sd.BufferDesc.Height = 64;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hWnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;

    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
    IDXGISwapChain* pSc = NULL;
    ID3D11Device* pDev = NULL;
    ID3D11DeviceContext* pCtx = NULL;

    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, 0, &fl, 1,
        D3D11_SDK_VERSION, &sd, &pSc, &pDev, NULL, &pCtx);
    if (FAILED(hr)) {
        hr = D3D11CreateDeviceAndSwapChain(
            NULL, D3D_DRIVER_TYPE_WARP, NULL, 0, &fl, 1,
            D3D11_SDK_VERSION, &sd, &pSc, &pDev, NULL, &pCtx);
    }

    BOOL hooked = FALSE;
    if (SUCCEEDED(hr) && pSc) {
        hooked = LosslessPlusInstallSwapChainHook(pSc);
        IDXGIDevice* pDxgiDev = NULL;
        if (pDev && SUCCEEDED(pDev->QueryInterface(__uuidof(IDXGIDevice), (void**)&pDxgiDev)) && pDxgiDev) {
            IDXGIAdapter* pAdapter = NULL;
            if (SUCCEEDED(pDxgiDev->GetAdapter(&pAdapter)) && pAdapter) {
                IDXGIFactory* pFac = NULL;
                if (SUCCEEDED(pAdapter->GetParent(__uuidof(IDXGIFactory), (void**)&pFac)) && pFac) {
                    HookFactoryInstance(pFac);
                    pFac->Release();
                }
                pAdapter->Release();
            }
            pDxgiDev->Release();
        }
    }

    if (pCtx) pCtx->Release();
    if (pDev) pDev->Release();
    if (pSc) pSc->Release();
    DestroyWindow(hWnd);
    UnregisterClassW(L"LSP_DummyWnd", wc.hInstance);
    return hooked;
}

// Tier 4 Consumer API: Opens the latest captured game BackBuffer on ANY external ID3D11Device (Zero-Copy VRAM)
extern "C" HRESULT WINAPI LosslessPlusOpenSharedFrame(
    ID3D11Device* pConsumerDevice,
    ID3D11Texture2D** ppOutTexture,
    uint32_t* pWidth,
    uint32_t* pHeight,
    uint32_t* pFormat,
    uint64_t* pQpcTimestamp)
{
    if (!pConsumerDevice || !ppOutTexture) return E_INVALIDARG;
    *ppOutTexture = NULL;
    if (!g_pState && !LosslessPlusInitShared()) return E_FAIL;

    HANDLE hShared = (HANDLE)(uintptr_t)g_pState->sharedTextureHandle;
    if (!hShared) return E_PENDING;

    if (pWidth)  *pWidth  = g_pState->frameWidth;
    if (pHeight) *pHeight = g_pState->frameHeight;
    if (pFormat) *pFormat = g_pState->frameFormat;
    if (pQpcTimestamp) *pQpcTimestamp = g_pState->lastPresentQpc;

    return pConsumerDevice->OpenSharedResource(hShared, __uuidof(ID3D11Texture2D), (void**)ppOutTexture);
}

// Generic x64 DXGI Proxy Forwarders for remaining System32\dxgi.dll exports
// Ensures zero "Entry Point Not Found" errors across all singleplayer games & emulators when used as dxgi.dll
typedef uint64_t(WINAPI* PFN_GenericDxgi6)(uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t);

static uint64_t ForwardDxgiExport(const char* name, uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) {
    if (!EnsureRealDxgiLoaded()) return (uint64_t)E_FAIL;
    PFN_GenericDxgi6 fn = (PFN_GenericDxgi6)GetProcAddress(g_hRealDxgi, name);
    if (!fn) return (uint64_t)E_NOTIMPL;
    return fn(a1, a2, a3, a4, a5, a6);
}

#define DEFINE_DXGI_FWD(fnName) \
    extern "C" uint64_t WINAPI LSP_Proxy_##fnName(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { \
        return ForwardDxgiExport(#fnName, a1, a2, a3, a4, a5, a6); \
    }

// Exported via LosslessPlusHook.def mapping or matching C names:
extern "C" {
uint64_t WINAPI ApplyCompatResolutionQuirking(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("ApplyCompatResolutionQuirking", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI CompatString(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("CompatString", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI CompatValue(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("CompatValue", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGID3D10CreateDevice(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGID3D10CreateDevice", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGID3D10CreateLayeredDevice(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGID3D10CreateLayeredDevice", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGID3D10GetLayeredDeviceSize(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGID3D10GetLayeredDeviceSize", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGID3D10RegisterLayers(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGID3D10RegisterLayers", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGIDeclareAdapterRemovalSupport(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGIDeclareAdapterRemovalSupport", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGIDisableVBlankVirtualization(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGIDisableVBlankVirtualization", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGIDumpJournal(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGIDumpJournal", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGIGetDebugInterface1(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGIGetDebugInterface1", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI DXGIReportAdapterConfiguration(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("DXGIReportAdapterConfiguration", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI PIXBeginCapture(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("PIXBeginCapture", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI PIXEndCapture(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("PIXEndCapture", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI PIXGetCaptureState(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("PIXGetCaptureState", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI SetAppCompatStringPointer(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("SetAppCompatStringPointer", a1, a2, a3, a4, a5, a6); }
uint64_t WINAPI UpdateHMDEmulationStatus(uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, uint64_t a5, uint64_t a6) { return ForwardDxgiExport("UpdateHMDEmulationStatus", a1, a2, a3, a4, a5, a6); }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            DisableThreadLibraryCalls(hModule);
            EnsureCsInit();
            LosslessPlusInitShared();
            break;
        case DLL_PROCESS_DETACH:
            if (!lpReserved) {
                EnterCriticalSection(&g_cs);
                ReleaseSharedTexturesLocked();
                LeaveCriticalSection(&g_cs);
                if (g_pState) { UnmapViewOfFile(g_pState); g_pState = NULL; }
                if (g_hMapFile) { CloseHandle(g_hMapFile); g_hMapFile = NULL; }
                if (g_hFrameEvent) { CloseHandle(g_hFrameEvent); g_hFrameEvent = NULL; }
                DeleteCriticalSection(&g_cs);
            }
            break;
    }
    return TRUE;
}
