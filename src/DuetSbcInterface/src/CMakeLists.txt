# The SBC side of the link to the controller, and the motion engine behind it.
#
# Every source under src/ is taken by glob, so adding a file to a module needs no edit here.
# CONFIGURE_DEPENDS makes the build system re-run the glob when the set of matching
# files changes, so a new source is picked up without a manual re-configure.
#
# CApi.cpp is the one exception: it is the C ABI the shared library exports, and is deliberately kept
# out of the static library the harness and the tests link.
#
# The duet_motion sources below are the other exception. They are excluded from the glob by name
# rather than by directory, so that a file belongs to one library or the other because someone said
# so and not because of where it happens to sit.

# The motion engine. The DDA ring, the lookahead and the segment machinery began as
# RepRapFirmware's and keep its algorithms; the rest is written here, for the parts of the firmware
# that have no counterpart on an SBC.
#
# It is a separate library from duet_sbc so that the engine's independence from the interface is
# structural rather than a convention: nothing in it may refer back to the transfer loop, and the
# offline tests rely on that.
set(DUET_MOTION_SOURCES
    Motion/DDA.cpp
    Motion/DDARing.cpp
    Motion/DriveTracker.cpp
    Motion/MotionSystem.cpp
    Motion/MoveSegment.cpp
    Motion/ScheduleMoveBuilder.cpp
    Motion/SegmentBuilder.cpp
    Motion/StepTimer.cpp
    Platform/Log.cpp
    Platform/MemoryArena.cpp
)

file(GLOB_RECURSE DUET_SBC_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/*.cpp")
list(REMOVE_ITEM DUET_SBC_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/CApi.cpp")
foreach(_src IN LISTS DUET_MOTION_SOURCES)
    list(REMOVE_ITEM DUET_SBC_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/${_src}")
endforeach()

add_library(duet_motion STATIC ${DUET_MOTION_SOURCES})
target_include_directories(duet_motion PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
# duet_spi_protocol for the ScheduleMove wire structs the builder fills. It is header-only and has
# no dependency of its own, so this does not tie the motion engine to the SPI transfer loop: the
# packets go wherever the sink the caller installs takes them.
target_link_libraries(duet_motion PUBLIC ${RRFLIBRARIES_TARGET} duet_spi_protocol)
target_compile_options(duet_motion PRIVATE
    # float16_t is __fp16 in RRFLibraries' Portability.h, which does not exist on x86-64. See the
    # header for why this is forced in rather than #included.
    -include "${CMAKE_CURRENT_SOURCE_DIR}/Config/Float16Compat.h"
)
# The engine is linted by the same .clang-tidy as the rest of this project. It used to be exempt on
# the grounds that upstream lints its own sources; the tree has diverged too far for that to be
# true, and the exemption is what let the unreachable code in it accumulate.
set_target_properties(duet_motion PROPERTIES POSITION_INDEPENDENT_CODE ON)

# Static library used by the standalone harness.
add_library(duet_sbc STATIC ${DUET_SBC_SOURCES})
target_include_directories(duet_sbc PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
# duet_motion for the ScheduleMoveSink the interface implements, and for the motion engine the CApi
# exposes. The dependency is one-way: the engine never refers back to the interface.
target_link_libraries(duet_sbc PUBLIC duet_spi_protocol duet_motion Threads::Threads)
set_target_properties(duet_sbc PROPERTIES POSITION_INDEPENDENT_CODE ON)

# Shared library exposing the C ABI, for P/Invoke from DuetControlServer.
add_library(duet_sbc_shared SHARED CApi.cpp)
target_include_directories(duet_sbc_shared PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(duet_sbc_shared PUBLIC duet_spi_protocol duet_sbc Threads::Threads)
set_target_properties(duet_sbc_shared PROPERTIES
    OUTPUT_NAME duet_sbc
    POSITION_INDEPENDENT_CODE ON
)
