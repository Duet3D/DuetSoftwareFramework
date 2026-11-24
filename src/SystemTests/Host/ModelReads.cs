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
    /// <summary>
    /// A field as a number, or NaN where the model leaves it null. Reading it as NaN fails the
    /// assertion that wanted a number rather than the test that asked for the field
    /// </summary>
    public static Task<double> ReadNumberAsync(this DcsTestHost host, Func<DuetControlServer.Model.ObjectModel, double?> read)
        => host.ReadModelAsync(model => read(model) ?? double.NaN);

    /// <summary>Where an axis has got to, in mm</summary>
    public static Task<double> MachinePositionAsync(this DcsTestHost host, int axis)
        => host.ReadModelAsync(model => (double)(model.Move.Axes[axis].MachinePosition ?? 0.0f));

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

    /// <summary>Read a global a macro records a yes or no in</summary>
    public static Task<bool> GlobalFlagAsync(this DcsTestHost host, string name)
        => host.ReadModelAsync(model => GlobalValue(model, name).GetBoolean());

    /// <summary>Read a global a macro records a name in</summary>
    public static Task<string> GlobalTextAsync(this DcsTestHost host, string name)
        => host.ReadModelAsync(model => GlobalValue(model, name).GetString() ?? string.Empty);

    /// <summary>Read a global holding an array of whole numbers</summary>
    public static Task<int[]> GlobalArrayAsync(this DcsTestHost host, string name)
        => host.ReadModelAsync(model => GlobalValue(model, name).EnumerateArray().Select(item => item.GetInt32()).ToArray());

    /// <summary>
    /// One global variable, as the object model holds it. The globals are JSON rather than typed
    /// fields, so this fails naming the variable when it is not set at all, which is a different
    /// fault from it holding the wrong thing
    /// </summary>
    public static JsonElement GlobalValue(DuetControlServer.Model.ObjectModel model, string name)
        => model.Global.TryGetValue(name, out JsonElement? value) && value is JsonElement set
            ? set
            : throw new AssertionException($"global.{name} is not set");

    /// <summary>
    /// The X and Y coordinates of a restore point of the first motion system. Both come out of one
    /// read, so a move landing between them cannot produce a pair the machine was never at
    /// </summary>
    public static Task<(double X, double Y)> RestorePointAsync(this DcsTestHost host, int slot)
        => host.ReadModelAsync(model => RestorePoint(model, slot));

    /// <summary>The coordinates <see cref="RestorePointAsync"/> reads, for a wait on them</summary>
    public static (double X, double Y) RestorePoint(DuetControlServer.Model.ObjectModel model, int slot)
        => (model.Move.MotionSystems[0].RestorePoints[slot].Coords[0],
            model.Move.MotionSystems[0].RestorePoints[slot].Coords[1]);
}
