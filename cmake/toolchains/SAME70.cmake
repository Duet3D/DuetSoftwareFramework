# Microchip SAME70 (Cortex-M7 with double-precision FPU): Duet 3 MB6HC and MB6XD.

include("${CMAKE_CURRENT_LIST_DIR}/arm-none-eabi.cmake")

# The MCU family every Duet library and firmware in this build tree is compiled for
set(DUET_MCU SAME70)

duet_set_target_flags(-mcpu=cortex-m7 -mfpu=fpv5-d16 -mfloat-abi=hard -mno-unaligned-access)
