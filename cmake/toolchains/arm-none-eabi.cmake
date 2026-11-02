# The bare-metal Arm GNU toolchain. The per-MCU toolchain files alongside this one include it and
# add the code generation flags for their core; select one of those, not this file.
#
# The compilers are found on PATH unless CROSS_COMPILE (a cache variable, or failing that an
# environment variable) names them, e.g.
#     -DCROSS_COMPILE=/opt/arm-gnu-toolchain-15.2.rel1/bin/arm-none-eabi-

set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_SYSTEM_PROCESSOR arm)

if(DEFINED ENV{CROSS_COMPILE})
    set(_cross_compile "$ENV{CROSS_COMPILE}")
else()
    set(_cross_compile "arm-none-eabi-")
endif()
set(CROSS_COMPILE "${_cross_compile}" CACHE STRING
    "Prefix of the cross toolchain executables, including the trailing dash")

set(CMAKE_C_COMPILER   "${CROSS_COMPILE}gcc")
set(CMAKE_CXX_COMPILER "${CROSS_COMPILE}g++")
set(CMAKE_ASM_COMPILER "${CROSS_COMPILE}gcc")
set(CMAKE_OBJCOPY      "${CROSS_COMPILE}objcopy" CACHE FILEPATH "objcopy for the cross toolchain")
set(CMAKE_SIZE         "${CROSS_COMPILE}size"    CACHE FILEPATH "size for the cross toolchain")
set(CMAKE_OBJDUMP      "${CROSS_COMPILE}objdump" CACHE FILEPATH "objdump for the cross toolchain")

# There is no C library to link a test executable against without a linker script, so the
# compiler checks stop at a static library.
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)

set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)

# CMake only predefines the RESCAN link group ($<LINK_GROUP:RESCAN,...>) for platforms it knows to
# use GNU ld. The firmware needs it because its libraries call into each other.
foreach(_lang C CXX)
    set(CMAKE_${_lang}_LINK_GROUP_USING_RESCAN_SUPPORTED TRUE)
    set(CMAKE_${_lang}_LINK_GROUP_USING_RESCAN "LINKER:--start-group" "LINKER:--end-group")
endforeach()

# Code generation shared by every Duet MCU, appended to the core flags of the including file.
function(duet_set_target_flags)
    list(JOIN ARGN " " _flags)
    string(APPEND _flags " -mthumb -mfp16-format=ieee -ffunction-sections -fdata-sections")
    foreach(_lang C CXX ASM)
        set(CMAKE_${_lang}_FLAGS_INIT "${_flags}" PARENT_SCOPE)
    endforeach()
endfunction()
