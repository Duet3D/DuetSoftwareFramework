# Shared SPI wire-protocol library: message formats, constants and CRC routines that must stay in
# sync between the SBC side (DuetSbcInterface / DuetControlServer) and the device side
# (DuetCANMaster). See README.md for how each side consumes it.
#
# The device side is built by DuetCANMaster's Makefile rather than CMake, and only consumes the
# headers, so this target exists purely for the SBC side.
cmake_minimum_required(VERSION 3.16)
project(DuetSpiInterface LANGUAGES CXX)

add_library(duet_spi_protocol STATIC
    src/Crc.cpp
)

target_include_directories(duet_spi_protocol PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

set_target_properties(duet_spi_protocol PROPERTIES POSITION_INDEPENDENT_CODE ON)
