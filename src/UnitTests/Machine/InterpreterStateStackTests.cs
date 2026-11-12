using System;
using System.Runtime.CompilerServices;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// Tests for the machine state stack that macros and M120 push levels on
/// </summary>
/// <remarks>
/// Duet Web Control brackets its jog buttons with <c>M120 G91 G1 ... M121</c>, so a pop that fails to
/// restore the relative-coordinate flag leaves the machine interpreting every later coordinate as an
/// offset. RepRapFirmware's <c>GCodeMachineState</c> chain is the reference for the rest
/// </remarks>
[TestFixture]
public class InterpreterStateStackTests
{
    private static InputChannel NewInput() => new()
    {
        FeedRate = 50.0f,
        AxesRelative = false,
        DrivesRelative = false,
        Volumetric = false,
        DistanceUnit = DistanceUnit.MM,
        InverseTimeMode = false,
        SelectedPlane = 0
    };

    /// <summary>
    /// A macro to own a level. The stack uses it for its identity alone, and a real one needs a whole code pipeline
    /// </summary>
    private static MacroFile NewMacro()
    {
        MacroFile macro = (MacroFile)RuntimeHelpers.GetUninitializedObject(typeof(MacroFile));
        GC.SuppressFinalize(macro);
        return macro;
    }

    [Test]
    public void APoppedStateIsTheOneThatWasPushed()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        Assert.That(stack.TryPushLocal(CodeChannel.HTTP, input), Is.True);

        // What a jog does between the push and the pop
        input.AxesRelative = true;
        input.FeedRate = 1000.0f;
        input.DistanceUnit = DistanceUnit.Inch;
        input.Compatibility = Compatibility.Marlin;

        Assert.That(stack.TryPopLocal(CodeChannel.HTTP, input), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(input.AxesRelative, Is.False);
            Assert.That(input.FeedRate, Is.EqualTo(50.0f));
            Assert.That(input.DistanceUnit, Is.EqualTo(DistanceUnit.MM));
            Assert.That(input.Compatibility, Is.EqualTo(Compatibility.RepRapFirmware));
        });
    }

    [Test]
    public void TheStackDepthCountsMacroAndM120Levels()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        stack.TryPushLocal(CodeChannel.HTTP, input);
        Assert.That(input.StackDepth, Is.EqualTo(1));
        stack.TryPushMacro(CodeChannel.HTTP, input, NewMacro(), "macro.g");
        Assert.That(input.StackDepth, Is.EqualTo(2));
        stack.TryPushLocal(CodeChannel.HTTP, input);
        Assert.That(input.StackDepth, Is.EqualTo(3));
        stack.TryPopLocal(CodeChannel.HTTP, input);
        Assert.That(input.StackDepth, Is.EqualTo(2));
    }

    [Test]
    public void PoppingWithNothingPushedFails()
    {
        // M121 without a matching M120 is a stack underrun, not a silent no-op
        Assert.That(new InterpreterStateStack().TryPopLocal(CodeChannel.HTTP, NewInput()), Is.False);
    }

    [Test]
    public void M121CannotPopAMacrosLevel()
    {
        // The level an M121 in a macro would reach is the macro's own, and the M120 levels below it are its caller's
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        stack.TryPushLocal(CodeChannel.HTTP, input);
        stack.TryPushMacro(CodeChannel.HTTP, input, NewMacro(), "macro.g");
        input.AxesRelative = true;

        Assert.That(stack.TryPopLocal(CodeChannel.HTTP, input), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(input.StackDepth, Is.EqualTo(2), "nothing was popped");
            Assert.That(input.InMacro, Is.True);
            Assert.That(input.AxesRelative, Is.True, "nothing was restored");
        });
    }

    [Test]
    public void MacroAndM120LevelsShareOneLimit()
    {
        // A macro looping over M120 without M121, or calling itself, would otherwise grow this without bound
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        for (int i = 0; i < InterpreterStateStack.MaxDepth; i++)
        {
            bool pushed = (i % 2 == 0) ? stack.TryPushMacro(CodeChannel.HTTP, input, NewMacro(), "macro.g") : stack.TryPushLocal(CodeChannel.HTTP, input);
            Assert.That(pushed, Is.True, $"push {i}");
        }
        Assert.Multiple(() =>
        {
            Assert.That(stack.TryPushLocal(CodeChannel.HTTP, input), Is.False);
            Assert.That(stack.TryPushMacro(CodeChannel.HTTP, input, NewMacro(), "macro.g"), Is.False);
            Assert.That(input.StackDepth, Is.EqualTo(InterpreterStateStack.MaxDepth));
        });
    }

    [Test]
    public void EachChannelHasItsOwnStack()
    {
        // A macro pushing on the file channel must not let a web request pop its state
        InterpreterStateStack stack = new();
        InputChannel file = NewInput(), http = NewInput();

        stack.TryPushLocal(CodeChannel.File, file);
        Assert.That(stack.TryPopLocal(CodeChannel.HTTP, http), Is.False);
        Assert.That(stack.TryPopLocal(CodeChannel.File, file), Is.True);
    }

    [Test]
    public void AMacroLevelStartsInAMacroWithInverseTimeAndVolumetricOff()
    {
        // GCodeMachineState's copy constructor for a new file
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        input.InverseTimeMode = true;
        input.Volumetric = true;
        stack.SetMacroRestartable(CodeChannel.HTTP, input, true);

        stack.TryPushMacro(CodeChannel.HTTP, input, NewMacro(), "macro.g");
        Assert.Multiple(() =>
        {
            Assert.That(input.InMacro, Is.True);
            Assert.That(input.MacroRestartable, Is.False);
            Assert.That(input.InverseTimeMode, Is.False);
            Assert.That(input.Volumetric, Is.False);
        });
    }

    [Test]
    public void AnM120LevelKeepsTheStateButForVolumetric()
    {
        // GCodeMachineState's copy constructor within the same file
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        MacroFile macro = NewMacro();
        stack.TryPushMacro(CodeChannel.HTTP, input, macro, "macro.g");
        stack.SetMacroRestartable(CodeChannel.HTTP, input, true);
        input.InverseTimeMode = true;
        input.Volumetric = true;

        stack.TryPushLocal(CodeChannel.HTTP, input);
        Assert.Multiple(() =>
        {
            Assert.That(input.InMacro, Is.True);
            Assert.That(input.MacroRestartable, Is.True);
            Assert.That(input.InverseTimeMode, Is.True);
            Assert.That(input.Volumetric, Is.False);
        });

        stack.TryPopLocal(CodeChannel.HTTP, input);
        Assert.That(input.Volumetric, Is.True, "the pop restores it");
    }

    [Test]
    public void PoppingAMacroPopsTheLevelsItLeftOpenAndRestoresItsCaller()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        MacroFile macro = NewMacro();
        input.InverseTimeMode = true;

        stack.TryPushMacro(CodeChannel.HTTP, input, macro, "macro.g");
        input.AxesRelative = true;
        stack.TryPushLocal(CodeChannel.HTTP, input);
        stack.TryPushLocal(CodeChannel.HTTP, input);

        stack.PopMacro(CodeChannel.HTTP, input, macro, keepInterpreterState: false);
        Assert.Multiple(() =>
        {
            Assert.That(input.StackDepth, Is.Zero);
            Assert.That(input.InMacro, Is.False);
            Assert.That(input.AxesRelative, Is.False);
            Assert.That(input.InverseTimeMode, Is.True);
        });
    }

    [Test]
    public void PoppingAMacroWithoutALevelChangesNothing()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        stack.TryPushLocal(CodeChannel.HTTP, input);
        input.AxesRelative = true;

        Assert.That(stack.PopMacro(CodeChannel.HTTP, input, NewMacro(), keepInterpreterState: false), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(input.StackDepth, Is.EqualTo(1));
            Assert.That(input.AxesRelative, Is.True);
        });
    }

    [Test]
    public void AStartupFileKeepsItsStateButNotItsCompatibility()
    {
        // CheckFinishedRunningConfigFile copies the state up with CopyStateFrom, which leaves compatibility out
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        MacroFile config = NewMacro();

        stack.TryPushMacro(CodeChannel.Trigger, input, config, "macro.g");
        input.DrivesRelative = true;
        input.Compatibility = Compatibility.Marlin;
        stack.PopMacro(CodeChannel.Trigger, input, config, keepInterpreterState: true);
        Assert.Multiple(() =>
        {
            Assert.That(input.DrivesRelative, Is.True);
            Assert.That(input.Compatibility, Is.EqualTo(Compatibility.RepRapFirmware));
            Assert.That(input.InMacro, Is.False);
        });
    }

    [Test]
    public void TheJobsLevelsArePoppedRestoringTheStateBeforeThem()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        stack.TryPushLocal(CodeChannel.File, input);
        input.AxesRelative = true;
        stack.TryPushLocal(CodeChannel.File, input);
        input.DrivesRelative = true;

        Assert.That(stack.PopBaseLevels(CodeChannel.File, input), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(input.StackDepth, Is.Zero);
            Assert.That(input.AxesRelative, Is.False);
            Assert.That(input.DrivesRelative, Is.False);
        });
    }

    [Test]
    public void TheJobsLevelsStayWhileAMacroIsRunning()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        stack.TryPushLocal(CodeChannel.File, input);
        stack.TryPushMacro(CodeChannel.File, input, NewMacro(), "macro.g");

        Assert.That(stack.PopBaseLevels(CodeChannel.File, input), Is.False);
        Assert.That(input.StackDepth, Is.EqualTo(2));
    }

    [Test]
    public void EveryMacroLevelMustBeRestartable()
    {
        // GCodeMachineState::CanRestartMacro, which looks at every level in a macro, M120 levels included
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        Assert.That(stack.CanRestartMacros(CodeChannel.File), Is.True, "outside a macro");

        stack.TryPushMacro(CodeChannel.File, input, NewMacro(), "macro.g");
        Assert.That(stack.CanRestartMacros(CodeChannel.File), Is.False, "a macro starts unmarked");

        stack.SetMacroRestartable(CodeChannel.File, input, true);
        Assert.That(stack.CanRestartMacros(CodeChannel.File), Is.True);

        stack.TryPushLocal(CodeChannel.File, input);
        stack.SetMacroRestartable(CodeChannel.File, input, false);
        Assert.That(stack.CanRestartMacros(CodeChannel.File), Is.False);

        stack.SetMacroRestartable(CodeChannel.File, input, true);
        stack.TryPushMacro(CodeChannel.File, input, NewMacro(), "macro.g");
        stack.SetMacroRestartable(CodeChannel.File, input, true);
        Assert.That(stack.CanRestartMacros(CodeChannel.File), Is.True);

        stack.TryPopLocal(CodeChannel.File, input);
        Assert.That(stack.CanRestartMacros(CodeChannel.File), Is.True, "M121 cannot pop the inner macro's level");
    }

    [Test]
    public void M121RestoresTheRestartableFlag()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        stack.SetMacroRestartable(CodeChannel.HTTP, input, true);
        stack.TryPushLocal(CodeChannel.HTTP, input);
        stack.SetMacroRestartable(CodeChannel.HTTP, input, false);
        stack.TryPopLocal(CodeChannel.HTTP, input);
        Assert.That(input.MacroRestartable, Is.True);
    }

    [Test]
    public void AMacroLevelNamesItsFileUntilItIsPopped()
    {
        // GCodeBuffer::StartNewFile names the new level's file; an M120 level keeps its file's name
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();
        input.CurrentFile = "0:/gcodes/job.gcode";
        MacroFile macro = NewMacro();

        stack.TryPushMacro(CodeChannel.File, input, macro, "inner.g");
        Assert.That(input.CurrentFile, Is.EqualTo("inner.g"));
        stack.TryPushLocal(CodeChannel.File, input);
        Assert.That(input.CurrentFile, Is.EqualTo("inner.g"));

        stack.PopMacro(CodeChannel.File, input, macro, keepInterpreterState: false);
        Assert.That(input.CurrentFile, Is.EqualTo("0:/gcodes/job.gcode"));
    }

    [Test]
    public void ConfigStateCopiesEverythingButCompatibility()
    {
        InputChannel config = NewInput(), other = NewInput();
        config.DrivesRelative = true;
        config.FeedRate = 1234.0f;
        config.Compatibility = Compatibility.Marlin;

        InterpreterStateStack.CopyConfigState(config, other);
        Assert.Multiple(() =>
        {
            Assert.That(other.DrivesRelative, Is.True);
            Assert.That(other.FeedRate, Is.EqualTo(1234.0f));
            Assert.That(other.Compatibility, Is.EqualTo(Compatibility.RepRapFirmware));
        });
    }

    [Test]
    public void AChannelWithoutAnInputsEntryStillHasAStack()
    {
        InterpreterStateStack stack = new();
        Assert.That(stack.TryPushLocal(CodeChannel.HTTP, null), Is.True);
        Assert.That(stack.TryPopLocal(CodeChannel.HTTP, null), Is.True);
        Assert.That(stack.TryPopLocal(CodeChannel.HTTP, null), Is.False);
    }
}
