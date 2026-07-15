# Shared wire-protocol library: message formats, constants and CRC routines that must stay in sync
# between the SBC side (this project / DuetControlServer) and the device side (DuetCANMaster).
add_library(duet_sbc_protocol STATIC
    src/Crc.cpp
)

target_include_directories(duet_sbc_protocol PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

set_target_properties(duet_sbc_protocol PROPERTIES POSITION_INDEPENDENT_CODE ON)
