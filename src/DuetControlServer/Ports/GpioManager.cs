using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Ports;

/// <summary>
/// The general-purpose outputs a machine has, and what they are driven to
/// </summary>
/// <remarks>
/// <para>
/// Ported from the parts of RepRapFirmware's <c>IoPort</c> handling that a main board with no I/O of
/// its own still needs. A general-purpose output is a PWM pin on an expansion board: this side says
/// what it should be driven to and the board drives it.
/// </para>
/// <para>
/// It is a layer rather than a feature. M42 sets one directly, M280 addresses one as a servo, and a
/// spindle is three of them - RepRapFirmware has no spindle message on the CAN bus at all, so a
/// remote spindle is a PWM port, an on/off port and a direction port driven together
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="linkInterface">Link interface, for the CAN messages a port is driven with</param>
public sealed class GpioManager(Model.ObjectModel model, LinkInterface linkInterface)
{
    /// <summary>
    /// Highest general-purpose output number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxGpOutPorts</c></remarks>
    public const int MaxGpOutPorts = 32;

    /// <summary>
    /// Highest general-purpose input number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxGpInPorts</c></remarks>
    public const int MaxGpInPorts = 32;

    /// <summary>
    /// The board that carries an output
    /// </summary>
    /// <param name="portNumber">The output</param>
    /// <param name="board">Receives the CAN address</param>
    /// <returns>True if the output is on a board that can drive it</returns>
    /// <remarks>
    /// Read from <c>state.gpOut[].port</c>. The number the board knows the port by is the number this
    /// side addresses it by, because that is the number M950 gave it. The caller must hold the object
    /// model lock
    /// </remarks>
    public bool TryGetBoard(int portNumber, out byte board)
    {
        board = CanId.MasterAddress;
        GpOutputPort? port = portNumber >= 0 && portNumber < model.State.GpOut.Count
                             ? model.State.GpOut[portNumber]
                             : null;
        if (port?.Port is not string name)
        {
            return false;
        }

        board = IoPorts.RemoveBoardAddress(name, out _);
        return !CanAddresses.HasNoHardware(board);
    }

    /// <summary>
    /// Make room for an output in the object model
    /// </summary>
    /// <param name="portNumber">The number</param>
    /// <returns>The port</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public GpOutputPort Create(int portNumber)
    {
        while (model.State.GpOut.Count <= portNumber)
        {
            model.State.GpOut.Add(null);
        }

        GpOutputPort port = new();
        model.State.GpOut[portNumber] = port;
        return port;
    }

    /// <summary>
    /// Drive an output
    /// </summary>
    /// <param name="portNumber">The output</param>
    /// <param name="pwm">Duty cycle, 0 to 1</param>
    /// <param name="isServo">Whether the value is a servo position rather than a duty cycle</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the output could not be driven, else null</returns>
    /// <remarks>
    /// A servo and a plain output are the same pin driven two ways, which is why one message carries
    /// both: RepRapFirmware distinguishes them so that a servo's pulse width is interpreted against
    /// its own range rather than as a fraction of full scale
    /// </remarks>
    public async ValueTask<string?> WriteAsync(int portNumber, float pwm, bool isServo,
                                               CancellationToken cancellationToken)
    {
        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (!TryGetBoard(portNumber, out board))
            {
                return $"Output {portNumber} is not configured";
            }
            model.State.GpOut[portNumber]!.Pwm = pwm;
        }

        CanMessageWriteGpio message = new()
        {
            PortNumber = (byte)portNumber,
            Pwm = pwm,
            IsServo = isServo
        };
        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        return reply.Type == MessageType.Error ? reply.Content : null;
    }
}
