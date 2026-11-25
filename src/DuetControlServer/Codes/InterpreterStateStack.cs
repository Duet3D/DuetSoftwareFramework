using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using System.Collections.Generic;
using System.Linq;

namespace DuetControlServer.Codes;

/// <summary>
/// The machine state stack of every code channel: one level for each macro the channel is running and one for each
/// M120 it has not matched with an M121
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's chain of <c>GCodeMachineState</c>s. A level saves the interpreter state of the level below it,
/// which is put back when the level is popped: the feed rate, relativity, units, plane, time mode, volumetric
/// extrusion and compatibility, the file it was running, and whether it was in a macro and marked restartable. The
/// current values are the channel's <c>inputs[]</c> entry, where this also publishes <c>stackDepth</c>,
/// <c>inMacro</c> and <c>macroRestartable</c>.
/// </para>
/// <para>
/// A level belongs to the file that pushed it. A macro's level is popped, with the M120 levels it left open, when
/// the macro ends; M121 pops only an M120 level of its own file; and the levels a job file pushed at the base of its
/// channel are popped when the job ends.
/// </para>
/// <para>
/// A method that changes a stack publishes the change to the channel's <see cref="InputChannel"/>, so its caller must
/// hold the object model's write lock. Each stack also has a lock of its own, so that
/// <see cref="CanRestartMacros"/> can be read without the object model's
/// </para>
/// </remarks>
public sealed class InterpreterStateStack
{
    /// <summary>
    /// How many levels a channel's stack holds, macro and M120 levels together
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxStackDepth</c></remarks>
    public const int MaxDepth = 10;

    /// <summary>
    /// Error a code that would push beyond <see cref="MaxDepth"/> is refused with
    /// </summary>
    /// <param name="channel">Channel whose stack is full</param>
    /// <returns>Error text</returns>
    public static string OverflowError(CodeChannel channel) => $"Push(): stack overflow on {channel}";

    /// <summary>
    /// Error M121 is refused with when its file has no M120 level to pop
    /// </summary>
    /// <param name="channel">Channel M121 ran on</param>
    /// <returns>Error text</returns>
    public static string UnderflowError(CodeChannel channel) => $"Pop(): stack underflow on {channel}";

    /// <summary>
    /// Interpreter state a level saves of the level below it
    /// </summary>
    private readonly record struct InterpreterState(
        float FeedRate,
        bool AxesRelative,
        bool DrivesRelative,
        bool Volumetric,
        DistanceUnit DistanceUnit,
        bool InverseTimeMode,
        int SelectedPlane,
        Compatibility Compatibility)
    {
        public static InterpreterState Capture(InputChannel input)
            => new(input.FeedRate, input.AxesRelative, input.DrivesRelative, input.Volumetric,
                   input.DistanceUnit, input.InverseTimeMode, input.SelectedPlane, input.Compatibility);

        public void Restore(InputChannel input)
        {
            input.FeedRate = FeedRate;
            input.AxesRelative = AxesRelative;
            input.DrivesRelative = DrivesRelative;
            input.Volumetric = Volumetric;
            input.DistanceUnit = DistanceUnit;
            input.InverseTimeMode = InverseTimeMode;
            input.SelectedPlane = SelectedPlane;
            input.Compatibility = Compatibility;
        }
    }

    /// <summary>
    /// Put a channel into the interpreter state of another
    /// </summary>
    /// <param name="from">Channel to copy from</param>
    /// <param name="to">Channel to copy to</param>
    /// <remarks>RepRapFirmware's <c>GCodeMachineState::CopyStateFrom</c>, which leaves the compatibility alone</remarks>
    public static void CopyState(InputChannel from, InputChannel to)
        => (InterpreterState.Capture(from) with { Compatibility = to.Compatibility }).Restore(to);

    /// <summary>
    /// One level of a stack: what it saved of the level below, and the macro that owns it if it is a macro's level
    /// </summary>
    private sealed record Level(InterpreterState? Interpreter, string? CurrentFile, bool InMacro, bool MacroRestartable, MacroFile? Macro);

    /// <summary>
    /// The stack of one channel, with the macro flags of its current level
    /// </summary>
    /// <remarks>The flags are kept here as well as in the object model so that they can be read under this lock alone</remarks>
    private sealed class ChannelStack
    {
        public readonly List<Level> Levels = [];
        public bool InMacro;
        public bool MacroRestartable;
    }

    private readonly ChannelStack[] _channels = [.. Enumerable.Range(0, Inputs.Total).Select(_ => new ChannelStack())];

    /// <summary>
    /// Push the level a macro runs on
    /// </summary>
    /// <param name="channel">Channel the macro runs on</param>
    /// <param name="input">The channel's object model entry, if it has one</param>
    /// <param name="macro">Macro that owns the level</param>
    /// <param name="fileName">Name the macro was started with, which becomes <c>inputs[].currentFile</c></param>
    /// <returns>False if the stack is full</returns>
    /// <remarks>
    /// The macro starts in a macro, not restartable, with inverse time mode and volumetric extrusion off, as the
    /// RepRapFirmware <c>GCodeMachineState</c> copy constructor starts a level for a new file
    /// </remarks>
    public bool TryPushMacro(CodeChannel channel, InputChannel? input, MacroFile macro, string fileName)
        => TryPush(channel, input, macro, fileName);

    /// <summary>
    /// Push a level for M120
    /// </summary>
    /// <param name="channel">Channel M120 ran on</param>
    /// <param name="input">The channel's object model entry, if it has one</param>
    /// <returns>False if the stack is full</returns>
    /// <remarks>
    /// The level keeps the current state but for volumetric extrusion, which starts off, as the RepRapFirmware
    /// <c>GCodeMachineState</c> copy constructor starts a level within the same file
    /// </remarks>
    public bool TryPushLocal(CodeChannel channel, InputChannel? input) => TryPush(channel, input, null, null);

    private bool TryPush(CodeChannel channel, InputChannel? input, MacroFile? macro, string? fileName)
    {
        ChannelStack stack = _channels[(int)channel];
        lock (stack)
        {
            if (stack.Levels.Count >= MaxDepth)
            {
                return false;
            }

            stack.Levels.Add(new Level(input is null ? null : InterpreterState.Capture(input), input?.CurrentFile, stack.InMacro, stack.MacroRestartable, macro));
            if (macro is not null)
            {
                stack.InMacro = true;
                stack.MacroRestartable = false;
            }
            if (input is not null)
            {
                input.Volumetric = false;
                if (macro is not null)
                {
                    input.InverseTimeMode = false;
                    input.CurrentFile = fileName;
                }
            }
            Publish(stack, input);
            return true;
        }
    }

    /// <summary>
    /// Pop the M120 level on top of a channel's stack for M121
    /// </summary>
    /// <param name="channel">Channel M121 ran on</param>
    /// <param name="input">The channel's object model entry, if it has one</param>
    /// <returns>False if the top level is not an M120 level, which leaves the stack as it was</returns>
    /// <remarks>
    /// The codes of a file run only while its level, or one of its own M120 levels, is on top, so an M120 level on
    /// top belongs to the file M121 is in. A macro's level is never popped here: it is the macro's to pop when it ends
    /// </remarks>
    public bool TryPopLocal(CodeChannel channel, InputChannel? input)
    {
        ChannelStack stack = _channels[(int)channel];
        lock (stack)
        {
            if (stack.Levels.Count == 0 || stack.Levels[^1].Macro is not null)
            {
                return false;
            }
            PopFrom(stack, input, stack.Levels.Count - 1, keepInterpreterState: false);
            return true;
        }
    }

    /// <summary>
    /// Pop a macro's level and the M120 levels it left open above it
    /// </summary>
    /// <param name="channel">Channel the macro ran on</param>
    /// <param name="input">The channel's object model entry, if it has one</param>
    /// <param name="macro">Macro that has ended</param>
    /// <param name="keepInterpreterState">
    /// Keep the macro's interpreter state rather than restoring its caller's, as a startup file does. The
    /// compatibility is restored all the same, as RepRapFirmware's <c>CopyStateFrom</c> leaves it out
    /// </param>
    /// <returns>False if the macro has no level on the stack, which leaves the stack as it was</returns>
    /// <remarks>
    /// A macro's level is popped after the level of every macro it started, because the code that starts a macro
    /// waits for its runner to pop. Only M120 levels can be above it
    /// </remarks>
    public bool PopMacro(CodeChannel channel, InputChannel? input, MacroFile macro, bool keepInterpreterState)
    {
        ChannelStack stack = _channels[(int)channel];
        lock (stack)
        {
            int index = stack.Levels.FindLastIndex(level => ReferenceEquals(level.Macro, macro));
            if (index < 0)
            {
                return false;
            }
            PopFrom(stack, input, index, keepInterpreterState);
            return true;
        }
    }

    /// <summary>
    /// Pop the levels a job file left open at the base of its channel, restoring the state from before them
    /// </summary>
    /// <param name="channel">Channel the job ran on</param>
    /// <param name="input">The channel's object model entry, if it has one</param>
    /// <returns>False if a macro still has a level on the stack, which leaves the stack as it was</returns>
    /// <remarks>
    /// RepRapFirmware pops them at the end of the file and in <c>GCodeBuffer::AbortFile</c>. No macro can be running
    /// on the channel by then: the job file's codes, and the macros they started, have finished before its file is
    /// closed, and the job's own macros run in sequences that wait for them
    /// </remarks>
    public bool PopBaseLevels(CodeChannel channel, InputChannel? input)
    {
        ChannelStack stack = _channels[(int)channel];
        lock (stack)
        {
            if (stack.Levels.Any(level => level.Macro is not null))
            {
                return false;
            }
            if (stack.Levels.Count > 0)
            {
                PopFrom(stack, input, 0, keepInterpreterState: false);
            }
            return true;
        }
    }

    /// <summary>
    /// Mark the current level of a channel restartable or not, for M98 R
    /// </summary>
    /// <param name="channel">Channel M98 ran on</param>
    /// <param name="input">The channel's object model entry, if it has one</param>
    /// <param name="restartable">Whether the level may be interrupted and restarted</param>
    public void SetMacroRestartable(CodeChannel channel, InputChannel? input, bool restartable)
    {
        ChannelStack stack = _channels[(int)channel];
        lock (stack)
        {
            stack.MacroRestartable = restartable;
            Publish(stack, input);
        }
    }

    /// <summary>
    /// Whether every macro level of a channel, and every M120 level inside a macro, is marked restartable
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <returns>True if a pause may interrupt the channel</returns>
    /// <remarks>RepRapFirmware's <c>GCodeMachineState::CanRestartMacro</c></remarks>
    public bool CanRestartMacros(CodeChannel channel)
    {
        ChannelStack stack = _channels[(int)channel];
        lock (stack)
        {
            return (!stack.InMacro || stack.MacroRestartable) &&
                   stack.Levels.All(level => !level.InMacro || level.MacroRestartable);
        }
    }

    /// <summary>
    /// Copy the interpreter state config.g finished with to another channel
    /// </summary>
    /// <param name="from">The channel config.g ran on</param>
    /// <param name="to">Channel to copy it to</param>
    /// <remarks>RepRapFirmware's <c>CopyStateFrom</c>, which leaves the compatibility out</remarks>
    public static void CopyConfigState(InputChannel from, InputChannel to)
    {
        to.FeedRate = from.FeedRate;
        to.AxesRelative = from.AxesRelative;
        to.DrivesRelative = from.DrivesRelative;
        to.Volumetric = from.Volumetric;
        to.DistanceUnit = from.DistanceUnit;
        to.InverseTimeMode = from.InverseTimeMode;
        to.SelectedPlane = from.SelectedPlane;
    }

    /// <summary>
    /// Remove the level at an index and every level above it, restoring what that level saved
    /// </summary>
    private static void PopFrom(ChannelStack stack, InputChannel? input, int index, bool keepInterpreterState)
    {
        Level level = stack.Levels[index];
        stack.Levels.RemoveRange(index, stack.Levels.Count - index);
        stack.InMacro = level.InMacro;
        stack.MacroRestartable = level.MacroRestartable;
        if (input is not null)
        {
            input.CurrentFile = level.CurrentFile;
        }
        if (input is not null && level.Interpreter is InterpreterState saved)
        {
            if (keepInterpreterState)
            {
                input.Compatibility = saved.Compatibility;
            }
            else
            {
                saved.Restore(input);
            }
        }
        Publish(stack, input);
    }

    /// <summary>
    /// Publish a channel's stack to its object model entry
    /// </summary>
    private static void Publish(ChannelStack stack, InputChannel? input)
    {
        if (input is not null)
        {
            input.StackDepth = (byte)stack.Levels.Count;
            input.InMacro = stack.InMacro;
            input.MacroRestartable = stack.MacroRestartable;
        }
    }
}
