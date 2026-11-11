using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Fans.FanSetup;

namespace SystemTests.Scenarios.Codes.Ports;

/// <summary>M42, driving a general purpose output</summary>
[TestFixture]
[GCode("M42")]
public class M42Tests : BenchFixture
{
    /// <summary>M42 S scales its value into state.gpOut[].pwm the way a fan speed scales</summary>
    /// <remarks>
    /// GCodes2.cpp case 42 reads S with GetPwmValue (GCodeBuffer.cpp: above 1 is out of 255,
    /// constrained to 0..1) and GpOutputPort::WriteAnalog stores it in lastPwm, which the OM table
    /// reports as state.gpOut[].pwm
    /// </remarks>
    [Test]
    public async Task M42DrivesGpOutPwm()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");

        GpOutputPort? gpOut = await bench.Host.ReadModelAsync(model => model.State.GpOut[0]);

        await bench.Host.ExecuteCodeAsync("M42 P0 S0.5");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.5).Within(1e-3),
                    "M42 S0.5 sets state.gpOut[0].pwm as a fraction (GCodes2.cpp case 42, GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M42 P0 S255");
        Assert.That(gpOut!.Pwm, Is.EqualTo(1.0).Within(1e-3),
                    "M42 S255 sets state.gpOut[0].pwm to 1 (GetPwmValue scales out of 255)");

        await bench.Host.ExecuteCodeAsync("M42 P0 S128");
        Assert.That(gpOut!.Pwm, Is.EqualTo(128.0 / 255.0).Within(1e-3),
                    "M42 S128 sets state.gpOut[0].pwm to 128/255 (GetPwmValue)");

        await bench.Host.ExecuteCodeAsync("M42 P0 S-3");
        Assert.That(gpOut!.Pwm, Is.Zero,
                    "M42 with a negative S constrains state.gpOut[0].pwm to 0 (GetPwmValue constrains 0..1)");

        // Every write reached the board as a WriteGpio naming the port and the duty cycle, scaled on
        // this side: CanInterface::WriteGpio carries the port number, the PWM and the servo flag
        Assert.That(bench.CanMaster.CanMessages<CanMessageWriteGpio>()
                         .Select(w => (w.Board, w.Message.PortNumber, w.Message.IsServo, Pwm: MathF.Round(w.Message.Pwm, 3))),
                    Is.EqualTo(new (byte, byte, bool, float)[]
                    {
                        (Machines.DriverBoard, 0, false, 0.5f),
                        (Machines.DriverBoard, 0, false, 1.0f),
                        (Machines.DriverBoard, 0, false, MathF.Round(128.0f / 255.0f, 3)),
                        (Machines.DriverBoard, 0, false, 0.0f)
                    }),
                    "one WriteGpio per M42, none of them flagged as a servo (GCodes2.cpp case 42)");
    }

    /// <summary>
    /// A board that drives an output but warns about it has its warning reported
    /// </summary>
    /// <remarks>
    /// The same reply path as a fan speed: CanInterface::WriteGpio answers with a result code, and
    /// M42 is the code that has to carry it
    /// </remarks>
    [Test]
    public async Task M42ReportsABoardWarningAboutTheOutput()
    {
        await using Bench bench = await Bench.StartAsync($"M950 P0 C\"{Expansion.Out(Machines.DriverBoard, 4)}\"");
        bench.CanMaster.ReportWith<CanMessageWriteGpio>("output 0 is already driven by a fan",
                                                        CodeResult.Warning);

        string reply = (await bench.Host.ExecuteCodeAsync("M42 P0 S0.5")).TrimEnd();
        Assert.That(reply, Is.EqualTo("Warning: M42: output 0 is already driven by a fan"),
                    "the board drove the output and warned about it, and the warning is the code's result");
    }
}
