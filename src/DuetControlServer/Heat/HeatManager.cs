using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link.Requests;
using DuetControlServer.Motion;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Heat;

/// <summary>
/// The heaters a machine has, what they are asked to reach, and waiting for them to reach it
/// </summary>
/// <remarks>
/// <para>
/// Ported from the parts of RepRapFirmware's <c>Heat</c> that deal with remote heaters. Every heater
/// is on a CAN-connected expansion board, which runs the PID loop and owns the fault detection, so
/// this side does not control temperature - it says what the setpoint is and hears back what the
/// temperature became. What is left is therefore smaller than RepRapFirmware's <c>Heat</c>: the
/// configuration, the setpoints, and the waiting.
/// </para>
/// <para>
/// The readings come the other way without passing through here. <c>ExpansionBoardManager</c> writes
/// <c>heat.heaters[].current</c> and <c>sensors.analog[].lastReading</c> as the boards report them,
/// so the object model is where a temperature is read from - including by the waiting below, which is
/// why it polls the model rather than subscribing to anything
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="linkInterface">Link interface, for the CAN messages a heater is configured with</param>
/// <param name="eventLogger">Writes out what a board said outside the reply to a code</param>
/// <param name="logger">Logger</param>
public sealed class HeatManager(Model.ObjectModel model, LinkInterface linkInterface, Utility.EventLogger eventLogger,
                                ILogger<HeatManager> logger)
{
    /// <summary>
    /// Highest heater number a machine may have
    /// </summary>
    /// <remarks>CANlib's <c>MaxHeaters</c>, through the CAN message schema</remarks>
    public const int MaxHeaters = CanLimits.MaxHeaters;

    /// <summary>
    /// Highest bed heater number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxBedHeaters</c> for a Duet 3 MB6HC</remarks>
    public const int MaxBedHeaters = 12;

    /// <summary>
    /// Highest chamber heater number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxChamberHeaters</c> for a Duet 3 MB6HC</remarks>
    public const int MaxChamberHeaters = 8;

    /// <summary>
    /// Most heaters one tool may collect
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxHeatersPerTool</c> for a Duet 3 MB6HC</remarks>
    public const int MaxHeatersPerTool = 20;

    /// <summary>
    /// Most output ports one heater may be driven through
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxPortsPerHeater</c> for a Duet 3 MB6HC</remarks>
    public const int MaxPortsPerHeater = 3;

    /// <summary>
    /// How close to the setpoint counts as having got there
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>TEMPERATURE_CLOSE_ENOUGH</c>. A heater never settles exactly, so waiting
    /// for equality would wait forever
    /// </remarks>
    public const float TemperatureCloseEnough = 1.5f;

    /// <summary>
    /// How often to re-read the temperature while waiting for a heater
    /// </summary>
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Find a heater by number
    /// </summary>
    /// <param name="heaterNumber">The number</param>
    /// <returns>The heater, or null if there is none</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    public Heater? Find(int heaterNumber)
        => heaterNumber >= 0 && heaterNumber < model.Heat.Heaters.Count ? model.Heat.Heaters[heaterNumber] : null;

    /// <summary>
    /// Make room for a heater number in the object model
    /// </summary>
    /// <param name="heaterNumber">The number</param>
    /// <returns>The heater</returns>
    /// <remarks>
    /// The collection is indexed by heater number, so a machine that defines heater 3 and nothing
    /// below it still has four entries - the first three null. The caller must hold the object model
    /// write lock
    /// </remarks>
    public Heater Create(int heaterNumber)
    {
        while (model.Heat.Heaters.Count <= heaterNumber)
        {
            model.Heat.Heaters.Add(null);
        }

        Heater heater = new();
        FopDt.Reset(heater.Model);
        for (int monitor = 0; monitor < CanLimits.MaxMonitorsPerHeater; monitor++)
        {
            heater.Monitors.Add(new HeaterMonitor());
        }
        UpdateTemperatureLimits(heater);
        model.Heat.Heaters[heaterNumber] = heater;
        _modelSetByUser[heaterNumber] = _monitorsSetByUser[heaterNumber] = false;
        _lastStandbyTool[heaterNumber] = -1;
        return heater;
    }

    /// <summary>
    /// Whether M307 has set a heater's model, which giving the heater a function then leaves alone
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heater::modelSetByUser</c></remarks>
    private readonly bool[] _modelSetByUser = new bool[MaxHeaters];

    /// <summary>
    /// Whether M143 has set a heater's monitors, which giving the heater a function then leaves alone
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heater::monitorsSetByUser</c></remarks>
    private readonly bool[] _monitorsSetByUser = new bool[MaxHeaters];

    /// <summary>
    /// The tool that last put each heater on standby, or -1 for none
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heat::lastStandbyTools</c></remarks>
    private readonly int[] _lastStandbyTool = Enumerable.Repeat(-1, MaxHeaters).ToArray();

    /// <summary>
    /// The tool that last put a heater on standby, or -1 for none
    /// </summary>
    public int GetLastStandbyTool(int heaterNumber) => _lastStandbyTool[heaterNumber];

    /// <summary>
    /// Record that M307 has set a heater's model
    /// </summary>
    public void SetModelSetByUser(int heaterNumber) => _modelSetByUser[heaterNumber] = true;

    /// <summary>
    /// Record that M143 has set a heater's monitors
    /// </summary>
    public void SetMonitorsSetByUser(int heaterNumber) => _monitorsSetByUser[heaterNumber] = true;

    /// <summary>
    /// RepRapFirmware's <c>BadErrorTemperature</c>, the highest limit of a heater with no over-temperature monitor
    /// </summary>
    public const float BadErrorTemperature = 2000.0f;

    /// <summary>
    /// RepRapFirmware's <c>ABS_ZERO</c>, the lowest limit of a heater with no under-temperature monitor
    /// </summary>
    public const float AbsoluteZero = -273.15f;

    /// <summary>
    /// RepRapFirmware's <c>NEARLY_ABS_ZERO</c>: a temperature at or below this switches a heater off
    /// </summary>
    public const float NearlyAbsoluteZero = -273.0f;

    /// <summary>
    /// RepRapFirmware's <c>DefaultHotEndTemperatureLimit</c>, a tool heater's default over-temperature limit
    /// </summary>
    public const float DefaultHotEndTemperatureLimit = 285.0f;

    /// <summary>
    /// RepRapFirmware's <c>DefaultBedTemperatureLimit</c>, a bed heater's default over-temperature limit
    /// </summary>
    public const float DefaultBedTemperatureLimit = 125.0f;

    /// <summary>
    /// RepRapFirmware's <c>DefaultChamberTemperatureLimit</c>, a chamber heater's default over-temperature limit
    /// </summary>
    public const float DefaultChamberTemperatureLimit = 100.0f;

    /// <summary>
    /// Highest temperature any of a heater's over-temperature monitors allows
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heater::GetHighestTemperatureLimit</c></remarks>
    public static float GetHighestTemperatureLimit(Heater heater)
    {
        float limit = BadErrorTemperature;
        foreach (HeaterMonitor? monitor in heater.Monitors)
        {
            if (monitor is { Condition: HeaterMonitorCondition.TooHigh, Limit: float t }
                && (limit == BadErrorTemperature || t > limit))
            {
                limit = t;
            }
        }
        return limit;
    }

    /// <summary>
    /// Lowest temperature any of a heater's under-temperature monitors allows
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heater::GetLowestTemperatureLimit</c></remarks>
    public static float GetLowestTemperatureLimit(Heater heater)
    {
        float limit = AbsoluteZero;
        foreach (HeaterMonitor? monitor in heater.Monitors)
        {
            if (monitor is { Condition: HeaterMonitorCondition.TooLow, Limit: float t }
                && (limit == AbsoluteZero || t < limit))
            {
                limit = t;
            }
        }
        return limit;
    }

    /// <summary>
    /// Set a heater's <c>max</c> and <c>min</c> from its monitors
    /// </summary>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public static void UpdateTemperatureLimits(Heater heater)
    {
        heater.Max = GetHighestTemperatureLimit(heater);
        heater.Min = GetLowestTemperatureLimit(heater);
    }

    /// <summary>
    /// Tell the board carrying a heater what to do with it
    /// </summary>
    /// <param name="heaterNumber">The heater</param>
    /// <param name="setPoint">Temperature to hold, in C</param>
    /// <param name="command">One of <c>CanMessageSetHeaterTemperatureV1</c>'s command constants</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board said, or a refusal if the heater is not on one</returns>
    /// <remarks>
    /// The setpoint and the on/off decision travel together because the board takes them together:
    /// a heater told to reach 200C is not heating until it is also told to switch on, and RRF's
    /// <c>CommandNone</c> is what changes one without the other
    /// </remarks>
    public async ValueTask<Message> SetTemperatureAsync(int heaterNumber, float setPoint, byte command,
                                                        CancellationToken cancellationToken)
    {
        byte board;
        HeaterFunction function;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Heater? heater = Find(heaterNumber);
            if (heater is null)
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} not found");
            }
            if (!TryGetBoard(heater, out board))
            {
                return new Message(MessageType.Error,
                                   $"Heater {heaterNumber} has no sensor");
            }
            function = GetHeaterFunction(heaterNumber);
        }

        CanMessageSetHeaterTemperatureV1 message = new()
        {
            HeaterNumber = (byte)heaterNumber,
            SetPoint = setPoint,
            Command = command,
            Function = (byte)function,
        };

        return await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
    }

    /// <summary>
    /// The board that carries a heater, which is the board carrying the sensor it reads
    /// </summary>
    /// <param name="heater">The heater</param>
    /// <param name="board">Receives the CAN address</param>
    /// <returns>True if the heater has a sensor on a board</returns>
    /// <remarks>
    /// A heater and its sensor are always on the same board: the board runs the control loop, so it
    /// has to be able to read the temperature without going over the bus for it. RepRapFirmware
    /// requires the same and refuses M950 H otherwise
    /// </remarks>
    public bool TryGetBoard(Heater heater, out byte board)
    {
        board = CanId.MasterAddress;
        AnalogSensor? sensor = heater.Sensor >= 0 && heater.Sensor < model.Sensors.Analog.Count
                               ? model.Sensors.Analog[heater.Sensor]
                               : null;
        if (sensor?.Port is null)
        {
            return false;
        }

        board = IoPorts.RemoveBoardAddress(sensor.Port, out _);
        return !CanAddresses.HasNoHardware(board);
    }

    /// <summary>
    /// RepRapFirmware's <c>TemperatureSoLowDontCare</c>: a heating target below this is not waited for
    /// </summary>
    public const float TemperatureSoLowDontCare = 40.0f;

    /// <summary>
    /// RepRapFirmware's <c>TemperatureSoHighDontCare</c>: a cooling device's target above this is not waited for
    /// </summary>
    public const float TemperatureSoHighDontCare = 20.0f;

    /// <summary>
    /// Wait for heaters to reach what they were asked to reach
    /// </summary>
    /// <param name="heaters">Heaters to wait for</param>
    /// <param name="waitWhenCooling">Whether a heater above its target is waited for until it has cooled</param>
    /// <param name="tolerance">How close to the target counts as there</param>
    /// <param name="waitOnFault">Whether a heater in a fault is waited for, which only a file channel does</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if they all got there, false if the wait was cancelled</returns>
    public async ValueTask<bool> WaitForTemperaturesAsync(IReadOnlyList<int> heaters, bool waitWhenCooling, float tolerance,
                                                          bool waitOnFault, CancellationToken cancellationToken)
    {
        // Counted rather than flagged, because several channels may be waiting at once and a flag
        // would be cleared by whichever finished first
        Interlocked.Increment(ref _waitingForTemperatures);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool allReady = true;
                using (await model.AccessReadOnlyAsync(cancellationToken))
                {
                    foreach (int heaterNumber in heaters)
                    {
                        if (!HeaterAtSetTemperature(heaterNumber, waitWhenCooling, tolerance, waitOnFault))
                        {
                            allReady = false;
                            break;
                        }
                    }
                }

                if (allReady)
                {
                    return true;
                }
                await Task.Delay(WaitPollInterval, cancellationToken);
            }
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _waitingForTemperatures);
        }
    }

    /// <summary>
    /// Whether a heater is close enough to its target, which it is if it does not exist or is not on
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heat::HeaterAtSetTemperature</c>. The caller must hold the object model lock</remarks>
    private bool HeaterAtSetTemperature(int heaterNumber, bool waitWhenCooling, float tolerance, bool waitOnFault)
    {
        if (Find(heaterNumber) is not Heater heater)
        {
            return true;
        }
        if (waitOnFault && heater.State == HeaterState.Fault)
        {
            return false;
        }
        if (heater.State is HeaterState.Active or HeaterState.Standby)
        {
            float target = (heater.State == HeaterState.Active) ? heater.Active : heater.Standby;
            bool cooling = heater.Model.Inverted;
            return (!cooling && target < TemperatureSoLowDontCare)
                || (cooling && target > TemperatureSoHighDontCare)
                || MathF.Abs(heater.Current - target) <= tolerance
                || (target < heater.Current && !waitWhenCooling);
        }
        return true;
    }

    /// <summary>
    /// Whether anything is waiting for a heater to reach its target
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::IsHeatingUp</c>, and what tells the job monitor that the time
    /// passing is warm-up rather than printing. A job that counted it would look as though it had
    /// started slowly and then sped up
    /// </remarks>
    public bool IsWaitingForTemperatures => Volatile.Read(ref _waitingForTemperatures) > 0;
    private int _waitingForTemperatures;

    /// <summary>
    /// RepRapFirmware's <c>MaxHeatersPerBed</c> for a Duet 3 MB6HC
    /// </summary>
    public const int MaxHeatersPerBed = 4;

    /// <summary>
    /// RepRapFirmware's <c>MaxHeatersPerChamber</c> for a Duet 3 MB6HC
    /// </summary>
    public const int MaxHeatersPerChamber = 4;

    /// <summary>
    /// The heaters of one bed or chamber slot
    /// </summary>
    /// <param name="chamber">True for a chamber slot, false for a bed slot</param>
    /// <param name="slot">The slot</param>
    /// <returns>The heater numbers, which may include heaters that do not exist</returns>
    /// <remarks>RepRapFirmware's <c>HeaterCollection</c>. The caller must hold the object model lock</remarks>
    public int[] BedOrChamberHeaters(bool chamber, int slot)
    {
        IReadOnlyList<int[]> mapping = chamber ? model.Heat.ChamberHeaterMapping : model.Heat.BedHeaterMapping;
        return slot < mapping.Count ? mapping[slot] : [];
    }

    /// <summary>
    /// Make heaters the heaters of a bed or chamber slot: the slot's old heaters are switched off, and the new ones are
    /// given their function
    /// </summary>
    /// <param name="chamber">True for a chamber slot, false for a bed slot</param>
    /// <param name="slot">The slot</param>
    /// <param name="heaterNumbers">The heaters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::SetBedHeaters</c> and <c>Heat::SetChamberHeaters</c>. The caller must not hold the
    /// object model lock
    /// </remarks>
    public async ValueTask SetBedOrChamberHeatersAsync(bool chamber, int slot, int[] heaterNumbers,
                                                       CancellationToken cancellationToken)
    {
        await ClearBedOrChamberHeatersAsync(chamber, slot, cancellationToken);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            WriteSlot(chamber, slot, heaterNumbers);
        }

        foreach (int heaterNumber in heaterNumbers)
        {
            await SetFunctionAsync(heaterNumber, chamber ? HeaterFunction.Chamber : HeaterFunction.Bed, cancellationToken);
        }
    }

    /// <summary>
    /// Switch off the heaters of a bed or chamber slot and empty it
    /// </summary>
    /// <param name="chamber">True for a chamber slot, false for a bed slot</param>
    /// <param name="slot">The slot</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::ClearBedHeaters</c> and <c>Heat::ClearChamberHeaters</c>. The caller must not hold the
    /// object model lock
    /// </remarks>
    public async ValueTask ClearBedOrChamberHeatersAsync(bool chamber, int slot, CancellationToken cancellationToken)
    {
        int[] heaterNumbers;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            heaterNumbers = BedOrChamberHeaters(chamber, slot);
        }

        foreach (int heaterNumber in heaterNumbers)
        {
            await SwitchOffAsync(heaterNumber, cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            WriteSlot(chamber, slot, []);
        }
    }

    /// <summary>
    /// Store the heaters of a slot in <c>heat.bedHeaterMapping</c> or <c>heat.chamberHeaterMapping</c>, and the first heater
    /// of every slot in the obsolete <c>heat.bedHeaters</c> or <c>heat.chamberHeaters</c>
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>Heat</c> object model table: the mapping reports up to the last slot that has a heater, and the
    /// obsolete array every slot, -1 where it is empty. The caller must hold the object model write lock
    /// </remarks>
    private void WriteSlot(bool chamber, int slot, int[] heaterNumbers)
    {
        ObservableCollection<int[]> mapping = chamber ? model.Heat.ChamberHeaterMapping : model.Heat.BedHeaterMapping;
        while (mapping.Count <= slot)
        {
            mapping.Add([]);
        }
        mapping[slot] = heaterNumbers;
        while (mapping.Count > 0 && mapping[^1].Length == 0)
        {
            mapping.RemoveAt(mapping.Count - 1);
        }

#pragma warning disable CS0618
        ObservableCollection<int> firstHeaters = chamber ? model.Heat.ChamberHeaters : model.Heat.BedHeaters;
#pragma warning restore CS0618
        int slots = chamber ? MaxChamberHeaters : MaxBedHeaters;
        while (firstHeaters.Count < slots)
        {
            firstHeaters.Add(-1);
        }
        for (int index = 0; index < slots; index++)
        {
            int first = (index < mapping.Count && mapping[index].Length > 0) ? mapping[index][0] : -1;
            if (firstHeaters[index] != first)
            {
                firstHeaters[index] = first;
            }
        }
    }

    /// <summary>
    /// Give a heater a function: the board's default model for that function unless M307 has set the model, and the
    /// default over-temperature monitor for it unless M143 has set the monitors
    /// </summary>
    /// <param name="heaterNumber">The heater, which need not exist</param>
    /// <param name="function">What the heater is for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>Heater::SetFunction</c> and <c>RemoteHeater::SetDefaultModel</c>, which ignores a board that
    /// does not answer with a model. The monitor is not sent to the board. The caller must not hold the object model lock
    /// </remarks>
    public async ValueTask SetFunctionAsync(int heaterNumber, HeaterFunction function, CancellationToken cancellationToken)
    {
        byte board = 0;
        bool askForModel;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Find(heaterNumber) is not Heater heater)
            {
                return;
            }
            askForModel = !_modelSetByUser[heaterNumber] && TryGetBoard(heater, out board);
        }

        CanMessageHeaterModelReport? report = null;
        if (askForModel)
        {
            CanMessageSetDefaultHeaterModel request = new() { Heater = (byte)heaterNumber, HeaterFunction = function };
            CanResponse response = await linkInterface.SendCanMessageAsync(board, in request, CanMessageType.HeaterModelReport,
                                                                           cancellationToken: cancellationToken);
            if (response.ResponseType == CanMessageType.HeaterModelReport)
            {
                report = response.AsCanMessage<CanMessageHeaterModelReport>();
            }
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(heaterNumber) is not Heater heater)
            {
                return;
            }

            if (report is CanMessageHeaterModelReport defaultModel)
            {
                FopDt.SetDefaultModel(heater.Model, defaultModel.Model);
            }

            if (!_monitorsSetByUser[heaterNumber] && heater.Sensor >= 0 && heater.Sensor < CanLimits.MaxSensors
                && heater.Monitors.Count > 0 && heater.Monitors[0] is HeaterMonitor monitor)
            {
                monitor.Sensor = heater.Sensor;
                monitor.Limit = function switch
                {
                    HeaterFunction.Bed => DefaultBedTemperatureLimit,
                    HeaterFunction.Chamber => DefaultChamberTemperatureLimit,
                    _ => DefaultHotEndTemperatureLimit,
                };
                monitor.Action = HeaterMonitorAction.GenerateFault;
                monitor.Condition = HeaterMonitorCondition.TooHigh;
                UpdateTemperatureLimits(heater);
            }
        }
    }

    /// <summary>
    /// Set a heater's active or standby temperature, and pass it on to the board if the heater is on in that mode
    /// </summary>
    /// <param name="heaterNumber">The heater, which need not exist</param>
    /// <param name="temperature">The temperature</param>
    /// <param name="active">True for the active temperature, false for the standby temperature</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the temperature is outside the heater's limits or the board refused it</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::SetTemperature</c> and <c>Heater::SetTemperature</c>, which recalculates the PID constants
    /// at the active temperature once the board has it. The caller must not hold the object model lock
    /// </remarks>
    public async ValueTask<Message> SetActiveOrStandbyTemperatureAsync(int heaterNumber, float temperature, bool active,
                                                                       CancellationToken cancellationToken)
    {
        bool update;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(heaterNumber) is not Heater heater)
            {
                return new Message();
            }
            if (temperature > GetHighestTemperatureLimit(heater))
            {
                return new Message(MessageType.Error, $"Temperature too high for heater {heaterNumber}");
            }
            if (temperature < GetLowestTemperatureLimit(heater))
            {
                return new Message(MessageType.Error, $"Temperature too low for heater {heaterNumber}");
            }

            if (active)
            {
                heater.Active = temperature;
            }
            else
            {
                heater.Standby = temperature;
            }
            update = heater.State == (active ? HeaterState.Active : HeaterState.Standby);
        }

        if (!update)
        {
            return new Message();
        }

        Message reply = await SwitchOnAsync(heaterNumber, cancellationToken);
        if (reply.Succeeded())
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                if (Find(heaterNumber) is Heater heater)
                {
                    FopDt.CalcPidConstants(heater.Model, GetHeaterFunction(heaterNumber), heater.Active);
                }
            }
        }
        return reply;
    }

    /// <summary>
    /// Switch a heater on at its active or standby temperature
    /// </summary>
    /// <param name="heaterNumber">The heater</param>
    /// <param name="active">True for the active temperature, false for the standby temperature</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="toolNumber">Tool putting the heater on standby, or -1 for none</param>
    /// <returns>What the board said, or an error if the heater does not exist or is in a fault</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::SetActiveOrStandby</c> and <c>Heater::SetActiveOrStandby</c>. The caller must not hold
    /// the object model lock
    /// </remarks>
    public async ValueTask<Message> SetActiveOrStandbyAsync(int heaterNumber, bool active, CancellationToken cancellationToken,
                                                            int toolNumber = -1)
    {
        HeaterState state = active ? HeaterState.Active : HeaterState.Standby;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(heaterNumber) is not Heater heater)
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} not found");
            }
            if (heater.State == HeaterState.Fault)
            {
                return new Message(MessageType.Error, $"Can't turn heater {heaterNumber} on while in fault state");
            }

            // Whether the heater is in active or standby mode is kept in its state while it is on
            if (heater.State is HeaterState.Active or HeaterState.Standby)
            {
                heater.State = state;
            }
        }

        Message reply = await SwitchOnAsync(heaterNumber, cancellationToken, state);
        if (reply.Succeeded())
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                if (Find(heaterNumber) is Heater { State: HeaterState.Off or HeaterState.Active or HeaterState.Standby } heater)
                {
                    heater.State = state;
                }
            }
        }
        if (reply.Type == MessageType.Success && !active)
        {
            _lastStandbyTool[heaterNumber] = toolNumber;
        }
        return reply;
    }

    /// <summary>
    /// Tell a heater's board to run it at its target temperature
    /// </summary>
    /// <param name="heaterNumber">The heater</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="state">Mode whose temperature is the target, or null for the mode the heater is in</param>
    /// <returns>What the board said</returns>
    /// <remarks>RepRapFirmware's <c>RemoteHeater::SwitchOn</c>. The caller must not hold the object model lock</remarks>
    private async ValueTask<Message> SwitchOnAsync(int heaterNumber, CancellationToken cancellationToken, HeaterState? state = null)
    {
        float target;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Find(heaterNumber) is not Heater heater)
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} not found");
            }
            target = ((state ?? heater.State) == HeaterState.Standby) ? heater.Standby : heater.Active;
        }
        return await SetTemperatureAsync(heaterNumber, target, CanMessageSetHeaterTemperatureV1.CommandOn, cancellationToken);
    }

    /// <summary>
    /// Switch a heater off, leaving its temperatures as they are
    /// </summary>
    /// <param name="heaterNumber">The heater, which need not exist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::SwitchOff</c> and <c>RemoteHeater::SwitchOff</c>, which reports a board's refusal as a
    /// message of its own rather than as the result of the code that switched the heater off. The caller must not hold
    /// the object model lock
    /// </remarks>
    public async ValueTask SwitchOffAsync(int heaterNumber, CancellationToken cancellationToken)
    {
        float target;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(heaterNumber) is not Heater heater)
            {
                return;
            }
            target = (heater.State == HeaterState.Standby) ? heater.Standby : heater.Active;
            if (heater.State != HeaterState.Fault)
            {
                heater.State = HeaterState.Off;
            }
            _lastStandbyTool[heaterNumber] = -1;
        }

        Message reply = await SetTemperatureAsync(heaterNumber, target, CanMessageSetHeaterTemperatureV1.CommandOff, cancellationToken);
        if (!reply.Succeeded())
        {
            await eventLogger.LogOutputAsync(MessageType.Error, $"Failed to switch off remote heater {heaterNumber}: {reply.Content}",
                                             cancellationToken);
        }
    }

    /// <summary>
    /// The first bed heater, which is the one M105 reports as B
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    public Heater? FirstBedHeater()
    {
        foreach (int[] bed in model.Heat.BedHeaterMapping)
        {
            foreach (int heaterNumber in bed)
            {
                if (Find(heaterNumber) is Heater heater)
                {
                    return heater;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Whether an extruder may be driven at the temperature its tool is at
    /// </summary>
    /// <param name="tool">The tool</param>
    /// <param name="extruding">True for extrusion, false for retraction</param>
    /// <returns>True if the move may go ahead</returns>
    /// <remarks>
    /// Cold extrusion strips the filament and jams the drive, so RepRapFirmware refuses it below
    /// M302's limits - with a lower limit for retraction, because pulling filament out of a
    /// half-warm nozzle is safer than pushing it in. The caller must hold the object model lock
    /// </remarks>
    public bool CanExtrude(Tool tool, bool extruding)
    {
        float limit = extruding ? model.Heat.ColdExtrudeTemperature : model.Heat.ColdRetractTemperature;
        foreach (int heaterNumber in tool.Heaters)
        {
            Heater? heater = Find(heaterNumber);
            if (heater is not null && heater.Current < limit)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Switch every heater off
    /// </summary>
    /// <param name="includingChamberAndBed">Whether the bed and chamber heaters go off too</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::SwitchOffAll</c>. It is what a job that has finished or been
    /// aborted does when there is no <c>stop.g</c> to decide otherwise, so it must not stop at the
    /// first heater that refuses - a board that has dropped off the bus would otherwise leave every
    /// heater after it running
    /// </remarks>
    public async ValueTask SwitchOffAllAsync(bool includingChamberAndBed, CancellationToken cancellationToken)
    {
        List<int> heaterNumbers = [];
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            for (int heaterNumber = 0; heaterNumber < model.Heat.Heaters.Count; heaterNumber++)
            {
                if (model.Heat.Heaters[heaterNumber] is not Heater heater)
                {
                    continue;
                }
                if (!includingChamberAndBed && !IsToolHeater(heaterNumber))
                {
                    continue;
                }

                heater.State = HeaterState.Off;
                heaterNumbers.Add(heaterNumber);
            }
        }

        foreach (int heaterNumber in heaterNumbers)
        {
            Message reply = await SetTemperatureAsync(heaterNumber, 0.0f,
                                                      CanMessageSetHeaterTemperatureV1.CommandOff,
                                                      cancellationToken);
            if (!reply.Succeeded())
            {
                logger.LogWarning("Could not switch heater {Heater} off: {Error}", heaterNumber, reply.Content);
            }
        }
    }

    /// <summary>
    /// What a heater is for: a bed or chamber heater if a bed or chamber mapping claims it, otherwise a tool heater
    /// </summary>
    /// <param name="heaterNumber">The heater</param>
    /// <returns>The heater's function</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::GetHeaterFunction</c>. The assignment lives in the bed and chamber mappings, and a
    /// machine may have several beds and each bed several heaters. The caller must hold the object model lock
    /// </remarks>
    public HeaterFunction GetHeaterFunction(int heaterNumber)
        => IsMapped(model.Heat.BedHeaterMapping, heaterNumber) ? HeaterFunction.Bed
         : IsMapped(model.Heat.ChamberHeaterMapping, heaterNumber) ? HeaterFunction.Chamber
         : HeaterFunction.Tool;

    /// <summary>
    /// Whether a heater is neither a bed nor a chamber heater
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    public bool IsToolHeater(int heaterNumber) => GetHeaterFunction(heaterNumber) == HeaterFunction.Tool;

    /// <summary>
    /// Whether a bed or chamber mapping claims a heater
    /// </summary>
    /// <param name="mapping">The mapping</param>
    /// <param name="heaterNumber">The heater</param>
    /// <returns>True if any entry names it</returns>
    private static bool IsMapped(IReadOnlyList<int[]> mapping, int heaterNumber)
    {
        foreach (int[] heaters in mapping)
        {
            foreach (int heater in heaters)
            {
                if (heater == heaterNumber)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
