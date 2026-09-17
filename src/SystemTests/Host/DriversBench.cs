using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SystemTests.Host;

/// <summary>
/// The machine the driver configuration scenarios run against: the same rig the regression suite
/// records its references on, so a scenario can be read side by side with the testcase it pins
/// </summary>
/// <remarks>
/// <para>
/// <c>lib/DuetRegressionTesting/testcases/drivers</c> declares two boards - <c>b0</c>, a six-driver
/// MB6HC at CAN address 1, and <c>b1</c>, a single-driver EXP1HCL at address 2 - and nearly every
/// case in the folder maps X, Y and Z onto b0 and the extruder onto b1 before doing anything else.
/// Configuring that once here is what lets a scenario send the case's G-code verbatim, so the reply,
/// the CAN traffic and the object model delta it asserts are the ones the reference holds.
/// </para>
/// <para>
/// What the boards say back is the scenario's to script. The fake controller answers every request
/// with an empty <c>StandardReply</c>, which is a board that took the setting and had nothing to add;
/// a case whose G-code asks a board to <em>report</em> is asserting the board's own text, so it
/// scripts that text with <see cref="ScriptedCanMaster.ReportWith{TMessage}"/> and the reply
/// travelling back intact is what the scenario checks
/// </para>
/// </remarks>
internal static class DriversBench
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

    /// <summary>
    /// The rig's configuration, in the order the regression suite's preamble establishes it: the
    /// drivers, the mapping, then the per-drive settings every case inherits
    /// </summary>
    /// <remarks>
    /// The values are the ones the references were recorded against - 16x interpolated microstepping,
    /// 80/80/400 steps per mm with the extruder at 420, low motor currents with the extruder at 800,
    /// and the firmware's default current percentages - so a case that does not set one of them and
    /// reports it is reporting the number its reference quotes
    /// </remarks>
    public const string RigConfig = RigMapping + "\n" + RigDriveSettings + "\n" + RigMotionLimits;

    /// <summary>
    /// The drivers and the mapping, which every case in the folder needs. M953 comes first: with the
    /// bus disabled the configuration's own CAN messages would be answered with BusError
    /// </summary>
    private const string RigMapping = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.2 S1
        M569 P1.3 S1
        M569 P1.4 S1
        M569 P1.5 S1
        M569 P2.0 S1
        M584 X1.0 Y1.1 Z1.2 E2.0
        """;

    /// <summary>
    /// The per-drive settings the regression suite's preamble establishes, which a case that does not
    /// set one of them and reports it is reporting
    /// </summary>
    private const string RigDriveSettings = """
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
    private const string RigMotionLimits = """
        M201 X500 Y500 Z100 E250
        M203 X6000 Y6000 Z600 E3600
        M566 X900 Y900 Z100 E120
        M208 X0:200 Y0:200 Z0:200
        M302 P1
        M564 H0 S0
        """;

    /// <summary>
    /// Start the rig, optionally with extra configuration and with the boards scripted to report
    /// </summary>
    /// <param name="configExtra">Extra configuration lines, run after <see cref="RigConfig"/></param>
    /// <param name="prepareController">Scripts what the boards say back</param>
    /// <param name="withDriveSettings">
    /// False to map the drives and leave their settings alone, for a scenario about what a drive
    /// starts life with rather than about what the preamble set it to
    /// </param>
    public static Task<JobBench> StartAsync(string configExtra = "",
                                            Action<ScriptedCanMaster>? prepareController = null,
                                            bool withDriveSettings = true)
        => JobControlBench.StartAsync(configExtra,
                                      machineConfig: withDriveSettings ? RigConfig : RigMapping + "\n" + RigMotionLimits,
                                      prepareController: prepareController);
}
