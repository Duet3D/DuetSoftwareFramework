# CMake toolchain file for cross-compiling to 64-bit Raspberry Pi OS (aarch64 Linux).
#
# The devcontainer is based on Debian Bookworm, so its cross toolchain targets the same glibc 2.36
# as Raspberry Pi OS Bookworm and a plain cross build produces a loadable libduet_sbc.so. No sysroot
# is needed for that target; the standalone jitter-test binary is still linked statically by default
# (see DUET_SBC_STATIC in the top-level CMakeLists) so it runs on any Pi OS release.
#
# A sysroot is only required to target something *older* than the container's glibc - an earlier Pi
# OS release, say. Point this toolchain at one with -DDUET_SBC_SYSROOT=/path/to/sysroot; fetch one
# from a running Pi with scripts/fetch-pi-sysroot.sh.
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)

set(CMAKE_C_COMPILER aarch64-linux-gnu-gcc)
set(CMAKE_CXX_COMPILER aarch64-linux-gnu-g++)

# Optional sysroot for targeting an older glibc than the container's. An empty value means "none",
# so the arm64 preset can pin it off and never inherit one from an earlier build in the same tree. A
# non-empty one that is not there is always a mistake - the arm64-sysroot preset names a path that only
# exists once fetch-pi-sysroot.sh has run - and silently ignoring it would produce a .so linked
# against the wrong glibc, so stop instead.
if(DUET_SBC_SYSROOT)
    if(NOT IS_DIRECTORY "${DUET_SBC_SYSROOT}")
        message(FATAL_ERROR
            "DUET_SBC_SYSROOT is set to '${DUET_SBC_SYSROOT}', which is not a directory.\n"
            "Fetch one from a running Pi with:\n"
            "  scripts/fetch-pi-sysroot.sh <user>@<pi-host> ${CMAKE_CURRENT_LIST_DIR}/../pi-sysroot\n"
            "or configure with the arm64 preset, which needs no sysroot and produces a "
            "libduet_sbc.so loadable on Raspberry Pi OS Bookworm.")
    endif()
    set(CMAKE_SYSROOT "${DUET_SBC_SYSROOT}")
    set(CMAKE_FIND_ROOT_PATH "${DUET_SBC_SYSROOT}")
endif()

# Look for programs on the host, but libraries/headers/packages in the target sysroot only.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
