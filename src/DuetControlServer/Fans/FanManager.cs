using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Fans;

/// <summary>
/// The fans a machine has, and what they are asked to run at
/// </summary>
/// <remarks>
/// <para>
/// Ported from the parts of RepRapFirmware's <c>FansManager</c> that survive §1's fourth rule. A fan
/// is a PWM output on an expansion board, and the board is what drives it - including thermostatic
/// control, which is a rule the board applies to sensors it already reads rather than something this
/// side polls and acts on.
/// </para>
/// <para>
/// So this is the configuration and the requested speed. What comes back - the actual PWM and the
/// tacho reading - is written to <c>fans[]</c> by <c>ExpansionBoardManager</c> as the boards report
/// it
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="linkInterface">Link interface, for the CAN messages a fan is driven with</param>
public sealed class FanManager(Model.ObjectModel model, LinkInterface linkInterface)
{
    /// <summary>
    /// Highest fan number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxFans</c> for a Duet 3 MB6HC</remarks>
    public const int MaxFans = 20;

    /// <summary>
    /// Find a fan by number
    /// </summary>
    /// <param name="fanNumber">The number</param>
    /// <returns>The fan, or null if there is none</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    public Fan? Find(int fanNumber)
        => fanNumber >= 0 && fanNumber < model.Fans.Count ? model.Fans[fanNumber] : null;

    /// <summary>
    /// Make room for a fan number in the object model
    /// </summary>
    /// <param name="fanNumber">The number</param>
    /// <returns>The fan</returns>
    /// <remarks>
    /// The collection is indexed by fan number, so a machine that defines fan 3 and nothing below it
    /// still has four entries. The caller must hold the object model write lock
    /// </remarks>
    public Fan Create(int fanNumber)
    {
        while (model.Fans.Count <= fanNumber)
        {
            model.Fans.Add(null);
        }

        Fan fan = new();
        model.Fans[fanNumber] = fan;
        return fan;
    }

    /// <summary>
    /// The board that carries a fan
    /// </summary>
    /// <param name="fanNumber">The fan</param>
    /// <param name="board">Receives the CAN address</param>
    /// <returns>True if the fan is on a board that can drive it</returns>
    /// <remarks>
    /// Read from <c>fans[].port</c>, which is where the machine's description of itself lives, so a
    /// fan is addressable for as long as the object model says where it is. The caller must hold the
    /// object model lock
    /// </remarks>
    public bool TryGetBoard(int fanNumber, out byte board)
    {
        board = CanId.MasterAddress;
        if (Find(fanNumber)?.Port is not string port)
        {
            return false;
        }

        board = IoPorts.RemoveBoardAddress(port, out _);
        return !CanAddresses.HasNoHardware(board);
    }

    /// <summary>
    /// Ask a fan to run at a speed
    /// </summary>
    /// <param name="fanNumber">The fan</param>
    /// <param name="pwm">Requested PWM, 0 to 1</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the fan could not be driven, else null</returns>
    public async ValueTask<string?> SetSpeedAsync(int fanNumber, float pwm, CancellationToken cancellationToken)
    {
        byte board;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(fanNumber) is not Fan fan)
            {
                return $"Fan {fanNumber} not found";
            }
            if (!TryGetBoard(fanNumber, out board))
            {
                return $"Fan {fanNumber} is not on an expansion board";
            }
            fan.RequestedValue = pwm;
        }

        CanMessageSetFanSpeed message = new()
        {
            FanNumber = (ushort)fanNumber,
            Pwm = pwm
        };
        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message,
                                                                       CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        Message reply = response.ToMessage();
        return reply.Type == MessageType.Error ? reply.Content : null;
    }
}
