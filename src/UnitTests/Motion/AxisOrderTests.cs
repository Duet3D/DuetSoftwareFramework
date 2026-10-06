using System;
using System.Collections.Generic;
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
    /// Build a geometry by the name a test case gives it
    /// </summary>
    /// <remarks>
    /// By name rather than by passing a factory, because the geometries are internal and a public
    /// test method cannot take a delegate that returns one
    /// </remarks>
    private static KinematicsEngine Create(string geometry) => geometry switch
    {
        "LinearDelta" => LinearDeltaKinematicsEngine.CreateDefault(),
        "RotaryDelta" => new RotaryDeltaKinematicsEngine(),
        "Scara" => new ScaraKinematicsEngine(crosstalk: [0.1f, 0.05f, -0.02f]),
        "FiveBarScara" => FiveBarScaraKinematicsEngine.CreateDefault(),
        "Polar" => new PolarKinematicsEngine(0.0f, 150.0f, 0.0f),
        "Hangprinter" => HangprinterKinematicsEngine.CreateDefault(),
        _ => CoreKinematicsEngine.TryCreate(geometry)!
    };

    /// <summary>One of each geometry, with a point it can reach given per letter</summary>
    private static IEnumerable<TestCaseData> Geometries()
    {
        yield return new TestCaseData("LinearDelta", new[] { 10.0f, -20.0f, 50.0f });
        yield return new TestCaseData("RotaryDelta", new[] { 20.0f, 0.0f, 110.0f });
        yield return new TestCaseData("Scara", new[] { 100.0f, 50.0f, 5.0f });
        yield return new TestCaseData("FiveBarScara", new[] { 20.0f, 140.0f, 3.0f });
        yield return new TestCaseData("Polar", new[] { 60.0f, 80.0f, -2.5f });
        yield return new TestCaseData("Hangprinter", new[] { 100.0f, 0.0f, 200.0f });
        yield return new TestCaseData("CoreXY", new[] { 10.0f, 20.0f, 5.0f });
        yield return new TestCaseData("CoreXYU", new[] { 10.0f, 20.0f, 5.0f });
        yield return new TestCaseData("MarkForged", new[] { 10.0f, 20.0f, 5.0f });
    }

    /// <summary>The point a test case gives, plus U and V, by letter</summary>
    private static Func<char, float> Point(float[] xyz)
        => letter => letter switch { 'X' => xyz[0], 'Y' => xyz[1], 'Z' => xyz[2], 'U' => 7.0f, _ => -3.0f };

    [TestCaseSource(nameof(Geometries))]
    public void PermutedAxesGiveTheSameMotorPositions(string geometry, float[] xyz)
    {
        KinematicsEngine canonical = Create(geometry).WithAxes(Canonical);
        KinematicsEngine permuted = Create(geometry).WithAxes(Permuted);
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
    public void PermutedAxesRoundTripToTheSamePosition(string geometry, float[] xyz)
    {
        KinematicsEngine permuted = Create(geometry).WithAxes(Permuted);
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
    public void PermutedAxesCoupleTheSameMotors(string geometry, float[] xyz)
    {
        _ = xyz;
        KinematicsEngine canonical = Create(geometry).WithAxes(Canonical);
        KinematicsEngine permuted = Create(geometry).WithAxes(Permuted);

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
    public void PermutedAxesHomeTheirEndstopsToTheSamePlace(string geometry, float[] xyz)
    {
        KinematicsEngine canonical = Create(geometry).WithAxes(Canonical);
        KinematicsEngine permuted = Create(geometry).WithAxes(Permuted);
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
    public void PermutedAxesAreLimitedTheSameWay(string geometry, float[] xyz)
    {
        _ = xyz;
        KinematicsEngine canonical = WithBox(Create(geometry).WithAxes(Canonical), Canonical);
        KinematicsEngine permuted = WithBox(Create(geometry).WithAxes(Permuted), Permuted);

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
    public void PermutedAxesLimitTheSameSpeed(string geometry, float[] xyz)
    {
        _ = xyz;
        KinematicsEngine canonical = Create(geometry).WithAxes(Canonical);
        KinematicsEngine permuted = Create(geometry).WithAxes(Permuted);

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
        yield return new TestCaseData("LinearDelta", "XYU", "The machine has no Z axis, which delta kinematics needs");
        yield return new TestCaseData("RotaryDelta", "ZU", "The machine has no X or Y axis, which Rotary delta kinematics needs");
        yield return new TestCaseData("Scara", "ZXU", "The machine has no Y axis, which Scara kinematics needs");
        yield return new TestCaseData("FiveBarScara", "XZ", "The machine has no Y axis, which FiveBarScara kinematics needs");
        yield return new TestCaseData("Polar", "YZ", "The machine has no X axis, which Polar kinematics needs");
        yield return new TestCaseData("Hangprinter", "", "The machine has no X, Y or Z axis, which Hangprinter kinematics needs");
    }

    [TestCaseSource(nameof(GeometriesMissingAnAxis))]
    public void AGeometryMissingAnAxisRefusesToTransform(string geometry, string letters, string error)
    {
        KinematicsEngine engine = Create(geometry).WithAxes(letters);

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
            Assert.That(engine.AxesAssumedHomed((1u << letters.Length) - 1) & AxisIndices.From(letters).XyBitmap, Is.Zero,
                        "naming every axis there is still does not pin the geometry down");
        });
    }

    [Test]
    public void AScaraWithoutZMovesItsArmsAndHasNoZToDrag()
    {
        // A pen plotter: two joints and no lift. The arms come out where they would on the same SCARA
        // with Z, and the crosstalk onto Z has nothing to act on
        const string noZ = "YXU";
        KinematicsEngine withZ = Create("Scara").WithAxes(Canonical);
        KinematicsEngine withoutZ = Create("Scara").WithAxes(noZ);
        Func<char, float> point = Point([100.0f, 50.0f, 5.0f]);
        float[] stepsPerMm = Lay(noZ, l => StepsByLetter[l]);

        int[] canonicalMotors = new int[NumDrives], motors = new int[NumDrives];
        Assert.That(withZ.CartesianToMotorSteps(Lay(Canonical, point), Lay(Canonical, l => StepsByLetter[l]), 5, 5, canonicalMotors),
                    Is.EqualTo(NativeMovementError.Ok));
        NativeMovementError error = withoutZ.CartesianToMotorSteps(Lay(noZ, point), stepsPerMm, 3, 3, motors);

        float[] roundTripped = new float[NumDrives];
        withoutZ.MotorStepsToCartesian(motors, stepsPerMm, 3, 3, roundTripped);

        AxisIndices axes = AxisIndices.From(noZ);
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

    [Test]
    public void AGeometryThatNeedsNoParticularAxisIsNeverMissingOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CoreKinematicsEngine.TryCreate(KinematicsName.Cartesian)!.WithAxes("Y").MissingAxesError, Is.Null);
            Assert.That(CoreKinematicsEngine.TryCreate(KinematicsName.CoreXY)!.WithAxes("UV").MissingAxesError, Is.Null);
            Assert.That(LinearDeltaKinematicsEngine.CreateDefault().WithAxes("ZYX").MissingAxesError, Is.Null);
        });
    }

    [Test]
    public void AGeometrySelectedBeforeTheAxesExistResolvesThemWhenTheyDo()
    {
        // config.g commonly selects the geometry before M584 creates the axes, so the geometry has to
        // pick them up afterwards rather than fail or fix on whatever it saw first
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        Assert.That(engine.MissingAxesError, Is.Not.Null, "no axes yet");

        engine.WithAxes("Y");
        engine.WithAxes("YX");
        engine.WithAxes("YXZ");

        Assert.Multiple(() =>
        {
            Assert.That(engine.MissingAxesError, Is.Null);
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b111u));
            Assert.That(engine.GetTiltCorrection(1), Is.EqualTo(engine.XTilt), "X is the second axis");
        });
    }

    // ---- Homing -----------------------------------------------------------------------------------

    [Test]
    public void ZIsHomedLastWhereverItIs()
    {
        // Z created first, as M584 Z.. then M584 X.. Y.. would leave it
        KinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.Cartesian)!.WithAxes("ZXY");
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
        KinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.Cartesian)!.WithAxes("XYU");
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
        KinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault().WithAxes("UZYX");

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
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.CoreXY)!.WithAxes("ZYX");

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetInverseMatrixRow(2)[..3], Is.EqualTo(new[] { 0.0f, 1.0f, 1.0f }), "X turns both motors one way");
            Assert.That(engine.GetInverseMatrixRow(1)[..3], Is.EqualTo(new[] { 0.0f, -1.0f, 1.0f }), "Y turns them opposite ways");
            Assert.That(engine.GetInverseMatrixRow(0)[..3], Is.EqualTo(new[] { 1.0f, 0.0f, 0.0f }), "Z has a motor of its own");
        });
    }

    [Test]
    public void ANamedMatrixFollowsAxesCreatedLater()
    {
        // M669 K1, then M584 Y.., then M584 X..: the X half of the coupling arrives with the X axis
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.CoreXY)!;
        engine.WithAxes("Y");
        Assert.That(engine.GetInverseMatrixRow(0)[..2], Is.EqualTo(new[] { -1.0f, 0.0f }), "only Y's own motor so far");

        engine.WithAxes("YX");
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetInverseMatrixRow(0)[..2], Is.EqualTo(new[] { -1.0f, 1.0f }));
            Assert.That(engine.GetInverseMatrixRow(1)[..2], Is.EqualTo(new[] { 1.0f, 1.0f }));
            Assert.That(engine.HasSharedMotor(0) && engine.HasSharedMotor(1), Is.True);
        });
    }

    [Test]
    public void AMatrixRowGoesToTheAxisWithItsLetter()
    {
        // U created before Z, so U is the third axis: M669 U names that one, not the fourth
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.Cartesian)!.WithAxes("XYUZ");
        bool seen = false;
        KinematicsEngine configured = engine.Configure(new Code("M669 U0:0:2:0"), ref seen);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.True);
            Assert.That(((CoreKinematicsEngine)configured).GetInverseMatrixRow(2)[..4], Is.EqualTo(new[] { 0.0f, 0.0f, 2.0f, 0.0f }));
            Assert.That(((CoreKinematicsEngine)configured).GetInverseMatrixRow(3)[..4], Is.EqualTo(new[] { 0.0f, 0.0f, 0.0f, 1.0f }),
                        "Z is untouched");
        });
    }

    [Test]
    public void AMatrixRowForAnAxisNotYetCreatedWaitsForIt()
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.Cartesian)!.WithAxes("XY");
        bool seen = false;
        KinematicsEngine configured = engine.Configure(new Code("M669 Z0:0:3"), ref seen);
        Assert.That(seen, Is.True);

        configured.WithAxes("XYZ");
        Assert.That(((CoreKinematicsEngine)configured).GetInverseMatrixRow(2)[..3], Is.EqualTo(new[] { 0.0f, 0.0f, 3.0f }));
    }

    [Test]
    public void TheMatrixIsWrittenWhereTheAxesAre()
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate(KinematicsName.CoreXY)!.WithAxes("ZYX");
        CoreKinematics kinematics = (CoreKinematics)DuetAPI.ObjectModel.Kinematics.Create(KinematicsName.CoreXY);
        engine.WriteTo(kinematics);

        Assert.That(kinematics.InverseMatrix[2][..3], Is.EqualTo(new[] { 0.0f, 1.0f, 1.0f }));
    }

    // ---- Tool transform ---------------------------------------------------------------------------

    /// <summary>A machine whose axes were created in the given order</summary>
    private static Move NewMove(string letters)
    {
        Move move = new();
        foreach (char letter in letters)
        {
            move.Axes.Add(new Axis { Letter = letter, Visible = true });
        }
        return move;
    }

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
        Move canonicalMove = NewMove("XYZU"), permutedMove = NewMove("UZXY");
        Tool canonicalTool = DefaultTool(canonicalMove, 3.0f, -2.0f, 0.5f), permutedTool = DefaultTool(permutedMove, 3.0f, -2.0f, 0.5f);
        canonicalTool.IsRetracted = permutedTool.IsRetracted = true;
        canonicalTool.Retraction.ZHop = permutedTool.Retraction.ZHop = 0.4f;

        Func<char, float> user = letter => letter switch { 'X' => 10.0f, 'Y' => 20.0f, 'Z' => 5.0f, _ => 99.0f };
        MovementState canonicalState = new(), permutedState = new();
        Lay("XYZU", user).AsSpan(0, 4).CopyTo(canonicalState.CurrentUserPosition);
        Lay("UZXY", user).AsSpan(0, 4).CopyTo(permutedState.CurrentUserPosition);

        float[] canonicalCoords = new float[4], permutedCoords = new float[4];
        ToolTransform.Apply(canonicalTool, canonicalMove, AxisIndices.From(canonicalMove), canonicalState, canonicalCoords, 4);
        ToolTransform.Apply(permutedTool, permutedMove, AxisIndices.From(permutedMove), permutedState, permutedCoords, 4);

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
        ToolTransform.Remove(permutedTool, permutedMove, AxisIndices.From(permutedMove), permutedCoords, 4);
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
        Move move = NewMove("YX");
        Tool tool = DefaultTool(move, 1.0f, 2.0f, 0.0f);
        tool.IsRetracted = true;
        tool.Retraction.ZHop = 0.4f;

        MovementState state = new();
        state.CurrentUserPosition[0] = 20.0f;               // Y
        state.CurrentUserPosition[1] = 10.0f;               // X

        float[] coords = new float[2];
        ToolTransform.Apply(tool, move, AxisIndices.From(move), state, coords, 2);

        Assert.Multiple(() =>
        {
            Assert.That(tool.Axes[2], Is.Empty, "M563's default Z map on a machine without Z");
            Assert.That(coords[0], Is.EqualTo(18.0f), "Y less its offset, and no hop");
            Assert.That(coords[1], Is.EqualTo(9.0f), "X less its offset, and no hop");
            Assert.That(ToolTransform.AxisBitmap(tool, AxisIndices.From(move), 'Z'), Is.Zero);
        });
    }

    [Test]
    public void AToolWithNoMapRecordedDrivesTheAxisCarryingTheLetter()
    {
        Move move = NewMove("ZYX");
        Tool tool = new();

        Assert.Multiple(() =>
        {
            Assert.That(ToolTransform.AxisBitmap(tool, AxisIndices.From(move), 'X'), Is.EqualTo(0b100u));
            Assert.That(ToolTransform.AxisBitmap(tool, AxisIndices.From(move), 'Z'), Is.EqualTo(0b001u));
            Assert.That(ToolTransform.AxisBitmap(null, AxisIndices.From(move), 'Y'), Is.EqualTo(0b010u));
        });
    }

    // ---- The lookup itself ------------------------------------------------------------------------

    [Test]
    public void TheAxesAreFoundByLetter()
    {
        AxisIndices axes = AxisIndices.From(NewMove("UZXa"));

        Assert.Multiple(() =>
        {
            Assert.That(axes.X, Is.EqualTo(2));
            Assert.That(axes.Y, Is.EqualTo(-1), "no Y axis");
            Assert.That(axes.Z, Is.EqualTo(1));
            Assert.That(axes.IndexOf('a'), Is.EqualTo(3), "a lower case letter is a letter of its own");
            Assert.That(axes.IndexOf('A'), Is.EqualTo(-1));
            Assert.That(axes.XyBitmap, Is.EqualTo(0b0100u));
            Assert.That(axes.XyzBitmap, Is.EqualTo(0b0110u));
            Assert.That(AxisIndices.IndexOf(NewMove("UZXa"), 'X'), Is.EqualTo(2));
            Assert.That(AxisIndices.None.X, Is.EqualTo(-1));
            Assert.That(axes.SameAs(AxisIndices.From("UZXa")), Is.True);
            Assert.That(axes.SameAs(AxisIndices.From("UZX")), Is.False);
        });
    }
}
