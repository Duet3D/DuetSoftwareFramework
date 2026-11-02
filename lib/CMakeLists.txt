# The libraries shared by the firmware projects: those in DUET_LIBRARIES, which the board's
# Boards.cmake sets. Each builds for DUET_MCU (set by the toolchain file) and documents its own
# options at the top of its CMakeLists.txt.

foreach(_lib IN LISTS DUET_LIBRARIES)
    if(_lib STREQUAL "LibMbedTls")
        # LibMbedTls keeps upstream mbedTLS's own CMakeLists.txt at its root
        add_subdirectory(LibMbedTls/duet)
    elseif(_lib STREQUAL "Qfplib")
        add_subdirectory(Qfplib-M0-full)
    elseif(_lib STREQUAL "WiFiSocketServerRTOS")
        # WiFiSocketServerRTOS is the firmware for the WiFi module; the main firmware only shares
        # the definitions of the messages the two exchange.
        add_library(WiFiSocketServerRTOS INTERFACE)
        add_library(Duet::WiFiSocketServerRTOS ALIAS WiFiSocketServerRTOS)
        target_include_directories(WiFiSocketServerRTOS INTERFACE WiFiSocketServerRTOS/src/include)
    elseif(_lib STREQUAL "DuetSpiInterface")
        # The SPI wire protocol DuetCANMaster shares with DuetRealtimeCore, which is header-only
        add_subdirectory(DuetSpiInterface)
        add_library(Duet::DuetSpiInterface ALIAS duet_spi_protocol)
    else()
        add_subdirectory(${_lib})
    endif()
endforeach()
