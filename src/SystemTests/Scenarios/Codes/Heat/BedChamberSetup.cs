using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>Heaters 3 and 4 with no function and tool 0 with heater 1, as the regression cases set them up, and reads of the bed and chamber slots</summary>
internal static class BedChamberSetup
{
    /// <summary>Heaters 3 and 4 on board 1 with no function, as the regression cases set them up, and tool 0 using heater 1</summary>
    internal static readonly string HeaterConfig = $"""
        M308 S1 P"{Expansion.Temp(Machines.DriverBoard, 2)}" Y"thermistor" T100000 B4138
        M950 H1 C"{Expansion.Out(Machines.DriverBoard, 2)}" T1
        M563 P0 H1
        M308 S3 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor" T100000 B4138
        M308 S4 P"{Expansion.Temp(Machines.DriverBoard, 1)}" Y"thermistor" T100000 B4138
        M950 H3 C"{Expansion.Out(Machines.DriverBoard, 0)}" T3
        M950 H4 C"{Expansion.Out(Machines.DriverBoard, 1)}" T4
        """;

    internal static Task<Bench> StartAsync(string extraConfig = "") => Bench.StartAsync(HeaterConfig, extraConfig);

    internal static Task<Heater> ReadHeaterAsync(Bench bench, int heater)
        => bench.Host.ReadModelAsync(model => model.Heat.Heaters[heater]!);

    internal static CanMessageSetHeaterTemperatureV1[] TemperatureMessages(Bench bench)
        => bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>().Select(m => m.Message).ToArray();
}
