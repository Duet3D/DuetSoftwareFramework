using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Probes.ProbeSetup;

namespace SystemTests.Scenarios.Codes.Probes;

/// <summary>M574, configuring an endstop</summary>
[TestFixture]
[GCode("M574")]
public class M574Tests : BenchFixture
{
    /// <summary>
    /// M574 X1 S1 P creates a low-end switch endstop: sensors.endstops[0] reports type, highEnd,
    /// triggered and the port, and M574 with no axes reports the configuration
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 (EndstopsManager.cpp) creates a SwitchEndstop at the
    /// given position; the OM fields are Endstop.cpp's table (highEnd, triggered, type), with type 1
    /// reported as "inputPin". The report wording is SwitchEndstop::AppendDetails. The port itself is
    /// a DSF addition documented in rrf-differences.md section 3 (the object model has to be able to
    /// recreate the machine)
    /// </remarks>
    [Test]
    public async Task M574ConfiguresLowEndSwitchEndstop()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(XEndstopLine);
        Assert.That(reply.Trim(), Is.Empty, "M574 X1 S1 P was accepted");

        (EndstopType Type, bool HighEnd, bool Triggered, string? Port)? endstop = await bench.Host.ReadModelAsync(
            model => model.Sensors.Endstops.Count > 0 && model.Sensors.Endstops[0] is Endstop e
                ? (e.Type, e.HighEnd, e.Triggered, e.Port)
                : ((EndstopType, bool, bool, string?)?)null);
        Assert.That(endstop, Is.Not.Null, "M574 X1 creates sensors.endstops[0] (RRF EndstopsManager::HandleM574)");
        Assert.Multiple(() =>
        {
            Assert.That(endstop!.Value.Type, Is.EqualTo(EndstopType.InputPin),
                        "M574 S1 sets sensors.endstops[0].type to inputPin (RRF Endstop.cpp OM table)");
            Assert.That(endstop!.Value.HighEnd, Is.False,
                        "M574 X1 sets sensors.endstops[0].highEnd false (RRF Endstop.cpp GetAtHighEnd)");
            Assert.That(endstop!.Value.Triggered, Is.False,
                        "a new endstop starts untriggered (RRF Endstop.cpp Stopped)");
            Assert.That(endstop!.Value.Port, Is.EqualTo(Expansion.GpioIn(Machines.DriverBoard, 0)),
                        "M574 P records sensors.endstops[0].port (rrf-differences.md section 3)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M574");
        Assert.That(report, Does.Contain("Endstop configuration:"),
                    "M574 with no axes reports the configuration (RRF EndstopsManager::HandleM574)");
        Assert.That(report, Does.Contain($"X: low end switch connected to pin {Expansion.GpioIn(Machines.DriverBoard, 0)}"),
                    "the report names the position and pin (RRF SwitchEndstop::AppendDetails)");
    }

    /// <summary>
    /// M574 S5 asks for a stall detected from the encoder position error rather than from StallGuard
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 (EndstopsManager.cpp) creates a StallDetectionEndstop
    /// with useEncoder for EndStopType::motorStallEncoder, which GetEndstopType reports back as that
    /// type. The mechanism travels to the board in CanMessageEnableStallEndstop's endstopType field
    /// (CANlib), and a board too old to know the field reads those bits as zero, which is the
    /// StallGuard it would have used anyway
    /// </remarks>
    [Test]
    public async Task M574ConfiguresEncoderStallEndstop()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M574 X1 S5");
        Assert.That(reply.Trim(), Is.Empty, "M574 X1 S5 was accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Type),
                    Is.EqualTo(EndstopType.MotorStallEncoder),
                    "M574 S5 sets sensors.endstops[0].type to motorStallEncoder "
                    + "(RRF StallDetectionEndstop::GetEndstopType)");

        Assert.That(await bench.Host.ExecuteCodeAsync("M574 X1 S6"), Does.Contain("Invalid endstop input type"),
                    "S6 names no mechanism, so it is refused rather than stored");
    }

    /// <summary>
    /// M574 K names the Z probe that stands in for the endstop, and a number out of range is refused
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 reads K with GetLimitedUIValue against MaxZProbes, which
    /// is issue #1275: it used to take any number, and sensors.endstops[].probe would then point at a
    /// probe that does not exist. K is read whether or not S asked for a probe endstop, as RRF does
    /// </remarks>
    [Test]
    public async Task M574RangeChecksTheProbeNumber()
    {
        await using Bench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync($"M558 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 3)}\"");
        string reply = await bench.Host.ExecuteCodeAsync("M574 X1 S2 K0");
        Assert.That(reply.Trim(), Is.Empty, "M574 S2 K0 was accepted");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Type),
                        Is.EqualTo(EndstopType.ZProbeAsEndstop), "M574 S2 makes the probe stand in for the endstop");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Probe), Is.Zero,
                        "M574 K records sensors.endstops[0].probe");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("M574 X1 S2 K99"),
                    Does.Contain("Z probe number must be between 0 and"),
                    "a K past the last probe is refused (RRF issue #1275, GetLimitedUIValue against MaxZProbes)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Probe), Is.Zero,
                    "and the endstop still names the probe it was given");
    }

    /// <summary>
    /// M574 Y2 makes a high-end endstop and M574 Y0 removes it again, leaving the slot empty
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 (EndstopsManager.cpp): position 2 is
    /// EndStopPosition::highEndStop, position 0 deletes the endstop so the OM array reports null for
    /// that axis (EndstopsManager.cpp object model array 1, FindEndstopWhenLockOwned)
    /// </remarks>
    [Test]
    public async Task M574HighEndAndRemovalUpdateTheModel()
    {
        await using Bench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync($"M574 Y2 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\"");
        bool? highEnd = await bench.Host.ReadModelAsync(
            model => model.Sensors.Endstops.Count > 1 ? model.Sensors.Endstops[1]?.HighEnd : null);
        Assert.That(highEnd, Is.True,
                    "M574 Y2 sets sensors.endstops[1].highEnd (RRF EndstopsManager::HandleM574, highEndStop)");

        await bench.Host.ExecuteCodeAsync("M574 Y0");
        bool removed = await bench.Host.ReadModelAsync(
            model => model.Sensors.Endstops.Count > 1 && model.Sensors.Endstops[1] is null);
        Assert.That(removed, Is.True,
                    "M574 Y0 removes the endstop, so sensors.endstops[1] is null (RRF EndstopsManager::HandleM574, noEndStop)");
    }

    #region Switch endstops

    /// <summary>
    /// A switch endstop asks the board carrying its pin to watch it, and the state the board answers
    /// with is the endstop's triggered state from the start
    /// </summary>
    /// <remarks>
    /// RRF truth: SwitchEndstop::Configure calls CanInterface::CreateHandle with
    /// RemoteInputHandle(typeEndstop, axis, 0), threshold 0 and the pin name with the board address
    /// removed, and takes the reply's extra byte as the switch's state. Reference
    /// m574-endstop-switch-low records handle 0x1000, threshold 0, pinName "io1.in" and, with the
    /// floating pin answering extra 1, sensors.endstops[0].triggered true. minInterval is 30 there
    /// (SwitchEndstop::MinimumSwitchReportInterval) and zero here, a deliberate difference documented
    /// in endstops.md section 3 and on EndstopMinReportInterval
    /// </remarks>
    [TestCase(false)]
    [TestCase(true)]
    public async Task SwitchEndstopAsksTheBoardCarryingItsPinToWatchIt(bool pinActive)
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(canMaster => AnswerCreates(canMaster, _ => (CodeResult.Ok, "", pinActive)))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"");
        Assert.That(reply.Trim(), Is.Empty, "the board took the pin, so M574 succeeds");

        IReadOnlyList<(byte Board, CanMessageCreateInputMonitorV1 Message)> creates = Creates(bench);
        Assert.That(creates, Has.Count.EqualTo(1), "one monitor for one switch");
        Assert.Multiple(() =>
        {
            Assert.That(creates[0].Board, Is.EqualTo(Machines.DriverBoard), "sent to the board the port names");
            Assert.That(creates[0].Message.Handle.All, Is.EqualTo(RemoteEndstops.HandleFor(0).All),
                        "handle is (typeEndstop, axis 0, switch 0), 0x1000 in the reference");
            Assert.That(creates[0].Message.Handle.Type, Is.EqualTo(RemoteInputHandle.TypeEndstop));
            Assert.That(creates[0].Message.Handle.Major, Is.Zero, "major is the axis");
            Assert.That(creates[0].Message.Handle.Minor, Is.Zero, "minor is the switch within the axis");
            Assert.That(creates[0].Message.Threshold, Is.Zero, "a switch is digital: threshold 0");
            Assert.That(creates[0].Message.MinInterval, Is.Zero,
                        "zero rather than RRF's 30 ms, as endstops.md section 3 documents");
            Assert.That(creates[0].Message.PinNameString, Is.EqualTo("io1.in"),
                        "the pin travels without its board address");
        });

        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Triggered), Is.EqualTo(pinActive),
                    "the state in the reply's extra byte seeds sensors.endstops[0].triggered (reference: extra 1, triggered true)");
    }

    /// <summary>
    /// A high-end switch on an inverted pin keeps the inversion on the wire and in the model
    /// </summary>
    /// <remarks>
    /// RRF truth: SwitchEndstop::Configure passes the pin name to CreateHandle as given, after
    /// IoPort::RemoveBoardAddress, which keeps a leading '!'. Reference m574-endstop-switch-high
    /// records handle 0x1040 (axis 1) and pinName "!io2.in", and sensors.endstops[1].highEnd true
    /// </remarks>
    [Test]
    public async Task InvertedHighEndSwitchKeepsItsInversion()
    {
        await using Bench bench = await StartBenchAsync();

        string port = "!" + Expansion.GpioIn(Machines.DriverBoard, 2);
        string reply = await bench.Host.ExecuteCodeAsync($"M574 Y2 S1 P\"{port}\"");
        Assert.That(reply.Trim(), Is.Empty);

        (byte board, CanMessageCreateInputMonitorV1 create) = bench.CanMaster.LastCanMessage<CanMessageCreateInputMonitorV1>();
        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(create.Handle.All, Is.EqualTo(RemoteEndstops.HandleFor(1).All), "axis 1: 0x1040 in the reference");
            Assert.That(create.PinNameString, Is.EqualTo("!io2.in"), "the inversion goes to the board, which applies it");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[1]!.HighEnd), Is.True);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[1]!.Port), Is.EqualTo(port),
                        "the port is recorded as given, inversion included");
        });
    }

    /// <summary>
    /// An axis with a switch per motor, written with '+', gets a monitor per switch, each under the
    /// handle of its position in the list
    /// </summary>
    /// <remarks>
    /// RRF truth: SwitchEndstop::Configure splits P on '+' and creates
    /// RemoteInputHandle(typeEndstop, axis, i) for the i-th pin, in order. Reference
    /// m574-multiple-endstops-per-axis records handles 0x1080 and 0x1081 with pins io3.in and io4.in,
    /// and the report is "switches connected to pins" (SwitchEndstop::AppendDetails)
    /// </remarks>
    [Test]
    public async Task SwitchPerMotorIsWatchedUnderAHandlePerSwitch()
    {
        await using Bench bench = await StartBenchAsync(
            $"M584 Z{Expansion.Driver(Machines.DriverBoard, Machines.ZDriver)}:{Expansion.Driver(Machines.DriverBoard, 4)}");

        string ports = $"{Expansion.GpioIn(Machines.DriverBoard, 3)}+{Expansion.GpioIn(Machines.DriverBoard, 4)}";
        string reply = await bench.Host.ExecuteCodeAsync($"M574 Z1 S1 P\"{ports}\"");
        Assert.That(reply.Trim(), Is.Empty);

        IReadOnlyList<(byte Board, CanMessageCreateInputMonitorV1 Message)> creates = Creates(bench);
        Assert.That(creates.Select(c => (c.Board, c.Message.Handle.All, c.Message.PinNameString)),
                    Is.EqualTo(new[]
                    {
                        (Machines.DriverBoard, RemoteEndstops.HandleFor(2, 0).All, "io3.in"),
                        (Machines.DriverBoard, RemoteEndstops.HandleFor(2, 1).All, "io4.in")
                    }),
                    "one monitor per pin, in order, minor numbering the switch");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[2]!.Port), Is.EqualTo(ports));
        Assert.That(await bench.Host.ExecuteCodeAsync("M574"),
                    Does.Contain($"Z: low end switches connected to pins {Expansion.GpioIn(Machines.DriverBoard, 3)} "
                                 + Expansion.GpioIn(Machines.DriverBoard, 4)));
    }

    /// <summary>
    /// The number of switches is not checked against the number of motors: two switches on a
    /// single-motor axis are both watched
    /// </summary>
    /// <remarks>
    /// RRF truth: SwitchEndstop::Configure takes up to MaxDriversPerAxis pins without consulting the
    /// axis' drivers; SwitchEndstop::PrimeAxis only uses the counts to decide whether to stop motors
    /// individually. The wiki says the same: "If the number of pins matches the number of motors
    /// assigned to that axis, motors will be stopped individually"
    /// </remarks>
    [Test]
    public async Task SwitchCountNeedNotMatchTheMotorCount()
    {
        await using Bench bench = await StartBenchAsync();

        string ports = $"{Expansion.GpioIn(Machines.DriverBoard, 0)}+{Expansion.GpioIn(Machines.DriverBoard, 1)}";
        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{ports}\"")).Trim(), Is.Empty,
                    "two switches on X, which has one motor, are accepted");
        Assert.That(Creates(bench).Select(c => c.Message.Handle.All),
                    Is.EqualTo(new[] { RemoteEndstops.HandleFor(0, 0).All, RemoteEndstops.HandleFor(0, 1).All }));
    }

    /// <summary>
    /// More switches than an axis can have motors is refused, and nothing is configured
    /// </summary>
    /// <remarks>
    /// RRF's SwitchEndstop::Configure stops reading at MaxDriversPerAxis and silently drops the rest,
    /// which is not how RRF treats an over-long list anywhere else (M584 refuses an array too long for
    /// the axis). DSF follows the convention and refuses, as rrf-differences.md section 3.1 records
    /// </remarks>
    [Test]
    public async Task MoreSwitchesThanAnAxisCanHaveAreRefused()
    {
        await using Bench bench = await StartBenchAsync();

        string ports = string.Join('+', Enumerable.Range(0, MotionLimits.MaxDriversPerAxis + 1)
                                                  .Select(io => Expansion.GpioIn(Machines.DriverBoard, io)));
        string reply = await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{ports}\"");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(Creates(bench), Is.Empty, "no pin is claimed for a refused endstop");
            Assert.That(await EndstopOf(bench, 0), Is.Null, "and X has no endstop");
        });
    }

    /// <summary>
    /// A port with no expansion board in its name, or naming board 0, is refused before anything is
    /// sent or recorded
    /// </summary>
    /// <remarks>
    /// RRF would assign the pin on its own main board (SwitchEndstop::Configure, local branch). Board
    /// 0 here owns no pins, so DSF refuses, as rrf-differences.md section 1 documents ("A port name has
    /// to name an expansion board")
    /// </remarks>
    [TestCase("io1.in")]
    [TestCase("0.io1.in")]
    public async Task PortWithoutAnExpansionBoardIsRefused(string port)
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{port}\"");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(Creates(bench), Is.Empty);
            Assert.That(await EndstopOf(bench, 0), Is.Null);
        });
    }

    /// <summary>
    /// A board that refuses the pin fails M574 with the board's reason, and the axis is left with no
    /// endstop
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 deletes the axis' endstop before configuring the new
    /// SwitchEndstop and only stores it if Configure returned ok, so a refusal leaves
    /// sensors.endstops[axis] null. The reply is the board's own text, carried back by
    /// CanInterface::CreateHandle (SendRequestAndGetStandardReply)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task BoardRefusingThePinLeavesTheAxisWithoutAnEndstop()
    {
        const string refusal = "Pin io1.in is not free";
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(canMaster => AnswerCreates(canMaster, _ => (CodeResult.Error, refusal, false)))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain(refusal), "the board's reason is the error");
            Assert.That(await EndstopOf(bench, 0), Is.Null,
                        "an endstop whose pin was refused is not recorded (RRF deletes the SwitchEndstop)");
        });
    }

    /// <summary>
    /// Moving an endstop to a pin the board refuses takes the old endstop away too: its pin is given
    /// back and the axis has no endstop
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 deletes axisEndstops[axis] (the destructor releasing the
    /// old handle with CanInterface::DeleteHandle) before the new SwitchEndstop is configured, and the
    /// new one is dropped when Configure fails. So the board holds neither pin and sensors.endstops[0]
    /// is null. Duet3Expansion's InputMonitor::Create deletes any monitor under the handle before
    /// assigning the new pin, so the old pin is free whether or not a delete is sent
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task RefusedReconfigurationRemovesTheOldEndstop()
    {
        string goodPort = Expansion.GpioIn(Machines.DriverBoard, 1), badPort = Expansion.GpioIn(Machines.DriverBoard, 9);
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(canMaster => AnswerCreates(canMaster, create =>
                                                 create.PinNameString == "io9.in" ? (CodeResult.Error, "Unknown pin name 'io9.in'", false)
                                                                                  : (CodeResult.Ok, "", false)))
                                             .StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{goodPort}\"")).Trim(), Is.Empty);
        bench.CanMaster.ClearCapture();

        string reply = await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{badPort}\"");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await EndstopOf(bench, 0), Is.Null,
                        "the old endstop was deleted before the new one was refused (RRF EndstopsManager::HandleM574)");
        });
    }

    /// <summary>
    /// When the second switch of an axis is refused, the first is given back as well and the axis has
    /// no endstop
    /// </summary>
    /// <remarks>
    /// RRF truth: SwitchEndstop::Configure calls ReleasePorts when any CreateHandle fails, which sends
    /// actionDelete for every handle already created (here switch 0), and HandleM574 then drops the
    /// endstop, so sensors.endstops[axis] is null
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task SecondSwitchRefusedGivesBackTheFirst()
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(canMaster => AnswerCreates(canMaster, create =>
                                                 create.Handle.Minor == 1 ? (CodeResult.Error, "Pin io4.in is not free", false)
                                                                          : (CodeResult.Ok, "", false)))
                                             .StartAsync();

        string ports = $"{Expansion.GpioIn(Machines.DriverBoard, 3)}+{Expansion.GpioIn(Machines.DriverBoard, 4)}";
        string reply = await bench.Host.ExecuteCodeAsync($"M574 Z1 S1 P\"{ports}\"");

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("io4.in"));
            Assert.That(MonitorTraffic(bench), Is.EqualTo(new[]
            {
                (Machines.DriverBoard, Create, RemoteEndstops.HandleFor(2, 0).All),
                (Machines.DriverBoard, Create, RemoteEndstops.HandleFor(2, 1).All),
                (Machines.DriverBoard, Delete, RemoteEndstops.HandleFor(2, 0).All)
            }), "the switch that was taken is given back (RRF SwitchEndstop::ReleasePorts)");
            Assert.That(await EndstopOf(bench, 2), Is.Null, "and Z has no endstop");
        });
    }

    /// <summary>
    /// Moving a switch to another pin of the same board re-creates the monitor under the same handle,
    /// which the board takes as replacing the old one
    /// </summary>
    /// <remarks>
    /// RRF's EndstopsManager::HandleM574 deletes the old SwitchEndstop first, so it sends actionDelete
    /// for handle 0x1000 and then creates it again. DSF sends only the create: Duet3Expansion's
    /// InputMonitor::Create deletes any monitor under the handle before assigning the new pin, so the
    /// board ends in the same state. endstops.md section 3 ("Reconfiguring gives the old pins back
    /// first") documents this
    /// </remarks>
    [Test]
    public async Task MovingASwitchToAnotherPinReplacesItsMonitor()
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync(XEndstopLine);
        bench.CanMaster.ClearCapture();

        string newPort = Expansion.GpioIn(Machines.DriverBoard, 5);
        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{newPort}\"")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(MonitorTraffic(bench), Is.EqualTo(new[] { (Machines.DriverBoard, Create, RemoteEndstops.HandleFor(0).All) }),
                        "the handle is re-created on the same board, which replaces the old monitor");
            Assert.That(bench.CanMaster.LastCanMessage<CanMessageCreateInputMonitorV1>().Message.PinNameString, Is.EqualTo("io5.in"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Port), Is.EqualTo(newPort));
        });
    }

    /// <summary>
    /// Moving a switch to another board gives the handle back on the old board before claiming the
    /// new pin
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 deletes the old SwitchEndstop, whose ReleasePorts sends
    /// actionDelete (CanInterface::DeleteHandle) to the board it was on, then configures the new one,
    /// which creates the handle on the new board
    /// </remarks>
    [Test]
    public async Task MovingASwitchToAnotherBoardReleasesTheOldBoardsHandle()
    {
        const byte otherBoard = Machines.DriverBoard + 1;
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync(XEndstopLine);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{Expansion.GpioIn(otherBoard, 0)}\"")).Trim(), Is.Empty);
        Assert.That(MonitorTraffic(bench), Is.EqualTo(new[]
        {
            (Machines.DriverBoard, Delete, RemoteEndstops.HandleFor(0).All),
            (otherBoard, Create, RemoteEndstops.HandleFor(0).All)
        }), "the old board lets go of the pin before the new board is asked for its own");
    }

    /// <summary>
    /// Giving an axis fewer switches gives back the handles it no longer uses, before the remaining
    /// switches are created
    /// </summary>
    /// <remarks>
    /// RRF truth: the old SwitchEndstop's ReleasePorts deletes every handle it held before the new one
    /// is configured. DSF deletes only the handle that is abandoned (switch 1) and re-creates switch 0,
    /// which the board takes as a replacement (endstops.md section 3); the delete has to come first,
    /// because io4.in moves from the handle of switch 1 to that of switch 0
    /// </remarks>
    [Test]
    public async Task ShrinkingASwitchPerMotorEndstopReleasesTheDroppedSwitch()
    {
        await using Bench bench = await StartBenchAsync();
        string first = Expansion.GpioIn(Machines.DriverBoard, 3), second = Expansion.GpioIn(Machines.DriverBoard, 4);
        await bench.Host.ExecuteCodeAsync($"M574 Z1 S1 P\"{first}+{second}\"");
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 Z1 S1 P\"{second}\"")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(MonitorTraffic(bench), Is.EqualTo(new[]
            {
                (Machines.DriverBoard, Delete, RemoteEndstops.HandleFor(2, 1).All),
                (Machines.DriverBoard, Create, RemoteEndstops.HandleFor(2, 0).All)
            }), "the abandoned handle is deleted before its pin is asked for under another");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[2]!.Port), Is.EqualTo(second));
        });
    }

    /// <summary>
    /// A switch endstop needs a pin: without P, an axis that has no switch already is refused and
    /// left as it was
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574, inputPin case without P: "Pin name needed for
    /// switch-type endstop on %c axis" when the axis has no SwitchEndstop. S defaults to 1, so the bare
    /// position form is refused the same way
    /// </remarks>
    [TestCase("M574 X1 S1")]
    [TestCase("M574 X2")]
    [Category("KnownGap")]
    public async Task SwitchWithoutAPinNeedsOneAlreadyConfigured(string line)
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(line);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("X axis"), "the pin name is needed (RRF HandleM574)");
            Assert.That(await EndstopOf(bench, 0), Is.Null, "and X is left without an endstop");
            Assert.That(MonitorTraffic(bench), Is.Empty);
        });
    }

    /// <summary>
    /// Without P, an axis that already has a switch keeps its pins and only moves to the other end
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574, inputPin case without P on an existing SwitchEndstop
    /// calls SetAtHighEnd and nothing else, so the board is not asked anything again
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task PositionWithoutAPinMovesAnExistingSwitch()
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync(XEndstopLine);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M574 X2")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.HighEnd), Is.True);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Port),
                        Is.EqualTo(Expansion.GpioIn(Machines.DriverBoard, 0)), "the pin is kept");
            Assert.That(MonitorTraffic(bench), Is.Empty, "only the position changed, so the board is not asked again");
        });
    }

    /// <summary>
    /// P names one switch input, so it is refused with more than one axis or with a type that is not a
    /// switch, and nothing is configured
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 refuses with "Invalid use of P parameter" when
    /// axesSeen &gt; 1 or the input type is not inputPin, before touching any endstop
    /// </remarks>
    [TestCase("X1 Y1 S1")]
    [TestCase("X1 S3")]
    [TestCase("X1 S2")]
    public async Task PortWithSeveralAxesOrANonSwitchTypeIsRefused(string parameters)
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M574 {parameters} P\"{Expansion.GpioIn(Machines.DriverBoard, 0)}\"");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("Invalid use of P parameter"));
            Assert.That(await EndstopOf(bench, 0), Is.Null);
            Assert.That(await EndstopOf(bench, 1), Is.Null);
            Assert.That(MonitorTraffic(bench), Is.Empty);
        });
    }

    #endregion

    #region Other endstop types

    /// <summary>The stall endstop types and the object model type each one is reported as</summary>
    private static readonly object[] StallTypes =
    [
        new object[] { 3, EndstopType.MotorStallAny },
        new object[] { 4, EndstopType.MotorStallIndividual },
        new object[] { 5, EndstopType.MotorStallEncoder }
    ];

    /// <summary>
    /// A stall endstop needs no pin, so nothing is sent at M574 time, and several axes can be given
    /// one in a single command
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 replaces each named axis' endstop with a
    /// StallDetectionEndstop (individualMotors for S4, useEncoder for S5), which sends nothing until a
    /// homing move primes it (StallDetectionEndstop::PrimeAxis). References m574-endstop-stall-detection
    /// and m574-endstop-encoder record no CAN traffic, type motorStallAny / motorStallEncoder, probe
    /// null and triggered false
    /// </remarks>
    [TestCaseSource(nameof(StallTypes))]
    public async Task StallEndstopsNeedNoPinAndSendNothing(int type, EndstopType expected)
    {
        await using Bench bench = await StartBenchAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 X1 Y2 S{type}")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            foreach (int axis in new[] { 0, 1 })
            {
                Endstop? endstop = await EndstopOf(bench, axis);
                Assert.That(endstop, Is.Not.Null);
                Assert.That(endstop!.Type, Is.EqualTo(expected));
                Assert.That(endstop.HighEnd, Is.EqualTo(axis == 1));
                Assert.That(endstop.Probe, Is.Null);
                Assert.That(endstop.Triggered, Is.False);
                Assert.That(endstop.Port, Is.Null, "a stall has no port");
            }
            Assert.That(MonitorTraffic(bench), Is.Empty, "nothing is asked of a board until a homing move");
        });
    }

    /// <summary>
    /// Changing a switch to a stall endstop gives its pin back
    /// </summary>
    /// <remarks>
    /// RRF truth: ReplaceObject in EndstopsManager::HandleM574 deletes the SwitchEndstop, whose
    /// destructor runs ReleasePorts and sends actionDelete for its handle
    /// </remarks>
    [Test]
    public async Task ChangingASwitchToAStallReleasesItsPin()
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync(XEndstopLine);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M574 X1 S3")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(MonitorTraffic(bench), Is.EqualTo(new[] { (Machines.DriverBoard, Delete, RemoteEndstops.HandleFor(0).All) }));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Type), Is.EqualTo(EndstopType.MotorStallAny));
        });
    }

    /// <summary>
    /// A switch replaced by a stall or Z probe endstop takes its port with it, so the object model does
    /// not name a pin no board is watching
    /// </summary>
    /// <remarks>
    /// RRF truth: ReplaceObject in EndstopsManager::HandleM574 swaps the SwitchEndstop for a new
    /// StallDetectionEndstop or ZProbeEndstop, neither of which has a pin. sensors.endstops[].port is
    /// DSF's record of the switch's pins (rrf-differences.md section 3), so it must go with the switch
    /// </remarks>
    [TestCase(3)]
    [TestCase(2)]
    [Category("KnownGap")]
    public async Task ReplacedSwitchTakesItsPortWithIt(int type)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);
        await bench.Host.ExecuteCodeAsync(XEndstopLine);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 X1 S{type}")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Port), Is.Null,
                    "the endstop is no longer a switch, so it has no port");
    }

    /// <summary>
    /// A Z probe endstop is watched under the probe's own monitor, so M574 sends nothing; K is still
    /// range-checked when S is not 2, and is not recorded then
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 creates a ZProbeEndstop, which has no input of its own.
    /// Reference m574-endstop-z-probe records no CAN traffic, type zProbeAsEndstop and probe 0. K is
    /// read with GetLimitedUIValue before P is looked at, so an out-of-range K refuses a switch
    /// endstop too, and the OM's probe is only reported for a Z probe endstop (Endstop.cpp,
    /// OBJECT_MODEL_FUNC_IF IsZProbe)
    /// </remarks>
    [Test]
    public async Task ZProbeEndstopSendsNothingOfItsOwn()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That((await bench.Host.ExecuteCodeAsync("M574 X1 S2 K0")).Trim(), Is.Empty);
        Assert.That(MonitorTraffic(bench), Is.Empty, "the probe's monitor already exists; the endstop adds none");

        Assert.That(await bench.Host.ExecuteCodeAsync($"M574 Y1 S1 K99 P\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\""),
                    Does.StartWith("Error:"), "K is range-checked whatever S is");
        Assert.That(await EndstopOf(bench, 1), Is.Null, "and the refused line configured nothing");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 Y1 S1 K1 P\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\"")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[1]!.Probe), Is.Null,
                    "a switch endstop names no probe even when K is given");
    }

    /// <summary>
    /// A Z probe endstop reads triggered whenever its probe does
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbeEndstop::Stopped returns the probe's own Stopped(), and Endstop.cpp's OM entry
    /// "triggered" is Stopped(). Reference m574-endstop-z-probe records sensors.endstops[0].triggered
    /// true, because the probe's floating input reads active
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ZProbeEndstopReadsTriggeredFromItsProbe()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);
        await bench.Host.ExecuteCodeAsync("M574 X1 S2 K0");

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        await bench.Host.WaitForModelAsync(model => model.Sensors.Probes[0]!.Value[0], value => value > 0,
                                           "the probe reads triggered");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Triggered), Is.True,
                    "the X endstop is the probe, so it is triggered with it (RRF ZProbeEndstop::Stopped)");
    }

    /// <summary>
    /// S0, the retired active-low type, is refused with a pointer to the replacement and configures
    /// nothing
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 refuses EndStopType::unused_wasActiveLow with "endstop
    /// type 0 is no longer supported. Use type 1 and invert the input pin instead."
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task TypeZeroIsRefused()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M574 X1 S0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("no longer supported"));
            Assert.That(await EndstopOf(bench, 0), Is.Null);
        });
    }

    /// <summary>
    /// A position past 2, or an axis letter with no position, is refused and configures nothing
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 refuses a position of numPositions or more with "Invalid
    /// endstop position" before anything is changed; an axis letter with no number throws "expected
    /// number after 'X'" from StringParser::CheckNumberFound
    /// </remarks>
    [TestCase("M574 X3 S1")]
    [TestCase("M574 X S1")]
    public async Task PositionOutOfRangeIsRefused(string line)
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(line);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await EndstopOf(bench, 0), Is.Null);
            Assert.That(MonitorTraffic(bench), Is.Empty);
        });
    }

    #endregion

    #region Removal

    /// <summary>
    /// Position 0 deletes the endstop and gives its pin back; an axis with nothing to delete sends
    /// nothing
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 DeleteObject, whose SwitchEndstop destructor calls
    /// CanInterface::DeleteHandle: CanMessageChangeInputMonitorV1 with action 2 (actionDelete), param
    /// 0. Reference m574-endstop-none records exactly that for handle 0x1000 and sensors.endstops[0]
    /// going to null
    /// </remarks>
    [Test]
    public async Task RemovingAnEndstopReleasesItsPin()
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync(XEndstopLine);
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M574 X0")).Trim(), Is.Empty);
        (byte board, CanMessageChangeInputMonitorV1 change) = bench.CanMaster.LastCanMessage<CanMessageChangeInputMonitorV1>();
        Assert.Multiple(async () =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard));
            Assert.That(change.Handle.All, Is.EqualTo(RemoteEndstops.HandleFor(0).All));
            Assert.That(change.Action, Is.EqualTo(CanMessageChangeInputMonitorV1.ActionDelete));
            Assert.That(change.Param, Is.Zero);
            Assert.That(MonitorTraffic(bench), Has.Count.EqualTo(1), "one delete and nothing else");
            Assert.That(await EndstopOf(bench, 0), Is.Null);
        });

        bench.CanMaster.ClearCapture();
        Assert.That((await bench.Host.ExecuteCodeAsync("M574 X0")).Trim(), Is.Empty, "removing nothing is not an error");
        Assert.That(MonitorTraffic(bench), Is.Empty, "and asks nothing of any board");
    }

    /// <summary>
    /// A board that will not release the handle does not fail M574 X0: the endstop is still removed
    /// </summary>
    /// <remarks>
    /// RRF truth: SwitchEndstop::ReleasePorts reports a failed CanInterface::DeleteHandle as a
    /// separate message and carries on, so HandleM574 returns ok and the endstop is gone.
    /// Duet3Expansion's InputMonitor::Change answers a handle it does not have with a warning
    /// </remarks>
    [TestCase(CodeResult.Warning)]
    [TestCase(CodeResult.Error)]
    public async Task RemovalSucceedsWhenTheBoardWillNotRelease(CodeResult result)
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync(XEndstopLine);
        bench.CanMaster.ReportWith<CanMessageChangeInputMonitorV1>("Board 1 does not have input handle 1000", result);

        string reply = await bench.Host.ExecuteCodeAsync("M574 X0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:"), "the removal itself succeeded");
            Assert.That(await EndstopOf(bench, 0), Is.Null);
        });
    }

    #endregion

    #region Reports and other axes

    /// <summary>
    /// M574 alone lists every axis, "none" for one without an endstop, from what DSF holds rather than
    /// by asking the boards
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 with no axes prints "Endstop configuration:" and a line
    /// per axis, "none" or "low end "/"high end " plus AppendDetails. Reference m574-report-endstops
    /// records "X: low end switch connected to pin 3.io1.in, min interval 30ms" for each switch, where
    /// RRF's SwitchEndstop::AppendDetails asks each board for the pin name (actionReturnPinName) and
    /// the board appends its interval. DSF answers from the port it recorded and sends nothing, which
    /// rrf-differences.md section 2.3 documents
    /// </remarks>
    [Test]
    public async Task ReportListsEveryAxisWithoutAskingTheBoards()
    {
        await using Bench bench = await StartBenchAsync();
        string x = Expansion.GpioIn(Machines.DriverBoard, 1), y = "!" + Expansion.GpioIn(Machines.DriverBoard, 2);
        await bench.Host.ExecuteCodeAsync($"M574 X1 S1 P\"{x}\"");
        await bench.Host.ExecuteCodeAsync($"M574 Y2 S1 P\"{y}\"");
        bench.CanMaster.ClearCapture();

        string report = await bench.Host.ExecuteCodeAsync("M574");
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.StartWith("Endstop configuration:"));
            Assert.That(report, Does.Contain($"\nX: low end switch connected to pin {x}"));
            Assert.That(report, Does.Contain($"\nY: high end switch connected to pin {y}"));
            Assert.That(report, Does.Contain("\nZ: none"));
            Assert.That(MonitorTraffic(bench), Is.Empty,
                        "the pin names come from the object model (rrf-differences.md section 2.3)");
        });
    }

    /// <summary>
    /// The report names a Z probe endstop and the two StallGuard stall endstops
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbeEndstop::AppendDetails is "Z probe" and StallDetectionEndstop::AppendDetails
    /// "motor stall (any motor)" or "motor stall (individual motors)". DSF says "drivers" for
    /// "motors", which is wording
    /// </remarks>
    [Test]
    public async Task ReportDescribesProbeAndStallEndstops()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);
        await bench.Host.ExecuteCodeAsync("M574 X1 S2 K0");
        await bench.Host.ExecuteCodeAsync("M574 Y2 S3");
        await bench.Host.ExecuteCodeAsync("M574 Z1 S4");

        string report = await bench.Host.ExecuteCodeAsync("M574");
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("\nX: low end Z probe"));
            Assert.That(report, Does.Contain("\nY: high end motor stall (any"));
            Assert.That(report, Does.Contain("\nZ: low end motor stall (individual"));
        });
    }

    /// <summary>
    /// The report names an encoder endstop as a stall endstop
    /// </summary>
    /// <remarks>
    /// RRF truth: an S5 endstop is a StallDetectionEndstop, whose AppendDetails reports "motor stall
    /// (any motor)" whether or not it uses the encoder
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task ReportDescribesAnEncoderEndstopAsAStall()
    {
        await using Bench bench = await StartBenchAsync();
        await bench.Host.ExecuteCodeAsync("M574 X1 S5");

        Assert.That(await bench.Host.ExecuteCodeAsync("M574"), Does.Contain("\nX: low end motor stall"));
    }

    /// <summary>
    /// Axes past Z and rotary axes take the handle of their own axis number, and the report lists them
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 walks GetTotalAxes() by letter, so any axis created by
    /// M584 can have an endstop, and SwitchEndstop uses the axis number as the handle's major; whether
    /// the axis is rotary makes no difference to an endstop
    /// </remarks>
    [Test]
    public async Task AxesPastZAndRotaryAxesTakeTheirOwnHandles()
    {
        await using Bench bench = await StartBenchAsync(
            $"M584 U{Expansion.Driver(Machines.DriverBoard, 3)}\nM584 A{Expansion.Driver(Machines.DriverBoard, 4)} R1");
        string u = Expansion.GpioIn(Machines.DriverBoard, 3), a = Expansion.GpioIn(Machines.DriverBoard, 4);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 U1 S1 P\"{u}\"")).Trim(), Is.Empty);
        Assert.That((await bench.Host.ExecuteCodeAsync($"M574 A2 S1 P\"{a}\"")).Trim(), Is.Empty);

        string report = await bench.Host.ExecuteCodeAsync("M574");
        Assert.Multiple(async () =>
        {
            Assert.That(Creates(bench).Select(c => c.Message.Handle.All),
                        Is.EqualTo(new[] { RemoteEndstops.HandleFor(3).All, RemoteEndstops.HandleFor(4).All }),
                        "U is axis 3 and A axis 4");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[3]!.Port), Is.EqualTo(u));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[4]!.HighEnd), Is.True);
            Assert.That(report, Does.Contain($"\nU: low end switch connected to pin {u}"));
            Assert.That(report, Does.Contain($"\nA: high end switch connected to pin {a}"));
        });
    }

    #endregion

    #region Extruder endstops

    /// <summary>
    /// M574 E binds a general purpose input to an extruder as its filament endstop, reports it, lists
    /// it in the full report, and P-1 reverts to stall detection
    /// </summary>
    /// <remarks>
    /// RRF truth (3.7.0-beta.3 and later): EndstopsManager::HandleM574's E branch stores P in
    /// extruderGpinNumbers, "M574 E0" replies "Extruder 0 endstop uses input pin 0" and M574 adds
    /// "E0: input pin 0". Reference m574-filament-endstop-for-extruder records all three replies and no
    /// CAN traffic. RRF does not report the binding in its object model; DSF records it as
    /// move.extruders[].endstopInput (rrf-differences.md section 3). A known gap because the input has
    /// to exist first and M950 J is not ported
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task FilamentEndstopBindsAnInputToAnExtruder()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze);
        Assert.That(await bench.Host.ExecuteCodeAsync($"M950 J0 C\"!{Expansion.GpioIn(Machines.DriverBoard, 2)}\""),
                    Does.Not.StartWith("Warning:").And.Not.StartWith("Error:"), "M950 J creates input 0");
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync("M574 E0 P0")).Trim(), Is.Empty);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].EndstopInput), Is.EqualTo(0));
            Assert.That(await bench.Host.ExecuteCodeAsync("M574 E0"), Does.Contain("Extruder 0 endstop uses input pin 0"));
            Assert.That((await bench.Host.ExecuteCodeAsync("M574")).TrimEnd(), Does.EndWith("\nE0: input pin 0"));
            Assert.That(MonitorTraffic(bench), Is.Empty, "the input is already monitored by M950 J");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M574 E0 P-1")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].EndstopInput), Is.Null);
        Assert.That(await bench.Host.ExecuteCodeAsync("M574 E0"), Does.Contain("Extruder 0 endstop uses motor stall detection"));
    }

    /// <summary>
    /// An extruder with nothing bound uses stall detection, and the full report has no line for it
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 replies "Extruder %u endstop uses motor stall detection"
    /// for an extruder at NoGpinPort, and the full report lists only extruders with an input
    /// </remarks>
    [Test]
    public async Task ExtruderEndstopDefaultsToStallDetection()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze);

        Assert.That(await bench.Host.ExecuteCodeAsync("M574 E0"), Does.Contain("Extruder 0 endstop uses motor stall detection"));
        Assert.That(await bench.Host.ExecuteCodeAsync("M574"), Does.Not.Contain("\nE0:"));
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].EndstopInput), Is.Null);
    }

    /// <summary>
    /// The E form is refused with an axis, for an extruder or input number out of range, and for an
    /// input that is not configured, leaving the binding as it was
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574: "Cannot configure axis and extruder endstops in the same
    /// command"; E read with GetLimitedUIValue against the number of extruders; P with
    /// GetLimitedIValue(-1, MaxGpInPorts - 1); "Input pin %d is not configured" for an unused input
    /// </remarks>
    [TestCase("M574 X1 E0 P-1")]
    [TestCase("M574 E1 P-1")]
    [TestCase("M574 E0 P-2")]
    [TestCase("M574 E0 P0")]
    public async Task ExtruderFormRefusals(string line)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze);

        string reply = await bench.Host.ExecuteCodeAsync(line);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].EndstopInput), Is.Null);
            Assert.That(await EndstopOf(bench, 0), Is.Null, "nothing was configured for X either");
        });
    }

    #endregion

    #region Helpers

    /// <summary>What a board was asked to do with an input monitor</summary>
    private const string Create = "create", Delete = "delete";

    /// <summary>Answer every input monitor creation with the given result, reply text and pin state</summary>
    private static void AnswerCreates(ScriptedCanMaster canMaster,
                                      Func<CanMessageCreateInputMonitorV1, (CodeResult Result, string Text, bool Active)> answer)
        => canMaster.OnCanMessage((ushort)CanMessageCreateInputMonitorV1.MessageType, (fake, header, payload) =>
        {
            (CodeResult result, string text, bool active) = answer(CanMessageSerializer.Deserialize<CanMessageCreateInputMonitorV1>(payload));
            fake.InjectStandardReply(header, result, text, (byte)(active ? 1 : 0));
        });

    /// <summary>Every input monitor creation sent, in order</summary>
    private static IReadOnlyList<(byte Board, CanMessageCreateInputMonitorV1 Message)> Creates(Bench bench)
        => bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>();

    /// <summary>Every input monitor creation and change sent, in order: the board, what it was asked and the handle</summary>
    private static IReadOnlyList<(byte Board, string What, ushort Handle)> MonitorTraffic(Bench bench)
    {
        List<(byte, string, ushort)> traffic = [];
        foreach (CapturedPacket packet in bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage))
        {
            (SendCanMessageHeader header, byte[] payload) = packet.DecodeCanMessage();
            if (header.MsgType == (ushort)CanMessageCreateInputMonitorV1.MessageType)
            {
                traffic.Add((header.DstAddress, Create,
                             CanMessageSerializer.Deserialize<CanMessageCreateInputMonitorV1>(payload).Handle.All));
            }
            else if (header.MsgType == (ushort)CanMessageChangeInputMonitorV1.MessageType)
            {
                CanMessageChangeInputMonitorV1 change = CanMessageSerializer.Deserialize<CanMessageChangeInputMonitorV1>(payload);
                traffic.Add((header.DstAddress,
                             change.Action == CanMessageChangeInputMonitorV1.ActionDelete ? Delete : $"action {change.Action}",
                             change.Handle.All));
            }
        }
        return traffic;
    }

    /// <summary>An axis' endstop, or null if it has none</summary>
    private static Task<Endstop?> EndstopOf(Bench bench, int axis)
        => bench.Host.ReadModelAsync(model => axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null);

    #endregion
}
