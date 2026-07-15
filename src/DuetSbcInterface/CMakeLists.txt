cmake_minimum_required(VERSION 3.16)
project(DuetSbcInterface LANGUAGES CXX)

# Standalone SBC-side replica of the DuetControlServer SPI interface, used to measure SPI transfer
# jitter without the .NET runtime. Builds:
#   - duet_sbc_protocol  : shared wire-protocol library (also intended for the device side)
#   - duet_sbc           : the SBC interface implementation (static)
#   - duet_sbc_shared    : the same, as a .so exposing a C ABI for P/Invoke from DCS
#   - sbc_jitter_test    : the standalone jitter test harness
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# Release-with-debuginfo is a good default for a latency test: optimised but still profileable.
set(CMAKE_CXX_FLAGS_RELEASE "-O2 -g")

add_compile_options(-Wall -Wextra)

# Statically link the standalone harness so it runs on the Pi regardless of the target glibc version
# (the devcontainer cross toolchain targets a newer glibc than Raspberry Pi OS Bookworm). Defaults on
# when cross-compiling. The C-ABI shared library is never affected (a .so must link glibc dynamically).
if(CMAKE_CROSSCOMPILING AND NOT DEFINED DUET_SBC_STATIC)
    set(DUET_SBC_STATIC ON)
endif()
option(DUET_SBC_STATIC "Statically link the standalone harness binary" ${DUET_SBC_STATIC})

find_package(Threads REQUIRED)

add_subdirectory(protocol)
add_subdirectory(sbc)
add_subdirectory(harness)
