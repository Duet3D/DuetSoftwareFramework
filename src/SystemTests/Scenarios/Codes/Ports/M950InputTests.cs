using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Ports;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Ports;

/// <summary>M950 J, creating, reporting and deleting a general-purpose input</summary>
[TestFixture]
[GCode("M950")]
public class M950InputTests : BenchFixture
{
    /// <summary>The input the scenarios create</summary>
    private const int InputNumber = 1;

    /// <summary>Shortest interval between two input change reports (in ms), RepRapFirmware's <c>MinimumGpinReportInterval</c></summary>
    private const ushort MinimumGpinReportInterval = 30;

    /// <summary>The handle an input is monitored under: major is the input number, as in RepRapFirmware's <c>GpInputPort::Configure</c></summary>
    private static RemoteInputHandle GpInHandle(int inputNumber)
    {
        RemoteInputHandle handle = default;
        handle.Type = (byte)RemoteInputHandle.TypeGpIn;
        handle.Major = (byte)inputNumber;
        return handle;
    }

    /// <summary>The pin it watches, with the pull-up enabled</summary>
    private static readonly string InputPort = $"^{Expansion.GpioIn(Machines.DriverBoard, 2)}";

    /// <summary>Input 1 on the driver board</summary>
    private static readonly string InputConfig = $"M950 J{InputNumber} C\"{InputPort}\"";

    /// <summary>
    /// M950 J asks the pin's board to watch it under the input's handle, and sensors.gpIn[] takes the
    /// state the board answers with
    /// </summary>
    /// <remarks>
    /// GpInputPort::Configure creates the handle with type gpIn, the input number as major and minor 0,
    /// no threshold and MinimumGpinReportInterval 30 ms, and adopts the state from the reply's extra
    /// byte (CanInterface::CreateHandle). The regression reference m950-gpio-input records
    /// <c>{handle: 8256, minInterval: 30, pinName: "^io2.in", threshold: 0}</c>, a reply with extra 1,
    /// and <c>sensors.gpIn[1].value</c> 1 with <c>sensors.gpIn[0]</c> null
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950JWatchesThePinOnItsBoard()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.ReportWith<CanMessageCreateInputMonitorV1>(string.Empty, extra: 1);

        string reply = await bench.Host.ExecuteCodeAsync(InputConfig);
        (byte board, CanMessageCreateInputMonitorV1 sent) = bench.CanMaster.LastCanMessage<CanMessageCreateInputMonitorV1>();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "the input is created without comment");
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the board the port names watches it");
            Assert.That(sent.Handle.Type, Is.EqualTo(RemoteInputHandle.TypeGpIn), "under a gpIn handle");
            Assert.That(sent.Handle.Major, Is.EqualTo(InputNumber), "whose major is the input number");
            Assert.That(sent.Handle.Minor, Is.Zero);
            Assert.That(sent.Handle.All, Is.EqualTo(GpInHandle(InputNumber).All));
            Assert.That(sent.MinInterval, Is.EqualTo(MinimumGpinReportInterval),
                        "reporting at most every MinimumGpinReportInterval");
            Assert.That(sent.Threshold, Is.Zero, "as a digital input");
            Assert.That(sent.PinNameString, Is.EqualTo("^io2.in"), "the pull-up travels with the pin, the address does not");
        });

        Assert.Multiple(async () =>
        {
            GpInputPort? input = await bench.Host.ReadModelAsync(model => model.Sensors.GpIn[InputNumber]);
            Assert.That(input?.Value, Is.EqualTo(1), "sensors.gpIn[1].value is the state the board answered with");
            Assert.That(input?.Port, Is.EqualTo(InputPort), "and sensors.gpIn[1].port the port (DSF addition)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.GpIn[0]), Is.Null,
                        "sensors.gpIn[0] is an empty slot");
        });
    }

    /// <summary>
    /// The board reports the input changing, and sensors.gpIn[].value follows it
    /// </summary>
    [Test]
    [Category("KnownGap")]
    public async Task AnInputReportUpdatesTheValue()
    {
        await using Bench bench = await Bench.StartAsync(InputConfig);

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, GpInHandle(InputNumber), active: true);
        await bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Sensors.GpIn[InputNumber]?.Value == 1,
                                             what: "sensors.gpIn[1].value to follow the report");
    }

    /// <summary>
    /// M950 J C"nil" stops the board watching the pin and empties sensors.gpIn[], and a report the
    /// board sends afterwards does not bring the input back
    /// </summary>
    /// <remarks>
    /// GpInputPort::Configure deletes the remote handle (CanInterface::DeleteHandle) before it looks at
    /// the new pin name, and with "nil" the port is left unused, which the object model reports as null
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950JDeletesAnInputWithCNil()
    {
        await using Bench bench = await Bench.StartAsync(InputConfig);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 J{InputNumber} C\"nil\"");
        (byte board, CanMessageChangeInputMonitorV1 sent) = bench.CanMaster.LastCanMessage<CanMessageChangeInputMonitorV1>();
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the board watching the pin is told to stop");
            Assert.That(sent.Action, Is.EqualTo(CanMessageChangeInputMonitorV1.ActionDelete));
            Assert.That(sent.Handle.All, Is.EqualTo(GpInHandle(InputNumber).All));
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "and nothing is created");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.GpIn.ElementAtOrDefault(InputNumber)),
                        Is.Null, "sensors.gpIn[1] is empty");
        });

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, GpInHandle(InputNumber), active: true);
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.CompletedExchanges > 0, what: "the report to be carried");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.GpIn.ElementAtOrDefault(InputNumber)),
                    Is.Null, "a late report does not bring the input back");
    }

    /// <summary>
    /// A pin the board refuses leaves no input
    /// </summary>
    [Test]
    [Category("KnownGap")]
    public async Task ARefusedInputPinLeavesNoInput()
    {
        await using Bench bench = await Bench.StartAsync();
        bench.CanMaster.ReportWith<CanMessageCreateInputMonitorV1>("Pin 1.io2.in is not free", CodeResult.Error);

        string reply = await bench.Host.ExecuteCodeAsync(InputConfig);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("is not free"));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.GpIn.ElementAtOrDefault(InputNumber)),
                        Is.Null);
        });
    }

    /// <summary>
    /// M950 J with no C asks the board for the pin's name and state, and reports both
    /// </summary>
    /// <remarks>
    /// GpInputPort::Configure sends ChangeInputMonitor with actionReturnPinName and answers
    /// <c>Pin &lt;name&gt;, active: true|false</c>, the state coming from the reply's extra byte
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950JReportsThePinAndItsState()
    {
        await using Bench bench = await Bench.StartAsync(InputConfig);
        bench.CanMaster.ReportWith<CanMessageChangeInputMonitorV1>("1.io2.in, pullup enabled", extra: 1);

        string reply = await bench.Host.ExecuteCodeAsync($"M950 J{InputNumber}");
        (_, CanMessageChangeInputMonitorV1 sent) = bench.CanMaster.LastCanMessage<CanMessageChangeInputMonitorV1>();
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.EqualTo("Pin 1.io2.in, pullup enabled, active: true"));
            Assert.That(sent.Action, Is.EqualTo(CanMessageChangeInputMonitorV1.ActionReturnPinName));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.GpIn[InputNumber]!.Value), Is.EqualTo(1),
                        "and sensors.gpIn[1].value takes the state the board answered with");
        });
    }

    /// <summary>An input with no pin is reported without asking any board</summary>
    [Test]
    [Category("KnownGap")]
    public async Task AnInputWithNoPinIsReportedAsSuch()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M950 J3");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error:").And.Contain("active: false"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>(), Is.Empty);
        });
    }

    /// <summary>An input number past MaxGpInPorts is refused before the port is looked at</summary>
    /// <remarks>Platform::ConfigurePort: <c>GetLimitedUIValue('J', MaxGpInPorts)</c></remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950RefusesAnInputNumberPastTheLimit()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M950 J{GpioManager.MaxGpInPorts} C\"{InputPort}\"");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error:").And.Contain("parameter 'J' too high"));
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty);
        });
    }

    /// <summary>
    /// M950 J C"probe0" creates a virtual input that follows Z probe 0's triggered state, with no pin
    /// and no board
    /// </summary>
    /// <remarks>
    /// GpInputPort::Configure (RRF 3.7) recognises "probeN", "fmN.switch" and "fmN.motion" before it
    /// looks for a pin, and GpInputPort::GetState reads the probe or filament monitor
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M950JCreatesAVirtualInputFollowingAProbe()
    {
        await using Bench bench = await Bench.StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync($"M950 J{InputNumber} C\"probe0\"");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty, "a virtual input needs no board");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.GpIn.ElementAtOrDefault(InputNumber)),
                        Is.Not.Null, "and appears in sensors.gpIn[]");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty);
        });
    }
}
