# SBC-side interface implementation (port of DuetControlServer/Link SPI.cs + LinkService.cs).
#
# Every source under src/ is taken by glob, so adding a file to a module needs no edit here.
# CONFIGURE_DEPENDS makes the build system re-run the glob when the set of matching
# files changes, so a new source is picked up without a manual re-configure.
#
# CApi.cpp is the one exception: it is the C ABI the shared library exports, and is deliberately kept
# out of the static library the harness and the tests link.
#
# Movement/, Compat/ and Motion/ are the second: they make up the duet_motion library below, which
# has its own lint policy and its own explicit source list. Anything added under them is picked up
# there, not here.
set(DUET_MOTION_DIRS "/Movement/" "/Compat/" "/Motion/")

file(GLOB_RECURSE DUET_SBC_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/*.cpp")
list(REMOVE_ITEM DUET_SBC_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/CApi.cpp")
foreach(_dir IN LISTS DUET_MOTION_DIRS)
    list(FILTER DUET_SBC_SOURCES EXCLUDE REGEX "${_dir}")
endforeach()

# The motion engine: RepRapFirmware's Movement/ sources, the Compat/ headers that let them compile
# unmodified against this host, and the Motion/ code written here to replace the parts of the
# firmware that have no counterpart on an SBC.
#
# It is a separate library from duet_sbc for two reasons. Imported upstream source is linted by its
# own project rather than by this one, so clang-tidy is off for the whole target - which would be
# the wrong default for the hand-written code in duet_sbc. And the source list is explicit: a
# recursive glob is what pulled the unported tree into duet_sbc and broke the build, so a file
# compiles here only once someone has ported it and said so.
set(DUET_MOTION_SOURCES
    Compat/Diagnostics.cpp
    Compat/Tasks.cpp
)

add_library(duet_motion STATIC ${DUET_MOTION_SOURCES})
target_include_directories(duet_motion PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/Compat
)
target_link_libraries(duet_motion PUBLIC ${RRFLIBRARIES_TARGET})
target_compile_options(duet_motion PRIVATE
    # float16_t is __fp16 in RRFLibraries' Portability.h, which does not exist on x86-64. See the
    # header for why this is forced in rather than #included.
    -include "${CMAKE_CURRENT_SOURCE_DIR}/Compat/Float16Compat.h"
)
set_target_properties(duet_motion PROPERTIES
    CXX_CLANG_TIDY ""
    POSITION_INDEPENDENT_CODE ON
)

# Static library used by the standalone harness.
add_library(duet_sbc STATIC ${DUET_SBC_SOURCES})
target_include_directories(duet_sbc PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(duet_sbc PUBLIC duet_spi_protocol Threads::Threads)
set_target_properties(duet_sbc PROPERTIES POSITION_INDEPENDENT_CODE ON)

# Shared library exposing the C ABI, for P/Invoke from DuetControlServer.
add_library(duet_sbc_shared SHARED CApi.cpp)
target_include_directories(duet_sbc_shared PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(duet_sbc_shared PUBLIC duet_spi_protocol duet_sbc Threads::Threads)
set_target_properties(duet_sbc_shared PROPERTIES
    OUTPUT_NAME duet_sbc
    POSITION_INDEPENDENT_CODE ON
)
