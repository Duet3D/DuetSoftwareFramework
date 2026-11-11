using System.Globalization;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Heat;

/// <summary>A bed, a tool heater and a chamber on one expansion board, and reads of the heaters they drive</summary>
internal static class HeatSetup
{
    /// <summary>
    /// A full heating bench on board 1: sensor and heater 0 form the bed (M140 H0), sensor and
    /// heater 1 belong to tool 0 (M563), sensor and heater 2 form the chamber (M141 H2)
    /// </summary>
    internal static readonly string HeatConfig = $"""
        M308 S0 P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor"
        M950 H0 C"{Expansion.Out(Machines.DriverBoard, 0)}" T0
        M140 H0
        M308 S1 P"{Expansion.Temp(Machines.DriverBoard, 1)}" Y"thermistor"
        M950 H1 C"{Expansion.Out(Machines.DriverBoard, 1)}" T1
        M563 P0 H1
        M308 S2 P"{Expansion.Temp(Machines.DriverBoard, 2)}" Y"thermistor"
        M950 H2 C"{Expansion.Out(Machines.DriverBoard, 2)}" T2
        M141 H2
        """;

    /// <summary>Lock-free model read of one heater, in the polling shape HeatingTests uses</summary>
    internal static Heater? ReadHeater(DcsTestHost host, int heater)
        => host.Model.Heat.Heaters.Count > heater ? host.Model.Heat.Heaters[heater] : null;
}
