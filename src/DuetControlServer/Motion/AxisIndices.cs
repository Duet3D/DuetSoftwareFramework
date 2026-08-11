namespace DuetControlServer.Motion;

/// <summary>
/// Where X, Y and Z sit in a coordinate vector
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>X_AXIS</c>, <c>Y_AXIS</c> and <c>Z_AXIS</c>. These are <em>positions</em>
/// rather than letters, and that is the whole point of them: a geometry reasons about the first two
/// as the pair its motors couple and the third as the one that lifts, whatever M584 called them, and
/// a tool's axis maps and the skew correction are indexed the same way. So they are not something to
/// ask the geometry or the object model for.
/// </para>
/// <para>
/// Imported with <c>using static</c> where they are needed, so that the code reads as
/// <c>coords[XAxis]</c> as it does in RepRapFirmware. Declared once: the kinematics, the move
/// builder and the G-code handler all index the same vector, and separate declarations would be
/// separate chances to give one of them a different value
/// </para>
/// </remarks>
public static class AxisIndices
{
    /// <summary>Position of the X axis</summary>
    public const int XAxis = 0;

    /// <summary>Position of the Y axis</summary>
    public const int YAxis = 1;

    /// <summary>Position of the Z axis</summary>
    public const int ZAxis = 2;
}
