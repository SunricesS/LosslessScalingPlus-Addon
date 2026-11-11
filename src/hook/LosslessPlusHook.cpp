// LosslessPlusHook.cpp — In-Process DXGI/D3D11 SwapChain, Frame Pacing & Depth/HUD Hook
// Target: Singleplayer Games & Console Emulators (RPCS3, Ryujinx, Yuzu, PCSX2, Cemu, Dolphin, DuckStation)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdint.h>

#pragma pack(push, 1)
struct LosslessPlusSharedState {
    uint32_t magic;                 // 'LSP+' (0x4C53502B)
    uint32_t version;               // 0x00010000
    uint64_t frameCount;            // Monotonic Present() counter
    uint64_t lastPresentQpc;        // Exact QueryPerformanceCounter at Present()
    uint64_t qpcFrequency;          // QueryPerformanceFrequency
    uint32_t targetPid;             // Hooked Game/Emulator Process ID
    uint32_t flags;                 // Bit 0: Hook Active, Bit 1: MicroPacing, Bit 2: DepthHook, Bit 3: HudlessHook
    uint32_t depthBufferDetected;   // 1 if active 3D DSV (Depth Buffer) bound before UI pass
    uint32_t uiPassDetected;        // 1 if 2D HUD/Overlay render pass isolated
    uint64_t targetFrameIntervalQpc;// For fixed-tickrate emulators (30/60 FPS lock)
};
#pragma pack(pop)

static HANDLE g_hMapFile = NULL;
static LosslessPlusSharedState* g_pState = NULL;
static HANDLE g_hFrameEvent = NULL;
static HMODULE g_hRealDxgi = NULL;

typedef HRESULT(WINAPI* PFN_CreateDXGIFactory)(REFIID, void**);
typedef HRESULT(WINAPI* PFN_CreateDXGIFactory1)(REFIID, void**);
typedef HRESULT(WINAPI* PFN_CreateDXGIFactory2)(UINT, REFIID, void**);
typedef HRESULT(STDMETHODCALLTYPE* PFN_Present)(IDXGISwapChain*, UINT, UINT);
typedef void(STDMETHODCALLTYPE* PFN_OMSetRenderTargets)(
    ID3D11DeviceContext*, UINT, ID3D11RenderTargetView* const*, ID3D11DepthStencilView*);

static PFN_Present g_origPresent = NULL;
static PFN_OMSetRenderTargets g_origOMSetRenderTargets = NULL;

extern "C" __declspec(dllexport) BOOL WINAPI LosslessPlusInitShared() {
    if (g_pState != NULL) return TRUE;
    g_hMapFile = CreateFileMappingW(
        INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0,
        sizeof(LosslessPlusSharedState), L"Local\\LosslessPlusSharedState");
    if (!g_hMapFile) return FALSE;

    g_pState = (LosslessPlusSharedState*)MapViewOfFile(
        g_hMapFile, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(LosslessPlusSharedState));
    if (!g_pState) return FALSE;

    LARGE_INTEGER freq, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&now);

    g_pState->magic = 0x4C53502B;
    g_pState->version = 0x00010000;
    g_pState->qpcFrequency = (uint64_t)freq.QuadPart;
    g_pState->lastPresentQpc = (uint64_t)now.QuadPart;
    g_pState->targetPid = GetCurrentProcessId();
    g_pState->flags |= 0x01; // Hook Active

    g_hFrameEvent = CreateEventW(NULL, FALSE, FALSE, L"Local\\LosslessPlusFrameEvent");
    return TRUE;
}

extern "C" __declspec(dllexport) uint64_t WINAPI LosslessPlusNotifyPresent(uint32_t depthDetected, uint32_t uiDetected) {
    if (!g_pState && !LosslessPlusInitShared()) return 0;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    uint64_t qpc = (uint64_t)now.QuadPart;
    g_pState->lastPresentQpc = qpc;
    g_pState->depthBufferDetected = depthDetected;
    g_pState->uiPassDetected = uiDetected;
    uint64_t fc = ++g_pState->frameCount;
    if (g_hFrameEvent) SetEvent(g_hFrameEvent);
    return fc;
}

extern "C" __declspec(dllexport) BOOL WINAPI LosslessPlusGetState(LosslessPlusSharedState* outState) {
    if (!outState) return FALSE;
    if (!g_pState && !LosslessPlusInitShared()) return FALSE;
    *outState = *g_pState;
    return TRUE;
}
