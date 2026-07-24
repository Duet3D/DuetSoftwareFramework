# RepRapFirmware for the DuetCANMaster boards (Duet 3 MB6HC / MB6XD, SAME70).
#
# Configure and build with the bundled presets (recommended, and what the VS Code CMake Tools
# extension uses):
#     cmake --preset default
#     cmake --build --preset Duet3_MB6HC
#     cmake --build --preset Duet3_MB6XD
#
# or drive it directly:
#     cmake -S . -B build --toolchain cmake/arm-none-eabi-toolchain.cmake
#     cmake --build build
#
# Each first-party source is linted by clang-tidy as it is compiled, with every check left enabled
# in .clang-tidy treated as an error; see cmake/ClangTidy.cmake, and pass -DDUET_CLANG_TIDY=OFF for
# a plain compile. scripts/gen-compile-commands.sh remains the way to run clang-tidy over the whole
# tree at once (and with --fix).
#
# Every board is one add_executable() defined via duet_add_firmware() (see cmake/DuetFirmware.cmake).
# The shared Duet3D libraries are compiled from source into this build tree, so a Debug and a
# Release configuration - or DuetCANMaster and Duet3Expansion - never share object files.

cmake_minimum_required(VERSION 3.24)  # LINK_GROUP generator expression

project(DuetCANMaster C CXX ASM)

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(LIBRARIES_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../lib" CACHE PATH
    "Path to the shared Duet3D libraries (CoreN2G, FreeRTOS, RRFLibraries, CANlib, LibTinyusb, LibMbedTls)")
get_filename_component(LIBRARIES_DIR "${LIBRARIES_DIR}" ABSOLUTE)

include(cmake/DuetFirmware.cmake)

duet_add_firmware(Duet3Firmware_MB6HC
    MCU SAME70
    DEFINE DUET3_MB6HC
    LINKER_SCRIPT Hardware/SAME70/same70q20b_flash.ld)

duet_add_firmware(Duet3Firmware_MB6XD
    MCU SAME70
    DEFINE DUET3_MB6XD
    LINKER_SCRIPT Hardware/SAME70/same70q20b_flash.ld)
