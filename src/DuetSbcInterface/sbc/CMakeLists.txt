# SBC-side interface implementation (port of DuetControlServer/Link SPI.cs + LinkService.cs).
set(DUET_SBC_SOURCES
    src/ProcessHelpers.cpp
    src/SpiDevice.cpp
    src/GpioInputPin.cpp
    src/OutputGpioPin.cpp
    src/SbcTransfer.cpp
    src/SbcInterface.cpp
)

# Static library used by the standalone harness.
add_library(duet_sbc STATIC ${DUET_SBC_SOURCES})
target_include_directories(duet_sbc PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(duet_sbc PUBLIC duet_spi_protocol Threads::Threads)
set_target_properties(duet_sbc PROPERTIES POSITION_INDEPENDENT_CODE ON)

# Shared library exposing the C ABI, for P/Invoke from DuetControlServer.
add_library(duet_sbc_shared SHARED ${DUET_SBC_SOURCES} src/CApi.cpp)
target_include_directories(duet_sbc_shared PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(duet_sbc_shared PUBLIC duet_spi_protocol Threads::Threads)
set_target_properties(duet_sbc_shared PROPERTIES
    OUTPUT_NAME duet_sbc
    POSITION_INDEPENDENT_CODE ON
)
