using System.Collections.Generic;
using System.Linq;

namespace SystemTests.Host.Config;

/// <summary>
/// The rig the regression suite's driver cases are recorded on, so a scenario can be read side by
/// side with the testcase it pins
/// </summary>
/// <remarks>
/// <para>
/// <c>lib/DuetRegressionTesting/testcases/drivers</c> declares two boards - <c>b0</c>, a six-driver
/// MB6HC at CAN address 1, and <c>b1</c>, a single-driver EXP1HCL at address 2 - and nearly every
/// case in the folder maps X, Y and Z onto b0 and the extruder onto b1 before doing anything else.
/// Configuring that here is what lets a scenario send the case's G-code verbatim, so the reply,
/// the CAN traffic and the object model delta it asserts are the ones the reference holds.
/// </para>
/// <para>
/// What the boards say back is the scenario's to script. The bench answers every request with an
/// empty <c>StandardReply</c>, which is a board that took the setting and had nothing to add; a case
/// whose G-code asks a board to <em>report</em> is asserting the board's own text, so it scripts
/// that text with <see cref="ScriptedCanMaster.ReportWith{TMessage}"/>
/// and the reply travelling back intact is what the scenario checks
/// </para>
/// </remarks>
internal static class DriversRig
{
    /// <summary>CAN address of the six-driver board the regression cases call <c>b0</c></summary>
    public const byte MainDriverBoard = 1;

    /// <summary>CAN address of the single-driver closed loop board the regression cases call <c>b1</c></summary>
    public const byte ClosedLoopBoard = 2;

    /// <summary>
    /// How many drivers each board of the rig carries, which is what makes a driver number one past
    /// the end of a board refusable
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, byte> BoardDrivers =
        new Dictionary<byte, byte> { [MainDriverBoard] = 6, [ClosedLoopBoard] = 1 };

    /// <summary>A driver of <see cref="MainDriverBoard"/> as config.g names it, e.g. 1.3</summary>
    public static string MainDriver(int driver) => Expansion.Driver(MainDriverBoard, driver);

    /// <summary>A driver of <see cref="ClosedLoopBoard"/> as config.g names it, e.g. 2.0</summary>
    public static string ClosedLoopDriver(int driver) => Expansion.Driver(ClosedLoopBoard, driver);

    /// <summary>Every driver of both boards, and X, Y and Z on the first three of b0 with the extruder on b1, which every case in the folder needs</summary>
    private static readonly string Mapping =
        string.Join("\n", BoardDrivers.SelectMany(board => Enumerable.Range(0, board.Value)
                                                                     .Select(driver => $"M569 P{Expansion.Driver(board.Key, driver)} S1")))
        + $"\nM584 X{MainDriver(0)} Y{MainDriver(1)} Z{MainDriver(2)} E{ClosedLoopDriver(0)}";

    /// <summary>
    /// The per-drive settings the regression suite's preamble establishes: 16x interpolated
    /// microstepping, 80/80/400 steps per mm with the extruder at 420, low motor currents with the
    /// extruder at 800, and the firmware's default current percentages. A case that does not set one
    /// of them and reports it is reporting the number its reference quotes
    /// </summary>
    private const string DriveSettings = """
        M350 X16 Y16 Z16 E16 I1
        M92 X80 Y80 Z400 E420
        M906 X400 Y400 Z400 E800 I30
        M913 X100 Y100 Z100 E100
        M917 X71 Y71 Z71 E71
        """;

    /// <summary>
    /// What the machine is allowed to do, so that a scenario which moves can. None of it is what any
    /// driver case is about
    /// </summary>
    private const string MotionLimits = """
        M201 X500 Y500 Z100 E250
        M203 X6000 Y6000 Z600 E3600
        M566 X900 Y900 Z100 E120
        M208 X0:200 Y0:200 Z0:200
        M302 P1
        M564 H0 S0
        """;

    /// <summary>The rig as the regression suite's preamble leaves it: the drivers, the mapping, then the per-drive settings</summary>
    public static readonly ConfigPart Config = Mapping + "\n" + DriveSettings + "\n" + MotionLimits;

    /// <summary>
    /// The rig with its drives mapped and their settings left alone, for a scenario about what a drive
    /// starts life with rather than about what the preamble set it to
    /// </summary>
    public static readonly ConfigPart WithoutDriveSettings = Mapping + "\n" + MotionLimits;
}
