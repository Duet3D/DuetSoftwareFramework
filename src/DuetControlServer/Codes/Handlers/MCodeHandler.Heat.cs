using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    /// M308: create, configure, report or delete a temperature sensor
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::ConfigureSensor</c>. A sensor belongs to the board carrying its port, and
    /// that board is the one that reads it, so the parameters are repackaged as the generic message its
    /// table describes and answered by that board (<c>RemoteSensor::Configure</c>). What is recorded here is
    /// what the object model needs to describe the machine. The one sensor without a port is the main
    /// board's MCU temperature sensor, which is read from <c>boards[0].mcuTemp</c>
    /// </remarks>
    private async ValueTask<Message> HandleConfigureSensorAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int sensorNumber = (int)code.GetUInt('S', max: SensorManager.MaxSensors - 1, errorString: _ => "Sensor number out of range");

        string? port = null;
        if (code.TryGetString('P', out string? portName))
        {
            if (IoPorts.IsNoPort(portName))
            {
                await DeleteSensorAsync(sensorNumber, cancellationToken);
                return new Message();
            }
            port = portName;
        }

        return code.TryGetString('Y', out string? typeName)
            ? await CreateSensorAsync(code, sensorNumber, port, typeName, cancellationToken)
            : await ConfigureSensorAsync(code, sensorNumber, port, cancellationToken);
    }

    /// <summary>
    /// M308 with Y: replace a sensor with a new one
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="sensorNumber">The sensor</param>
    /// <param name="port">Port the code gave, or null</param>
    /// <param name="typeName">Sensor type name as written</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The old sensor is deleted first, whether or not the new one can be made, and the new one starts from
    /// nothing rather than from the old one's settings. A sensor whose settings are refused, whose board refuses
    /// it or whose board does not answer is released on that board again (<c>Heat::ConfigureSensor</c>, which
    /// creates the sensor before configuring it, and <c>~RemoteSensor</c>)
    /// </remarks>
    private async ValueTask<Message> CreateSensorAsync(Commands.Code code, int sensorNumber, string? port, string typeName,
                                                       CancellationToken cancellationToken)
    {
        await DeleteSensorAsync(sensorNumber, cancellationToken);

        AnalogSensorType type = AnalogSensorTypeConverter.Parse(typeName);
        if (port is null)
        {
            return await CreateMainBoardSensorAsync(sensorNumber, type, typeName,
                                                    SensorSettings.Read(code, type, portChanged: true), cancellationToken);
        }

        if (!IoPorts.TrySplitPort(port, "Sensor port", out byte board, out _, out string? error))
        {
            return new Message(MessageType.Error, error);
        }

        SensorSettings settings;
        CanMessageM308V1 message = default;
        try
        {
            settings = SensorSettings.Read(code, type, portChanged: true);
            message.FromCode(code);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await ReleaseSensorAsync(board, sensorNumber, cancellationToken);
            throw;
        }

        AnalogSensor sensor;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            sensor = sensorManager.Create(sensorNumber);
            sensor.Type = type;
            sensor.Port = port;
            settings.ApplyTo(sensor);
        }

        Message reply = await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
        if (!reply.Succeeded())
        {
            await DeleteSensorAsync(sensorNumber, cancellationToken, sensor);
        }
        return reply;
    }

    /// <summary>
    /// M308 with Y and no P: a sensor on the main board, of which only the MCU temperature sensor exists
    /// </summary>
    /// <remarks>
    /// <c>Heat::ConfigureSensor</c> puts a sensor without a port on the main board. Board 0 runs
    /// DuetCANMaster and has no sensor inputs or drivers, so its MCU temperature is the only thing it can
    /// read
    /// </remarks>
    private async ValueTask<Message> CreateMainBoardSensorAsync(int sensorNumber, AnalogSensorType type, string typeName,
                                                                SensorSettings settings, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case AnalogSensorType.McuTemp:
                using (await model.AccessReadWriteAsync(cancellationToken))
                {
                    AnalogSensor sensor = sensorManager.Create(sensorNumber);
                    sensor.Type = type;
                    settings.ApplyTo(sensor);
                }
                return new Message();

            case AnalogSensorType.Unknown:
                return new Message(MessageType.Error, $"Unknown sensor type name \"{DuetAPI.Utility.ReducedString.Reduce(typeName)}\"");

            case AnalogSensorType.Drivers or AnalogSensorType.DriversDuex:
                return new Message(MessageType.Error, CanAddresses.NoHardwareMessage($"Sensor {sensorNumber}"));

            default:
                return new Message(MessageType.Error, "Missing port name parameter");
        }
    }

    /// <summary>
    /// M308 without Y: amend or report an existing sensor
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="sensorNumber">The sensor</param>
    /// <param name="port">Port the code gave, or null</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// <c>Heat::ConfigureSensor</c> then <c>RemoteSensor::Configure</c>: the board is sent only what the code
    /// gave, and a board that finds nothing to change answers with its report, which is prefixed with the
    /// sensor's number and name. A sensor moves to another board only by being created again
    /// </remarks>
    private async ValueTask<Message> ConfigureSensorAsync(Commands.Code code, int sensorNumber, string? port,
                                                          CancellationToken cancellationToken)
    {
        AnalogSensor sensor;
        byte board;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (sensorManager.Find(sensorNumber) is not AnalogSensor existing)
            {
                return new Message(MessageType.Error, $"Sensor {sensorNumber} does not exist");
            }
            sensor = existing;
            board = SensorManager.GetBoard(sensor);
        }

        if (port is not null && IoPorts.RemoveBoardAddress(port, out _) != board)
        {
            return new Message(MessageType.Error, "Sensor type parameter needed when moving a sensor to a different board");
        }

        if (CanAddresses.HasNoHardware(board))
        {
            return await ConfigureMainBoardSensorAsync(code, sensorNumber, sensor, cancellationToken);
        }

        if (port is not null && !IoPorts.TrySplitPort(port, "Sensor port", out _, out _, out string? error))
        {
            return new Message(MessageType.Error, error);
        }

        SensorSettings settings = SensorSettings.Read(code, sensor.Type, portChanged: port is not null);
        CanMessageM308V1 message = default;
        message.FromCode(code);

        // The name is this side's alone, so it is set whatever the board says (ConfigureCommonParameters)
        if (settings.Name is string name)
        {
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                sensor.Name = name;
            }
        }

        Message reply = await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
        if (!reply.Succeeded())
        {
            return reply;
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (reply.Content.StartsWith("type ", StringComparison.Ordinal))
            {
                reply.Content = SensorReportPrefix(sensorNumber, sensor) + reply.Content;
            }
            else if (ReferenceEquals(sensorManager.Find(sensorNumber), sensor))
            {
                if (port is not null)
                {
                    sensor.Port = port;
                }
                settings.ApplyTo(sensor);
            }
        }
        return reply;
    }

    /// <summary>
    /// M308 without Y for the main board's MCU temperature sensor, whose only settings are the common ones
    /// </summary>
    /// <remarks>RepRapFirmware's <c>TemperatureSensor::Configure</c>, which reports the sensor when nothing changes</remarks>
    private async ValueTask<Message> ConfigureMainBoardSensorAsync(Commands.Code code, int sensorNumber, AnalogSensor sensor,
                                                                   CancellationToken cancellationToken)
    {
        SensorSettings settings = SensorSettings.Read(code, sensor.Type, portChanged: false);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!ReferenceEquals(sensorManager.Find(sensorNumber), sensor))
            {
                return new Message(MessageType.Error, $"Sensor {sensorNumber} does not exist");
            }
            if (settings.SeenCommon)
            {
                settings.ApplyTo(sensor);
                return new Message();
            }

            // TemperatureSensor::CopyBasicDetails
            float reading = sensor.LastReading ?? 0.0f;
            string lastError = JsonNamingPolicy.CamelCase.ConvertName(sensorManager.GetLastRealError(sensorNumber).ToString());
            string readingText = sensor.OffsetAdj != 0.0f || sensor.SlopeAdj != 0.0f
                ? string.Create(CultureInfo.InvariantCulture,
                                $"offset adjustment {sensor.OffsetAdj:F1}, slope adjustment {sensor.SlopeAdj:F3}, adjusted reading {reading:F1}")
                : string.Create(CultureInfo.InvariantCulture, $"reading {reading:F1}");
            return new Message(MessageType.Success,
                $"{SensorReportPrefix(sensorNumber, sensor)}type {SensorManager.McuSensorDescription}, last error {lastError}, {readingText}");
        }
    }

    /// <summary>
    /// What a sensor's report starts with: its number and, if it has one, its name
    /// </summary>
    /// <remarks>RemoteSensor::Configure and TemperatureSensor::CopyBasicDetails</remarks>
    private static string SensorReportPrefix(int sensorNumber, AnalogSensor sensor)
        => string.IsNullOrEmpty(sensor.Name) ? $"Sensor {sensorNumber} " : $"Sensor {sensorNumber} ({sensor.Name}) ";

    /// <summary>
    /// Take a sensor out of the object model and tell its board to let go of the port
    /// </summary>
    /// <param name="sensorNumber">The sensor, which may not exist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="expected">The sensor to delete, or null for whichever holds the number</param>
    /// <remarks>
    /// RepRapFirmware's <c>Heat::DeleteSensor</c>. The sensor leaves the object model whatever its board says to
    /// <see cref="ReleaseSensorAsync"/>, as a destructor cannot fail
    /// </remarks>
    private async ValueTask DeleteSensorAsync(int sensorNumber, CancellationToken cancellationToken, AnalogSensor? expected = null)
    {
        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (sensorManager.Find(sensorNumber) is not AnalogSensor sensor || (expected is not null && !ReferenceEquals(sensor, expected)))
            {
                return;
            }
            board = SensorManager.GetBoard(sensor);
            sensorManager.Delete(sensorNumber);
        }

        await ReleaseSensorAsync(board, sensorNumber, cancellationToken);
    }

    /// <summary>
    /// Tell a board to free the port of a sensor number
    /// </summary>
    /// <param name="board">The board</param>
    /// <param name="sensorNumber">The sensor</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <c>~RemoteSensor</c>'s <c>M308 S P"nil"</c>, whose reply is not read: a board that is unreachable or
    /// has no such sensor cannot be made to let go of it. The caller must not hold the object model lock
    /// </remarks>
    private async ValueTask ReleaseSensorAsync(byte board, int sensorNumber, CancellationToken cancellationToken)
    {
        if (!CanAddresses.HasNoHardware(board))
        {
            CanMessageM308V1 release = default;
            release.S = (byte)sensorNumber;
            release.P = IoPorts.NoPortName;
            await linkInterface.SendCanRequestAsync(board, in release, cancellationToken);
        }
    }

    /// <summary>
    /// What an M308 sets in <c>sensors.analog[]</c>, read from the code before anything is changed so that a
    /// bad value refuses it with nothing to undo
    /// </summary>
    /// <remarks>
    /// Which field a letter sets depends on the type, as in RepRapFirmware's <c>Configure</c> of each sensor
    /// class: B and C are a thermistor's beta and Steinhart-Hart C but a linear analog sensor's low and high
    /// readings. The common parameters A, U and V are <c>TemperatureSensor::ConfigureCommonParameters</c>
    /// </remarks>
    private sealed class SensorSettings
    {
        /// <summary>Name given by A</summary>
        public string? Name { get; private init; }

        /// <summary>Whether A, U or V was given</summary>
        public bool SeenCommon { get; private init; }

        private float? _offsetAdj, _slopeAdj;
        private float? _r25, _beta, _c, _rRef, _lowReading, _highReading, _radiationExponent;
        private int? _adcLowOffset, _adcHighOffset, _mainsFrequency, _wires, _objectMix, _auxMix;
        private bool? _filtered;
        private string? _thermocoupleType;
        private bool _clearAdcOffsets;

        /// <summary>
        /// Value of L or H that asks a thermistor's board to measure the ADC correction itself
        /// </summary>
        /// <remarks>RepRapFirmware's <c>Thermistor::ConfigureLParam</c> and <c>ConfigureHParam</c></remarks>
        private const int MeasureAdcOffset = 999;

        /// <summary>
        /// Range of a thermopile sensor's radiation exponent, outside which its board ignores R
        /// </summary>
        /// <remarks>Duet3Expansion's <c>TPiS_1T_1086_L5_5::Configure</c></remarks>
        private const float MinRadiationExponent = 3.8f, MaxRadiationExponent = 4.4f;

        /// <summary>
        /// Read the settings of a sensor of the given type from a code
        /// </summary>
        /// <param name="code">The code</param>
        /// <param name="type">Type of the sensor the code configures</param>
        /// <param name="portChanged">Whether the code assigns the sensor a port</param>
        /// <returns>The settings</returns>
        /// <exception cref="DuetAPI.GCodeException">A value is out of range</exception>
        public static SensorSettings Read(Commands.Code code, AnalogSensorType type, bool portChanged)
        {
            bool seenName = code.TryGetString('A', out string? name);
            bool seenOffset = code.TryGetFloat('U', out float? offsetAdj, min: -20.0f, max: 20.0f);
            bool seenSlope = code.TryGetFloat('V', out float? slopeAdj, min: -0.2f, max: 0.2f);
            SensorSettings settings = new()
            {
                Name = name,
                SeenCommon = seenName || seenOffset || seenSlope,
                _offsetAdj = offsetAdj,
                _slopeAdj = slopeAdj
            };

            switch (type)
            {
                case AnalogSensorType.Thermistor or AnalogSensorType.PT1000:
                    // Thermistor::Configure. A new port clears the ADC corrections (InitPort)
                    settings._clearAdcOffsets = portChanged;
                    settings._rRef = OptionalFloat(code, 'R');
                    if (type == AnalogSensorType.Thermistor)
                    {
                        settings.ReadThermistorCurve(code);
                    }
                    // TODO L999 and H999 make the board measure the correction, which only its reply states
                    settings._adcLowOffset = OptionalInt(code, 'L') is int low and not MeasureAdcOffset ? low : null;
                    settings._adcHighOffset = OptionalInt(code, 'H') is int high and not MeasureAdcOffset ? high : null;
                    break;

                case AnalogSensorType.LinearAnalog:
                    settings._filtered = OptionalInt(code, 'F') is int filtered ? filtered >= 1 : null;
                    settings._lowReading = OptionalFloat(code, 'B');
                    settings._highReading = OptionalFloat(code, 'C');
                    break;

                case AnalogSensorType.MAX31865:
                    settings._rRef = OptionalFloat(code, 'R');
                    settings._wires = OptionalInt(code, 'W');
                    settings._mainsFrequency = OptionalInt(code, 'F');
                    break;

                case AnalogSensorType.MAX31856:
                    settings._thermocoupleType = code.TryGetString('K', out string? thermocoupleType) ? thermocoupleType : null;
                    settings._mainsFrequency = OptionalInt(code, 'F');
                    break;

                case AnalogSensorType.ThermopileObject:
                    // Duet3Expansion's TPiS_1T_1086_L5_5::Configure: T, B and C describe the environment thermistor
                    settings.ReadThermistorCurve(code);
                    settings._objectMix = OptionalInt(code, 'F');
                    settings._auxMix = OptionalInt(code, 'W');
                    settings._radiationExponent = OptionalFloat(code, 'R') is float exponent
                                                  and >= MinRadiationExponent and <= MaxRadiationExponent ? exponent : null;
                    break;
            }
            return settings;
        }

        /// <summary>
        /// Read a thermistor's T, B and C, where B without C sets C to zero
        /// </summary>
        /// <param name="code">The code</param>
        /// <remarks>RepRapFirmware's <c>Thermistor::Configure</c></remarks>
        private void ReadThermistorCurve(Commands.Code code)
        {
            _beta = OptionalFloat(code, 'B');
            _c = OptionalFloat(code, 'C') ?? (_beta is not null ? 0.0f : null);
            _r25 = OptionalFloat(code, 'T');
        }

        private static float? OptionalFloat(Commands.Code code, char letter)
            => code.TryGetFloat(letter, out float? value) ? value : null;

        private static int? OptionalInt(Commands.Code code, char letter)
            => code.TryGetInt(letter, out int? value) ? value : null;

        /// <summary>
        /// Write what the code set into a sensor
        /// </summary>
        /// <param name="sensor">The sensor</param>
        /// <remarks>The caller must hold the object model write lock</remarks>
        public void ApplyTo(AnalogSensor sensor)
        {
            if (Name is not null)
            {
                sensor.Name = Name;
            }
            if (_clearAdcOffsets)
            {
                sensor.AdcLowOffset = sensor.AdcHighOffset = null;
            }
            sensor.OffsetAdj = _offsetAdj ?? sensor.OffsetAdj;
            sensor.SlopeAdj = _slopeAdj ?? sensor.SlopeAdj;
            sensor.R25 = _r25 ?? sensor.R25;
            sensor.Beta = _beta ?? sensor.Beta;
            sensor.C = _c ?? sensor.C;
            sensor.RRef = _rRef ?? sensor.RRef;
            sensor.LowReading = _lowReading ?? sensor.LowReading;
            sensor.HighReading = _highReading ?? sensor.HighReading;
            sensor.AdcLowOffset = _adcLowOffset ?? sensor.AdcLowOffset;
            sensor.AdcHighOffset = _adcHighOffset ?? sensor.AdcHighOffset;
            sensor.MainsFrequency = _mainsFrequency ?? sensor.MainsFrequency;
            sensor.Wires = _wires ?? sensor.Wires;
            sensor.Filtered = _filtered ?? sensor.Filtered;
            sensor.ThermocoupleType = _thermocoupleType ?? sensor.ThermocoupleType;
            sensor.ObjectMix = _objectMix ?? sensor.ObjectMix;
            sensor.AuxMix = _auxMix ?? sensor.AuxMix;
            sensor.RadiationExponent = _radiationExponent ?? sensor.RadiationExponent;
        }
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

            if (sensorManager.Find(sensorNumber) is not AnalogSensor sensor)
            {
                return new Message(MessageType.Error,
                                   $"Sensor {sensorNumber} is not configured; use M308 before M950 H");
            }

            // The control loop runs where the port is driven, so a sensor on another board would be
            // read over the bus at every step of it. RepRapFirmware refuses the same combination
            byte sensorBoard = SensorManager.GetBoard(sensor);
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
    /// Whether a code came from a file channel, which keeps waiting for a heater that is in a fault
    /// </summary>
    private static bool IsFileChannel(Commands.Code code) => code.Channel is DuetAPI.CodeChannel.File or DuetAPI.CodeChannel.File2;

    /// <summary>
    /// The target temperature of M104, M109, M190 or M191: R, which also waits while cooling, or else S
    /// </summary>
    /// <returns>False if neither is given</returns>
    private static bool TryGetTargetTemperature(Commands.Code code, out float temperature, out bool waitWhenCooling)
    {
        waitWhenCooling = code.TryGetFloat('R', out temperature);
        return waitWhenCooling || code.TryGetFloat('S', out temperature);
    }

    /// <summary>
    /// M104 / M109: set a tool's active and standby temperatures, and for M109 select it if no tool is selected and wait
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="wait">True for M109</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's GCodes2.cpp case 104/109 and <c>GCodes::SetToolHeaters</c>. The tool is T, or the selected tool,
    /// or the lowest-numbered tool. Both temperatures are set because slicers do not know there are two. A tool that is
    /// not selected goes on standby, and the selected tool is switched to active again. M109 waits for the selected tool
    /// </remarks>
    private async ValueTask<Message> HandleToolTemperatureAsync(Commands.Code code, bool wait, CancellationToken cancellationToken)
    {
        if (!TryGetTargetTemperature(code, out float temperature, out bool waitWhenCooling))
        {
            return new Message();
        }

        int toolNumber, heaterCount;
        bool noToolSelected;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Tool? tool = code.TryGetInt('T', out int given) ? toolManager.Find(given) : toolManager.CurrentOrDefault;
            if (tool is null)
            {
                return new Message(MessageType.Error, "Invalid tool number");
            }
            toolNumber = tool.Number;
            heaterCount = tool.Heaters.Count;
            noToolSelected = toolManager.Current is null;
        }

        for (int index = 0; index < heaterCount; index++)
        {
            foreach (bool active in (bool[])[true, false])
            {
                Message reply = await toolManager.SetToolHeaterTemperatureAsync(toolNumber, index, temperature, active, cancellationToken);
                if (!reply.Succeeded())
                {
                    return reply;
                }
            }
        }

        if (wait && noToolSelected)
        {
            Message selected = await toolManager.SelectAsync(code.Channel, toolNumber, Tools.ToolChangeParameters.All, code, cancellationToken);
            if (!selected.Succeeded())
            {
                return selected;
            }
        }
        else
        {
            bool isCurrent;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                isCurrent = toolManager.Current?.Number == toolNumber;
            }
            if (isCurrent)
            {
                // The tool is selected but its heaters may have been switched off, e.g. by a heater fault that was cleared
                await toolManager.ActivateAsync(toolNumber, cancellationToken);
            }
            else
            {
                await toolManager.StandbyAsync(toolNumber, cancellationToken);
            }
        }

        if (wait)
        {
            List<int> heaters;
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                heaters = [.. toolManager.Current?.Heaters ?? []];
            }

            // Another channel may move while the heaters come up
            code.ReleaseMovementLock();
            if (!await heatManager.WaitForTemperaturesAsync(heaters, waitWhenCooling, HeatManager.TemperatureCloseEnough,
                                                             IsFileChannel(code), cancellationToken))
            {
                throw new System.OperationCanceledException();
            }
        }
        return new Message();
    }

    /// <summary>
    /// M190 / M191: set the active temperature of a bed or chamber slot's heaters, switch them on and wait for them
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="chamber">True for M191, false for M190</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's GCodes2.cpp case 190/191. A slot with no heaters, or a code with neither S nor R, does nothing.
    /// Every heater of the slot is set before any is waited for, and the replies of all are reported together
    /// (rrf-differences.md)
    /// </remarks>
    private async ValueTask<Message> HandleBedOrChamberWaitAsync(Commands.Code code, bool chamber, CancellationToken cancellationToken)
    {
        int slot = BedOrChamberSlot(code, chamber);
        int[] heaters;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            heaters = heatManager.BedOrChamberHeaters(chamber, slot);
        }
        if (heaters.Length == 0 || !TryGetTargetTemperature(code, out float temperature, out bool waitWhenCooling))
        {
            return new Message();
        }

        List<Message> replies = [];
        foreach (int heaterNumber in heaters)
        {
            Message set = await heatManager.SetActiveOrStandbyTemperatureAsync(heaterNumber, temperature, true, cancellationToken);
            if (!set.Succeeded())
            {
                return set;
            }
            replies.Add(await heatManager.SetActiveOrStandbyAsync(heaterNumber, true, cancellationToken));
        }

        // Another channel may move while the heaters come up
        code.ReleaseMovementLock();
        if (!await heatManager.WaitForTemperaturesAsync(heaters, waitWhenCooling, HeatManager.TemperatureCloseEnough,
                                                         IsFileChannel(code), cancellationToken))
        {
            throw new System.OperationCanceledException();
        }
        return replies.ToMessage();
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
    /// M108: end every M109, M116, M190 and M191 wait in progress on any channel, each of which completes successfully
    /// </summary>
    /// <remarks>RepRapFirmware's GCodes2.cpp case 108. Nothing waiting is not an error</remarks>
    private ValueTask<Message> HandleCancelHeatingWaitAsync()
    {
        heatManager.EndTemperatureWaits();
        return ValueTask.FromResult(new Message());
    }

    /// <summary>
    /// M116: wait for the heaters of the tools P, the heaters H and the chamber slots C, or without any of them for every
    /// tool, bed and chamber heater, to reach their temperatures
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's GCodes2.cpp case 116. A bare P means every tool and a bare C every chamber slot, while a bare H
    /// means no heater. A tool, heater or slot that does not exist is skipped. Every wait includes cooling down
    /// </remarks>
    private async ValueTask<Message> HandleWaitForTemperaturesAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        float tolerance = code.GetPositiveFloat('S', defaultValue: HeatManager.TemperatureCloseEnough);
        HashSet<int> heaters = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            bool seen = false;
            if (code.TryGetUIntArray('P', Tools.ToolManager.MaxTools, out uint[]? toolNumbers, allowZeroLength: true))
            {
                seen = true;
                if (toolNumbers.Length == 0)
                {
                    AddToolHeaters(heaters, model.Tools);
                }
                else
                {
                    AddToolHeaters(heaters, toolNumbers.Select(toolNumber => toolManager.Find((int)toolNumber)));
                }
            }

            if (code.TryGetUIntArray('H', HeatManager.MaxHeaters, out uint[]? heaterNumbers, allowZeroLength: true))
            {
                seen = true;
                heaters.UnionWith(heaterNumbers.Select(heaterNumber => (int)heaterNumber));
            }

            if (code.TryGetUIntArray('C', HeatManager.MaxChamberHeaters, out uint[]? chamberSlots, allowZeroLength: true))
            {
                seen = true;
                IEnumerable<int> slots = (chamberSlots.Length == 0)
                    ? Enumerable.Range(0, HeatManager.MaxChamberHeaters)
                    : chamberSlots.Where(slot => slot < HeatManager.MaxChamberHeaters).Select(slot => (int)slot);
                foreach (int slot in slots)
                {
                    heaters.UnionWith(heatManager.BedOrChamberHeaters(chamber: true, slot));
                }
            }

            if (!seen)
            {
                // TODO RRF skips the tools selected in another motion system once there is more than one
                AddToolHeaters(heaters, model.Tools);
                for (int slot = 0; slot < HeatManager.MaxBedHeaters; slot++)
                {
                    heaters.UnionWith(heatManager.BedOrChamberHeaters(chamber: false, slot));
                }
                for (int slot = 0; slot < HeatManager.MaxChamberHeaters; slot++)
                {
                    heaters.UnionWith(heatManager.BedOrChamberHeaters(chamber: true, slot));
                }
            }
        }

        // Another channel may move while the heaters come up
        code.ReleaseMovementLock();
        if (!await heatManager.WaitForTemperaturesAsync([.. heaters], true, tolerance, IsFileChannel(code), cancellationToken))
        {
            throw new System.OperationCanceledException();
        }
        return new Message();

        // Add the heaters of tools, skipping a tool that does not exist
        static void AddToolHeaters(HashSet<int> heaters, IEnumerable<Tool?> tools)
        {
            foreach (Tool? tool in tools)
            {
                if (tool is not null)
                {
                    heaters.UnionWith(tool.Heaters);
                }
            }
        }
    }

    /// <summary>
    /// M302: allow or forbid cold extrusion, set the minimum temperatures it is judged against, or report them
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's GCodes2.cpp case 302, applying P, S and R in that order. Any of them waits for standstill first
    /// (see the code table), so the change applies from the next move on
    /// </remarks>
    private async ValueTask<Message> HandleColdExtrusionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            bool seen = false;
            if (code.TryGetInt('P', out int allow))
            {
                coldExtrusion.SetAllowed(allow > 0);
                seen = true;
            }
            if (code.TryGetFloat('S', out float extrude))
            {
                coldExtrusion.SetMinExtrusionTemperature(extrude);
                seen = true;
            }
            if (code.TryGetFloat('R', out float retract))
            {
                coldExtrusion.SetMinRetractionTemperature(retract);
                seen = true;
            }

            if (!seen)
            {
                return new Message(MessageType.Success, model.Heat.ColdExtrusionAllowed
                    ? "Cold extrusion is allowed (use M302 P0 to forbid it)"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"Cold extrusion is forbidden (use M302 P1 to allow it), min. extrusion temperature {model.Heat.MinExtrusionTemperature:F1}C, min. retraction temperature {model.Heat.MinRetractionTemperature:F1}C"));
            }
        }
        return new Message();
    }

    /// <summary>
    /// The bed or chamber slot a code addresses: P, or slot 0 without it
    /// </summary>
    private static int BedOrChamberSlot(Commands.Code code, bool chamber)
        => code.GetInt('P', defaultValue: 0, min: 0,
                       max: (chamber ? HeatManager.MaxChamberHeaters : HeatManager.MaxBedHeaters) - 1);

    /// <summary>
    /// M140 / M141: assign the heaters of a bed or chamber slot, set their temperatures, or report them
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="chamber">True for M141, false for M140</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's GCodes2.cpp case 140/141. A refused H changes nothing and ends the code, and the replies of every
    /// heater switched on are reported together (rrf-differences.md)
    /// </remarks>
    private async ValueTask<Message> HandleBedOrChamberHeatersAsync(Commands.Code code, bool chamber,
                                                                    CancellationToken cancellationToken)
    {
        string kind = chamber ? "chamber" : "bed";
        int slot = BedOrChamberSlot(code, chamber);
        bool seen = false;

        if (code.TryGetIntArray('H', chamber ? HeatManager.MaxHeatersPerChamber : HeatManager.MaxHeatersPerBed,
                                out int[]? heaterNumbers))
        {
            seen = true;
            if (heaterNumbers.Length == 1 && heaterNumbers[0] < 0)
            {
                await heatManager.ClearBedOrChamberHeatersAsync(chamber, slot, cancellationToken);
            }
            else
            {
                using (await model.AccessReadOnlyAsync(cancellationToken))
                {
                    foreach (int heaterNumber in heaterNumbers)
                    {
                        if (heaterNumber < 0 || heaterNumber >= HeatManager.MaxHeaters)
                        {
                            return new Message(MessageType.Error, $"Invalid heater number '{heaterNumber}'");
                        }
                        if (toolManager.IsHeaterAssignedToTool(heaterNumber))
                        {
                            return new Message(MessageType.Error, $"Heater {heaterNumber} is already assigned to a tool");
                        }
                        HeaterFunction function = heatManager.GetHeaterFunction(heaterNumber);
                        if ((!chamber && function == HeaterFunction.Chamber) || (chamber && function == HeaterFunction.Bed))
                        {
                            return new Message(MessageType.Error,
                                $"Heater {heaterNumber} is already assigned as a {(function == HeaterFunction.Bed ? "bed" : "chamber")} heater");
                        }
                    }
                }
                await heatManager.SetBedOrChamberHeatersAsync(chamber, slot, heaterNumbers, cancellationToken);
            }
        }

        int[] heaters;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            heaters = heatManager.BedOrChamberHeaters(chamber, slot);
        }

        bool hasActive = code.TryGetFloat('S', out float active);
        bool hasStandby = code.TryGetFloat('R', out float standby);
        if (heaters.Length == 0 && ((hasActive && active > 0.0f) || hasStandby))
        {
            // Turning off a bed or chamber heater that does not exist is not an error
            return new Message(MessageType.Error, $"No {kind} heater has been configured for slot {slot}");
        }

        List<Message> replies = [];
        if (hasActive)
        {
            seen = true;
            foreach (int heaterNumber in heaters)
            {
                if (active <= HeatManager.NearlyAbsoluteZero)
                {
                    await heatManager.SwitchOffAsync(heaterNumber, cancellationToken);
                    continue;
                }

                Message set = await heatManager.SetActiveOrStandbyTemperatureAsync(heaterNumber, active, true, cancellationToken);
                if (!set.Succeeded())
                {
                    return set;
                }
                replies.Add(await heatManager.SetActiveOrStandbyAsync(heaterNumber, true, cancellationToken));
            }
        }

        if (hasStandby)
        {
            seen = true;
            foreach (int heaterNumber in heaters)
            {
                Message set = await heatManager.SetActiveOrStandbyTemperatureAsync(heaterNumber, standby, false, cancellationToken);
                if (!set.Succeeded())
                {
                    return set;
                }
            }
        }

        if (!seen)
        {
            if (heaters.Length == 0)
            {
                return new Message(MessageType.Success, $"No {kind} heater has been configured for slot {slot}");
            }

            StringBuilder report = new();
            report.Append(CultureInfo.InvariantCulture, $"{(chamber ? "Chamber" : "Bed")} heaters for slot {slot}:");
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                foreach (int heaterNumber in heaters)
                {
                    float temperature = heatManager.Find(heaterNumber)?.Current ?? HeatManager.AbsoluteZero;
                    report.Append(CultureInfo.InvariantCulture, $" {heaterNumber} ({temperature:F1}°C)");
                }
            }
            return new Message(MessageType.Success, report.ToString());
        }
        return replies.ToMessage();
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
                monitor.Condition = code.GetEnum<HeaterMonitorCondition>('C', HeaterMonitorCondition.TooHigh);

                HeatManager.UpdateTemperatureLimits(heater);
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
                    Trigger = monitor.Condition switch
                    {
                        HeaterMonitorCondition.TooHigh => 0,
                        HeaterMonitorCondition.TooLow => 1,
                        _ => -1,
                    }
                };
            }
        }

        Message reply = await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
        if (reply.Succeeded())
        {
            heatManager.SetMonitorsSetByUser(heaterNumber);
        }
        return reply;
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

            FopDt.Apply(message, heater.Model, FopDt.SetModelPidTemperature);
            highestTemperatureLimit = HeatManager.GetHighestTemperatureLimit(heater);
        }

        Message reply = await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
        if (reply.Succeeded())
        {
            heatManager.SetModelSetByUser(heaterNumber);
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
