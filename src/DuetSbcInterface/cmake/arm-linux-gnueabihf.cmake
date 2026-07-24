# CMake toolchain file for cross-compiling to 32-bit Raspberry Pi OS (armhf Linux).
#
# Mirrors aarch64-linux-gnu.cmake; see that file for the glibc caveat. In short: a dynamically linked
# .so built with the devcontainer toolchain requires the container's (newer) glibc, so to produce a
# Bookworm-compatible libduet_sbc.so either build on the Pi or point this toolchain at a Bookworm
# sysroot via -DDUET_SBC_SYSROOT=/path/to/pi-sysroot (see scripts/fetch-pi-sysroot.sh).
#
# The toolchain itself comes from crossbuild-essential-armhf (see scripts/install-arm-gcc.sh).
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER arm-linux-gnueabihf-gcc)
set(CMAKE_CXX_COMPILER arm-linux-gnueabihf-g++)

# Optional Bookworm sysroot for a glibc-matched dynamic build (mainly for the .so). Empty means
# "none"; a non-empty path that is not there is an error rather than something to ignore. See
# aarch64-linux-gnu.cmake for the reasoning.
if(DUET_SBC_SYSROOT)
    if(NOT IS_DIRECTORY "${DUET_SBC_SYSROOT}")
        message(FATAL_ERROR
            "DUET_SBC_SYSROOT is set to '${DUET_SBC_SYSROOT}', which is not a directory.\n"
            "Fetch one from a running Pi with:\n"
            "  scripts/fetch-pi-sysroot.sh <user>@<pi-host> ${CMAKE_CURRENT_LIST_DIR}/../pi-sysroot")
    endif()
    set(CMAKE_SYSROOT "${DUET_SBC_SYSROOT}")
    set(CMAKE_FIND_ROOT_PATH "${DUET_SBC_SYSROOT}")
endif()

# Look for programs on the host, but libraries/headers/packages in the target sysroot only.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
