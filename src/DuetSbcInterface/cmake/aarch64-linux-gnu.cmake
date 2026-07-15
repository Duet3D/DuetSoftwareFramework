# CMake toolchain file for cross-compiling to 64-bit Raspberry Pi OS (aarch64 Linux).
#
# The devcontainer's cross toolchain (Ubuntu 24.04) targets glibc 2.39, while Raspberry Pi OS
# Bookworm ships glibc 2.36. A *dynamically* linked binary built here would therefore fail on the Pi
# with "GLIBC_2.3x not found". To keep the standalone jitter-test binary portable across glibc
# versions we link it statically by default (see DUET_SBC_STATIC in the top-level CMakeLists).
#
# The shared library for P/Invoke (libduet_sbc.so) cannot be statically self-contained; to produce a
# Bookworm-compatible .so, build on the Pi or point this toolchain at a Bookworm sysroot via
# -DDUET_SBC_SYSROOT=/path/to/pi-sysroot (see scripts/fetch-pi-sysroot.sh).
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)

set(CMAKE_C_COMPILER aarch64-linux-gnu-gcc)
set(CMAKE_CXX_COMPILER aarch64-linux-gnu-g++)

# Optional Bookworm sysroot for a glibc-matched dynamic build (mainly for the .so).
if(DEFINED DUET_SBC_SYSROOT)
    set(CMAKE_SYSROOT "${DUET_SBC_SYSROOT}")
    set(CMAKE_FIND_ROOT_PATH "${DUET_SBC_SYSROOT}")
endif()

# Look for programs on the host, but libraries/headers/packages in the target sysroot only.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
