/* guard-smoke — a dependency-free proof that inutil_core's SEH fault-guard catches a c0000005 (a bad read + a
 * garbage-class invoke) WITHOUT killing the process, under mingw + Wine.
 *
 * The Phase-0 de-risk for Inutil.Probe (IsLiveObject/Describe) and Inutil.Invoke's guarded path (TryInvoke): it
 * exercises the REAL exported inutil_guard_read / inutil_guarded_invoke from the built inutil_core.dll — no il2cpp,
 * no Unity, no BepInEx. mingw C has no __try/__except (GCC rejects the keyword), so the guard rides a Vectored
 * Exception Handler + setjmp; this proves that mechanism catches a hardware fault under Wine and returns a verdict.
 *
 * Emits greppable RESULT lines; tools/wine/guard-smoke.sh scrapes "RESULT: guard-smoke PASS". */
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "interceptor.h"

/* Stand-ins for il2cpp_runtime_invoke (same ABI: void*(method, obj, params, exc)). One benign; one that
 * dereferences a garbage pointer — mimicking the serverstub crash (templates->klass read at offset 0). */
static void* fake_invoke_ok(const void* method, void* obj, void** params, void** exc) {
    (void)method; (void)obj; (void)exc;
    long v = *(long*)params[0];
    return (void*)(intptr_t)(v * 3);
}
static void* fake_invoke_fault(const void* method, void* obj, void** params, void** exc) {
    (void)method; (void)obj; (void)params; (void)exc;
    volatile int* garbage = (int*)(intptr_t)0x10;   /* a small, obviously-invalid "class" pointer */
    return (void*)(intptr_t)*garbage;               /* -> EXCEPTION_ACCESS_VIOLATION */
}

int main(void) {
    int fails = 0;

    /* (1) guard_read: a bad pointer -> 0 (no crash); a good pointer -> 1 + the correct value. */
    void* got = (void*)(intptr_t)0xDEAD;
    int bad  = inutil_guard_read((const void*)(intptr_t)0x10, &got);
    long target = 0x1234; void* tp = &target; void* good_val = NULL;
    int good = inutil_guard_read(&tp, &good_val);
    int read_ok = (bad == 0) && (good == 1) && (good_val == &target);
    printf("GUARD read:         bad=%d good=%d val=%p (want bad=0 good=1 val=%p) -> %s\n",
           bad, good, good_val, (void*)&target, read_ok ? "PASS" : "FAIL");
    if (!read_ok) fails++;

    /* (2) guarded_invoke: a benign call -> code 0 + the correct result. */
    long arg = 14; void* params[1] = { &arg };
    void *ret = NULL, *exc = NULL, *fa = NULL;
    uint32_t c1 = inutil_guarded_invoke((void*)fake_invoke_ok, NULL, NULL, params, &exc, &ret, &fa);
    int ok_ok = (c1 == 0) && ((long)(intptr_t)ret == 42);
    printf("GUARD invoke-ok:    code=0x%lx ret=%ld (want code=0 ret=42) -> %s\n",
           (unsigned long)c1, (long)(intptr_t)ret, ok_ok ? "PASS" : "FAIL");
    if (!ok_ok) fails++;

    /* (3) guarded_invoke: a faulting call -> the fault code + address, and the process SURVIVES. */
    void *ret2 = NULL, *exc2 = NULL, *fa2 = NULL;
    uint32_t c2 = inutil_guarded_invoke((void*)fake_invoke_fault, NULL, NULL, params, &exc2, &ret2, &fa2);
    int fault_ok = (c2 == (uint32_t)0xC0000005) && (fa2 == (void*)(intptr_t)0x10);
    printf("GUARD invoke-fault: code=0x%lx addr=%p (want code=0xc0000005 addr=0x10) survived -> %s\n",
           (unsigned long)c2, fa2, fault_ok ? "PASS" : "FAIL");
    if (!fault_ok) fails++;

    printf("RESULT: guard-smoke %s\n", fails == 0 ? "PASS" : "FAIL");
    return fails == 0 ? 0 : 1;
}
