using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DuetEmulation.Boards;

/// <summary>The boards the bench can emulate</summary>
public static class BoardTypes
{
    private static readonly string[] CommonModels = ["Common/CanBusLogger.cs", "Common/DuetMCAN.cs", "Common/DuetIoTrace.cs", "Common/DuetThermalModel.cs"];

    private static readonly string[] Same70Models =
    [
        "SAME70/SAME70_TimerCounter.cs", "SAME70/SAME70_ParallelIO.cs", "SAME70/SAME70_AnalogFrontEnd.cs",
        "SAME70/SAME70_Xdmac.cs", "SAME70/SAME70_Hsmci.cs", "SAME70/SAME70_ResetController.cs", "SAME70/SAME70_UsartSpi.cs",
        "SAME70/SAME70_SbcSpi.cs"
    ];

    private static readonly string[] Same5xModels =
    [
        "SAME5x/SAME5x_RegisterFile.cs", "SAME5x/SAME5x_Gclk.cs", "SAME5x/SAME5x_Port.cs", "SAME5x/SAME5x_Eic.cs", "SAME5x/SAME5x_TimerCounter.cs",
        "SAME5x/SAME5x_Tcc.cs", "SAME5x/SAME5x_Dmac.cs", "SAME5x/SAME5x_Adc.cs"
    ];

    private static string Same5xSvd(string part) =>
        Path.Combine(RepositoryPaths.Root, "lib", "CoreN2G", "src", "atmel", "SAME51_DFP", "1.1.139", "svd", part + ".svd");

    private static string Same70Svd => Path.Combine(RepositoryPaths.InstalledSvds, "ATSAME70Q21.svd");

    /// <summary>The Duet 3 MB6HC running RepRapFirmware standalone, with its console on Aux2 and an SD card</summary>
    public static readonly BoardType Mb6hcStandalone = new()
    {
        Name = "MB6HC",
        FirmwarePreset = "Duet3Firmware_MB6HC",
        Mcu = McuFamily.Same70,
        Platform = "Duet3_MB6HC.repl",
        Models = [.. CommonModels, .. Same70Models],
        Svds = new Dictionary<string, string> { ["svd_same70"] = Same70Svd },
        ApplicationAddress = 0x400000,
        CanController = "mcan1",
        ConfigurationHeader = "src/RepRapFirmware/src/Config/Pins_Duet3_MB6HC.h",
        StepPinsArray = "STEP_PINS",
        DirectionPinsArray = "DIRECTION_PINS",
        ConsoleUart = "usart2",
        SdCardController = "hsmci"
    };

    /// <summary>The Duet 3 MB6HC running DuetCANMaster, connected to DuetControlServer over its SBC SPI port</summary>
    public static readonly BoardType Mb6hcCanMaster = new()
    {
        Name = "MB6HC",
        FirmwarePreset = "DuetCANMaster_MB6HC",
        Mcu = McuFamily.Same70,
        Platform = "Duet3_MB6HC.repl",
        Models = [.. CommonModels, .. Same70Models],
        Svds = new Dictionary<string, string> { ["svd_same70"] = Same70Svd },
        ApplicationAddress = 0x400000,
        CanController = "mcan1",
        ConfigurationHeader = "src/DuetCANMaster/src/Config/Pins_Duet3_MB6HC.h",
        StepPinsArray = "STEP_PINS",
        DirectionPinsArray = "DIRECTION_PINS",
        SbcBridge = "spi1"
    };

    /// <summary>The Duet 3 Expansion 3HC, version 1.03 (Duet3Expansion's Config/EXP3HC.h)</summary>
    public static readonly BoardType Exp3hc = new()
    {
        Name = "EXP3HC",
        FirmwarePreset = "Duet3Firmware_EXP3HC",
        Mcu = McuFamily.Same5x,
        Platform = "EXP3HC.repl",
        Models = [.. CommonModels, .. Same5xModels],
        Svds = new Dictionary<string, string> { ["svd_same51"] = Same5xSvd("ATSAME51N19A") },
        ApplicationAddress = 0x10000,
        BootloaderPreset = "Duet3Bootloader-SAME5x",
        CanController = "can1",
        AddressSwitches = [McuPin.Parse("PC11"), McuPin.Parse("PC12"), McuPin.Parse("PC14"), McuPin.Parse("PC15")],
        SerialNumberWords = [0x008061FC, 0x00806010, 0x00806014, 0x00806018],
        ConfigurationHeader = "src/Duet3Expansion/src/Config/EXP3HC.h",
        StepPinsArray = "StepPins",
        DirectionPinsArray = "DirectionPins_v103"
    };

    /// <summary>The Duet 3 Expansion 1HCL, version 2.0 (Duet3Expansion's Config/EXP1HCL.h), at its default address</summary>
    public static readonly BoardType Exp1hcl = new()
    {
        Name = "EXP1HCL",
        FirmwarePreset = "Duet3Firmware_EXP1HCL",
        Mcu = McuFamily.Same5x,
        Platform = "EXP1HCL.repl",
        Models = [.. CommonModels, .. Same5xModels],
        Svds = new Dictionary<string, string> { ["svd_same51"] = Same5xSvd("ATSAME51G19A") },
        ApplicationAddress = 0x10000,
        BootloaderPreset = "Duet3Bootloader-SAME5x",
        CanController = "can0",
        // CANlib's CanId::Exp1HCLBoardDefaultAddress
        DefaultAddress = 123,
        SerialNumberWords = [0x008061FC, 0x00806010, 0x00806014, 0x00806018],
        ConfigurationHeader = "src/Duet3Expansion/src/Config/EXP1HCL.h",
        StepPinsArray = "StepPins",
        DirectionPinsArray = "DirectionPins"
    };

    /// <summary>Every board type, main boards first</summary>
    public static IReadOnlyList<BoardType> All { get; } = [Mb6hcStandalone, Mb6hcCanMaster, Exp3hc, Exp1hcl];

    /// <summary>The expansion board of a name, as the regression suite names them (EXP3HC)</summary>
    /// <exception cref="ArgumentException">The bench cannot emulate it</exception>
    public static BoardType Expansion(string name) =>
        All.FirstOrDefault(b => b.IsExpansion && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"The bench cannot emulate a {name}; it has {string.Join(", ", All.Where(b => b.IsExpansion).Select(b => b.Name))}", nameof(name));
}
