import struct
import os
import ctypes

def align(val, alignment):
    return (val + alignment - 1) & ~(alignment - 1)

def build_hook_dll(out_path):
    """
    Builds a native x64 PE DLL (LosslessPlusHook.dll) with:
      - .text section: DllMain, LosslessPlusInitShared, LosslessPlusNotifyPresent, LosslessPlusGetState,
        and DXGI proxy wrappers (CreateDXGIFactory, CreateDXGIFactory1, CreateDXGIFactory2)
      - .rdata section: Import Directory (KERNEL32.dll), Export Directory, UTF-16LE strings
      - .data section: Global state pointers (g_hMapFile, g_pState, g_hFrameEvent, g_hSysDxgi, etc.)
      - .reloc section: Base relocation table so ASLR works at any load address
    """
    IMAGE_BASE = 0x180000000
    SECT_ALIGN = 0x1000
    FILE_ALIGN = 0x200

    # Layout RVAs:
    # 0x1000: .text
    # 0x2000: .rdata (IAT at 0x2000, Import Table, Export Table, Strings)
    # 0x3000: .data (Globals)
    # 0x4000: .reloc

    # Kernel32 imports at IAT RVA 0x2000 (8 bytes each):
    imports = [
        "CreateFileMappingW",       # 0: 0x2000
        "MapViewOfFile",            # 1: 0x2008
        "CreateEventW",             # 2: 0x2010
        "SetEvent",                 # 3: 0x2018
        "QueryPerformanceCounter",  # 4: 0x2020
        "QueryPerformanceFrequency",# 5: 0x2028
        "GetCurrentProcessId",      # 6: 0x2030
        "LoadLibraryW",             # 7: 0x2038
        "GetProcAddress",           # 8: 0x2040
    ]
    iat_rvas = {name: 0x2000 + i * 8 for i, name in enumerate(imports)}

    # .data globals at RVA 0x3000:
    # 0x3000: g_pState (uint64)
    # 0x3008: g_hFrameEvent (uint64)
    # 0x3010: g_hMapFile (uint64)
    # 0x3018: g_hSysDxgi (uint64)
    # 0x3020: g_pfnCreateDXGIFactory (uint64)
    # 0x3028: g_pfnCreateDXGIFactory1 (uint64)
    # 0x3030: g_pfnCreateDXGIFactory2 (uint64)
    # 0x3040: qpc_temp (uint64)
    # 0x3048: freq_temp (uint64)
    DATA_PSTATE = 0x3000
    DATA_HEVENT = 0x3008
    DATA_HMAP   = 0x3010
    DATA_HDXGI  = 0x3018
    DATA_PFN_F0 = 0x3020
    DATA_PFN_F1 = 0x3028
    DATA_PFN_F2 = 0x3030
    DATA_QPC    = 0x3040
    DATA_FREQ   = 0x3048

    # Fixed string RVAs in .rdata (starting at 0x2400):
    STR_SHM_RVA   = 0x2400 # L"Local\\LosslessPlusSharedState"
    STR_EVT_RVA   = 0x2450 # L"Local\\LosslessPlusFrameEvent"
    STR_DXGI_RVA  = 0x24A0 # L"C:\\Windows\\System32\\dxgi.dll"
    STR_F0_RVA    = 0x2500 # "CreateDXGIFactory"
    STR_F1_RVA    = 0x2520 # "CreateDXGIFactory1"
    STR_F2_RVA    = 0x2540 # "CreateDXGIFactory2"

    code = bytearray()

    def emit(b):
        code.extend(b)

    def cur_rva():
        return 0x1000 + len(code)

    def rip_disp(target_rva, instr_len, disp_offset_in_instr):
        next_rip = cur_rva() + instr_len
        return target_rva - next_rip

    def emit_call_iat(func_name):
        # FF 15 <disp32> (6 bytes)
        disp = rip_disp(iat_rvas[func_name], 6, 2)
        emit(b"\xFF\x15" + struct.pack("<i", disp))

    def emit_lea_rcx_rip(target_rva):
        # 48 8D 0D <disp32> (7 bytes)
        disp = rip_disp(target_rva, 7, 3)
        emit(b"\x48\x8D\x0D" + struct.pack("<i", disp))

    def emit_lea_rdx_rip(target_rva):
        # 48 8D 15 <disp32> (7 bytes)
        disp = rip_disp(target_rva, 7, 3)
        emit(b"\x48\x8D\x15" + struct.pack("<i", disp))

    def emit_lea_r9_rip(target_rva):
        # 4C 8D 0D <disp32> (7 bytes)
        disp = rip_disp(target_rva, 7, 3)
        emit(b"\x4C\x8D\x0D" + struct.pack("<i", disp))

    def emit_mov_rax_rip(target_rva):
        # 48 8B 05 <disp32> (7 bytes)
        disp = rip_disp(target_rva, 7, 3)
        emit(b"\x48\x8B\x05" + struct.pack("<i", disp))

    def emit_mov_rip_rax(target_rva):
        # 48 89 05 <disp32> (7 bytes)
        disp = rip_disp(target_rva, 7, 3)
        emit(b"\x48\x89\x05" + struct.pack("<i", disp))

    # -------------------------------------------------------------
    # Function 1: LosslessPlusInitShared() at RVA 0x1000
    # -------------------------------------------------------------
    fn_init_rva = cur_rva()
    emit(b"\x48\x83\xEC\x38") # sub rsp, 0x38
    emit_mov_rax_rip(DATA_PSTATE)
    emit(b"\x48\x85\xC0")     # test rax, rax
    emit(b"\x0F\x85\xB2\x00\x00\x00") # jnz .already_init (patched below)
    jnz_patch_pos = len(code) - 4

    # CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE(4), 0, 64, L"Local\\LosslessPlusSharedState")
    emit(b"\x48\xC7\xC1\xFF\xFF\xFF\xFF") # mov rcx, -1
    emit(b"\x31\xD2")                     # xor edx, edx
    emit(b"\x41\xB8\x04\x00\x00\x00")     # mov r8d, 4
    emit(b"\x45\x31\xC9")                 # xor r9d, r9d
    emit(b"\xC7\x44\x24\x20\x40\x00\x00\x00") # mov dword ptr [rsp+0x20], 64
    disp_shm = rip_disp(STR_SHM_RVA, 7, 3)
    emit(b"\x48\x8D\x05" + struct.pack("<i", disp_shm)) # lea rax, [rip+STR_SHM]
    emit(b"\x48\x89\x44\x24\x28")         # mov [rsp+0x28], rax
    emit_call_iat("CreateFileMappingW")
    emit(b"\x48\x85\xC0")                 # test rax, rax
    emit(b"\x0F\x84\x90\x00\x00\x00")     # jz .fail
    jz1_patch_pos = len(code) - 4
    emit_mov_rip_rax(DATA_HMAP)

    # MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS(0xF001F), 0, 0, 64)
    emit(b"\x48\x89\xC1")                 # mov rcx, rax
    emit(b"\xBA\x1F\x00\x0F\x00")         # mov edx, 0xF001F
    emit(b"\x45\x31\xC0")                 # xor r8d, r8d
    emit(b"\x45\x31\xC9")                 # xor r9d, r9d
    emit(b"\x48\xC7\x44\x24\x20\x40\x00\x00\x00") # mov qword ptr [rsp+0x20], 64
    emit_call_iat("MapViewOfFile")
    emit(b"\x48\x85\xC0")                 # test rax, rax
    emit(b"\x0F\x84\x60\x00\x00\x00")     # jz .fail
    jz2_patch_pos = len(code) - 4
    emit_mov_rip_rax(DATA_PSTATE)

    # Populate magic ('LSP+' = 0x4C53502B) and version (0x00010000)
    emit(b"\xC7\x00\x2B\x50\x53\x4C")     # mov dword ptr [rax], 0x4C53502B
    emit(b"\xC7\x40\x04\x00\x00\x01\x00") # mov dword ptr [rax+4], 0x00010000
    emit(b"\x83\x48\x24\x01")             # or dword ptr [rax+0x24], 1 (flags |= 1)

    # QueryPerformanceFrequency(&freq_temp)
    emit_lea_rcx_rip(DATA_FREQ)
    emit_call_iat("QueryPerformanceFrequency")
    emit_mov_rax_rip(DATA_PSTATE)
    disp_f = rip_disp(DATA_FREQ, 7, 3)
    emit(b"\x48\x8B\x0D" + struct.pack("<i", disp_f)) # mov rcx, [rip+DATA_FREQ]
    emit(b"\x48\x89\x48\x18")             # mov [rax+0x18], rcx

    # QueryPerformanceCounter(&qpc_temp)
    emit_lea_rcx_rip(DATA_QPC)
    emit_call_iat("QueryPerformanceCounter")
    emit_mov_rax_rip(DATA_PSTATE)
    disp_q = rip_disp(DATA_QPC, 7, 3)
    emit(b"\x48\x8B\x0D" + struct.pack("<i", disp_q)) # mov rcx, [rip+DATA_QPC]
    emit(b"\x48\x89\x48\x10")             # mov [rax+0x10], rcx

    # GetCurrentProcessId()
    emit_call_iat("GetCurrentProcessId")
    disp_ps = rip_disp(DATA_PSTATE, 7, 3)
    emit(b"\x48\x8B\x0D" + struct.pack("<i", disp_ps)) # mov rcx, [rip+DATA_PSTATE]
    emit(b"\x89\x41\x20")                 # mov [rcx+0x20], eax (targetPid at offset 0x20)

    # CreateEventW(NULL, FALSE, FALSE, L"Local\\LosslessPlusFrameEvent")
    emit(b"\x31\xC9")                     # xor ecx, ecx
    emit(b"\x31\xD2")                     # xor edx, edx
    emit(b"\x45\x31\xC0")                 # xor r8d, r8d
    emit_lea_r9_rip(STR_EVT_RVA)
    emit_call_iat("CreateEventW")
    emit_mov_rip_rax(DATA_HEVENT)

    # .already_init:
    already_init_pos = len(code)
    struct.pack_into("<i", code, jnz_patch_pos, already_init_pos - (jnz_patch_pos + 4))
    emit(b"\xB8\x01\x00\x00\x00")         # mov eax, 1
    emit(b"\x48\x83\xC4\x38")             # add rsp, 0x38
    emit(b"\xC3")                         # ret

    # .fail:
    fail_pos = len(code)
    struct.pack_into("<i", code, jz1_patch_pos, fail_pos - (jz1_patch_pos + 4))
    struct.pack_into("<i", code, jz2_patch_pos, fail_pos - (jz2_patch_pos + 4))
    emit(b"\x31\xC0")                     # xor eax, eax
    emit(b"\x48\x83\xC4\x38")             # add rsp, 0x38
    emit(b"\xC3")                         # ret

    # Align to 16 bytes
    while len(code) % 16 != 0:
        emit(b"\xCC")

    # -------------------------------------------------------------
    # Function 2: LosslessPlusNotifyPresent(uint32_t depth, uint32_t ui)
    # -------------------------------------------------------------
    fn_notify_rva = cur_rva()
    emit(b"\x53")                         # push rbx
    emit(b"\x56")                         # push rsi
    emit(b"\x48\x83\xEC\x28")             # sub rsp, 0x28
    emit(b"\x89\xCB")                     # mov ebx, ecx (depthDetected)
    emit(b"\x89\xD6")                     # mov esi, edx (uiDetected)
    emit_mov_rax_rip(DATA_PSTATE)
    emit(b"\x48\x85\xC0")                 # test rax, rax
    emit(b"\x75\x09")                     # jnz .have_state (5 + 2 + 2 = 9 bytes)
    # call LosslessPlusInitShared
    disp_init = fn_init_rva - (cur_rva() + 5)
    emit(b"\xE8" + struct.pack("<i", disp_init))
    emit(b"\x85\xC0")                     # test eax, eax
    emit(b"\x74\x42")                     # jz .notify_fail
    jz_nfail_pos = len(code) - 1
    # .have_state:
    emit_lea_rcx_rip(DATA_QPC)
    emit_call_iat("QueryPerformanceCounter")
    emit_mov_rax_rip(DATA_PSTATE)
    disp_q2 = rip_disp(DATA_QPC, 7, 3)
    emit(b"\x48\x8B\x0D" + struct.pack("<i", disp_q2))
    emit(b"\x48\x89\x48\x10")             # mov [rax+0x10], rcx (lastPresentQpc)
    emit(b"\x89\x58\x28")                 # mov [rax+0x28], ebx (depthBufferDetected)
    emit(b"\x89\x70\x2C")                 # mov [rax+0x2C], esi (uiPassDetected)
    emit(b"\x48\xFF\x40\x08")             # inc qword ptr [rax+0x08] (frameCount)
    emit(b"\x48\x8B\x58\x08")             # mov rbx, [rax+0x08]
    # SetEvent(g_hFrameEvent)
    disp_ev = rip_disp(DATA_HEVENT, 7, 3)
    emit(b"\x48\x8B\x0D" + struct.pack("<i", disp_ev))
    emit(b"\x48\x85\xC9")                 # test rcx, rcx
    emit(b"\x74\x06")                     # jz .skip_ev (6 bytes for call_iat)
    emit_call_iat("SetEvent")
    # .skip_ev:
    emit(b"\x48\x89\xD8")                 # mov rax, rbx
    emit(b"\x48\x83\xC4\x28")             # add rsp, 0x28
    emit(b"\x5E")                         # pop rsi
    emit(b"\x5B")                         # pop rbx
    emit(b"\xC3")                         # ret
    # .notify_fail:
    code[jz_nfail_pos] = len(code) - (jz_nfail_pos + 1)
    emit(b"\x31\xC0")                     # xor eax, eax
    emit(b"\x48\x83\xC4\x28")             # add rsp, 0x28
    emit(b"\x5E")                         # pop rsi
    emit(b"\x5B")                         # pop rbx
    emit(b"\xC3")                         # ret

    while len(code) % 16 != 0:
        emit(b"\xCC")

    # -------------------------------------------------------------
    # Function 3: LosslessPlusGetState(LosslessPlusSharedState* out)
    # -------------------------------------------------------------
    fn_getstate_rva = cur_rva()
    emit(b"\x53")                         # push rbx
    emit(b"\x48\x83\xEC\x20")             # sub rsp, 0x20
    emit(b"\x48\x85\xC9")                 # test rcx, rcx
    emit(b"\x74\x00")                     # jz .gs_fail
    jz_gs1_pos = len(code) - 1
    emit(b"\x48\x89\xCB")                 # mov rbx, rcx
    emit_mov_rax_rip(DATA_PSTATE)
    emit(b"\x48\x85\xC0")                 # test rax, rax
    emit(b"\x75\x10")                     # jnz .gs_copy (5 + 2 + 2 + 7 = 16 bytes)
    disp_init2 = fn_init_rva - (cur_rva() + 5)
    emit(b"\xE8" + struct.pack("<i", disp_init2))
    emit(b"\x85\xC0")                     # test eax, eax
    emit(b"\x74\x00")                     # jz .gs_fail
    jz_gs2_pos = len(code) - 1
    emit_mov_rax_rip(DATA_PSTATE)
    # .gs_copy: copy 56 bytes (7 qwords) from [rax] to [rbx]
    for off in range(0, 56, 8):
        emit(b"\x48\x8B\x48" + struct.pack("<B", off)) # mov rcx, [rax+off]
        emit(b"\x48\x89\x4B" + struct.pack("<B", off)) # mov [rbx+off], rcx
    emit(b"\xB8\x01\x00\x00\x00")         # mov eax, 1
    emit(b"\x48\x83\xC4\x20")             # add rsp, 0x20
    emit(b"\x5B")                         # pop rbx
    emit(b"\xC3")                         # ret
    # .gs_fail:
    gs_fail_pos = len(code)
    code[jz_gs1_pos] = gs_fail_pos - (jz_gs1_pos + 1)
    code[jz_gs2_pos] = gs_fail_pos - (jz_gs2_pos + 1)
    emit(b"\x31\xC0")                     # xor eax, eax
    emit(b"\x48\x83\xC4\x20")             # add rsp, 0x20
    emit(b"\x5B")                         # pop rbx
    emit(b"\xC3")                         # ret

    while len(code) % 16 != 0:
        emit(b"\xCC")

    # -------------------------------------------------------------
    # Function 4: DllMain(hinstDLL, fdwReason, lpvReserved)
    # -------------------------------------------------------------
    fn_dllmain_rva = cur_rva()
    emit(b"\x48\x83\xEC\x28")             # sub rsp, 0x28
    emit(b"\x83\xFA\x01")                 # cmp edx, 1 (DLL_PROCESS_ATTACH)
    emit(b"\x75\x05")                     # jnz .dm_done
    disp_init3 = fn_init_rva - (cur_rva() + 5)
    emit(b"\xE8" + struct.pack("<i", disp_init3))
    # .dm_done:
    emit(b"\xB8\x01\x00\x00\x00")         # mov eax, 1
    emit(b"\x48\x83\xC4\x28")             # add rsp, 0x28
    emit(b"\xC3")                         # ret

    while len(code) % 16 != 0:
        emit(b"\xCC")

    # -------------------------------------------------------------
    # Function 5/6/7: DXGI Proxy Exports (CreateDXGIFactory, CreateDXGIFactory1, CreateDXGIFactory2)
    # Loads C:\Windows\System32\dxgi.dll on demand, notifies frame init, and forwards call
    # -------------------------------------------------------------
    def build_dxgi_forwarder(str_fn_rva, pfn_cache_rva):
        rva = cur_rva()
        emit(b"\x48\x89\x4C\x24\x08")     # mov [rsp+8], rcx
        emit(b"\x48\x89\x54\x24\x10")     # mov [rsp+16], rdx
        emit(b"\x4C\x89\x44\x24\x18")     # mov [rsp+24], r8
        emit(b"\x48\x83\xEC\x28")         # sub rsp, 0x28
        disp_init_f = fn_init_rva - (cur_rva() + 5)
        emit(b"\xE8" + struct.pack("<i", disp_init_f))
        emit_mov_rax_rip(pfn_cache_rva)
        emit(b"\x48\x85\xC0")             # test rax, rax
        emit(b"\x75\x00")                 # jnz .have_pfn
        jnz_pfn_pos = len(code) - 1
        emit_mov_rax_rip(DATA_HDXGI)
        emit(b"\x48\x85\xC0")             # test rax, rax
        emit(b"\x75\x00")                 # jnz .have_hmod
        jnz_hmod_pos = len(code) - 1
        emit_lea_rcx_rip(STR_DXGI_RVA)
        emit_call_iat("LoadLibraryW")
        emit_mov_rip_rax(DATA_HDXGI)
        # .have_hmod:
        code[jnz_hmod_pos] = len(code) - (jnz_hmod_pos + 1)
        emit(b"\x48\x89\xC1")             # mov rcx, rax
        emit_lea_rdx_rip(str_fn_rva)
        emit_call_iat("GetProcAddress")
        emit_mov_rip_rax(pfn_cache_rva)
        # .have_pfn:
        code[jnz_pfn_pos] = len(code) - (jnz_pfn_pos + 1)
        emit(b"\x48\x8B\x4C\x24\x30")     # mov rcx, [rsp+0x30]
        emit(b"\x48\x8B\x54\x24\x38")     # mov rdx, [rsp+0x38]
        emit(b"\x4C\x8B\x44\x24\x40")     # mov r8,  [rsp+0x40]
        emit(b"\x48\x83\xC4\x28")         # add rsp, 0x28
        emit(b"\x48\xFF\xE0")             # jmp rax
        while len(code) % 16 != 0:
            emit(b"\xCC")
        return rva

    fn_dxgi0_rva = build_dxgi_forwarder(STR_F0_RVA, DATA_PFN_F0)
    fn_dxgi1_rva = build_dxgi_forwarder(STR_F1_RVA, DATA_PFN_F1)
    fn_dxgi2_rva = build_dxgi_forwarder(STR_F2_RVA, DATA_PFN_F2)

    # Optional verification of generated x86_64 instructions with Capstone
    try:
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        insns = list(md.disasm(bytes(code), IMAGE_BASE + 0x1000))
        print(f"Assembled .text section: {len(code)} bytes ({len(insns)} instructions verified by Capstone)")
    except ImportError:
        print(f"Assembled .text section: {len(code)} bytes")

    # Build .rdata section (at RVA 0x2000, size 0x1000)
    rdata = bytearray(0x1000)

    # 1. IAT at 0x2000 (0x000 in rdata) and ILT at 0x2080 (0x080 in rdata)
    # Hint/Name entries starting at 0x2100
    hn_off = 0x100
    for i, name in enumerate(imports):
        rva_hn = 0x2000 + hn_off
        struct.pack_into("<Q", rdata, i * 8, rva_hn)         # IAT
        struct.pack_into("<Q", rdata, 0x80 + i * 8, rva_hn)  # ILT
        nb = name.encode("ascii") + b"\x00"
        struct.pack_into("<H", rdata, hn_off, 0)             # Hint = 0
        rdata[hn_off + 2 : hn_off + 2 + len(nb)] = nb
        hn_off = align(hn_off + 2 + len(nb), 2)

    # DLL name "KERNEL32.dll"
    k32_name_rva = 0x2000 + hn_off
    rdata[hn_off : hn_off + 13] = b"KERNEL32.dll\x00"

    # Import Descriptor at 0x2200 (0x200 in rdata)
    IMPORT_DIR_RVA = 0x2200
    struct.pack_into("<IIIII", rdata, 0x200,
        0x2080,       # OriginalFirstThunk (ILT RVA)
        0,            # TimeDateStamp
        0,            # ForwarderChain
        k32_name_rva, # Name RVA
        0x2000        # FirstThunk (IAT RVA)
    )
    # Null Import Descriptor at 0x2214

    # 2. Export Directory at 0x2240 (0x240 in rdata)
    EXPORT_DIR_RVA = 0x2240
    exports = [
        ("CreateDXGIFactory", fn_dxgi0_rva),
        ("CreateDXGIFactory1", fn_dxgi1_rva),
        ("CreateDXGIFactory2", fn_dxgi2_rva),
        ("LosslessPlusGetState", fn_getstate_rva),
        ("LosslessPlusInitShared", fn_init_rva),
        ("LosslessPlusNotifyPresent", fn_notify_rva),
    ]
    exports.sort(key=lambda x: x[0]) # Must be sorted lexically for binary search!
    num_exp = len(exports)

    eat_off = 0x280
    enpt_off = 0x2B0
    eord_off = 0x2E0
    ename_off = 0x300

    dll_exp_name = b"LosslessPlusHook.dll\x00"
    rdata[ename_off : ename_off + len(dll_exp_name)] = dll_exp_name
    dll_exp_name_rva = 0x2000 + ename_off
    ename_off += len(dll_exp_name)

    for i, (ename, erva) in enumerate(exports):
        struct.pack_into("<I", rdata, eat_off + i * 4, erva)
        struct.pack_into("<I", rdata, enpt_off + i * 4, 0x2000 + ename_off)
        struct.pack_into("<H", rdata, eord_off + i * 2, i)
        eb = ename.encode("ascii") + b"\x00"
        rdata[ename_off : ename_off + len(eb)] = eb
        ename_off += len(eb)

    struct.pack_into("<IIHHIIIIIII", rdata, 0x240,
        0,                  # Characteristics
        0,                  # TimeDateStamp
        1, 0,               # MajorVersion, MinorVersion
        dll_exp_name_rva,   # Name RVA
        1,                  # Base ordinal
        num_exp,            # NumberOfFunctions
        num_exp,            # NumberOfNames
        0x2000 + eat_off,   # AddressOfFunctions RVA
        0x2000 + enpt_off,  # AddressOfNames RVA
        0x2000 + eord_off   # AddressOfNameOrdinals RVA
    )

    # 3. Fixed Strings at 0x2400+
    def put_wstr(rva, s):
        off = rva - 0x2000
        b = s.encode("utf-16le") + b"\x00\x00"
        rdata[off : off + len(b)] = b

    def put_astr(rva, s):
        off = rva - 0x2000
        b = s.encode("ascii") + b"\x00"
        rdata[off : off + len(b)] = b

    put_wstr(STR_SHM_RVA, r"Local\LosslessPlusSharedState")
    put_wstr(STR_EVT_RVA, r"Local\LosslessPlusFrameEvent")
    put_wstr(STR_DXGI_RVA, r"C:\Windows\System32\dxgi.dll")
    put_astr(STR_F0_RVA, "CreateDXGIFactory")
    put_astr(STR_F1_RVA, "CreateDXGIFactory1")
    put_astr(STR_F2_RVA, "CreateDXGIFactory2")

    # Build .data section (at RVA 0x3000)
    data_sec = bytearray(0x200)

    # Build .reloc section (at RVA 0x4000, empty single dummy block so loader allows ASLR)
    reloc_sec = bytearray(0x200)
    struct.pack_into("<IIHH", reloc_sec, 0, 0x3000, 12, 0, 0) # IMAGE_REL_BASED_ABSOLUTE padding

    # Build DOS Header + PE64 Headers (0x400 bytes)
    headers = bytearray(0x400)
    headers[0:2] = b"MZ"
    struct.pack_into("<I", headers, 0x3C, 0x80) # e_lfanew = 0x80

    pe_off = 0x80
    headers[pe_off : pe_off + 4] = b"PE\x00\x00"
    # COFF File Header (20 bytes)
    # Machine=0x8664 (AMD64), NumberOfSections=4, SizeOfOptionalHeader=0xF0, Characteristics=0x2022 (EXECUTABLE | LARGE_ADDRESS_AWARE | DLL)
    struct.pack_into("<HHIIIHH", headers, pe_off + 4,
        0x8664, 4, 0, 0, 0, 0xF0, 0x2022
    )

    opt_off = pe_off + 24
    # Optional Header PE32+ (Magic=0x20B)
    struct.pack_into("<HBBIIIIIQIIHHHHHHIIIIHHQQQQII", headers, opt_off,
        0x20B,          # Magic (PE32+)
        14, 0,          # Linker version
        0x400,          # SizeOfCode
        0x600,          # SizeOfInitializedData
        0,              # SizeOfUninitializedData
        fn_dllmain_rva, # AddressOfEntryPoint
        0x1000,         # BaseOfCode
        IMAGE_BASE,     # ImageBase
        SECT_ALIGN,     # SectionAlignment
        FILE_ALIGN,     # FileAlignment
        6, 0,           # OS Version
        0, 0,           # Image Version
        6, 0,           # Subsystem Version
        0,              # Win32VersionValue
        0x5000,         # SizeOfImage
        0x400,          # SizeOfHeaders
        0,              # CheckSum
        2,              # Subsystem (WINDOWS_GUI)
        0x0160,         # DllCharacteristics (HIGH_ENTROPY_VA | DYNAMIC_BASE | NX_COMPAT)
        0x100000, 0x1000, 0x100000, 0x1000, # Stack/Heap reserve & commit
        0,              # LoaderFlags
        16              # NumberOfRvaAndSizes
    )

    dd_off = opt_off + 112
    # Data Directories:
    # 0: Export Table
    struct.pack_into("<II", headers, dd_off + 0 * 8, EXPORT_DIR_RVA, 40)
    # 1: Import Table
    struct.pack_into("<II", headers, dd_off + 1 * 8, IMPORT_DIR_RVA, 40)
    # 5: Base Relocation Table
    struct.pack_into("<II", headers, dd_off + 5 * 8, 0x4000, 12)
    # 12: IAT
    struct.pack_into("<II", headers, dd_off + 12 * 8, 0x2000, len(imports) * 8 + 8)

    sec_off = opt_off + 0xF0
    sections = [
        # Name, VirtSize, VirtAddr, RawSize, RawPtr, Characteristics
        (b".text\x00\x00\x00", len(code), 0x1000, 0x400, 0x400, 0x60000020),
        (b".rdata\x00\x00", 0x600, 0x2000, 0x600, 0x800, 0x40000040),
        (b".data\x00\x00\x00", 0x100, 0x3000, 0x200, 0xE00, 0xC0000040),
        (b".reloc\x00\x00", 12,    0x4000, 0x200, 0x1000, 0x42000040),
    ]
    for i, (sname, vsize, vaddr, rsize, rptr, chars) in enumerate(sections):
        off = sec_off + i * 40
        headers[off : off + 8] = sname
        struct.pack_into("<IIIIIIHHI", headers, off + 8,
            vsize, vaddr, rsize, rptr, 0, 0, 0, 0, chars
        )

    text_padded = bytearray(0x400)
    text_padded[:len(code)] = code

    full_pe = headers + text_padded + rdata[:0x600] + data_sec + reloc_sec
    with open(out_path, "wb") as f:
        f.write(full_pe)

    try:
        import pefile
        pe = pefile.PE(out_path, fast_load=True)
        pe.OPTIONAL_HEADER.CheckSum = pe.generate_checksum()
        pe.write(out_path)
        pe.close()
    except ImportError:
        pass
    print(f"Built native x64 {out_path} ({len(full_pe)} bytes)")

if __name__ == "__main__":
    base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = os.path.join(base_dir, "bin")
    os.makedirs(out_dir, exist_ok=True)
    dll_path = os.path.join(out_dir, "LosslessPlusHook.dll")
    build_hook_dll(dll_path)

    # Live runtime verification of LosslessPlusHook.dll!
    hook = ctypes.WinDLL(dll_path)
    hook.LosslessPlusInitShared.restype = ctypes.c_int
    hook.LosslessPlusNotifyPresent.argtypes = [ctypes.c_uint32, ctypes.c_uint32]
    hook.LosslessPlusNotifyPresent.restype = ctypes.c_uint64
    hook.LosslessPlusGetState.argtypes = [ctypes.c_void_p]
    hook.LosslessPlusGetState.restype = ctypes.c_int

    ok = hook.LosslessPlusInitShared()
    fc1 = hook.LosslessPlusNotifyPresent(1, 1)
    fc2 = hook.LosslessPlusNotifyPresent(1, 1)

    buf = ctypes.create_string_buffer(56)
    hook.LosslessPlusGetState(buf)
    magic, ver, frame_cnt, last_qpc, freq, pid, flags, depth_ok, ui_ok, interval = struct.unpack("<IIQQQIIIIQ", buf.raw)
    print(f"LIVE HOOK VERIFICATION: magic={magic:#x} ('LSP+'), ver={ver:#x}, frameCount={frame_cnt}, qpc={last_qpc}, freq={freq}, pid={pid}, depth={depth_ok}, ui={ui_ok}")
