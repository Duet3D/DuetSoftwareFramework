using System;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Motion;

/// <summary>
/// Which axis carries each letter, and in particular where X, Y and Z are
/// </summary>
/// <remarks>
/// <para>
/// DSF has no default axes. Every axis is created by M584, in whatever order config.g names them, so
/// X, Y and Z can sit at any index and any of them may not exist at all. RepRapFirmware's
/// <c>X_AXIS</c>, <c>Y_AXIS</c> and <c>Z_AXIS</c> are fixed positions because its first three axes are
/// always X, Y and Z; here the axis that means X is the axis lettered X, wherever it is, and every
/// part of the motion system that needs one of the three finds it by letter through this class.
/// </para>
/// <para>
/// An instance is a resolved, immutable view of <c>move.axes[]</c>, which stays the one source of the
/// letters. The geometry holds one (<see cref="Kinematics.KinematicsEngine.Axes"/>), replaced whenever
/// the axes change, because it is read without the object model lock and derives state of its own
/// from where the axes are. Everything else holds the object model lock while it works, so it asks
/// <see cref="IndexOf(Move, char)"/> of <c>move.axes[]</c> directly
/// </para>
/// </remarks>
internal sealed class AxisIndices
{
    /// <summary>
    /// A machine with no axes, which is what there is before M584 has created any
    /// </summary>
    public static readonly AxisIndices None = new(string.Empty);

    /// <summary>
    /// The letter of each axis, in axis order
    /// </summary>
    public string Letters { get; }

    /// <summary>
    /// Index of the axis lettered X, or -1 if there is none
    /// </summary>
    public int X { get; }

    /// <summary>
    /// Index of the axis lettered Y, or -1 if there is none
    /// </summary>
    public int Y { get; }

    /// <summary>
    /// Index of the axis lettered Z, or -1 if there is none
    /// </summary>
    public int Z { get; }

    /// <summary>
    /// Whichever of X and Y exist, as a bitmap
    /// </summary>
    public uint XyBitmap { get; }

    /// <summary>
    /// Whichever of X, Y and Z exist, as a bitmap
    /// </summary>
    public uint XyzBitmap { get; }

    /// <summary>
    /// Number of axes
    /// </summary>
    public int Count => Letters.Length;

    private AxisIndices(string letters)
    {
        Letters = letters;
        X = IndexOf(letters, 'X');
        Y = IndexOf(letters, 'Y');
        Z = IndexOf(letters, 'Z');
        XyBitmap = Bit(X) | Bit(Y);
        XyzBitmap = XyBitmap | Bit(Z);
    }

    /// <summary>
    /// Resolve the axes the object model describes
    /// </summary>
    /// <param name="move">The move model</param>
    /// <returns>The resolved axes</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    public static AxisIndices From(Move move)
    {
        if (move.Axes.Count == 0)
        {
            return None;
        }

        char[] letters = new char[move.Axes.Count];
        for (int axis = 0; axis < letters.Length; axis++)
        {
            letters[axis] = move.Axes[axis].Letter;
        }
        return new AxisIndices(new string(letters));
    }

    /// <summary>
    /// Resolve a list of axis letters
    /// </summary>
    /// <param name="letters">The letter of each axis, in axis order</param>
    /// <returns>The resolved axes</returns>
    public static AxisIndices From(ReadOnlySpan<char> letters) => letters.IsEmpty ? None : new AxisIndices(new string(letters));

    /// <summary>
    /// Which axis carries a letter
    /// </summary>
    /// <param name="letter">The axis letter</param>
    /// <returns>The axis index, or -1 if no axis carries it</returns>
    public int IndexOf(char letter) => IndexOf(Letters, letter);

    /// <summary>
    /// Which axis of the object model carries a letter
    /// </summary>
    /// <param name="move">The move model</param>
    /// <param name="letter">The axis letter</param>
    /// <returns>The axis index, or -1 if the machine has no such axis</returns>
    /// <remarks>
    /// For code that looks an axis up once rather than per move. The caller must hold the object model
    /// lock
    /// </remarks>
    public static int IndexOf(Move move, char letter)
    {
        for (int axis = 0; axis < move.Axes.Count; axis++)
        {
            if (move.Axes[axis].Letter == letter)
            {
                return axis;
            }
        }
        return -1;
    }

    /// <summary>
    /// Whether this describes the same axes as another
    /// </summary>
    /// <param name="other">The other axes</param>
    /// <returns>True if the letters and their order agree</returns>
    public bool SameAs(AxisIndices other) => string.Equals(Letters, other.Letters, StringComparison.Ordinal);

    /// <summary>
    /// The bitmap bit of an axis, or nothing for an axis that does not exist
    /// </summary>
    /// <param name="axis">The axis index, or -1</param>
    /// <returns>The bit, zero for -1</returns>
    public static uint Bit(int axis) => axis is >= 0 and < 32 ? 1u << axis : 0u;

    private static int IndexOf(ReadOnlySpan<char> letters, char letter) => letters.IndexOf(letter);
}
