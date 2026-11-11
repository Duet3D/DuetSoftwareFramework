using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Host;

/// <summary>Reads of the object model the scenarios repeat</summary>
internal static class ModelReads
{
    /// <summary>Where an axis has got to, in mm</summary>
    public static Task<double> MachinePositionAsync(this DcsTestHost host, int axis)
        => host.ReadModelAsync(model => (double)(model.Move.Axes[axis].MachinePosition ?? 0.0f));

    /// <summary>
    /// Wait until an axis' live machine position reaches the expected value. The position is read
    /// outside the model lock, as the controller's reports update it
    /// </summary>
    public static Task WaitForMachinePositionAsync(this Bench bench, int axis, double expected)
        => bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Move.Axes.Count > axis
                  && bench.Host.Model.Move.Axes[axis].MachinePosition is float position
                  && Math.Abs(position - expected) < 1e-3,
            what: $"move.axes[{axis}].machinePosition reaching {expected}");

    /// <summary>
    /// The index of a channel in inputs[], for a macro that reads it as <c>inputs[n]</c>. Found by
    /// name because the channel order is not part of any contract under test. HTTP by default, as
    /// that is the channel <see cref="DcsTestHost.ExecuteCodeAsync"/> runs codes on
    /// </summary>
    public static async Task<int> InputIndexAsync(this DcsTestHost host, CodeChannel channel = CodeChannel.HTTP)
    {
        using (await host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            for (int i = 0; i < host.Model.Inputs.Count; i++)
            {
                if (host.Model.Inputs[i]?.Name == channel)
                {
                    return i;
                }
            }
        }
        throw new AssertionException($"inputs[] does not contain the {channel} channel");
    }

    /// <summary>Read part of a channel's inputs[] entry, the HTTP channel's by default</summary>
    public static Task<T> ReadInputAsync<T>(this DcsTestHost host, Func<InputChannel, T> read, CodeChannel channel = CodeChannel.HTTP)
        => host.ReadModelAsync(model => read(model.Inputs[channel] ?? throw new AssertionException($"inputs[] does not contain the {channel} channel")));

    /// <summary>Read one axis' value by its letter</summary>
    public static Task<T> ReadAxisAsync<T>(this DcsTestHost host, char letter, Func<Axis, T> read)
        => host.ReadModelAsync(model => read(model.Move.Axes.First(axis => axis.Letter == letter)));

    /// <summary>Read a global holding a number, such as a macro run counter</summary>
    public static Task<int> GlobalAsync(this DcsTestHost host, string name)
        => host.ReadModelAsync(model => GlobalValue(model, name).GetInt32());

    /// <summary>
    /// Poll a global holding a number, such as a macro run counter, until it reaches the expected
    /// value, bounded so a macro that never runs fails the test naming the global
    /// </summary>
    public static async Task WaitForGlobalAsync(this DcsTestHost host, string name, int expected, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int value;
        do
        {
            value = await host.GlobalAsync(name);
            if (value == expected)
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"global.{name} stayed {value}, expected {expected}");
    }

    /// <summary>Read a global a macro records a yes or no in</summary>
    public static Task<bool> GlobalFlagAsync(this DcsTestHost host, string name)
        => host.ReadModelAsync(model => GlobalValue(model, name).GetBoolean());

    /// <summary>Read a global a macro records a name in</summary>
    public static Task<string> GlobalTextAsync(this DcsTestHost host, string name)
        => host.ReadModelAsync(model => GlobalValue(model, name).GetString() ?? string.Empty);

    /// <summary>
    /// One global variable, as the object model holds it. The globals are JSON rather than typed
    /// fields, so this fails naming the variable when it is not set at all, which is a different
    /// fault from it holding the wrong thing
    /// </summary>
    private static JsonElement GlobalValue(DuetControlServer.Model.ObjectModel model, string name)
        => model.Global.TryGetValue(name, out JsonElement? value) && value is JsonElement set
            ? set
            : throw new AssertionException($"global.{name} is not set");

    /// <summary>
    /// The X and Y coordinates of a restore point of the first motion system. Both come out of one
    /// read, so a move landing between them cannot produce a pair the machine was never at
    /// </summary>
    public static Task<(double X, double Y)> RestorePointAsync(this DcsTestHost host, int slot)
        => host.ReadModelAsync(model => ((double)model.Move.MotionSystems[0].RestorePoints[slot].Coords[0],
                                         (double)model.Move.MotionSystems[0].RestorePoints[slot].Coords[1]));

    /// <summary>Wait until part of the object model equals the given value</summary>
    /// <param name="host">The host to ask</param>
    /// <param name="read">What to read out of the object model, e.g. an endstop's triggered flag</param>
    /// <param name="expected">The value it should reach</param>
    /// <param name="what">What is being waited for, for the timeout message</param>
    /// <param name="timeoutMs">How long to wait</param>
    /// <remarks>
    /// What a board reports arrives asynchronously, so a scenario that depends on the machine having
    /// taken it in has to wait for it rather than for a round trip of its own
    /// </remarks>
    public static async Task WaitForModelAsync<T>(this DcsTestHost host, Func<DuetControlServer.Model.ObjectModel, T> read,
                                                  T expected, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T value;
        do
        {
            value = await host.ReadModelAsync(read);
            if (Equals(value, expected))
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"{what} stayed \"{value}\", expected \"{expected}\"");
    }
}
