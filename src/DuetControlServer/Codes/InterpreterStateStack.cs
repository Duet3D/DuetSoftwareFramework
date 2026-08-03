using DuetAPI;
using DuetAPI.ObjectModel;
using System.Collections.Generic;
using System.Linq;

namespace DuetControlServer.Codes;

/// <summary>
/// The interpreter state M120 saves and M121 restores, per code channel
/// </summary>
/// <remarks>
/// <para>
/// This is the state a G-code stream carries rather than anything about the machine: the feed rate,
/// whether coordinates are relative, which units they are in and which plane is selected. Duet Web
/// Control brackets its jog buttons with <c>M120 G91 G1 ... M121</c> precisely so that switching to
/// relative coordinates does not leak back into whatever the user was doing.
/// </para>
/// <para>
/// It is kept here rather than in the object model because it is transient - it describes how the
/// next code will be read, not what the machine is - and because RepRapFirmware does not expose it
/// either. What the object model does carry is <c>inputs[].stackDepth</c>, which this maintains
/// </para>
/// </remarks>
public sealed class InterpreterStateStack
{
    /// <summary>
    /// How deeply a channel may push before M120 is refused
    /// </summary>
    /// <remarks>
    /// A macro looping over M120 without a matching M121 would otherwise grow this without bound.
    /// RepRapFirmware has the same limit on its own stack
    /// </remarks>
    public const int MaxDepth = 10;

    /// <summary>
    /// One saved level
    /// </summary>
    private readonly record struct SavedState(
        float FeedRate,
        bool AxesRelative,
        bool DrivesRelative,
        bool Volumetric,
        DistanceUnit DistanceUnit,
        bool InverseTimeMode,
        int SelectedPlane);

    private readonly Stack<SavedState>[] _stacks = [.. Enumerable.Range(0, Inputs.Total).Select(_ => new Stack<SavedState>())];

    /// <summary>
    /// Save the interpreter state of a channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <returns>True if it was saved, false if the channel has pushed too deeply already</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public bool TryPush(CodeChannel channel, InputChannel input)
    {
        Stack<SavedState> stack = _stacks[(int)channel];
        if (stack.Count >= MaxDepth)
        {
            return false;
        }

        stack.Push(new SavedState(input.FeedRate, input.AxesRelative, input.DrivesRelative, input.Volumetric,
                                  input.DistanceUnit, input.InverseTimeMode, input.SelectedPlane));
        input.StackDepth = (byte)stack.Count;
        return true;
    }

    /// <summary>
    /// Restore the interpreter state of a channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <returns>True if a saved state was restored, false if there was nothing to restore</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public bool TryPop(CodeChannel channel, InputChannel input)
    {
        Stack<SavedState> stack = _stacks[(int)channel];
        if (!stack.TryPop(out SavedState saved))
        {
            return false;
        }

        input.FeedRate = saved.FeedRate;
        input.AxesRelative = saved.AxesRelative;
        input.DrivesRelative = saved.DrivesRelative;
        input.Volumetric = saved.Volumetric;
        input.DistanceUnit = saved.DistanceUnit;
        input.InverseTimeMode = saved.InverseTimeMode;
        input.SelectedPlane = saved.SelectedPlane;
        input.StackDepth = (byte)stack.Count;
        return true;
    }

    /// <summary>
    /// Forget everything a channel saved, because the channel is being reset
    /// </summary>
    /// <param name="channel">Code channel</param>
    public void Clear(CodeChannel channel) => _stacks[(int)channel].Clear();
}
