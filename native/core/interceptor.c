/* The substrate-agnostic hook engine. See interceptor.h for the seam rationale.
 *
 * Mechanism: generic_thunk_post.S + a signature-driven dispatcher (zero per-method codegen) that crosses into
 * managed; the install target is whatever methodPointer the host's typed proxy resolved. Split out of the
 * standalone host so external-loader adapters (BepInEx, MelonLoader, etc.) can link it without dragging in the
 * standalone il2cpp/CoreCLR bring-up.
 */
#include <stdint.h>
#include <stdlib.h>
#include <setjmp.h>
#include <windows.h>
#include "MinHook.h"
#include "interceptor.h"

typedef struct HookCtx {
    void*       trampoline;     /* offset 0 — generic_thunk_post.S reads this */
    void*       methodPointer;  /* the detoured body (dedup key for shared __Canon pointers) */
    const void* boundMethodInfo;/* the MethodInfo* bound at install (the key when miSlot spills) */
    int         miSlot;         /* POSITIONAL arg index of the trailing MethodInfo* (this+sret+params): 0-3 =
                                 * GPR home slot, 4+ = caller-stack spill (CallFrame.stack[miSlot-4]); <0 unknown */
    int         refs;           /* offset 28 — how many MethodInfo* registered at this body: 1 = a SINGLE
                                 * instantiation (route by boundMethodInfo); >1 = a fully-shared __Canon body
                                 * serving several instantiations (route by the live trailing MethodInfo*). */
} HookCtx;

/* Vtable-slot hook context — binary-compatible with HookCtx at offsets 0/16/24 (trampoline /
 * boundMethodInfo / miSlot) so make_closure((HookCtx*)c) and the thunk dispatch work unchanged.
 * Slot 8 differs semantically (slotAddr vs methodPointer) but is the same size and unused by the
 * dispatch path. `active` tracks whether the vtable slot is currently patched: 0 after restore so
 * the dedup check skips inactive entries and a re-install allocates a fresh closure. */
typedef struct VtableCtx {
    void*       trampoline;      /* offset 0: saved original VirtualInvokeData.methodPtr */
    void*       slotAddr;        /* offset 8: &vtable[slot].methodPtr — dedup + restore key */
    const void* boundMethodInfo; /* offset 16: MethodInfo* dispatch key */
    int         miSlot;          /* offset 24 */
    int         active;          /* offset 28: nonzero while the slot is patched */
} VtableCtx;

extern void inutil_thunk_post(void);      /* native/core/generic_thunk_post.S */
extern void inutil_invoker_thunk(void);   /* native/core/generic_thunk_post.S (the invoke path) */

/* The adapter's managed dispatchers (set by init). */
static inutil_pre_fn          g_pre;
static inutil_post_fn         g_post;
static inutil_invoke_pre_fn   g_ipre;
static inutil_invoke_post_fn  g_ipost;

/* ---- proceed / call-the-original (the "around" capability) ---- */
/* The hook frame currently firing on THIS thread, so a managed ctx.Proceed() can re-enter the original.
 * Thread-local + SAVED/RESTORED around every pre-dispatch, so when the original (run via Proceed) calls
 * another hooked method, the nested dispatch doesn't clobber the outer frame. kind: 0 none, 1 methodPointer
 * detour, 2 invoke. */
struct InvokeCtx;   /* the invoke-path ctx, defined below; forward-declared for the proceed frame */
typedef struct ProceedFrame {
    int kind;
    HookCtx* ctx; CallFrame* f; RetFrame* r;                                                   /* kind 1 */
    struct InvokeCtx* ic; void* mp; const void* method; void* obj; void** params; void* ret;   /* kind 2 */
} ProceedFrame;
/* Per-thread current frame via the Win32 TLS API (kernel32). NOT __thread: on this toolchain __thread pulls
 * in libmcfgthread-2.dll, a runtime DLL not deployed beside the core, so the DLL fails to load under the
 * loader. The frame is a STACK local in each dispatcher; TLS holds a POINTER to it, saved/restored for re-entrancy. */
static DWORD g_tls = TLS_OUT_OF_INDEXES;
extern void inutil_call_original(void* trampoline, CallFrame* f, RetFrame* r);   /* generic_thunk_post.S */

/* The dispatch key: prefer the LIVE MethodInfo* the runtime passed in the frame (so distinct generic
 * instantiations sharing one __Canon body route to distinct managed keys); fall back to the install-bound
 * MethodInfo* only when the captured slot is NULL. A direct/devirtualized/tail call into our own entry can ZERO
 * the trailing MethodInfo* (a compiler-closure tail-call does `xor r9d,r9d` before `jmp`), so without this a
 * non-shared body routes to key 0 and misses its hook (observed on EFT's
 * HideoutRepresentation.OnResourceConsumptionChanged). A shared __Canon body ALWAYS passes a non-zero live mi,
 * so the bound-mi fallback only ever engages for a non-shared body — where the bound MethodInfo* is exactly the key.
 *
 * miSlot is the trailing MethodInfo*'s POSITIONAL arg index (this + sret + params), not capped at 3. Win64 places
 * arg index N in GPR[N] for N<4, else on the caller's stack at [rsp+0x20 + (N-4)*8] — which the thunk exposes as
 * CallFrame.stack[N-4] (generic_thunk_post.S: stack = rbp+0x30 = the 5th+ args). So a shared __Canon body whose
 * real arg count spilled the hidden MethodInfo* past slot 3 reads it from the stack and routes correctly, instead
 * of mis-routing every such instantiation to the first registrant. */
static const void* dispatch_key(HookCtx* c, CallFrame* f) {
    /* Only a body shared by MULTIPLE registered instantiations (refs>1 — a fully-shared __Canon generic hooked
     * for several type-args) needs the live trailing MethodInfo* to disambiguate which chain to run. A single
     * instantiation (refs<=1) has exactly one chain — the bound MethodInfo* — so prefer it unconditionally. This
     * matters because the runtime's live MethodInfo* for a generic call can be a DIFFERENT inflation of the same
     * closed method than reflection bound at install: il2cpp mints distinct MethodInfo* for the same <method,
     * type-args> reached via interface dispatch vs the concrete class vs an RGCTX generic-method reference
     * (observed: EFT's GlobalsDataLoader GetTraderSettings<…>). Keying a single-instantiation body by that
     * unpredictable live mi missed the managed table; the bound mi can't. */
    if (c->refs > 1 && c->miSlot >= 0) {
        const void* live = (c->miSlot < 4)
            ? (const void*)f->gpr[c->miSlot]           /* arg index 0-3: RCX/RDX/R8/R9 (home) */
            : (const void*)f->stack[c->miSlot - 4];    /* arg index 4+: caller's 5th+ args (the ABI spill) */
        if (live) return live;
    }
    return c->boundMethodInfo;
}

/* The thunk lands here; cross into managed keyed by MethodInfo*. The pre dispatcher returns SKIP (nonzero =>
 * the thunk bypasses the original CALL and returns what the hook left in RetFrame). We publish the current frame
 * to TLS (save/restore for re-entrancy) so a hook can ctx.Proceed() the original. */
int inutil_pre_dispatch(HookCtx* c, CallFrame* f, RetFrame* r) {
    if (!g_pre) return 0;
    ProceedFrame frame; frame.kind = 1; frame.ctx = c; frame.f = f; frame.r = r;
    void* saved = TlsGetValue(g_tls);
    TlsSetValue(g_tls, &frame);
    int skip = g_pre(dispatch_key(c, f), f, r);
    TlsSetValue(g_tls, saved);
    return skip;
}
void inutil_post_dispatch(HookCtx* c, CallFrame* a, RetFrame* r) { if (g_post) g_post(dispatch_key(c, a), a, r); }

/* Per-target closure: mov r11, ctx ; mov rax, &inutil_thunk_post ; jmp rax. */
static void* make_closure(HookCtx* ctx) {
    unsigned char* m = (unsigned char*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    unsigned char* p = m;
    *p++ = 0x49; *p++ = 0xBB; *(uint64_t*)p = (uint64_t)ctx;                p += 8; /* mov r11, ctx  */
    *p++ = 0x48; *p++ = 0xB8; *(uint64_t*)p = (uint64_t)&inutil_thunk_post; p += 8; /* mov rax, post */
    *p++ = 0xFF; *p++ = 0xE0;                                                       /* jmp rax       */
    FlushInstructionCache(GetCurrentProcess(), m, 64);
    return m;
}

/* A grown pool, NOT a fixed array: a real target hooks thousands of methods. The pool is an array of POINTERS to
 * individually-heap-allocated ctxs; the pointer array doubles on demand. A realloc that moves it is harmless
 * because the generated closures (make_closure) bake each ctx's STABLE malloc address into machine code, never an
 * array slot, and detours are never removed — growth never pulls a live ctx out from under its closure. Installs
 * are serialized by the managed side's _reg lock, so the pool needs no native lock. */
static void* pool_add(void*** pool, int* n, int* cap, size_t elem) {
    if (*n == *cap) {
        int ncap = *cap ? *cap * 2 : 64;
        void** grown = (void**)realloc(*pool, (size_t)ncap * sizeof(void*));
        if (!grown) return NULL;
        *pool = grown; *cap = ncap;
    }
    void* node = calloc(1, elem);               /* the STABLE address closures capture */
    if (!node) return NULL;
    (*pool)[(*n)++] = node;
    return node;
}

/* The install callback handed to managed. One detour per UNIQUE methodPointer; managed may install
 * several MethodInfo* at the same pointer (reference-type generic instantiations share a __Canon
 * body) — the second+ such call finds the existing detour and succeeds without re-hooking, because
 * the dispatcher already routes by the live MethodInfo* and managed already holds each key. */
static HookCtx** g_hooks;
static int       g_hook_n, g_hook_cap;
static int CORECLR_DELEGATE_CALLTYPE inutil_install(void* methodPointer, void* methodInfo, int miSlot) {
    for (int i = 0; i < g_hook_n; i++)
        if (g_hooks[i]->methodPointer == methodPointer) {
            g_hooks[i]->refs++;            /* another instantiation shares this __Canon body -> now route by live mi */
            return 1;                      /* shared __Canon pointer already detoured: nothing else to do */
        }
    HookCtx* c = (HookCtx*)pool_add((void***)&g_hooks, &g_hook_n, &g_hook_cap, sizeof(HookCtx));
    if (!c) return 0;
    c->methodPointer = methodPointer;
    c->boundMethodInfo = methodInfo;
    c->miSlot = miSlot;
    c->refs = 1;                           /* first (so far only) instantiation at this body */
    void* tramp = NULL;
    if (MH_CreateHook(methodPointer, make_closure(c), &tramp) != MH_OK) { free(c); g_hook_n--; return 0; }
    c->trampoline = tramp;
    /* no-partial-commit: if the enable fails, UNDO the CreateHook and pop the pool entry so the pool is left
     * exactly as before the attempt. Otherwise the created-but-disabled detour is a zombie AND a later install of
     * the same methodPointer hits the dedup above, bumps refs, and treats a FAILED install as a live shared
     * __Canon body. Never leave an entry the dedup can resurrect. */
    if (MH_EnableHook(methodPointer) != MH_OK) { MH_RemoveHook(methodPointer); free(c); g_hook_n--; return 0; }
    return 1;
}

/* ---- the INVOKE path: overwrite MethodInfo::invoker_method (for __Canon generic methods) ---- */

/* il2cpp invoker ABI (2022.3): void(Il2CppMethodPointer, const MethodInfo*, void* obj, void** params,
 * void* ret); invoker_method is the field at MethodInfo+0x10. */
typedef void (*InvokerMethod)(void* mp, const void* method, void* obj, void** params, void* ret);
#define IL2CPP_INVOKER_OFFSET 0x10

typedef struct InvokeCtx {
    InvokerMethod original;     /* offset 0 — the displaced invoker we chain to  */
    const void*   methodInfo;   /* the managed dispatch key                       */
} InvokeCtx;

/* The invoke thunk (generic_thunk_post.S) forwards here, ctx prepended. Fire pre, run the original
 * invoker (which runs the method body + writes ret), fire post — the managed side reads/writes the
 * args via `params` and the return via `ret`, keyed by methodInfo. If a pre-hook called ctx.Skip()
 * (g_ipre returns nonzero) the original invoker is bypassed and `ret` holds whatever the hook wrote. */
void inutil_invoker_c(InvokeCtx* c, void* mp, const void* method, void* obj, void** params, void* ret) {
    ProceedFrame frame;             /* publish the invoke frame so a pre-hook can ctx.Proceed() (save/restore) */
    frame.kind = 2; frame.ic = c; frame.mp = mp; frame.method = method; frame.obj = obj; frame.params = params; frame.ret = ret;
    void* saved = TlsGetValue(g_tls);
    TlsSetValue(g_tls, &frame);
    int skip = g_ipre ? g_ipre(c->methodInfo, obj, params, ret) : 0;
    TlsSetValue(g_tls, saved);
    if (!skip) c->original(mp, method, obj, params, ret);
    if (g_ipost) g_ipost(c->methodInfo, obj, params, ret);
}

/* Per-method closure: mov r11, InvokeCtx ; mov rax, &inutil_invoker_thunk ; jmp rax. */
static void* make_invoker_closure(InvokeCtx* ctx) {
    unsigned char* m = (unsigned char*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    unsigned char* p = m;
    *p++ = 0x49; *p++ = 0xBB; *(uint64_t*)p = (uint64_t)ctx;                   p += 8; /* mov r11, ctx */
    *p++ = 0x48; *p++ = 0xB8; *(uint64_t*)p = (uint64_t)&inutil_invoker_thunk; p += 8; /* mov rax, thunk */
    *p++ = 0xFF; *p++ = 0xE0;                                                          /* jmp rax */
    FlushInstructionCache(GetCurrentProcess(), m, 64);
    return m;
}

static InvokeCtx** g_invokers;          /* same grown pool of stable-address nodes (see pool_add) */
static int         g_invoker_n, g_invoker_cap;
static int CORECLR_DELEGATE_CALLTYPE inutil_install_invoker(void* methodInfo) {
    void** slot = (void**)((char*)methodInfo + IL2CPP_INVOKER_OFFSET);   /* &mi->invoker_method */
    for (int i = 0; i < g_invoker_n; i++)
        if (g_invokers[i]->methodInfo == methodInfo) return 1;           /* already redirected */
    InvokeCtx* c = (InvokeCtx*)pool_add((void***)&g_invokers, &g_invoker_n, &g_invoker_cap, sizeof(InvokeCtx));
    if (!c) return 0;
    c->original = (InvokerMethod)*slot;
    c->methodInfo = methodInfo;
    void* closure = make_invoker_closure(c);
    DWORD old;                                                           /* the MethodInfo may be read-only */
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) { free(c); g_invoker_n--; return 0; }
    *slot = closure;
    VirtualProtect(slot, sizeof(void*), old, &old);
    FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
    return 1;
}

/* Run the original of the method whose hook is firing on THIS thread (managed ctx.Proceed()). The original
 * (the MinHook trampoline / the displaced invoker) runs the un-hooked body, so this does NOT re-enter our
 * detour; its return lands in the RetFrame / out-buffer the managed ctx already reads. Returns 1 if a frame
 * was live (called inside a pre-hook), 0 otherwise (a post-hook or no hook firing). */
static int CORECLR_DELEGATE_CALLTYPE inutil_proceed(void) {
    ProceedFrame* cur = (g_tls == TLS_OUT_OF_INDEXES) ? NULL : (ProceedFrame*)TlsGetValue(g_tls);
    if (!cur) return 0;
    if (cur->kind == 1) { inutil_call_original((void*)cur->ctx->trampoline, cur->f, cur->r); return 1; }
    if (cur->kind == 2) { cur->ic->original(cur->mp, cur->method, cur->obj, cur->params, cur->ret); return 1; }
    return 0;
}

/* ---- vtable-slot hook path -------------------------------------------------------------------- */
/* Patches VirtualInvokeData.methodPtr at a specific class's vtable slot rather than detouring the
 * function body. This is the only correct path when the target body is a SHARED EMPTY STUB (il2cpp
 * collapses all empty virtual bodies to one stub; detouring it fires for unrelated empty methods).
 * The closure is the same generic thunk as a methodPointer detour; the dispatch key is always the
 * bound methodInfo* (miSlot=-1), since the goal is class-scoped, not shared-generic dispatch. */

static VtableCtx** g_vtable_hooks;
static int         g_vtable_n, g_vtable_cap;

static intptr_t CORECLR_DELEGATE_CALLTYPE inutil_install_vtable(void* slotAddr, void* methodInfo, int miSlot) {
    for (int i = 0; i < g_vtable_n; i++)
        if (g_vtable_hooks[i]->active && g_vtable_hooks[i]->slotAddr == slotAddr)
            return (intptr_t)g_vtable_hooks[i]->trampoline;   /* already patched — return saved orig */
    VtableCtx* c = (VtableCtx*)pool_add((void***)&g_vtable_hooks, &g_vtable_n, &g_vtable_cap, sizeof(VtableCtx));
    if (!c) return 0;
    /* no-partial-commit: validate EVERYTHING this entry depends on BEFORE writing the slot or marking it active,
     * and on any failure pop the pool entry so it is left exactly as before the attempt. A real saved methodPtr is
     * never NULL, so `orig != 0` both guards a bad/empty slot AND makes the 0 return an UNAMBIGUOUS failure sentinel:
     * success always returns a non-zero orig. make_closure runs only after orig + VirtualProtect succeed, so no
     * allocation/patch leaks. */
    void* orig = *(void**)slotAddr;
    if (!orig) { free(c); g_vtable_n--; return 0; }                 /* no valid original to save/restore */
    DWORD old;
    if (!VirtualProtect(slotAddr, sizeof(void*), PAGE_READWRITE, &old)) { free(c); g_vtable_n--; return 0; }
    void* closure = make_closure((HookCtx*)c);   /* safe: closure captures ctx by pointer; fields read at call time */
    /* Fill in fields BEFORE writing the slot so a concurrent dispatch sees a valid ctx. */
    c->trampoline      = orig;        /* Proceed() calls this directly (no MinHook trampoline needed) */
    c->slotAddr        = slotAddr;
    c->boundMethodInfo = methodInfo;
    c->miSlot          = miSlot;
    c->active          = 1;
    *(void**)slotAddr  = closure;
    VirtualProtect(slotAddr, sizeof(void*), old, &old);
    FlushInstructionCache(GetCurrentProcess(), slotAddr, sizeof(void*));
    return (intptr_t)orig;
}

static void CORECLR_DELEGATE_CALLTYPE inutil_restore_vtable(void* slotAddr, void* origMethodPtr) {
    for (int i = 0; i < g_vtable_n; i++)
        if (g_vtable_hooks[i]->active && g_vtable_hooks[i]->slotAddr == slotAddr) {
            g_vtable_hooks[i]->active = 0;   /* mark inactive before the write so a concurrent dispatch sees 0 */
            break;
        }
    DWORD old;
    if (!VirtualProtect(slotAddr, sizeof(void*), PAGE_READWRITE, &old)) return;
    *(void**)slotAddr = origMethodPtr;
    VirtualProtect(slotAddr, sizeof(void*), old, &old);
    FlushInstructionCache(GetCurrentProcess(), slotAddr, sizeof(void*));
}

/* ---- SEH-guarded execution: fault-tolerant read + call (see interceptor.h) --------------------
 * mingw C has no __try/__except (GCC doesn't implement the keyword), so the guard is a process-wide Vectored
 * Exception Handler + setjmp/longjmp. A guard fn publishes a GuardFrame (a stack local) in a TLS slot and setjmp's
 * a recovery point; if the risky read/call faults, the VEH — which runs BEFORE any frame handler — finds the live
 * GuardFrame for THIS thread, records the code + faulting address, and longjmp's back to the recovery branch. With
 * no active frame, or for anything but a hardware memory/instruction fault, it returns CONTINUE_SEARCH and is
 * invisible to CoreCLR/il2cpp's own exception handling (a managed exception comes back through il2cpp's `exc`
 * out-param, never as a hardware fault). Self-initializing, idempotent, independent of the hook engine. */
typedef struct GuardFrame { jmp_buf jb; DWORD code; void* addr; } GuardFrame;
static DWORD         g_guard_tls = TLS_OUT_OF_INDEXES;   /* &current GuardFrame, per thread (Win32 TLS, not __thread) */
static PVOID         g_guard_veh;
static volatile LONG g_guard_state;                      /* 0 uninit, 1 initializing, 2 ready */

/* Faults worth turning into a verdict: a bad memory access (the garbage-class c0000005) or a jump through a
 * garbage function pointer (illegal/priv instruction). NOT stack overflow — the guard page is already tripped,
 * so unwinding out is unsafe; let that fail fast. */
static int guard_catchable(DWORD c) {
    return c == EXCEPTION_ACCESS_VIOLATION      || c == EXCEPTION_IN_PAGE_ERROR
        || c == EXCEPTION_DATATYPE_MISALIGNMENT || c == EXCEPTION_ILLEGAL_INSTRUCTION
        || c == EXCEPTION_PRIV_INSTRUCTION;
}

static LONG CALLBACK inutil_guard_veh(PEXCEPTION_POINTERS ep) {
    if (g_guard_tls == TLS_OUT_OF_INDEXES) return EXCEPTION_CONTINUE_SEARCH;
    GuardFrame* gf = (GuardFrame*)TlsGetValue(g_guard_tls);
    if (!gf) return EXCEPTION_CONTINUE_SEARCH;                 /* not inside a guarded region on this thread */
    DWORD c = ep->ExceptionRecord->ExceptionCode;
    if (!guard_catchable(c)) return EXCEPTION_CONTINUE_SEARCH; /* leave CoreCLR/il2cpp's own exceptions alone */
    gf->code = c;
    gf->addr = (ep->ExceptionRecord->NumberParameters >= 2)
             ? (void*)ep->ExceptionRecord->ExceptionInformation[1] : NULL;
    longjmp(gf->jb, 1);                                        /* -> the guard fn's recovery branch */
}

/* Idempotent one-time bring-up: allocate the TLS slot + register the VEH (registered FIRST, so a guarded
 * fault is caught before CoreCLR turns it into a managed AV / fail-fast). Called from inutil_interceptor_init
 * AND lazily from the guard fns, so they work with no hook engine. */
static void guard_init(void) {
    if (InterlockedCompareExchange(&g_guard_state, 1, 0) == 0) {
        g_guard_tls = TlsAlloc();
        g_guard_veh = AddVectoredExceptionHandler(1 /* first */, inutil_guard_veh);
        InterlockedExchange(&g_guard_state, 2);               /* publish ready last (TLS slot now valid) */
    } else {
        while (InterlockedCompareExchange(&g_guard_state, 2, 2) != 2) Sleep(0);   /* lost the init race — wait */
    }
}

/* Read one pointer-sized value at `addr` into *out; 1 = ok, 0 = the read faulted (addr unmapped/unreadable). */
int inutil_guard_read(const void* addr, void** out) {
    if (g_guard_state != 2) guard_init();
    GuardFrame gf; gf.code = 0; gf.addr = NULL;
    void* volatile saved = TlsGetValue(g_guard_tls);          /* volatile: survives the setjmp/longjmp clobber */
    TlsSetValue(g_guard_tls, &gf);
    int ok;
    if (setjmp(gf.jb) == 0) { *out = *(void* const*)addr; ok = 1; }
    else                    { ok = 0; }                       /* the read faulted */
    TlsSetValue(g_guard_tls, (void*)saved);
    return ok;
}

/* il2cpp_runtime_invoke's ABI: Il2CppObject* (const MethodInfo*, void* obj, void** params, Il2CppException**). */
typedef void* (*il2cpp_runtime_invoke_t)(const void* method, void* obj, void** params, void** exc);

/* Call il2cpp_runtime_invoke (handed in as `invoke_fn`) under the guard. 0 = clean (with *out_ret = the boxed
 * result and *out_exc = any managed exception il2cpp returned); else the hardware fault code (e.g. 0xC0000005)
 * with *out_fault_addr = the faulting address. After a caught fault the runtime is TAINTED (unwound past
 * il2cpp frames): report + restart, don't keep iterating. Any out-param may be NULL. */
uint32_t inutil_guarded_invoke(void* invoke_fn, const void* method, void* obj, void** params,
                               void** out_exc, void** out_ret, void** out_fault_addr) {
    if (g_guard_state != 2) guard_init();
    GuardFrame gf; gf.code = 0; gf.addr = NULL;
    void* volatile saved = TlsGetValue(g_guard_tls);
    TlsSetValue(g_guard_tls, &gf);
    uint32_t code;
    if (setjmp(gf.jb) == 0) {
        void* exc = NULL;
        void* ret = ((il2cpp_runtime_invoke_t)invoke_fn)(method, obj, params, &exc);
        if (out_exc) *out_exc = exc;
        if (out_ret) *out_ret = ret;
        code = 0;
    } else {
        code = (uint32_t)gf.code;                             /* the call faulted */
        if (out_fault_addr) *out_fault_addr = gf.addr;
    }
    TlsSetValue(g_guard_tls, (void*)saved);
    return code;
}

void* inutil_interceptor_init(inutil_pre_fn pre, inutil_post_fn post,
                              inutil_invoke_pre_fn ipre, inutil_invoke_post_fn ipost,
                              void** out_install_invoker, void** out_proceed,
                              void** out_install_vtable, void** out_restore_vtable) {
    g_pre = pre; g_post = post; g_ipre = ipre; g_ipost = ipost;
    if (g_tls == TLS_OUT_OF_INDEXES) g_tls = TlsAlloc();   /* for ctx.Proceed()'s per-thread current frame */
    guard_init();                                          /* SEH fault-guard: TLS slot + VEH (idempotent) */
    if (MH_Initialize() != MH_OK) return NULL;
    if (out_install_invoker) *out_install_invoker = (void*)&inutil_install_invoker;
    if (out_proceed)         *out_proceed         = (void*)&inutil_proceed;
    if (out_install_vtable)  *out_install_vtable  = (void*)&inutil_install_vtable;
    if (out_restore_vtable)  *out_restore_vtable  = (void*)&inutil_restore_vtable;
    return (void*)&inutil_install;
}

void inutil_interceptor_shutdown(void) {
    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
}
