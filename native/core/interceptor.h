/* inutil's native hook engine — the substrate-AGNOSTIC interceptor core.
 *
 * This is the native core every host adapter reuses: the generic call+post thunk + MinHook + the install
 * primitive + the dispatchers that reverse-P/Invoke into managed. It knows nothing about HOW the runtime was
 * brought up or HOW CoreCLR was embedded — an adapter handles that and hands the core the two managed dispatch
 * fnptrs. The live path: an external loader (BepInEx, MelonLoader, etc.) has already il2cpp_init'd and hosts
 * CoreCLR; the SDK library (managed/Inutil, via LoaderAdapter) [DllImport]s THIS file, resolves the managed
 * dispatchers through the loader's runtime, and calls init() — no core changes; the thin host plugins just
 * reference it. That additivity is the whole point of the split.
 */
#ifndef INUTIL_INTERCEPTOR_H
#define INUTIL_INTERCEPTOR_H

#include <stdint.h>
#include "coreclr_delegates.h"

/* The interceptor frame (mirrors generic_thunk_post.S + the C# CallFrame/RetFrame). `stack` points at
 * the caller's 5th+ args (above the return address) so the managed side can read/write spilled args. */
typedef struct CallFrame { uint64_t gpr[4]; double xmm[4]; uint64_t* stack; } CallFrame;
typedef struct RetFrame  { uint64_t rax; double xmm0; } RetFrame;

/* Managed reverse-P/Invoke targets the thunk dispatches into, keyed by MethodInfo*. The PRE dispatcher
 * returns a SKIP flag (nonzero => a hook called ctx.Skip(): bypass the original and use the return the
 * hook left in RetFrame). It also receives RetFrame so a skipping pre-hook can SET that return — the
 * thunk zeroes RetFrame before the call, so a skip with no SetReturn yields a deterministic zero. */
typedef int  (CORECLR_DELEGATE_CALLTYPE *inutil_pre_fn) (const void* mi, CallFrame* f, RetFrame* r);
typedef void (CORECLR_DELEGATE_CALLTYPE *inutil_post_fn)(const void* mi, CallFrame* a, RetFrame* r);

/* The INVOKE path (for fully-shared __Canon generic methods, intercepted by overwriting
 * MethodInfo::invoker_method rather than detouring a methodPointer). Managed gets the il2cpp invoke
 * frame: the args array + the return out-buffer, keyed by MethodInfo*. The pre dispatcher likewise
 * returns SKIP (nonzero => don't chain the original invoker; the hook filled `ret`). */
typedef int  (CORECLR_DELEGATE_CALLTYPE *inutil_invoke_pre_fn) (const void* mi, void* obj, void** params, void* ret);
typedef void (CORECLR_DELEGATE_CALLTYPE *inutil_invoke_post_fn)(const void* mi, void* obj, void** params, void* ret);

/* The install callback handed to managed: detour `methodPointer` with the generic thunk, keyed by `methodInfo`.
 * Managed decides WHICH methods; the core owns the hook engine.
 *
 * `miSlot` is the POSITIONAL arg index of the call's trailing il2cpp MethodInfo* (this + sret + params): 0-3 = a
 * GPR home slot, 4+ = a caller-stack spill the core reads from CallFrame.stack[miSlot-4]; <0 = unknown. It lets
 * ONE detour at a shared __Canon methodPointer (reference-type generic instantiations fold to one native body)
 * route each instantiation to its own managed key by reading the LIVE MethodInfo* from the frame. */
typedef int (CORECLR_DELEGATE_CALLTYPE *inutil_install_fn)(void* methodPointer, void* methodInfo, int miSlot);

/* The invoke-path install callback handed to managed: overwrite MethodInfo::invoker_method so the
 * shared-generic invoke routes through us, keyed by this MethodInfo*. */
typedef int (CORECLR_DELEGATE_CALLTYPE *inutil_install_invoker_fn)(void* methodInfo);

/* The proceed callback handed to managed (ctx.Proceed()): run the ORIGINAL of the method whose hook is
 * firing on this thread, capturing its return into the RetFrame / out-buffer the managed ctx reads. The
 * core tracks the current frame in thread-local state, so this takes no args. Returns 1 if a frame was
 * live (inside a pre-hook), 0 otherwise. The "around" wrap = a pre-hook that Proceed()s then Skip()s. */
typedef int (CORECLR_DELEGATE_CALLTYPE *inutil_proceed_fn)(void);

/* Vtable-slot install: saves the current VirtualInvokeData.methodPtr at slotAddr, writes a generic
 * thunk closure that dispatches through the same pre/post path as a methodPointer detour (keyed by
 * methodInfo), and returns the saved original pointer (0 on failure). miSlot=-1 always keys by the
 * bound methodInfo* (vtable hooks target a specific class, not a shared __Canon body). Idempotent:
 * if slotAddr is already patched by a prior active install, returns the saved original without re-patching. */
typedef intptr_t (CORECLR_DELEGATE_CALLTYPE *inutil_install_vtable_fn)(void* slotAddr, void* methodInfo, int miSlot);

/* Vtable-slot restore: writes origMethodPtr back into slotAddr (VirtualProtect + FlushInstructionCache)
 * and marks the slot's tracking entry inactive so a future install_vtable call re-patches from scratch. */
typedef void (CORECLR_DELEGATE_CALLTYPE *inutil_restore_vtable_fn)(void* slotAddr, void* origMethodPtr);

/* Bring the hook engine up with the adapter's managed dispatchers (both the methodPointer-detour
 * path and the invoke path). Returns the methodPointer-install callback and, via the out-params, the
 * invoke-install callback, the proceed callback, and the two vtable-slot callbacks (all handed to
 * managed). Returns NULL on failure. Idempotent. */
void* inutil_interceptor_init(inutil_pre_fn pre, inutil_post_fn post,
                              inutil_invoke_pre_fn ipre, inutil_invoke_post_fn ipost,
                              void** out_install_invoker, void** out_proceed,
                              void** out_install_vtable, void** out_restore_vtable);

/* Disable all hooks and tear the engine down. */
void inutil_interceptor_shutdown(void);

/* ---- SEH-guarded execution (fault-tolerant read + call) --------------------------------------
 * A fault the game would take as an unrecoverable c0000005 becomes a RETURN VALUE instead: the guard runs
 * the risky read/call under a Vectored Exception Handler + setjmp recovery, so a bad pointer or a garbage
 * class handed to an il2cpp method is REPORTED, not fatal. (mingw C has no __try/__except; this is the
 * portable equivalent, validated under Wine.) Self-initializing, idempotent, and independent of the hook
 * engine — a caller may use them without inutil_interceptor_init. Call on the thread taking the risk. */

/* Read one pointer-sized value at `addr` into *out. 1 = success, 0 = the read faulted (addr unmapped /
 * unreadable) — this is how the managed IsLiveObject walks a suspect Il2CppObject* / Il2CppClass* safely. */
int inutil_guard_read(const void* addr, void** out);

/* Call il2cpp_runtime_invoke (passed as `invoke_fn`) under the guard. Returns 0 on a clean call (with
 * *out_ret = the boxed result and *out_exc = any managed exception il2cpp returned), or the hardware fault
 * code (e.g. 0xC0000005) if the call faulted, with *out_fault_addr = the faulting address. After a caught
 * fault the runtime is TAINTED (unwound past il2cpp frames) — report and restart, don't keep going. Any
 * out-param may be NULL. */
uint32_t inutil_guarded_invoke(void* invoke_fn, const void* method, void* obj, void** params,
                               void** out_exc, void** out_ret, void** out_fault_addr);

#endif /* INUTIL_INTERCEPTOR_H */
