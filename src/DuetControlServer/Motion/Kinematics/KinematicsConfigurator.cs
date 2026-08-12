using DuetAPI;
using DuetAPI.ObjectModel;
using Code = DuetAPI.Commands.Code;
using CodeParameter = DuetAPI.Commands.CodeParameter;

namespace DuetControlServer.Motion.Kinematics;

/// <summary>
/// What M665, M666 and M669 do: configure the geometry, then say so in the object model
/// </summary>
/// <remarks>
/// <para>
/// The geometry is what the machine moves by and the object model describes it - see §14 of
/// <c>docs/devel/MCODE_MIGRATION.md</c>. This is the one place the two are brought into step, so
/// there is one thing to get right rather than one per code.
/// </para>
/// <para>
/// The parts that belong to all geometries are here and the parts that belong to one are on the
/// engine: selecting a geometry by name, the segmentation parameters that every
/// <c>Kinematics::Configure</c> in RepRapFirmware takes, and creating the object model node that
/// carries the selected geometry's parameters
/// </para>
/// </remarks>
internal static class KinematicsConfigurator
{
    /// <summary>
    /// Apply a configuring M-code to a geometry
    /// </summary>
    /// <param name="current">The geometry as it is now</param>
    /// <param name="code">An M665, M666 or M669</param>
    /// <param name="seen">Set when the code carried a parameter that configured something</param>
    /// <returns>The geometry the code leaves behind, which is <paramref name="current"/> if nothing changed</returns>
    /// <exception cref="GCodeException">The code asked for something this geometry cannot do</exception>
    /// <remarks>
    /// Takes no locks and touches no object model. The caller applies the result and then projects it
    /// with <see cref="WriteTo"/>, which is what keeps the lock order in §14.4
    /// </remarks>
    public static KinematicsEngine Apply(KinematicsEngine current, Code code, ref bool seen)
    {
        KinematicsEngine engine = current;

        if (code.MajorNumber == 669 && code.TryGetInt('K', out int kinematicsType))
        {
            KinematicsName? name = NameFor(kinematicsType);
            if (name is null)
            {
                throw new GCodeException($"Unknown kinematics type {kinematicsType}");
            }

            if (name.Value != engine.Kind)
            {
                // A new geometry starts from its own defaults, including its segmentation, which is
                // what constructing a new Kinematics does in RepRapFirmware
                engine = KinematicsFactory.Create(name.Value);
            }
            seen = true;
        }
        else if (code.MajorNumber == 665 && engine is not LinearDeltaKinematicsEngine
                 && (code.HasParameter('L') || code.HasParameter('D')))
        {
            // M665 with a rod length switches the machine to a delta if it is not one already, which
            // is how a delta config.g is usually written
            engine = KinematicsFactory.Create(KinematicsName.LinearDelta);
            seen = true;
        }

        if (engine is HangprinterKinematicsEngine && HasConfiguringParameters(code))
        {
            throw new GCodeException("Hangprinter kinematics are not supported yet");
        }

        KinematicsEngine configured = engine.Configure(code, ref seen);
        if (!ReferenceEquals(configured, engine) && ReferenceEquals(engine, current))
        {
            // The geometry itself did not change, so what M669 S and T had already set carries over
            configured.InheritSegmentationFrom(current);
        }
        engine = configured;

        // S and T apply to every geometry: each of RepRapFirmware's Configure implementations calls
        // TryConfigureSegmentation, so they are handled once here rather than seven times
        if (code.MajorNumber == 669 && (code.HasParameter('S') || code.HasParameter('T')))
        {
            // Each parameter keeps its existing value when the code does not give it, which is what
            // RepRapFirmware's TryGetNonNegativeFValue does. Reading them straight into the variables
            // cannot express that: an out parameter is assigned whether or not the value was found,
            // so an absent T would zero the minimum segment length and turn segmentation off - on a
            // code that had just asked for it
            float segmentsPerSecond = engine.SegmentsPerSecond;
            float minSegmentLength = engine.MinSegmentLength;
            if (code.TryGetFloat('S', out float givenSegmentsPerSecond))
            {
                segmentsPerSecond = givenSegmentsPerSecond;
            }
            if (code.TryGetFloat('T', out float givenMinSegmentLength))
            {
                minSegmentLength = givenMinSegmentLength;
            }
            engine.ConfigureSegmentation(segmentsPerSecond, minSegmentLength);
            seen = true;
        }

        return engine;
    }

    /// <summary>
    /// Whether a code carries anything beyond the geometry selection
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>True if it is configuring rather than asking</returns>
    /// <remarks>
    /// A geometry that cannot be configured still has to answer <c>M669</c> on its own and to accept
    /// <c>M669 K</c> selecting something else, so only the parameters that would configure it are
    /// refused
    /// </remarks>
    private static bool HasConfiguringParameters(Code code)
    {
        foreach (CodeParameter parameter in code.Parameters)
        {
            if (parameter.Letter != 'K')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Write a geometry into the object model, replacing the node if the geometry changed
    /// </summary>
    /// <param name="engine">The geometry</param>
    /// <param name="move">The move subsystem of the object model</param>
    /// <remarks>
    /// <para>
    /// The caller holds the object model's write lock.
    /// </para>
    /// <para>
    /// The node is recreated rather than written over when the geometry changes, because the class
    /// that carries a delta's parameters is not the one that carries a SCARA's - and because
    /// <c>Kinematics.Name</c> is settable only from inside that hierarchy, so
    /// <c>Kinematics.Create</c> is the only way to name one
    /// </para>
    /// </remarks>
    public static void WriteTo(KinematicsEngine engine, Move move)
    {
        if (move.Kinematics.Name != engine.Kind)
        {
            move.Kinematics = DuetAPI.ObjectModel.Kinematics.Create(engine.Kind);
        }
        engine.WriteTo(move.Kinematics);
    }

    /// <summary>
    /// The geometry an M669 K number selects
    /// </summary>
    /// <param name="kinematicsType">The K parameter</param>
    /// <returns>The geometry, or null if the number names none</returns>
    /// <remarks>RepRapFirmware's <c>KinematicsType</c> enumeration, in its declaration order</remarks>
    public static KinematicsName? NameFor(int kinematicsType)
        => kinematicsType switch
        {
            0 => KinematicsName.Cartesian,
            1 => KinematicsName.CoreXY,
            2 => KinematicsName.CoreXZ,
            3 => KinematicsName.LinearDelta,
            4 => KinematicsName.Scara,
            5 => KinematicsName.CoreXYU,
            6 => KinematicsName.Hangprinter,
            7 => KinematicsName.Polar,
            8 => KinematicsName.CoreXYUV,
            9 => KinematicsName.FiveBarScara,
            10 => KinematicsName.RotaryDelta,
            11 => KinematicsName.MarkForged,
            _ => null
        };
}
