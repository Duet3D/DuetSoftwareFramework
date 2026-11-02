# The Duet3D firmware projects - DuetCANMaster, RepRapFirmware, Duet3Expansion, Duet3Bootloader and
# DuetIAP - and the libraries they share. The SBC side (DuetRealtimeCore and the dotnet projects) is
# built separately; scripts/build.py builds both.
#
# A build tree builds the project DUET_PROJECT for one hardware board, DUET_BOARD. Each build has a
# preset (cmake --list-presets), which picks them, the MCU's toolchain file from cmake/toolchains,
# and the cache variables that select a variant of the board, which the project's Boards.cmake
# declares (such as RRF_CAN0 or IAP_SBC):
#     cmake --preset EXP3HC
#     cmake --build --preset EXP3HC
# or, for one or more boards or a whole project, scripts/build.py EXP3HC
# Each board's -debug preset builds it with debug symbols and reduced optimisation.

cmake_minimum_required(VERSION 3.25)

# The firmware's optimised builds keep their assertions (no NDEBUG), and Debug builds define DEBUG.
# Set before project() so these replace CMake's defaults instead of being initialised from them.
foreach(_lang C CXX ASM)
    set(CMAKE_${_lang}_FLAGS_RELEASE "-O2"             CACHE STRING "Flags used by the ${_lang} compiler during Release builds")
    set(CMAKE_${_lang}_FLAGS_DEBUG   "-Og -g3 -DDEBUG" CACHE STRING "Flags used by the ${_lang} compiler during Debug builds")
endforeach()

project(DuetFirmware C CXX ASM)

# Compile through ccache if it is installed. Its base directory makes the paths under the source
# tree relative, so that the build trees of boards that build a library the same way share its
# objects. It is set through the environment because the ccache in Debian Bookworm (4.7) predates
# passing settings on its command line.
find_program(CCACHE ccache)
option(DUET_CCACHE "Compile through ccache, if it is installed" ON)
if(DUET_CCACHE AND CCACHE)
    foreach(_lang C CXX ASM)
        set(CMAKE_${_lang}_COMPILER_LAUNCHER
            "${CMAKE_COMMAND}" -E env "CCACHE_BASEDIR=${CMAKE_SOURCE_DIR}" "${CCACHE}")
    endforeach()
endif()
option(DUET_RTL_DUMPS "Write the libraries' RTL (-fdump-rtl-expand), for call graph and stack analysis" OFF)
option(DUET_DISASSEMBLY "Write each image's disassembly (<name>.lst), for interpreting software reset data" OFF)
option(DUET_DISASSEMBLY_LABELS "Label the branch targets within functions in the disassembly" ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Release or Debug" FORCE)
endif()

set(DUET_PROJECT "" CACHE STRING "Project this build tree builds: DuetCANMaster, RepRapFirmware, Duet3Expansion, Duet3Bootloader or DuetIAP")
set(DUET_BOARD "" CACHE STRING "Board this build tree builds")
if(NOT DUET_PROJECT OR NOT DUET_BOARD OR NOT DEFINED DUET_MCU)
    message(FATAL_ERROR "Configure with a board's preset (cmake --list-presets)")
endif()

include(cmake/DuetFirmware.cmake)
include(cmake/ClangTidy.cmake)

# The board decides how the libraries are built, so its definition comes first
include(src/${DUET_PROJECT}/Boards.cmake)

add_subdirectory(lib)
add_subdirectory(src/${DUET_PROJECT})

if(DUET_CCACHE AND CCACHE)
    duet_relative_prefix_maps(${CMAKE_CURRENT_SOURCE_DIR})
endif()
duet_clang_tidy(${CMAKE_CURRENT_SOURCE_DIR})
