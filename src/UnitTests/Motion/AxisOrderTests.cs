using System;
using System.Collections.Generic;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

using Code = DuetAPI.Commands.Code;

namespace UnitTests.Motion;

/// <summary>
/// X, Y and Z found by letter rather than by position
/// </summary>
/// <remarks>
/// <para>
/// DSF has no default axes, so config.g can create them in any order and need not create all three.
/// Every geometry and transform that needs X, Y or Z has to find it by letter, and a machine whose
/// axes are in another order has to behave exactly as the same machine with them in the usual order.
/// </para>
/// <para>
/// The tests here run the same calculation on the canonical order and on a permutation, giving each
/// letter its own steps per mm so that an axis read from the wrong place shows, and compare the two
/// letter by letter
/// </para>
/// </remarks>
[TestFixture]
public class AxisOrderTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>The usual order</summary>
    private const string Canonical = "XYZUV";

    /// <summary>The same axes, created in another order: U first, X fourth and Z last</summary>
    private const string Permuted = "UYVXZ";

    /// <summary>Steps per mm per letter, all different so an axis read from the wrong place shows</summary>
    private static readonly Dictionary<char, float> StepsByLetter = new()
    {
        ['X'] = 80.0f, ['Y'] = 90.0f, ['Z'] = 400.0f, ['U'] = 50.0f, ['V'] = 60.0f
    };

    /// <summary>A value per letter laid out in the given axis order</summary>
    private static float[] Lay(string letters, Func<char, float> valueOf)
    {
        float[] values = new float[NumDrives];
        for (int axis = 0; axis < letters.Length; axis++)
        {
            values[axis] = valueOf(letters[axis]);
        }
        return values;
    }

    /// <summary>A bitmap over one axis order expressed over another</summary>
    private static uint Translate(uint bitmap, string from, string to)
    {
        uint result = 0;
        for (int axis = 0; axis < from.Length; axis++)
        {
            if ((bitmap & (1u << axis)) != 0)
            {
                result |= 1u << to.IndexOf(from[axis]);
            }
        }
        return result;
    }

    /// <summary>
    /// A geometry with its defaults for the machine's axes, but a SCARA with crosstalk onto Z so that
    /// which axis it takes to be Z shows
    /// </summary>
    /// <param name="geometry">Which geometry</param>
    /// <param name="letters">The machine's axes, in axis order</param>
    private static KinematicsEngine Create(KinematicsName geometry, string letters) => geometry == KinematicsName.Scara
        ? new ScaraKinematicsEngine(crosstalk: [0.1f, 0.05f, -0.02f]).WithAxes(letters)
        : KinematicsFactory.Create(geometry, KinematicsAxes.From(letters));

    /// <summary>One row of a core geometry's inverse matrix, as the object model reports it</summary>
    /// <param name="engine">The geometry</param>
    /// <param name="axis">The axis whose row it is</param>
    /// <returns>How far each motor turns per mm of that axis</returns>
    private static float[] InverseRow(KinematicsEngine engine, int axis)
        => KinematicsAxes.Reported<CoreKinematics>(engine).InverseMatrix[axis];

    /// <summary>One of each geometry, with a point it can reach given per letter</summary>
    private static IEnumerable<TestCaseData> Geometries()
    {
        yield return new TestCaseData(KinematicsName.LinearDelta, new[] { 10.0f, -20.0f, 50.0f });
        yield return new TestCaseData(KinematicsName.RotaryDelta, new[] { 20.0f, 0.0f, 110.0f });
        yield return new TestCaseData(KinematicsName.Scara, new[] { 100.0f, 50.0f, 5.0f });
        yield return new TestCaseData(KinematicsName.FiveBarScara, new[] { 20.0f, 140.0f, 3.0f });
        yield return new TestCaseData(KinematicsName.Polar, new[] { 60.0f, 80.0f, -2.5f });
        yield return new TestCaseData(KinematicsName.Hangprinter, new[] { 100.0f, 0.0f, 200.0f });
        yield return new TestCaseData(KinematicsName.CoreXY, new[] { 10.0f, 20.0f, 5.0f });
        yield return new TestCaseData(KinematicsName.CoreXYU, new[] { 10.0f, 20.0f, 5.0f });
        yield return new TestCaseData(KinematicsName.MarkForged, new[] { 10.0f, 20.0f, 5.0f });
    }

    /// <summary>The point a test case gives, plus U and V, by letter</summary>
    private static Func<char, float> Point(float[] xyz)
        => letter => letter switch { 'X' => xyz[0], 'Y' => xyz[1], 'Z' => xyz[2], 'U' => 7.0f, _ => -3.0f };

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesGiveTheSameMotorPositions(KinematicsName geometry, float[] xyz)
    {
        KinematicsEngine canonical = Create(geometry, Canonical);
        KinematicsEngine permuted = Create(geometry, Permuted);
        Func<char, float> point = Point(xyz);

        int[] canonicalMotors = new int[NumDrives], permutedMotors = new int[NumDrives];
        NativeMovementError canonicalError = canonical.CartesianToMotorSteps(
            Lay(Canonical, point), Lay(Canonical, l => StepsByLetter[l]), 5, 5, canonicalMotors, isCoordinated: false);
        NativeMovementError permutedError = permuted.CartesianToMotorSteps(
            Lay(Permuted, point), Lay(Permuted, l => StepsByLetter[l]), 5, 5, permutedMotors, isCoordinated: false);

        Assert.Multiple(() =>
        {
            Assert.That(canonicalError, Is.EqualTo(NativeMovementError.Ok));
            Assert.That(permutedError, Is.EqualTo(NativeMovementError.Ok));
            foreach (char letter in Canonical)
            {
                Assert.That(permutedMotors[Permuted.IndexOf(letter)], Is.EqualTo(canonicalMotors[Canonical.IndexOf(letter)]),
                            $"the motor of {letter}");
            }
        });
    }

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesRoundTripToTheSamePosition(KinematicsName geometry, float[] xyz)
    {
        KinematicsEngine permuted = Create(geometry, Permuted);
        Func<char, float> point = Point(xyz);
        float[] stepsPerMm = Lay(Permuted, l => StepsByLetter[l]);

        int[] motors = new int[NumDrives];
        Assert.That(permuted.CartesianToMotorSteps(Lay(Permuted, point), stepsPerMm, 5, 5, motors),
                    Is.EqualTo(NativeMovementError.Ok));

        float[] roundTripped = new float[NumDrives];
        permuted.MotorStepsToCartesian(motors, stepsPerMm, 5, 5, roundTripped);

        // The Hangprinter's solver is iterative and stops when its step is small rather than exact. Its
        // drives are all lines, so it has no other axes to convert, as in RepRapFirmware
        bool hangprinter = permuted is HangprinterKinematicsEngine;
        float tolerance = hangprinter ? 0.5f : 0.05f;
        Assert.Multiple(() =>
        {
            foreach (char letter in hangprinter ? "XYZ" : Canonical)
            {
                Assert.That(roundTripped[Permuted.IndexOf(letter)], Is.EqualTo(point(letter)).Within(tolerance), letter.ToString());
            }
        });
    }

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesCoupleTheSameMotors(KinematicsName geometry, float[] xyz)
    {
        _ = xyz;
        KinematicsEngine canonical = Create(geometry, Canonical);
        KinematicsEngine permuted = Create(geometry, Permuted);

        Assert.Multiple(() =>
        {
            foreach (char letter in Canonical)
            {
                uint expected = Translate(canonical.GetControllingDrives(Canonical.IndexOf(letter)), Canonical, Permuted);
                Assert.That(permuted.GetControllingDrives(Permuted.IndexOf(letter)), Is.EqualTo(expected),
                            $"the drives holding {letter}");

                uint moving = 1u << Canonical.IndexOf(letter);
                Assert.That(permuted.MustBeHomedAxes(Translate(moving, Canonical, Permuted), false),
                            Is.EqualTo(Translate(canonical.MustBeHomedAxes(moving, false), Canonical, Permuted)),
                            $"what has to be homed to move {letter}");
            }

            Assert.That(permuted.AxesAssumedHomed(Translate(0b111, Canonical, Permuted)),
                        Is.EqualTo(Translate(canonical.AxesAssumedHomed(0b111), Canonical, Permuted)), "G92 X Y Z");
            Assert.That(permuted.AxesAssumedHomed(Translate(0b001, Canonical, Permuted)),
                        Is.EqualTo(Translate(canonical.AxesAssumedHomed(0b001), Canonical, Permuted)), "G92 X");
            Assert.That(permuted.ContinuousRotationAxes,
                        Is.EqualTo(Translate(canonical.ContinuousRotationAxes, Canonical, Permuted)), "continuous rotation");
            Assert.That(permuted.AxesToHomeBeforeProbing,
                        Is.EqualTo(Translate(canonical.AxesToHomeBeforeProbing, Canonical, Permuted)), "homed before probing");
        });
    }

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesHomeTheirEndstopsToTheSamePlace(KinematicsName geometry, float[] xyz)
    {
        KinematicsEngine canonical = Create(geometry, Canonical);
        KinematicsEngine permuted = Create(geometry, Permuted);
        Func<char, float> point = Point(xyz);

        // Somewhere other than the origin, so that a SCARA joint's crosstalk has something to subtract
        int[] canonicalMotors = new int[NumDrives], permutedMotors = new int[NumDrives];
        float[] canonicalSteps = Lay(Canonical, l => StepsByLetter[l]), permutedSteps = Lay(Permuted, l => StepsByLetter[l]);
        canonical.CartesianToMotorSteps(Lay(Canonical, point), canonicalSteps, 5, 5, canonicalMotors);
        permuted.CartesianToMotorSteps(Lay(Permuted, point), permutedSteps, 5, 5, permutedMotors);

        Assert.Multiple(() =>
        {
            foreach (char letter in Canonical)
            {
                foreach (bool highEnd in new[] { false, true })
                {
                    Assert.That(permuted.GetEndstopPosition(Permuted.IndexOf(letter), highEnd, -10.0f, 200.0f, permutedMotors, permutedSteps),
                                Is.EqualTo(canonical.GetEndstopPosition(Canonical.IndexOf(letter), highEnd, -10.0f, 200.0f, canonicalMotors, canonicalSteps))
                                  .Within(1e-4f),
                                $"{letter} {(highEnd ? "high" : "low")} end");
                }
            }
        });
    }

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesAreLimitedTheSameWay(KinematicsName geometry, float[] xyz)
    {
        _ = xyz;
        KinematicsEngine canonical = WithBox(Create(geometry, Canonical), Canonical);
        KinematicsEngine permuted = WithBox(Create(geometry, Permuted), Permuted);

        // Far outside everything, so that every geometry has something to bring in
        Func<char, float> outside = letter => letter switch { 'X' => 400.0f, 'Y' => 300.0f, 'Z' => 900.0f, 'U' => 700.0f, _ => -500.0f };
        float[] canonicalCoords = Lay(Canonical, outside), permutedCoords = Lay(Permuted, outside);

        LimitPositionResult canonicalResult = canonical.LimitPosition(canonicalCoords, [], 5, 0b11111, true, true);
        LimitPositionResult permutedResult = permuted.LimitPosition(permutedCoords, [], 5, 0b11111, true, true);

        Assert.Multiple(() =>
        {
            Assert.That(permutedResult, Is.EqualTo(canonicalResult));
            foreach (char letter in Canonical)
            {
                Assert.That(permutedCoords[Permuted.IndexOf(letter)], Is.EqualTo(canonicalCoords[Canonical.IndexOf(letter)]).Within(1e-3f),
                            letter.ToString());
            }
        });

        static KinematicsEngine WithBox(KinematicsEngine engine, string letters)
        {
            for (int axis = 0; axis < letters.Length; axis++)
            {
                engine.SetAxisLimits(axis, letters[axis] == 'Z' ? 0.0f : -100.0f, letters[axis] == 'Z' ? 250.0f : 100.0f);
            }
            return engine;
        }
    }

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesLimitTheSameSpeed(KinematicsName geometry, float[] xyz)
    {
        _ = xyz;
        KinematicsEngine canonical = Create(geometry, Canonical);
        KinematicsEngine permuted = Create(geometry, Permuted);

        // A diagonal in X and Y, with X and Y given different limits so that which is which shows
        Func<char, float> direction = letter => letter switch { 'X' => 0.6f, 'Y' => 0.8f, _ => 0.0f };
        Func<char, float> speeds = letter => letter switch { 'X' => 2.0f, 'Y' => 1.0f, 'Z' => 0.1f, _ => 3.0f };
        Func<char, float> accelerations = letter => letter switch { 'X' => 20.0f, 'Y' => 10.0f, 'Z' => 1.0f, _ => 30.0f };

        MoveLimits canonicalLimits = Limit(canonical, Canonical);
        MoveLimits permutedLimits = Limit(permuted, Permuted);

        Assert.Multiple(() =>
        {
            Assert.That(permutedLimits.RequestedSpeed, Is.EqualTo(canonicalLimits.RequestedSpeed).Within(1e-5f));
            Assert.That(permutedLimits.MaxAcceleration, Is.EqualTo(canonicalLimits.MaxAcceleration).Within(1e-5f));
        });

        MoveLimits Limit(KinematicsEngine engine, string letters)
        {
            MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
            int[] motors = new int[NumDrives];
            PlannedMove move = new()
            {
                NormalisedDirectionVector = Lay(letters, direction),
                StartMotorPos = motors,
                EndMotorPos = motors,
                StepsPerMm = Lay(letters, l => StepsByLetter[l]),
                NumVisibleAxes = 5,
                TotalDistance = 10.0f
            };
            engine.LimitSpeedAndAcceleration(ref limits, move, Lay(letters, speeds), Lay(letters, accelerations));
            return limits;
        }
    }

    // ---- Missing axes -----------------------------------------------------------------------------

    private static IEnumerable<TestCaseData> GeometriesMissingAnAxis()
    {
        yield return new TestCaseData(KinematicsName.LinearDelta, "XYU", "The machine has no Z axis, which delta kinematics needs");
        yield return new TestCaseData(KinematicsName.RotaryDelta, "ZU", "The machine has no X or Y axis, which Rotary delta kinematics needs");
        yield return new TestCaseData(KinematicsName.Scara, "ZXU", "The machine has no Y axis, which Scara kinematics needs");
        yield return new TestCaseData(KinematicsName.FiveBarScara, "XZ", "The machine has no Y axis, which FiveBarScara kinematics needs");
        yield return new TestCaseData(KinematicsName.Polar, "YZ", "The machine has no X axis, which Polar kinematics needs");
        yield return new TestCaseData(KinematicsName.Hangprinter, "", "The machine has no X, Y, Z or U axis, which Hangprinter kinematics needs");
        yield return new TestCaseData(KinematicsName.Hangprinter, "ZYXV", "The machine has no U axis, which Hangprinter kinematics needs");
    }

    [TestCaseSource(nameof(GeometriesMissingAnAxis))]
    public void AGeometryMissingAnAxisRefusesToTransform(KinematicsName geometry, string letters, string error)
    {
        KinematicsEngine engine = Create(geometry, letters);

        float[] machinePos = Lay(letters, _ => 10.0f);
        int[] motorPos = new int[NumDrives];
        Array.Fill(motorPos, 1234);
        float[] stepsPerMm = Lay("XYZUV", _ => 80.0f);

        Assert.Multiple(() =>
        {
            Assert.That(engine.MissingAxesError, Is.EqualTo(error));
            Assert.That(engine.CartesianToMotorSteps(machinePos, stepsPerMm, letters.Length, letters.Length, motorPos),
                        Is.EqualTo(NativeMovementError.UnreachablePosition));

            // Nothing that cannot be computed is made up: the axes the geometry transforms keep
            // whatever the caller held, and the rest still convert as ordinary axes
            float[] position = new float[NumDrives];
            Array.Fill(position, -1.0f);
            Assert.DoesNotThrow(() => engine.MotorStepsToCartesian(motorPos, stepsPerMm, letters.Length, letters.Length, position));
            Assert.That(engine.AxesAssumedHomed((1u << letters.Length) - 1) & KinematicsAxes.From(letters).XyBitmap, Is.Zero,
                        "naming every axis there is still does not pin the geometry down");
        });
    }

    [Test]
    public void AScaraWithoutZMovesItsArmsAndHasNoZToDrag()
    {
        // A pen plotter: two joints and no lift. The arms come out where they would on the same SCARA
        // with Z, and the crosstalk onto Z has nothing to act on
        const string noZ = "YXU";
        KinematicsEngine withZ = Create(KinematicsName.Scara, Canonical);
        KinematicsEngine withoutZ = Create(KinematicsName.Scara, noZ);
        Func<char, float> point = Point([100.0f, 50.0f, 5.0f]);
        float[] stepsPerMm = Lay(noZ, l => StepsByLetter[l]);

        int[] canonicalMotors = new int[NumDrives], motors = new int[NumDrives];
        Assert.That(withZ.CartesianToMotorSteps(Lay(Canonical, point), Lay(Canonical, l => StepsByLetter[l]), 5, 5, canonicalMotors),
                    Is.EqualTo(NativeMovementError.Ok));
        NativeMovementError error = withoutZ.CartesianToMotorSteps(Lay(noZ, point), stepsPerMm, 3, 3, motors);

        float[] roundTripped = new float[NumDrives];
        withoutZ.MotorStepsToCartesian(motors, stepsPerMm, 3, 3, roundTripped);

        AxisIndices axes = KinematicsAxes.From(noZ);
        Assert.Multiple(() =>
        {
            Assert.That(withoutZ.MissingAxesError, Is.Null);
            Assert.That(error, Is.EqualTo(NativeMovementError.Ok));
            foreach (char letter in "XYU")
            {
                Assert.That(motors[noZ.IndexOf(letter)], Is.EqualTo(canonicalMotors[Canonical.IndexOf(letter)]), $"the motor of {letter}");
                Assert.That(roundTripped[noZ.IndexOf(letter)], Is.EqualTo(point(letter)).Within(0.05f), letter.ToString());
            }
            Assert.That(withoutZ.GetControllingDrives(axes.X), Is.EqualTo(axes.XyBitmap), "the two arms, with no Z to drag");
            Assert.That(withoutZ.GetControllingDrives(noZ.IndexOf('U')), Is.EqualTo(AxisIndices.Bit(noZ.IndexOf('U'))));
            Assert.That(withoutZ.AxesAssumedHomed(0b111), Is.EqualTo(0b111u), "G92 X Y U pins both arms down");
            Assert.That(withoutZ.GetEndstopPosition(noZ.IndexOf('U'), true, 0.0f, 200.0f, motors, stepsPerMm), Is.EqualTo(200.0f),
                        "U is an ordinary axis");
        });
    }

    private static IEnumerable<TestCaseData> CoreGeometriesMissingAnAxis()
    {
        yield return new TestCaseData(KinematicsName.CoreXY, "XZU", "The machine has no Y axis, which coreXY kinematics needs");
        yield return new TestCaseData(KinematicsName.CoreXZ, "ZYU", "The machine has no X axis, which coreXZ kinematics needs");
        yield return new TestCaseData(KinematicsName.CoreXYU, "XYZU", "The machine has no V axis, which coreXYU kinematics needs");
        yield return new TestCaseData(KinematicsName.CoreXYUV, "YXZ", "The machine has no U or V axis, which coreXYUV kinematics needs");
        yield return new TestCaseData(KinematicsName.MarkForged, "UY", "The machine has no X axis, which markForged kinematics needs");
    }

    [TestCaseSource(nameof(CoreGeometriesMissingAnAxis))]
    public void ACoreGeometryMissingAnAxisItsMatrixNamesRefusesToTransform(KinematicsName geometry, string letters, string error)
    {
        // Only a geometry adopted from the object model can get here: M669 refuses to select one
        KinematicsEngine engine = Create(geometry, letters);
        int[] motorPos = new int[NumDrives];

        Assert.Multiple(() =>
        {
            Assert.That(engine.MissingAxesError, Is.EqualTo(error));
            Assert.That(engine.CannotPlanReason, Is.EqualTo(error));
            Assert.That(engine.CartesianToMotorSteps(Lay(letters, _ => 10.0f), Lay(letters, _ => 80.0f), letters.Length, letters.Length, motorPos),
                        Is.EqualTo(NativeMovementError.UnreachablePosition));
        });
    }

    [Test]
    public void ADeltasFurtherTowersAreTheAxesLetteredUVAndW()
    {
        // Five towers: A, B and C on X, Y and Z, the fourth on U and the fifth on V, wherever they are
        static LinearDeltaKinematicsEngine FiveTowers(string letters) => new LinearDeltaKinematicsEngine(
            numTowers: 5, radius: 105.6f,
            diagonals: [215.0f, 215.0f, 215.0f, 150.0f, 160.0f],
            angleCorrections: [0.0f, 0.0f, 0.0f],
            endstopAdjustments: [0.0f, 0.0f, 0.0f, 0.0f, 0.0f],
            homedHeight: 240.0f, printRadius: 80.0f).WithAxes(letters);

        LinearDeltaKinematicsEngine canonical = FiveTowers(Canonical);
        LinearDeltaKinematicsEngine permuted = FiveTowers(Permuted);
        Func<char, float> point = Point([10.0f, -20.0f, 50.0f]);
        int[] canonicalMotors = new int[NumDrives], permutedMotors = new int[NumDrives];

        Assert.Multiple(() =>
        {
            Assert.That(FiveTowers("XYZU").MissingAxesError, Is.EqualTo("The machine has no V axis, which delta kinematics needs"));
            Assert.That(FiveTowers("XYZW").MissingAxesError, Is.EqualTo("The machine has no U or V axis, which delta kinematics needs"),
                        "W is the sixth tower's, not the fourth's");
            Assert.That(permuted.MissingAxesError, Is.Null);

            Assert.That(canonical.CartesianToMotorSteps(Lay(Canonical, point), Lay(Canonical, l => StepsByLetter[l]), 5, 5, canonicalMotors),
                        Is.EqualTo(NativeMovementError.Ok));
            Assert.That(permuted.CartesianToMotorSteps(Lay(Permuted, point), Lay(Permuted, l => StepsByLetter[l]), 5, 5, permutedMotors),
                        Is.EqualTo(NativeMovementError.Ok));
            foreach (char letter in Canonical)
            {
                Assert.That(permutedMotors[Permuted.IndexOf(letter)], Is.EqualTo(canonicalMotors[Canonical.IndexOf(letter)]),
                            $"the motor of {letter}");
            }
            Assert.That(permuted.GetControllingDrives(Permuted.IndexOf('X')), Is.EqualTo(0b11111u), "every tower holds the effector");
        });
    }

    [Test]
    public void OnlyTheLettersAGeometryNeedsHaveToExist()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("Y")).MissingAxesError, Is.Null);
            Assert.That(KinematicsFactory.Create(KinematicsName.CoreXY, KinematicsAxes.From("YX")).MissingAxesError, Is.Null, "Z is not needed");
            Assert.That(KinematicsFactory.Create(KinematicsName.CoreXZ, KinematicsAxes.From("ZX")).MissingAxesError, Is.Null, "nor Y here");
            Assert.That(KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From("ZYX")).MissingAxesError, Is.Null);
        });
    }

    [Test]
    public void AGeometryKnownBeforeTheAxesExistResolvesThemWhenTheyDo()
    {
        // One adopted from the object model at startup, before config.g's M584 has created the axes,
        // has to pick them up afterwards rather than fail or fix on whatever it saw first
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, AxisIndices.None);
        Assert.That(engine.MissingAxesError, Is.Not.Null, "no axes yet");

        engine.SetAxes(KinematicsAxes.From("Y"));
        engine.SetAxes(KinematicsAxes.From("YX"));
        engine.SetAxes(KinematicsAxes.From("YXZ"));

        Assert.Multiple(() =>
        {
            Assert.That(engine.MissingAxesError, Is.Null);
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b111u));
        });
    }

    [Test]
    public void ADeltasBedTiltLiftsTheAxisLetteredZ()
    {
        // Z first, then U, then Y and X: the tilt reads X and Y and lifts Z wherever they are
        LinearDeltaKinematicsEngine engine = new LinearDeltaKinematicsEngine(
            numTowers: 3, radius: 105.6f,
            diagonals: [215.0f, 215.0f, 215.0f],
            angleCorrections: [0.0f, 0.0f, 0.0f],
            endstopAdjustments: [0.0f, 0.0f, 0.0f],
            homedHeight: 240.0f, printRadius: 80.0f,
            xTilt: 0.01f, yTilt: -0.005f).WithAxes("ZUYX");

        float[] direction = [1.0f, 7.0f, 4.0f, 3.0f];
        engine.ApplyTiltCorrection(direction, 4);

        Assert.Multiple(() =>
        {
            Assert.That(direction[0], Is.EqualTo(1.0f + (3.0f * 0.01f) + (4.0f * -0.005f)).Within(1e-6f), "Z");
            Assert.That(direction[1], Is.EqualTo(7.0f), "U is not corrected");
            Assert.That(direction[2], Is.EqualTo(4.0f), "Y is not corrected");
            Assert.That(direction[3], Is.EqualTo(3.0f), "X is not corrected");
        });
    }

    // ---- Homing -----------------------------------------------------------------------------------

    [Test]
    public void ZIsHomedLastWhereverItIs()
    {
        // Z created first, as M584 Z.. then M584 X.. Y.. would leave it
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("ZXY"));
        engine.HomesZWithProbe = true;
        char[] letters = ['Z', 'X', 'Y'];

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetHomingFileName(0b011, 0, letters, out string first), Is.Zero);
            Assert.That(first, Is.EqualTo("homex.g"), "Z is skipped for X even though it comes first");

            Assert.That(engine.GetHomingFileName(0b001, 0, letters, out _), Is.EqualTo(0b110u), "X and Y before Z");
            Assert.That(engine.GetHomingFileName(0b001, 0b110, letters, out string last), Is.Zero);
            Assert.That(last, Is.EqualTo("homez.g"));
        });
    }

    [Test]
    public void AMachineWithNoZHasNothingToHoldBack()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("XYU"));
        engine.HomesZWithProbe = true;

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetHomingFileName(0b100, 0, ['X', 'Y', 'U'], out string fileName), Is.Zero);
            Assert.That(fileName, Is.EqualTo("homeu.g"));
            Assert.That(engine.AxesToHomeBeforeProbing, Is.EqualTo(0b011u));
        });
    }

    [Test]
    public void ADeltaHomesWhicheverAxesAreItsTowers()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From("UZYX"));

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetHomingFileName(0b0001, 0, ['U', 'Z', 'Y', 'X'], out string u), Is.Zero);
            Assert.That(u, Is.EqualTo("homeu.g"), "U is not a tower");
            Assert.That(engine.GetHomingFileName(0b1000, 0, ['U', 'Z', 'Y', 'X'], out string x), Is.Zero);
            Assert.That(x, Is.EqualTo("homedelta.g"));
            Assert.That(engine.AxesToHomeBeforeProbing, Is.EqualTo(0b1110u));
        });
    }

    // ---- Core matrices ----------------------------------------------------------------------------

    [Test]
    public void ANamedMatrixIsPlacedAtTheLettersItNames()
    {
        // CoreXY couples the X and Y motors, wherever those axes are
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.CoreXY, KinematicsAxes.From("ZYX"));

        Assert.Multiple(() =>
        {
            Assert.That(InverseRow(engine, 2)[..3], Is.EqualTo(new[] { 0.0f, 1.0f, 1.0f }), "X turns both motors one way");
            Assert.That(InverseRow(engine, 1)[..3], Is.EqualTo(new[] { 0.0f, -1.0f, 1.0f }), "Y turns them opposite ways");
            Assert.That(InverseRow(engine, 0)[..3], Is.EqualTo(new[] { 1.0f, 0.0f, 0.0f }), "Z has a motor of its own");
        });
    }

    [Test]
    public void AMatrixRowGoesToTheAxisWithItsLetter()
    {
        // U created before Z, so U is the third axis: M669 U names that one, not the fourth
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("XYUZ"));
        bool seen = false;
        KinematicsEngine configured = KinematicsConfigurator.Apply(engine, KinematicsAxes.NewMoveWithAxes("XYUZ"), new Code("M669 U0:0:2:0"), ref seen);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.True);
            Assert.That(InverseRow(configured, 2)[..4], Is.EqualTo(new[] { 0.0f, 0.0f, 2.0f, 0.0f }));
            Assert.That(InverseRow(configured, 3)[..4], Is.EqualTo(new[] { 0.0f, 0.0f, 0.0f, 1.0f }), "Z is untouched");
        });
    }

    [TestCase("M669 Z0:0:3", "XY", "The machine has no Z axis")]
    [TestCase("M669 A1 X1:0 Z0:0:1", "XY", "The machine has no Z or A axis")]
    public void AMatrixRowForAnAxisTheMachineDoesNotHaveIsRefused(string m669, string letters, string error)
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.CoreXY, KinematicsAxes.From(letters));
        bool seen = false;

        GCodeException? refused = Assert.Throws<GCodeException>(
            () => KinematicsConfigurator.Apply(engine, KinematicsAxes.NewMoveWithAxes(letters), new Code(m669), ref seen));
        Assert.Multiple(() =>
        {
            Assert.That(refused!.Message, Is.EqualTo(error));
            Assert.That(InverseRow(engine, 0)[..2], Is.EqualTo(new[] { 1.0f, 1.0f }), "the geometry is as it was");
        });
    }

    [Test]
    public void AMatrixRowLongerThanTheMachineHasMotorsIsRefused()
    {
        // The values are the motors of the axes there are, in axis order, as in RepRapFirmware
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("XYZ"));
        bool seen = false;

        GCodeException? refused = Assert.Throws<GCodeException>(
            () => KinematicsConfigurator.Apply(engine, KinematicsAxes.NewMoveWithAxes("XYZ"), new Code("M669 X1:0:0:1"), ref seen));
        Assert.That(refused!.Message, Is.EqualTo("array too long for parameter 'X'"));
    }

    [Test]
    public void AShortMatrixRowLeavesTheRestOfTheRowAsItWas()
    {
        // Z's own motor is the third, and the row given stops at the second
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.CoreXZ, KinematicsAxes.From("XYZ"));
        bool seen = false;
        KinematicsEngine configured = KinematicsConfigurator.Apply(engine, KinematicsAxes.NewMoveWithAxes("XYZ"), new Code("M669 Z1"), ref seen);

        Assert.That(InverseRow(configured, 2)[..3], Is.EqualTo(new[] { 1.0f, 0.0f, -3.0f }));
    }

    [Test]
    public void AnM665OrM666IsNotAMatrixRow()
    {
        // RepRapFirmware's CoreKinematics takes rows from M669 only
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("XYZ"));
        Move move = KinematicsAxes.NewMoveWithAxes("XYZ");
        bool seen = false;

        Assert.Multiple(() =>
        {
            Assert.That(KinematicsConfigurator.Apply(engine, move, new Code("M665 X2"), ref seen), Is.SameAs(engine));
            Assert.That(KinematicsConfigurator.Apply(engine, move, new Code("M666 X2"), ref seen), Is.SameAs(engine));
            Assert.That(seen, Is.False);
        });
    }

    [Test]
    public void TheMatrixIsWrittenWhereTheAxesAre()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.CoreXY, KinematicsAxes.From("ZYX"));

        Assert.That(InverseRow(engine, 2)[..3], Is.EqualTo(new[] { 0.0f, 1.0f, 1.0f }));
    }

    [Test]
    public void AMatrixThatCannotBeInvertedIsRefused()
    {
        // Both rows turn the same two motors the same way, so no motor positions tell X from Y
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From("XYZ"));
        bool seen = false;

        GCodeException? refused = Assert.Throws<GCodeException>(
            () => KinematicsConfigurator.Apply(engine, KinematicsAxes.NewMoveWithAxes("XYZ"), new Code("M669 X1:1 Y1:1"), ref seen));
        Assert.That(refused!.Message, Is.EqualTo("Invalid kinematics matrix"), "RepRapFirmware's words");
    }

    [Test]
    public void ASingularMatrixFromElsewhereRefusesToPlanAndCanBeCorrected()
    {
        // M669 never leaves one behind; one built from a whole matrix, as the object model holds it, can be
        KinematicsEngine singular = new CoreKinematicsEngine(KinematicsName.CoreXY, [[1, 1, 0], [1, 1, 0], [0, 0, 1]]).WithAxes("XYZ");

        float[] machinePos = Lay("XYZ", l => l switch { 'X' => 10.0f, 'Y' => 20.0f, _ => 5.0f });
        float[] stepsPerMm = Lay("XYZ", _ => 80.0f);
        int[] motorPos = new int[NumDrives];
        Array.Fill(motorPos, 1234);
        float[] position = new float[NumDrives];
        Array.Fill(position, -1.0f);
        singular.MotorStepsToCartesian(motorPos, stepsPerMm, 3, 3, position);

        Assert.Multiple(() =>
        {
            Assert.That(singular.ConfigurationError, Is.EqualTo("Invalid kinematics matrix"));
            Assert.That(singular.CannotPlanReason, Is.EqualTo("Invalid kinematics matrix"));
            Assert.That(singular.CartesianToMotorSteps(machinePos, stepsPerMm, 3, 3, motorPos),
                        Is.EqualTo(NativeMovementError.UnreachablePosition), "nothing is planned through it");
            Assert.That(position[..3], Is.EqualTo(new[] { -1.0f, -1.0f, -1.0f }),
                        "there is no way back from the motors, so the positions are left alone rather than zeroed");
        });

        bool seen = false;
        KinematicsEngine corrected = KinematicsConfigurator.Apply(singular, KinematicsAxes.NewMoveWithAxes("XYZ"), new Code("M669 Y1:-1"), ref seen);
        Assert.Multiple(() =>
        {
            Assert.That(corrected.CannotPlanReason, Is.Null);
            Assert.That(InverseRow(corrected, 1)[..3], Is.EqualTo(new[] { 1.0f, -1.0f, 0.0f }));
        });
    }

    /// <summary>
    /// RepRapFirmware's named matrices, from <c>CoreKinematics::CoreKinematics</c>: rows are axes and
    /// columns motors, both X, Y, Z, U, V, A, which is where its first axes are
    /// </summary>
    private static IEnumerable<TestCaseData> RepRapFirmwareMatrices()
    {
        float[][] identity = [[1, 0, 0, 0, 0, 0], [0, 1, 0, 0, 0, 0], [0, 0, 1, 0, 0, 0], [0, 0, 0, 1, 0, 0], [0, 0, 0, 0, 1, 0], [0, 0, 0, 0, 0, 1]];
        yield return new TestCaseData(KinematicsName.Cartesian, identity);
        yield return new TestCaseData(KinematicsName.CoreXY, new float[][]
        {
            [1, 1, 0, 0, 0, 0],
            [1, -1, 0, 0, 0, 0],
            [0, 0, 1, 0, 0, 0],
            [0, 0, 0, 1, 0, 0],
            [0, 0, 0, 0, 1, 0],
            [0, 0, 0, 0, 0, 1]
        });
        yield return new TestCaseData(KinematicsName.CoreXZ, new float[][]
        {
            [1, 0, 1, 0, 0, 0],
            [0, 1, 0, 0, 0, 0],
            [3, 0, -3, 0, 0, 0],
            [0, 0, 0, 1, 0, 0],
            [0, 0, 0, 0, 1, 0],
            [0, 0, 0, 0, 0, 1]
        });
        yield return new TestCaseData(KinematicsName.CoreXYU, new float[][]
        {
            [1, 1, 0, 0, 0, 0],
            [1, -1, 0, 1, -1, 0],
            [0, 0, 1, 0, 0, 0],
            [0, 0, 0, 1, 1, 0],
            [0, 0, 0, 0, 1, 0],
            [0, 0, 0, 0, 0, 1]
        });
        yield return new TestCaseData(KinematicsName.CoreXYUV, new float[][]
        {
            [1, 1, 0, 0, 0, 0],
            [1, -1, 0, 0, 0, 0],
            [0, 0, 1, 0, 0, 0],
            [0, 0, 0, 1, 1, 0],
            [0, 0, 0, 1, -1, 0],
            [0, 0, 0, 0, 0, 1]
        });
        yield return new TestCaseData(KinematicsName.MarkForged, new float[][]
        {
            [1, 0, 0, 0, 0, 0],
            [-1, 1, 0, 0, 0, 0],
            [0, 0, 1, 0, 0, 0],
            [0, 0, 0, 1, 0, 0],
            [0, 0, 0, 0, 1, 0],
            [0, 0, 0, 0, 0, 1]
        });
    }

    [TestCaseSource(nameof(RepRapFirmwareMatrices))]
    public void ANamedMatrixIsRepRapFirmwaresWhereverItsAxesAre(KinematicsName geometry, float[][] expected)
    {
        const string rrfOrder = "XYZUVA";
        foreach (string order in new[] { rrfOrder, "AUYVXZ", "ZVAXUY" })
        {
            KinematicsEngine engine = Create(geometry, order);
            Assert.Multiple(() =>
            {
                Assert.That(engine.MissingAxesError, Is.Null, order);
                Assert.That(engine.ConfigurationError, Is.Null, order);
                for (int row = 0; row < rrfOrder.Length; row++)
                {
                    float[] placed = InverseRow(engine, order.IndexOf(rrfOrder[row]));
                    for (int column = 0; column < rrfOrder.Length; column++)
                    {
                        Assert.That(placed[order.IndexOf(rrfOrder[column])], Is.EqualTo(expected[row][column]),
                                    $"{order}: how far {rrfOrder[column]}'s motor turns per mm of {rrfOrder[row]}");
                    }
                }
            });
        }
    }

    // ---- M669, M665 and M666 need the geometry's axes --------------------------------------------

    private static IEnumerable<TestCaseData> GeometriesSelectedWithoutTheirAxes()
    {
        yield return new TestCaseData("M669 K1", "XZ", "The machine has no Y axis, which coreXY kinematics needs");
        yield return new TestCaseData("M669 K\"coreXZ\"", "XY", "The machine has no Z axis, which coreXZ kinematics needs");
        yield return new TestCaseData("M669 K5", "XYZA", "The machine has no U or V axis, which coreXYU kinematics needs");
        yield return new TestCaseData("M669 K8", "XYZU", "The machine has no V axis, which coreXYUV kinematics needs");
        yield return new TestCaseData("M669 K11", "YZ", "The machine has no X axis, which markForged kinematics needs");
        yield return new TestCaseData("M669 K3", "XY", "The machine has no Z axis, which delta kinematics needs");
        yield return new TestCaseData("M665 L215 R105.6 H240", "XYU", "The machine has no Z axis, which delta kinematics needs");
        yield return new TestCaseData("M669 K10", "", "The machine has no X, Y or Z axis, which Rotary delta kinematics needs");
        yield return new TestCaseData("M669 K4 P150 D160", "XZ", "The machine has no Y axis, which Scara kinematics needs");
        yield return new TestCaseData("M669 K9", "Y", "The machine has no X axis, which FiveBarScara kinematics needs");
        yield return new TestCaseData("M669 K7", "XZ", "The machine has no Y axis, which Polar kinematics needs");
        yield return new TestCaseData("M669 K6", "XYZ", "The machine has no U axis, which Hangprinter kinematics needs");
        yield return new TestCaseData("M669 K1 X1:1:0", "XZ", "The machine has no Y axis, which coreXY kinematics needs");
    }

    [TestCaseSource(nameof(GeometriesSelectedWithoutTheirAxes))]
    public void SelectingAGeometryWithoutItsAxesIsRefused(string code, string letters, string error)
    {
        KinematicsEngine current = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From(letters));
        bool seen = false;

        GCodeException? refused = Assert.Throws<GCodeException>(
            () => KinematicsConfigurator.Apply(current, KinematicsAxes.NewMoveWithAxes(letters), new Code(code), ref seen));
        Assert.That(refused!.Message, Is.EqualTo(error));
    }

    [TestCase("M669 K5", KinematicsName.CoreXYU)]
    [TestCase("M669 K8", KinematicsName.CoreXYUV)]
    [TestCase("M669 K3", KinematicsName.LinearDelta)]
    [TestCase("M669 K6", KinematicsName.Hangprinter)]
    public void SelectingAGeometryWithItsAxesInAnyOrderIsAccepted(string code, KinematicsName expected)
    {
        bool seen = false;
        KinematicsEngine engine = KinematicsConfigurator.Apply(KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.From()),
                                                               KinematicsAxes.NewMoveWithAxes("VYAUZX"), new Code(code), ref seen);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Kind, Is.EqualTo(expected));
            Assert.That(engine.CannotPlanReason, Is.Null);
        });
    }

    [TestCase("M665 R100")]
    [TestCase("M666 X0.5")]
    [TestCase("M669 S50")]
    public void ConfiguringAGeometryWithoutItsAxesIsRefused(string code)
    {
        // A delta adopted from the object model on a machine whose config.g never created Z
        KinematicsEngine delta = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From("XYU"));
        bool seen = false;

        GCodeException? refused = Assert.Throws<GCodeException>(
            () => KinematicsConfigurator.Apply(delta, KinematicsAxes.NewMoveWithAxes("XYU"), new Code(code), ref seen));
        Assert.That(refused!.Message, Is.EqualTo("The machine has no Z axis, which delta kinematics needs"));
    }

    [TestCase("M665")]
    [TestCase("M666")]
    [TestCase("M669")]
    public void AskingAboutAGeometryWithoutItsAxesIsNot(string code)
    {
        KinematicsEngine delta = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From("XYU"));
        bool seen = false;

        KinematicsEngine engine = KinematicsConfigurator.Apply(delta, KinematicsAxes.NewMoveWithAxes("XYU"), new Code(code), ref seen);
        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.False);
            Assert.That(engine, Is.SameAs(delta));
        });
    }

    // ---- Tool transform ---------------------------------------------------------------------------

    /// <summary>A tool mapped as M563 maps one by default: each letter to the axis carrying it</summary>
    private static Tool DefaultTool(Move move, float xOffset, float yOffset, float zOffset)
    {
        Tool tool = new();
        foreach (char letter in "XYZ")
        {
            int axis = AxisIndices.IndexOf(move, letter);
            tool.Axes.Add(axis >= 0 ? [axis] : []);
        }
        foreach (Axis axis in move.Axes)
        {
            tool.Offsets.Add(axis.Letter switch { 'X' => xOffset, 'Y' => yOffset, 'Z' => zOffset, _ => 0.0f });
        }
        return tool;
    }

    [Test]
    public void AToolMapsByLetterWhereverTheAxesAre()
    {
        Move canonicalMove = KinematicsAxes.NewMoveWithAxes("XYZU"), permutedMove = KinematicsAxes.NewMoveWithAxes("UZXY");
        Tool canonicalTool = DefaultTool(canonicalMove, 3.0f, -2.0f, 0.5f), permutedTool = DefaultTool(permutedMove, 3.0f, -2.0f, 0.5f);
        canonicalTool.IsRetracted = permutedTool.IsRetracted = true;
        canonicalTool.Retraction.ZHop = permutedTool.Retraction.ZHop = 0.4f;

        Func<char, float> user = letter => letter switch { 'X' => 10.0f, 'Y' => 20.0f, 'Z' => 5.0f, _ => 99.0f };
        MovementState canonicalState = new(), permutedState = new();
        Lay("XYZU", user).AsSpan(0, 4).CopyTo(canonicalState.CurrentUserPosition);
        Lay("UZXY", user).AsSpan(0, 4).CopyTo(permutedState.CurrentUserPosition);

        float[] canonicalCoords = new float[4], permutedCoords = new float[4];
        ToolTransform.Apply(canonicalTool, canonicalMove, canonicalState, canonicalCoords, 4);
        ToolTransform.Apply(permutedTool, permutedMove, permutedState, permutedCoords, 4);

        Assert.Multiple(() =>
        {
            foreach (char letter in "XYZU")
            {
                Assert.That(permutedCoords["UZXY".IndexOf(letter)], Is.EqualTo(canonicalCoords["XYZU".IndexOf(letter)]).Within(1e-5f),
                            letter.ToString());
            }
            Assert.That(permutedCoords["UZXY".IndexOf('Z')], Is.EqualTo(5.0f - 0.5f + 0.4f).Within(1e-5f), "Z gets the offset and the hop");
        });

        // And back again, into the axes carrying the letters
        ToolTransform.Remove(permutedTool, permutedMove, permutedCoords, 4);
        Assert.Multiple(() =>
        {
            foreach (char letter in "XYZU")
            {
                Assert.That(permutedCoords["UZXY".IndexOf(letter)], Is.EqualTo(user(letter)).Within(1e-4f), letter.ToString());
            }
        });
    }

    [Test]
    public void ALetterTheMachineDoesNotHaveContributesNothing()
    {
        // No Z: the default Z map is empty, so the hop has nowhere to go and nothing reads a Z
        Move move = KinematicsAxes.NewMoveWithAxes("YX");
        Tool tool = DefaultTool(move, 1.0f, 2.0f, 0.0f);
        tool.IsRetracted = true;
        tool.Retraction.ZHop = 0.4f;

        MovementState state = new();
        state.CurrentUserPosition[0] = 20.0f;               // Y
        state.CurrentUserPosition[1] = 10.0f;               // X

        float[] coords = new float[2];
        ToolTransform.Apply(tool, move, state, coords, 2);

        Assert.Multiple(() =>
        {
            Assert.That(tool.Axes[2], Is.Empty, "M563's default Z map on a machine without Z");
            Assert.That(coords[0], Is.EqualTo(18.0f), "Y less its offset, and no hop");
            Assert.That(coords[1], Is.EqualTo(9.0f), "X less its offset, and no hop");
            Assert.That(ToolTransform.AxisBitmap(tool, move, 'Z'), Is.Zero);
        });
    }

    [Test]
    public void AToolWithNoMapRecordedDrivesTheAxisCarryingTheLetter()
    {
        Move move = KinematicsAxes.NewMoveWithAxes("ZYX");
        Tool tool = new();

        Assert.Multiple(() =>
        {
            Assert.That(ToolTransform.AxisBitmap(tool, move, 'X'), Is.EqualTo(0b100u));
            Assert.That(ToolTransform.AxisBitmap(tool, move, 'Z'), Is.EqualTo(0b001u));
            Assert.That(ToolTransform.AxisBitmap(null, move, 'Y'), Is.EqualTo(0b010u));
        });
    }

    // ---- The lookup itself ------------------------------------------------------------------------

    [Test]
    public void TheAxesAreFoundByLetter()
    {
        AxisIndices axes = AxisIndices.From(KinematicsAxes.NewMoveWithAxes("UZXa"));

        Assert.Multiple(() =>
        {
            Assert.That(axes.X, Is.EqualTo(2));
            Assert.That(axes.Y, Is.EqualTo(-1), "no Y axis");
            Assert.That(axes.Z, Is.EqualTo(1));
            Assert.That(axes.IndexOf('a'), Is.EqualTo(3), "a lower case letter is a letter of its own");
            Assert.That(axes.IndexOf('A'), Is.EqualTo(-1));
            Assert.That(axes.XyBitmap, Is.EqualTo(0b0100u));
            Assert.That(axes.XyzBitmap, Is.EqualTo(0b0110u));
            Assert.That(AxisIndices.IndexOf(KinematicsAxes.NewMoveWithAxes("UZXa"), 'X'), Is.EqualTo(2));
            Assert.That(AxisIndices.None.X, Is.EqualTo(-1));
            Assert.That(axes.SameAs(KinematicsAxes.From("UZXa")), Is.True);
            Assert.That(axes.SameAs(KinematicsAxes.From("UZX")), Is.False);
        });
    }
}
