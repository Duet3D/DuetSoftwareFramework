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
        // TODO RRF has no list form: S is required, and S >= MaxSensors is "Sensor number out of range"
        if (!code.TryGetInt('S', out int sensorNumber) || sensorNumber < 0)
        {
            return await ReportSensorsAsync(cancellationToken);
        }

        string? port = code.TryGetString('P', out string? portName) ? portName : null;
        byte board;

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            // TODO RRF's Heat::ConfigureSensor:
            // - P"nil" (with or without a board) deletes the sensor: P"nil" goes to the board it was on and the
            //   entry becomes null
            // - Y always deletes the old sensor first (P"nil" to its board) and starts from a fresh one
            // - P without Y is "Sensor N does not exist" for a new sensor, and an error when it moves the sensor
            //   to another board
            // - a new sensor starts notReady at 2000C and times out without reports
            AnalogSensor? sensor = sensorNumber < model.Sensors.Analog.Count ? model.Sensors.Analog[sensorNumber] : null;
            if (port is not null)
            {
                if (!IoPorts.TrySplitPort(port, "Sensor port", out board, out _, out string? error))
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
            else if (!IoPorts.TrySplitPort(sensor.Port, "Sensor port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            // An unrecognised name is left for the board to judge rather than refused here: the CAN
            // message carries Y as the operator wrote it, and which types a board supports is the
            // board's business. The object model keeps whatever it had, which is worse than nothing
            // only for a sensor type this side does not know the name of
            // TODO this misses the hyphenated RRF names (mcu-temp, linear-analog, rtd-max31865, ...);
            // AnalogSensorTypeConverter already maps the reduced forms
            if (code.TryGetString('Y', out string? type)
                && System.Enum.TryParse(type, ignoreCase: true, out AnalogSensorType parsed))
            {
                sensor.Type = parsed;
            }
            if (code.TryGetString('A', out string? name))
            {
                sensor.Name = name;
            }

            // TODO which fields a parameter sets depends on the type in RRF: a linear-analog sensor's B and C are its
            // low and high readings, a thermistor's L and H are ADC corrections and B without C resets C, and F/W/K
            // are not recorded. RRF also refuses U outside +-20 and V outside +-0.2
            // The board is what turns a resistance into a temperature, but the constants it does
            // that with are how the machine is built - a thermistor's beta is a property of the part
            // that was fitted. Storing only the port would leave the model unable to describe it
            if (code.TryGetFloat('T', out float r25))
            {
                sensor.R25 = r25;
            }
            if (code.TryGetFloat('B', out float beta))
            {
                sensor.Beta = beta;
            }
            if (code.TryGetFloat('C', out float shC))
            {
                sensor.C = shC;
            }
            if (code.TryGetFloat('R', out float seriesResistor))
            {
                sensor.RRef = seriesResistor;
            }
            if (code.TryGetFloat('L', out float lowReading))
            {
                sensor.LowReading = lowReading;
            }
            if (code.TryGetFloat('H', out float highReading))
            {
                sensor.HighReading = highReading;
            }
            if (code.TryGetFloat('U', out float offsetAdj))
            {
                sensor.OffsetAdj = offsetAdj;
            }
            if (code.TryGetFloat('V', out float slopeAdj))
            {
                sensor.SlopeAdj = slopeAdj;
            }
        }

        // The board is what reads the sensor, so it is what has to be told how. The parameter table
        // is the message, which is what makes this a repackaging rather than a reimplementation
        // TODO the model was changed before the board answered; RRF only keeps a new sensor the board accepted,
        // and deletes it again (P"nil") when the board refuses it
        return await linkInterface.SendCodeRequestAsync<CanMessageM308V1>(board, code, cancellationToken);
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
            if (!IoPorts.TrySplitPort(port, "Heater port", out board, out _, out string? error))
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
            heater.Port = port;
            heater.State = HeaterState.Off;
            if (code.TryGetFloat('Q', out float frequency))
            {
                heater.Frequency = frequency;
            }
        }

        return await linkInterface.SendCodeRequestAsync<CanMessageM950Heater>(board, code, cancellationToken);
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
    /// M104 / M109 / M140 / M141 / M190 / M191: set a temperature, and wait for it where asked
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
        // TODO RRF M140 with no bed heater refuses S > 0 and R but accepts S <= 0; M104/M109 on a tool with no
        // heaters still completes (and M109 still selects the tool)
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

        // TODO behaviour RRF has that this does not:
        // - M104/M109: R takes precedence over S, the one value sets both active and standby (and the tool's
        //   active[]/standby[]), and R makes M109 wait while cooling. M140: R alone only stores standby and
        //   does not switch the heater on
        // - only S <= -273 switches off, leaving active unchanged; S0 sets 0 and switches the heater on
        // - a temperature outside the heater's limits, or a heater in fault, is refused before anything changes
        // - M104/M109 on a tool other than the current one puts that tool on standby, and a heater shared
        //   with the current tool is left alone
        // As written, R alone marks the heater active but leaves Active, so the M109 wait reads the old setpoint
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

        // Every heater the code named is told, whether or not an earlier one refused. RepRapFirmware's
        // SetToolHeaters (GCodes.cpp) walks the whole tool without testing anything, and stopping half
        // way here would leave the rest of a tool's heaters at whatever they were while the object
        // model said otherwise. What every heater said is reported together
        List<Message> replies = [];
        foreach (int heaterNumber in heaters)
        {
            float target = hasActive ? active : standby;
            byte command = hasActive && active <= 0.0f
                ? CanMessageSetHeaterTemperatureV1.CommandOff
                : CanMessageSetHeaterTemperatureV1.CommandOn;
            replies.Add(await heatManager.SetTemperatureAsync(heaterNumber, target, command, cancellationToken));
        }

        Message reply = replies.ToMessage();
        if (!reply.Succeeded())
        {
            return reply;
        }

        if (wait)
        {
            // Another channel may move while the heaters come up
            code.ReleaseMovementLock();
            if (!await heatManager.WaitForTemperaturesAsync(heaters, cancellationToken))
            {
                throw new System.OperationCanceledException();
            }
        }
        return reply;
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
            // TODO RRF M116: S is the tolerance (must be > 0); P takes a list, a bare P means every tool, and a
            // missing tool is skipped; H waits for heaters and C for chamber slots; P, H and C combine; with
            // none of them only tool, bed and chamber heaters are waited for, not every heater
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

        // Another channel may move while the heaters come up
        code.ReleaseMovementLock();
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

            // TODO RRF keeps P as a flag beside the two minimums, which it reports as 0 only while P1 is in force:
            // P0 brings the stored minimums back, and S/R under P1 are stored without forbidding anything. Zeroing
            // them here loses them, so P0 after P1 leaves cold extrusion allowed. The minimums need somewhere in the
            // object model to live while P1 hides them. RRF also waits for standstill before changing any of these
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
    /// The heaters M140 or M141 addresses, assigning one first where H says which
    /// </summary>
    /// <remarks>
    /// <c>M140 H0</c> is what makes heater 0 the bed, and without it the mapping is empty and M140
    /// has nothing to address. RepRapFirmware treats the assignment and the temperature as one code
    /// for that reason: a config.g says which heater the bed is and a job says how hot it should be,
    /// and both are M140
    /// </remarks>
    private async ValueTask<List<int>> BedOrChamberHeatersAsync(Commands.Code code, bool chamber,
                                                                CancellationToken cancellationToken)
    {
        // TODO RRF: no P means slot 0, not every slot, and P is range-checked; H takes a list, refuses invalid
        // heaters and heaters already used by a tool or the other kind of slot, switches off the slot's old
        // heaters, and sets the new ones' default bed/chamber model and over-temperature monitor
        // (Heat::SetFunction); heat.bedHeaters/chamberHeaters are never written
        int index = code.TryGetInt('P', out int given) ? given : -1;
        if (code.TryGetInt('H', out int heaterNumber))
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                heatManager.AssignBedOrChamberHeater(chamber, index < 0 ? 0 : index, heaterNumber);
            }
        }

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return heatManager.BedOrChamberHeaters(chamber, index);
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

    /// <summary>
    /// M143: set the limits a heater is monitored against
    /// </summary>
    /// <remarks>
    /// A monitor is a sensor, a limit and what to do when the limit is crossed, and the board holds
    /// all of them because the board is what reads the sensor - a limit enforced from this side would
    /// be enforced at the speed of the CAN bus. RepRapFirmware sends the whole set each time for the
    /// same reason it does elsewhere: the message carries the array, so a partial update would need
    /// the board to remember which entries it had been given
    /// </remarks>
    private async ValueTask<Message> HandleHeaterMonitorAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int heaterNumber = code.TryGetInt('H', out int given) ? given : -1;
        if (heaterNumber < 0)
        {
            return new Message(MessageType.Error, "Missing heater number");
        }

        byte board;
        CanMessageSetHeaterMonitors message = new() { Heater = (ushort)heaterNumber };
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (heatManager.Find(heaterNumber) is not Heater heater)
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} not found");
            }
            if (!heatManager.TryGetBoard(heater, out board))
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} is not on an expansion board");
            }

            if (code.TryGetFloat('S', out float limit))
            {
                int index = code.TryGetInt('P', out int monitorIndex) ? monitorIndex : 0;
                while (heater.Monitors.Count <= index)
                {
                    heater.Monitors.Add(new HeaterMonitor());
                }

                HeaterMonitor monitor = heater.Monitors[index];
                monitor.Limit = limit;
                monitor.Sensor = code.GetInt('T', defaultValue: heater.Sensor);
                monitor.Action = code.GetEnum<HeaterMonitorAction>('A', HeaterMonitorAction.GenerateFault);
                monitor.Condition = code.TryGetInt('C', out int condition)
                                    ? (HeaterMonitorCondition)condition
                                    : HeaterMonitorCondition.TooHigh;
            }
            else
            {
                return ReportHeaterMonitors(heaterNumber, heater);
            }

            for (int index = 0; index < heater.Monitors.Count && index < CanHeaterMonitorArray7.Length; index++)
            {
                HeaterMonitor monitor = heater.Monitors[index];
                message.Monitors[index] = new CanHeaterMonitor
                {
                    Limit = monitor.Limit ?? 0.0f,
                    Sensor = (sbyte)monitor.Sensor,
                    Action = (byte)(monitor.Action ?? HeaterMonitorAction.GenerateFault),
                    Trigger = (sbyte)monitor.Condition
                };
            }
        }

        return await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
    }

    /// <summary>
    /// Report a heater's monitors, as M143 with only H does
    /// </summary>
    private static Message ReportHeaterMonitors(int heaterNumber, Heater heater)
    {
        StringBuilder builder = new();
        for (int index = 0; index < heater.Monitors.Count; index++)
        {
            HeaterMonitor monitor = heater.Monitors[index];
            builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Heater {heaterNumber} monitor {index}: sensor {monitor.Sensor}, "
                + $"{monitor.Condition} {monitor.Limit:F1}C, action {monitor.Action}"));
        }
        return builder.Length == 0
            ? new Message(MessageType.Success, $"Heater {heaterNumber} has no monitors")
            : new Message(MessageType.Success, builder.ToString().TrimEnd());
    }

    /// <summary>
    /// M570: how long a heater may misbehave before it is faulted
    /// </summary>
    private async ValueTask<Message> HandleHeaterFaultDetectionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('H', out int heaterNumber))
        {
            return new Message(MessageType.Error, "Missing heater number");
        }

        byte board;
        CanMessageSetHeaterFaultDetectionParameters message = new() { Heater = (ushort)heaterNumber };
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (heatManager.Find(heaterNumber) is not Heater heater)
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} not found");
            }
            if (!heatManager.TryGetBoard(heater, out board))
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} is not on an expansion board");
            }

            if (code.TryGetFloat('P', out float faultTime))
            {
                heater.MaxHeatingFaultTime = faultTime;
            }
            if (code.TryGetFloat('T', out float excursion))
            {
                heater.MaxTempExcursion = excursion;
            }
            if (code.TryGetInt('S', out int badReadings))
            {
                heater.MaxBadReadings = badReadings;
            }

            message.MaxFaultTime = heater.MaxHeatingFaultTime;
            message.MaxTempExcursion = heater.MaxTempExcursion;
            message.MaxBadTemperatureCount = (uint)heater.MaxBadReadings;
        }

        return await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
    }

    /// <summary>
    /// M562: clear a heater fault
    /// </summary>
    /// <remarks>
    /// The fault is the board's, so clearing it is a command to the board rather than a change to the
    /// object model - the board reports the new state back and that is what <c>heat.heaters[].state</c>
    /// then says. With no P every heater is cleared, as in RepRapFirmware
    /// </remarks>
    private async ValueTask<Message> HandleClearHeaterFaultAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<int> heaters = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (code.TryGetInt('P', out int heaterNumber))
            {
                heaters.Add(heaterNumber);
            }
            else
            {
                for (int index = 0; index < model.Heat.Heaters.Count; index++)
                {
                    if (model.Heat.Heaters[index] is not null)
                    {
                        heaters.Add(index);
                    }
                }
            }
        }

        List<Message> replies = [];
        foreach (int heaterNumber in heaters)
        {
            Message reply = await heatManager.SetTemperatureAsync(heaterNumber, 0.0f,
                                                                  CanMessageSetHeaterTemperatureV1.CommandResetFault,
                                                                  cancellationToken);
            if (!reply.Succeeded())
            {
                return reply;
            }
            replies.Add(reply);
        }
        return replies.ToMessage();
    }

    /// <summary>
    /// M307: set or report a heater's process model
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>Heater::SetOrReportModel</c> and <c>Heater::SetModel</c>. Parameters not given keep
    /// their current values, except I, which is off unless given. The model is kept here as well as on the
    /// board, as RepRapFirmware keeps it on the main board, and is kept even if the board refuses it
    /// </remarks>
    private async ValueTask<Message> HandleHeaterModelAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int heaterNumber = code.GetInt('H', min: 0, max: HeatManager.MaxHeaters - 1);
        byte board;
        float highestTemperatureLimit;
        CanMessageHeaterModelV3 message;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (heatManager.Find(heaterNumber) is not Heater heater)
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} not found");
            }
            if (!heatManager.TryGetBoard(heater, out board))
            {
                return new Message(MessageType.Error, $"Heater {heaterNumber} is not on an expansion board");
            }

            HeaterFunction function = heatManager.GetHeaterFunction(heaterNumber);
            message = FopDt.ToCanMessage(heaterNumber, function, heater.Model);
            ref Link.Protocol.CanMessages.HeaterModel basicModel = ref message.BasicModel;
            bool seen = false;

            if (code.TryGetFloatArray('K', 2, out float[]? coolingRates))
            {
                seen = true;
                basicModel.BasicCoolingRate = coolingRates[0];
                basicModel.FanCoolingRate = (coolingRates.Length == 2) ? coolingRates[1] : 0.0f;
                if (code.TryGetFloat('E', out float exponent))
                {
                    basicModel.CoolingRateExponent = exponent;
                }
            }
            else if (code.TryGetFloatArray('C', 2, out float[]? timeConstants, pad: true))
            {
                seen = true;
                basicModel.BasicCoolingRate = 100.0f / timeConstants[0];
                basicModel.FanCoolingRate = 100.0f / timeConstants[1] - basicModel.BasicCoolingRate;
                basicModel.CoolingRateExponent = 1.0f;
            }

            if (code.TryGetPositiveFloat('R', out float heatingRate))
            {
                seen = true;
                basicModel.HeatingRate = heatingRate;
            }
            else if (code.TryGetFloat('A', out float gain))
            {
                seen = true;
                basicModel.HeatingRate = gain * basicModel.BasicCoolingRate * 0.01f;
            }

            if (code.TryGetFloat('D', out float deadTime))
            {
                seen = true;
                basicModel.DeadTime = deadTime;
            }
            if (code.TryGetInt('B', out int dontUsePid))
            {
                seen = true;
                basicModel.UsePid = dontUsePid == 0;
            }
            if (code.TryGetFloat('S', out float maxPwm))
            {
                seen = true;
                message.MaxPwm = maxPwm;
            }
            if (code.TryGetFloat('V', out float voltage))
            {
                seen = true;
                basicModel.StandardVoltage = voltage;
            }
            int inversion = 0;
            if (code.TryGetInt('I', out int givenInversion))
            {
                seen = true;
                inversion = givenInversion;
            }

            if (!seen)
            {
                if (!message.Enabled)
                {
                    return new Message(MessageType.Success, $"Heater {heaterNumber} is disabled because its model is undefined");
                }
                StringBuilder report = new();
                FopDt.AppendModelParameters(report, message, function == HeaterFunction.Tool, FopDt.SetModelPidTemperature);
                return new Message(MessageType.Success, report.ToString());
            }

            message.Inverted = inversion is 1 or 3;
            message.Enabled = true;
            if (FopDt.Validate(message) is string error)
            {
                return new Message(MessageType.Error, $"bad model parameters: {error}");
            }

            // TODO record that the user set this model once Heat::SetFunction's default models are ported, so that
            // giving the heater a function does not replace it
            FopDt.Apply(message, heater.Model, FopDt.SetModelPidTemperature);
            highestTemperatureLimit = HeatManager.GetHighestTemperatureLimit(heater);
        }

        Message reply = await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
        if (reply.Succeeded())
        {
            float predictedMaxTemp = message.BasicModel.EstimateMaxTemperatureRise() + FopDt.NormalAmbientTemperature;
            float noWarnTemp = (highestTemperatureLimit - FopDt.NormalAmbientTemperature) * 1.5f + 50.0f;
            if (predictedMaxTemp > noWarnTemp)
            {
                return new Message(MessageType.Warning,
                                   $"Heater {heaterNumber} predicted maximum temperature at full power is {(int)predictedMaxTemp}°C");
            }
        }
        return reply;
    }
}
