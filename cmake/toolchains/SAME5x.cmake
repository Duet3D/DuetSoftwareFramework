# Microchip SAME51/SAME54 (Cortex-M4F): Duet 3 Mini 5+, Expansion 3HC and 1HCL, and other tool boards.

include("${CMAKE_CURRENT_LIST_DIR}/arm-none-eabi.cmake")

# The MCU family every Duet library and firmware in this build tree is compiled for
set(DUET_MCU SAME5x)

duet_set_target_flags(-mcpu=cortex-m4 -mfpu=fpv4-sp-d16 -mfloat-abi=hard)
