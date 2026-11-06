# The DuetCANMaster boards: everything about DUET_BOARD apart from its MCU's toolchain file, which
# its preset selects. Included by the top-level CMakeLists.txt before the libraries are added,
# because it also sets the options they are built with.
#
# Library configuration:
#   DUET_LIBRARIES    the libraries to build
#   DUET_PART_DEFINE  macro naming the MCU part
#   DUET_RTOS, COREN2G_CAN, COREN2G_USB, COREN2G_SDHC
#
# Firmware:
#   CANMASTER_MCU            the DUET_MCU the board is built for
#   CANMASTER_OUTPUT         image file name, without extension
#   CANMASTER_DEFINES        board macro, which selects the board's configuration headers
#   CANMASTER_LINKER_SCRIPT  linker script, under src/

set(DUET_LIBRARIES
    FreeRTOS RRFLibraries CANlib CoreN2G LibTinyusb LibMbedTls DuetSpiInterface)

# The CoreN2G and library builds are the RepRapFirmware main board's: FreeRTOS, with CAN, USB and
# SD card support
set(DUET_RTOS ON)
set(COREN2G_CAN ON)
set(COREN2G_USB ON)
set(COREN2G_SDHC ON)

if(DUET_BOARD MATCHES "^Duet3_(MB6HC|MB6XD)$")
    set(CANMASTER_MCU SAME70)
    set(DUET_PART_DEFINE __SAME70Q20B__)
    set(CANMASTER_LINKER_SCRIPT Hardware/SAME70/same70q20b_flash.ld)
    set(CANMASTER_OUTPUT DuetCANMaster_${CMAKE_MATCH_1})
    set(CANMASTER_DEFINES DUET3_${CMAKE_MATCH_1})
else()
    message(FATAL_ERROR "Unknown DUET_BOARD '${DUET_BOARD}': configure with a board's preset (cmake --list-presets)")
endif()

if(NOT DUET_MCU STREQUAL CANMASTER_MCU)
    message(FATAL_ERROR "${DUET_BOARD} is a ${CANMASTER_MCU} board, but this build tree is for ${DUET_MCU}: configure it with its preset")
endif()
