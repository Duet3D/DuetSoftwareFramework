using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>Finding a channel in inputs[], whose interpreter state these scenarios read, and waiting for an axis to arrive</summary>
internal static class InputChannels
{
    /// <summary>
    /// The index of the HTTP channel in inputs[], found by name because ExecuteCodeAsync runs
    /// codes on that channel and the channel order is not part of the contract under test
    /// </summary>
    internal static Task<int> HttpInputIndexAsync(DcsTestHost host) => ChannelInputIndexAsync(host, CodeChannel.HTTP);

    /// <summary>The index of a named channel in inputs[], so its interpreter state can be read</summary>
    internal static async Task<int> ChannelInputIndexAsync(DcsTestHost host, CodeChannel channel)
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

    /// <summary>Poll the live machine position of an axis until it reaches the expected value</summary>
    internal static Task WaitForMachinePositionAsync(Bench bench, int axis, double expected)
        => bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Move.Axes.Count > axis
                  && bench.Host.Model.Move.Axes[axis].MachinePosition is float position
                  && Math.Abs(position - expected) < 1e-3,
            what: $"move.axes[{axis}].machinePosition reaching {expected}");
}
