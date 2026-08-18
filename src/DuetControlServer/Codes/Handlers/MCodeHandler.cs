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
using DuetControlServer.Link.Protocol.Shared;
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
/// <param name="jobProcessor">Job processor</param>
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
    JobProcessor jobProcessor,
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
    /// dispatched from the same switch as everything else; only their bodies live elsewhere
    /// </para>
    /// </remarks>
    public async ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.IsFromFileChannel && jobProcessor.IsSimulating && code.MajorNumber is not 0 and not 1 and not 2)
        {
            // Ignore most M-codes from files in simulation mode...
            return new Message();
        }

        // Keep numerically ordered (where possible) for easier maintenance.
        return code.MajorNumber switch
        {
            // Stop or unconditional stop, sleep or conditional stop, program end
            0 or 1 or 2 => await HandleStopAsync(code, cancellationToken),
            // Spindle clockwise / laser power
            3 => await HandleSpindleOnAsync(code, reverse: false, cancellationToken),
            // Spindle counter-clockwise
            4 => await HandleSpindleOnAsync(code, reverse: true, cancellationToken),
            // Spindle off
            5 => await HandleSpindleOffAsync(code, cancellationToken),
            // Motors on / motors off
            17 or 18 or 84 => await HandleDriverStateAsync(code, cancellationToken),
            // List SD card
            20 => await HandleListFilesAsync(code, cancellationToken),
            // Initialize SD card
            21 => await HandleInitializeSDCardAsync(code, cancellationToken),
            // Release SD card
            22 => throw new NotSupportedException(),
            // Select a file to print, or select it and start printing
            23 or 32 => await HandleSelectFileAsync(code, cancellationToken),
            // Resume a file print
            24 => await HandleResumePrintAsync(code, cancellationToken),
            // Pause the print
            25 => await HandlePausePrintAsync(code, cancellationToken),
            // Set SD position
            26 => await HandleSetFilePositionAsync(code, cancellationToken),
            // Report SD print status
            27 => await HandleReportPrintStatusAsync(code, cancellationToken),
            // Begin write to SD card
            28 => await HandleBeginFileWriteAsync(code, cancellationToken),
            // End write to SD card
            29 => await HandleEndFileWriteAsync(code, cancellationToken),
            // Delete a file on the SD card
            30 => await HandleDeleteFileAsync(code, cancellationToken),
            // Return file information
            36 => await HandleFileInfoAsync(code, cancellationToken),
            // Simulate file
            37 => await HandleSimulateFileAsync(code, cancellationToken),
            // Compute CRC32 checksum of target file
            38 => await HandleFileChecksumAsync(code, cancellationToken),
            // Report SD card information
            39 => await HandleSDCardInfoAsync(code, cancellationToken),
            // Set output pin
            42 => await HandleSetOutputAsync(code, cancellationToken),
            // Absolute / relative extruder positioning
            82 or 83 => await HandleExtruderPositioningAsync(code, cancellationToken),
            // Set the idle timeout
            85 => await HandleIdleTimeoutAsync(code, cancellationToken),
            // Set steps per mm
            92 => await HandleStepsPerMmAsync(code, cancellationToken),
            // Flag current macro file as (not) pausable
            98 => await HandleMacroPausableAsync(code, cancellationToken),
            // Set extruder temperature without waiting
            104 => await SetTemperaturesAsync(code, await CurrentToolHeatersAsync(code, cancellationToken), wait: false, cancellationToken),
            // Report temperatures
            105 => await ReportTemperaturesAsync(cancellationToken),
            // Set fan speed
            106 => await HandleFanSpeedAsync(code, cancellationToken),
            // Fan off
            107 => await HandleFanOffAsync(code, cancellationToken),
            // Set extruder temperature and wait
            109 => await SetTemperaturesAsync(code, await CurrentToolHeatersAsync(code, cancellationToken), wait: true, cancellationToken),
            // Set debug level
            111 => await HandleDebugLevelAsync(code, cancellationToken),
            // Emergency stop
            112 => await HandleEmergencyStopAsync(code, cancellationToken),
            // Report the current position
            114 => await HandleReportPositionAsync(code, cancellationToken),
            // Report firmware version
            115 => await HandleFirmwareVersionAsync(code, cancellationToken),
            // Wait for temperatures
            116 => await HandleWaitForTemperaturesAsync(code, cancellationToken),
            // Publish MQTT message
            118 => await HandlePublishMqttAsync(code, cancellationToken),
            // Report the endstop states
            119 => await HandleReportEndstopsAsync(code, cancellationToken),
            // Push and pop the interpreter state
            120 or 121 => await HandleStateStackAsync(code, cancellationToken),
            // Immediate DSF diagnostics
            122 => await HandleDiagnosticsAsync(code, cancellationToken),
            // Set bed temperature without waiting
            140 => await SetTemperaturesAsync(code, await BedOrChamberHeatersAsync(code, chamber: false, cancellationToken), wait: false, cancellationToken),
            // Set chamber temperature without waiting
            141 => await SetTemperaturesAsync(code, await BedOrChamberHeatersAsync(code, chamber: true, cancellationToken), wait: false, cancellationToken),
            // Heater monitors
            143 => await HandleHeaterMonitorAsync(code, cancellationToken),
            // Set bed temperature and wait
            190 => await SetTemperaturesAsync(code, await BedOrChamberHeatersAsync(code, chamber: false, cancellationToken), wait: true, cancellationToken),
            // Set chamber temperature and wait
            191 => await SetTemperaturesAsync(code, await BedOrChamberHeatersAsync(code, chamber: true, cancellationToken), wait: true, cancellationToken),
            // Set axis and extruder accelerations
            201 => await HandleAccelerationsAsync(code, cancellationToken),
            // Set maximum feedrates
            203 => await HandleMaxFeedratesAsync(code, cancellationToken),
            // Set printing and travel accelerations
            204 => await HandleMoveAccelerationsAsync(code, cancellationToken),
            // Set jerk, in mm/sec (M205) or mm/min (M566)
            205 or 566 => await HandleJerkAsync(code, cancellationToken),
            // Set axis limits
            208 => await HandleAxisLimitsAsync(code, cancellationToken),
            // Set the speed factor
            220 => await HandleSpeedFactorAsync(code, cancellationToken),
            // Synchronous pause, filament change pause, Prusa-style pause
            226 or 600 or 601 => await HandleSynchronousPauseAsync(code, cancellationToken),
            // Set the extrusion factor
            221 => await HandleExtrusionFactorAsync(code, cancellationToken),
            // Servo control
            280 => await HandleServoAsync(code, cancellationToken),
            // Babystepping
            290 => await HandleBabysteppingAsync(code, cancellationToken),
            // Cold extrude and retract limits
            302 => await HandleColdExtrusionAsync(code, cancellationToken),
            // Heater process model
            307 => await HandleHeaterModelAsync(code, cancellationToken),
            // Configure a temperature sensor
            308 => await HandleConfigureSensorAsync(code, cancellationToken),
            // Set microstepping
            350 => await HandleMicrosteppingAsync(code, cancellationToken),
            // Save and load the height map, and set the compensation taper
            374 => await HandleSaveHeightMapAsync(code, cancellationToken),
            375 => await HandleLoadHeightMapAsync(code, cancellationToken),
            376 => await HandleTaperHeightAsync(code, cancellationToken),
            // Wait for the current moves to finish
            400 => await HandleWaitForMovesAsync(code, cancellationToken),
            // Deploy and retract the Z probe
            401 => await HandleDeployProbeAsync(code, cancellationToken),
            402 => await HandleRetractProbeAsync(code, cancellationToken),
            // Query object model
            409 => await HandleQueryObjectModelAsync(code, cancellationToken),
            // Backlash compensation
            425 => await HandleBacklashAsync(code, cancellationToken),
            // Report the machine mode
            450 => await HandleReportMachineModeAsync(cancellationToken),
            // Select FFF mode
            451 => await HandleSetMachineModeAsync(code, MachineMode.FFF, cancellationToken),
            // Select laser mode
            452 => await HandleSetMachineModeAsync(code, MachineMode.Laser, cancellationToken),
            // Select CNC mode
            453 => await HandleSetMachineModeAsync(code, MachineMode.CNC, cancellationToken),
            // Create directory on SD card
            470 => await HandleCreateDirectoryAsync(code, cancellationToken),
            // Rename file or directory on SD card
            471 => await HandleRenameFileAsync(code, cancellationToken),
            // Delete file or directory
            472 => await HandleDeleteFileOrDirectoryAsync(code, cancellationToken),
            // Save parameters to config-override.g
            500 => await HandleSaveConfigOverrideAsync(code, cancellationToken),
            // Load parameters from config-override.g
            501 => await HandleLoadConfigOverrideAsync(code, cancellationToken),
            // Print settings
            503 => await HandlePrintSettingsAsync(code, cancellationToken),
            // Set configuration file folder
            505 => await HandleSetFolderAsync(code, cancellationToken),
            // Set machine name
            550 => await HandleSetNameAsync(code, cancellationToken),
            // Set password
            551 => await HandleSetPasswordAsync(code, cancellationToken),
            // Set IP address
            552 => await HandleSetIPAddressAsync(code, cancellationToken),
            // Axis compensation
            556 => await HandleAxisCompensationAsync(code, cancellationToken),
            // Define the mesh compensation grid
            557 => await HandleProbeGridAsync(code, cancellationToken),
            // Configure a Z probe
            558 => await HandleProbeConfigAsync(code, cancellationToken),
            // Stop applying bed compensation
            561 => await HandleClearCompensationAsync(code, cancellationToken),
            // Clear a heater fault
            562 => await HandleClearHeaterFaultAsync(code, cancellationToken),
            // Define or delete a tool
            563 => await HandleDefineToolAsync(code, cancellationToken),
            // Limit axes and movement before homing
            564 => await HandleMovementLimitsAsync(code, cancellationToken),
            // Set the mixing ratios of a tool
            567 => await HandleMixRatiosAsync(code, cancellationToken),
            // Tool settings
            568 => await HandleToolSettingsAsync(code, cancellationToken),
            // Configure a stepper driver
            569 => await HandleDriverConfigAsync(code, cancellationToken),
            // Heater fault detection
            570 => await HandleHeaterFaultDetectionAsync(code, cancellationToken),
            // Set pressure advance
            572 => await HandlePressureAdvanceAsync(code, cancellationToken),
            // Configure the endstops
            574 => await HandleEndstopConfigAsync(code, cancellationToken),
            // Wait for an endstop or input to reach a state
            577 => await HandleWaitForInputAsync(code, cancellationToken),
            // Configure external trigger
            581 => await HandleConfigureTriggerAsync(code, cancellationToken),
            // Map axes and extruders onto stepper drivers
            584 => await HandleDriveMappingAsync(code, cancellationToken),
            // Configure network protocols
            586 => await HandleNetworkProtocolsAsync(code, cancellationToken),
            // Configure nonlinear extrusion
            592 => await HandleNonlinearExtrusionAsync(code, cancellationToken),
            // Configure input shaping
            593 => await HandleInputShapingAsync(code, cancellationToken),
            // Fork input reader
            606 => await HandleForkInputReaderAsync(code, cancellationToken),
            // Delta configuration and delta endstop adjustments
            665 or 666 => await HandleKinematicsAsync(code, cancellationToken),
            // Retired in RepRapFirmware in favour of M669
            667 => new Message(MessageType.Error, "M667 is no longer supported - use M669 instead"),
            // Select the kinematics and configure them
            669 => await HandleKinematicsAsync(code, cancellationToken),
            // Z leadscrew positions
            671 => await HandleLeadscrewsAsync(code, cancellationToken),
            // Z probe offset, for Marlin compatibility
            851 => await HandleProbeOffsetAsync(code, cancellationToken),
            // Set motor currents, current percentage and standstill current percentage
            906 or 913 or 917 => await HandleMotorCurrentsAsync(code, cancellationToken),
            // Configure stall detection
            915 => await HandleStallDetectionAsync(code, cancellationToken),
            // Start/stop event logging to SD card
            929 => await HandleEventLoggingAsync(code, cancellationToken),
            // Create a heater, fan or other I/O device
            950 => await HandleCreateDeviceAsync(code, cancellationToken),
            // Configure CAN
            952 => await HandleConfigureCanAsync(code, cancellationToken),
            // Enable CAN
            953 => await HandleEnableCanAsync(code, cancellationToken),
            // Configure phase stepping
            970 => await HandlePhaseSteppingAsync(code, cancellationToken),
            // Raise an event
            957 => HandleRaiseEvent(code),
            // Update the firmware
            997 => await HandleFirmwareUpdateAsync(code, cancellationToken),
            // Request resend of line
            998 => throw new NotSupportedException(),
            // Reset controller
            999 => await HandleResetAsync(code, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported code '{code}'")
        };
    }

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

            // How the job ended decides which macro runs. A stop from inside the job file is the job
            // reaching its end; a stop from anywhere else is the operator cancelling one that has
            // already been paused, which is the only state RepRapFirmware allows it from
            PrintStoppedReason? reason = null;
            using (await jobProcessor.LockAsync(cancellationToken))
            {
                if (jobProcessor.IsFileSelected)
                {
                    if (!code.IsFromFileChannel && !jobProcessor.IsPaused)
                    {
                        return new Message(MessageType.Error, "Pause the print before attempting to cancel it");
                    }

                    reason = code.IsFromFileChannel ? PrintStoppedReason.NormalCompletion
                                                    : PrintStoppedReason.UserCancelled;

                    // Invalidate the print file and make sure no more codes are read from it
                    jobProcessor.Cancel();
                }
            }

            // Cancelling the job cancels this code with it, so give it a fresh token to report on
            if (code.IsFromFileChannel)
            {
                code.ResetCancellationToken();
            }

            if (reason is not null)
            {
                await jobProcessor.StopAsync(code.Channel, reason.Value, cancellationToken);
            }
            else
            {
                // No job to stop, so M0/M1/M2 is the machine being put down for the night. RRF runs
                // stop.g here too, through the same state
                await jobProcessor.StopAsync(code.Channel, PrintStoppedReason.NormalCompletion, cancellationToken);
            }
            return new Message();
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
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M21: initialize the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleInitializeSDCardAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            if (code.GetInt('P', 0) == 0)
            {
                // M21 (P0) will always work because it's always mounted
                return new Message();
            }
            throw new NotSupportedException();
        }
        throw new OperationCanceledException();
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

                using (await jobProcessor.LockAsync(cancellationToken))
                {
                    if (!code.IsFromFileChannel && (jobProcessor.IsProcessing || jobProcessor.IsPausedOrChanging))
                    {
                        return new Message(MessageType.Error, "Cannot set file to print, because a file is already being printed");
                    }
                    await jobProcessor.SelectFileAsync(fileName, physicalFile, false, cancellationToken);
                }

                // M32 starts what it selected; M23 only selects. Starting goes through the same call
                // M24 makes, so start.g runs for both of them
                if (code.MajorNumber == 32)
                {
                    return await jobProcessor.ResumeAsync(code.Channel, runMacro: true, cancellationToken);
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
        if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            if (code.Channel == CodeChannel.File2)
            {
                return new Message();
            }

            // P0 skips resume.g, as it does in RepRapFirmware
            bool runMacro = code.GetInt('P', 1) != 0;
            return await jobProcessor.ResumeAsync(code.Channel, runMacro, cancellationToken);
        }
        throw new OperationCanceledException();
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
    /// TODO RepRapFirmware defers a pause requested while the job is inside a macro that cannot be
    /// restarted, by stashing an M226 and injecting it once the job is back out - see
    /// <c>deferredPauseCommandPending</c> and JOB_LIFECYCLE.md phase 5. Until that lands the pause
    /// happens immediately, macro or no macro
    /// </para>
    /// </remarks>
    private async ValueTask<Message> HandlePausePrintAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.MinorNumber > 1)
        {
            return new Message(MessageType.Error, $"M25.{code.MinorNumber} is not supported");
        }

        // M25.1 asks for the rapid pause: rather than letting the queued moves run, the engine plans
        // a deceleration at the first move it has not committed and drops the rest. Everything past
        // the stop is shared with M25 - the same pause.g, the same restore point, the same reason
        bool feedhold = code.MinorNumber == 1;

        if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
        {
            if (code.Channel == CodeChannel.File2)
            {
                return new Message();
            }

            // A job inside a macro that has not said it can be restarted must not be interrupted
            // part-way: the macro would be abandoned with no way to put back what it had already
            // done. RepRapFirmware stashes the request and injects it once the job is back out
            if (!code.IsFromFileChannel && jobProcessor.IsProcessing &&
                codeProcessor.IsDoingMacro(CodeChannel.File) && !codeProcessor.CanRestartMacros(CodeChannel.File))
            {
                return jobProcessor.TryDeferPause(PauseMacro.Pause)
                       ? new Message()
                       : new Message(MessageType.Warning, "Pausing is already pending");
            }

            return await jobProcessor.PauseAsync(code.Channel, PrintPausedReason.User, PauseMacro.Pause,
                                                 synchronous: code.IsFromFileChannel, feedhold: feedhold,
                                                 reportPosition: true,
                                                 pausingCode: code.IsFromFileChannel ? code : null,
                                                 cancellationToken);
        }
        throw new OperationCanceledException();
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
            PauseMacro macro = filamentChange ? PauseMacro.FilamentChange
                               : code.GetInt('P', 1) == 0 ? PauseMacro.None
                               : PauseMacro.Pause;
            PrintPausedReason reason = filamentChange ? PrintPausedReason.FilamentChange : PrintPausedReason.GCode;

            // Inside a macro that cannot be restarted, the pause waits until the job is back out of
            // it, exactly as an M25 from elsewhere does
            if (codeProcessor.IsDoingMacro(code.Channel) && !codeProcessor.CanRestartMacros(code.Channel))
            {
                jobProcessor.TryDeferPause(macro);
                return new Message();
            }

            return await jobProcessor.PauseAsync(code.Channel, reason, macro,
                                                 synchronous: true, feedhold: false, reportPosition: true,
                                                 pausingCode: code, cancellationToken);
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
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            // Wait for inputs[].motionSystem to be up-to-date
            await model.WaitForFullUpdateAsync(cancellationToken);

            int motionSystem;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                motionSystem = model.Inputs[code.Channel]?.MotionSystem ?? 0;
            }

            using (await jobProcessor.LockAsync(cancellationToken))
            {
                if (!jobProcessor.IsFileSelected)
                {
                    return new Message(MessageType.Error, "Not printing a file");
                }

                if (code.TryGetLong('S', out long newPosition))
                {
                    if (newPosition < 0L || newPosition > jobProcessor.FileLength)
                    {
                        return new Message(MessageType.Error, "Position is out of range");
                    }

                    await jobProcessor.SetFilePositionAsync(motionSystem, newPosition, cancellationToken);
                }
            }

            // TODO handle P parameter if present (used to be handled by RRF)
            return new Message(MessageType.Warning, "Not implemented yet");
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M27: report the SD print status
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleReportPrintStatusAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            // Wait for inputs[].motionSystem to be up-to-date
            await model.WaitForFullUpdateAsync(cancellationToken);
            int motionSystem;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                motionSystem = model.Inputs[code.Channel]?.MotionSystem ?? 0;
            }

            using (await jobProcessor.LockAsync(cancellationToken))
            {
                if (jobProcessor.IsFileSelected)
                {
                    long filePosition = await jobProcessor.GetFilePositionAsync(motionSystem, cancellationToken);
                    return new Message(MessageType.Success, $"SD printing byte {filePosition}/{jobProcessor.FileLength}");
                }
                return new Message(MessageType.Success, "Not SD printing.");
            }
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M28: begin writing to a file on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleBeginFileWriteAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M29: finish writing to a file on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleEndFileWriteAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M30: delete a file on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleDeleteFileAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M36: return information about a G-code file, a thumbnail in it, or a fragment of it
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleFileInfoAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            if (code.Parameters.Count > 0)
            {
                string virtualFilename = string.Empty;
                try
                {
                    if (code.MinorNumber <= 0)
                    {
                        // Get fileinfo
                        virtualFilename = code.GetUnprecedentedString();
                        string physicalFilename = await filePathResolver.ToPhysicalAsync(virtualFilename, FileDirectory.GCodes, cancellationToken);
                        GCodeFileInfo info = await fileInfoParser.ParseAsync(physicalFilename, false, cancellationToken);

                        string json = JsonSerializer.Serialize(info, ObjectModelContext.Default.GCodeFileInfo);
                        return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":0," : "{\"err\":0,") + json[1..]);
                    }
                    else if (code.MinorNumber == 1 || code.MinorNumber == 2)
                    {
                        // Get thumbnail or file fragment
                        virtualFilename = code.GetString('P');
                        string physicalFilename = await filePathResolver.ToPhysicalAsync(virtualFilename, FileDirectory.GCodes, cancellationToken);

                        string json = await fileInfoParser.ParseFileFragment(physicalFilename, code.GetLong('S'), code.MinorNumber == 1, code.ExplicitLineNumber);
                        return new Message(MessageType.Success, json);
                    }
                    else
                    {
                        throw new NotSupportedException();
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
        throw new OperationCanceledException();
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

                using (await jobProcessor.LockAsync(cancellationToken))
                {
                    if (!code.IsFromFileChannel && (jobProcessor.IsProcessing || jobProcessor.IsPausedOrChanging))
                    {
                        return new Message(MessageType.Error, "Cannot set file to simulate, because a file is already being printed");
                    }

                    await jobProcessor.SelectFileAsync(fileName, physicalFile, true, cancellationToken);
                    // F0 suppresses writing the simulated time back to the file; absent or F1 updates it, as in standalone mode
                    jobProcessor.UpdateSimulatedTime = code.GetInt('F', 1) == 1;
                    // Simulation is started when M37 has been processed by the firmware
                }
            }

            return new Message();
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M38: compute the CRC32 checksum of a file
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleFileChecksumAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M39: report information about an SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSDCardInfoAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
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
            if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
            throw new OperationCanceledException();
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
            // model now and it is this one, so every key is answered here

            // Wait until pending codes have finished
            if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
            {
                throw new OperationCanceledException();
            }

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
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M471: rename a file or directory on the SD card
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleRenameFileAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M472: delete a file or directory
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleDeleteFileOrDirectoryAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
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
        if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }

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
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M505: set the system folder, or with M505.1 the web folder
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetFolderAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            if (code.TryGetString('P', out string? directory))
            {
                // Changing the system folder under a running job would change which macros a queued
                // move's callbacks resolve to, so wait for the machine to stop first
                if (await planner.WaitForStandstillAsync(cancellationToken))
                {
                    string physicalDirectory = (code.MinorNumber != 1)
                        ? await filePathResolver.ToPhysicalAsync(directory, "sys", cancellationToken)
                        : await filePathResolver.ToPhysicalAsync(directory, "www", cancellationToken);
                    if (Directory.Exists(physicalDirectory))
                    {
                        string virtualDirectory = await filePathResolver.ToVirtualAsync(physicalDirectory, cancellationToken);
                        using (await model.AccessReadWriteAsync(cancellationToken))
                        {
                            if (code.MinorNumber != 1)
                            {
                                model.Directories.System = virtualDirectory;
                            }
                            else
                            {
                                model.Directories.Web = virtualDirectory;
                            }
                        }
                        return new Message();
                    }
                }
                return new Message(MessageType.Error, "Directory not found");
            }

            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                return new Message(MessageType.Success, $"{((code.MinorNumber != 1) ? "Sys" : "HTTP")} file path is {((code.MinorNumber != 1) ? model.Directories.System : model.Directories.Web)}");
            }
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M550: set the machine name
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetNameAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M551: set the password
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetPasswordAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M552: set the IP address, which is the SBC's business rather than the firmware's
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleSetIPAddressAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            return new Message(MessageType.Error, "M552 is reserved for SBC mode");
        }
        throw new OperationCanceledException();
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
    private async ValueTask<Message> HandleConfigureTriggerAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.MinorNumber == 1)
        {
            if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
            {
                Message? result = await sbcTriggerService.ConfigureAsync(code, cancellationToken);
                if (result != null)
                {
                    // Expression was handled by SbcTriggerService (contains SBC fields)
                    return result;
                }
                // No SBC fields in the expression — let RRF handle M581.1 natively
                // TODO this used to fallthrough to RRF
                return new Message(MessageType.Warning, "Not implemented");
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
    private async ValueTask<Message> HandleNetworkProtocolsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
        {
            // Configure MQTT
            if (code.MinorNumber == 4)
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
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M606: fork the input reader
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleForkInputReaderAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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

                // Try to fork the file and report an error if anything went wrong
                using (await jobProcessor.LockAsync(cancellationToken))
                {
                    Message result = await jobProcessor.ForkAsync(cancellationToken);
                    if (result.Type != MessageType.Success)
                    {
                        return result;
                    }
                }
            }

            return new Message();
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// M929: start or stop event logging
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result, or null to let the code carry on</returns>
    private async ValueTask<Message> HandleEventLoggingAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
        throw new OperationCanceledException();
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
            if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
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
            throw new OperationCanceledException();
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

        switch (code.MajorNumber)
        {
            // Stop or unconditional stop, sleep or conditional stop
            // Simulate file
            // M24 and M32 no longer appear here: starting and resuming a job is what
            // JobProcessor.ResumeAsync does, and it has to happen before the code returns so that
            // start.g and resume.g run inside it
            case 0:
            case 1:
            case 37:
                using (await jobProcessor.LockAsync(cancellationToken))
                {
                    // Start reading from the job file, or finish the cancellation process
                    jobProcessor.Resume();
                }
                break;

            // Fork input reader
            case 606:
                if (code.TryGetInt('S', out int sParam) && sParam == 1)
                {
                    using (await jobProcessor.LockAsync(cancellationToken))
                    {
                        jobProcessor.StartSecondJob();
                    }
                }
                break;
        }
    }
}
