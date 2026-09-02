using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Files.Parser;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Model;
using DuetControlServer.Motion;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Class that processes M-codes in the control server
/// </summary>
/// <param name="codeProcessor">Code processor</param>
/// <param name="commandFactory">Command factory</param>
/// <param name="eventLogger">Event logger</param>
/// <param name="fileInfoParser">File info parser</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="diagnosticsProvider">Diagnostics provider</param>
/// <param name="jobController">Job controller</param>
/// <param name="jobMonitor">How the job is getting on, which M73 tells what the slicer expects</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="expansionBoardManager">What the expansion boards have reported about themselves</param>
/// <param name="mqtt">MQTT provider</param>
/// <param name="sbcTriggerService">SBC trigger service</param>
/// <param name="logger">Logger</param>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="macroRunner">Runs macro files</param>
/// <param name="bedCompensation">The height map in effect</param>
/// <param name="stateStack">Interpreter state saved by M120 and restored by M121</param>
/// <param name="planner">Where G-codes become queued moves, and what holds the machine description</param>
/// <param name="fanManager">The fans the machine has, and what they are asked to run at</param>
/// <param name="gpioManager">The general-purpose outputs, which spindles and servos are built on</param>
/// <param name="spindleManager">The spindles the machine has</param>
/// <param name="heatManager">The heaters the machine has, and what they are asked to reach</param>
/// <param name="toolManager">The tools the machine has, and which one is selected</param>
/// <param name="settings">Settings</param>
internal partial class MCodeHandler(
    CodeProcessor codeProcessor,
    CommandFactory commandFactory,
    DiagnosticsProvider diagnosticsProvider,
    EventLogger eventLogger,
    Events.EventQueue events,
    FileInfoParser fileInfoParser,
    FilePathResolver filePathResolver,
    LinkInterface linkInterface,
    Link.Expansion.ExpansionBoardManager expansionBoardManager,
    Model.ObjectModel model,
    MQTT mqtt,
    SbcTriggerService sbcTriggerService,
    Files.Job.JobController jobController,
    JobMonitor jobMonitor,
    ILogger<MCodeHandler> logger,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime,
    MacroRunner macroRunner,
    Motion.BedCompensation bedCompensation,
    InterpreterStateStack stateStack,
    MovePlanner planner,
    Fans.FanManager fanManager,
    Ports.GpioManager gpioManager,
    Spindles.SpindleManager spindleManager,
    Heat.HeatManager heatManager,
    Tools.ToolManager toolManager,
    IOptions<Settings> settings) : ICodeHandler
{
    private MessageLoggerProvider? _messageLoggerProvider;

    /// <summary>
    /// Process an M-code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    /// <remarks>
    /// <para>
    /// Every code gets one method, and each returns null to mean "not finished here". Several codes
    /// have an SBC half and a machine half - M23 selects the file but leaves the rest to the firmware,
    /// M550 checks the hostname and then passes the code on - and null is how the second half is asked
    /// for. It is not the same as an empty message, which means the code is done.
    /// </para>
    /// <para>
    /// The machine configuration and motion codes are implemented in MCodeHandler.Motion.cs. They are
    /// dispatched from the same table as everything else; only their bodies live elsewhere
    /// </para>
    /// </remarks>
    public async ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.IsFromFileChannel && jobController.State.IsSimulating && code.MajorNumber is not 0 and not 1 and not 2)
        {
            // Ignore most M-codes from files in simulation mode...
            return new Message();
        }

        return await Rows.Invoke(this, code, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A code the simulation gate in <see cref="ProcessAsync"/> is going to ignore needs no
    /// synchronisation either, so it classifies Immediate; whether it exists at all still comes
    /// from the table
    /// </remarks>
    public CodeClass? Classify(DuetAPI.Commands.Code code)
    {
        CodeClass? codeClass = Rows.Classify(code);
        if (codeClass is not null && code.IsFromFileChannel && jobController.State.IsSimulating &&
            code.MajorNumber is not 0 and not 1 and not 2)
        {
            return CodeClass.Immediate;
        }
        return codeClass;
    }

    /// <summary>
    /// Whether a drive-configuration code names a drive to change rather than only asking for a
    /// report, which decides between a standstill and acting immediately
    /// </summary>
    private static CodeClass FlushAndStandstillWhenSettingDrives(DuetAPI.Commands.Code code)
        => SetsAnyDrive(code) ? CodeClass.FlushAndStandstill : CodeClass.Immediate;

    /// <summary>
    /// Every M-code this handler implements: its class, enforced by the pipeline before dispatch,
    /// and its handler. A fractional code is its own row; an M-code with no row takes the
    /// macro-then-unsupported path, which is why M22 (release SD card) and M998 (resend request)
    /// are absent
    /// </summary>
    internal static readonly CodeTable<MCodeHandler> Rows = new(CodeType.MCode)
    {
        // Keep numerically ordered (where possible) for easier maintenance.
        // Stop or unconditional stop, sleep or conditional stop, program end
        { [0, 1, 2], CodeClass.Immediate, (h, c, ct) => h.HandleStopAsync(c, ct) }, // TODO synchronous flush
        // Spindle clockwise / laser power
        { 3, CodeClass.Deferred, (h, c, ct) => h.HandleSpindleOnAsync(c, reverse: false, ct) },
        // Spindle counter-clockwise
        { 4, CodeClass.Deferred, (h, c, ct) => h.HandleSpindleOnAsync(c, reverse: true, ct) },
        // Spindle off
        { 5, CodeClass.Deferred, (h, c, ct) => h.HandleSpindleOffAsync(c, ct) },
        // Motors on / motors off
        { [17, 18, 84], CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleDriverStateAsync(c, ct) },
        // List SD card
        { 20, CodeClass.Immediate, (h, c, ct) => h.HandleListFilesAsync(c, ct) },
        // Initialize SD card
        { 21, CodeClass.Immediate, (h, c, ct) => h.HandleInitializeSDCardAsync(c, ct) },
        // Select a file to print, or select it and start printing
        { [23, 32], CodeClass.Immediate, (h, c, ct) => h.HandleSelectFileAsync(c, ct) }, // the handler flushes inline before swapping the job file
        // Resume a file print
        { 24, CodeClass.Immediate, (h, c, ct) => h.HandleResumePrintAsync(c, ct) }, // resume sequences its macros itself; the handler flushes inline first
        // Pause the print
        { 25, CodeClass.Immediate, (h, c, ct) => h.HandlePausePrintAsync(c, ct) }, // a pause must not queue behind the codes it is meant to interrupt. TODO synchronous flush
        // Set SD position
        { 26, CodeClass.Flush, (h, c, ct) => h.HandleSetFilePositionAsync(c, ct) }, // the file position it overwrites settles when the pending codes finish
        // Report SD print status
        { 27, CodeClass.Flush, (h, c, ct) => h.HandleReportPrintStatusAsync(c, ct) }, // reports the file position, which pending codes are still advancing
        // Begin write to SD card
        { 28, CodeClass.Flush, (h, c, ct) => h.HandleBeginFileWriteAsync(c, ct) }, // codes already in flight must finish before capture starts, or be swallowed
        // End write to SD card
        { 29, CodeClass.Flush, (h, c, ct) => h.HandleEndFileWriteAsync(c, ct) }, // pending captured writes must reach the file before it closes
        // Delete a file on the SD card
        { 30, CodeClass.Flush, (h, c, ct) => h.HandleDeleteFileAsync(c, ct) }, // a file a pending code is still writing must not vanish under it
        // Return file information; M36.1 reads a thumbnail fragment, M36.2 a plain file fragment
        { 36, CodeClass.Flush, (h, c, ct) => h.HandleFileInfoAsync(c, thumbnail: null, ct) }, // a pending capture may still be writing the file it inspects
        { (36, 1), CodeClass.Flush, (h, c, ct) => h.HandleFileInfoAsync(c, thumbnail: true, ct) }, // the thumbnail source must be complete
        { (36, 2), CodeClass.Flush, (h, c, ct) => h.HandleFileInfoAsync(c, thumbnail: false, ct) }, // the fragment source must be complete
        // Simulate file
        { 37, CodeClass.Immediate, (h, c, ct) => h.HandleSimulateFileAsync(c, ct) }, // the handler flushes inline with file-stream sync, which no class expresses
        // Compute CRC32 checksum of target file
        { 38, CodeClass.Flush, (h, c, ct) => h.HandleFileChecksumAsync(c, ct) }, // checksums a file a pending capture may still be writing
        // Report SD card information
        { 39, CodeClass.Flush, (h, c, ct) => h.HandleSDCardInfoAsync(c, ct) }, // free space is settled once pending file operations complete
        // Set output pin
        { 42, CodeClass.Deferred, (h, c, ct) => h.HandleSetOutputAsync(c, ct) },
        // Slicer-inserted print time values
        { 73, CodeClass.Immediate, (h, c, ct) => new ValueTask<Message>(h.HandleSlicerTimeHints(c)) }, // feeds the job monitor; nothing reads it in pipeline order
        // Absolute / relative extruder positioning
        { [82, 83], CodeClass.Immediate, (h, c, ct) => h.HandleExtruderPositioningAsync(c, ct) }, // interpreter state; later codes are processed behind it by construction
        // Set the idle timeout
        { 85, CodeClass.Immediate, (h, c, ct) => h.HandleIdleTimeoutAsync(c, ct) }, // a timer setting with no relation to pending codes
        // Set steps per mm; a bare M92 is a report, which DWC polls mid-print
        { 92, FlushAndStandstillWhenSettingDrives, (h, c, ct) => h.HandleStepsPerMmAsync(c, ct) },
        // Flag current macro file as (not) pausable
        { 98, CodeClass.Immediate, (h, c, ct) => h.HandleMacroPausableAsync(c, ct) }, // M98 P starts the macro on its own stack level; the handler flushes inline first
        // Set extruder temperature without waiting
        { 104, CodeClass.Deferred, async (h, c, ct) => await h.SetTemperaturesAsync(c, await h.CurrentToolHeatersAsync(c, ct), wait: false, ct) },
        // Report temperatures
        { 105, CodeClass.Immediate, (h, c, ct) => h.ReportTemperaturesAsync(ct) },
        // Set fan speed
        { 106, CodeClass.Deferred, (h, c, ct) => h.HandleFanSpeedAsync(c, ct) },
        // Fan off
        { 107, CodeClass.Deferred, (h, c, ct) => h.HandleFanOffAsync(c, ct) },
        // Set extruder temperature and wait: the target must be in force before the wait begins
        { 109, CodeClass.FlushAndStandstill, async (h, c, ct) => await h.SetTemperaturesAsync(c, await h.CurrentToolHeatersAsync(c, ct), wait: true, ct) },
        // Set debug level
        { 111, CodeClass.Immediate, (h, c, ct) => h.HandleDebugLevelAsync(c, ct) },
        // Emergency stop
        { 112, CodeClass.Immediate, (h, c, ct) => h.HandleEmergencyStopAsync(c, ct) }, // must never wait behind anything
        // Report the current position
        { 114, CodeClass.Immediate, (h, c, ct) => h.HandleReportPositionAsync(c, ct) },
        // Report firmware version
        { 115, CodeClass.Immediate, (h, c, ct) => h.HandleFirmwareVersionAsync(c, ct) },
        // Wait for temperatures: a barrier by definition, it blocks later G-code on a condition
        // derived from the targets
        { 116, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleWaitForTemperaturesAsync(c, ct) },
        // Publish MQTT message
        { 118, CodeClass.Immediate, (h, c, ct) => h.HandlePublishMqttAsync(c, ct) }, // the handler flushes inline so the message lands after earlier replies.
        // Report the endstop states
        { 119, CodeClass.Immediate, (h, c, ct) => h.HandleReportEndstopsAsync(c, ct) },
        // Push and pop the interpreter state
        { [120, 121], CodeClass.Immediate, (h, c, ct) => h.HandleStateStackAsync(c, ct) },
        // Immediate DSF diagnostics
        { 122, CodeClass.Immediate, (h, c, ct) => h.HandleDiagnosticsAsync(c, ct) },
        // Set bed temperature without waiting
        { 140, CodeClass.Deferred, async (h, c, ct) => await h.SetTemperaturesAsync(c, await h.BedOrChamberHeatersAsync(c, chamber: false, ct), wait: false, ct) },
        // Set chamber temperature without waiting
        { 141, CodeClass.Deferred, async (h, c, ct) => await h.SetTemperaturesAsync(c, await h.BedOrChamberHeatersAsync(c, chamber: true, ct), wait: false, ct) },
        // Heater monitors
        { 143, CodeClass.Immediate, (h, c, ct) => h.HandleHeaterMonitorAsync(c, ct) },
        // Set bed temperature and wait
        { 190, CodeClass.FlushAndStandstill, async (h, c, ct) => await h.SetTemperaturesAsync(c, await h.BedOrChamberHeatersAsync(c, chamber: false, ct), wait: true, ct) },
        // Set chamber temperature and wait
        { 191, CodeClass.FlushAndStandstill, async (h, c, ct) => await h.SetTemperaturesAsync(c, await h.BedOrChamberHeatersAsync(c, chamber: true, ct), wait: true, ct) },
        // Set axis and extruder accelerations; M201.1 sets the reduced set
        { 201, CodeClass.Immediate, (h, c, ct) => h.HandleAccelerationsAsync(c, reduced: false, ct) },
        { (201, 1), CodeClass.Immediate, (h, c, ct) => h.HandleAccelerationsAsync(c, reduced: true, ct) },
        // Set maximum feedrates
        { 203, CodeClass.Immediate, (h, c, ct) => h.HandleMaxFeedratesAsync(c, ct) },
        // Set printing and travel accelerations
        { 204, CodeClass.Immediate, (h, c, ct) => h.HandleMoveAccelerationsAsync(c, ct) },
        // Set jerk, in mm/sec (M205) or mm/min (M566)
        { [205, 566], CodeClass.Immediate, (h, c, ct) => h.HandleJerkAsync(c, ct) },
        // Set axis limits
        { 208, CodeClass.Immediate, (h, c, ct) => h.HandleAxisLimitsAsync(c, ct) }, // open decision (§5.1): §1 argues a standstill, but no handler ever waited
        // Set the speed factor
        { 220, CodeClass.Immediate, (h, c, ct) => h.HandleSpeedFactorAsync(c, ct) },
        // Set the extrusion factor
        { 221, CodeClass.Immediate, (h, c, ct) => h.HandleExtrusionFactorAsync(c, ct) },
        // Synchronous pause, filament change pause, Prusa-style pause
        { [226, 600, 601], CodeClass.Immediate, (h, c, ct) => h.HandleSynchronousPauseAsync(c, ct) }, // the pause point is where the handler's inline flush lands.
        // Servo control
        { 280, CodeClass.Deferred, (h, c, ct) => h.HandleServoAsync(c, ct) },
        // Babystepping
        { 290, CodeClass.Immediate, (h, c, ct) => h.HandleBabysteppingAsync(c, ct) },
        // Cold extrude and retract limits
        { 302, CodeClass.Immediate, (h, c, ct) => h.HandleColdExtrusionAsync(c, ct) },
        // Heater process model
        { 307, CodeClass.Immediate, (h, c, ct) => h.HandleHeaterModelAsync(c, ct) },
        // Configure a temperature sensor
        { 308, CodeClass.Immediate, (h, c, ct) => h.HandleConfigureSensorAsync(c, ct) },
        // Set microstepping; a bare M350 is a report
        { 350, FlushAndStandstillWhenSettingDrives, (h, c, ct) => h.HandleMicrosteppingAsync(c, ct) },
        // Save and load the height map, and set the compensation taper
        { 374, CodeClass.Immediate, (h, c, ct) => h.HandleSaveHeightMapAsync(c, ct) },
        { 375, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleLoadHeightMapAsync(c, ct) },
        { 376, CodeClass.Immediate, (h, c, ct) => h.HandleTaperHeightAsync(c, ct) },
        // Wait for the current moves to finish
        { 400, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleWaitForMovesAsync(c, ct) },
        // Deploy and retract the Z probe
        { 401, CodeClass.Immediate, (h, c, ct) => h.HandleDeployProbeAsync(c, ct) }, // runs deployprobe.g; the macro system sequences it
        { 402, CodeClass.Immediate, (h, c, ct) => h.HandleRetractProbeAsync(c, ct) }, // runs retractprobe.g; the macro system sequences it
        // Query object model
        { 409, CodeClass.Immediate, (h, c, ct) => h.HandleQueryObjectModelAsync(c, ct) }, // answers from the current model
        // Backlash compensation
        { 425, CodeClass.Immediate, (h, c, ct) => h.HandleBacklashAsync(c, ct) },
        // Report the machine mode
        { 450, CodeClass.Immediate, (h, c, ct) => h.HandleReportMachineModeAsync(ct) },
        // Select FFF, laser or CNC mode
        { 451, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleSetMachineModeAsync(c, MachineMode.FFF, ct) },
        { 452, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleSetMachineModeAsync(c, MachineMode.Laser, ct) },
        { 453, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleSetMachineModeAsync(c, MachineMode.CNC, ct) },
        // Create directory on SD card
        { 470, CodeClass.Flush, (h, c, ct) => h.HandleCreateDirectoryAsync(c, ct) }, // file operations land between codes, not while one is mid-completion
        // Rename file or directory on SD card
        { 471, CodeClass.Flush, (h, c, ct) => h.HandleRenameFileAsync(c, ct) }, // a pending code may still hold the old name
        // Delete file or directory
        { 472, CodeClass.Flush, (h, c, ct) => h.HandleDeleteFileOrDirectoryAsync(c, ct) }, // nothing pending may still be writing what it deletes
        // Save parameters to config-override.g
        { 500, CodeClass.Immediate, (h, c, ct) => h.HandleSaveConfigOverrideAsync(c, ct) },
        // Load parameters from config-override.g
        { 501, CodeClass.Flush, (h, c, ct) => h.HandleLoadConfigOverrideAsync(c, ct) }, // the override lands after pending codes stop writing what it replaces
        // Print settings
        { 503, CodeClass.Flush, (h, c, ct) => h.HandlePrintSettingsAsync(c, ct) }, // prints config.g, which a pending capture could be rewriting
        // Set the system folder, or with M505.1 the web folder
        { 505, CodeClass.Flush, (h, c, ct) => h.HandleSetFolderAsync(c, web: false, ct) }, // sys/ decides what macros resolve to, so it changes between codes. sometimes standstill
        { (505, 1), CodeClass.Flush, (h, c, ct) => h.HandleSetFolderAsync(c, web: true, ct) }, // as M505. sometimes standstill
        // Set machine name
        { 550, CodeClass.Flush, (h, c, ct) => h.HandleSetNameAsync(c, ct) }, // identity changes land between codes, not while replies are in flight
        // Set password
        { 551, CodeClass.Flush, (h, c, ct) => h.HandleSetPasswordAsync(c, ct) }, // as M550
        // Set IP address
        { 552, CodeClass.Flush, (h, c, ct) => h.HandleSetIPAddressAsync(c, ct) }, // as M550
        // Axis compensation
        { 556, CodeClass.Immediate, (h, c, ct) => h.HandleAxisCompensationAsync(c, ct) }, // the skew transform is applied when a move is built
        // Define the mesh compensation grid
        { 557, CodeClass.Flush, (h, c, ct) => h.HandleProbeGridAsync(c, ct) }, // the grid lands between codes; G29 itself is FlushAndStandstill
        // Configure a Z probe: no move that consults a probe may be queued or running while its
        // input monitor is replaced. A bare M558, or with K only, is a report
        { 558, c => c.Parameters.Any(p => p.Letter != 'K') ? CodeClass.FlushAndStandstill : CodeClass.Immediate, (h, c, ct) => h.HandleProbeConfigAsync(c, ct) },
        // Stop applying bed compensation
        { 561, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleClearCompensationAsync(c, ct) },
        // Clear a heater fault
        { 562, CodeClass.Immediate, (h, c, ct) => h.HandleClearHeaterFaultAsync(c, ct) }, // clearing a fault must not wait on a queue the fault may be blocking
        // Define or delete a tool: its offsets are part of the transform queued moves were planned
        // against. A bare M563 is a report
        { 563, c => c.Parameters.Any(p => p.Letter == 'P') ? CodeClass.FlushAndStandstill : CodeClass.Immediate, (h, c, ct) => h.HandleDefineToolAsync(c, ct) },
        // Limit axes and movement before homing
        { 564, CodeClass.Immediate, (h, c, ct) => h.HandleMovementLimitsAsync(c, ct) },
        // Set the mixing ratios of a tool
        { 567, CodeClass.Immediate, (h, c, ct) => h.HandleMixRatiosAsync(c, ct) },
        // Tool settings
        { 568, CodeClass.Deferred, (h, c, ct) => h.HandleToolSettingsAsync(c, ct) },
        // Configure a stepper driver and its subfunctions; unlisted minors have no row
        { [569, (569, 1), (569, 2), (569, 4), (569, 6), (569, 7)], CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleDriverConfigAsync(c, ct) },
        // Heater fault detection
        { 570, CodeClass.Immediate, (h, c, ct) => h.HandleHeaterFaultDetectionAsync(c, ct) },
        // Set pressure advance. TODO the value already rides the move on the SBC side; the
        // standstill remains until D3 of MOTION_SYNCHRONISED_ACTIONS.md is decided
        { 572, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandlePressureAdvanceAsync(c, ct) }, // TODO sync with motion
        // Configure the endstops
        { 574, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleEndstopConfigAsync(c, ct) },
        // Wait for an endstop or input to reach a state
        { 577, CodeClass.Immediate, (h, c, ct) => h.HandleWaitForInputAsync(c, ct) },
        // Configure external trigger; M581.1 is the expression form
        { 581, CodeClass.Immediate, (h, c, ct) => h.HandleConfigureTriggerAsync(c, expressionForm: false, ct) },
        { (581, 1), CodeClass.Immediate, (h, c, ct) => h.HandleConfigureTriggerAsync(c, expressionForm: true, ct) }, // the handler flushes inline before seeding from the model
        // Map axes and extruders onto stepper drivers; a bare M584 is a report
        { 584, c => c.Parameters.Count > 0 ? CodeClass.FlushAndStandstill : CodeClass.Immediate, (h, c, ct) => h.HandleDriveMappingAsync(c, ct) },
        // Configure network protocols; M586.4 configures MQTT
        { 586, CodeClass.Flush, (h, c, ct) => h.HandleNetworkProtocolsAsync(c, configureMqtt: false, ct) }, // protocol changes land between codes
        { (586, 4), CodeClass.Flush, (h, c, ct) => h.HandleNetworkProtocolsAsync(c, configureMqtt: true, ct) }, // as M586
        // Configure nonlinear extrusion
        { 592, CodeClass.Immediate, (h, c, ct) => h.HandleNonlinearExtrusionAsync(c, ct) },
        // Configure input shaping: queued moves were shaped with the old filter, so setting waits;
        // a bare M593 is a report
        { 593, c => c.Parameters.Count > 0 ? CodeClass.FlushAndStandstill : CodeClass.Immediate, (h, c, ct) => h.HandleInputShapingAsync(c, ct) },
        // Fork input reader
        { 606, CodeClass.Flush, (h, c, ct) => h.HandleForkInputReaderAsync(c, ct) }, // the fork point is the settled file position of the pending codes
        // Delta configuration and endstop adjustments, and selecting and configuring kinematics
        { [665, 666, 669], CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleKinematicsAsync(c, ct) },
        // Retired in RepRapFirmware in favour of M669
        { 667, CodeClass.Immediate, (h, c, ct) => new ValueTask<Message>(new Message(MessageType.Error, "M667 is no longer supported - use M669 instead")) },
        // Z leadscrew positions
        { 671, CodeClass.Immediate, (h, c, ct) => h.HandleLeadscrewsAsync(c, ct) },
        // Z probe offset, for Marlin compatibility
        { 851, CodeClass.Immediate, (h, c, ct) => h.HandleProbeOffsetAsync(c, ct) }, // rewrites G31 values; probing runs at standstill anyway
        // Set motor currents, current percentage and standstill current percentage; bare forms are
        // reports, which DWC polls mid-print
        { [906, 913, 917], FlushAndStandstillWhenSettingDrives, (h, c, ct) => h.HandleMotorCurrentsAsync(c, ct) },
        // Configure stall detection
        { 915, CodeClass.Immediate, (h, c, ct) => h.HandleStallDetectionAsync(c, ct) },
        // Start/stop event logging to SD card
        { 929, CodeClass.Flush, (h, c, ct) => h.HandleEventLoggingAsync(c, ct) }, // log entries are written as codes complete; start and stop order with them
        // Create a heater, fan or other I/O device
        { 950, CodeClass.Immediate, (h, c, ct) => h.HandleCreateDeviceAsync(c, ct) }, // open decision (§5.1): reassigning a live driver or port argues a standstill
        // Configure CAN: it changes the bus the moves travel on
        { 952, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleConfigureCanAsync(c, ct) },
        // Enable CAN
        { 953, CodeClass.Immediate, (h, c, ct) => h.HandleEnableCanAsync(c, ct) },
        // Raise an event
        { 957, CodeClass.Immediate, (h, c, ct) => new ValueTask<Message>(h.HandleRaiseEvent(c)) },
        // Configure phase stepping
        { 970, CodeClass.Immediate, (h, c, ct) => h.HandlePhaseSteppingAsync(c, ct) },
        // Update the firmware: everything is locked while it runs
        { 997, CodeClass.FlushAndStandstill, (h, c, ct) => h.HandleFirmwareUpdateAsync(c, ct) }, // TODO sometimes flushes
        // Reset the controller; M999 B resets a board, which must not happen with moves in its queue
        { 999, c => c.Parameters.Any(p => p.Letter == 'B') ? CodeClass.FlushAndStandstill : CodeClass.Immediate, (h, c, ct) => h.HandleResetAsync(c, ct) }, // the bare form flushes inline before rebooting DCS
    };

    /// <summary>
    /// M0, M1 and M2: stop, sleep or end the program
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleStopAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            if (code.Channel == CodeChannel.File2)
            {
                return new Message();
            }

            // Stopping the job cancels the read-ahead this code is part of, so from here it runs
            // under the shutdown token alone and its own reply survives
            if (code.IsFromFileChannel)
            {
                cancellationToken = code.DetachFromChannelCancellation();
            }

            // How the job ended decides which macro runs, and the transition table decides which of
            // them this is: a stop from inside the job file is the job reaching its end, a stop from
            // anywhere else is the operator cancelling one that has already been paused, and a stop
            // with no job at all is the machine being put down for the night
            return await jobController.StopAsync(code.Channel, cancellationToken);
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M20: list the files on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleListFilesAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // Resolve the directory
        if (!code.TryGetString('P', out string? virtualDirectory))
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                virtualDirectory = model.Directories.GCodes;
            }
        }
        string physicalDirectory = await filePathResolver.ToPhysicalAsync(virtualDirectory, cancellationToken: cancellationToken);

        // Make sure to stay within limits if it is a request from the firmware
        int maxSize = -1;
        if (code.Flags.HasFlag(CodeFlags.IsFromFirmware))
        {
            maxSize = settings.Value.MaxMessageLength;
        }

        // Check if JSON file lists were requested
        int startAt = Math.Max(code.GetInt('R', 0), 0), type = code.GetInt('S', 0), maxItems = code.GetInt('C', -1);
        if (type == 2)
        {
            string json = FileLists.GetFiles(virtualDirectory, physicalDirectory, startAt, true, maxSize, maxItems, code.ExplicitLineNumber);
            return new Message(MessageType.Success, json);
        }
        if (type == 3)
        {
            string json = FileLists.GetFileList(virtualDirectory, physicalDirectory, startAt, maxSize, maxItems, code.ExplicitLineNumber);
            return new Message(MessageType.Success, json);
        }

        // Print standard G-code response
        Compatibility compatibility;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            compatibility = model.Inputs[code.Channel]?.Compatibility ?? Compatibility.RepRapFirmware;
        }

        StringBuilder result = new();
        if (compatibility == Compatibility.Default || compatibility == Compatibility.RepRapFirmware)
        {
            result.AppendLine("GCode files:");
        }
        else if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
        {
            result.AppendLine("Begin file list:");
        }

        bool itemFound = false;
        foreach (string file in Directory.EnumerateFileSystemEntries(physicalDirectory))
        {
            string filename = Path.GetFileName(file);
            if (maxSize > 0 && result.Length + filename.Length + 3 > maxSize)
            {
                // Stay within limits...
                break;
            }

            if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
            {
                result.AppendLine(filename);
            }
            else
            {
                if (itemFound)
                {
                    result.Append(',');
                }
                result.Append($"\"{filename}\"");
            }
            itemFound = true;
        }

        if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
        {
            if (!itemFound)
            {
                result.AppendLine("NONE");
            }
            result.Append("End file list");
        }

        return new Message(MessageType.Success, result.ToString());
    }

    /// <summary>
    /// M21: initialize the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleInitializeSDCardAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.GetInt('P', 0) == 0)
        {
            // M21 (P0) will always work because it's always mounted
            return new Message();
        }
        throw new NotSupportedException();
    }

    /// <summary>
    /// M23 and M32: select a file to print, and for M32 start printing it
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSelectFileAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            if (code.Channel != CodeChannel.File2)
            {
                string fileName = code.GetUnprecedentedString();
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return new Message(MessageType.Error, "Filename expected");
                }

                string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, FileDirectory.GCodes, cancellationToken);
                if (!File.Exists(physicalFile))
                {
                    return new Message(MessageType.Error, $"Could not find file {fileName}");
                }

                // M32 read from the job file ends the run it is part of, so it leaves the channel's
                // cancellation behind in the same way M0 does
                if (code.IsFromFileChannel && code.MajorNumber == 32)
                {
                    cancellationToken = code.DetachFromChannelCancellation();
                }

                Message selected = await jobController.SelectFileAsync(fileName, physicalFile, simulating: false,
                                                                       updateSimulatedTime: true, code.Channel,
                                                                       startsNextRun: code.MajorNumber == 32,
                                                                       cancellationToken);
                if (selected.Type != MessageType.Success)
                {
                    return selected;
                }

                // M32 starts what it selected; M23 only selects. Starting goes through the same call
                // M24 makes, so start.g runs for both of them. M32 read from the job file selects for
                // the run after this one, which is started by the teardown rather than from here
                if (code.MajorNumber == 32 && !code.IsFromFileChannel)
                {
                    return await jobController.StartOrResumeAsync(code.Channel, runMacro: true, cancellationToken);
                }
            }

            return new Message();
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M24: start or resume a file print
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleResumePrintAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }
        if (code.Channel == CodeChannel.File2)
        {
            return new Message();
        }

        // P0 skips resume.g, as it does in RepRapFirmware
        bool runMacro = code.GetInt('P', 1) != 0;
        return await jobController.StartOrResumeAsync(code.Channel, runMacro, cancellationToken);
    }

    /// <summary>
    /// M25: pause the print
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// <para>
    /// A pause from inside the job file is <em>synchronous</em>: the file has already reached the
    /// point it is pausing at, so everything before it runs and the machine stops there. A pause from
    /// anywhere else interrupts whatever the job was doing, which is RepRapFirmware's distinction
    /// between <c>DoSynchronousPause</c> and <c>DoAsynchronousPause</c>.
    /// </para>
    /// <para>
    /// A pause asked for while the job is inside a macro that cannot be restarted is held until the
    /// job is back out of it, which the transition table decides rather than this handler:
    /// RepRapFirmware's <c>deferredPauseCommandPending</c>
    /// </para>
    /// </remarks>
    private async ValueTask<Message> HandlePausePrintAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }

        if (code.Channel == CodeChannel.File2)
        {
            return new Message();
        }

        // A pause the job file asked for cannot be cancelled by the freeze it asks for, so its own
        // reply survives the read-ahead being dropped
        if (code.IsFromFileChannel)
        {
            cancellationToken = code.DetachFromChannelCancellation();
        }

        return await jobController.PauseAsync(new Files.Job.PauseRequest(code.Channel, PrintPausedReason.User, Files.Job.PauseMacro.Pause,
                                                               Synchronous: code.IsFromFileChannel,
                                                               ReportPosition: true),
                                              cancellationToken);
    }

    /// <summary>
    /// M226, M600 and M601: pause from within the job file
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// M600 asks for a filament change, so it prefers <c>filament-change.g</c> and says so when it
    /// reports where it stopped. <c>M226 P0</c> runs no macro at all. M601 is M226 under the name
    /// Prusa slicers emit
    /// </remarks>
    private async ValueTask<Message> HandleSynchronousPauseAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.IsFromFileChannel)
        {
            return new Message(MessageType.Error, "use M226/600/601 only within a file being printed");
        }

        if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            if (code.Channel == CodeChannel.File2)
            {
                return new Message();
            }

            bool filamentChange = code.MajorNumber == 600;
            Files.Job.PauseMacro macro = filamentChange ? Files.Job.PauseMacro.FilamentChange
                                         : code.GetInt('P', 1) == 0 ? Files.Job.PauseMacro.None
                                         : Files.Job.PauseMacro.Pause;
            PrintPausedReason reason = filamentChange ? PrintPausedReason.FilamentChange : PrintPausedReason.GCode;

            // The freeze this asks for must not cancel its own reply
            cancellationToken = code.DetachFromChannelCancellation();

            return await jobController.PauseAsync(new Files.Job.PauseRequest(code.Channel, reason, macro,
                                                                   Synchronous: true, ReportPosition: true),
                                                  cancellationToken);
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M26: set the position within the file being printed
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetFilePositionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int motionSystem;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            motionSystem = model.Inputs[code.Channel]?.MotionSystem ?? 0;
        }

        if (code.TryGetLong('S', out long newPosition))
        {
            Message result = await jobController.SetFilePositionAsync(motionSystem, newPosition, cancellationToken);
            if (result.Type != MessageType.Success)
            {
                return result;
            }
        }
        else if (!jobController.State.IsFileSelected)
        {
            return new Message(MessageType.Error, "Not printing a file");
        }

        // How the line at that position is to be read when the job starts. P is how much of it the
        // machine has already made - a job restarted by resurrect.g after a power failure is
        // part-way through a line exactly as a resumed pause is - and C is the modal command it was
        // read under, which the line itself may not name. They are held until M24 because that is
        // what starts printing
        //
        // TODO M26 also takes the arc restart point in the selected plane's two axis words, which
        // needs InitialUserC0 / InitialUserC1 and so waits for G2/G3
        using (planner.Lock())
        {
            planner.State.RestartMoveFractionDone = code.GetFloatLimited('P', 0.0f, 1.0f, 0.0f);
            planner.State.RestartGCommandNumber = code.GetInt('C', -1);
        }

        return new Message();
    }

    /// <summary>
    /// M27: report the SD print status
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleReportPrintStatusAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int motionSystem;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            motionSystem = model.Inputs[code.Channel]?.MotionSystem ?? 0;
        }

        // A file that is only selected is not being printed, which is what RepRapFirmware reports:
        // Pronterface polls this and takes "SD printing byte" as the job running
        Files.Job.JobState state = jobController.State;
        if (state.IsJobInProgress)
        {
            return new Message(MessageType.Success,
                               $"SD printing byte {jobController.GetFilePosition(motionSystem)}/{state.FileLength}");
        }
        return new Message(MessageType.Success, "Not SD printing.");
    }

    /// <summary>
    /// M28: begin writing to a file on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleBeginFileWriteAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int numChannel = (int)code.Channel;
        using (await codeProcessor.FileLocks[numChannel].LockAsync(cancellationToken))
        {
            if (codeProcessor.FilesBeingWritten[numChannel] is not null)
            {
                return new Message(MessageType.Error, "Another file is already being written to");
            }

            string file = code.GetUnprecedentedString();
            if (string.IsNullOrWhiteSpace(file))
            {
                return new Message(MessageType.Error, "Filename expected");
            }

            string prefix = await model.IsEmulatingMarlinAsync(code.Channel, cancellationToken) ? "ok\n" : string.Empty;
            string physicalFile = await filePathResolver.ToPhysicalAsync(file, FileDirectory.GCodes, cancellationToken), parentDirectory = Path.GetDirectoryName(physicalFile)!;
            try
            {
                if (!Directory.Exists(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                FileStream fileStream = new(physicalFile, FileMode.Create, FileAccess.Write, FileShare.Read, settings.Value.FileBufferSize);
                StreamWriter writer = new(fileStream, Encoding.UTF8, settings.Value.FileBufferSize);
                codeProcessor.FilesBeingWritten[numChannel] = writer;
                return new Message(MessageType.Success, prefix + $"Writing to file: {file}");
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Failed to open file for writing");
                return new Message(MessageType.Error, prefix + $"Can't open file {file} for writing.");
            }
        }
    }

    /// <summary>
    /// M29: finish writing to a file on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleEndFileWriteAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int numChannel = (int)code.Channel;
        using (await codeProcessor.FileLocks[numChannel].LockAsync(cancellationToken))
        {
            StreamWriter? writer = codeProcessor.FilesBeingWritten[numChannel];
            if (writer is not null)
            {
                Stream stream = writer.BaseStream;
                await writer.DisposeAsync();
                codeProcessor.FilesBeingWritten[numChannel] = null;
                await stream.DisposeAsync();

                if (await model.IsEmulatingMarlinAsync(code.Channel, cancellationToken))
                {
                    return new Message(MessageType.Success, "Done saving file.");
                }
                return new Message();
            }
            // TODO DSF used to let this fall through to RRF. Determine what actually needs to happen
            return new Message(MessageType.Warning, "Possibly undefined behaviour");
        }
    }

    /// <summary>
    /// M30: delete a file on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleDeleteFileAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string file = code.GetUnprecedentedString();
        string physicalFile = await filePathResolver.ToPhysicalAsync(file, cancellationToken: cancellationToken);

        try
        {
            File.Delete(physicalFile);
            return new Message();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to delete file");
            return new Message(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
        }
    }

    /// <summary>
    /// M36: return information about a G-code file, a thumbnail in it, or a fragment of it
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleFileInfoAsync(Commands.Code code, bool? thumbnail, CancellationToken cancellationToken)
    {
        if (code.Parameters.Count > 0)
        {
            string virtualFilename = string.Empty;
            try
            {
                if (thumbnail is null)
                {
                    // Get fileinfo
                    virtualFilename = code.GetUnprecedentedString();
                    string physicalFilename = await filePathResolver.ToPhysicalAsync(virtualFilename, FileDirectory.GCodes, cancellationToken);
                    GCodeFileInfo info = await fileInfoParser.ParseAsync(physicalFilename, false, cancellationToken);

                    string json = JsonSerializer.Serialize(info, ObjectModelContext.Default.GCodeFileInfo);
                    return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":0," : "{\"err\":0,") + json[1..]);
                }
                else
                {
                    // Get thumbnail or file fragment
                    virtualFilename = code.GetString('P');
                    string physicalFilename = await filePathResolver.ToPhysicalAsync(virtualFilename, FileDirectory.GCodes, cancellationToken);

                    string json = await fileInfoParser.ParseFileFragment(physicalFilename, code.GetLong('S'), thumbnail.Value, code.ExplicitLineNumber);
                    return new Message(MessageType.Success, json);
                }
            }
            catch (Exception e) when (e is not MissingParameterException and not InvalidParameterTypeException)
            {
                logger.LogDebug(e, "Failed to return file information");
                return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":1,\"fileName:" : "{\"err\":1,\"fileName:") + JsonSerializer.Serialize(virtualFilename, CommonContext.Default.String) + "}");
            }
        }
        else
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (model.Job.File.FileName != null)
                {
                    string json = JsonSerializer.Serialize(model.Job.File, ObjectModelContext.Default.GCodeFileInfo);
                    return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":0," : "{\"err\":0,") + json[1..]);
                }
            }
            return new Message(MessageType.Success, (code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":1}}" : "{\"err\":1}");
        }
    }

    /// <summary>
    /// M37: select a file to simulate
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSimulateFileAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            if (code.Channel != CodeChannel.File2 && code.HasParameter('P'))
            {
                string fileName = code.GetString('P');
                string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, FileDirectory.GCodes, cancellationToken);
                if (!File.Exists(physicalFile))
                {
                    return new Message(MessageType.Error, $"GCode file \"{fileName}\" not found");
                }

                // F0 suppresses writing the simulated time back to the file; absent or F1 updates
                // it, as in standalone mode
                Message selected = await jobController.SelectFileAsync(fileName, physicalFile, simulating: true,
                                                                      updateSimulatedTime: code.GetInt('F', 1) == 1,
                                                                      code.Channel, startsNextRun: true,
                                                                      cancellationToken);
                if (selected.Type != MessageType.Success)
                {
                    return selected;
                }

                // Where the machine was before the simulation ran, so it can be put back afterwards.
                // RepRapFirmware saves it into the simulation restore point for the same reason
                await SaveSimulationRestorePointAsync(code, cancellationToken);

                // Starting a simulation is starting a job, so it goes through the same call M24 makes
                return await jobController.StartOrResumeAsync(code.Channel, runMacro: true, cancellationToken);
            }

            return new Message();
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// Save where the machine is before a simulation runs
    /// </summary>
    /// <param name="code">The code that started the simulation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>RepRapFirmware's <c>SimulationRestorePointNumber</c></remarks>
    private async ValueTask SaveSimulationRestorePointAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? 25.4f : 1.0f;
            float feedRateMmPerSec = (input?.FeedRate ?? 0.0f) * unitScale / 60.0f;

            using (planner.Lock())
            {
                planner.State.SavePosition(Motion.RestorePoint.SimulationNumber,
                                           planner.Parameters.SharedAxisCount(model.Move),
                                           feedRateMmPerSec, model.State.CurrentTool, filePosition: null);
            }
        }
    }

    /// <summary>
    /// M73: what the slicer expects the job to take
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// A slicer knows what it generated, so its estimate is better than anything measured from the
    /// outside until the job has been running for a while. R is the time left in minutes; P is the
    /// percentage done, which is the slicer's own view and is not used - the file position says that
    /// </remarks>
    private Message HandleSlicerTimeHints(Commands.Code code)
    {
        if (code.TryGetFloat('R', out float minutesLeft))
        {
            jobMonitor.SetSlicerTimeLeft(minutesLeft * 60.0f);
        }
        return new Message();
    }

    /// <summary>
    /// M38: compute the CRC32 checksum of a file
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleFileChecksumAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string file = code.GetUnprecedentedString(), physicalFile = await filePathResolver.ToPhysicalAsync(file, cancellationToken: cancellationToken);
        try
        {
            await using FileStream stream = new(physicalFile, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
            uint checksum = await CRC32.CalculateAsync(stream, settings.Value.FileBufferSize, cancellationToken);
            return new Message(MessageType.Success, checksum.ToString("x8"));
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to compute CRC32 checksum");
            if (e is AggregateException ae)
            {
                e = ae.InnerException!;
            }
            return new Message(MessageType.Error, $"Could not compute CRC32 checksum for file {file}: {e.Message}");
        }
    }

    /// <summary>
    /// M39: report information about an SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSDCardInfoAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            int index = code.GetInt('P', 0);
            if (code.GetInt('S', 0) == 2)
            {
                if (index < 0 || index >= model.Volumes.Count)
                {
                    return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber}," : "{") + $"\"SDinfo\":{{\"slot\":{index},\"present\":0}}}}");
                }

                Volume storage = model.Volumes[index];
                SDInfoDetails output = new()
                {
                    Slot = index,
                    Present = 1,
                    Capacity = storage.Capacity,
                    PartitionSize = storage.PartitionSize,
                    Free = storage.FreeSpace,
                    Speed = storage.Speed
                };

                string sdInfo = JsonSerializer.Serialize(output, MCodeResponseContext.Default.SDInfoDetails);
                return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber}," : "{") + $"\"SDinfo\":{sdInfo}}}");
            }
            else
            {
                if (index < 0 || index >= model.Volumes.Count)
                {
                    return new Message(MessageType.Error, $"Bad SD slot number: {index}");
                }

                Volume storage = model.Volumes[index];
                return new Message(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / 1000000000.0:F2}Gb, partition size {storage.PartitionSize / 1000000000.0:F2}Gb, free space {storage.FreeSpace / 1000000000.0:F2}Gb, speed {storage.Speed / 1000000.0:F2}MBytes/sec");
            }
        }
    }

    /// <summary>
    /// M98: flag the current macro file as (not) pausable
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleMacroPausableAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // R on its own flags the macro that is already running rather than starting a new one
        if (code.TryGetInt('R', out int rParam) && !code.HasParameter('P'))
        {
            if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
            {
                throw new OperationCanceledException();
            }

            if (codeProcessor.GetCurrentFile(code.Channel) is MacroFile currentMacro)
            {
                using (await currentMacro.LockAsync(cancellationToken))
                {
                    currentMacro.IsPausable = rParam == 1;
                }
            }
            return new Message();
        }

        if (!code.TryGetString('P', out string? fileName))
        {
            return new Message(MessageType.Error, "Filename expected");
        }

        if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }

        // A macro named without a directory is looked up in the system directory, which is what makes
        // M98 P"homex.g" find sys/homex.g the way it does in RepRapFirmware
        // M98 is the user invoking a macro, not the firmware asking for one, so workplace offsets
        // and the speed and extrusion overrides still apply to the moves inside it
        if (!await macroRunner.TryRunAsync(code.Channel, fileName, code, isSystemMacro: false,
                                           cancellationToken: cancellationToken))
        {
            return new Message(MessageType.Error, $"Macro file {fileName} not found");
        }
        return new Message();
    }

    /// <summary>
    /// M111: set the debug level
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    /// <remarks>
    /// Only P-1 is handled here, and only two of its options:
    /// S"&lt;level&gt;" sets the log level - trace, debug, info, warn, error, fatal, off, and their long
    /// forms - and Onnn turns logging via generic messages on or off, which is what makes it visible in
    /// the web interface
    /// </remarks>
    private async ValueTask<Message> HandleDebugLevelAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetInt('P', out int pParam) && pParam == -1)
        {
            bool seen = false;
            if (code.TryGetString('S', out string? levelString))
            {
                // Parse the log level using shared helper that supports short aliases
                if (LogLevelHelper.TryParseLogLevel(levelString, out LogLevel level))
                {
                    // Writing settings.Value.LogLevel is all that's needed: the dynamic
                    // logging filter in Program.cs reads it directly on every IsEnabled() call.
                    settings.Value.LogLevel = level;
                    logger.LogInformation("Log level changed to {Level}", level);
                    seen = true;
                }
                else
                {
                    return new Message(MessageType.Error, $"Invalid log level '{levelString}'. Valid values: {LogLevelHelper.ValidLogLevels}");
                }
            }
            if (code.TryGetBool('O', out bool oParam))
            {
                if (oParam)
                {
                    if (_messageLoggerProvider == null)
                    {
                        // Only add this provider once and don't allow higher log level than debug, else we may get recursion
                        LogLevel minimumLevel = settings.Value.LogLevel > LogLevel.Trace ? settings.Value.LogLevel : LogLevel.Debug;
                        _messageLoggerProvider = new MessageLoggerProvider(model, minimumLevel);
                        loggerFactory.AddProvider(_messageLoggerProvider);
                    }
                    else
                    {
                        _messageLoggerProvider.Enabled = true;
                    }
                }
                else if (_messageLoggerProvider is not null)
                {
                    // The logger factory offers no way to remove the provider again, so just disable its output
                    _messageLoggerProvider.Enabled = false;
                }
                seen = true;
            }

            if (seen)
            {
                return new Message();
            }
            return new Message(MessageType.Success, $"Current DCS log level: {settings.Value.LogLevel}");
        }
        return new Message(MessageType.Success, $"Current DCS log level: {settings.Value.LogLevel}");
    }

    /// <summary>
    /// M112: emergency stop
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleEmergencyStopAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            // Wait for potential firmware updates to complete first
            await linkInterface.WaitForUpdateAsync(cancellationToken);

            // Perform emergency stop but don't wait longer than 4.5s
            Task stopTask = linkInterface.EmergencyStopAsync(cancellationToken);
            Task completedTask = await Task.WhenAny(stopTask, Task.Delay(4500, lifetime.ApplicationStopped));
            if (stopTask != completedTask)
            {
                // Halt timed out, shut down this program
                lifetime.StopApplication();
                return new Message(MessageType.Error, "Halt timed out, stopping DCS");
            }

            // RRF halted
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.IsHalted = true;
            }
            return new Message();
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M115: report the firmware version of this program or of an expansion board
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleFirmwareVersionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // Like M122, M115 is about the program rather than about attached hardware, so board 0 is a
        // real answer here rather than the mistake it is everywhere else
        int board = code.GetInt('B', CanId.MasterAddress);
        if (board == CanId.MasterAddress)
        {
            // TODO reply with DSF firmware info
            return new Message(MessageType.Success, "DSF firmware version");
        }
        else if (board > CanId.MasterAddress && board <= CanId.BroadcastAddress)
        {
            logger.LogDebug("Requesting firmware version for board {Board}", board);
            CanMessageReturnInfo msg = new()
            {
                Type = CanMessageReturnInfo.TypeFirmwareVersion,
                Param = 0
            };
            CanResponse response = await linkInterface.SendCanMessageAsync((byte)board, msg, CanMessageType.StandardReply, cancellationToken: cancellationToken);
            logger.LogDebug("Received firmware version for board {Board}: {Payload}", board, response.Text);
            return response.ToMessage();
        }
        else
        {
            return new Message(MessageType.Error, $"Invalid board number {board}");
        }
    }

    /// <summary>
    /// M118 P6: publish an MQTT message
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandlePublishMqttAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetInt('P', out int pParam) && pParam == 6)
        {
            if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
            {
                return await mqtt.PublishAsync(code);
            }
            throw new OperationCanceledException();
        }
        // TODO this used to fallthrough to RRF
        return new Message(MessageType.Warning, "Not implemented");
    }

    /// <summary>
    /// M122 "DSF": report this program's diagnostics without waiting for the firmware
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleDiagnosticsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // M122 is one of the codes board 0 does answer for: DuetCANMaster and this program are what
        // there is to report on, whatever hardware is or is not attached to it
        int board = code.GetInt('B', CanId.MasterAddress);
        if (board != CanId.MasterAddress)
        {
            return new Message(MessageType.Error, $"Diagnostics for expansion board {board} are not supported yet");
        }

        string diagnostics = await diagnosticsProvider.PrintAsync();
        return new Message(MessageType.Success, diagnostics);
    }

    /// <summary>
    /// M409: query the object model
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleQueryObjectModelAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetString('K', out string? key) && (!code.TryGetInt('R', out int rParam) || rParam == 0))
        {
            // This used to answer only for the keys the SBC owned - network, plugins, sbc, volumes -
            // and leave the rest to the firmware's copy of the object model. There is one object
            // model now and it is this one, so every key is answered here. A read of the model does
            // not wait for pending codes: DWC polls this mid-print and the answer is a snapshot
            // either way

            // Query the object model using the new command
            code.TryGetString('F', out string? flags);
            Commands.QueryObjectModel queryCommand = commandFactory.Create<Commands.QueryObjectModel>();
            queryCommand.Key = key;
            queryCommand.Flags = flags ?? string.Empty;
            JsonElement queryResult = await queryCommand.ExecuteAsync(cancellationToken);

            string json = queryResult.GetRawText();
            return new Message(MessageType.Success, (code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber}," + json[1..] : json);
        }
        // TODO this used to fallthrough to RRF
        return new Message(MessageType.Warning, "Not implemented");
    }

    /// <summary>
    /// M470: create a directory on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleCreateDirectoryAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string path = code.GetString('P'), physicalPath = await filePathResolver.ToPhysicalAsync(path, cancellationToken: cancellationToken);
        try
        {
            Directory.CreateDirectory(physicalPath);
            return new Message();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to create directory");
            return new Message(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
        }
    }

    /// <summary>
    /// M471: rename a file or directory on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleRenameFileAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string from = code.GetString('S'), to = code.GetString('T');
        try
        {
            string source = await filePathResolver.ToPhysicalAsync(from, cancellationToken: cancellationToken), destination = await filePathResolver.ToPhysicalAsync(to, cancellationToken: cancellationToken);
            if (File.Exists(source))
            {
                if (File.Exists(destination) && code.GetBool('D', false))
                {
                    File.Delete(destination);
                }
                File.Move(source, destination);
            }
            else if (Directory.Exists(source))
            {
                if (Directory.Exists(destination) && code.GetBool('D', false))
                {
                    // This could be recursive but at the moment we mimic RRF's behaviour
                    Directory.Delete(destination);
                }
                Directory.Move(source, destination);
            }
            else
            {
                throw new FileNotFoundException();
            }
            return new Message();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to rename file or directory");
            return new Message(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
        }
    }

    /// <summary>
    /// M472: delete a file or directory
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleDeleteFileOrDirectoryAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string path = code.GetString('P'), physicalPath = await filePathResolver.ToPhysicalAsync(path, cancellationToken: cancellationToken);
        try
        {
            if (Directory.Exists(physicalPath))
            {
                _ = code.TryGetBool('R', out bool recursive);
                Directory.Delete(physicalPath, recursive);
            }
            else
            {
                File.Delete(physicalPath);
            }
            return new Message();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to delete file or directory");
            return new Message(MessageType.Error, $"Failed to delete file or directory {path}: {e.Message}");
        }
    }

    /// <summary>
    /// M501: load the saved settings from config-override.g
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    /// <remarks>
    /// config-override.g is a macro like any other - it holds the M-codes M500 wrote - so loading it
    /// is running it
    /// </remarks>
    private async ValueTask<Message> HandleLoadConfigOverrideAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!await macroRunner.TryRunAsync(code.Channel, FilePathResolver.ConfigOverrideFile, code, cancellationToken: cancellationToken))
        {
            return new Message(MessageType.Error, $"Macro file {FilePathResolver.ConfigOverrideFile} not found");
        }
        return new Message();
    }

    /// <summary>
    /// M503: report the configuration file
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandlePrintSettingsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        string configFile = await filePathResolver.ToPhysicalAsync(FilePathResolver.ConfigFile, FileDirectory.System, cancellationToken);
        if (File.Exists(configFile))
        {
            string content = await File.ReadAllTextAsync(configFile, cancellationToken);
            return new Message(MessageType.Success, content);
        }

        string configFileFallback = await filePathResolver.ToPhysicalAsync(FilePathResolver.ConfigFileFallback, FileDirectory.System, cancellationToken);
        if (File.Exists(configFileFallback))
        {
            string content = await File.ReadAllTextAsync(configFileFallback, cancellationToken);
            return new Message(MessageType.Success, content);
        }
        return new Message(MessageType.Error, "Configuration file not found");
    }

    /// <summary>
    /// M505: set the system folder, or with M505.1 the web folder
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetFolderAsync(Commands.Code code, bool web, CancellationToken cancellationToken)
    {
        if (code.TryGetString('P', out string? directory))
        {
            // Changing the system folder under a running job would change which macros a queued
            // move's callbacks resolve to, so wait for the machine to stop first
            if (await planner.StandstillAsync(cancellationToken))
            {
                string physicalDirectory = await filePathResolver.ToPhysicalAsync(directory, web ? "www" : "sys", cancellationToken);
                if (Directory.Exists(physicalDirectory))
                {
                    string virtualDirectory = await filePathResolver.ToVirtualAsync(physicalDirectory, cancellationToken);
                    using (await model.AccessReadWriteAsync(cancellationToken))
                    {
                        if (web)
                        {
                            model.Directories.Web = virtualDirectory;
                        }
                        else
                        {
                            model.Directories.System = virtualDirectory;
                        }
                    }
                    return new Message();
                }
            }
            return new Message(MessageType.Error, "Directory not found");
        }

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return new Message(MessageType.Success, $"{(web ? "HTTP" : "Sys")} file path is {(web ? model.Directories.Web : model.Directories.System)}");
        }
    }

    /// <summary>
    /// M550: set the machine name
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetNameAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetString('P', out string? newName))
        {
            if (newName.Length > 40)
            {
                return new Message(MessageType.Error, "Machine name is too long");
            }

            // Strip letters and digits from the machine name
            string machineName = string.Empty;
            foreach (char c in Environment.MachineName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    machineName += c;
                }
            }

            // Strip letters and digits from the desired name
            string desiredName = string.Empty;
            foreach (char c in newName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    desiredName += c;
                }
            }

            // Make sure the subset of letters and digits is equal
            if (!machineName.Equals(desiredName, StringComparison.CurrentCultureIgnoreCase))
            {
                return new Message(MessageType.Error, "Machine name must consist of the same letters and digits as configured by the Linux hostname");
            }

            // The name matches the Linux hostname, so it is safe to adopt
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.Network.Name = newName;
            }
            return new Message();
        }

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return new Message(MessageType.Success, $"RepRap name: {model.Network.Name}");
        }
    }

    /// <summary>
    /// M551: set the password
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetPasswordAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetString('P', out string? password))
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.Password = password;
            }
        }
        return new Message();
    }

    /// <summary>
    /// M552: set the IP address
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetIPAddressAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // TODO implement M552
        throw new NotImplementedException();
    }

    /// <summary>
    /// M581: configure an external trigger
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    /// <remarks>
    /// Only the expression form M581.1 is handled here, and only when the expression names SBC fields.
    /// Plain M581 hands the slot back to the firmware
    /// </remarks>
    private async ValueTask<Message> HandleConfigureTriggerAsync(Commands.Code code, bool expressionForm, CancellationToken cancellationToken)
    {
        if (expressionForm)
        {
            // The seed evaluation reads the object model, so pending codes settle first
            if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
            {
                return await sbcTriggerService.ConfigureAsync(code, cancellationToken);
            }
            throw new OperationCanceledException();
        }

        // The plain form used to hand the slot back to the firmware's own trigger system; there is
        // no such system now, so all this can do is drop the trigger managed here
        if (code.TryGetInt('T', out int triggerNumber))
        {
            sbcTriggerService.Remove(triggerNumber);
            return new Message();
        }
        return new Message(MessageType.Error, "Only the expression form M581.1 is supported");
    }

    /// <summary>
    /// M586: configure the network protocols
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleNetworkProtocolsAsync(Commands.Code code, bool configureMqtt, CancellationToken cancellationToken)
    {
        // Configure MQTT
        if (configureMqtt)
        {
            return mqtt.Configure(code);
        }
        else if (code.TryGetInt('P', out int pParam) && pParam == 4)
        {
            return await mqtt.ConfigureProtocolAsync(code);
        }

        // Set CORS site
        if (code.TryGetString('C', out string? corsSite))
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                model.Network.CorsSite = string.IsNullOrWhiteSpace(corsSite) ? null : corsSite;
            }
            return new Message();
        }

        // Report CORS state
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (string.IsNullOrEmpty(model.Network.CorsSite))
            {
                return new Message(MessageType.Success, "CORS disabled");
            }
            return new Message(MessageType.Success, $"CORS enabled for site '{model.Network.CorsSite}'");
        }
    }

    /// <summary>
    /// M606: fork the input reader
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleForkInputReaderAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetInt('S', out int sParam) && sParam == 1)
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (model.Inputs[CodeChannel.File2] is null)
                {
                    // Command not supported. Let RRF decide what to do
                    // TODO this used to fallthrough to RRF
                    return new Message(MessageType.Warning, "Not implemented");
                }
            }

            // Try to fork the file and report an error if anything went wrong. The command starts
            // the second stream itself, so nothing has to come back afterwards to do it
            Message result = await jobController.ForkAsync(cancellationToken);
            if (result.Type != MessageType.Success)
            {
                return result;
            }
        }

        return new Message();
    }

    /// <summary>
    /// M929: start or stop event logging
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleEventLoggingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('S', out int sParam))
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (model.State.LogLevel == EventLogLevel.Off)
                {
                    return new Message(MessageType.Success, "Event logging is disabled");
                }
                return new Message(MessageType.Success, $"Event logging is enabled at log level {model.State.LogLevel.ToString().ToLowerInvariant()}");
            }
        }

        if (sParam > 0 && sParam < 4)
        {
            EventLogLevel logLevel = sParam switch
            {
                1 => EventLogLevel.Warn,
                2 => EventLogLevel.Info,
                3 => EventLogLevel.Debug,
                _ => EventLogLevel.Off
            };

            string defaultLogFile = EventLogger.DefaultLogFile;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (!string.IsNullOrEmpty(model.State.LogFile))
                {
                    defaultLogFile = model.State.LogFile;
                }
            }

            await eventLogger.StartAsync(code.GetString('P', defaultLogFile), logLevel);
        }
        else
        {
            await eventLogger.StopAsync();
        }
        return new Message();
    }

    /// <summary>
    /// M952: set the CAN address and timing of an expansion board
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleConfigureCanAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        uint oldAddress = code.GetUInt('B', 0);

        CanTiming timing = new();
        bool changeTiming = false;
        if (code.TryGetUIntLimited('S', 15, 5000, out uint speed)) // TODO set these as constants somewhere
        {
            changeTiming = true;
            timing.SetDefaults(speed * 1000);

            if (code.TryGetFloatLimited('T', 0.5f, 0.95f, out float normalSamplePoint))
            {
                timing.SetNormalSamplePoint(normalSamplePoint);
            }

            if (code.TryGetFloatLimited('J', 0.05f, 0.5f, out float normalJumpWidth))
            {
                timing.SetNormalJumpWidth(normalJumpWidth);
            }
        }

        if (changeTiming)
        {
            code.TryGetUIntLimited('A', 1, 127, out uint? newAddress);

            await linkInterface.ConfigCanAsync((byte)oldAddress, (byte?)newAddress, timing, cancellationToken);
        }
        else
        {
            CanResponse response = await linkInterface.ReportCanConfigAsync((byte)oldAddress, cancellationToken);
            return response.ToMessage();
        }
        return new Message();
    }

    /// <summary>
    /// M953: enable CAN and set its data rate
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleEnableCanAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        bool changeTiming = false;
        uint DefaultCanBitRate = CanTiming.DefaultCanBitRate / 1000;
        CanTiming timing = new();

        if (code.TryGetUIntLimited('S', 15, 5000, out uint speed))
        {
            if (speed != DefaultCanBitRate && speed != DefaultCanBitRate / 2 && speed != DefaultCanBitRate / 4)
            {
                return new Message(MessageType.Error, $"Invalid CAN speed {speed}. Valid values are {DefaultCanBitRate}, {DefaultCanBitRate / 2}, {DefaultCanBitRate / 4}");
            }

            changeTiming = true;
        }
        else
        {
            speed = DefaultCanBitRate;
        }
        timing.SetDefaults(speed * 1000);

        if (code.TryGetFloatLimited('T', 0.5f, 0.95f, out float normalSamplePoint))
        {
            changeTiming = true;
            timing.SetNormalSamplePoint(normalSamplePoint);
        }

        if (code.TryGetFloatLimited('J', 0.05f, 0.5f, out float normalJumpWidth))
        {
            changeTiming = true;
            timing.SetNormalJumpWidth(normalJumpWidth);
        }

        if (code.TryGetUIntLimited('R', 0, 8, out uint bitRateMultiplier))
        {
            changeTiming = true;
            if (bitRateMultiplier == 0 || bitRateMultiplier == 5 || bitRateMultiplier == 7)
            {
                return new Message(MessageType.Error, $"Invalid bit rate multiplier {bitRateMultiplier}. Valid values are 1, 2, 3, 4, 6, 8");
            }

            timing.EnableBrs((byte)bitRateMultiplier);

            if (code.TryGetFloatLimited('U', 0.5f, 0.95f, out float dataSamplePoint))
            {
                timing.SetDataSamplePoint(dataSamplePoint);
            }

            if (code.TryGetFloatLimited('K', 0.05f, 0.5f, out float dataJumpWidth))
            {
                timing.SetDataJumpWidth(dataJumpWidth);
            }
        }

        if (changeTiming)
        {
            await linkInterface.ConfigCanAsync(0, null, timing, cancellationToken);
        }

        await linkInterface.EnableCanAsync(true, cancellationToken);

        return new Message();
    }

    /// <summary>
    /// M957: raise an event
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// How an event macro is exercised without the machine having to produce the fault it is for, and
    /// the only way to reach <c>controller-disconnect.g</c> short of pulling a cable. The event is
    /// raised and nothing else happens: this does not touch the link or invalidate anything, so a
    /// simulated disconnect runs its macro against a machine that is still there
    /// </remarks>
    private Message HandleRaiseEvent(Commands.Code code)
    {
        if (!code.TryGetString('E', out string? typeName))
        {
            return new Message(MessageType.Error, "Missing event type");
        }
        if (!Events.EventText.TryParse(typeName, out EventType eventType))
        {
            return new Message(MessageType.Error, "Invalid event type");
        }

        if (!code.TryGetUIntLimited('D', 0, 255, out uint deviceNumber))
        {
            return new Message(MessageType.Error, "Missing device number");
        }
        uint param = code.GetUInt('P', 0);
        uint boardAddress = code.GetUInt('B', CanId.MasterAddress);
        code.TryGetString('S', out string? text);

        Events.MachineEvent machineEvent = new(eventType, (ushort)param, (byte)boardAddress, (byte)deviceNumber, text ?? string.Empty);
        return events.Raise(machineEvent)
            ? new Message()
            : new Message(MessageType.Warning, "a similar event is already queued");
    }

    /// <summary>
    /// M997: update the firmware
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleFirmwareUpdateAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.GetIntArray('S', [0]).Contains(0) && code.GetInt('B', 0) == 0)
        {
            // Get the IAP and Firmware files
            string? iapFile, firmwareFile;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                if (model.Boards.Count == 0)
                {
                    return new Message(MessageType.Error, "No boards have been detected");
                }

                // There are now two different IAP binaries, check which one to use
                iapFile = model.Boards[0].IapFileNameSBC;
                if (!code.TryGetString('P', out firmwareFile))
                {
                    firmwareFile = model.Boards[0].FirmwareFileName;
                }
            }

            if (string.IsNullOrEmpty(iapFile) || string.IsNullOrEmpty(firmwareFile))
            {
                return new Message(MessageType.Error, "Cannot update firmware because IAP and firmware filenames are unknown");
            }

            string physicalIapFile = await filePathResolver.ToPhysicalAsync(iapFile, FileDirectory.Firmware, cancellationToken);
            if (!File.Exists(physicalIapFile))
            {
                string fallbackIapFile = await filePathResolver.ToPhysicalAsync($"0:/firmware/{iapFile}", cancellationToken: cancellationToken);
                if (!File.Exists(fallbackIapFile))
                {
                    fallbackIapFile = await filePathResolver.ToPhysicalAsync(iapFile, FileDirectory.System, cancellationToken);
                    if (!File.Exists(fallbackIapFile))
                    {
                        return new Message(MessageType.Error, $"Failed to find IAP file {iapFile}");
                    }
                }
                logger.LogWarning("Using fallback IAP file {File}", fallbackIapFile);
                physicalIapFile = fallbackIapFile;
            }

            string physicalFirmwareFile = await filePathResolver.ToPhysicalAsync(firmwareFile, FileDirectory.Firmware, cancellationToken);
            if (!File.Exists(physicalFirmwareFile))
            {
                string fallbackFirmwareFile = await filePathResolver.ToPhysicalAsync($"0:/firmware/{firmwareFile}", cancellationToken: cancellationToken);
                if (!File.Exists(fallbackFirmwareFile))
                {
                    fallbackFirmwareFile = await filePathResolver.ToPhysicalAsync(firmwareFile, FileDirectory.System, cancellationToken);
                    if (!File.Exists(fallbackFirmwareFile))
                    {
                        return new Message(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                    }
                }
                logger.LogWarning("Using fallback firmware file {File}", fallbackFirmwareFile);
                physicalFirmwareFile = fallbackFirmwareFile;
            }

            // Stop all the plugins
            Commands.StopPlugins stopCommand = commandFactory.Create<Commands.StopPlugins>();
            await stopCommand.ExecuteAsync(cancellationToken);

            // Update the firmware
            await using FileStream iapStream = new(physicalIapFile, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
            await using FileStream firmwareStream = new(physicalFirmwareFile, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
            if (Path.GetExtension(firmwareFile) == ".uf2")
            {
                await using MemoryStream unpackedFirmwareStream = await Firmware.UnpackUF2Async(firmwareStream);
                await linkInterface.UpdateFirmware(iapStream, unpackedFirmwareStream, lifetime.ApplicationStopped);
            }
            else
            {
                await linkInterface.UpdateFirmware(iapStream, firmwareStream, lifetime.ApplicationStopped);
            }

            // Updating the firmware resets the controller, which invalidates every channel and cancels
            // this very code. Reassign its cancellation token so it can report success instead of cancelled
            code.ResetCancellationToken();

            // Terminate the program once this code has finished. Give the success response a
            // moment to propagate through DWS to the clients first - stopping immediately tears
            // down the IPC connections, which lets the reply race against the shutdown
            _ = code.Task.ContinueWith(async task =>
            {
                await task;
                await Task.Delay(1000);
                lifetime.StopApplication();
            }, TaskContinuationOptions.RunContinuationsAsynchronously);

            // Done
            return new Message();
        }
        // TODO this used to fallthrough to RRF
        return new Message(MessageType.Warning, "Not implemented");
    }

    /// <summary>
    /// M999: reset the controller
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleResetAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.Parameters.Count == 0)
        {
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
            {
                // Wait for potential firmware updates to complete first
                await linkInterface.WaitForUpdateAsync();

                // Perform firmware reset but don't wait longer than 4.5s
                Task resetTask = linkInterface.ResetFirmwareAsync(lifetime.ApplicationStopping);
                Task completedTask = await Task.WhenAny(resetTask, Task.Delay(4500, lifetime.ApplicationStopped));
                if (resetTask != completedTask)
                {
                    // Reset timed out, stop this program
                    lifetime.StopApplication();
                    return new Message(MessageType.Error, "Reset timed out, stopping DCS");
                }

                // Terminate the program once this code has finished. Give the success response a
                // moment to propagate through DWS to the clients first - stopping immediately tears
                // down the IPC connections, which lets the reply race against the shutdown
                _ = code.Task.ContinueWith(async task =>
                {
                    await task;
                    await Task.Delay(1000);
                    lifetime.StopApplication();
                }, TaskContinuationOptions.RunContinuationsAsynchronously);

                // Firmware reset
                return new Message();
            }
            throw new OperationCanceledException();
        }
        // TODO this used to fallthrough to RRF
        return new Message(MessageType.Warning, "Not implemented");
    }

    /// <summary>
    /// React to an executed M-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
    public async ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.Result is null || code.Result.Type != MessageType.Success)
        {
            return;
        }

        // Nothing is left to do here: every job transition happens inside the handler that asked
        // for it, so that start.g, resume.g and stop.g run before the code returns
        await ValueTask.CompletedTask;
    }
}
