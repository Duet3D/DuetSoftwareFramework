using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;

namespace UnitTests.Motion;

/// <summary>
/// The machine a test's geometry is built for, the way config.g builds it
/// </summary>
/// <remarks>
/// <para>
/// A geometry finds its axes by letter and knows none until M584 has created them, so a test names the
/// letters of its machine's axes, in the order M584 created them.
/// </para>
/// <para>
/// A geometry with its defaults is what M669 K selects: <see cref="KinematicsFactory.Create(KinematicsName, AxisIndices)"/>
/// for the axes <see cref="From"/> gives. One with parameters of its own is built with the constructor
/// the factory builds it with from the object model, and given its axes with <see cref="WithAxes{T}"/>
/// </para>
/// <para>
/// A machine described as a whole object model is turned into what the planner works from with
/// <see cref="Snapshot"/>, and what a geometry reports back to the object model is read with
/// <see cref="Reported{T}"/>
/// </para>
/// </remarks>
internal static class KinematicsAxes
{
    /// <summary>
    /// The axes of a machine whose config.g created them in the usual order
    /// </summary>
    public const string Canonical = "XYZUVWABCD";

    /// <summary>
    /// A move model with the given axes, as M584 would have created them
    /// </summary>
    /// <param name="letters">The letter of each axis, in axis order</param>
    /// <returns>The move model</returns>
    public static Move NewMoveWithAxes(string letters = Canonical)
    {
        Move move = new();
        foreach (char letter in letters)
        {
            move.Axes.Add(new Axis { Letter = letter, Visible = true });
        }
        return move;
    }

    /// <summary>
    /// The axes of a machine whose config.g created them with the given letters, in that order
    /// </summary>
    /// <param name="letters">The letter of each axis, in axis order</param>
    /// <returns>The axes, resolved from the move model as the planner resolves them</returns>
    public static AxisIndices From(string letters = Canonical) => AxisIndices.From(NewMoveWithAxes(letters));

    /// <summary>
    /// Give a geometry the machine's axes
    /// </summary>
    /// <typeparam name="T">The geometry's type</typeparam>
    /// <param name="engine">The geometry</param>
    /// <param name="letters">The letter of each axis, in axis order</param>
    /// <returns>The same geometry</returns>
    public static T WithAxes<T>(this T engine, string letters = Canonical) where T : KinematicsEngine
    {
        engine.SetAxes(From(letters));
        return engine;
    }

    /// <summary>
    /// Snapshot a machine described by its object model, building its geometry from the object model
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <returns>The snapshot</returns>
    /// <remarks>
    /// The planner owns its geometry rather than deriving it (MCODE_MIGRATION §14), so the snapshot is
    /// handed one. A test that describes a machine as an object model wants the geometry it describes,
    /// which is what <see cref="KinematicsFactory.Create(DuetAPI.ObjectModel.Kinematics, AxisIndices)"/> is for
    /// </remarks>
    public static MotionParameters Snapshot(Move move)
    {
        KinematicsEngine geometry = KinematicsFactory.Create(move.Kinematics, AxisIndices.From(move));
        MotionParameters.ApplyAxes(move, geometry);
        return MotionParameters.FromObjectModel(move, geometry);
    }

    /// <summary>
    /// What a geometry reports in the object model
    /// </summary>
    /// <typeparam name="T">The node type its kind has</typeparam>
    /// <param name="engine">The geometry</param>
    /// <returns>A fresh node the geometry has written itself into</returns>
    public static T Reported<T>(KinematicsEngine engine) where T : DuetAPI.ObjectModel.Kinematics
    {
        DuetAPI.ObjectModel.Kinematics kinematics = DuetAPI.ObjectModel.Kinematics.Create(engine.Kind);
        engine.WriteTo(kinematics);
        return (T)kinematics;
    }
}
