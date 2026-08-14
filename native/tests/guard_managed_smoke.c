/* guard-managed-smoke — the NATIVE-level de-risk for Inutil.Safe.Run's stack shape (docs/contribution/architecture/17-reach-faces.md).
 *
 * guard_smoke.c proves the SEH fault-guard catches a c0000005 inside invoke_fn (ONE native frame). Safe.Run adds
 * two things guard_smoke does not model, and this test isolates each:
 *   (a) a DEEPER risky window: Safe.Run's stack is guard -> reverse-P/Invoke trampoline -> managed delegate ->
 *       il2cpp proxy -> il2cpp -> fault, so a caught fault's longjmp must unwind SEVERAL frames. Modeled with a
 *       NESTED faulter: guard -> nested_faulter -> deref_garbage -> fault (two native frames above the setjmp).
 *   (b) REPEATED use: every caught fault must leave the guard's TLS/setjmp/VEH state consistent and REUSABLE. We
 *       fault 64 times, then prove a CLEAN guarded call still works AFTER a fault.
 *
 * WHAT THIS CANNOT DO: a pure-C harness has no CLR, so the reverse-P/Invoke transition frame Safe.Run's longjmp
 * skips is NOT present — this cannot exercise CLR-frame-chain survival. That property is proven only with a real
 * CLR in the loop: the in-game battery case `safe.run.faulted-survives`. This file de-risks the NATIVE guard; that
 * case de-risks the managed frame — together they cover Safe.Run, neither alone. (Honesty: don't let a green C
 * test read as "the CLR-frame concern is settled" when it structurally cannot see that frame.)
 *
 * Dependency-free: builds the real inutil_core.dll + this harness under mingw + Wine. Emits greppable RESULT
 * lines; tools/wine/guard-smoke.sh scrapes "RESULT: guard-managed-smoke PASS". */
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "interceptor.h"

/* Observable side effect proving nested_faulter's body actually ran (past entry) before the deeper frame faulted —
 * so the multi-frame unwind is real, not a fault optimized up to the call site. volatile: never elided. */
static volatile int g_depth;

/* The deepest leaf: dereference a small, obviously-invalid pointer -> EXCEPTION_ACCESS_VIOLATION at address 0x10.
 * noinline so it is a DISTINCT stack frame the longjmp must unwind past (the whole point of the nested case). */
__attribute__((noinline)) static void* deref_garbage(void) {
    volatile int* p = (int*)(intptr_t)0x10;
    return (void*)(intptr_t)*p;                 /* faults here */
}

/* invoke_fn with a NESTED call before the fault: guard -> nested_faulter -> deref_garbage -> fault. The longjmp
 * back to the guard's setjmp must correctly skip BOTH this frame and deref_garbage's — the multi-frame unwind
 * Safe.Run's deep stack needs. Same ABI as il2cpp_runtime_invoke (method, obj, params, exc). */
__attribute__((noinline)) static void* nested_faulter(const void* m, void* o, void** p, void** e) {
    (void)m; (void)o; (void)p; (void)e;
    g_depth++;                                  /* got past entry: this frame is live when the deeper one faults */
    return deref_garbage();
}

/* A benign invoke_fn: params[0] * 7. Proves a CLEAN guarded call still works between/after faults. */
static void* clean_fn(const void* m, void* o, void** p, void** e) {
    (void)m; (void)o; (void)e;
    return (void*)(intptr_t)(*(long*)p[0] * 7);
}

int main(void) {
    int fails = 0;
    long arg = 6; void* params[1] = { &arg };

    /* (1) NESTED-frame fault: the longjmp unwinds guard <- nested_faulter <- deref_garbage. Caught -> code +
     *     addr, process survives, and g_depth==1 proves nested_faulter's frame was live (multi-frame unwind). */
    g_depth = 0;
    void *ret = NULL, *exc = NULL, *fa = NULL;
    uint32_t c1 = inutil_guarded_invoke((void*)nested_faulter, NULL, NULL, params, &exc, &ret, &fa);
    int nested_ok = (c1 == (uint32_t)0xC0000005) && (fa == (void*)(intptr_t)0x10) && (g_depth == 1);
    printf("MANAGED nested-fault: code=0x%lx addr=%p depth=%d (want 0xc0000005 0x10 1) -> %s\n",
           (unsigned long)c1, fa, g_depth, nested_ok ? "PASS" : "FAIL");
    if (!nested_ok) fails++;

    /* (2) REUSE across repeated faults: 64 nested faults in a row must each be caught (the guard's per-thread
     *     TLS/setjmp/VEH state stays consistent — Safe.Run is invoked many times over a session). */
    int reused = 1;
    for (int i = 0; i < 64; i++) {
        void *r = NULL, *x = NULL, *a = NULL;
        uint32_t c = inutil_guarded_invoke((void*)nested_faulter, NULL, NULL, params, &x, &r, &a);
        if (c != (uint32_t)0xC0000005) { reused = 0; break; }
    }
    printf("MANAGED reuse-loop:   64 repeated faults each caught -> %s\n", reused ? "PASS" : "FAIL");
    if (!reused) fails++;

    /* (3) INTERLEAVE fault + clean: a caught fault must not poison the NEXT guarded call. fault -> clean(6)=42.
     *     This is the native stand-in for the in-game "run more managed after the fault" — here the "more" is a
     *     second guarded native call succeeding, proving the guard is usable again once a fault is caught. */
    void *rf = NULL, *xf = NULL, *af = NULL;
    inutil_guarded_invoke((void*)nested_faulter, NULL, NULL, params, &xf, &rf, &af);      /* fault */
    void *rc = NULL, *xc = NULL, *ac = NULL;
    uint32_t cc = inutil_guarded_invoke((void*)clean_fn, NULL, NULL, params, &xc, &rc, &ac);   /* clean AFTER a fault */
    int interleave_ok = (cc == 0) && ((long)(intptr_t)rc == 42);
    printf("MANAGED interleave:   clean-after-fault code=0x%lx ret=%ld (want 0 42) -> %s\n",
           (unsigned long)cc, (long)(intptr_t)rc, interleave_ok ? "PASS" : "FAIL");
    if (!interleave_ok) fails++;

    printf("RESULT: guard-managed-smoke %s\n", fails == 0 ? "PASS" : "FAIL");
    return fails == 0 ? 0 : 1;
}
