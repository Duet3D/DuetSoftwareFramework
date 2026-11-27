using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation.Boards;
using DuetEmulation.Can;
using DuetEmulation.Traces;

namespace DuetEmulation.Captures;

/// <summary>
/// Runs codes and drives inputs on a bench, and records everything that follows in a
/// <see cref="MachineCapture"/>: each code and its reply, every CAN frame, every change of the object
/// model and of the boards' output pins, and every input it drove
/// </summary>
/// <remarks>
/// The object model is compared with its last snapshot whenever the recorder catches up, which is
/// after each code finishes, each wait and each input it drives; a change is stamped with the time it
/// was seen, which is when the code that made it had finished
/// </remarks>
public sealed class Recorder
{
    /// <summary>
    /// Object model paths left out by default, because they change with time and not with what the
    /// machine does, or describe the computer running the bench rather than the machine
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultIgnoredPaths =
    [
        "state.time", "state.upTime", "state.msUpTime", "sbc", "network", "volumes", "seqs",
        // RepRapFirmware counts the bench's own M409 queries on the channel codes arrive on
        "inputs[*].lineNumber"
    ];

    private readonly Bench _bench;
    private readonly IReadOnlyList<string> _ignoredPaths;
    private readonly bool _unnamedPins;
    private int _canRecorded;
    private int _ioRecorded;
    private int _inputsRecorded;
    private JsonNode? _model;

    /// <summary>What has been recorded</summary>
    public MachineCapture Capture { get; }

    /// <summary>The bench it records</summary>
    public Bench Bench => _bench;

    private Recorder(Bench bench, MachineCapture capture, IReadOnlyList<string> ignoredPaths, bool unnamedPins)
    {
        _bench = bench;
        Capture = capture;
        _ignoredPaths = ignoredPaths;
        _unnamedPins = unnamedPins;
    }

    /// <summary>
    /// Start recording a bench that has booted: from its present state, or from when the boards were
    /// created with <paramref name="includeBoot"/>, which records the boot's CAN frames and pin changes too
    /// </summary>
    /// <param name="bench">The bench</param>
    /// <param name="header">What the capture is of</param>
    /// <param name="includeBoot">Whether to record from when the boards were created</param>
    /// <param name="ignoredPaths">Object model paths to leave out, <see cref="DefaultIgnoredPaths"/> by default</param>
    /// <param name="unnamedPins">Whether to record pins the firmware gives no port name, such as chip selects and LEDs</param>
    /// <param name="cancellationToken">Cancels starting</param>
    public static async Task<Recorder> StartAsync(Bench bench, CaptureHeader header, bool includeBoot = false,
                                                  IReadOnlyList<string>? ignoredPaths = null, bool unnamedPins = false,
                                                  CancellationToken cancellationToken = default)
    {
        Recorder recorder = new(bench, new MachineCapture(header), ignoredPaths ?? DefaultIgnoredPaths, unnamedPins);
        if (!includeBoot)
        {
            recorder._canRecorded = bench.Can.All.Count;
            recorder._ioRecorded = bench.System.Io.All.Count;
            recorder._inputsRecorded = bench.System.Io.Inputs.Count;
        }
        recorder._model = await bench.GetObjectModelSnapshotAsync(cancellationToken);
        recorder.Capture.Add([new ObjectModelSnapshot(recorder.Now, ObjectModelDiff.Without(recorder._model, recorder._ignoredPaths))]);
        await recorder.CatchUpAsync(cancellationToken);
        return recorder;
    }

    /// <summary>The time now, as captures record it</summary>
    public double Now => _bench.System.Time.TotalSeconds;

    /// <summary>Note a remark in the capture, such as the name of the step that follows</summary>
    public void Note(string text) => Capture.Add([new Note(Now, text)]);

    /// <summary>Send a code, wait for it to finish, and record what it did</summary>
    public async Task<CodeReply> SendCodeAsync(string code, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        await (await StartCodeAsync(code, cancellationToken)).WaitAsync(timeout, cancellationToken);

    /// <summary>Send a code without waiting for it; what it did is recorded once it finishes and is waited for</summary>
    public async Task<RecordedCode> StartCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        await CatchUpAsync(cancellationToken);
        Capture.Add([new CodeSent(Now, code)]);
        return new RecordedCode(this, await _bench.StartCodeAsync(code, cancellationToken));
    }

    /// <summary>Let emulated time pass, recording the wait and what happens during it</summary>
    public async Task WaitAsync(TimeSpan time, CancellationToken cancellationToken = default)
    {
        await CatchUpAsync(cancellationToken);
        Capture.Add([new WaitStarted(Now, Scenarios.Scenario.FormatDuration(time))]);
        await _bench.WaitAsync(time, cancellationToken);
        await CatchUpAsync(cancellationToken);
    }

    /// <summary>Drive a digital input, as a switch or sensor would</summary>
    public async Task DriveAsync(int address, string port, bool level, CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, port, level ? "high" : "low", cancellationToken);
        await _bench.Io.DriveAsync(address, port, level, cancellationToken);
    }

    /// <summary>Stop driving a digital input</summary>
    public async Task ReleaseAsync(int address, string port, CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, port, "released", cancellationToken);
        await _bench.Io.ReleaseAsync(address, port, cancellationToken);
    }

    /// <summary>Make an analogue input read a fraction of its reference</summary>
    public async Task SetAnalogAsync(int address, string port, double fraction, CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, port, string.Create(CultureInfo.InvariantCulture, $"analog {fraction:0.####}"), cancellationToken);
        await _bench.Io.SetAnalogAsync(address, port, fraction, cancellationToken);
    }

    /// <summary>Make a thermistor input read a temperature</summary>
    public async Task SetThermistorAsync(int address, string port, double celsius, CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, port, string.Create(CultureInfo.InvariantCulture, $"thermistor {celsius:0.##}C"), cancellationToken);
        await _bench.Io.SetThermistorAsync(address, port, celsius, cancellationToken: cancellationToken);
    }

    /// <summary>Drive a digital input once a driver on the same board has had so many more step pulses</summary>
    public async Task DriveAfterStepsAsync(int address, int driver, ulong steps, string port, bool level, CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, port, string.Create(CultureInfo.InvariantCulture,
            $"{(level ? "high" : "low")} after {steps} steps of driver {address}.{driver}"), cancellationToken);
        await _bench.Io.DriveAfterStepsAsync(address, driver, steps, port, level, cancellationToken);
    }

    /// <summary>Drive a digital input as a square wave, or stop it with 0</summary>
    public async Task PulsesAsync(int address, string port, double frequency, CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, port, string.Create(CultureInfo.InvariantCulture, $"pulses {frequency:0.###}Hz"), cancellationToken);
        await _bench.Io.PulsesAsync(address, port, frequency, cancellationToken);
    }

    /// <summary>Pulse a tacho input as a fan driven by an output would turn</summary>
    public async Task TachoFollowsAsync(int address, string fanPort, string tachoPort, double maxRpm, double pulsesPerRev = BoardIo.DefaultTachoPulsesPerRev, bool inverted = false,
                                        CancellationToken cancellationToken = default)
    {
        await RecordInputAsync(address, tachoPort, string.Create(CultureInfo.InvariantCulture,
            $"tacho of {fanPort}, {maxRpm:0.#} RPM at full duty, {pulsesPerRev:0.#} pulses a revolution{(inverted ? ", inverted" : string.Empty)}"), cancellationToken);
        await _bench.Io.TachoFollowsAsync(address, fanPort, tachoPort, maxRpm, pulsesPerRev, inverted, cancellationToken);
    }

    // Catch up before the input changes, so that what came before it is recorded before it
    private async Task RecordInputAsync(int address, string port, string value, CancellationToken cancellationToken)
    {
        await CatchUpAsync(cancellationToken);
        Capture.Add([new InputDriven(Now, address, port, value)]);
    }

    /// <summary>
    /// Record what has happened since the last catch-up: the CAN frames, the pin changes, and the
    /// changes of the object model since its last snapshot
    /// </summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken = default)
    {
        Capture.Add(TraceEvents());
        Capture.Add(await ObjectModelEventsAsync(cancellationToken));
    }

    // The CAN frames and pin changes since the last catch-up
    private List<CaptureEvent> TraceEvents()
    {
        List<CaptureEvent> events = [];

        IReadOnlyList<CanFrame> frames = _bench.Can.All;
        for (; _canRecorded < frames.Count; _canRecorded++)
        {
            CanFrame frame = frames[_canRecorded];
            events.Add(new CanFrameSent(frame.Time.TotalSeconds, frame.Source, frame.Destination, frame.MessageType.ToString(), frame.IsResponse,
                                        Convert.ToHexStringLower(frame.Data), CanFrameDecoder.Decode(frame)));
        }

        IReadOnlyList<PinEvent> pins = _bench.System.Io.All;
        for (; _ioRecorded < pins.Count; _ioRecorded++)
        {
            PinEvent pin = pins[_ioRecorded];
            if (pin.Ports.Count == 0 && !_unnamedPins)
            {
                continue;
            }
            bool pwm = pin.Output.Mode == PinMode.Pwm;
            events.Add(new PinChanged(pin.Time.TotalSeconds, pin.Board.Address, [.. pin.Ports], pin.Output.Mode.ToString(),
                                      pwm ? pin.Output.Duty : null, pwm ? pin.Output.Frequency : null));
        }

        IReadOnlyList<InputEvent> inputs = _bench.System.Io.Inputs;
        for (; _inputsRecorded < inputs.Count; _inputsRecorded++)
        {
            InputEvent input = inputs[_inputsRecorded];
            events.Add(new InputChanged(input.Time.TotalSeconds, input.Board.Address, [.. input.Ports], input.Value));
        }
        return [.. events.OrderBy(e => e.T)];
    }

    // The changes of the object model since its last snapshot
    private async Task<List<CaptureEvent>> ObjectModelEventsAsync(CancellationToken cancellationToken)
    {
        JsonNode? model = await _bench.GetObjectModelSnapshotAsync(cancellationToken);
        double now = Now;
        List<CaptureEvent> events = [.. ObjectModelDiff.Compare(_model, model, _ignoredPaths).Select(c => new ObjectModelChanged(now, c.Path, c.Old, c.New))];
        _model = model;
        return events;
    }

    /// <summary>The last snapshot of the object model</summary>
    public JsonNode? ObjectModel => _model;

    /// <summary>A code the recorder sent, whose reply and effects are recorded when it is waited for</summary>
    public sealed class RecordedCode(Recorder recorder, PendingCode pending)
    {
        /// <summary>The code</summary>
        public string Code => pending.Code;

        /// <summary>
        /// Record that the script waits for the code, as a step of its own, then wait for it as
        /// <see cref="WaitAsync"/> does
        /// </summary>
        public async Task<CodeReply> AwaitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await recorder.CatchUpAsync(cancellationToken);
            recorder.Capture.Add([new CodeAwaited(recorder.Now, pending.Code)]);
            return await WaitAsync(timeout, cancellationToken);
        }

        /// <summary>Let emulated time pass until the code has finished, record what it did, and return its reply</summary>
        public async Task<CodeReply> WaitAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            CodeReply reply = await pending.WaitAsync(timeout, cancellationToken);
            double replied = recorder.Now;
            // What the code sent and switched while it ran, then its reply, then what it changed in the object model
            recorder.Capture.Add(recorder.TraceEvents());
            recorder.Capture.Add([new CodeReplied(replied, pending.Code, Severity(reply.Severity), reply.Text)]);
            recorder.Capture.Add(await recorder.ObjectModelEventsAsync(cancellationToken));
            return reply;
        }
    }

    private static string Severity(MessageType type) => type switch
    {
        MessageType.Warning => "warning",
        MessageType.Error => "error",
        _ => "success"
    };

    /// <summary>The header for a capture of a bench running on <paramref name="stack"/></summary>
    public static CaptureHeader Header(string name, Stack stack, IReadOnlyDictionary<int, BoardType> boards, string config) =>
        new(name, stack.ToString().ToLowerInvariant(), boards.ToDictionary(b => b.Key, b => b.Value.Name), config);
}
