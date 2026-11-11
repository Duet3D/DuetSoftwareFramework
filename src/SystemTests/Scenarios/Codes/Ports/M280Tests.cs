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

/// <summary>M280, positioning a servo</summary>
[TestFixture]
[GCode("M280")]
public class M280Tests : BenchFixture
{
    /// <summary>
    /// M280 converts its S value to a pulse width and reports the position through
    /// state.gpOut[].pwm as the duty cycle that width amounts to
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 280: an S below MinServoPulseWidth = 544 is an angle, converted as
    /// min(angle, 180) * (2400 - 544) / 180 + 544 microseconds (GCodes.h); 544 and above is a pulse
    /// width, clamped to MaxServoPulseWidth = 2400; negative disables with width 0. The stored
    /// value is width * 1e-6 * frequency (GpOutputPort::WriteAnalog into lastPwm), so at the servo
    /// default of 50 Hz an angle of 90 is 1472 us and pwm 0.0736
    /// </remarks>
    [Test]
    public async Task M280ReportsServoPositionAsGpOutPwm()
    {
        await using Bench bench = await Bench.StartAsync($"M950 S0 C\"{Expansion.Out(Machines.DriverBoard, 5)}\"");

        GpOutputPort? gpOut = await bench.Host.ReadModelAsync(model => model.State.GpOut[0]);

        await bench.Host.ExecuteCodeAsync("M280 P0 S90");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.0736).Within(1e-3),
                    "M280 S90 converts the angle to 1472 us and state.gpOut[0].pwm 0.0736 at 50 Hz (GCodes2.cpp case 280)");

        await bench.Host.ExecuteCodeAsync("M280 P0 S1000");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.05).Within(1e-3),
                    "M280 S1000 is a pulse width in us: state.gpOut[0].pwm 0.05 at 50 Hz (GCodes2.cpp case 280)");

        await bench.Host.ExecuteCodeAsync("M280 P0 S3000");
        Assert.That(gpOut!.Pwm, Is.EqualTo(0.12).Within(1e-3),
                    "M280 S3000 clamps to MaxServoPulseWidth 2400 us: state.gpOut[0].pwm 0.12 (GCodes.h, GCodes2.cpp case 280)");

        await bench.Host.ExecuteCodeAsync("M280 P0 S-30");
        Assert.That(gpOut!.Pwm, Is.Zero,
                    "M280 with a negative S disables the servo: state.gpOut[0].pwm 0 (GCodes2.cpp case 280)");

        // The board is asked for a duty cycle, not an angle: the pulse width and the port's refresh
        // frequency are resolved here, which is why the frequency has to be kept on this side at all
        Assert.That(bench.CanMaster.CanMessages<CanMessageWriteGpio>()
                         .Select(w => (w.Board, w.Message.PortNumber, w.Message.IsServo, Pwm: MathF.Round(w.Message.Pwm, 4))),
                    Is.EqualTo(new (byte, byte, bool, float)[]
                    {
                        (Machines.DriverBoard, 0, true, 0.0736f),
                        (Machines.DriverBoard, 0, true, 0.05f),
                        (Machines.DriverBoard, 0, true, 0.12f),
                        (Machines.DriverBoard, 0, true, 0.0f)
                    }),
                    "one WriteGpio per M280, each flagged as a servo and carrying the duty cycle its pulse "
                    + "width amounts to at 50 Hz (GCodes2.cpp case 280, CanInterface::WriteGpio)");
    }
}
