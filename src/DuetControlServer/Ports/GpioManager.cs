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
    /// Where each output lives: which board carries it and which port it is on that board
    /// </summary>
    /// <remarks>
    /// <c>state.gpOut[]</c> holds the frequency and the duty cycle but not the port, so the address
    /// has to live beside it. That is a gap in the object model rather than a decision - §1's first
    /// rule wants a machine rebuildable from the model, and a port whose board is forgotten cannot be
    /// driven after a restart
    /// </remarks>
    private readonly Dictionary<int, (byte Board, byte Port)> _outputs = [];

    /// <summary>
    /// Record where an output lives
    /// </summary>
    public void SetLocation(int portNumber, byte board, byte localPort) => _outputs[portNumber] = (board, localPort);

    /// <summary>
    /// Whether an output has been created
    /// </summary>
    public bool IsConfigured(int portNumber) => _outputs.ContainsKey(portNumber);

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
        if (!_outputs.TryGetValue(portNumber, out (byte Board, byte Port) location))
        {
            return $"Output {portNumber} is not configured";
        }
        if (CanAddresses.HasNoHardware(location.Board))
        {
            return CanAddresses.NoHardwareMessage($"Output {portNumber}");
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (portNumber < model.State.GpOut.Count && model.State.GpOut[portNumber] is GpOutputPort port)
            {
                port.Pwm = pwm;
            }
        }

        CanMessageWriteGpio message = new()
        {
            PortNumber = location.Port,
            Pwm = pwm,
            IsServo = isServo
        };
        CanResponse response = await linkInterface.SendCanMessageAsync(location.Board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        return reply.Type == MessageType.Error ? reply.Content : null;
    }
}
