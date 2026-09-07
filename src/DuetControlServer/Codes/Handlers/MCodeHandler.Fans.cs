using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that create and drive the fans
/// </summary>
/// <remarks>
/// A fan is a PWM output on an expansion board and the board drives it, including the thermostatic
/// rule, which the board applies to sensors it already reads. So these codes configure and request;
/// the actual PWM and the tacho reading come back the other way into <c>fans[]</c>
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// The fan a code with no fan number addresses while no tool is selected
    /// </summary>
    /// <remarks>RepRapFirmware's <c>GCodes::SetMappedFanSpeed</c> with no current tool</remarks>
    private const int MappedFanWithoutTool = 0;

    /// <summary>
    /// M950: create a heater, fan or other I/O device
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// Which device it creates is decided by which letter the code carries, so the letter is the
    /// dispatch. RepRapFirmware does the same, and refuses a code that names more than one
    /// </remarks>
    private async ValueTask<Message> HandleCreateDeviceAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        char[] chars = {'D', 'E', 'R', 'J', 'F', 'H', 'P', 'S'};
        uint seen = 0;
        foreach (char c in chars)
        {
            if (code.HasParameter(c))
            {
                seen++;
            }
        }

        if (seen != 1)
        {
            return new Message(MessageType.Error, $"exactly one of {chars} must be given");
        }

        if (code.HasParameter('S'))
        {
            return await HandleCreateOutputAsync(code, isServo: true, cancellationToken);
        }
        if (code.HasParameter('P'))
        {
            return await HandleCreateOutputAsync(code, isServo: false, cancellationToken);
        }
        if (code.HasParameter('H'))
        {
            return await HandleCreateHeaterAsync(code, cancellationToken);
        }
        if (code.HasParameter('F'))
        {
            return await HandleCreateFanAsync(code, cancellationToken);
        }
        if (code.HasParameter('R'))
        {
            return await HandleCreateSpindleAsync(code, cancellationToken);
        }

        // TODO J creates a general-purpose input and D a LED strip. sensors.gpIn[] is the home for
        // the first and CanMessageCreateInputMonitorV1 the message; M950LedParams is the second
        return new Message(MessageType.Warning, "M950 J and D are not ported yet");
    }

    /// <summary>
    /// M950 F: create a fan
    /// </summary>
    private async ValueTask<Message> HandleCreateFanAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('F', out int fanNumber) || fanNumber < 0 || fanNumber >= FanManager.MaxFans)
        {
            return new Message(MessageType.Error, $"Fan number must be between 0 and {FanManager.MaxFans - 1}");
        }

        if (!code.TryGetString('C', out string? port))
        {
            // Without a port this is either a change to the parameters of a fan that exists already
            // or a request to report it
            return code.HasParameter('Q') || code.HasParameter('K')
                   ? await SetFanParametersAsync(code, fanNumber, cancellationToken)
                   : await ReportFanAsync(fanNumber, cancellationToken);
        }

        byte board;
        float createFrequency, createPulsesPerRev;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!IoPorts.TrySplitPort(port, "Fan port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            // A new fan starts at the defaults, so Q and K only have to overwrite what they were given
            Fan fan = fanManager.Create(fanNumber);
            fan.Port = port;
            if (code.TryGetFloat('Q', out float frequency))
            {
                fan.Frequency = frequency;
            }
            if (code.TryGetFloatLimited('K', Fan.MinTachoPpr, Fan.MaxTachoPpr, out float pulsesPerRev))
            {
                fan.TachoPpr = pulsesPerRev;
            }
            createFrequency = fan.Frequency;
            createPulsesPerRev = fan.TachoPpr;
        }

        return await SendM950FanAsync(fanNumber, port, createFrequency, createPulsesPerRev, board, cancellationToken);
    }

    /// <summary>
    /// M950 F with Q or K and no C: change the parameters of a fan that exists already
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) falls through to RemoteFan::SetFanParameters
    /// when no pin name is given, which sends the board the same M950 carrying only what changed
    /// </remarks>
    private async ValueTask<Message> SetFanParametersAsync(Commands.Code code, int fanNumber,
                                                           CancellationToken cancellationToken)
    {
        byte board;
        float? frequency = null, pulsesPerRev = null;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (fanManager.Find(fanNumber) is not Fan fan)
            {
                return new Message(MessageType.Error, $"Fan {fanNumber} not found");
            }
            if (!fanManager.TryGetBoard(fanNumber, out board))
            {
                return new Message(MessageType.Error, $"Fan {fanNumber} is not on an expansion board");
            }

            if (code.TryGetFloat('Q', out float seenFrequency))
            {
                frequency = seenFrequency;
                fan.Frequency = seenFrequency;
            }
            if (code.TryGetFloatLimited('K', Fan.MinTachoPpr, Fan.MaxTachoPpr, out float seenPulsesPerRev))
            {
                pulsesPerRev = seenPulsesPerRev;
                fan.TachoPpr = seenPulsesPerRev;
            }
        }

        // TODO RRF only updates OM if CAN message is successful
        return await SendM950FanAsync(fanNumber, port: null, frequency, pulsesPerRev, board, cancellationToken);
    }

    /// <summary>
    /// Send a board the M950 that creates or reconfigures one of its fans
    /// </summary>
    /// <param name="fanNumber">The fan, which is the number the board will know it by</param>
    /// <param name="port">Port to assign, or null to leave the board's assignment alone</param>
    /// <param name="frequency">PWM frequency in Hz, or null not to change it</param>
    /// <param name="pulsesPerRev">Tacho pulses per revolution, or null not to change it</param>
    /// <param name="board">CAN address of the board carrying the fan</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board replied</returns>
    /// <remarks>
    /// <para>
    /// RemoteFan::ConfigurePort and RemoteFan::SetFanParameters (RemoteFan.cpp), which differ in
    /// exactly this: creating a port states the whole configuration, defaults included, while a
    /// change to an existing fan carries only what changed and no port at all.
    /// </para>
    /// <para>
    /// The message is built from what this side resolved rather than passed through from the code.
    /// A board told only what the operator typed would fill in its own defaults, and those are the
    /// board's rather than the ones <c>fans[]</c> now reports - so the fan would run at a frequency
    /// the object model does not know about. It also keeps K clamped and, for a servo or an output,
    /// stops the number in the code being read as something else entirely
    /// </para>
    /// </remarks>
    private async ValueTask<Message> SendM950FanAsync(int fanNumber, string? port, float? frequency,
                                                      float? pulsesPerRev, byte board,
                                                      CancellationToken cancellationToken)
    {
        CanMessageM950Fan message = default;
        message.F = (ushort)fanNumber;
        message.Q = frequency is float hz ? (ushort)MathF.Round(hz) : null;
        message.C = port;
        message.K = pulsesPerRev;

        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        return response.ToMessage();
    }

    /// <summary>
    /// Report one fan, as M950 F with no C does
    /// </summary>
    private async ValueTask<Message> ReportFanAsync(int fanNumber, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return fanManager.Find(fanNumber) is not Fan fan
                ? new Message(MessageType.Success, $"Fan {fanNumber} is not configured")
                : new Message(MessageType.Success, string.Create(CultureInfo.InvariantCulture,
                    $"Fan {fanNumber} frequency {fan.Frequency:F0}Hz, speed {fan.ActualValue * 100.0f:F0}%"));
        }
    }

    /// <summary>
    /// M106: set a fan speed and its parameters
    /// </summary>
    /// <remarks>
    /// <para>
    /// S is the speed, and RepRapFirmware reads it as a fraction when it is at most 1 and as a
    /// PWM byte otherwise, so that both <c>M106 S0.5</c> and <c>M106 S128</c> mean half. That
    /// ambiguity is in the code the slicers emit, so it has to be kept.
    /// </para>
    /// <para>
    /// With no fan number it addresses the current tool's fans, which is what makes a slicer's bare
    /// <c>M106 S255</c> drive the part-cooling fan of whichever tool is printing
    /// </para>
    /// </remarks>
    private async ValueTask<Message> HandleFanSpeedAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // Only a fan P names is configured: a code without P addresses whatever the current tool maps,
        // which may be several fans or none, so a configuration parameter would have no fan to land on
        bool seenFanNumber = code.TryGetInt('P', out int fanNumber);
        bool configured = false;
        if (seenFanNumber)
        {
            Message? configError;
            (configured, configError) = await ConfigureFanAsync(code, fanNumber, cancellationToken);
            if (configError is not null)
            {
                return configError;
            }
        }

        // The configuration acts on S itself, and only alongside another parameter, so what reaches
        // here is a code whose S is all it carries
        if (!configured && code.TryGetFloat('S', out float speed))
        {
            float pwm = GetPwmValue(speed);
            if (seenFanNumber)
            {
                // TODO handle fan feed forward
                await RecordVirtualFanSpeedAsync(fanNumber, pwm, cancellationToken);
                if (await fanManager.SetSpeedAsync(fanNumber, pwm, cancellationToken) is string error)
                {
                    return new Message(MessageType.Error, error);
                }
            }
            else if (await SetMappedFanSpeedAsync(pwm, cancellationToken) is Message error)
            {
                return error;
            }
            return new Message();
        }

        // R puts back the speed a restore point saved, and only for the mapped fans
        if (!seenFanNumber && code.TryGetInt('R', out int restorePointNumber))
        {
            if (restorePointNumber < 0 || restorePointNumber >= Motion.RestorePoint.NumVisible)
            {
                return new Message(MessageType.Error,
                                   $"Restore point number must be between 0 and {Motion.RestorePoint.NumVisible - 1}");
            }

            float saved;
            using (planner.Lock())
            {
                saved = planner.State.RestorePoints[restorePointNumber].FanSpeed;
            }
            return await SetMappedFanSpeedAsync(saved, cancellationToken) ?? new Message();
        }

        if (configured)
        {
            return new Message();
        }
        return await ReportFanSpeedsAsync(seenFanNumber ? [fanNumber] : await MappedFansAsync(cancellationToken),
                                          cancellationToken);
    }

    /// <summary>
    /// Apply the parameters an M106 carries to one fan
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="fanNumber">The fan it named</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether any parameter was seen, and an error if one was refused</returns>
    /// <remarks>
    /// Fan::Configure (Fan.cpp). S is acted on here rather than as a speed of its own, but only
    /// alongside another parameter and after H, because H on its own defaults the fan to full speed
    /// and an S given with it has to win. L is clamped to the maximum and X to the minimum, and both
    /// read as a fan speed does. What the fan ends up at is then sent to the board in one message,
    /// as RepRapFirmware's UpdateFanConfiguration does
    /// </remarks>
    private async ValueTask<(bool Seen, Message? Error)> ConfigureFanAsync(Commands.Code code, int fanNumber,
                                                                          CancellationToken cancellationToken)
    {
        bool seen = false;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (fanManager.Find(fanNumber) is not Fan fan)
            {
                return (false, new Message(MessageType.Error, $"Fan {fanNumber} not found"));
            }

            // Two temperatures: the fan is off below the first and full on above the second, which is
            // what makes it ramp rather than chatter around one threshold. A single value is both
            if (code.TryGetFloatArray('T', out float[]? temperatures) && temperatures.Length > 0)
            {
                fan.Thermostatic.LowTemperature = temperatures[0];
                fan.Thermostatic.HighTemperature = temperatures.Length > 1 ? temperatures[1] : temperatures[0];
                seen = true;
            }
            if (code.TryGetFloat('B', out float blip))
            {
                fan.Blip = MathF.Max(blip, 0.0f);
                seen = true;
            }

            bool seenMinMax = false;
            if (code.TryGetFloat('L', out float min))
            {
                fan.Min = GetPwmValue(min);
                seen = true;
                seenMinMax = true;
            }
            if (code.TryGetFloat('X', out float max))
            {
                fan.Max = GetPwmValue(max);
                seen = true;
                seenMinMax = true;
            }
            if (seenMinMax)
            {
                // TODO send a warning that the min/max was clamped
                fan.Min = MathF.Min(fan.Min, fan.Max);
                fan.Max = MathF.Max(fan.Min, fan.Max);
            }

            if (code.TryGetIntArray('H', out int[]? sensors))
            {
                // A negative sensor number is how M106 H-1 turns thermostatic mode off, so rebuilding
                // the list from the non-negative ones implements that on its own
                fan.Thermostatic.Sensors.Clear();
                foreach (int sensor in sensors)
                {
                    if (sensor < 0)
                    {
                        continue;
                    }
                    if (sensor >= model.Limits.Sensors)
                    {
                        return (seen, new Message(MessageType.Error, "Sensor number out of range"));
                    }
                    fan.Thermostatic.Sensors.Add(sensor);
                }

                if (fan.Thermostatic.Sensors.Count > 0)
                {
                    // Default the fan to full speed for safety: a hot end fan that is asked to watch a
                    // sensor and left at zero would leave the hot end uncooled until something set it
                    fan.RequestedValue = 1.0f;
                }
                else
                {
                    // With nothing monitored the trigger temperatures say nothing about the machine
                    fan.Thermostatic.LowTemperature = fan.Thermostatic.HighTemperature = null;
                }
                seen = true;
            }
            if (code.TryGetString('C', out string? name))
            {
                fan.Name = name;
                seen = true;
            }

            if (seen && code.TryGetFloat('S', out float speed))
            {
                fan.RequestedValue = GetPwmValue(speed);
            }
        }

        return seen ? (true, await SendFanParametersAsync(fanNumber, cancellationToken)) : (false, null);
    }

    /// <summary>
    /// M107: switch a fan off
    /// </summary>
    /// <remarks>
    /// Deprecated in favour of <c>M106 S0</c>, and identical to it. RepRapFirmware reads no parameter
    /// at all here - case 107 is one call to SetMappedFanSpeed(0) - so even a P is ignored and the
    /// fans the current tool maps are what stops
    /// </remarks>
    private async ValueTask<Message> HandleFanOffAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        _ = code;
        return await SetMappedFanSpeedAsync(0.0f, cancellationToken) ?? new Message();
    }

    /// <summary>
    /// Drive the fans the current tool maps, or fan 0 when no tool is selected
    /// </summary>
    /// <param name="pwm">Speed to set, 0..1</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if a fan refused the speed, else null</returns>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::SetMappedFanSpeed</c>. The speed is recorded as the one the
    /// operator asked for whether or not a fan took it, because that is what a restore point saves
    /// </remarks>
    private async ValueTask<Message?> SetMappedFanSpeedAsync(float pwm, CancellationToken cancellationToken)
    {
        List<int> fans = await MappedFansAsync(cancellationToken);

        using (planner.Lock())
        {
            planner.State.VirtualFanSpeed = pwm;
        }

        foreach (int fanNumber in fans)
        {
            if (await fanManager.SetSpeedAsync(fanNumber, pwm, cancellationToken) is string error)
            {
                return new Message(MessageType.Error, error);
            }
        }
        return null;
    }

    /// <summary>
    /// Remember the speed as the one the operator asked the current tool for
    /// </summary>
    /// <param name="fanNumber">Fan the code named</param>
    /// <param name="pwm">Speed that was set, 0..1</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>ms.virtualFanSpeed</c> for the case where a fan was named: only a fan the
    /// current tool maps counts, because it is the tool's speed that a restore point saves. A tool may
    /// map several fans and what has to be put back is the one speed that was asked for rather than
    /// any one fan's
    /// </remarks>
    private async ValueTask RecordVirtualFanSpeedAsync(int fanNumber, float pwm, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (toolManager.Current is not Tool tool || !tool.Fans.Contains(fanNumber))
            {
                return;
            }
        }

        using (planner.Lock())
        {
            planner.State.VirtualFanSpeed = pwm;
        }
    }

    /// <summary>
    /// The fans a code with no fan number addresses
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The current tool's fans, or fan 0 when no tool is selected</returns>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::SetMappedFanSpeed</c> drives whatever the mapping names and says
    /// nothing when one of those fans does not exist, so an unconfigured fan is left out here rather
    /// than turned into an error
    /// </remarks>
    private async ValueTask<List<int>> MappedFansAsync(CancellationToken cancellationToken)
    {
        List<int> fans = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            IEnumerable<int> mapped = toolManager.Current is Tool tool ? tool.Fans : [MappedFanWithoutTool];
            fans.AddRange(mapped.Where(fanNumber => fanManager.Find(fanNumber) is not null));
        }
        return fans;
    }

    /// <summary>
    /// Report what fans are running at
    /// </summary>
    private async ValueTask<Message> ReportFanSpeedsAsync(IReadOnlyList<int> fans, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (int fanNumber in fans)
            {
                if (fanManager.Find(fanNumber) is Fan fan)
                {
                    builder.Append(CultureInfo.InvariantCulture,
                                   $"Fan {fanNumber} speed {fan.ActualValue * 100.0f:F0}%, requested "
                                   + $"{fan.RequestedValue * 100.0f:F0}%");
                    if (fan.Rpm >= 0)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $", {fan.Rpm} RPM");
                    }
                    builder.AppendLine();
                }
            }
        }
        return builder.Length == 0
            ? new Message(MessageType.Success, "No fans are configured")
            : new Message(MessageType.Success, builder.ToString().TrimEnd());
    }

    /// <summary>
    /// Convert a value from 0.0f-1.0f or 0-255 to 0.0f-1.0f
    /// </summary>
    /// <param name="value">The value as the code gave it</param>
    /// <returns>PWM between 0 and 1</returns>
    /// <remarks>
    /// A value of at most 1 is a fraction and anything above it is a PWM byte out of 255, so
    /// <c>S0.5</c> and <c>S128</c> both mean half. Slicers emit both, so the ambiguity has to be kept
    /// rather than resolved
    /// </remarks>
    private static float GetPwmValue(float value)
    {
        float pwm = value > 1.0f ? value / 255.0f : value;
        return Math.Clamp(pwm, 0.0f, 1.0f);
    }

    /// <summary>
    /// Send a fan the parameters the object model now holds for it, including the sensors it watches
    /// </summary>
    /// <param name="fanNumber">The fan</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the board refused them, else null</returns>
    /// <remarks>
    /// RepRapFirmware's <c>RemoteFan::UpdateFanConfiguration</c>, which sends the fan's whole state
    /// rather than what the code changed. Thermostatic control belongs to the board because the board
    /// is what reads the sensors: a rule applied from this side would be applied at the speed of the
    /// CAN bus, and a fan that cools a stepper has to react faster than that
    /// </remarks>
    private async ValueTask<Message?> SendFanParametersAsync(int fanNumber, CancellationToken cancellationToken)
    {
        byte board;
        CanMessageFanParameters message = new() { FanNumber = (ushort)fanNumber };
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (fanManager.Find(fanNumber) is not Fan fan)
            {
                return new Message(MessageType.Error, $"Fan {fanNumber} not found");
            }
            if (!fanManager.TryGetBoard(fanNumber, out board))
            {
                return new Message(MessageType.Error, $"Fan {fanNumber} is not on an expansion board");
            }

            ulong monitored = 0;
            foreach (int sensor in fan.Thermostatic.Sensors)
            {
                monitored |= 1UL << sensor;
            }
            message.SensorsMonitored = monitored;

            // The board ignores the trigger temperatures while it monitors nothing, and the object
            // model reports none in that state, so what it is given then is the value a thermostatic
            // fan starts at
            message.TriggerTemperatures[0] = fan.Thermostatic.LowTemperature ?? Fan.DefaultTriggerTemperature;
            message.TriggerTemperatures[1] = fan.Thermostatic.HighTemperature ?? Fan.DefaultTriggerTemperature;

            message.Val = fan.RequestedValue;
            message.MinVal = fan.Min;
            message.MaxVal = fan.Max;
            message.BlipTime = (ushort)(fan.Blip * 1000.0f);
        }

        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        return reply.Type == MessageType.Error ? reply : null;
    }
}
