cmake_minimum_required(VERSION 3.16)
project(DuetSbcInterface LANGUAGES CXX)

# Standalone SBC-side replica of the DuetControlServer SPI interface, used to measure SPI transfer
# jitter without the .NET runtime. Builds:
#   - duet_spi_protocol  : shared wire-protocol library (also intended for the device side)
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

# Lint every translation unit as it is compiled: CMAKE_CXX_CLANG_TIDY runs clang-tidy right after
# the compiler for the same source, so a finding stops the build at the file that caused it. Every
# check left enabled in .clang-tidy is an error (--warnings-as-errors=*).
#
# Unlike the firmware, this is an ordinary hosted build - clang can consume the GCC command line as
# it stands, including the aarch64/armhf cross-compiler ones (it takes the target from the compiler
# CMake names in the command). The setting is inherited by every target added below;
# duet_spi_protocol is header-only, so it compiles nothing of its own.
#
# Turn it off with -DDUET_SBC_CLANG_TIDY=OFF for a plain, faster compile.
option(DUET_SBC_CLANG_TIDY "Run clang-tidy on the sources during the build" ON)
if(DUET_SBC_CLANG_TIDY)
    find_program(CLANG_TIDY_EXE NAMES clang-tidy DOC "clang-tidy executable used during the build")
    if(CLANG_TIDY_EXE)
        set(CMAKE_CXX_CLANG_TIDY "${CLANG_TIDY_EXE};--warnings-as-errors=*;--quiet")
    else()
        message(WARNING "DUET_SBC_CLANG_TIDY is ON but clang-tidy was not found; not linting")
    endif()
endif()

# Statically link the standalone harness so it runs on the Pi regardless of the target glibc version
# (the devcontainer cross toolchain targets a newer glibc than Raspberry Pi OS Bookworm). Defaults on
# when cross-compiling. The C-ABI shared library is never affected (a .so must link glibc dynamically).
if(CMAKE_CROSSCOMPILING AND NOT DEFINED DUET_SBC_STATIC)
    set(DUET_SBC_STATIC ON)
endif()
option(DUET_SBC_STATIC "Statically link the standalone harness binary" ${DUET_SBC_STATIC})

find_package(Threads REQUIRED)

# The wire protocol lives outside this project because DuetCANMaster consumes the same headers.
# DUET_LIBRARIES_DIR mirrors the LIBRARIES_DIR variable DuetCANMaster's Makefile uses.
set(DUET_LIBRARIES_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../lib" CACHE PATH
    "Path to the shared Duet libraries directory")
add_subdirectory("${DUET_LIBRARIES_DIR}/DuetSpiInterface" duet_spi_protocol)

add_subdirectory(sbc)
add_subdirectory(harness)

# Host-side unit tests (no hardware required). Skipped when cross-compiling, since the resulting
# binaries could not be executed here anyway.
if(NOT CMAKE_CROSSCOMPILING)
    enable_testing()
    add_subdirectory(tests)
endif()
