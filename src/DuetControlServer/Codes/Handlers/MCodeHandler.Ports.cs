using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
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
    /// <summary>Shortest pulse a servo is driven with, and the value below which M280 S is an angle (in us)</summary>
    /// <remarks>RepRapFirmware's <c>GCodes::MinServoPulseWidth</c></remarks>
    private const float MinServoPulseWidth = 544.0f;

    /// <summary>Longest pulse a servo is driven with (in us)</summary>
    /// <remarks>RepRapFirmware's <c>GCodes::MaxServoPulseWidth</c></remarks>
    private const float MaxServoPulseWidth = 2400.0f;

    /// <summary>Angle the longest pulse stands for, and the most M280 S can ask for (in degrees)</summary>
    /// <remarks>RepRapFirmware's GCodes2.cpp case 280</remarks>
    private const float MaxServoAngle = 180.0f;

    /// <summary>Seconds in a microsecond, for turning a pulse width into a duty cycle</summary>
    private const float MicrosecondsToSeconds = 1.0e-6f;

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
            // Without a port this is either a change to the frequency of an output that exists
            // already or a request to report it
            return code.HasParameter('Q')
                   ? await SetOutputFrequencyAsync(code, portNumber, cancellationToken)
                   : await ReportOutputAsync(portNumber, cancellationToken);
        }

        byte board;
        int createFrequency;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!IoPorts.TrySplitPort(port, "Output port", out board, out _, out string? error))
            {
                return new Message(MessageType.Error, error);
            }

            GpOutputPort created = gpioManager.Create(portNumber);
            created.Port = port;

            // The frequency is kept on this side as well as sent to the board, because M280 is what
            // turns a pulse width into the duty cycle the board is asked for and needs it to do so
            created.Freq = code.TryGetInt('Q', out int frequency) ? frequency
                           : isServo ? GpOutputPort.DefaultServoFrequency : GpOutputPort.DefaultFrequency;
            createFrequency = created.Freq;
        }

        return await SendM950GpioAsync(portNumber, port, createFrequency, isServo, board, cancellationToken);
    }

    /// <summary>
    /// Send a board the M950 that creates or reconfigures one of its outputs
    /// </summary>
    /// <param name="portNumber">The output, which is the number the board will know it by</param>
    /// <param name="port">Port to assign, or null to leave the board's assignment alone</param>
    /// <param name="frequency">PWM frequency in Hz</param>
    /// <param name="isServo">Whether the port is driven as a servo, or null not to say</param>
    /// <param name="board">CAN address of the board carrying the output</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board replied</returns>
    /// <remarks>
    /// <para>
    /// GpOutputPort::Configure (GpOutPort.cpp), which sends P, Q, S and C when it assigns a port and
    /// only P and Q when it changes the frequency of one that exists already.
    /// </para>
    /// <para>
    /// The message says what this side resolved rather than repeating the code, because the two are
    /// not the same letters: the board reads P as the port number and S as the servo flag, so an
    /// <c>M950 S0</c> passed through would arrive as a plain output flagged with its own number and
    /// no port number at all. The frequency travels for the same reason it is kept here - the board
    /// would otherwise apply a default of its own, and M280 turns a pulse width into a duty cycle
    /// against the figure this side holds
    /// </para>
    /// </remarks>
    private async ValueTask<Message> SendM950GpioAsync(int portNumber, string? port, int? frequency,
                                                       bool? isServo, byte board,
                                                       CancellationToken cancellationToken)
    {
        CanMessageM950Gpio message = default;
        message.P = (ushort)portNumber;
        message.Q = frequency is int hz ? (ushort)hz : null;
        message.S = isServo is bool servo ? (byte)(servo ? 1 : 0) : null;
        message.C = port;

        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        return response.ToMessage();
    }

    /// <summary>
    /// M950 P or S with Q and no C: change the frequency of an output that exists already
    /// </summary>
    /// <remarks>GpOutputPort::Configure (GpOutPort.cpp), the frequency-only form</remarks>
    private async ValueTask<Message> SetOutputFrequencyAsync(Commands.Code code, int portNumber,
                                                             CancellationToken cancellationToken)
    {
        byte board;
        int frequency;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!gpioManager.TryGetBoard(portNumber, out board))
            {
                return new Message(MessageType.Error, $"Output {portNumber} is not configured");
            }
            model.State.GpOut[portNumber]!.Freq = frequency = code.GetInt('Q');
        }

        return await SendM950GpioAsync(portNumber, port: null, frequency, isServo: null, board, cancellationToken);
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

        string? error = await gpioManager.WriteAsync(portNumber, GetPwmValue(value), isServo: false,
                                                     cancellationToken);
        return error is null ? new Message() : new Message(MessageType.Error, error);
    }

    /// <summary>
    /// M280: set a servo position
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 280. The value is a pulse width in microseconds where it is large enough to
    /// be one, and an angle otherwise - RepRapFirmware's own rule. What reaches the board is the duty
    /// cycle that width amounts to at the port's refresh frequency, and the servo flag travels with it
    /// so the board drives the pin as a servo rather than as a plain PWM output
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

        // An S small enough to be an angle is one, and anything else is a pulse width in microseconds
        float pulseWidth;
        if (value < 0.0f)
        {
            // Negative disables the servo, which is a pulse width of nothing at all
            pulseWidth = 0.0f;
        }
        else if (value < MinServoPulseWidth)
        {
            pulseWidth = MathF.Min(value, MaxServoAngle) * ((MaxServoPulseWidth - MinServoPulseWidth) / MaxServoAngle)
                         + MinServoPulseWidth;
        }
        else
        {
            pulseWidth = MathF.Min(value, MaxServoPulseWidth);
        }

        int frequency;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            GpOutputPort? servo = portNumber >= 0 && portNumber < model.State.GpOut.Count
                                  ? model.State.GpOut[portNumber]
                                  : null;
            if (servo is null)
            {
                return new Message(MessageType.Error, $"Output {portNumber} is not configured");
            }
            frequency = servo.Freq;
        }

        // What the port is driven to is the fraction of each refresh period the pulse takes up, which
        // is why the servo's own refresh frequency has to be known on this side
        string? error = await gpioManager.WriteAsync(portNumber, pulseWidth * MicrosecondsToSeconds * frequency,
                                                     isServo: true, cancellationToken);
        return error is null ? new Message() : new Message(MessageType.Error, error);
    }
}
