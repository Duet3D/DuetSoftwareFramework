using System.Collections.Generic;
using System.Linq;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using OmDriverId = DuetAPI.Utility.DriverId;

namespace UnitTests.Motion;

/// <summary>
/// What a move decides it watches, before either half of arming acts on it
/// </summary>
/// <remarks>
/// Arming an endstop takes two phases and cannot take one: telling a driver what speed to expect is a
/// CAN round trip, and writing the stop input into the move happens inside a lock that may not await.
/// The phases agree because this is worked out once and handed to both. These are the tests of that
/// one answer - the drivers a board is armed for and the drivers the move tells the controller to
/// watch are the same list, and there is no second derivation left to drift
/// </remarks>
[TestFixture]
public class EndstopPlannerTests
{
    private const int NumAxes = 3;
    private const float FeedRateMmPerSec = 30.0f;

    /// <summary>Steps per mm by drive, distinct per axis so that a wrong drive shows up as a wrong speed</summary>
    private static readonly float[] StepsPerMm = BuildStepsPerMm();

    private static float[] BuildStepsPerMm()
    {
        float[] stepsPerMm = new float[MotionLimits.MaxAxesPlusExtruders];
        stepsPerMm[0] = 80.0f;
        stepsPerMm[1] = 160.0f;
        stepsPerMm[2] = 400.0f;
        return stepsPerMm;
    }

    /// <summary>
    /// Three axes, one driver each unless asked for more, all on expansion boards
    /// </summary>
    private static (Move Move, Sensors Sensors) Machine(EndstopType type = EndstopType.InputPin,
                                                        int xDrivers = 1)
    {
        Move move = new();
        Sensors sensors = new();

        char[] letters = ['X', 'Y', 'Z'];
        for (int axis = 0; axis < NumAxes; axis++)
        {
            Axis a = new() { Letter = letters[axis] };
            for (int i = 0; i < (axis == 0 ? xDrivers : 1); i++)
            {
                // A board per driver, so that which board a driver is on is visible in the plan
                a.Drivers.Add(new OmDriverId(axis + 1, i));
            }
            move.Axes.Add(a);
            sensors.Endstops.Add(new Endstop { Type = type, Port = $"1.io{axis}.in" });
        }
        return (move, sensors);
    }

    private static Code Homing(params char[] axes)
    {
        Code code = new() { Type = CodeType.GCode, MajorNumber = 1 };
        code.Parameters.Add(new CodeParameter('H', 1));
        foreach (char axis in axes)
        {
            code.Parameters.Add(new CodeParameter(axis, -300.0f));
        }
        return code;
    }

    private static List<EndstopPlan> Plan((Move Move, Sensors Sensors) machine, Code code,
                                          KinematicsName kinematics = KinematicsName.Cartesian)
        => EndstopPlanner.Plan(code, machine.Move, machine.Sensors, KinematicsFactory.Create(kinematics),
                               NumAxes, StepsPerMm, FeedRateMmPerSec);

    [Test]
    public void OnlyTheAxesTheCodeNamesArePlannedFor()
    {
        // A homing move naming X must not be stopped by Z's switch happening to be closed
        List<EndstopPlan> plans = Plan(Machine(), Homing('X', 'Z'));

        Assert.That(plans.Select(plan => plan.Axis), Is.EqualTo(new[] { 0, 2 }), "in the order it names them");
    }

    [Test]
    public void AnAxisWithNoEndstopIsRefusedBeforeAnythingIsSent()
    {
        // The refusal has to come before the boards are armed, or a move that cannot run has already
        // told a driver to watch for a stall and left it watching
        (Move move, Sensors sensors) = Machine();
        sensors.Endstops[0] = null;

        Assert.Throws<GCodeException>(() => Plan((move, sensors), Homing('X')));
    }

    [Test]
    public void ASwitchHomedAxisWatchesNoDrivers()
    {
        // Nothing is armed over the bus for a switch: M574 asked the board to watch the pin and it
        // has reported every change since
        List<EndstopPlan> plans = Plan(Machine(), Homing('X'));

        Assert.Multiple(() =>
        {
            Assert.That(plans[0].Kind, Is.EqualTo(EndstopType.InputPin));
            Assert.That(plans[0].DriversWatched, Is.Empty);
            Assert.That(plans[0].NumAxisDrivers, Is.EqualTo(1), "but the switch-per-driver rule still needs the count");
        });
    }

    [Test]
    public void AStallHomedAxisWatchesEveryDriverOfItsOwnDrive()
    {
        List<EndstopPlan> plans = Plan(Machine(EndstopType.MotorStallAny, xDrivers: 2), Homing('X'));

        Assert.Multiple(() =>
        {
            Assert.That(plans[0].DriversWatched.Select(watched => watched.Driver.Port), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(plans[0].NumAxisDrivers, Is.EqualTo(2));
        });
    }

    [Test]
    public void AStallHomedCoupledAxisWatchesTheDriversOfEveryControllingDrive()
    {
        // Moving X on a CoreXY turns both motors, so a stall on either of them is X stalling. This is
        // the list that used to be worked out once for the boards and again for the move
        List<EndstopPlan> plans = Plan(Machine(EndstopType.MotorStallAny), Homing('X'), KinematicsName.CoreXY);

        Assert.That(plans[0].DriversWatched.Select(watched => watched.Driver.Board),
                    Is.EqualTo(new[] { 1, 2 }), "X's motor and Y's, each on its own board");
    }

    [Test]
    public void EachWatchedDriverIsToldTheSpeedOfItsOwnDrive()
    {
        // A driver decides it has stalled by comparing the back-EMF against what the commanded speed
        // implies, so it is told steps per second - and a coupled machine need not have the same
        // steps per mm on each of its drives
        List<EndstopPlan> plans = Plan(Machine(EndstopType.MotorStallAny), Homing('X'), KinematicsName.CoreXY);

        Assert.That(plans[0].DriversWatched.Select(watched => watched.StepsPerSecond),
                    Is.EqualTo(new[] { FeedRateMmPerSec * 80.0f, FeedRateMmPerSec * 160.0f }));
    }

    [Test]
    public void MotorStallIndividualIsPlannedTheSameWayAsMotorStallAny()
    {
        // They are not told apart yet, which is the defect §4.3 of the plan describes. Recorded so
        // that the phase which does tell them apart changes a test rather than adding one
        List<EndstopPlan> any = Plan(Machine(EndstopType.MotorStallAny, xDrivers: 2), Homing('X'));
        List<EndstopPlan> individual = Plan(Machine(EndstopType.MotorStallIndividual, xDrivers: 2), Homing('X'));

        Assert.That(individual[0].DriversWatched, Is.EqualTo(any[0].DriversWatched));
    }

    /// <summary>
    /// A motor-stall Z probe watches the drivers that move Z, which is the same question a
    /// stall-homed axis asks
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's motor stall probe reads the stalled-driver bitmap of its *local* drivers, so
    /// on this architecture - where every driver is on a CAN board - it can never trigger. The
    /// probing path arms the drivers instead, through the list this returns
    /// </remarks>
    [Test]
    public void AStallProbeWatchesEveryDriverThatMovesZ()
    {
        // On a CoreXZ, Z only comes down because both motors turn, so a stall probe has to watch both
        (Move move, Sensors sensors) = Machine();
        _ = sensors;

        IReadOnlyList<WatchedDriver> drivers =
            EndstopPlanner.DriversMoving(move, KinematicsFactory.Create(KinematicsName.CoreXZ), NumAxes, 2,
                                         StepsPerMm, FeedRateMmPerSec);

        Assert.Multiple(() =>
        {
            Assert.That(drivers.Select(d => d.Driver.Board), Is.EqualTo(new[] { 1, 3 }),
                        "X's motor and Z's, each on its own board");
            Assert.That(drivers.Select(d => d.StepsPerSecond),
                        Is.EqualTo(new[] { FeedRateMmPerSec * 80.0f, FeedRateMmPerSec * 400.0f }),
                        "each told the speed its own drive will turn at");
        });
    }

    [Test]
    public void AnIndependentAxisWatchesOnlyItsOwnDriversWhenProbing()
    {
        (Move move, Sensors sensors) = Machine();
        _ = sensors;

        IReadOnlyList<WatchedDriver> drivers =
            EndstopPlanner.DriversMoving(move, KinematicsFactory.Create(KinematicsName.Cartesian), NumAxes, 2,
                                         StepsPerMm, FeedRateMmPerSec);

        Assert.That(drivers.Select(d => d.Driver.Board), Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public void EveryKindAMoveCanBeStoppedByHasAKind()
    {
        // A type with no kind is a move that refuses to arm. The dispatch is what makes adding a kind
        // one edit instead of two, so a type falling through it has to fail here rather than on a machine
        Assert.Multiple(() =>
        {
            Assert.That(EndstopKinds.For(EndstopType.InputPin), Is.Not.Null);
            Assert.That(EndstopKinds.For(EndstopType.ZProbeAsEndstop), Is.Not.Null);
            Assert.That(EndstopKinds.For(EndstopType.MotorStallAny), Is.Not.Null);
            Assert.That(EndstopKinds.For(EndstopType.MotorStallIndividual), Is.Not.Null);
            Assert.That(EndstopKinds.For(EndstopType.Unknown), Is.Null, "and a type a move cannot watch has none");
        });
    }

    [Test]
    public void OnlyAStallEndstopAsksForReducedAcceleration()
    {
        // The driver has to be turning slowly enough to tell a stall from normal load, which is what
        // M201.1 configures. RepRapFirmware's Endstop::ShouldReduceAcceleration
        Assert.Multiple(() =>
        {
            Assert.That(EndstopKinds.For(EndstopType.MotorStallAny)!.ReducesAcceleration, Is.True);
            Assert.That(EndstopKinds.For(EndstopType.InputPin)!.ReducesAcceleration, Is.False);
            Assert.That(EndstopKinds.For(EndstopType.ZProbeAsEndstop)!.ReducesAcceleration, Is.False);
        });
    }

    [Test]
    public void EachKindIsReleasedOnceHoweverManyAxesUseIt()
    {
        // One message disables every stall endstop on a board, so two stall-homed axes must not
        // release twice
        List<EndstopPlan> plans = Plan(Machine(EndstopType.MotorStallAny), Homing('X', 'Y'));

        Assert.That(EndstopKinds.Used(plans).Count(), Is.EqualTo(1));
    }

    /// <summary>
    /// The machine, with Z homed by the given probe rather than by a switch
    /// </summary>
    private static (Move Move, Sensors Sensors) ProbeHomedZ(Probe probe, int probeNumber = 0)
    {
        (Move move, Sensors sensors) = Machine();
        while (sensors.Probes.Count <= probeNumber)
        {
            sensors.Probes.Add(null);
        }
        sensors.Probes[probeNumber] = probe;
        sensors.Endstops[2] = new Endstop { Type = EndstopType.ZProbeAsEndstop, Probe = probeNumber };
        return (move, sensors);
    }

    [Test]
    public void AProbeHomedAxisCarriesWhatItsBoardHasToBeTold()
    {
        // A probe reports at the idle interval until something asks it not to, so a homing move that
        // did not arm it would be stopped up to that interval late. What to send is read from the
        // probe here, under the model lock, because the phase that sends it runs outside one
        List<EndstopPlan> plans = Plan(ProbeHomedZ(new Probe
        {
            Type = ProbeType.Analog,
            Port = "2.io4.in",
            Threshold = 123
        }, probeNumber: 1), Homing('Z'));

        Assert.That(plans[0].ProbeMonitor, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(plans[0].ProbeMonitor!.Value.Board, Is.EqualTo(2));
            Assert.That(plans[0].ProbeMonitor!.Value.ProbeNumber, Is.EqualTo(1),
                        "the endstop's probe, not probe 0");
            Assert.That(plans[0].ProbeMonitor!.Value.Threshold, Is.EqualTo(123u));
        });
    }

    [Test]
    public void AProbeHomedAxisWithNothingToTellCarriesNoMonitor()
    {
        // A stall probe is detected by the driver and has no handle to change. Arming it would send
        // to a handle M558 never created
        List<EndstopPlan> stall = Plan(ProbeHomedZ(new Probe { Type = ProbeType.ZMotorStall }), Homing('Z'));
        List<EndstopPlan> unported = Plan(ProbeHomedZ(new Probe { Type = ProbeType.Digital }), Homing('Z'));

        Assert.Multiple(() =>
        {
            Assert.That(stall[0].ProbeMonitor, Is.Null, "a stall probe is not an input");
            Assert.That(unported[0].ProbeMonitor, Is.Null, "nor is a probe whose port has not been given yet");
        });
    }

    [Test]
    public void AnAxisHomedOnASwitchCarriesNoProbeMonitor()
    {
        // Only the Z probe kind sends anything per move. A switch was armed by M574 and has reported
        // every change since
        Assert.That(Plan(Machine(), Homing('X'))[0].ProbeMonitor, Is.Null);
    }
}
