# SBC-side interface implementation (port of DuetControlServer/Link SPI.cs + LinkService.cs).
#
# Every source under src/ is taken by glob, so adding a file to a module needs no edit here.
# CONFIGURE_DEPENDS makes the build system re-run the glob when the set of matching
# files changes, so a new source is picked up without a manual re-configure.
#
# CApi.cpp is the one exception: it is the C ABI the shared library exports, and is deliberately kept
# out of the static library the harness and the tests link.
file(GLOB_RECURSE DUET_SBC_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/*.cpp")
list(REMOVE_ITEM DUET_SBC_SOURCES "${CMAKE_CURRENT_SOURCE_DIR}/CApi.cpp")

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
