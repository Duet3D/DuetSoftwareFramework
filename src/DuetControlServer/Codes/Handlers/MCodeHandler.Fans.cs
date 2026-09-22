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
    /// Temperatures a thermostatic fan is described by: the one it comes on at and the one it is
    /// full on at
    /// </summary>
    /// <remarks>RepRapFirmware reads M106 T into two values and lets one stand for both (Fan.cpp
    /// <c>Fan::Configure</c>, <c>numTemps = 2</c> with padding)</remarks>
    private const int ThermostaticTemperatures = 2;

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
        const string chars = "DEFHJPSR";
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
    /// M950 F: create, reassign, delete or report a fan
    /// </summary>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp). A C parameter always destroys the fan that
    /// held that number first, whether the code goes on to create another or names
    /// <see cref="IoPorts.NoPortName"/> to delete it, so the two paths differ only in what follows
    /// the release. Without a C it is a change to the parameters of a fan that exists already, or a
    /// request to report it
    /// </remarks>
    private async ValueTask<Message> HandleCreateFanAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int fanNumber = code.GetInt('F', min: 0, max: FanManager.MaxFans - 1);

        // Q and K are read before anything is touched, so a K outside the range a tacho can be read
        // over refuses the code while the fan that holds the number is still whole and still on its
        // pins (RRF FansManager::ConfigureFanPort, which reads both ahead of the C it may delete on)
        bool seenFrequency = code.TryGetFloat('Q', out float frequency);
        bool seenPulsesPerRev = code.TryGetFloat('K', out float pulsesPerRev, min: Fan.MinTachoPpr, max: Fan.MaxTachoPpr);

        if (!code.TryGetString('C', out string? port))
        {
            return (seenFrequency || seenPulsesPerRev)
                   ? await SetFanParametersAsync(fanNumber, seenFrequency, frequency, seenPulsesPerRev, pulsesPerRev,
                                                 cancellationToken)
                   : await ReportFanAsync(fanNumber, cancellationToken);
        }

        // A port cannot belong to two fans, so whatever held this number lets go of its pins before
        // anything else claims them
        await DeleteFanAsync(fanNumber, cancellationToken);
        if (IoPorts.IsNoPort(port))
        {
            return new Message();
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
            if (seenFrequency)
            {
                fan.Frequency = frequency;
            }
            if (seenPulsesPerRev)
            {
                fan.TachoPpr = pulsesPerRev;
            }
            createFrequency = fan.Frequency;
            createPulsesPerRev = fan.TachoPpr;
        }

        return await SendM950FanAsync(fanNumber, port, createFrequency, createPulsesPerRev, board, cancellationToken);
    }

    /// <summary>
    /// Take a fan out of the object model and tell its board to let go of the pins
    /// </summary>
    /// <param name="fanNumber">The fan, which may not exist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>~RemoteFan</c> (RemoteFan.cpp), which sends the board an M950 naming the
    /// fan and <see cref="IoPorts.NoPortName"/>. What the board says to that is deliberately not
    /// read: a destructor cannot fail, so a board that is unreachable or has already forgotten the
    /// fan must not be able to keep it alive here. The pin would then be claimed on a board nothing
    /// on this side knows about, which is the state a delete exists to avoid
    /// </remarks>
    private async ValueTask DeleteFanAsync(int fanNumber, CancellationToken cancellationToken)
    {
        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (fanManager.Find(fanNumber) is null)
            {
                return;
            }

            bool onABoard = fanManager.TryGetBoard(fanNumber, out board);
            fanManager.Delete(fanNumber);
            if (!onABoard)
            {
                return;
            }
        }

        await SendM950FanAsync(fanNumber, IoPorts.NoPortName, frequency: null, pulsesPerRev: null, board,
                               cancellationToken);
    }

    /// <summary>
    /// M950 F with Q or K and no C: change the parameters of a fan that exists already
    /// </summary>
    /// <param name="fanNumber">The fan the code named</param>
    /// <param name="seenFrequency">Whether the code carried a Q</param>
    /// <param name="frequency">The frequency Q asked for, if it did</param>
    /// <param name="seenPulsesPerRev">Whether the code carried a K</param>
    /// <param name="pulsesPerRev">The tacho pulses per revolution K asked for, if it did</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// FansManager::ConfigureFanPort (FansManager.cpp) falls through to RemoteFan::SetFanParameters
    /// when no pin name is given, which sends the board the same M950 carrying only what changed.
    /// The values arrive already read, as they do there, so both paths read the code once
    /// </remarks>
    private async ValueTask<Message> SetFanParametersAsync(int fanNumber, bool seenFrequency, float frequency,
                                                           bool seenPulsesPerRev, float pulsesPerRev,
                                                           CancellationToken cancellationToken)
    {
        byte board;
        float? sentFrequency = null, sentPulsesPerRev = null;
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

            if (seenFrequency)
            {
                sentFrequency = frequency;
                fan.Frequency = frequency;
            }
            if (seenPulsesPerRev)
            {
                sentPulsesPerRev = pulsesPerRev;
                fan.TachoPpr = pulsesPerRev;
            }
        }

        // TODO RRF only updates OM if CAN message is successful
        return await SendM950FanAsync(fanNumber, port: null, sentFrequency, sentPulsesPerRev, board, cancellationToken);
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

        return await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
    }

    /// <summary>
    /// Report the pins one fan is driven through, as M950 F with no C does
    /// </summary>
    /// <remarks>
    /// RemoteFan::ReportPortDetails (RemoteFan.cpp) sends the board a bare M950 naming the fan and
    /// answers with what comes back, and this does the same. The board is asked rather than
    /// <c>fans[].port</c> read because the board holds the pin table: a pin usually has more than one
    /// name, and only the board that drives it knows them. <c>1.io1.in</c> is reported as
    /// <c>1.(io1.in,serial1.rx)</c>, which nothing on this side could reconstruct.
    ///
    /// A fan that does not exist is an error rather than a description of nothing, because a client
    /// that branches on severity has no other way to tell a fan it can drive from one it cannot
    /// (FansManager.cpp ConfigureFanPort)
    /// </remarks>
    private async ValueTask<Message> ReportFanAsync(int fanNumber, CancellationToken cancellationToken)
    {
        byte board;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (fanManager.Find(fanNumber) is null)
            {
                return new Message(MessageType.Error, $"Fan {fanNumber} not found");
            }
            if (!fanManager.TryGetBoard(fanNumber, out board))
            {
                return new Message(MessageType.Error, $"Fan {fanNumber} is not on an expansion board");
            }
        }

        // No C, Q or K, which is what makes this a query rather than a change: RemoteFan sends the
        // fan number alone and the board fills in the reply
        return await SendM950FanAsync(fanNumber, port: null, frequency: null, pulsesPerRev: null, board,
                                      cancellationToken);
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
        Message reply = new();
        if (seenFanNumber)
        {
            (configured, reply) = await ConfigureFanAsync(code, fanNumber, cancellationToken);
            if (!reply.Succeeded())
            {
                return reply;
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
                reply = await fanManager.SetSpeedAsync(fanNumber, pwm, cancellationToken);
            }
            else
            {
                reply = await SetMappedFanSpeedAsync(pwm, cancellationToken);
            }

            if (!reply.Succeeded())
            {
                return reply;
            }
        }

        // R puts back the speed a restore point saved, and only for the mapped fans: Fan::Configure
        // does not read R either, so an R alongside a fan number is carried by a code that nothing
        // acts on
        if (!seenFanNumber && code.TryGetInt('R', out int restorePointNumber, min: 0, max: Motion.RestorePoint.NumVisible - 1))
        {
            float saved;
            using (planner.Lock())
            {
                saved = planner.State.RestorePoints[restorePointNumber].FanSpeed;
            }
            return reply.CombinedWith(await SetMappedFanSpeedAsync(saved, cancellationToken));
        }

        return reply;
    }

    /// <summary>
    /// Apply the parameters an M106 carries to one fan
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="fanNumber">The fan it named</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether any parameter was seen, and what to reply: an error, the report, or nothing</returns>
    /// <remarks>
    /// Fan::Configure (Fan.cpp). S is acted on here rather than as a speed of its own, but only
    /// alongside another parameter and after H, because H on its own defaults the fan to full speed
    /// and an S given with it has to win. L is clamped to the maximum and X to the minimum, and both
    /// read as a fan speed does. What the fan ends up at is then sent to the board in one message,
    /// as RepRapFirmware's UpdateFanConfiguration does.
    ///
    /// A code that changed nothing reports the fan instead, and only when there is no R and no S for
    /// the caller to act on - so an M106 that carries only a parameter this firmware has no use for
    /// reads as a query, and one that is about to set a speed says nothing
    /// </remarks>
    private async ValueTask<(bool Seen, Message Reply)> ConfigureFanAsync(Commands.Code code, int fanNumber,
                                                                         CancellationToken cancellationToken)
    {
        bool seen = false;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (fanManager.Find(fanNumber) is not Fan fan)
            {
                return (false, new Message(MessageType.Error, $"Fan number {fanNumber} not found"));
            }

            // Two temperatures: the fan is off below the first and full on above the second, which is
            // what makes it ramp rather than chatter around one threshold. A single value is both
            if (code.TryGetFloatArray('T', ThermostaticTemperatures, out float[]? temperatures, pad: true) && temperatures.Length > 0)
            {
                fan.Thermostatic.LowTemperature = temperatures[0];
                fan.Thermostatic.HighTemperature = temperatures[1];
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

            // Signed, because M106 H-1 is how thermostatic mode is turned off (RRF Fan.cpp
            // Fan::Configure, GetIntArray over an array of MaxSensors)
            if (code.TryGetIntArray('H', CanLimits.MaxSensors, out int[]? sensors))
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

            if (!seen)
            {
                return (false, code.HasParameter('R') || code.HasParameter('S')
                               ? new Message()
                               : new Message(MessageType.Success, FanConfigurationReport(fanNumber, fan)));
            }
        }

        return (true, await SendFanParametersAsync(fanNumber, cancellationToken));
    }

    /// <summary>
    /// What one fan is configured to do, in the wording M106 reports it with
    /// </summary>
    /// <param name="fanNumber">The fan</param>
    /// <param name="fan">Its object model entry</param>
    /// <returns>The report</returns>
    /// <remarks>
    /// Fan::Configure's report branch (Fan.cpp). A thermostatic fan reports the sensors it watches,
    /// the temperatures it switches between and the PWM the board has it at, in place of the
    /// requested speed a manual fan reports: a requested speed says nothing about a fan the board is
    /// driving from a temperature. That running PWM is the board's to report, so it reads "unknown"
    /// until one has arrived (RemoteFan.cpp lastPwm). The caller must hold the object model lock
    /// </remarks>
    private static string FanConfigurationReport(int fanNumber, Fan fan)
    {
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"Fan {fanNumber}");
        if (!string.IsNullOrEmpty(fan.Name))
        {
            builder.Append(CultureInfo.InvariantCulture, $" ({fan.Name})");
        }

        bool thermostatic = fan.Thermostatic.Sensors.Count > 0;
        if (!thermostatic)
        {
            builder.Append(CultureInfo.InvariantCulture, $", speed {AsPercent(fan.RequestedValue)}%");
        }
        builder.Append(CultureInfo.InvariantCulture,
                       $", min: {AsPercent(fan.Min)}%, max: {AsPercent(fan.Max)}%, blip: {fan.Blip:F2}");

        if (thermostatic)
        {
            builder.Append(CultureInfo.InvariantCulture,
                           $", temperature: {fan.Thermostatic.LowTemperature ?? Fan.DefaultTriggerTemperature:F1}"
                           + $":{fan.Thermostatic.HighTemperature ?? Fan.DefaultTriggerTemperature:F1}C, sensors:");
            foreach (int sensor in fan.Thermostatic.Sensors)
            {
                builder.Append(CultureInfo.InvariantCulture, $" {sensor}");
            }
            builder.Append(", current speed: ");
            builder.Append(fan.ActualValue >= 0.0f
                           ? string.Create(CultureInfo.InvariantCulture, $"{AsPercent(fan.ActualValue)}%:")
                           : "unknown");
        }
        return builder.ToString();
    }

    /// <summary>
    /// A 0 to 1 fan value as the whole percent a report prints
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The percentage, truncated</returns>
    /// <remarks>
    /// Truncated rather than rounded, because RepRapFirmware's reports cast the percentage to an
    /// integer (Fan.cpp) and a fan reported at 80% by one and 79% by the other is a difference an
    /// operator has no way to explain.
    ///
    /// The multiplication is deliberately single precision. Read literally, Fan.cpp's
    /// <c>(int)(maxVal * 100.0)</c> widens the float to a double, and X0.7 would then truncate to
    /// 69% because 0.7f is 0.69999998808. The boards report 70%, because they are built with
    /// single-precision constants and the whole expression stays in float, where the same product
    /// rounds to exactly 70. Widening here would put this side one percent below the reference on
    /// the values that land just under a float boundary: 0.7, 0.9 and 0.45 among them
    /// </remarks>
    private static int AsPercent(float value) => (int)(value * 100.0f);

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
        return await SetMappedFanSpeedAsync(0.0f, cancellationToken);
    }

    /// <summary>
    /// Drive the fans the current tool maps, or fan 0 when no tool is selected
    /// </summary>
    /// <param name="pwm">Speed to set, 0..1</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the fans said, or the refusal of the first that would not take the speed</returns>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::SetMappedFanSpeed</c>. The speed is recorded as the one the
    /// operator asked for whether or not a fan took it, because that is what a restore point saves.
    /// A fan that refuses stops the rest, as RepRapFirmware's loop does
    /// </remarks>
    private async ValueTask<Message> SetMappedFanSpeedAsync(float pwm, CancellationToken cancellationToken)
    {
        List<int> fans = await MappedFansAsync(cancellationToken);

        using (planner.Lock())
        {
            planner.State.VirtualFanSpeed = pwm;
        }

        List<Message> replies = [];
        foreach (int fanNumber in fans)
        {
            Message reply = await fanManager.SetSpeedAsync(fanNumber, pwm, cancellationToken);
            if (!reply.Succeeded())
            {
                return reply;
            }
            replies.Add(reply);
        }
        return replies.ToMessage();
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
    /// <returns>What the board said about them</returns>
    /// <remarks>
    /// RepRapFirmware's <c>RemoteFan::UpdateFanConfiguration</c>, which sends the fan's whole state
    /// rather than what the code changed. Thermostatic control belongs to the board because the board
    /// is what reads the sensors: a rule applied from this side would be applied at the speed of the
    /// CAN bus, and a fan that cools a stepper has to react faster than that
    /// </remarks>
    private async ValueTask<Message> SendFanParametersAsync(int fanNumber, CancellationToken cancellationToken)
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

        return await linkInterface.SendCanRequestAsync(board, in message, cancellationToken);
    }
}
