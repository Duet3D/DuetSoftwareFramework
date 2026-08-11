using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Fans;
using DuetControlServer.Link.Protocol.CanMessages;
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

        // TODO the remaining M950 devices: P and S create a GPIO output or servo, J a GPIO input,
        // D a LED strip, R a spindle. The generic tables M950GpioParams and M950LedParams exist and
        // CanMessageWriteGpio is what drives one; state.gpOut[] and sensors.gpIn[] are their homes
        return new Message(MessageType.Warning,
                           "M950 supports H and F so far; P, S, J, D and R are not ported yet");
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
            if (code.TryGetFloat('Q', out float frequency))
            {
                fan.Frequency = frequency;
            }
            fanManager.SetBoard(fanNumber, board);
        }

        return await SendGenericAsync<CanMessageM950Fan>(board, code, cancellationToken);
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
            seen = true;
        }

        // TODO thermostatic control (M106 H and T) needs CanMessageFanParameters, which carries the
        // monitored sensors and the trigger temperatures. The message exists; the mapping from H and
        // T to its SensorsMonitored bitmap and TriggerTemperatures pair is not written
        if (code.HasParameter('H') || code.HasParameter('T'))
        {
            return new Message(MessageType.Warning,
                               "Thermostatic fan control is not supported yet; H and T were ignored");
        }

        return seen ? new Message() : await ReportFanSpeedsAsync(fans, cancellationToken);
    }

    /// <summary>
    /// M107: switch a fan off
    /// </summary>
    /// <remarks>Deprecated in favour of <c>M106 S0</c>, and identical to it</remarks>
    private async ValueTask<Message> HandleFanOffAsync(Commands.Code code, CancellationToken cancellationToken)
    {
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
}
