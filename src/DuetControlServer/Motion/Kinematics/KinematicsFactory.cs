using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// Builds the geometry engine the object model's kinematics describes
/// </summary>
/// <remarks>
/// <para>
/// The object model does not hold every parameter every geometry has - it reports what
/// RepRapFirmware reports, and RepRapFirmware keeps some of it to itself. Where a parameter is
/// missing the engine takes the same default RepRapFirmware would have before the M-code that sets
/// it has been seen, so a machine that has not been configured behaves as an unconfigured machine of
/// that kind rather than as some other kind of machine.
/// </para>
/// <para>
/// Since §14.6 step 3 the geometry is authoritative and <c>KinematicsEngine.WriteTo</c> is what keeps
/// the object model in step with it, so reading in the other direction is no longer how a machine is
/// configured. It is still how one is adopted: <c>MovePlanner.ReconfigureAsync</c> takes the object
/// model's description at startup, before any code has selected a geometry, and it is the inverse the
/// round-trip test needs to show that the projection loses nothing
/// </para>
/// </remarks>
internal static class KinematicsFactory
{
    /// <summary>
    /// Build the geometry engine described by the object model's kinematics
    /// </summary>
    /// <param name="kinematics">The configured kinematics</param>
    /// <returns>The engine, falling back to Cartesian if the geometry cannot be described</returns>
    public static KinematicsEngine Create(DuetAPI.ObjectModel.Kinematics kinematics)
    {
        KinematicsEngine engine = CreateGeometry(kinematics);

        // Every geometry's Configure calls RepRapFirmware's TryConfigureSegmentation, so M669 S and T
        // mean the same thing on all of them and are applied here rather than per geometry. Absent
        // means M669 has not set them, which is not the same as setting them to zero: zero turns
        // segmentation off, and the geometry's own default is what applies until it is asked for
        if (kinematics.Segmentation is MoveSegmentation segmentation)
        {
            engine.ConfigureSegmentation(segmentation.SegmentsPerSec, segmentation.MinSegLength);
        }

        return engine;
    }

    /// <summary>
    /// Build a geometry with the defaults it has before any M-code has configured it
    /// </summary>
    /// <param name="name">Which geometry</param>
    /// <returns>The engine</returns>
    /// <remarks>
    /// <para>
    /// What RepRapFirmware's <c>SelectKinematics</c> does: constructing a geometry gives it the
    /// defaults its constructor carries, and the M-codes then configure it from there.
    /// </para>
    /// <para>
    /// Every geometry is built through its own <c>CreateDefault</c> so that what "unconfigured" means
    /// for a machine is stated once, next to that machine's parameters, rather than being assembled
    /// here from constructor arguments this class would have to keep in step. The core geometries
    /// take a name because six of them share one class, and theirs falls back to Cartesian - an
    /// unconfigured machine - for a name that is not a core geometry's
    /// </para>
    /// </remarks>
    public static KinematicsEngine Create(KinematicsName name)
        => name switch
        {
            KinematicsName.LinearDelta => LinearDeltaKinematicsEngine.CreateDefault(),
            KinematicsName.RotaryDelta => RotaryDeltaKinematicsEngine.CreateDefault(),
            KinematicsName.Scara => ScaraKinematicsEngine.CreateDefault(),
            KinematicsName.FiveBarScara => FiveBarScaraKinematicsEngine.CreateDefault(),
            KinematicsName.Polar => PolarKinematicsEngine.CreateDefault(),
            KinematicsName.Hangprinter => HangprinterKinematicsEngine.CreateDefault(),
            _ => CoreKinematicsEngine.CreateDefault(name)
        };

    /// <summary>
    /// Build the geometry itself, before M669's segmentation parameters are applied to it
    /// </summary>
    /// <param name="kinematics">The configured kinematics</param>
    /// <returns>The engine</returns>
    /// <remarks>
    /// One case per object model class, each delegating to that geometry's <c>CreateX</c>. Two of the
    /// classes carry two geometries each - both deltas share one and both SCARAs share the other,
    /// because that is how RepRapFirmware reports them - so those cases have to look at the name as
    /// well as the type. The geometries the object model says nothing about fall through to
    /// <see cref="Create(KinematicsName)"/>, which is also where an unusable description ends up
    /// </remarks>
    private static KinematicsEngine CreateGeometry(DuetAPI.ObjectModel.Kinematics kinematics)
        => kinematics switch
        {
            CoreKinematics core => CreateCore(core),
            DeltaKinematics delta when delta.Name != KinematicsName.RotaryDelta => CreateDelta(delta),
            ScaraKinematics scara when scara.Name != KinematicsName.FiveBarScara => CreateScara(scara),
            PolarKinematics polar => CreatePolar(polar),
            HangprinterKinematics hangprinter => CreateHangprinter(hangprinter),

            // A rotary delta and a five-bar SCARA reach here: nothing in their nodes describes them,
            // so the geometry their name selects with its own defaults is all there is to build
            _ => Create(kinematics.Name)
        };

    /// <summary>
    /// Build a core geometry from the object model
    /// </summary>
    /// <param name="core">The configured kinematics</param>
    /// <returns>The engine</returns>
    /// <remarks>
    /// The matrix in the object model is authoritative when it is there: M669 can set an arbitrary
    /// one, which is the whole point of the matrix form. A matrix that cannot be inverted describes a
    /// machine whose motors cannot reach every position, so the named geometry's own matrix is used
    /// instead of a geometry that cannot plan
    /// </remarks>
    private static KinematicsEngine CreateCore(CoreKinematics core)
    {
        if (core.InverseMatrix.Count > 0)
        {
            float[][] inverse = new float[core.InverseMatrix.Count][];
            for (int i = 0; i < inverse.Length; i++)
            {
                inverse[i] = core.InverseMatrix[i];
            }

            CoreKinematicsEngine engine = new(core.Name, inverse);
            if (engine.IsValid)
            {
                return engine;
            }
        }

        return CoreKinematicsEngine.CreateDefault(core.Name);
    }

    /// <summary>
    /// Build a delta geometry from the object model
    /// </summary>
    /// <param name="delta">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine CreateDelta(DeltaKinematics delta)
    {
        int numTowers = Math.Clamp(delta.Towers.Count, LinearDeltaKinematicsEngine.UsualNumTowers, LinearDeltaKinematicsEngine.MaxTowers);

        float[] diagonals = new float[numTowers];
        float[] endstopAdjustments = new float[numTowers];
        float[] angleCorrections = new float[LinearDeltaKinematicsEngine.UsualNumTowers];

        for (int tower = 0; tower < numTowers; tower++)
        {
            DeltaTower? configured = tower < delta.Towers.Count ? delta.Towers[tower] : null;
            diagonals[tower] = configured is not null && configured.Diagonal > 0.0f
                ? configured.Diagonal
                : LinearDeltaKinematicsEngine.DefaultDiagonal;
            endstopAdjustments[tower] = configured?.EndstopAdjustment ?? 0.0f;
            if (tower < LinearDeltaKinematicsEngine.UsualNumTowers)
            {
                angleCorrections[tower] = configured?.AngleCorrection ?? 0.0f;
            }
        }

        // A delta radius of zero would put all three towers on top of each other, which is not a
        // machine - so it means M665 has not run rather than that the towers are really there
        float radius = delta.DeltaRadius > 0.0f ? delta.DeltaRadius : LinearDeltaKinematicsEngine.DefaultDeltaRadius;
        float printRadius = delta.PrintRadius > 0.0f ? delta.PrintRadius : LinearDeltaKinematicsEngine.DefaultPrintRadius;

        return new LinearDeltaKinematicsEngine(
            numTowers, radius, diagonals, angleCorrections, endstopAdjustments,
            delta.HomedHeight, printRadius, delta.XTilt, delta.YTilt);
    }

    /// <summary>
    /// Build a SCARA geometry from the object model
    /// </summary>
    /// <param name="scara">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine CreateScara(ScaraKinematics scara)
    {
        float[] thetaLimits = [.. scara.ThetaLimits];
        float[] psiLimits = [.. scara.PsiLimits];
        float[] crosstalk = [.. scara.Crosstalk];

        float proximal = scara.ProximalLength > 0.0f ? scara.ProximalLength : ScaraKinematicsEngine.DefaultProximalArmLength;
        float distal = scara.DistalLength > 0.0f ? scara.DistalLength : ScaraKinematicsEngine.DefaultDistalArmLength;

        // Both limits at zero means the joint cannot turn at all, which is the object model's default
        // rather than a real configuration
        if (thetaLimits.Length < 2 || (thetaLimits[0] == 0.0f && thetaLimits[1] == 0.0f))
        {
            thetaLimits = [ScaraKinematicsEngine.DefaultMinTheta, ScaraKinematicsEngine.DefaultMaxTheta];
        }
        if (psiLimits.Length < 2 || (psiLimits[0] == 0.0f && psiLimits[1] == 0.0f))
        {
            psiLimits = [ScaraKinematicsEngine.DefaultMinPsi, ScaraKinematicsEngine.DefaultMaxPsi];
        }

        return new ScaraKinematicsEngine(
            proximal, distal, thetaLimits, psiLimits, crosstalk,
            scara.XOffset, scara.YOffset, scara.MinRadius);
    }

    /// <summary>
    /// Build a polar geometry from the object model
    /// </summary>
    /// <param name="polar">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine CreatePolar(PolarKinematics polar)
    {
        float maxRadius = polar.RadiusMax > 0.0f ? polar.RadiusMax : PolarKinematicsEngine.DefaultMaxRadius;
        float maxSpeed = polar.TTSpeedMax > 0.0f ? polar.TTSpeedMax : PolarKinematicsEngine.DefaultMaxTurntableSpeed;
        float maxAcceleration = polar.TTAccMax > 0.0f ? polar.TTAccMax : PolarKinematicsEngine.DefaultMaxTurntableAcceleration;

        return new PolarKinematicsEngine(polar.RadiusMin, maxRadius, polar.RadiusHomed, maxSpeed, maxAcceleration);
    }

    /// <summary>
    /// Build a hangprinter geometry from the object model
    /// </summary>
    /// <param name="hangprinter">The configured kinematics</param>
    /// <returns>The engine</returns>
    private static KinematicsEngine CreateHangprinter(HangprinterKinematics hangprinter)
    {
        if (hangprinter.Anchors.Count < 3)
        {
            return HangprinterKinematicsEngine.CreateDefault();
        }

        float[][] anchors = new float[hangprinter.Anchors.Count][];
        for (int i = 0; i < anchors.Length; i++)
        {
            anchors[i] = hangprinter.Anchors[i];
        }

        float printRadius = hangprinter.PrintRadius > 0.0f ? hangprinter.PrintRadius : HangprinterKinematicsEngine.DefaultPrintRadius;
        return new HangprinterKinematicsEngine(anchors, printRadius);
    }
}
