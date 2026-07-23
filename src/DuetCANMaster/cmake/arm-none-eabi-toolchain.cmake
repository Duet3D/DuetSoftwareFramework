# Cross-compilation toolchain for the bare-metal SAME70 firmware and its libraries.
# Select with -DCMAKE_TOOLCHAIN_FILE=cmake/arm-none-eabi-toolchain.cmake (the CMakePresets do this).
#
# CROSS_COMPILE (the prefix of the arm-none-eabi-* executables, including the trailing dash) is
# resolved from, in order: a -DCROSS_COMPILE on the command line, then the toolchain sitting next
# to the repository, matching the layout the old Makefiles assumed.

set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR arm)

if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
    set(_host_os "macos")
else()
    set(_host_os "linux")
endif()
if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
    set(_host_arch "aarch64")
else()
    set(_host_arch "x86_64")
endif()

set(CROSS_COMPILE
    "$ENV{ARM_GNU_TOOLCHAIN_DIR}/bin/arm-none-eabi-"
    CACHE STRING "Prefix (including trailing dash) of the arm-none-eabi cross-compiler executables")
message("CROSS_COMPILE prefix = ${CROSS_COMPILE}")
get_filename_component(CROSS_COMPILE "${CROSS_COMPILE}" ABSOLUTE)

set(CMAKE_C_COMPILER   "${CROSS_COMPILE}gcc")
set(CMAKE_CXX_COMPILER "${CROSS_COMPILE}g++")
set(CMAKE_ASM_COMPILER "${CROSS_COMPILE}gcc")
set(CMAKE_OBJCOPY      "${CROSS_COMPILE}objcopy" CACHE FILEPATH "")
set(CMAKE_SIZE         "${CROSS_COMPILE}size"    CACHE FILEPATH "")

# The firmware links only against its own libraries and a bare-metal linker script, so CMake's
# compiler probe cannot produce a runnable executable; stop it at a static library instead.
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)

# CMake auto-defines the RESCAN link group ($<LINK_GROUP:RESCAN,...> -> ld --start-group/--end-group)
# only for recognised GNU-like platforms; on a Generic system it must be declared explicitly. The
# firmware needs it because its static libraries reference each other cyclically.
foreach(_lang C CXX)
    set(CMAKE_${_lang}_LINK_GROUP_USING_RESCAN_SUPPORTED TRUE)
    set(CMAKE_${_lang}_LINK_GROUP_USING_RESCAN "LINKER:--start-group" "LINKER:--end-group")
endforeach()

# CrcAppender, which stamps the CRC onto each firmware image, is a native host tool shipped in the
# firmware repo - not part of the cross toolchain.
set(CRC_APPENDER_DIR "${CMAKE_CURRENT_LIST_DIR}/../Tools/CrcAppender/${_host_os}-${_host_arch}"
    CACHE PATH "Directory containing the host CrcAppender binary")
