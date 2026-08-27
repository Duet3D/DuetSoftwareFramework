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
        if (code.HasParameter('H'))
        {
            return await HandleCreateHeaterAsync(code, cancellationToken);
        }
        if (code.HasParameter('F'))
        {
            return await HandleCreateFanAsync(code, cancellationToken);
        }

        if (code.HasParameter('P'))
        {
            return await HandleCreateOutputAsync(code, isServo: false, cancellationToken);
        }
        if (code.HasParameter('S'))
        {
            return await HandleCreateOutputAsync(code, isServo: true, cancellationToken);
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
            return await ReportFanAsync(fanNumber, cancellationToken);
        }

        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!RemoteEndstops.TrySplitPort(port, "Fan port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            Fan fan = fanManager.Create(fanNumber);
            fan.Port = port;
            if (code.TryGetFloat('Q', out float frequency))
            {
                fan.Frequency = frequency;
            }
        }

        return (await linkInterface.SendCodeAsync<CanMessageM950Fan>(board, code, cancellationToken: cancellationToken)).ToMessage();
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
        List<int> fans = await FansAddressedAsync(code, cancellationToken);
        if (fans.Count == 0)
        {
            return new Message(MessageType.Warning, "No fan to set");
        }

        bool seen = false;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            foreach (int fanNumber in fans)
            {
                if (fanManager.Find(fanNumber) is not Fan fan)
                {
                    continue;
                }

                if (code.TryGetFloat('B', out float blip))
                {
                    fan.Blip = blip;
                    seen = true;
                }
                if (code.TryGetFloat('L', out float min))
                {
                    fan.Min = NormalisedSpeed(min);
                    seen = true;
                }
                if (code.TryGetFloat('X', out float max))
                {
                    fan.Max = NormalisedSpeed(max);
                    seen = true;
                }
                if (code.TryGetString('A', out string? name))
                {
                    fan.Name = name;
                    seen = true;
                }
            }
        }

        if (code.TryGetFloat('S', out float speed))
        {
            float pwm = NormalisedSpeed(speed);
            foreach (int fanNumber in fans)
            {
                if (await fanManager.SetSpeedAsync(fanNumber, pwm, cancellationToken) is string error)
                {
                    return new Message(MessageType.Error, error);
                }
            }
            await RecordVirtualFanSpeedAsync(code, fans, pwm, cancellationToken);
            seen = true;
        }

        if (code.HasParameter('H') || code.HasParameter('T'))
        {
            foreach (int fanNumber in fans)
            {
                if (await SendFanParametersAsync(code, fanNumber, cancellationToken) is Message error)
                {
                    return error;
                }
            }
            seen = true;
        }

        return seen ? new Message() : await ReportFanSpeedsAsync(fans, cancellationToken);
    }

    /// <summary>
    /// M107: switch a fan off
    /// </summary>
    /// <remarks>Deprecated in favour of <c>M106 S0</c>, and identical to it</remarks>
    private async ValueTask<Message> HandleFanOffAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // TODO RRF only turns off the fans associated with the current tool and ignores the P param
        foreach (int fanNumber in await FansAddressedAsync(code, cancellationToken))
        {
            if (await fanManager.SetSpeedAsync(fanNumber, 0.0f, cancellationToken) is string error)
            {
                return new Message(MessageType.Error, error);
            }
        }
        return new Message();
    }

    /// <summary>
    /// Remember the speed as the one the operator asked the current tool for
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="fans">Fans the code addressed</param>
    /// <param name="pwm">Speed that was set, 0..1</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>ms.virtualFanSpeed</c>, set from both of the places that write it: an
    /// M106 naming a fan the current tool maps, and an M106 with no P at all, which addresses the
    /// tool's fans. It is what a restore point saves, because a tool may map several fans and what
    /// has to be put back is the one speed that was asked for rather than any one fan's
    /// </remarks>
    private async ValueTask RecordVirtualFanSpeedAsync(Commands.Code code, IReadOnlyList<int> fans, float pwm,
                                                       CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (toolManager.Current is not Tool tool)
            {
                return;
            }

            // Without P the fans came from the tool already; with it, only a fan the tool maps counts
            bool addressesTool = !code.HasParameter('P') || fans.Any(tool.Fans.Contains);
            if (addressesTool)
            {
                using (planner.Lock())
                {
                    planner.State.VirtualFanSpeed = pwm;
                }
            }
        }
    }

    /// <summary>
    /// The fans a code addresses: the ones P names, or the current tool's
    /// </summary>
    private async ValueTask<List<int>> FansAddressedAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        List<int> fans = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (code.TryGetInt('P', out int fanNumber))
            {
                fans.Add(fanNumber);
            }
            else if (toolManager.Current is Tool tool)
            {
                fans.AddRange(tool.Fans);
            }
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
    /// Read a fan speed the way RepRapFirmware does
    /// </summary>
    /// <param name="value">The value as the code gave it</param>
    /// <returns>PWM between 0 and 1</returns>
    /// <remarks>
    /// A value of at most 1 is a fraction and anything above it is a PWM byte out of 255, so
    /// <c>S0.5</c> and <c>S128</c> both mean half. Slicers emit both, so the ambiguity has to be kept
    /// rather than resolved
    /// </remarks>
    private static float NormalisedSpeed(float value)
    {
        float pwm = value > 1.0f ? value / 255.0f : value;
        return pwm < 0.0f ? 0.0f : pwm > 1.0f ? 1.0f : pwm;
    }

    /// <summary>
    /// Send a fan's parameters, including the sensors it watches
    /// </summary>
    /// <returns>An error if the board refused them, else null</returns>
    /// <remarks>
    /// Thermostatic control belongs to the board because the board is what reads the sensors: a rule
    /// applied from this side would be applied at the speed of the CAN bus, and a fan that cools a
    /// stepper has to react faster than that. H names the sensors and T the temperatures they trigger
    /// at, which is what the message carries
    /// </remarks>
    private async ValueTask<Message?> SendFanParametersAsync(Commands.Code code, int fanNumber,
                                                             CancellationToken cancellationToken)
    {
        byte board;
        CanMessageFanParameters message = new() { FanNumber = (ushort)fanNumber };
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

            if (code.TryGetIntArray('H', out int[]? sensors))
            {
                ulong monitored = 0;
                foreach (int sensor in sensors)
                {
                    if (sensor >= 0 && sensor < 64)
                    {
                        monitored |= 1UL << sensor;
                    }
                }
                fan.Thermostatic.Sensors.Clear();
                foreach (int sensor in sensors)
                {
                    if (sensor >= 0)
                    {
                        fan.Thermostatic.Sensors.Add(sensor);
                    }
                }
                message.SensorsMonitored = monitored;
            }

            // Two temperatures: the fan is off below the first and full on above the second, which is
            // what makes it ramp rather than chatter around one threshold
            if (code.TryGetFloatArray('T', out float[]? temperatures) && temperatures.Length > 0)
            {
                float low = temperatures[0];
                float high = temperatures.Length > 1 ? temperatures[1] : temperatures[0];
                fan.Thermostatic.LowTemperature = low;
                fan.Thermostatic.HighTemperature = high;
                message.TriggerTemperatures[0] = low;
                message.TriggerTemperatures[1] = high;
            }

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
