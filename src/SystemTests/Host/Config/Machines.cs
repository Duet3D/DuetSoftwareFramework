using static System.FormattableString;

namespace SystemTests.Host.Config;

/// <summary>A machine's axes and drives, with the G92 that marks every one of its axes homed</summary>
internal sealed class Machine(string lines, string axesAtOrigin) : ConfigPart(lines)
{
    /// <summary>
    /// Every axis of this machine told it is at 0, which also marks it homed. It comes last because
    /// configuring the geometry un-homes every axis
    /// </summary>
    public ConfigPart Homed { get; } = new($"G92 {axesAtOrigin}", ConfigOrder.Last);
}

/// <summary>
/// The machines a scenario can configure. Every drive is on <see cref="DriverBoard"/>, since board 0
/// runs DuetCANMaster and has none, each axis letter is on the same driver in every machine, and every
/// machine may move without homing
/// </summary>
internal static class Machines
{
    /// <summary>CAN address of the expansion board carrying the drivers, endstops and probes</summary>
    public const byte DriverBoard = 1;

    /// <summary>The driver of <see cref="DriverBoard"/> each drive is on, whichever of them a machine has</summary>
    public const byte XDriver = 0, YDriver = 1, ZDriver = 2, EDriver = 3;

    /// <summary>Steps per mm of the drives (M92)</summary>
    public const float XyStepsPerMm = 80.0f, ZStepsPerMm = 400.0f, EStepsPerMm = 420.0f;

    /// <summary>Acceleration limits, mm/s² (M201)</summary>
    public const float XyAcceleration = 500.0f, ZAcceleration = 100.0f, EAcceleration = 250.0f;

    /// <summary>Speed limits, mm/min (M203)</summary>
    public const float XySpeed = 6000.0f, ZSpeed = 600.0f, ESpeed = 3600.0f;

    /// <summary>Maximum instantaneous speed changes, mm/s (M566 takes mm/min)</summary>
    public const float XyJerk = 900.0f / 60.0f, ZJerk = 60.0f / 60.0f, EJerk = 120.0f / 60.0f;

    /// <summary>Motor current of every drive, mA (M906)</summary>
    public const int MotorCurrent = 800;

    /// <summary>The upper axis limits, mm (M208); every axis starts at 0</summary>
    public const float XyMax = 200.0f, ZMax = 150.0f;

    /// <summary>A driver of <see cref="DriverBoard"/> as config.g names it</summary>
    private static string On(byte driver) => Expansion.Driver(DriverBoard, driver);

    /// <summary>X alone</summary>
    public static readonly Machine X = new(Invariant($"""
        M569 P{On(XDriver)} S1
        M584 X{On(XDriver)}
        M92 X{XyStepsPerMm}
        M906 X{MotorCurrent}
        M201 X{XyAcceleration}
        M203 X{XySpeed}
        M566 X{XyJerk * 60}
        M208 X0:{XyMax}
        M564 H0 S0
        """), "X0");

    /// <summary>X and Y plus one extruder, with cold extrusion allowed so that a move may extrude with no heater configured</summary>
    public static readonly Machine Xye = new(Invariant($"""
        M569 P{On(XDriver)} S1
        M569 P{On(YDriver)} S1
        M569 P{On(EDriver)} S1
        M584 X{On(XDriver)} Y{On(YDriver)} E{On(EDriver)}
        M92 X{XyStepsPerMm} Y{XyStepsPerMm} E{EStepsPerMm}
        M906 X{MotorCurrent} Y{MotorCurrent} E{MotorCurrent}
        M201 X{XyAcceleration} Y{XyAcceleration} E{EAcceleration}
        M203 X{XySpeed} Y{XySpeed} E{ESpeed}
        M566 X{XyJerk * 60} Y{XyJerk * 60} E{EJerk * 60}
        M208 X0:{XyMax} Y0:{XyMax}
        M302 P1
        M564 H0 S0
        """), "X0 Y0");

    /// <summary>X, Y and Z</summary>
    public static readonly Machine Xyz = new(Invariant($"""
        M569 P{On(XDriver)} S1
        M569 P{On(YDriver)} S1
        M569 P{On(ZDriver)} S1
        M584 X{On(XDriver)} Y{On(YDriver)} Z{On(ZDriver)}
        M92 X{XyStepsPerMm} Y{XyStepsPerMm} Z{ZStepsPerMm}
        M906 X{MotorCurrent} Y{MotorCurrent} Z{MotorCurrent}
        M201 X{XyAcceleration} Y{XyAcceleration} Z{ZAcceleration}
        M203 X{XySpeed} Y{XySpeed} Z{ZSpeed}
        M566 X{XyJerk * 60} Y{XyJerk * 60} Z{ZJerk * 60}
        M208 X0:{XyMax} Y0:{XyMax} Z0:{ZMax}
        M564 H0 S0
        """), "X0 Y0 Z0");

    /// <summary>X, Y, Z and one extruder, with cold extrusion allowed so that a move may extrude with no heater configured</summary>
    public static readonly Machine Xyze = new(Invariant($"""
        M569 P{On(XDriver)} S1
        M569 P{On(YDriver)} S1
        M569 P{On(ZDriver)} S1
        M569 P{On(EDriver)} S1
        M584 X{On(XDriver)} Y{On(YDriver)} Z{On(ZDriver)} E{On(EDriver)}
        M92 X{XyStepsPerMm} Y{XyStepsPerMm} Z{ZStepsPerMm} E{EStepsPerMm}
        M906 X{MotorCurrent} Y{MotorCurrent} Z{MotorCurrent} E{MotorCurrent}
        M201 X{XyAcceleration} Y{XyAcceleration} Z{ZAcceleration} E{EAcceleration}
        M203 X{XySpeed} Y{XySpeed} Z{ZSpeed} E{ESpeed}
        M566 X{XyJerk * 60} Y{XyJerk * 60} Z{ZJerk * 60} E{EJerk * 60}
        M208 X0:{XyMax} Y0:{XyMax} Z0:{ZMax}
        M302 P1
        M564 H0 S0
        """), "X0 Y0 Z0");

    /// <summary>Segments per second <see cref="Segmented"/> asks for (M669 S)</summary>
    public const float SegmentsPerSecond = 100.0f;

    /// <summary>Shortest segment <see cref="Segmented"/> allows, in mm (M669 T)</summary>
    public const float MinSegmentLength = 0.2f;

    /// <summary>
    /// M669 turning segmentation on. A stop is planned at a move boundary, and with segmentation off a
    /// whole G-code is one move. It comes after the geometry, because selecting one with M669 K turns
    /// segmentation off
    /// </summary>
    public static readonly ConfigPart Segmented = new(Invariant($"M669 S{SegmentsPerSecond} T{MinSegmentLength}"), ConfigOrder.AfterGeometry);

    /// <summary>
    /// Tool 0 driving extruder 0, selected. Extruding with no tool selected is an error rather than a
    /// move
    /// </summary>
    public static readonly ConfigPart OneTool = "M563 P0 D0\nT0";
}
