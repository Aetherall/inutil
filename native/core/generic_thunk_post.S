/* The call+post thunk (S6). Unlike inutil_thunk_shared, which tail-JMPs to the original
 * and never regains control, this CALLs the original so it can rewrite the RETURN value:
 *
 *   spill args -> zero RetFrame -> pre-dispatch (may rewrite args; returns SKIP in eax) -> reload args
 *              -> [unless SKIP] CALL original (trampoline) -> spill return (RAX + XMM0)
 *              -> post-dispatch (may rewrite return / sret) -> reload return -> ret
 *
 * SKIP (replace-original): pre-dispatch returns nonzero when a hook called ctx.Skip(). We then bypass
 * the original CALL and return whatever the hook left in RetFrame (zeroed before pre-dispatch, which is
 * why pre-dispatch also gets &RetFrame). For an sret return the managed side parks the buffer pointer in
 * RetFrame.RAX, so the reload-return tail is uniform whether or not the original ran.
 *
 * Entry (we stand in for the hooked function):
 *   r11           = HookCtx*            (offset 0 = trampoline-to-original)
 *   rcx,rdx,r8,r9 = integer/pointer args      xmm0..xmm3 = float args
 *   [rsp]         = caller's return address   (rsp % 16 == 8)
 *
 * Frame (0x90, established under a rbp frame so rsp stays 16-aligned at every CALL):
 *   [0x00..0x1F] shadow space for the callees we invoke
 *   [0x20..0x3F] gpr[0..3]  = rcx,rdx,r8,r9     <- CallFrame* (args; gpr[0] = sret ptr if any)
 *   [0x40..0x5F] xmm[0..3]
 *   [0x60]       stack-arg pointer (= rbp+0x30)   <- CallFrame.Stack (the caller's 5th+ args)
 *   [0x68]       saved HookCtx*
 *   [0x70]       return RAX                       <- RetFrame*
 *   [0x78]       return XMM0
 *   [0x80..0x8F] pad
 *
 * >4-arg methods ARE supported: the caller's 5th+ args sit above the return address (at rbp+0x30,
 * a fixed offset), exposed read/write to the hook via CallFrame.Stack. Before CALLing the original
 * we re-stage them (a hook may have rewritten them) into the original's incoming-arg area, then call
 * under a lowered rsp so they land where Win64 expects (8 slots covers every realistic arity).
 */
    .intel_syntax noprefix
    .text
    .globl  inutil_thunk_post
    .def    inutil_thunk_post; .scl 2; .type 32; .endef

inutil_thunk_post:
    push    rbp
    mov     rbp, rsp
    sub     rsp, 0x90
    mov     [rsp+0x20], rcx
    mov     [rsp+0x28], rdx
    mov     [rsp+0x30], r8
    mov     [rsp+0x38], r9
    movsd   [rsp+0x40], xmm0
    movsd   [rsp+0x48], xmm1
    movsd   [rsp+0x50], xmm2
    movsd   [rsp+0x58], xmm3
    lea     rax, [rbp+0x30]            /* the caller's 5th+ args (above retaddr + shadow) */
    mov     [rsp+0x60], rax            /* CallFrame.Stack */
    mov     [rsp+0x68], r11            /* save ctx */
    xor     eax, eax                   /* zero RetFrame: a Skip() with no SetReturn => deterministic 0 */
    mov     [rsp+0x70], rax
    mov     [rsp+0x78], rax

    mov     rcx, r11                   /* pre-dispatch(ctx, &CallFrame, &RetFrame) -> SKIP in eax */
    lea     rdx, [rsp+0x20]
    lea     r8,  [rsp+0x70]
    call    inutil_pre_dispatch
    mov     r11d, eax                  /* stash SKIP (no CALL touches r11 before the test below) */

    mov     rcx, [rsp+0x20]            /* reload (possibly modified) register args */
    mov     rdx, [rsp+0x28]
    mov     r8,  [rsp+0x30]
    mov     r9,  [rsp+0x38]
    movsd   xmm0, [rsp+0x40]
    movsd   xmm1, [rsp+0x48]
    movsd   xmm2, [rsp+0x50]
    movsd   xmm3, [rsp+0x58]

    test    r11d, r11d                 /* a hook called Skip()? then bypass the original entirely */
    jnz     .Lskip_original

    mov     rax, [rsp+0x68]            /* ctx->trampoline (offset 0) */
    mov     rax, [rax]

    sub     rsp, 0x80                  /* fresh shadow + stack-arg area for the original CALL */
    mov     r10, [rbp+0x30]
    mov     [rsp+0x20], r10            /* re-stage 8 caller-stack slots (harmless if fewer real args) */
    mov     r10, [rbp+0x38]
    mov     [rsp+0x28], r10
    mov     r10, [rbp+0x40]
    mov     [rsp+0x30], r10
    mov     r10, [rbp+0x48]
    mov     [rsp+0x38], r10
    mov     r10, [rbp+0x50]
    mov     [rsp+0x40], r10
    mov     r10, [rbp+0x58]
    mov     [rsp+0x48], r10
    mov     r10, [rbp+0x60]
    mov     [rsp+0x50], r10
    mov     r10, [rbp+0x68]
    mov     [rsp+0x58], r10
    call    rax                        /* CALL original; return flows back to us */
    add     rsp, 0x80

    mov     [rsp+0x70], rax            /* spill return value (overwrites the zeroed RetFrame) */
    movsd   [rsp+0x78], xmm0
.Lskip_original:                       /* SKIP lands here: RetFrame already holds the hook's return */

    mov     rcx, [rsp+0x68]            /* post-dispatch(ctx, &CallFrame, &RetFrame) */
    lea     rdx, [rsp+0x20]
    lea     r8,  [rsp+0x70]
    call    inutil_post_dispatch

    mov     rax, [rsp+0x70]            /* reload (possibly modified) return value */
    movsd   xmm0, [rsp+0x78]

    add     rsp, 0x90
    pop     rbp
    ret

/* The INVOKER thunk — the second interception path, for methods whose real entry isn't a
 * detourable methodPointer: fully-shared (__Canon) generic methods, which il2cpp dispatches
 * through MethodInfo::invoker_method (offset 0x10), NOT methodPointer. We overwrite that field
 * with a per-method closure (mov r11, InvokeCtx ; jmp here); il2cpp's Runtime::Invoke then calls
 *   invoker(methodPointer, MethodInfo*, obj, params, ret)        [il2cpp-class-internals.h:225]
 * which lands here. We forward those 5 args (shifted) to the C orchestrator, prepending the ctx:
 *   inutil_invoker_c(ctx, methodPointer, MethodInfo*, obj, params, ret)
 * which fires the managed pre, calls the ORIGINAL invoker (runs the method + fills ret), then post.
 *
 * Entry (we stand in for invoker_method):
 *   r11           = InvokeCtx*           rcx=methodPointer rdx=MethodInfo* r8=obj r9=params
 *   [rsp+0x28]    = ret (5th arg)        [rsp] = return address (rsp % 16 == 8)
 */
    .globl  inutil_invoker_thunk
    .def    inutil_invoker_thunk; .scl 2; .type 32; .endef
inutil_invoker_thunk:
    push    rbp
    mov     rbp, rsp
    mov     rax, [rbp+0x30]            /* ret (incoming 5th arg, at entry_rsp+0x28 = rbp+0x30) */
    sub     rsp, 0x40                  /* 0x20 shadow + 2 stack args, 16-aligned */
    mov     r10, r9                    /* save params before shifting */
    mov     r9,  r8                    /* arg3 = obj    */
    mov     r8,  rdx                   /* arg2 = MethodInfo* */
    mov     rdx, rcx                   /* arg1 = methodPointer */
    mov     rcx, r11                   /* arg0 = InvokeCtx* */
    mov     [rsp+0x20], r10            /* arg4 = params (5th, on stack) */
    mov     [rsp+0x28], rax            /* arg5 = ret    (6th, on stack) */
    call    inutil_invoker_c
    add     rsp, 0x40
    pop     rbp
    ret

/* The PROCEED routine — run the ORIGINAL from inside a hook (the "around" capability). Same re-stage +
 * CALL + spill-return sequence the post-thunk runs after pre-dispatch, but invoked ON DEMAND from managed
 * (ctx.Proceed()) with an explicit CallFrame/RetFrame, so a pre-hook can call the original mid-body, read
 * its result, then Skip() to suppress the thunk's own call. The original is the MinHook trampoline (the
 * un-hooked prologue), so this does NOT re-enter our detour. Args (Win64):
 *   rcx = trampoline (the original's entry)   rdx = CallFrame*   r8 = RetFrame* (filled with rax/xmm0)
 * CallFrame: gpr[4]@0x00, xmm[4]@0x20, stack-ptr@0x40 (the caller's 5th+ args, re-staged like the thunk). */
    .globl  inutil_call_original
    .def    inutil_call_original; .scl 2; .type 32; .endef
inutil_call_original:
    push    rbp
    mov     rbp, rsp
    push    rbx                        /* callee-saved: survive the CALL */
    push    rsi
    push    rdi
    mov     rbx, r8                    /* RetFrame*  */
    mov     rsi, rdx                   /* CallFrame* */
    mov     rdi, rcx                   /* trampoline */

    sub     rsp, 0x88                  /* 0x20 shadow + 8 stack-arg slots (+8 pad => 16-aligned at the CALL) */
    mov     rax, [rsi+0x40]            /* CallFrame.Stack -> the caller's 5th+ args */
    mov     r10, [rax+0x00]; mov [rsp+0x20], r10   /* re-stage 8 slots (harmless if fewer real args) */
    mov     r10, [rax+0x08]; mov [rsp+0x28], r10
    mov     r10, [rax+0x10]; mov [rsp+0x30], r10
    mov     r10, [rax+0x18]; mov [rsp+0x38], r10
    mov     r10, [rax+0x20]; mov [rsp+0x40], r10
    mov     r10, [rax+0x28]; mov [rsp+0x48], r10
    mov     r10, [rax+0x30]; mov [rsp+0x50], r10
    mov     r10, [rax+0x38]; mov [rsp+0x58], r10

    mov     rcx, [rsi+0x00]            /* reload register args from the CallFrame */
    mov     rdx, [rsi+0x08]
    mov     r8,  [rsi+0x10]
    mov     r9,  [rsi+0x18]
    movsd   xmm0, [rsi+0x20]
    movsd   xmm1, [rsi+0x28]
    movsd   xmm2, [rsi+0x30]
    movsd   xmm3, [rsi+0x38]
    call    rdi                        /* CALL original; return flows back here */
    add     rsp, 0x88

    mov     [rbx+0x00], rax            /* RetFrame.rax  */
    movsd   [rbx+0x08], xmm0           /* RetFrame.xmm0 */
    pop     rdi
    pop     rsi
    pop     rbx
    pop     rbp
    ret
