# ST STM32H5 (Cortex-M33 with single-precision FPU).

include("${CMAKE_CURRENT_LIST_DIR}/arm-none-eabi.cmake")

# The MCU family every Duet library and firmware in this build tree is compiled for
set(DUET_MCU STM32H5)

duet_set_target_flags(-mcpu=cortex-m33 -mfpu=fpv5-sp-d16 -mfloat-abi=hard)
