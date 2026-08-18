using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Heat;

/// <summary>
/// The heaters a machine has, what they are asked to reach, and waiting for them to reach it
/// </summary>
/// <remarks>
/// <para>
/// Ported from the parts of RepRapFirmware's <c>Heat</c> that survive §1's fourth rule. Every heater
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
/// <param name="logger">Logger</param>
public sealed class HeatManager(Model.ObjectModel model, LinkInterface linkInterface, ILogger<HeatManager> logger)
{
    /// <summary>
    /// Highest heater number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxHeaters</c> for a Duet 3 MB6HC</remarks>
    public const int MaxHeaters = 32;

    /// <summary>
    /// How close to the setpoint counts as having got there
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>TEMPERATURE_CLOSE_ENOUGH</c>. A heater never settles exactly, so waiting
    /// for equality would wait forever
    /// </remarks>
    public const float TemperatureCloseEnough = 2.5f;

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
        model.Heat.Heaters[heaterNumber] = heater;
        return heater;
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
    public async ValueTask<string?> SetTemperatureAsync(int heaterNumber, float setPoint, byte command,
                                                        CancellationToken cancellationToken)
    {
        byte board;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Heater? heater = Find(heaterNumber);
            if (heater is null)
            {
                return $"Heater {heaterNumber} not found";
            }
            if (!TryGetBoard(heater, out board))
            {
                return $"Heater {heaterNumber} has no sensor, so nothing knows how hot it is";
            }
        }

        CanMessageSetHeaterTemperatureV1 message = new()
        {
            HeaterNumber = (byte)heaterNumber,
            SetPoint = setPoint,
            Function = command
        };

        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        return reply.Type == MessageType.Error ? reply.Content : null;
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
    /// Wait for heaters to reach what they were asked to reach
    /// </summary>
    /// <param name="heaters">Heaters to wait for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if they all got there, false if the wait was cancelled</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>HeaterAtSetTemperature</c> loop. A heater that is off is not waited for -
    /// it will never arrive - and neither is one in a fault, which is what stops M109 hanging a print
    /// on a heater that has already given up.
    /// </para>
    /// <para>
    /// Only heating is waited for, not cooling: RepRapFirmware waits for a heater to come *up* to
    /// temperature and treats one above its setpoint as ready, because a print does not have to wait
    /// for a nozzle to cool before it can move
    /// </para>
    /// </remarks>
    public async ValueTask<bool> WaitForTemperaturesAsync(IReadOnlyList<int> heaters,
                                                          CancellationToken cancellationToken)
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
                    Heater? heater = Find(heaterNumber);
                    if (heater is null || heater.State is HeaterState.Off or HeaterState.Fault)
                    {
                        continue;               // nothing to wait for, and a fault will not resolve itself
                    }

                    float target = heater.State == HeaterState.Standby ? heater.Standby : heater.Active;
                    if (heater.Current < target - TemperatureCloseEnough)
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
    /// The heaters M140 or M141 addresses
    /// </summary>
    /// <param name="chamber">True for the chamber heaters, false for the bed</param>
    /// <param name="index">Which bed or chamber, or -1 for all of them</param>
    /// <returns>The heater numbers</returns>
    /// <remarks>
    /// A machine may have several beds and each bed several heaters, which is what the mapping
    /// expresses: entry <c>i</c> is the heaters of bed <c>i</c>. So M140 with no P addresses every
    /// bed heater and M140 P1 addresses the second bed's. The caller must hold the object model lock
    /// </remarks>
    public List<int> BedOrChamberHeaters(bool chamber, int index)
    {
        IReadOnlyList<int[]> mapping = chamber ? model.Heat.ChamberHeaterMapping : model.Heat.BedHeaterMapping;
        List<int> heaters = [];
        for (int which = 0; which < mapping.Count; which++)
        {
            if (index < 0 || index == which)
            {
                heaters.AddRange(mapping[which]);
            }
        }
        return heaters;
    }

    /// <summary>
    /// Make a heater the bed or chamber heater, or remove it from that role
    /// </summary>
    /// <param name="chamber">True for a chamber, false for a bed</param>
    /// <param name="index">Which bed or chamber</param>
    /// <param name="heaterNumber">The heater, or negative to leave it with none</param>
    /// <remarks>
    /// Each entry of the mapping is the heaters of one bed, so this replaces that bed's set rather
    /// than adding to it - which is what makes a re-run config.g idempotent. The caller must hold the
    /// object model write lock
    /// </remarks>
    public void AssignBedOrChamberHeater(bool chamber, int index, int heaterNumber)
    {
        var mapping = chamber ? model.Heat.ChamberHeaterMapping : model.Heat.BedHeaterMapping;
        while (mapping.Count <= index)
        {
            mapping.Add([]);
        }
        mapping[index] = heaterNumber >= 0 ? [heaterNumber] : [];
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
    /// Bring a tool's heaters to its active or standby temperatures
    /// </summary>
    /// <param name="tool">The tool</param>
    /// <param name="state">What the tool is becoming</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An awaitable task</returns>
    /// <remarks>
    /// A tool that is put down goes to standby rather than off, which is what lets it be picked up
    /// again without waiting for it to reheat. The caller must not hold the object model lock
    /// </remarks>
    public async ValueTask ApplyToolStateAsync(Tool tool, ToolState state, CancellationToken cancellationToken)
    {
        List<(int Heater, float Target)> targets = [];
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            for (int index = 0; index < tool.Heaters.Count; index++)
            {
                int heaterNumber = tool.Heaters[index];
                Heater? heater = Find(heaterNumber);
                if (heater is null)
                {
                    continue;
                }

                float active = index < tool.Active.Count ? tool.Active[index] : 0.0f;
                float standby = index < tool.Standby.Count ? tool.Standby[index] : 0.0f;

                heater.Active = active;
                heater.Standby = standby;
                heater.State = state switch
                {
                    ToolState.Active => HeaterState.Active,
                    ToolState.Standby => HeaterState.Standby,
                    _ => HeaterState.Off
                };
                targets.Add((heaterNumber, state == ToolState.Active ? active : standby));
            }
        }

        foreach ((int heaterNumber, float target) in targets)
        {
            byte command = state == ToolState.Off
                ? CanMessageSetHeaterTemperatureV1.CommandOff
                : CanMessageSetHeaterTemperatureV1.CommandOn;
            if (await SetTemperatureAsync(heaterNumber, target, command, cancellationToken) is string error)
            {
                logger.LogWarning("Could not set heater {Heater} for tool {Tool}: {Error}",
                                  heaterNumber, tool.Number, error);
            }
        }
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
            if (await SetTemperatureAsync(heaterNumber, 0.0f, CanMessageSetHeaterTemperatureV1.CommandOff,
                                          cancellationToken) is string error)
            {
                logger.LogWarning("Could not switch heater {Heater} off: {Error}", heaterNumber, error);
            }
        }
    }

    /// <summary>
    /// Whether a heater is neither a bed nor a chamber heater
    /// </summary>
    /// <param name="heaterNumber">The heater</param>
    /// <returns>True if nothing has claimed it as a bed or chamber heater</returns>
    /// <remarks>
    /// RepRapFirmware's <c>HeaterFunction::tool</c>, which it stores on the heater. Here the
    /// assignment lives in <c>heat.bedHeaters</c> and <c>heat.chamberHeaters</c>, so the question is
    /// asked of those. The caller must hold the object model lock
    /// </remarks>
    private bool IsToolHeater(int heaterNumber)
        => !model.Heat.BedHeaters.Contains(heaterNumber) && !model.Heat.ChamberHeaters.Contains(heaterNumber);
}
