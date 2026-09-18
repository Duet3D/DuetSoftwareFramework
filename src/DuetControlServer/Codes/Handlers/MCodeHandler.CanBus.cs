using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link;
using DuetAPI;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that address the CAN bus itself, or a board across it
/// </summary>
/// <remarks>
/// What these do is put a message on the bus and report what came back, so the board is what composes
/// most of the text: a board holds its own pin table, its own diagnostics and its own debug modules,
/// and none of that can be reconstructed from this side. The exception is M959, whose timeout the main
/// board keeps a copy of because it is the main board that acts on it
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// Read the board a code is addressed to, refusing an address no board can have
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="allowMainBoard">Whether address <see cref="CanId.MasterAddress"> is a legal answer</param>
    /// <param name="defaultAddress">Default address to use if B param not provided</param>
    /// <returns>B parameter of code</returns>
    /// <exception cref="GCodeException">The address is outside the valid range</exception>
    /// <exception cref="MissingParameterException">The code does not say which board</exception>
    /// <remarks>
    /// RepRapFirmware's <c>CanInterface::CheckCanAddress</c>, which refuses 0 and anything past
    /// <see cref="CanId.MaxCanAddress"/> with this wording. A CAN address is seven bits, so an
    /// address past the maximum does not merely fail to find a board: truncated onto the wire it
    /// finds a different one, which is why this is a refusal rather than a warning
    /// </remarks>
    private static byte GetBoardAddress(Commands.Code code, bool allowMainBoard = false, int? defaultAddress = null)
    {
        int address = code.GetInt('B', defaultValue: defaultAddress);
        return CanAddresses.CheckAddressIsValid(address, allowMainBoard);
    }

    /// <summary>
    /// M655: send a custom request to a CAN-connected board
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the board replied</returns>
    /// <remarks>
    /// CanInterface::ProcessM655 (CanInterface.cpp). M655 exists for boards with features M950 and
    /// M42 cannot express, so the request is whatever parameters the code carried and the board is
    /// the only thing that can make sense of them. Either B gives the address or C names a port on
    /// the board that is to answer; with neither there is nowhere to send it. No Duet board
    /// implements a custom feature, so the usual reply is the board saying so
    /// </remarks>
    private async ValueTask<Message> HandleCustomCanRequestAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        byte board;
        if (code.HasParameter('B'))
        {
            board = GetBoardAddress(code);
        }
        else if (code.TryGetString('C', out string? port))
        {
            // C names a board and a port together, and only the port travels: the address in front
            // of it is what says where the request goes
            board = IoPorts.RemoveBoardAddress(port, out _);
        }
        else
        {
            return new Message(MessageType.Error, "B or C parameter must be provided");
        }

        if (board == CanId.MasterAddress)
        {
            return new Message(MessageType.Error, "Not implemented on main board");
        }

        return await linkInterface.SendCodeRequestAsync<CanMessageM655>(board, code, cancellationToken);
    }

    /// <summary>
    /// M959: set or report how long a board may be silent before it is taken to have gone away
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// ExpansionManager::ConfigureConnectionTimeout (ExpansionManager.cpp). The value lives on both
    /// sides: the board uses it to decide how often it must be heard from, and this side uses it to
    /// decide when to raise the expansion-timeout and expansion-reconnect events. This side's copy
    /// is written first, so the readback is answered from it whether or not the board took the
    /// message
    /// </remarks>
    private async ValueTask<Message> HandleConnectionTimeoutAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.HasParameter('B'))
        {
            return await ReportConnectionTimeoutsAsync(cancellationToken);
        }

        byte board = GetBoardAddress(code);

        if (!code.TryGetInt('T', out int timeout))
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                return new Message(MessageType.Success, string.Create(CultureInfo.InvariantCulture,
                    $"Board {board} connection timeout "
                    + $"{expansionBoardManager.FindBoard(board)?.Timeout ?? Board.DefaultConnectionTimeoutSeconds} seconds"));
            }
        }

        if (timeout < MinConnectionTimeoutSeconds || timeout > ushort.MaxValue)
        {
            return new Message(MessageType.Error, "parameter 'T' out of range");
        }

        // Written before the message goes out, as ExpansionManager::ConfigureConnectionTimeout does,
        // so that the readback answers from this side's copy whether or not the board took it. That
        // is not a detail: no firmware handles this message yet, so on real hardware the send times
        // out and the copy is all there is
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            expansionBoardManager.GetOrCreateBoard(board).Timeout = timeout;
        }

        return await linkInterface.SendCodeRequestAsync<CanMessageM959>(board, code, cancellationToken);
    }

    /// <summary>
    /// Shortest connection timeout a board may be given
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MinConnectionTimeoutSeconds</c> (ExpansionManager.h)</remarks>
    private const int MinConnectionTimeoutSeconds = 3;

    /// <summary>
    /// Report the connection timeout of every board the machine knows about, as a bare M959 does
    /// </summary>
    private async ValueTask<Message> ReportConnectionTimeoutsAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            // Board 0 is skipped: it is this program and DuetCANMaster, which cannot lose a
            // connection to themselves. So is a board that exists only because something recorded a
            // setting for it, which is RepRapFirmware's state == unknown and the same filter
            // In address order, as ExpansionManager::ConfigureConnectionTimeout's loop over the
            // addresses is. boards[] here is in the order the boards were discovered, which is the
            // order they happened to announce themselves in and says nothing about the machine
            foreach (Board known in model.Boards
                                         .Where(b => b.CanAddress > CanId.MasterAddress
                                                     && b.State != BoardState.Unknown)
                                         .OrderBy(b => b.CanAddress))
            {
                builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"Board {known.CanAddress} connection timeout {known.Timeout} seconds"));
            }
        }
        return builder.Length == 0
            ? new Message(MessageType.Success, "No expansion boards are connected")
            : new Message(MessageType.Success, builder.ToString().TrimEnd());
    }
}
