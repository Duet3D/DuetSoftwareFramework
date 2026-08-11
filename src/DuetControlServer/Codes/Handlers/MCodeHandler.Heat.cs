using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Heat;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that configure and drive the heaters
/// </summary>
/// <remarks>
/// Every heater is on a CAN-connected expansion board, which runs the control loop and owns the
/// fault detection. So these codes configure and command; they do not control. What comes back the
/// other way - the temperatures and the heater states - is written to the object model by
/// <c>ExpansionBoardManager</c> as the boards report it
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// M308: configure a temperature sensor
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The sensor belongs to the board carrying its port, and that board is the one that reads it, so
    /// the parameters are repackaged as the generic message its table describes and answered by that
    /// board. What is recorded here is what the object model needs to describe the machine
    /// </remarks>
    private async ValueTask<Message> HandleConfigureSensorAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('S', out int sensorNumber) || sensorNumber < 0)
        {
            return await ReportSensorsAsync(cancellationToken);
        }

        string? port = code.TryGetString('P', out string? portName) ? portName : null;
        byte board;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            AnalogSensor? sensor = sensorNumber < model.Sensors.Analog.Count ? model.Sensors.Analog[sensorNumber] : null;
            if (port is not null)
            {
                if (!RemoteEndstops.TrySplitPort(port, "Sensor port", out board, out _, out string? error))
                {
                    return new Message(MessageType.Error, error);
                }

                while (model.Sensors.Analog.Count <= sensorNumber)
                {
                    model.Sensors.Analog.Add(null);
                }
                sensor ??= new AnalogSensor();
                sensor.Port = port;
                model.Sensors.Analog[sensorNumber] = sensor;
            }
            else if (sensor?.Port is null)
            {
                return new Message(MessageType.Error, $"Sensor {sensorNumber} has no port; use P to give it one");
            }
            else if (!RemoteEndstops.TrySplitPort(sensor.Port, "Sensor port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            // An unrecognised name is left for the board to judge rather than refused here: the CAN
            // message carries Y as the operator wrote it, and which types a board supports is the
            // board's business. The object model keeps whatever it had, which is worse than nothing
            // only for a sensor type this side does not know the name of
            if (code.TryGetString('Y', out string? type)
                && System.Enum.TryParse(type, ignoreCase: true, out AnalogSensorType parsed))
            {
                sensor.Type = parsed;
            }
            if (code.TryGetString('A', out string? name))
            {
                sensor.Name = name;
            }
        }

        // The board is what reads the sensor, so it is what has to be told how. The parameter table
        // is the message, which is what makes this a repackaging rather than a reimplementation
        return await SendGenericAsync<CanMessageM308V1>(board, code, cancellationToken);
    }

    /// <summary>
    /// Report the sensors the machine has, as M308 with no parameters does
    /// </summary>
    private async ValueTask<Message> ReportSensorsAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            for (int sensor = 0; sensor < model.Sensors.Analog.Count; sensor++)
            {
                AnalogSensor? analog = model.Sensors.Analog[sensor];
                if (analog is null)
                {
                    continue;
                }

                builder.Append(CultureInfo.InvariantCulture,
                               $"Sensor {sensor} type {analog.Type} using pin {analog.Port}");
                if (analog.LastReading is float reading)
                {
                    builder.Append(CultureInfo.InvariantCulture, $", last error: {analog.State}, reading {reading:F1}C");
                }
                builder.AppendLine();
            }
        }
        return builder.Length == 0
            ? new Message(MessageType.Success, "No temperature sensors are configured")
            : new Message(MessageType.Success, builder.ToString().TrimEnd());
    }

    /// <summary>
    /// Send a code to a board as the generic message its parameter table describes
    /// </summary>
    /// <typeparam name="TMessage">Type of the CAN message</typeparam>
    /// <param name="board">CAN address of the board</param>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board said</returns>
    private async ValueTask<Message> SendGenericAsync<TMessage>(byte board, Commands.Code code,
                                                                CancellationToken cancellationToken)
        where TMessage : struct, ICanGenericMessage<TMessage>
    {
        TMessage message = default;
        message.FromCode(code);
        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        return response.ToMessage();
    }

    /// <summary>
    /// M950 H: create a heater
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// A heater is a port that is driven and a sensor that is read, and both belong to the same
    /// board: the board runs the control loop, so it has to read the temperature without going over
    /// the bus for it
    /// </remarks>
    private async ValueTask<Message> HandleCreateHeaterAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('H', out int heaterNumber) || heaterNumber < 0 || heaterNumber >= HeatManager.MaxHeaters)
        {
            return new Message(MessageType.Error, $"Heater number must be between 0 and {HeatManager.MaxHeaters - 1}");
        }

        if (!code.TryGetString('C', out string? port))
        {
            return await ReportHeaterAsync(heaterNumber, cancellationToken);
        }

        if (!code.TryGetInt('T', out int sensorNumber))
        {
            return new Message(MessageType.Error, "Missing sensor number; a heater needs T to say what reads it");
        }

        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!RemoteEndstops.TrySplitPort(port, "Heater port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            AnalogSensor? sensor = sensorNumber >= 0 && sensorNumber < model.Sensors.Analog.Count
                                   ? model.Sensors.Analog[sensorNumber]
                                   : null;
            if (sensor?.Port is null)
            {
                return new Message(MessageType.Error,
                                   $"Sensor {sensorNumber} is not configured; use M308 before M950 H");
            }

            // The control loop runs where the port is driven, so a sensor on another board would be
            // read over the bus at every step of it. RepRapFirmware refuses the same combination
            byte sensorBoard = IoPorts.RemoveBoardAddress(sensor.Port, out _);
            if (sensorBoard != board)
            {
                return new Message(MessageType.Error,
                    $"Heater {heaterNumber} is on board {board} but sensor {sensorNumber} is on board {sensorBoard}; "
                    + "a heater and the sensor that controls it must be on the same board");
            }

            Heater heater = heatManager.Create(heaterNumber);
            heater.Sensor = sensorNumber;
            heater.State = HeaterState.Off;
        }

        return await SendGenericAsync<CanMessageM950Heater>(board, code, cancellationToken);
    }

    /// <summary>
    /// Report one heater, as M950 H with no C does
    /// </summary>
    private async ValueTask<Message> ReportHeaterAsync(int heaterNumber, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Heater? heater = heatManager.Find(heaterNumber);
            return heater is null
                ? new Message(MessageType.Success, $"Heater {heaterNumber} is not configured")
                : new Message(MessageType.Success,
                    string.Create(CultureInfo.InvariantCulture,
                                  $"Heater {heaterNumber} uses sensor {heater.Sensor}, current temperature {heater.Current:F1}C"));
        }
    }

    /// <summary>
    /// M104 / M109 / M140 / M141 / M144 / M190 / M191: set a temperature, and wait for it where asked
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="heaters">Heaters the code addresses</param>
    /// <param name="wait">Whether to wait for them to get there</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The setpoint and the on/off decision go to the board together, because a heater told to reach
    /// a temperature is not heating until it is also told to switch on. A setpoint of zero switches
    /// it off, which is what M104 S0 means and what a slicer emits at the end of a print
    /// </remarks>
    private async ValueTask<Message> SetTemperaturesAsync(Commands.Code code, IReadOnlyList<int> heaters, bool wait,
                                                          CancellationToken cancellationToken)
    {
        if (heaters.Count == 0)
        {
            return new Message(MessageType.Warning, "No heater to set");
        }

        bool hasActive = code.TryGetFloat('S', out float active);
        bool hasStandby = code.TryGetFloat('R', out float standby);
        if (!hasActive && !hasStandby)
        {
            return await ReportTemperaturesAsync(cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            foreach (int heaterNumber in heaters)
            {
                if (heatManager.Find(heaterNumber) is Heater heater)
                {
                    if (hasActive)
                    {
                        heater.Active = active;
                    }
                    if (hasStandby)
                    {
                        heater.Standby = standby;
                    }
                    heater.State = !hasActive || active > 0.0f ? HeaterState.Active : HeaterState.Off;
                }
            }
        }

        List<Message> errors = [];
        foreach (int heaterNumber in heaters)
        {
            float target = hasActive ? active : standby;
            byte command = hasActive && active <= 0.0f
                ? CanMessageSetHeaterTemperatureV1.CommandOff
                : CanMessageSetHeaterTemperatureV1.CommandOn;
            if (await heatManager.SetTemperatureAsync(heaterNumber, target, command, cancellationToken) is string error)
            {
                errors.Add(new Message(MessageType.Error, error));
            }
        }

        if (errors.Count > 0)
        {
            return errors[0];
        }

        if (wait && !await heatManager.WaitForTemperaturesAsync(heaters, cancellationToken))
        {
            throw new System.OperationCanceledException();
        }
        return new Message();
    }

    /// <summary>
    /// M105: report the temperatures
    /// </summary>
    private async ValueTask<Message> ReportTemperaturesAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // RepRapFirmware leads with the bed so that a host parsing the line finds it where it
            // has always been, then lists the heaters in order
            if (heatManager.FirstBedHeater() is Heater bed)
            {
                builder.Append(CultureInfo.InvariantCulture, $"B:{bed.Current:F1} /{bed.Active:F1}");
            }

            for (int heaterNumber = 0; heaterNumber < model.Heat.Heaters.Count; heaterNumber++)
            {
                if (model.Heat.Heaters[heaterNumber] is Heater heater)
                {
                    builder.Append(CultureInfo.InvariantCulture,
                                   $" T{heaterNumber}:{heater.Current:F1} /{heater.Active:F1}");
                }
            }
        }
        return builder.Length == 0
            ? new Message(MessageType.Success, "No heaters are configured")
            : new Message(MessageType.Success, builder.ToString().TrimStart());
    }

    /// <summary>
    /// M116: wait for every heater that matters to reach its temperature
    /// </summary>
    private async ValueTask<Message> HandleWaitForTemperaturesAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<int> heaters = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (code.TryGetInt('P', out int toolNumber))
            {
                if (toolManager.Find(toolNumber) is not Tool tool)
                {
                    return new Message(MessageType.Error, $"Tool {toolNumber} not found");
                }
                heaters.AddRange(tool.Heaters);
            }
            else
            {
                // Every heater that is on. RepRapFirmware waits for all of them rather than only the
                // current tool's, which is what makes a bare M116 the "wait until the machine is
                // ready" a start.g ends with
                for (int heaterNumber = 0; heaterNumber < model.Heat.Heaters.Count; heaterNumber++)
                {
                    if (model.Heat.Heaters[heaterNumber] is not null)
                    {
                        heaters.Add(heaterNumber);
                    }
                }
            }
        }

        if (!await heatManager.WaitForTemperaturesAsync(heaters, cancellationToken))
        {
            throw new System.OperationCanceledException();
        }
        return new Message();
    }

    /// <summary>
    /// M302: allow or forbid cold extrusion, and set the temperatures it is judged against
    /// </summary>
    private async ValueTask<Message> HandleColdExtrusionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            bool seen = false;
            if (code.TryGetFloat('S', out float extrude))
            {
                model.Heat.ColdExtrudeTemperature = extrude;
                seen = true;
            }
            if (code.TryGetFloat('R', out float retract))
            {
                model.Heat.ColdRetractTemperature = retract;
                seen = true;
            }

            if (code.TryGetInt('P', out int allow))
            {
                // P is the blunt form: allowing cold extrusion is the same as saying it is permitted
                // at any temperature, which is how RepRapFirmware stores it
                seen = true;
                if (allow > 0)
                {
                    model.Heat.ColdExtrudeTemperature = 0.0f;
                    model.Heat.ColdRetractTemperature = 0.0f;
                }
            }

            if (!seen)
            {
                return new Message(MessageType.Success, string.Create(CultureInfo.InvariantCulture,
                    $"Cold extrusion is allowed above {model.Heat.ColdExtrudeTemperature:F1}C, "
                    + $"cold retraction above {model.Heat.ColdRetractTemperature:F1}C"));
            }
        }
        return new Message();
    }

    /// <summary>
    /// The heaters M140 or M141 addresses
    /// </summary>
    private async ValueTask<List<int>> BedOrChamberHeatersAsync(Commands.Code code, bool chamber,
                                                                CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return heatManager.BedOrChamberHeaters(chamber, code.TryGetInt('P', out int index) ? index : -1);
        }
    }

    /// <summary>
    /// The heaters M104 or M109 addresses
    /// </summary>
    /// <remarks>
    /// The tool named by T, or the selected one. A heater is a property of a tool, so a temperature
    /// with no tool to apply it to has nowhere to go - RepRapFirmware reports the same
    /// </remarks>
    private async ValueTask<List<int>> CurrentToolHeatersAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<int> heaters = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Tool? tool = code.TryGetInt('T', out int toolNumber) ? toolManager.Find(toolNumber) : toolManager.Current;
            if (tool is not null)
            {
                heaters.AddRange(tool.Heaters);
            }
        }
        return heaters;
    }
}
