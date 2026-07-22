# Cross-compile to Windows x64 using the mingw-w64 toolchain provided by devenv.
# Usage: cmake -S native -B native/build -G Ninja \
#          -DCMAKE_TOOLCHAIN_FILE=$PWD/native/cmake/toolchain-mingw-w64.cmake
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)

set(_triple x86_64-w64-mingw32)
set(CMAKE_C_COMPILER   ${_triple}-gcc)
set(CMAKE_CXX_COMPILER ${_triple}-g++)
set(CMAKE_ASM_COMPILER ${_triple}-gcc)
set(CMAKE_RC_COMPILER  ${_triple}-windres)

# Find host programs on the build host, but libraries/headers only in the target sysroot.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
