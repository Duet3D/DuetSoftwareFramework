namespace SystemTests.Scenarios.Codes.Tools;

/// <summary>The tools the tool, spindle and machine mode scenarios configure, and reads of the values they set</summary>
internal static class ToolSetup
{
    /// <summary>
    /// Sensor 0 and heater 0 on board 1, a fan for the tool to map, and tool 0 using extruder
    /// drive 0, heater 0 and fan 0 (the shape of HeatingTests.HeatedBedConfig, minus M140)
    /// </summary>
    internal static readonly string HeaterToolConfig = $"""
        M308 S0 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor"
        M950 H0 C"{Expansion.Out(Machines.DriverBoard, 0)}" T0
        M950 F0 C"{Expansion.Out(Machines.DriverBoard, 3)}"
        M563 P0 S"Primary" D0 H0 F0
        """;
}
