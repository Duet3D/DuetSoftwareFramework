# CMake toolchain file for cross-compiling to 64-bit Raspberry Pi OS (aarch64 Linux).
#
# The devcontainer is based on Debian Bookworm, so its cross toolchain targets the same glibc 2.36
# as Raspberry Pi OS Bookworm and a plain cross build produces a loadable libduet_realtime_core.so. The
# standalone jitter-test binary is linked statically by default (see DUET_REALTIME_CORE_STATIC in the
# top-level CMakeLists) so it runs on any Pi OS release.
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)

set(CMAKE_C_COMPILER aarch64-linux-gnu-gcc)
set(CMAKE_CXX_COMPILER aarch64-linux-gnu-g++)

# Look for programs on the host, but libraries/headers/packages for the target only.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
