namespace DuetControlServer.Motion;

/// <summary>
/// What kind of move a G0 or G1 is, as chosen by its H parameter
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware keeps this as a bare integer and limits it to 0-4 when it reads the parameter -
/// <c>gb.TryGetLimitedUIValue('H', moveType, dummy, 5)</c> in <c>GCodes::DoStraightMove</c>. The
/// values are the same ones here; only the spelling is different.
/// </para>
/// <para>
/// Anything other than <see cref="Normal"/> is a special move: it bypasses the user coordinate
/// system, is planned against the motor or machine positions rather than the interpreter's own, and
/// the code waits for it to finish rather than queueing it and moving on
/// </para>
/// </remarks>
internal enum MoveType
{
    /// <summary>H0: an ordinary move, and what a G0 or G1 with no H is</summary>
    Normal = 0,

    /// <summary>
    /// H1: stop on the endstops and put each axis that stopped at the position of its switch
    /// </summary>
    /// <remarks>This is the move a homing macro is made of, and the only one that marks an axis homed</remarks>
    Homing = 1,

    /// <summary>H2: move the motors directly, ignoring the kinematics, the endstops and the axis limits</summary>
    RawMotor = 2,

    /// <summary>
    /// H3: stop on the endstops and record where each axis stopped as that end of its travel
    /// </summary>
    /// <remarks>
    /// Measures how long an axis turned out to be, which is what M208 would otherwise have to be told
    /// by hand. The axis is deliberately left unhomed - knowing where the end is is not the same as
    /// knowing where the head is
    /// </remarks>
    SenseLength = 3,

    /// <summary>H4: stop on the endstops and record nothing</summary>
    /// <remarks>
    /// What probing is built out of. The move stops in the same way <see cref="Homing"/> does, but
    /// neither the axis position nor the axis limit is set from it, because the probing sequence owns
    /// what comes of the move and needs several of them before it knows anything
    /// </remarks>
    Probing = 4
}

/// <summary>
/// Helpers for <see cref="MoveType"/>
/// </summary>
internal static class MoveTypeExtensions
{
    /// <summary>
    /// Whether a move of this kind watches the endstops, so it may stop short of where it was sent
    /// </summary>
    /// <param name="moveType">The kind of move</param>
    /// <returns>True if the move stops on an endstop</returns>
    /// <remarks>
    /// <see cref="MoveType.RawMotor"/> is deliberately excluded: it is the move that ignores the
    /// endstops, which is what makes it the one to use when an axis has to be moved off a switch that
    /// is already closed
    /// </remarks>
    public static bool ChecksEndstops(this MoveType moveType)
        => moveType is MoveType.Homing or MoveType.SenseLength or MoveType.Probing;
}
