using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using ObjectModel = DuetControlServer.Model.ObjectModel;

namespace SystemTests.Host;

/// <summary>Waits for the object model to reach a state</summary>
/// <remarks>
/// <para>
/// What a board reports, and what a background task publishes, reaches the model some time after the
/// report or the reply, so a scenario that depends on it has to wait for it.
/// </para>
/// <para>
/// The <c>WaitFor</c> methods throw if the state is never reached, for what a scenario cannot go on
/// without. The <c>Settled</c> methods return the last reading instead, for a value the scenario then
/// asserts on.
/// </para>
/// </remarks>
internal static class ModelWaits
{
    /// <summary>How long the model is given to reach a state a scenario depends on</summary>
    private const int WaitTimeoutMs = 20_000;

    /// <summary>How long a value that settles shortly after a code's reply is given to do so</summary>
    private const int SettleTimeoutMs = 5_000;

    /// <summary>How long to leave between two readings of the model</summary>
    private const int PollIntervalMs = 25;

    /// <summary>Poll part of the object model until it satisfies a condition or the time runs out</summary>
    /// <returns>The last reading, and whether the condition held for it</returns>
    private static async Task<(T Value, bool IsReady)> PollAsync<T>(DcsTestHost host, Func<ObjectModel, T> read,
                                                                    Func<T, bool> isReady, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T value;
        do
        {
            value = await host.ReadModelAsync(read);
            if (isReady(value))
            {
                return (value, true);
            }
            await Task.Delay(PollIntervalMs);
        }
        while (DateTime.UtcNow < deadline);
        return (value, false);
    }

    private static bool IsNear(double value, double expected, double tolerance) => Math.Abs(value - expected) <= tolerance;

    /// <summary>Wait until part of the object model satisfies a condition, and return it</summary>
    /// <typeparam name="T">What is being read</typeparam>
    /// <param name="host">The host to ask</param>
    /// <param name="read">Reads the part of the model in question</param>
    /// <param name="isReady">Says whether what was read is what the test is waiting for</param>
    /// <param name="what">What is being waited for, for the timeout message</param>
    /// <param name="timeoutMs">How long to wait</param>
    /// <returns>The value once the condition holds</returns>
    public static async Task<T> WaitForModelAsync<T>(this DcsTestHost host, Func<ObjectModel, T> read, Func<T, bool> isReady,
                                                     string what, int timeoutMs = WaitTimeoutMs)
    {
        (T value, bool reached) = await PollAsync(host, read, isReady, timeoutMs);
        return reached ? value : throw new TimeoutException($"Timed out waiting: {what}; last read \"{value}\"");
    }

    /// <summary>Wait until part of the object model equals the given value</summary>
    public static Task<T> WaitForModelAsync<T>(this DcsTestHost host, Func<ObjectModel, T> read, T expected,
                                               string what, int timeoutMs = WaitTimeoutMs)
        => host.WaitForModelAsync(read, reading => EqualityComparer<T>.Default.Equals(reading, expected),
                                  $"{what} to be \"{expected}\"", timeoutMs);

    /// <summary>Wait until a number in the object model is within a tolerance of the given value</summary>
    public static Task<double> WaitForNumberAsync(this DcsTestHost host, Func<ObjectModel, double?> read, double expected,
                                                  string what, double tolerance = 1e-3)
        => host.WaitForModelAsync(model => read(model) ?? double.NaN, reading => IsNear(reading, expected, tolerance),
                                  $"{what} to be {expected}");

    /// <summary>Part of the object model once it equals the given value, or its last reading when it never does</summary>
    public static async Task<T> SettledValueAsync<T>(this DcsTestHost host, Func<ObjectModel, T> read, T expected,
                                                     int timeoutMs = SettleTimeoutMs)
        => (await PollAsync(host, read, reading => EqualityComparer<T>.Default.Equals(reading, expected), timeoutMs)).Value;

    /// <summary>
    /// A number in the object model once it is within a tolerance of the given value, or its last
    /// reading when it never is. A null field reads as NaN
    /// </summary>
    public static async Task<double> SettledNumberAsync(this DcsTestHost host, Func<ObjectModel, double?> read, double expected,
                                                        double tolerance = 1e-3, int timeoutMs = SettleTimeoutMs)
        => (await PollAsync(host, model => read(model) ?? double.NaN, reading => IsNear(reading, expected, tolerance), timeoutMs)).Value;

    /// <summary>A number in the object model once the field is set, or NaN when it stays null</summary>
    public static async Task<double> SettledNumberAsync(this DcsTestHost host, Func<ObjectModel, double?> read)
        => (await PollAsync(host, model => read(model) ?? double.NaN, reading => !double.IsNaN(reading), SettleTimeoutMs)).Value;

    /// <summary>Wait until config.g has run to its <see cref="DcsTestHost.ConfigDoneMarker"/> last line</summary>
    public static Task WaitForConfigDoneAsync(this DcsTestHost host, int timeoutMs = 30_000)
        => host.WaitForModelAsync(model => model.Global.ContainsKey("configDone"), true, "config.g setting its done marker", timeoutMs);

    /// <summary>Wait until the machine reports the given status</summary>
    public static Task WaitForStatusAsync(this DcsTestHost host, MachineStatus status, int timeoutMs = WaitTimeoutMs)
        => host.WaitForModelAsync(model => model.State.Status, status, "the machine status", timeoutMs);

    /// <summary>Wait until an axis' live machine position reaches the expected value</summary>
    public static Task WaitForMachinePositionAsync(this Bench bench, int axis, double expected)
        => bench.Host.WaitForNumberAsync(model => axis < model.Move.Axes.Count ? model.Move.Axes[axis].MachinePosition : null,
                                         expected, $"move.axes[{axis}].machinePosition");

    /// <summary>
    /// Wait for a message of the given type containing the given text, and return it. A code run from a macro or a job
    /// reports its reply as a message rather than to a caller
    /// </summary>
    public static async Task<string> WaitForMessageAsync(this DcsTestHost host, MessageType type, string containing, int timeoutMs = WaitTimeoutMs)
        => (await host.WaitForModelAsync(model => model.Messages.FirstOrDefault(message => message.Type == type && message.Content.Contains(containing))?.Content,
                                         content => content is not null, $"a {type} message containing \"{containing}\"", timeoutMs))!;

    /// <summary>Wait until a global holding a number, such as a macro run counter, reaches the expected value</summary>
    public static Task WaitForGlobalAsync(this DcsTestHost host, string name, int expected, int timeoutMs = WaitTimeoutMs)
        => host.WaitForModelAsync(model => ModelReads.GlobalValue(model, name).GetInt32(), expected, $"global.{name}", timeoutMs);

    /// <summary>
    /// Wait until the machine is paused with the pause restore point at the given coordinates. This
    /// is the wait for the second of two pauses in one job: a resume's read-ahead can reach the next
    /// in-file pause so quickly that no intermediate status is ever observable, so the handover is
    /// only visible in the restore point moving on
    /// </summary>
    public static async Task WaitForPauseAtAsync(this DcsTestHost host, double x, double y)
    {
        await host.WaitForModelAsync(model => ModelReads.RestorePoint(model, 1),
                                     point => IsNear(point.X, x, 1e-3) && IsNear(point.Y, y, 1e-3),
                                     $"the pause restore point to be ({x}, {y})");
        await host.WaitForStatusAsync(MachineStatus.Paused);
    }
}
