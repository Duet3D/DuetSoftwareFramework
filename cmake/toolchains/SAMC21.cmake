# Microchip SAMC21 (Cortex-M0+, no FPU): Duet 3 Tool 1LC, Expansion 1XD and other small tool boards.

include("${CMAKE_CURRENT_LIST_DIR}/arm-none-eabi.cmake")

# The MCU family every Duet library and firmware in this build tree is compiled for
set(DUET_MCU SAMC21)

duet_set_target_flags(-mcpu=cortex-m0plus)
