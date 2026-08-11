using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Tests for which homing macro G28 runs next
/// </summary>
/// <remarks>
/// Nothing in DuetControlServer knows how to home a machine; the machine's own macros do. All G28
/// decides is which macro comes next, so that decision is the whole of the logic worth pinning down
/// </remarks>
[TestFixture]
public class HomingFilesTests
{

    /// <summary>
    /// Snapshot a machine, building its geometry from the object model as the factory does
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <returns>The snapshot</returns>
    /// <remarks>
    /// The planner owns its geometry rather than deriving it (§14), so the snapshot is handed one.
    /// These tests describe a machine as an object model and want the geometry that describes, which
    /// is what KinematicsFactory.Create is for
    /// </remarks>
    private static MotionParameters Snapshot(Move move)
        => MotionParameters.FromObjectModel(move, KinematicsFactory.Create(move.Kinematics));
    private static readonly char[] Xyz = ['X', 'Y', 'Z'];

    /// <summary>
    /// The engine a machine of the given geometry would use
    /// </summary>
    /// <param name="name">Which geometry</param>
    /// <returns>The engine</returns>
    /// <remarks>
    /// Built through the object model rather than by calling a constructor, because that is the only
    /// way a machine ever gets one: M669 writes the geometry and the engine follows from it
    /// </remarks>
    private static KinematicsEngine Engine(KinematicsName name)
    {
        Move move = new() { Kinematics = Kinematics.Create(name) };
        return Snapshot(move).Geometry;
    }

    private static string NextFile(KinematicsEngine engine, uint toBeHomed, uint alreadyHomed = 0,
                                   char[]? letters = null)
    {
        engine.GetHomingFileName(toBeHomed, alreadyHomed, letters ?? Xyz, out string fileName);
        return fileName;
    }

    [Test]
    public void HomingEverythingRunsTheOneMacroThatDoesIt()
    {
        // homeall.g exists so a machine can home in one coordinated sequence rather than three
        // independent ones, which is what most beds need
        Assert.That(NextFile(Engine(KinematicsName.Cartesian), 0b111), Is.EqualTo("homeall.g"));
    }

    [Test]
    public void HomingSomeAxesRunsTheLowestOnesMacro()
    {
        // The caller loops, so naming only the first is enough: it will ask again once that macro has
        // run, and the macro is free to have homed more than it was asked for
        KinematicsEngine engine = Engine(KinematicsName.Cartesian);
        Assert.Multiple(() =>
        {
            Assert.That(NextFile(engine, 0b011), Is.EqualTo("homex.g"), "X and Y, so X first");
            Assert.That(NextFile(engine, 0b010), Is.EqualTo("homey.g"));
            Assert.That(NextFile(engine, 0b100), Is.EqualTo("homez.g"));
        });
    }

    [Test]
    public void ALowerCaseAxisLetterIsWrittenWithAnApostrophe()
    {
        // home'a.g and homea.g are different files, so an axis named 'a' must not collide with A
        Assert.That(NextFile(Engine(KinematicsName.Cartesian), 0b1000, letters: ['X', 'Y', 'Z', 'a']),
                    Is.EqualTo("home'a.g"));
    }

    [Test]
    public void ZCannotBeHomedWithAProbeUntilXAndYAre()
    {
        // Homing Z with a probe means driving the nozzle at the bed. Doing that before X and Y are
        // known could put it over a clip, the edge of the bed, or nothing at all
        KinematicsEngine engine = Engine(KinematicsName.Cartesian);
        engine.HomesZWithProbe = true;

        uint mustHomeFirst = engine.GetHomingFileName(0b100, 0, Xyz, out _);
        Assert.That(mustHomeFirst, Is.EqualTo(0b011), "X and Y first");

        // Once they are homed the probe can be positioned, so Z is allowed
        Assert.That(engine.GetHomingFileName(0b100, 0b011, Xyz, out string fileName), Is.Zero);
        Assert.That(fileName, Is.EqualTo("homez.g"));
    }

    [Test]
    public void AZEndstopOfItsOwnNeedsNothingHomedFirst()
    {
        KinematicsEngine engine = Engine(KinematicsName.Cartesian);
        engine.HomesZWithProbe = false;
        Assert.That(engine.GetHomingFileName(0b100, 0, Xyz, out string fileName), Is.Zero);
        Assert.That(fileName, Is.EqualTo("homez.g"));
    }

    [Test]
    public void ADeltaHomesEveryTowerWhicheverAxisWasAskedFor()
    {
        // No carriage of a delta moves on its own and the effector is only where all three put it, so
        // homing one axis of one is meaningless
        KinematicsEngine engine = Engine(KinematicsName.LinearDelta);
        Assert.Multiple(() =>
        {
            Assert.That(NextFile(engine, 0b001), Is.EqualTo("homedelta.g"), "X alone");
            Assert.That(NextFile(engine, 0b100), Is.EqualTo("homedelta.g"), "Z alone");
            Assert.That(NextFile(engine, 0b111), Is.EqualTo("homedelta.g"),
                        "even asking for everything, because homedelta.g is what homes everything here");
        });
    }

    [Test]
    public void ADeltaHomesAllThreeTowersBeforeItProbes()
    {
        // The default is X and Y, which is right for a machine whose Z moves a motor of its own. A
        // delta has no such axis, so its Z has to be homed before the probe can be lowered
        Assert.That(Engine(KinematicsName.LinearDelta).AxesToHomeBeforeProbing, Is.EqualTo(0b111u));
        Assert.That(Engine(KinematicsName.Cartesian).AxesToHomeBeforeProbing, Is.EqualTo(0b011u));
    }

    [Test]
    public void ScaraNamesItsMacrosAfterTheJointsRatherThanTheDirections()
    {
        // X and Y of a SCARA are the two arm joints, not two directions, so homex.g would be a
        // misleading name for what the macro has to do
        KinematicsEngine engine = Engine(KinematicsName.Scara);
        Assert.Multiple(() =>
        {
            Assert.That(NextFile(engine, 0b001), Is.EqualTo("homeproximal.g"));
            Assert.That(NextFile(engine, 0b010), Is.EqualTo("homedistal.g"));
            Assert.That(NextFile(engine, 0b100), Is.EqualTo("homez.g"), "Z is still Z");
        });
    }

    [Test]
    public void PolarNamesTheRadiusArmButNotTheTurntable()
    {
        // The turntable has nowhere to home to, so only the radius arm has a macro of its own
        KinematicsEngine engine = Engine(KinematicsName.Polar);
        Assert.Multiple(() =>
        {
            Assert.That(NextFile(engine, 0b001), Is.EqualTo("homeradius.g"));
            Assert.That(NextFile(engine, 0b010), Is.EqualTo("homey.g"));
        });
    }
}
