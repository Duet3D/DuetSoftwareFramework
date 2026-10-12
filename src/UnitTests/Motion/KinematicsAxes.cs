using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;

namespace UnitTests.Motion;

/// <summary>
/// Giving a geometry the axes a test's machine has
/// </summary>
/// <remarks>
/// A geometry finds X, Y and Z by letter and knows no axes until it is told them, as it does not
/// before M584 has run. A test that drives a geometry directly tells it the axes first
/// </remarks>
internal static class KinematicsAxes
{
    /// <summary>
    /// The axes of a machine whose config.g created them in the usual order
    /// </summary>
    public const string Canonical = "XYZUVWABCD";

    /// <summary>
    /// Give a geometry the machine's axes
    /// </summary>
    /// <typeparam name="T">The geometry's type</typeparam>
    /// <param name="engine">The geometry</param>
    /// <param name="letters">The letter of each axis, in axis order</param>
    /// <returns>The same geometry</returns>
    public static T WithAxes<T>(this T engine, string letters = Canonical) where T : KinematicsEngine
    {
        engine.SetAxes(AxisIndices.From(letters));
        return engine;
    }

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
    /// Give a geometry the axes a move model has, as M584 would have
    /// </summary>
    /// <typeparam name="T">The geometry's type</typeparam>
    /// <param name="engine">The geometry</param>
    /// <param name="move">The move model whose axes the machine has</param>
    /// <returns>The same geometry</returns>
    public static T WithAxes<T>(this T engine, Move move) where T : KinematicsEngine
    {
        engine.SetAxes(AxisIndices.From(move));
        return engine;
    }
}
