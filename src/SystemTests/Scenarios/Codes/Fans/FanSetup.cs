using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Fans;

/// <summary>The fans the fan and port scenarios configure</summary>
internal static class FanSetup
{
    /// <summary>Fan 0 on board 1 with an explicit PWM frequency</summary>
    internal static readonly string Fan0Config = $"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 3)}\" Q500";

    /// <summary>Fans 0 and 1 on board 1, for the codes whose fan mapping matters</summary>
    internal static readonly string TwoFanConfig = Fan0Config + $"\nM950 F1 C\"{Expansion.Out(Machines.DriverBoard, 6)}\"";
}
