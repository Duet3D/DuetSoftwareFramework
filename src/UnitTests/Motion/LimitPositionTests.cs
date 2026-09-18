using System;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Bringing a target position within what the machine can reach
/// </summary>
/// <remarks>
/// Ported from <c>Kinematics::LimitPosition</c> and its overrides. Nothing applied M208's limits to
/// any move before this, so a G1 could be commanded straight past the end of an axis. The part worth
/// testing beyond that is the shape of the reachable region: it is a box only on a Cartesian machine,
/// and the geometries whose region is not a box are the ones where clamping per axis gives an answer
/// the machine cannot reach
/// </remarks>
[TestFixture]
public class LimitPositionTests
{
    private static KinematicsEngine WithLimits(KinematicsEngine engine, float min, float max, int numAxes = 3)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            engine.AxisMinima[axis] = min;
            engine.AxisMaxima[axis] = max;
        }
        return engine;
    }

    private static LimitPositionResult Limit(KinematicsEngine engine, Span<float> coords,
                                             ReadOnlySpan<float> initial = default, uint axesToLimit = 0b111,
                                             bool isCoordinated = true, bool applyM208 = true)
        => engine.LimitPosition(coords, initial, 3, axesToLimit, isCoordinated, applyM208);

    [Test]
    public void ACartesianTargetIsClampedToTheM208Box()
    {
        KinematicsEngine engine = WithLimits(CoreKinematicsEngine.TryCreate("cartesian")!, 0.0f, 200.0f);

        float[] coords = [250.0f, -30.0f, 100.0f];
        Assert.That(Limit(engine, coords), Is.EqualTo(LimitPositionResult.Adjusted));
        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(200.0f), "past the maximum");
            Assert.That(coords[1], Is.EqualTo(0.0f), "past the minimum");
            Assert.That(coords[2], Is.EqualTo(100.0f), "in range, so untouched");
        });
    }

    [Test]
    public void AnAxisTheMoveDoesNotTouchIsNotLimited()
    {
        // Only the axes that are both homed and moving are limited. Limiting an axis the move does
        // not touch would turn a move in one axis into a move in two, and the planner would then have
        // to decide what to do about movement the user never asked for
        KinematicsEngine engine = WithLimits(CoreKinematicsEngine.TryCreate("cartesian")!, 0.0f, 200.0f);

        float[] coords = [250.0f, 250.0f, 100.0f];
        Assert.That(Limit(engine, coords, axesToLimit: 0b001), Is.EqualTo(LimitPositionResult.Adjusted));
        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(200.0f), "X was in the set");
            Assert.That(coords[1], Is.EqualTo(250.0f), "Y was not, so it is left out of range");
        });
    }

    [Test]
    public void AnAxisJustPastItsLimitIsNotCountedAsOutOfRange()
    {
        // Homing converts an axis limit to steps and back, and that does not land exactly on the limit
        // when the steps per mm is not a whole number. Without the tolerance an axis would be reported
        // out of range the moment it was homed to its own maximum
        KinematicsEngine engine = WithLimits(CoreKinematicsEngine.TryCreate("cartesian")!, 0.0f, 200.0f);

        float[] coords = [200.0f + (KinematicsEngine.AxisRoundingError / 2.0f), 100.0f, 100.0f];
        Assert.That(Limit(engine, coords), Is.EqualTo(LimitPositionResult.Ok));
    }

    [Test]
    public void M208LimitsCanBeTurnedOff()
    {
        // M564 S0 H0 is what lets a homing macro drive an axis to its switch, which is past the limit
        // by definition
        KinematicsEngine engine = WithLimits(CoreKinematicsEngine.TryCreate("cartesian")!, 0.0f, 200.0f);

        float[] coords = [-500.0f, 100.0f, 100.0f];
        Assert.That(Limit(engine, coords, applyM208: false), Is.EqualTo(LimitPositionResult.Ok));
        Assert.That(coords[0], Is.EqualTo(-500.0f));
    }

    [Test]
    public void APolarTargetIsPulledOntoTheNearerCircle()
    {
        // The reachable region is an annulus. Clamping X and Y separately would give a point in the
        // corner of a box, which the arm cannot reach at all
        PolarKinematicsEngine engine = new(minRadius: 20.0f, maxRadius: 100.0f, homedRadius: 20.0f,
                                           maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f);
        WithLimits(engine, -50.0f, 50.0f);

        float[] tooFar = [300.0f, 0.0f, 10.0f];
        Assert.That(Limit(engine, tooFar), Is.EqualTo(LimitPositionResult.Adjusted));
        Assert.That(tooFar[0], Is.EqualTo(100.0f).Within(1e-3f), "pulled in to the maximum radius");

        float[] tooClose = [5.0f, 0.0f, 10.0f];
        Assert.That(Limit(engine, tooClose), Is.EqualTo(LimitPositionResult.Adjusted));
        Assert.That(tooClose[0], Is.EqualTo(20.0f).Within(1e-3f), "pushed out to the minimum radius");
    }

    [Test]
    public void APolarTargetAtTheCentreIsPushedOutInAKnownDirection()
    {
        // The middle of the bed has no direction to push out in, so RepRapFirmware picks one rather
        // than dividing by zero
        PolarKinematicsEngine engine = new(minRadius: 20.0f, maxRadius: 100.0f, homedRadius: 20.0f,
                                           maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f);
        WithLimits(engine, -50.0f, 50.0f);

        float[] centre = [0.0f, 0.0f, 10.0f];
        Assert.That(Limit(engine, centre), Is.EqualTo(LimitPositionResult.Adjusted));
        Assert.Multiple(() =>
        {
            Assert.That(centre[0], Is.EqualTo(20.0f).Within(1e-3f));
            Assert.That(centre[1], Is.Zero);
        });
    }

    [Test]
    public void ADeltaTargetIsPulledInsideThePrintRadius()
    {
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        WithLimits(engine, 0.0f, 300.0f);

        float beyond = LinearDeltaKinematicsEngine.DefaultPrintRadius * 2.0f;
        float[] coords = [beyond, 0.0f, 10.0f];
        Assert.That(Limit(engine, coords), Is.EqualTo(LimitPositionResult.Adjusted));

        float radius = MathF.Sqrt((coords[0] * coords[0]) + (coords[1] * coords[1]));
        Assert.That(radius, Is.EqualTo(LinearDeltaKinematicsEngine.DefaultPrintRadius).Within(1e-2f));
    }

    [Test]
    public void ADeltaTargetIsLoweredToWhatTheTowersCanReach()
    {
        // A delta's ceiling is not flat: how high the effector can go depends on where it is, because
        // each carriage can only rise to its homed height. Asking for a height no tower can support
        // lowers the target rather than being refused
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        WithLimits(engine, 0.0f, 1000.0f);

        float[] coords = [0.0f, 0.0f, 900.0f];
        Assert.That(Limit(engine, coords), Is.EqualTo(LimitPositionResult.Adjusted));
        Assert.That(coords[2], Is.LessThan(900.0f), "brought down to a reachable height");

        // And what came back must itself be reachable, or the limit has not finished its job
        float[] again = [coords[0], coords[1], coords[2]];
        Assert.That(Limit(engine, again), Is.EqualTo(LimitPositionResult.Ok));
    }

    [Test]
    public void ADeltaChecksTheWholeOfAStraightMoveRatherThanItsEnds()
    {
        // A delta's ceiling is a surface, not a plane: how high the effector can go depends on where
        // it is, because each carriage can only rise to its homed height and the rods are a fixed
        // length. So the ceiling is lowest where the effector passes closest to a tower, and a move
        // whose closest approach falls in the middle can pass under a ceiling that is higher at both
        // of its ends. This move runs left to right in front of the Z tower, so it approaches it and
        // recedes again - and the highest carriage is halfway, not at either end
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        WithLimits(engine, 0.0f, 1000.0f);

        float[] left = [-60.0f, 40.0f, 1000.0f];
        Limit(engine, left);
        float[] right = [60.0f, 40.0f, 1000.0f];
        Limit(engine, right);
        float[] middle = [0.0f, 40.0f, 1000.0f];
        Limit(engine, middle);

        Assert.That(middle[2], Is.LessThan(left[2]),
                    "the middle of this move is the constrained part, which is what makes it a test");

        // Both ends are reachable at the height the ends allow, less a hair so that neither end is
        // itself sitting exactly on its own ceiling
        float height = left[2] - 1.0f;
        float[] endpointOnly = [60.0f, 40.0f, height];
        Assert.That(Limit(engine, endpointOnly), Is.EqualTo(LimitPositionResult.Ok));

        // ...but the straight line between them is not, and only looking at the path finds that
        float[] alongLine = [60.0f, 40.0f, height];
        float[] from = [-60.0f, 40.0f, height];
        LimitPositionResult withPath = Limit(engine, alongLine, initial: from);

        Assert.That(withPath, Is.EqualTo(LimitPositionResult.IntermediateUnreachable),
                    "level move, so there is no Z movement to absorb a lowering of the target");
    }

    [Test]
    public void ADeltaLowersATargetWhenThatIsEnoughToClearTheTowers()
    {
        // A descending move has Z movement to give up, so lowering the target lowers the whole path
        // with it and the move becomes possible. That is the difference between "adjusted" and
        // "intermediate unreachable" - the same obstruction, but one of them can be moved out of
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        WithLimits(engine, 0.0f, 1000.0f);

        float[] left = [-60.0f, 40.0f, 1000.0f];
        Limit(engine, left);

        // Start high at the roomy end and descend a long way at the constrained one
        float[] descending = [60.0f, 40.0f, 20.0f];
        float[] from = [-60.0f, 40.0f, left[2]];
        Assert.That(Limit(engine, descending, initial: from), Is.EqualTo(LimitPositionResult.Ok),
                    "the move dips below the obstruction on its own");
    }

    [Test]
    public void CoupledGeometriesRequireEveryAxisHomedWhateverM564Says()
    {
        // On a delta the head's position is a function of all three towers, so a coordinate in one of
        // them means nothing until every one is homed. M564 S0 does not make that safe, which is why
        // the geometry widens the set rather than the setting deciding on its own
        LinearDeltaKinematicsEngine delta = LinearDeltaKinematicsEngine.CreateDefault();
        KinematicsEngine cartesian = CoreKinematicsEngine.TryCreate("cartesian")!;

        Assert.Multiple(() =>
        {
            Assert.That(delta.MustBeHomedAxes(0b001, disallowMovesBeforeHoming: false), Is.EqualTo(0b111u),
                        "moving X on a delta needs Y and Z homed too");
            Assert.That(cartesian.MustBeHomedAxes(0b001, disallowMovesBeforeHoming: false), Is.Zero,
                        "an independently driven axis is M564's business alone");
            Assert.That(cartesian.MustBeHomedAxes(0b001, disallowMovesBeforeHoming: true), Is.EqualTo(0b001u));

            // An extruder-only move names no axis, so there is nothing to be unsure about
            Assert.That(delta.MustBeHomedAxes(0, disallowMovesBeforeHoming: true), Is.Zero);
        });
    }
}
