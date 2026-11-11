using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetAPI.ObjectModel;
using NUnit.Framework;

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

    /// <summary>
    /// Read a numeric object model field, failing the test when the model leaves it null rather
    /// than letting the assertion that wanted a number report something else
    /// </summary>
    internal static async Task<double> EvaluateNumberAsync(DcsTestHost host,
                                                          Func<DuetControlServer.Model.ObjectModel, double?> read)
    {
        double? value = await host.ReadModelAsync(read);
        if (value is not double reading)
        {
            Assert.Fail("the object model leaves that field null, so it holds no number");
            return double.NaN;
        }
        return reading;
    }
}
