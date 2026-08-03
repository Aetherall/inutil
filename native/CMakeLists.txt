cmake_minimum_required(VERSION 3.20)
project(inutil_native C ASM)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# --- MinHook (vendored) : inline x64 detour primitive for the hook engine -------
add_library(minhook STATIC
  third_party/minhook/src/buffer.c
  third_party/minhook/src/hook.c
  third_party/minhook/src/trampoline.c
  third_party/minhook/src/hde/hde64.c)
target_include_directories(minhook
  PUBLIC  third_party/minhook/include
  PRIVATE third_party/minhook/src)

# --- The interceptor core, built as a LOADABLE DLL (§7.7) -----------------------
# The whole hook engine: the call+post asm thunk (core/generic_thunk_post.S — re-stage
# args, CALL the original, spill the RetFrame so a post-hook can rewrite RAX/XMM0/sret,
# plus inutil_call_original for ctx.Proceed()), MinHook detours, the `install` /
# `install_invoker` / vtable primitives, and the pre/post dispatchers that reverse-P/Invoke
# into managed. It is substrate-agnostic: the host supplies the two managed dispatchers via
# inutil_interceptor_init(); it knows nothing about how the runtime/CoreCLR were brought up.
# v2 additions on the ported core (see docs/contribution/architecture/13-hook-engine.md): the shared-__Canon ABI-
# completeness fix (dispatch_key reads a stack-spilled trailing MethodInfo*, not just GPR
# slots 0-3) and the two no-partial-commit install fixes (MH_EnableHook + vtable).
#
# The SDK library (managed/src/Inutil, referenced by the host loader plugins) pulls it in via
# [DllImport]: the loader is the host and our code is a managed assembly it loads, so the core
# must be a loadable DLL rather than linked into a host EXE.
# --export-all-symbols keeps interceptor.{c,h} byte-for-byte unannotated (no dllexport) — the
# additivity claim, enforced by the build; -static-libgcc makes the DLL self-contained so
# .NET's [DllImport] resolves it with no mingw runtime DLLs on the path.
add_library(inutil_core SHARED core/interceptor.c core/generic_thunk_post.S)
target_link_libraries(inutil_core PRIVATE minhook)
target_include_directories(inutil_core PRIVATE core third_party/dotnet)
target_link_options(inutil_core PRIVATE -Wl,--export-all-symbols -static-libgcc)
set_target_properties(inutil_core PROPERTIES PREFIX "" OUTPUT_NAME "inutil_core")
target_compile_options(inutil_core PRIVATE -O0)

# --- x64 unwind-data gate -------------------------------------------------------
# The asm thunks stand between the game's caller and the hooked original, and il2cpp raises managed
# exceptions as MSVC C++ EH, which unwinds ONLY through .pdata/.xdata. A proc without .seh_* directives
# gets no entry, so RtlLookupFunctionEntry misses it, the unwinder falls back to the leaf assumption and
# walks into garbage — a game-side try/catch around a hooked call silently stops working. Gate the BUILD
# on it rather than a test: the check reads the proc list out of the .S itself, so a newly added thunk
# that forgets its annotations fails on the build that adds it.
find_package(Python3 COMPONENTS Interpreter QUIET)
if(Python3_Interpreter_FOUND)
  add_custom_command(TARGET inutil_core POST_BUILD
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/cmake/check-unwind.py
            ${CMAKE_OBJDUMP} $<TARGET_FILE:inutil_core>
            ${CMAKE_CURRENT_SOURCE_DIR}/core/generic_thunk_post.S
    VERBATIM)
else()
  message(WARNING "python3 not found — skipping the x64 unwind-data gate on the asm thunks")
endif()
