namespace DuetControlServer.Model;

/// <summary>
/// The limits this build is put together with that no subsystem of its own enforces
/// </summary>
/// <remarks>
/// <para>
/// Most of what <c>limits</c> publishes belongs somewhere else: a limit shared with the expansion
/// boards is in the CAN message schema, because a bitmap on the bus is what bounds it and both sides
/// have to agree on the width, and the rest sit with the motion engine, the heat subsystem or the
/// tools, next to the code that refuses a number outside them.
/// </para>
/// <para>
/// These four have no such home. Two of them bound features that are not ported yet, so the constant
/// moves to whatever ends up enforcing it when they are - see <c>docs/devel/MCODE_MIGRATION.md</c>
/// for M486 and <c>docs/devel/EVENTS_MIGRATION.md</c> for the triggers
/// </para>
/// </remarks>
internal static class MachineLimits
{
    /// <summary>Most axes reported when a client asks for the move key</summary>
    /// <remarks>
    /// RepRapFirmware's <c>MaxReportedAxes</c>. Past this a client has to ask for move.axes
    /// explicitly to see them all
    /// </remarks>
    public const int MaxReportedAxes = 5;

    /// <summary>Most build plate objects a job may be split into</summary>
    /// <remarks>RepRapFirmware's <c>MaxTrackedObjects</c> for a SAME70 or SAME5x</remarks>
    public const int MaxTrackedObjects = 64;

    /// <summary>Most triggers a machine may have</summary>
    /// <remarks>
    /// RepRapFirmware's <c>MaxTriggers</c> for a Duet 3 MB6HC, which must stay at or below 32 because
    /// the pending triggers are held as a 32-bit bitmap
    /// </remarks>
    public const int MaxTriggers = 32;

    /// <summary>Coordinate systems G54 to G59.3 name</summary>
    /// <remarks>RepRapFirmware's <c>NumCoordinateSystems</c></remarks>
    public const int NumWorkplaces = 9;
}
