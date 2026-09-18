using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// Tests for the interpreter state M120 saves and M121 restores
/// </summary>
/// <remarks>
/// Duet Web Control brackets its jog buttons with <c>M120 G91 G1 ... M121</c>, so a pop that fails to
/// restore the relative-coordinate flag leaves the machine interpreting every later coordinate as an
/// offset. That is what these cover
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

    [Test]
    public void APoppedStateIsTheOneThatWasPushed()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        Assert.That(stack.TryPush(CodeChannel.HTTP, input), Is.True);

        // What a jog does between the push and the pop
        input.AxesRelative = true;
        input.FeedRate = 1000.0f;
        input.DistanceUnit = DistanceUnit.Inch;

        Assert.That(stack.TryPop(CodeChannel.HTTP, input), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(input.AxesRelative, Is.False);
            Assert.That(input.FeedRate, Is.EqualTo(50.0f));
            Assert.That(input.DistanceUnit, Is.EqualTo(DistanceUnit.MM));
        });
    }

    [Test]
    public void TheStackDepthIsReportedInTheObjectModel()
    {
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        stack.TryPush(CodeChannel.HTTP, input);
        Assert.That(input.StackDepth, Is.EqualTo(1));
        stack.TryPush(CodeChannel.HTTP, input);
        Assert.That(input.StackDepth, Is.EqualTo(2));
        stack.TryPop(CodeChannel.HTTP, input);
        Assert.That(input.StackDepth, Is.EqualTo(1));
    }

    [Test]
    public void PoppingWithNothingPushedFails()
    {
        // M121 without a matching M120 is a stack underrun, not a silent no-op
        Assert.That(new InterpreterStateStack().TryPop(CodeChannel.HTTP, NewInput()), Is.False);
    }

    [Test]
    public void PushingTooDeeplyFails()
    {
        // A macro looping over M120 without M121 would otherwise grow this without bound
        InterpreterStateStack stack = new();
        InputChannel input = NewInput();

        for (int i = 0; i < InterpreterStateStack.MaxDepth; i++)
        {
            Assert.That(stack.TryPush(CodeChannel.HTTP, input), Is.True, $"push {i}");
        }
        Assert.That(stack.TryPush(CodeChannel.HTTP, input), Is.False);
    }

    [Test]
    public void EachChannelHasItsOwnStack()
    {
        // A macro pushing on the file channel must not let a web request pop its state
        InterpreterStateStack stack = new();
        InputChannel file = NewInput(), http = NewInput();

        stack.TryPush(CodeChannel.File, file);
        Assert.That(stack.TryPop(CodeChannel.HTTP, http), Is.False);
        Assert.That(stack.TryPop(CodeChannel.File, file), Is.True);
    }
}
