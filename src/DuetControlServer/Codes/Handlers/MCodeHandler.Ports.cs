using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Motion;
using DuetControlServer.Ports;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that create and drive general-purpose I/O
/// </summary>
/// <remarks>
/// A layer rather than a feature: M42 sets an output, M280 addresses one as a servo, and a spindle is
/// three of them driven together. RepRapFirmware has no spindle message on the CAN bus, so this is
/// what a remote spindle is built out of
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// M950 P or S: create a general-purpose output or a servo
    /// </summary>
    /// <remarks>
    /// The two differ only in how the value written to them is interpreted, so they are one port
    /// created two ways. RepRapFirmware likewise treats a servo as a GPIO port with a flag
    /// </remarks>
    private async ValueTask<Message> HandleCreateOutputAsync(Commands.Code code, bool isServo,
                                                             CancellationToken cancellationToken)
    {
        char letter = isServo ? 'S' : 'P';
        if (!code.TryGetInt(letter, out int portNumber)
            || portNumber < 0 || portNumber >= GpioManager.MaxGpOutPorts)
        {
            return new Message(MessageType.Error,
                               $"Output number must be between 0 and {GpioManager.MaxGpOutPorts - 1}");
        }

        if (!code.TryGetString('C', out string? port))
        {
            return await ReportOutputAsync(portNumber, cancellationToken);
        }

        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!RemoteEndstops.TrySplitPort(port, "Output port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            GpOutputPort created = gpioManager.Create(portNumber);
            created.Port = port;
            if (code.TryGetInt('Q', out int frequency))
            {
                created.Freq = frequency;
            }
        }

        // The board numbers its own ports, and the number it will report a write against is the one
        // it is given here. RepRapFirmware sends the same M950 through to the board for that reason
        return (await linkInterface.SendCodeAsync<CanMessageM950Gpio>(board, code, cancellationToken: cancellationToken)).ToMessage();
    }

    /// <summary>
    /// Report one output, as M950 P with no C does
    /// </summary>
    private async ValueTask<Message> ReportOutputAsync(int portNumber, CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            GpOutputPort? port = portNumber < model.State.GpOut.Count ? model.State.GpOut[portNumber] : null;
            return port is null
                ? new Message(MessageType.Success, $"Output {portNumber} is not configured")
                : new Message(MessageType.Success, string.Create(CultureInfo.InvariantCulture,
                    $"Output {portNumber} frequency {port.Freq}Hz, PWM {port.Pwm:F2}"));
        }
    }

    /// <summary>
    /// M42: set the value of a general-purpose output
    /// </summary>
    /// <remarks>
    /// S is read the way a fan speed is: at most 1 is a fraction and anything above it is out of
    /// 255, so <c>M42 P0 S255</c> and <c>M42 P0 S1</c> both mean fully on
    /// </remarks>
    private async ValueTask<Message> HandleSetOutputAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('P', out int portNumber))
        {
            return new Message(MessageType.Error, "Missing output number");
        }
        if (!code.TryGetFloat('S', out float value))
        {
            return await ReportOutputAsync(portNumber, cancellationToken);
        }

        string? error = await gpioManager.WriteAsync(portNumber, NormalisedSpeed(value), isServo: false,
                                                     cancellationToken);
        return error is null ? new Message() : new Message(MessageType.Error, error);
    }

    /// <summary>
    /// M280: set a servo position
    /// </summary>
    /// <remarks>
    /// The value is a pulse width in microseconds where it is large enough to be one, and an angle
    /// otherwise - RepRapFirmware's own rule, and the reason the servo flag travels with the value:
    /// the board is what knows the port's pulse range
    /// </remarks>
    private async ValueTask<Message> HandleServoAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('P', out int portNumber))
        {
            return new Message(MessageType.Error, "Missing servo number");
        }
        if (!code.TryGetFloat('S', out float value))
        {
            return await ReportOutputAsync(portNumber, cancellationToken);
        }

        string? error = await gpioManager.WriteAsync(portNumber, value, isServo: true, cancellationToken);
        return error is null ? new Message() : new Message(MessageType.Error, error);
    }
}
