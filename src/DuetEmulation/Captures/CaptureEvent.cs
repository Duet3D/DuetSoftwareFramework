using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DuetEmulation.Captures;

/// <summary>
/// Something that happened on the emulated machine, at <see cref="T"/> seconds of emulated time since
/// the boards were created
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CodeSent), "code")]
[JsonDerivedType(typeof(CodeReplied), "reply")]
[JsonDerivedType(typeof(CodeAwaited), "await")]
[JsonDerivedType(typeof(WaitStarted), "wait")]
[JsonDerivedType(typeof(ConfigStarted), "config")]
[JsonDerivedType(typeof(CanFrameSent), "can")]
[JsonDerivedType(typeof(ObjectModelChanged), "om")]
[JsonDerivedType(typeof(ObjectModelSnapshot), "om-snapshot")]
[JsonDerivedType(typeof(PinChanged), "pin")]
[JsonDerivedType(typeof(InputDriven), "input")]
[JsonDerivedType(typeof(InputChanged), "input-change")]
[JsonDerivedType(typeof(Note), "note")]
public abstract record CaptureEvent(double T)
{
    /// <summary>The event as one line of the readable report, without its time</summary>
    public abstract string Describe();

    /// <summary>The time as the report shows it</summary>
    public string Time => T.ToString("0.000000", CultureInfo.InvariantCulture);
}

/// <summary>A code was sent to the machine</summary>
public sealed record CodeSent(double T, string Code) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"> {Code}";
}

/// <summary>The script started waiting for a code it sent without waiting</summary>
public sealed record CodeAwaited(double T, string Code) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"await {Code}";
}

/// <summary>The script let emulated time pass</summary>
/// <param name="T">When the wait started</param>
/// <param name="Duration">How long, as the script gave it</param>
public sealed record WaitStarted(double T, string Duration) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"wait {Duration}";
}

/// <summary>config.g carried on from its hold: what follows until the next step is what config.g did</summary>
public sealed record ConfigStarted(double T) : CaptureEvent(T)
{
    /// <summary>What the step is called</summary>
    public const string Action = "config.g";

    /// <inheritdoc/>
    public override string Describe() => $"run {Action}";
}

/// <summary>A code finished, with its reply</summary>
/// <param name="T">When it finished</param>
/// <param name="Code">The code</param>
/// <param name="Severity">success, warning or error</param>
/// <param name="Text">The reply, without its severity prefix</param>
public sealed record CodeReplied(double T, string Code, string Severity, string Text) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"< {(Severity == "success" ? string.Empty : Severity + ": ")}{(Text.Length == 0 ? "(empty)" : Text.Replace("\n", "\n    "))}";
}

/// <summary>A frame crossed the CAN bus</summary>
/// <param name="T">When its sender sent it</param>
/// <param name="Source">The sender's CAN address</param>
/// <param name="Destination">The CAN address it was for, or 63 for all</param>
/// <param name="Type">Its message type</param>
/// <param name="Response">Whether it answers a request</param>
/// <param name="Data">Its payload in hex</param>
/// <param name="Message">Its payload decoded, by field name or by parameter letter; null for a type with no layout</param>
public sealed record CanFrameSent(double T, int Source, int Destination, string Type, bool Response, string Data, JsonObject? Message) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"CAN {Source}->{(Destination == 63 ? "all" : Destination.ToString(CultureInfo.InvariantCulture))} {Type}{(Response ? " (response)" : string.Empty)} " +
        (Message?.ToJsonString() ?? Data);
}

/// <summary>A value of the object model changed, seen when the code that changed it finished</summary>
public sealed record ObjectModelChanged(double T, string Path, JsonNode? Old, JsonNode? New) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"OM {Path}: {Old?.ToJsonString() ?? "null"} -> {New?.ToJsonString() ?? "null"}";
}

/// <summary>The whole object model when the capture started, which its changes build on</summary>
public sealed record ObjectModelSnapshot(double T, JsonNode? Model) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"OM snapshot of {(Model as JsonObject)?.Count ?? 0} keys";
}

/// <summary>An output pin of a board changed what it drives</summary>
/// <param name="T">When</param>
/// <param name="Board">The board's CAN address</param>
/// <param name="Ports">The port names the firmware gives the pin</param>
/// <param name="Output">What it drives now: Low, High, Pwm, Peripheral or Floating</param>
/// <param name="Duty">Its duty for PWM, as a fraction</param>
/// <param name="Frequency">Its frequency for PWM, in Hz</param>
public sealed record PinChanged(double T, int Board, string[] Ports, string Output, double? Duty, double? Frequency) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"PIN {Board}.{string.Join('/', Ports)} {Output}" +
        (Duty is double duty ? string.Create(CultureInfo.InvariantCulture, $" {duty:P1} at {Frequency:0.#}Hz") : string.Empty);
}

/// <summary>Something outside the boards drove one of their inputs</summary>
/// <param name="T">When</param>
/// <param name="Board">The board's CAN address</param>
/// <param name="Port">The port name the firmware gives the pin</param>
/// <param name="Value">What drives it: high, low, released, an analogue fraction, a temperature, a pulse train...</param>
public sealed record InputDriven(double T, int Board, string Port, string Value) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"INPUT {Board}.{Port} {Value}";
}

/// <summary>
/// An input changed by itself, as a script set up earlier made it: an endstop triggering after so many
/// steps, a tacho starting to pulse. Unlike <see cref="InputDriven"/> it does not start a step
/// </summary>
public sealed record InputChanged(double T, int Board, string[] Ports, string Value) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"INPUT {Board}.{string.Join('/', Ports)} {Value} (scripted)";
}

/// <summary>A remark the script made, such as the name of the step that follows</summary>
public sealed record Note(double T, string Text) : CaptureEvent(T)
{
    /// <inheritdoc/>
    public override string Describe() => $"# {Text}";
}
