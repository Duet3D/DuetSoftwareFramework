# Commands for SEGGER's J-Link GDB Server, which the launch configurations and the Restart Firmware
# task source when it is the GDB server picked. Each GDB server script defines the same commands

# Reset the MCU and halt it at its reset vector, before the bootloader runs. J-Link's reset halts it
define duet-reset-halt
    monitor reset
    # GDB does not see the reset, so it reads the registers again
    maintenance flush register-cache
end

# Reset the MCU and let it run, for the Restart Firmware task
define duet-reset-run
    monitor reset
    monitor go
end
