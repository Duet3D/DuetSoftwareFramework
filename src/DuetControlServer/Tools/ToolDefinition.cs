using System;
using System.Collections.Generic;

namespace DuetControlServer.Tools;

/// <summary>
/// Everything M563 can say about a tool
/// </summary>
/// <remarks>
/// A record rather than a long parameter list, because M563 carries nine independent things and the
/// order of nine <c>int</c>s and <c>List</c>s at a call site is not something a reader can check.
/// Defaults are RepRapFirmware's: X maps to X, Y to Y, Z to Z, and fan 0 to fan 0
/// </remarks>
public sealed record ToolDefinition
{
    /// <summary>Tool number, from P</summary>
    public required int Number { get; init; }

    /// <summary>Tool name, from S</summary>
    public string? Name { get; init; }

    /// <summary>Extruder drives, from D</summary>
    public IReadOnlyList<int> Extruders { get; init; } = [];

    /// <summary>Heaters, from H</summary>
    public IReadOnlyList<int> Heaters { get; init; } = [];

    /// <summary>Fans, from F</summary>
    public IReadOnlyList<int> Fans { get; init; } = [0];

    /// <summary>Axes the tool's X coordinate drives, from X</summary>
    public IReadOnlyList<int> XMap { get; init; } = [0];

    /// <summary>Axes the tool's Y coordinate drives, from Y</summary>
    public IReadOnlyList<int> YMap { get; init; } = [1];

    /// <summary>Axes the tool's Z coordinate drives, from Z</summary>
    public IReadOnlyList<int> ZMap { get; init; } = [2];

    /// <summary>
    /// Extruder whose filament is tracked, from L
    /// </summary>
    /// <remarks>
    /// RepRapFirmware defaults this to the tool's only drive when it has exactly one, and to none
    /// otherwise: with several drives there is no single filament to speak of
    /// </remarks>
    public int FilamentExtruder { get; init; } = -1;

    /// <summary>Spindle, from R, or -1 for none</summary>
    public int Spindle { get; init; } = -1;
}

/// <summary>
/// Which parts of a tool change to run
/// </summary>
/// <remarks>
/// RepRapFirmware's T parameter: <c>T1 P0</c> selects a tool without running any of the macros, which
/// is what a resurrect.g does when the tool is already in the head and only the state needs restoring
/// </remarks>
[Flags]
public enum ToolChangeParameters
{
    /// <summary>Change the selection and nothing else</summary>
    None = 0,

    /// <summary>Run <c>tfree&lt;n&gt;.g</c> for the tool being put down</summary>
    RunFree = 1,

    /// <summary>Run <c>tpre&lt;n&gt;.g</c> for the tool being picked up</summary>
    RunPre = 2,

    /// <summary>Run <c>tpost&lt;n&gt;.g</c> for the tool being picked up</summary>
    RunPost = 4,

    /// <summary>All three, which is what a bare T-code does</summary>
    All = RunFree | RunPre | RunPost
}
