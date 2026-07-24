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

# Optional Bookworm sysroot for a glibc-matched dynamic build (mainly for the .so). An empty value
# means "none", so the arm64 preset can pin it off and never inherit one from an earlier build in
# the same tree. A non-empty one that is not there is always a mistake - the pi-arm64 preset
# names a path that only exists once fetch-pi-sysroot.sh has run - and silently ignoring it would
# produce a .so that fails to load on the Pi, so stop instead.
if(DUET_SBC_SYSROOT)
    if(NOT IS_DIRECTORY "${DUET_SBC_SYSROOT}")
        message(FATAL_ERROR
            "DUET_SBC_SYSROOT is set to '${DUET_SBC_SYSROOT}', which is not a directory.\n"
            "Fetch one from a running Pi with:\n"
            "  scripts/fetch-pi-sysroot.sh <user>@<pi-host> ${CMAKE_CURRENT_LIST_DIR}/../pi-sysroot\n"
            "or configure with the arm64 preset to compile against this container's aarch64 "
            "libraries instead (the resulting libduet_sbc.so is not loadable on the Pi).")
    endif()
    set(CMAKE_SYSROOT "${DUET_SBC_SYSROOT}")
    set(CMAKE_FIND_ROOT_PATH "${DUET_SBC_SYSROOT}")
endif()

# Look for programs on the host, but libraries/headers/packages in the target sysroot only.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
