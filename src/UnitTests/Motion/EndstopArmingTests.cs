using System;
using System.Collections.Generic;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using OmDriverId = DuetAPI.Utility.DriverId;

namespace UnitTests.Motion;

/// <summary>
/// Which endstop stops which motor, and which motors a move must not start
/// </summary>
/// <remarks>
/// These are the rules that have been wrong in practice, and every one of them fails silently: an
/// axis armed on the wrong switch runs into it, an axis held when it should not have moved reports
/// itself homed without moving, and a motor started when it was already down drives into a closed
/// switch. None of that is visible from the object model afterwards, which is why the decision is
/// made somewhere it can be tested against a machine description rather than against a printer
/// </remarks>
[TestFixture]
public class EndstopArmingTests
{
    private const int NumAxes = 3;

    /// <summary>
    /// A machine with three axes, one driver and one switch each unless Y is asked for more
    /// </summary>
    /// <remarks>
    /// The geometry is passed to the arming separately, so it is not described here: what the object
    /// model carries is the axes, their drivers and their endstop ports
    /// </remarks>
    private static (Move Move, Sensors Sensors) Machine(int yDrivers = 1, int ySwitches = 1,
                                                        int xDrivers = 1, int xSwitches = 1)
    {
        Move move = new();
        Sensors sensors = new();

        char[] letters = ['X', 'Y', 'Z'];
        for (int axis = 0; axis < NumAxes; axis++)
        {
            Axis a = new() { Letter = letters[axis] };
            int drivers = axis switch { 0 => xDrivers, 1 => yDrivers, _ => 1 };
            for (int i = 0; i < drivers; i++)
            {
                a.Drivers.Add(new OmDriverId(1, (axis * 4) + i));
            }
            move.Axes.Add(a);

            // Switch i of an axis is a port of its own, which is how M574 registers them
            List<string> ports = [];
            int switches = axis switch { 0 => xSwitches, 1 => ySwitches, _ => 1 };
            for (int i = 0; i < switches; i++)
            {
                ports.Add($"1.io{(axis * 4) + i}.in");
            }
            sensors.Endstops.Add(new Endstop
            {
                Type = EndstopType.InputPin,
                Port = string.Join(RemoteEndstops.PortSeparator, ports),
                HighEnd = false
            });
        }
        return (move, sensors);
    }

    private static MoveStopInput[] NewStopInputs()
    {
        MoveStopInput[] stopInputs = new MoveStopInput[MotionLimits.MaxAxesPlusExtruders];
        for (int i = 0; i < stopInputs.Length; i++)
        {
            stopInputs[i] = new MoveStopInput();
        }
        return stopInputs;
    }

    private static ArmedMove Arm((Move Move, Sensors Sensors) machine, MoveStopInput[] stopInputs,
                                 IReadOnlyList<int> axes, Func<int, uint>? closed = null,
                                 KinematicsName kinematics = KinematicsName.Cartesian)
        => EndstopArming.Arm(machine.Move, machine.Sensors, KinematicsFactory.Create(kinematics),
                             NumAxes, axes, closed ?? (_ => 0), stopInputs);

    [Test]
    public void AnIndependentAxisIsStoppedByItsOwnSwitch()
    {
        // stopAxis: nothing but this axis' drivers watches anything, so another axis in the same
        // move keeps its own endstop
        (Move move, Sensors sensors) = Machine();
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [0, 2]);

        Assert.Multiple(() =>
        {
            Assert.That(armed.ArmedAxes, Is.EqualTo(new[] { 0, 2 }));
            Assert.That(stopInputs[0].NumSwitches, Is.EqualTo(1), "X watches its own switch");
            Assert.That(stopInputs[2].NumSwitches, Is.EqualTo(1), "and Z its own");
            Assert.That(stopInputs[1].NumSwitches, Is.Zero, "an axis the code did not name watches nothing");
            Assert.That(stopInputs[0].Handle, Is.Not.EqualTo(stopInputs[2].Handle), "under distinct handles");
            Assert.That(armed.AxesToHold, Is.Empty);
            Assert.That(armed.TriggeredAxes, Is.Zero);
        });
    }

    [Test]
    public void ACoupledAxisArmsEveryDriveOnTheOneSwitch()
    {
        // stopAll. On a CoreXY holding X still needs both motors, so stopping only "X's drivers"
        // would leave the other running and drag the head diagonally into the switch
        (Move move, Sensors sensors) = Machine();
        MoveStopInput[] stopInputs = NewStopInputs();

        Arm((move, sensors), stopInputs, [0], kinematics: KinematicsName.CoreXY);

        Assert.Multiple(() =>
        {
            Assert.That(stopInputs[0].NumSwitches, Is.EqualTo(1));
            Assert.That(stopInputs[1].Handle, Is.EqualTo(stopInputs[0].Handle), "Y's drive watches X's switch");
            Assert.That(stopInputs[2].Handle, Is.EqualTo(stopInputs[0].Handle), "and so does every other drive");
        });
    }

    [Test]
    public void ACoupledAxisKeepsEveryOneOfItsSwitches()
    {
        // The bug this replaced: demoting to stopAll collapsed the axis to its first switch, so the
        // second was armed on nothing - it did nothing, and M119 still showed it because the state
        // comes from the board. RepRapFirmware watches every port of an endstop whatever the action
        // M584 X1.0:2.0, M669 K1, M574 X1 P"2.io1.in+1.io0.in"
        (Move move, Sensors sensors) = Machine(xDrivers: 2, xSwitches: 2);
        sensors.Endstops[0]!.Port = "2.io1.in+1.io0.in";
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [0], kinematics: KinematicsName.CoreXY);

        Assert.Multiple(() =>
        {
            Assert.That(armed.StopsEveryDrive, Is.True, "any of them has to stop the whole move");
            Assert.That(stopInputs[0].NumSwitches, Is.EqualTo(2), "both switches are kept");
            Assert.That(stopInputs[0].Boards[0], Is.EqualTo(2), "the first on its own board");
            Assert.That(stopInputs[0].Boards[1], Is.EqualTo(1), "and the second on its own");
            Assert.That(stopInputs[1].NumSwitches, Is.EqualTo(2), "every drive carries them");
            Assert.That(stopInputs[1].Boards[1], Is.EqualTo(1));
        });
    }

    [Test]
    public void AnIndependentAxisDoesNotStopEveryDrive()
    {
        (Move move, Sensors sensors) = Machine();
        MoveStopInput[] stopInputs = NewStopInputs();

        Assert.That(Arm((move, sensors), stopInputs, [0]).StopsEveryDrive, Is.False);
    }

    [Test]
    public void TwoCoupledAxesCannotBeHomedTogether()
    {
        // A drive carries one stop input, so the second endstop would have nowhere to live. Refusing
        // is what stops one of the two being silently disarmed
        (Move move, Sensors sensors) = Machine();
        MoveStopInput[] stopInputs = NewStopInputs();

        Assert.Throws<GCodeException>(() => Arm((move, sensors), stopInputs, [0, 1],
                                                kinematics: KinematicsName.CoreXY));
    }

    [Test]
    public void ACoupledAxisCannotBeHomedAlongsideAnIndependentOne()
    {
        (Move move, Sensors sensors) = Machine();
        MoveStopInput[] stopInputs = NewStopInputs();

        Assert.Throws<GCodeException>(() => Arm((move, sensors), stopInputs, [0, 2],
                                                kinematics: KinematicsName.CoreXY));
    }

    [Test]
    public void AnAxisWithNoEndstopIsRefusedRatherThanLeftUnarmed()
    {
        // Carrying on would run the move to its full commanded length with nothing to stop it, which
        // for a homing move means driving into the end of the axis
        (Move move, Sensors sensors) = Machine();
        sensors.Endstops[0]!.Port = null;
        MoveStopInput[] stopInputs = NewStopInputs();

        GCodeException? thrown = Assert.Throws<GCodeException>(() => Arm((move, sensors), stopInputs, [0]));
        Assert.That(thrown!.Message, Does.Contain("no port"), "and says why, since the axis letter alone does not");
    }

    [Test]
    public void AClosedSwitchHoldsTheAxisRatherThanDrivingIntoIt()
    {
        // The controller only stops a move when an input *changes*, so a switch already closed would
        // never report anything. The axis is commanded to stay where it is and counts as triggered,
        // because it is at its switch - which is the whole question a homing move asks
        (Move move, Sensors sensors) = Machine();
        sensors.Endstops[0]!.Triggered = true;
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [0], closed: axis => axis == 0 ? 0b1u : 0u);

        Assert.Multiple(() =>
        {
            Assert.That(armed.AxesToHold, Is.EqualTo(new[] { 0 }));
            Assert.That(armed.TriggeredAxes, Is.EqualTo(1u), "and it counts as having triggered");
            Assert.That(stopInputs[0].HeldDrivers, Is.Zero, "a single-switch axis holds the drive, not a motor");
        });
    }

    [Test]
    public void OneClosedSwitchOfAGantryHoldsOnlyThatMotor()
    {
        // The move that squares a gantry is exactly the one that starts with one side already down.
        // Holding the whole axis would make it do nothing and then call the axis homed
        (Move move, Sensors sensors) = Machine(yDrivers: 2, ySwitches: 2);
        sensors.Endstops[1]!.Triggered = true;
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [1], closed: axis => axis == 1 ? 0b10u : 0u);

        Assert.Multiple(() =>
        {
            Assert.That(armed.AxesToHold, Is.Empty, "the axis still has a motor to move");
            Assert.That(armed.TriggeredAxes, Is.Zero, "and has not finished homing, so it is not latched");
            Assert.That(stopInputs[1].NumSwitches, Is.EqualTo(2), "each motor keeps its own switch");
            Assert.That(stopInputs[1].HeldDrivers, Is.EqualTo(0b10), "only the motor that is down is held");
        });
    }

    [Test]
    public void AGantryWithBothSwitchesClosedHoldsTheWholeAxis()
    {
        // Nothing is left to move, so this is the ordinary already-closed case again
        (Move move, Sensors sensors) = Machine(yDrivers: 2, ySwitches: 2);
        sensors.Endstops[1]!.Triggered = true;
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [1], closed: axis => axis == 1 ? 0b11u : 0u);

        Assert.Multiple(() =>
        {
            Assert.That(armed.AxesToHold, Is.EqualTo(new[] { 1 }));
            Assert.That(armed.TriggeredAxes, Is.EqualTo(0b10u), "the axis is at its switches");
            Assert.That(stopInputs[1].HeldDrivers, Is.Zero, "the drive is held, so no motor needs holding");
        });
    }

    [Test]
    public void AClosedSwitchOnCoupledKinematicsHoldsEveryAxis()
    {
        // The one endstop stops every drive, so an endstop that is already closed has to hold every
        // drive too - including the axes this move never named
        (Move move, Sensors sensors) = Machine();
        sensors.Endstops[0]!.Triggered = true;
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [0], closed: axis => axis == 0 ? 0b1u : 0u,
                              kinematics: KinematicsName.CoreXY);

        Assert.That(armed.AxesToHold, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void AStallEndstopAsksForReducedAcceleration()
    {
        // The driver has to be turning slowly enough to tell a stall from normal load, which is what
        // M201.1 configures
        (Move move, Sensors sensors) = Machine();
        sensors.Endstops[0]!.Type = EndstopType.MotorStallAny;
        MoveStopInput[] stopInputs = NewStopInputs();

        ArmedMove armed = Arm((move, sensors), stopInputs, [0]);

        Assert.Multiple(() =>
        {
            Assert.That(armed.ReduceAcceleration, Is.True);
            Assert.That(stopInputs[0].NumSwitches, Is.Not.Zero, "and the move still watches the stall");
        });
    }

    [Test]
    public void ASwitchEndstopDoesNotAskForReducedAcceleration()
    {
        (Move move, Sensors sensors) = Machine();
        MoveStopInput[] stopInputs = NewStopInputs();

        Assert.That(Arm((move, sensors), stopInputs, [0]).ReduceAcceleration, Is.False);
    }
}
