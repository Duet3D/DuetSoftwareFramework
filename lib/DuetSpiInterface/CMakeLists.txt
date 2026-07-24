# Shared SPI wire-protocol definitions: message formats and constants that must stay in sync between
# the SBC side (DuetSbcInterface / DuetControlServer) and the device side (DuetCANMaster).
# See README.md for how each side consumes it.
#
# Header-only: the protocol is pure declarations, and each side brings its own CRC implementation
# (they compute identical values, but the firmware's is tuned for its hardware). The device side is
# built by DuetCANMaster's Makefile rather than CMake, so this target exists purely for the SBC side.
cmake_minimum_required(VERSION 3.16)
project(DuetSpiInterface LANGUAGES CXX)

add_library(duet_spi_protocol INTERFACE)

target_include_directories(duet_spi_protocol INTERFACE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)
