using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using DuetControlServer.Ports;
using DuetControlServer.Spindles;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that create and drive the spindles
/// </summary>
internal partial class MCodeHandler
{
    /// <summary>
    /// M950 R: create a spindle
    /// </summary>
    /// <remarks>
    /// C names up to three ports, separated by <c>+</c>: the PWM output that sets the speed, the
    /// on/off output that starts it, and the direction output that reverses it. That is
    /// RepRapFirmware's own syntax, and the reason a spindle needs no message of its own - it is
    /// three general-purpose outputs driven together
    /// </remarks>
    private async ValueTask<Message> HandleCreateSpindleAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('R', out int spindleNumber)
            || spindleNumber < 0 || spindleNumber >= SpindleManager.MaxSpindles)
        {
            return new Message(MessageType.Error,
                               $"Spindle number must be between 0 and {SpindleManager.MaxSpindles - 1}");
        }

        if (!code.TryGetString('C', out string? ports))
        {
            return await ReportSpindleAsync(spindleNumber, cancellationToken);
        }

        // The ports have to exist before the spindle can be built from them, so each is created as a
        // general-purpose output first. RepRapFirmware assigns them with IoPort::AssignPorts, which
        // is the same operation spelled differently
        string[] names = ports.Split(RemoteEndstops.PortSeparator, System.StringSplitOptions.RemoveEmptyEntries
                                                                   | System.StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            return new Message(MessageType.Error, "A spindle needs at least a PWM port");
        }

        int[] portNumbers = [-1, -1, -1];
        for (int index = 0; index < names.Length && index < 3; index++)
        {
            // Numbered above the ports a machine addresses directly, so that creating a spindle does
            // not consume output numbers M42 might be using
            int portNumber = GpioManager.MaxGpOutPorts - 1 - ((spindleNumber * 3) + index);
            if (await CreateSpindlePortAsync(portNumber, names[index], code, cancellationToken) is Message error)
            {
                return error;
            }
            portNumbers[index] = portNumber;
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Spindle spindle = spindleManager.Create(spindleNumber, portNumbers[0], portNumbers[1], portNumbers[2]);
            spindle.Min = code.TryGetInt('L', out int min) ? min : 0;
            spindle.Max = code.TryGetInt('F', out int max) ? max : 10000;
            spindle.Frequency = code.TryGetInt('Q', out int frequency) ? frequency : 500;
            spindle.MinPwm = code.TryGetFloat('N', out float minPwm) ? minPwm : 0.0f;
            spindle.MaxPwm = code.TryGetFloat('P', out float maxPwm) ? maxPwm : 1.0f;
            spindle.IdlePwm = code.TryGetFloat('V', out float idlePwm) ? idlePwm : 0.0f;
        }
        return new Message();
    }

    /// <summary>
    /// Create one of the outputs a spindle is driven through
    /// </summary>
    /// <returns>An error if the port could not be created, else null</returns>
    private async ValueTask<Message?> CreateSpindlePortAsync(int portNumber, string port, Commands.Code code,
                                                             CancellationToken cancellationToken)
    {
        byte board;
        string localPort;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!RemoteEndstops.TrySplitPort(port, "Spindle port", out board, out localPort, out string? error))
            {
                return new Message(MessageType.Error, error);
            }
            gpioManager.Create(portNumber);
        }

        // Built from the values rather than from a synthesised code: the port this side addresses is
        // not the one M950 R named, so there is no code carrying the right P to repackage
        CanMessageM950Gpio message = default;
        CanGenericWriter.SetUInt(ref message.Generic, CanMessageM950Gpio.ParamTable, 'P', (uint)portNumber);
        CanGenericWriter.SetString(ref message.Generic, CanMessageM950Gpio.ParamTable, 'C', localPort);
        if (code.TryGetInt('Q', out int frequency))
        {
            CanGenericWriter.SetUInt(ref message.Generic, CanMessageM950Gpio.ParamTable, 'Q', (uint)frequency);
        }

        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        if (reply.Type == MessageType.Error)
        {
            return reply;
        }

        gpioManager.SetLocation(portNumber, board, (byte)portNumber);
        return null;
    }

    /// <summary>
    /// Report one spindle, as M950 R with no C does
    /// </summary>
    private async ValueTask<Message> ReportSpindleAsync(int spindleNumber, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return spindleManager.Find(spindleNumber) is not Spindle spindle
                ? new Message(MessageType.Success, $"Spindle {spindleNumber} is not configured")
                : new Message(MessageType.Success, string.Create(CultureInfo.InvariantCulture,
                    $"Spindle {spindleNumber} speed {spindle.Min}-{spindle.Max} RPM, "
                    + $"currently {spindle.Current} RPM {spindle.State}"));
        }
    }

    /// <summary>
    /// M3 and M4: start a spindle, or set the laser power
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="reverse">True for M4, which turns counter-clockwise</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// In laser mode M3 sets the power rather than starting a spindle, which is why RepRapFirmware
    /// branches on the machine mode here. Laser mode is not ported, so this is the CNC reading
    /// </remarks>
    private async ValueTask<Message> HandleSpindleOnAsync(Commands.Code code, bool reverse,
                                                          CancellationToken cancellationToken)
    {
        // TODO in laser mode M3 sets the laser power (state.machineMode, M452), which is a different
        // code sharing a number. M451/M452/M453 are not ported, so there is no mode to branch on

        int spindleNumber = await SpindleForCodeAsync(code, cancellationToken);
        if (spindleNumber < 0)
        {
            return new Message(MessageType.Error, "No spindle is selected; the current tool has none");
        }

        int rpm;
        if (code.TryGetInt('S', out int requested))
        {
            rpm = requested;
        }
        else
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                rpm = spindleManager.Find(spindleNumber)?.Active ?? 0;
            }
        }

        string? error = await spindleManager.SetSpeedAsync(spindleNumber, rpm, reverse, cancellationToken);
        return error is null ? new Message() : new Message(MessageType.Error, error);
    }

    /// <summary>
    /// M5: stop a spindle, or all of them
    /// </summary>
    private async ValueTask<Message> HandleSpindleOffAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.HasParameter('P'))
        {
            int spindleNumber = code.GetInt('P', 0);
            string? error = await spindleManager.StopAsync(spindleNumber, cancellationToken);
            return error is null ? new Message() : new Message(MessageType.Error, error);
        }

        await spindleManager.StopAllAsync(cancellationToken);
        return new Message();
    }

    /// <summary>
    /// The spindle a code addresses: the one P names, or the current tool's
    /// </summary>
    private async ValueTask<int> SpindleForCodeAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetInt('P', out int spindleNumber))
        {
            return spindleNumber;
        }

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return toolManager.Current?.Spindle ?? -1;
        }
    }
}
