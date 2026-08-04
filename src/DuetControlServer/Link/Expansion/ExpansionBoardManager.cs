using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DuetControlServer.Link.Expansion;

/// <summary>
/// Turns the status reports the expansion boards broadcast into object model state
/// </summary>
/// <remarks>
/// <para>
/// The boards report on their own initiative: an announcement when one starts or regains time sync,
/// then a periodic board status report and whichever of the driver, sensor, heater, fan, input and
/// filament monitor reports apply to what it is carrying. Nothing here asks for any of it, so this is
/// a receiver rather than a poller, and the object model is the only place the information goes.
/// </para>
/// <para>
/// Reports arrive on the link dispatch thread, which also carries move completions and message
/// output, so nothing is decoded there. Messages are queued as raw payloads and applied on this
/// service's own task, which is also what keeps the object model write lock off the dispatch thread.
/// The queue is bounded and drops the oldest report when it fills: status reports are periodic, so a
/// stale one is worth less than the newest, and blocking the dispatch thread to keep it would stall
/// the link.
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="logger">Logger</param>
internal sealed class ExpansionBoardManager(Model.ObjectModel model, ILogger<ExpansionBoardManager> logger) : BackgroundService
{
    /// <summary>
    /// How many reports may be waiting before the oldest is dropped
    /// </summary>
    private const int QueueSize = 256;

    /// <summary>
    /// A report as it came off the bus, decoded later on this service's own task
    /// </summary>
    /// <param name="Type">CAN message type</param>
    /// <param name="Source">CAN address that sent it</param>
    /// <param name="Payload">Raw message payload</param>
    private readonly record struct Report(CanMessageType Type, byte Source, byte[] Payload);

    private readonly Channel<Report> _reports = Channel.CreateBounded<Report>(new BoundedChannelOptions(QueueSize)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    /// <summary>
    /// Take a report broadcast by an expansion board
    /// </summary>
    /// <param name="type">CAN message type</param>
    /// <param name="source">CAN address that sent it</param>
    /// <param name="payload">Raw message payload</param>
    /// <returns>True if this manager consumes the message type</returns>
    /// <remarks>
    /// Called from the link dispatch thread, so this does no more than recognise the type and queue
    /// the bytes
    /// </remarks>
    public bool TryEnqueue(CanMessageType type, byte source, byte[] payload)
    {
        switch (type)
        {
            case CanMessageType.AnnounceV0:
            case CanMessageType.AnnounceV1:
            case CanMessageType.BoardStatusReportV0:
            case CanMessageType.BoardStatusReportV1:
            case CanMessageType.DriversStatusReport:
            case CanMessageType.SensorTemperaturesReport:
            case CanMessageType.HeatersStatusReport:
            case CanMessageType.FansReport:
            case CanMessageType.InputStateChangedV1:
            case CanMessageType.InputStateChangedV2:
            case CanMessageType.FilamentMonitorsStatusReportV2:
            case CanMessageType.Event:
            case CanMessageType.DebugText:
                break;

            default:
                return false;
        }

        if (!_reports.Writer.TryWrite(new Report(type, source, payload)))
        {
            logger.LogWarning("Dropped a {Type} report from board {Source}", type, source);
        }
        return true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (Report report in _reports.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ApplyAsync(report, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // One malformed report must not take the receiver down; the next one supersedes it
                logger.LogError(e, "Failed to apply a {Type} report from board {Source}", report.Type, report.Source);
            }
        }
    }

    /// <summary>
    /// Apply one report to the object model
    /// </summary>
    /// <param name="report">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyAsync(Report report, CancellationToken cancellationToken)
    {
        switch (report.Type)
        {
            case CanMessageType.AnnounceV0:
                await ApplyAnnouncementAsync(report.Source,
                    CanMessageSerializer.Deserialize<CanMessageAnnounceV0>(report.Payload).BoardTypeAndFirmwareVersionString,
                    numDrivers: null, uniqueId: null, cancellationToken);
                break;

            case CanMessageType.AnnounceV1:
                {
                    CanMessageAnnounceV1 announce = CanMessageSerializer.Deserialize<CanMessageAnnounceV1>(report.Payload);
                    await ApplyAnnouncementAsync(report.Source, announce.BoardTypeAndFirmwareVersionString,
                                                 announce.NumDrivers, FormatUniqueId(announce.UniqueId), cancellationToken);
                }
                break;

            case CanMessageType.BoardStatusReportV0:
                await ApplyBoardStatusAsync(report.Source, CanMessageSerializer.Deserialize<CanMessageBoardStatusV0>(report.Payload), cancellationToken);
                break;

            case CanMessageType.BoardStatusReportV1:
                await ApplyBoardStatusAsync(report.Source, CanMessageSerializer.Deserialize<CanMessageBoardStatusV1>(report.Payload),
                                            report.Payload, cancellationToken);
                break;

            case CanMessageType.DriversStatusReport:
                await ApplyDriversStatusAsync(report.Source, CanMessageSerializer.Deserialize<CanMessageDriversStatus>(report.Payload), cancellationToken);
                break;

            case CanMessageType.SensorTemperaturesReport:
                await ApplySensorTemperaturesAsync(CanMessageSerializer.Deserialize<CanMessageSensorTemperatures>(report.Payload), cancellationToken);
                break;

            case CanMessageType.HeatersStatusReport:
                await ApplyHeatersStatusAsync(CanMessageSerializer.Deserialize<CanMessageHeatersStatus>(report.Payload), cancellationToken);
                break;

            case CanMessageType.FansReport:
                await ApplyFansReportAsync(CanMessageSerializer.Deserialize<CanMessageFansReport>(report.Payload), cancellationToken);
                break;

            case CanMessageType.InputStateChangedV1:
                {
                    // V1 and V2 differ in the size of their per-handle entries, so each has to be
                    // read as itself; reading one as the other silently shifts every handle
                    CanMessageInputChangedV1 changed = CanMessageSerializer.Deserialize<CanMessageInputChangedV1>(report.Payload);
                    await ApplyInputChangedAsync(changed.States, changed.NumHandles, changed.GetEntryHandle, cancellationToken);
                }
                break;

            case CanMessageType.InputStateChangedV2:
                {
                    CanMessageInputChangedV2 changed = CanMessageSerializer.Deserialize<CanMessageInputChangedV2>(report.Payload);
                    await ApplyInputChangedAsync(changed.States, changed.NumHandles, changed.GetEntryHandle, cancellationToken);
                }
                break;

            case CanMessageType.FilamentMonitorsStatusReportV2:
                logger.LogDebug("Filament monitor status from board {Source} is not applied yet: sensors.filamentMonitors[] is keyed by extruder, which needs the filament monitor configuration M591 does not write yet",
                                report.Source);
                break;

            case CanMessageType.Event:
                {
                    CanMessageEvent canEvent = CanMessageSerializer.Deserialize<CanMessageEvent>(report.Payload);
                    logger.LogWarning("Event from board {Source}: type {EventType}, device {Device}, parameter {Param}: {Text}",
                                      report.Source, canEvent.EventType, canEvent.DeviceNumber, canEvent.EventParam, canEvent.TextString);
                }
                break;

            case CanMessageType.DebugText:
                logger.LogDebug("Board {Source}: {Text}", report.Source,
                                CanMessageSerializer.Deserialize<CanMessageDebugText>(report.Payload).TextString);
                break;
        }
    }

    /// <summary>
    /// Record a board that has just announced itself
    /// </summary>
    /// <param name="source">CAN address of the board</param>
    /// <param name="description">Board type, firmware version and firmware date, separated by pipes</param>
    /// <param name="numDrivers">How many drivers it carries, if it said</param>
    /// <param name="uniqueId">Its unique id, if it said</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyAnnouncementAsync(byte source, string description, byte? numDrivers, string? uniqueId,
                                                   CancellationToken cancellationToken)
    {
        // Duet3Expansion sends "<board type>|<firmware version>|<firmware date>"
        string[] parts = description.Split('|');

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Board board = GetOrCreateBoard(source);
            board.ShortName = parts.Length > 0 ? parts[0] : string.Empty;
            board.Name = board.ShortName;
            board.FirmwareVersion = parts.Length > 1 ? parts[1] : string.Empty;
            board.FirmwareDate = parts.Length > 2 ? parts[2] : string.Empty;
            board.State = BoardState.Running;

            if (uniqueId is not null)
            {
                board.UniqueId = uniqueId;
            }

            if (numDrivers is not null)
            {
                board.MaxMotors = numDrivers.Value;
                board.Drivers ??= [];
                while (board.Drivers.Count > numDrivers.Value)
                {
                    board.Drivers.RemoveAt(board.Drivers.Count - 1);
                }
                while (board.Drivers.Count < numDrivers.Value)
                {
                    board.Drivers.Add(new Driver());
                }
            }
        }

        logger.LogInformation("Expansion board {Source} announced itself as {Description}", source, description);
    }

    /// <summary>
    /// Apply a board status report
    /// </summary>
    /// <param name="source">CAN address of the board</param>
    /// <param name="status">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyBoardStatusAsync(byte source, CanMessageBoardStatusV1 status,
                                                  byte[] payload, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Board board = GetOrCreateBoard(source);
            board.State = BoardState.Running;

            // A report carrying a movement delay is reporting that instead of its free memory
            if (!status.HasMovementDelay)
            {
                board.FreeRam = status.NeverUsedRam;
            }

            // The readings are packed in a fixed order and only the present ones take a slot, so they
            // have to be walked in that order rather than indexed by what they are
            int index = 0;
            board.VIn = status.HasVin ? ToMinMaxCurrent(status.ShortValues[index++]) : null;
            board.V12 = status.HasV12 ? ToMinMaxCurrent(status.ShortValues[index++]) : null;
            board.McuTemp = status.HasMcuTemp ? ToMinMaxCurrent(status.ShortValues[index]) : null;

            ApplyAnalogHandles(status, payload);
        }
    }

    /// <summary>
    /// Apply the analog readings a board appends to its status report
    /// </summary>
    /// <param name="status">The report</param>
    /// <param name="payload">The raw report, which is where the readings are</param>
    /// <remarks>
    /// <para>
    /// A board status report is variable length: the packed min/current/max values are followed by
    /// one <see cref="AnalogHandleDataV1"/> per analog input the board is watching. They are not part
    /// of the fixed struct, because where they start depends on how many of Vin, V12 and MCU
    /// temperature that board has - which is why they are read from the payload rather than from a
    /// field.
    /// </para>
    /// <para>
    /// Only Z probes use analog handles, as in RepRapFirmware. This is where an analog or scanning
    /// probe's reading comes from; a digital probe reports a level through
    /// <c>InputStateChanged</c> instead
    /// </para>
    /// </remarks>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private void ApplyAnalogHandles(CanMessageBoardStatusV1 status, byte[] payload)
    {
        int offset = (int)status.GetAnalogHandlesOffset();
        int entrySize = Marshal.SizeOf<AnalogHandleDataV1>();

        for (int i = 0; i < status.NumAnalogHandles && offset + entrySize <= payload.Length; i++)
        {
            AnalogHandleDataV1 data = MemoryMarshal.Read<AnalogHandleDataV1>(payload.AsSpan(offset));
            offset += entrySize;

            if (data.Handle.Type != RemoteInputHandle.TypeZprobe)
            {
                continue;
            }

            Probe? probe = data.Handle.Major < model.Sensors.Probes.Count
                ? model.Sensors.Probes[data.Handle.Major]
                : null;
            if (probe is not null)
            {
                while (probe.Value.Count < 1)
                {
                    probe.Value.Add(0);
                }
                probe.Value[0] = data.Reading;
            }
        }
    }

    /// <summary>
    /// Apply a board status report in the older format
    /// </summary>
    /// <param name="source">CAN address of the board</param>
    /// <param name="status">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyBoardStatusAsync(byte source, CanMessageBoardStatusV0 status, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Board board = GetOrCreateBoard(source);
            board.State = BoardState.Running;

            int index = 0;
            board.VIn = status.HasVin ? ToMinMaxCurrent(status.Values[index++]) : null;
            board.V12 = status.HasV12 ? ToMinMaxCurrent(status.Values[index++]) : null;
            board.McuTemp = status.HasMcuTemp ? ToMinMaxCurrent(status.Values[index]) : null;
        }
    }

    /// <summary>
    /// Apply a driver status report
    /// </summary>
    /// <param name="source">CAN address of the board</param>
    /// <param name="status">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyDriversStatusAsync(byte source, CanMessageDriversStatus status, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Board board = GetOrCreateBoard(source);
            board.Drivers ??= [];

            int reported = Math.Min((int)status.NumDriversReported, OpenLoopStatusArray15.Length);
            while (board.Drivers.Count < reported)
            {
                board.Drivers.Add(new Driver());
            }

            for (int driver = 0; driver < reported; driver++)
            {
                // The closed-loop form carries the same status word plus the tracking data, which has
                // nowhere to go until the closed-loop configuration is ported
                board.Drivers[driver].Status = status.HasClosedLoopData
                    ? status.ClosedLoopData[driver].Status
                    : status.OpenLoopData[driver].Status;
            }
        }
    }

    /// <summary>
    /// Apply a sensor temperature report
    /// </summary>
    /// <param name="report">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplySensorTemperaturesAsync(CanMessageSensorTemperatures report, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            int slot = 0;
            foreach (int sensor in SetBits(report.WhichSensors, CanSensorReportArray11.Length))
            {
                CanSensorReport sensorReport = report.TemperatureReports[slot++];
                AnalogSensor? analogSensor = GetOrCreate(model.Sensors.Analog, sensor, () => new AnalogSensor());
                if (analogSensor is not null)
                {
                    analogSensor.LastReading = sensorReport.GetTemperature();
                    analogSensor.State = (TemperatureError)sensorReport.ErrorCode;
                }
            }
        }
    }

    /// <summary>
    /// Apply a heater status report
    /// </summary>
    /// <param name="report">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyHeatersStatusAsync(CanMessageHeatersStatus report, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            int slot = 0;
            foreach (int heaterNumber in SetBits(report.WhichHeaters, CanHeaterReportArray9.Length))
            {
                CanHeaterReport heaterReport = report.Reports[slot++];
                Heater? heater = GetOrCreate(model.Heat.Heaters, heaterNumber, () => new Heater());
                if (heater is not null)
                {
                    heater.Current = heaterReport.GetTemperature();

                    // The wire value is a PWM duty cycle in 0..255 and the object model carries a fraction
                    heater.AvgPwm = heaterReport.AveragePwm / 255.0f;
                    heater.State = Enum.IsDefined((HeaterState)heaterReport.Mode) ? (HeaterState)heaterReport.Mode : HeaterState.Off;
                }
            }
        }
    }

    /// <summary>
    /// Apply a fan report
    /// </summary>
    /// <param name="report">The report</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask ApplyFansReportAsync(CanMessageFansReport report, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            int slot = 0;
            foreach (int fanNumber in SetBits(report.WhichFans, FanReportArray14.Length))
            {
                FanReport fanReport = report.FanReports[slot++];
                Fan? fan = GetOrCreate(model.Fans, fanNumber, () => new Fan());
                if (fan is not null)
                {
                    fan.ActualValue = fanReport.ActualPwm / 65535.0f;

                    // A negative RPM means the board has no tacho for that fan
                    fan.Rpm = fanReport.Rpm;
                }
            }
        }
    }

    /// <summary>
    /// Apply an input change notification
    /// </summary>
    /// <param name="states">Digital level of each reported handle, one per bit</param>
    /// <param name="numHandles">How many handles the message carries</param>
    /// <param name="getHandle">Reads the n'th handle</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// General-purpose inputs and endstops are applied. A Z probe handle is not: M558 has not been
    /// ported, so nothing has created the probe it would refer to.
    /// <para>
    /// This only records the state. Stopping a move on an endstop is decided by the controller,
    /// which is the only place close enough to the bus for the latency - see section 10 of
    /// docs/devel/MCODE_MIGRATION.md
    /// </para>
    /// </remarks>
    /// <summary>
    /// Reading reported for a triggered digital probe
    /// </summary>
    /// <remarks>
    /// A digital probe has no reading of its own, but <c>sensors.probes[].value</c> is an analog
    /// scale and a client compares it against the threshold. The top of the scale is what
    /// RepRapFirmware reports for a closed digital probe
    /// </remarks>
    private const int MaxProbeReading = 1000;

    /// <summary>
    /// Which switches of each endstop are currently closed, one bit per switch
    /// </summary>
    /// <remarks>
    /// An axis with a switch per driver has several switches under one endstop, and each reports
    /// separately. The object model has one flag for the endstop, so the switches are tracked here
    /// and the flag is whether any of them is closed - which is what
    /// <c>SwitchEndstop::Stopped</c> answers in RepRapFirmware
    /// </remarks>
    private readonly Dictionary<int, uint> _endstopSwitches = [];

    /// <summary>
    /// Record the state of one switch of an endstop
    /// </summary>
    /// <param name="axis">Axis the endstop belongs to</param>
    /// <param name="switchIndex">Which switch of that endstop</param>
    /// <param name="closed">Whether it is now closed</param>
    /// <returns>Whether any switch of the endstop is closed</returns>
    private bool NoteEndstopSwitch(int axis, int switchIndex, bool closed)
    {
        _endstopSwitches.TryGetValue(axis, out uint switches);
        uint bit = 1u << (switchIndex & 31);
        switches = closed ? switches | bit : switches & ~bit;
        _endstopSwitches[axis] = switches;
        return switches != 0;
    }

    private async ValueTask ApplyInputChangedAsync(ushort states, byte numHandles, Func<uint, RemoteInputHandle> getHandle,
                                                   CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // One bit of States per handle, so a message can carry no more handles than that
            int handles = Math.Min((int)numHandles, 16);
            for (int i = 0; i < handles; i++)
            {
                RemoteInputHandle handle = getHandle((uint)i);

                // Bit i of States is the digital level of the i'th handle in this message
                bool active = (states & (1 << i)) != 0;

                if (handle.Type == RemoteInputHandle.TypeGpIn)
                {
                    GpInputPort? port = GetOrCreate(model.Sensors.GpIn, handle.Major, () => new GpInputPort());
                    if (port is not null)
                    {
                        port.Value = active ? 1.0f : 0.0f;
                    }
                }
                else if (handle.Type == RemoteInputHandle.TypeEndstop)
                {
                    // Major is the axis the endstop belongs to, which is how M574 registered it. An
                    // axis with a switch per driver reports each switch under its own minor, and any
                    // of them being closed is the axis being stopped, which is how RepRapFirmware's
                    // SwitchEndstop::Stopped reads it too
                    Endstop? endstop = handle.Major < model.Sensors.Endstops.Count
                        ? model.Sensors.Endstops[handle.Major]
                        : null;
                    if (endstop is not null)
                    {
                        endstop.Triggered = NoteEndstopSwitch(handle.Major, handle.Minor, active);
                    }
                }
                else if (handle.Type == RemoteInputHandle.TypeZprobe)
                {
                    // Major is the probe number, which is how M558 registered it. The board sends the
                    // level of a digital probe and the reading of an analog one; both arrive here as
                    // one bit, so a digital probe reads as the extremes of the analog range
                    Probe? probe = handle.Major < model.Sensors.Probes.Count
                        ? model.Sensors.Probes[handle.Major]
                        : null;
                    if (probe is not null)
                    {
                        while (probe.Value.Count < 1)
                        {
                            probe.Value.Add(0);
                        }
                        probe.Value[0] = active ? MaxProbeReading : 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Find the board with the given CAN address, adding it if this is the first thing heard from it
    /// </summary>
    /// <param name="address">CAN address</param>
    /// <returns>The board</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private Board GetOrCreateBoard(byte address)
    {
        foreach (Board existing in model.Boards)
        {
            if (existing.CanAddress == address)
            {
                return existing;
            }
        }

        Board board = new() { CanAddress = address, State = BoardState.Unknown };
        model.Boards.Add(board);
        logger.LogInformation("Discovered expansion board at CAN address {Address}", address);
        return board;
    }

    /// <summary>
    /// Get an item of an object model collection, growing the collection to reach it
    /// </summary>
    /// <typeparam name="T">Type of the item</typeparam>
    /// <param name="collection">The collection</param>
    /// <param name="index">Index being reported on</param>
    /// <param name="create">Creates a missing item</param>
    /// <returns>The item, or null if the index is not usable</returns>
    /// <remarks>
    /// A board reports on the things it has been configured with, so an index arriving before the
    /// object model has an entry for it means the configuration is ahead of us rather than wrong.
    /// The gaps are left null, which is what an unconfigured slot means in these collections
    /// </remarks>
    private static T? GetOrCreate<T>(StaticModelCollection<T?> collection, int index, Func<T> create)
        where T : ModelObject, IStaticModelObject, new()
    {
        if (index < 0 || index >= MaxReportedIndex)
        {
            return null;
        }

        while (collection.Count <= index)
        {
            collection.Add(null);
        }
        return collection[index] ??= create();
    }

    /// <summary>
    /// Highest index a report may address, as a guard against a malformed bitmap growing a collection without bound
    /// </summary>
    private const int MaxReportedIndex = 64;

    /// <summary>
    /// The set bit positions of a bitmap, lowest first
    /// </summary>
    /// <param name="bitmap">The bitmap</param>
    /// <param name="maxResults">How many values the message actually carries</param>
    /// <returns>The bit positions</returns>
    /// <remarks>
    /// The n'th value in one of these reports belongs to the n'th set bit, so the order matters and
    /// the count is capped by the size of the value array rather than by the bitmap
    /// </remarks>
    private static System.Collections.Generic.IEnumerable<int> SetBits(ulong bitmap, int maxResults)
    {
        int found = 0;
        for (int bit = 0; bit < 64 && found < maxResults; bit++)
        {
            if ((bitmap & (1UL << bit)) != 0)
            {
                found++;
                yield return bit;
            }
        }
    }

    /// <summary>
    /// Turn a reported minimum/current/maximum triple into its object model form
    /// </summary>
    /// <param name="value">The reported triple</param>
    /// <returns>The object model value</returns>
    private static MinMaxCurrent ToMinMaxCurrent(ShortMinCurMax value) => new()
    {
        Current = (float)value.Current,
        Min = (float)value.Minimum,
        Max = (float)value.Maximum
    };

    /// <summary>
    /// Turn a reported minimum/current/maximum triple in the older format into its object model form
    /// </summary>
    /// <param name="value">The reported triple</param>
    /// <returns>The object model value</returns>
    private static MinMaxCurrent ToMinMaxCurrent(MinCurMax value) => new()
    {
        Current = value.Current,
        Min = value.Minimum,
        Max = value.Maximum
    };

    /// <summary>
    /// Format a board's unique id the way it is shown everywhere else
    /// </summary>
    /// <param name="uniqueId">The raw id</param>
    /// <returns>The formatted id</returns>
    private static string FormatUniqueId(ByteArray16 uniqueId)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            bytes[i] = uniqueId[i];
        }
        return Convert.ToHexStringLower(bytes);
    }
}
