using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Per-driver settings addressed to whichever expansion boards carry the drivers
/// </summary>
/// <remarks>
/// <para>
/// The <c>CanMessageMultipleDrivesRequest</c> family carries one value per driver, but a message goes
/// to a single board and its <c>DriversToUpdate</c> bitmap is in that board's local driver numbers. A
/// setting applied to a set of <see cref="DriverId"/>s therefore has to be split by board address
/// first, which is what this does.
/// </para>
/// <para>
/// Values are packed in ascending order of local driver number rather than at the driver's own index:
/// the receiving board walks the bitmap and takes the next value for each bit it finds, so the n'th
/// value belongs to the n'th set bit.
/// </para>
/// </remarks>
internal static class RemoteDrivers
{
    /// <summary>
    /// Highest local driver number the 16-bit bitmap can address
    /// </summary>
    private const int MaxLocalDriver = 15;

    /// <summary>
    /// How many drivers one message can carry, which is the length of its value array
    /// </summary>
    private const int MaxDriversPerMessage = 8;

    /// <summary>
    /// A driver and the value being applied to it
    /// </summary>
    /// <typeparam name="T">Type of the value</typeparam>
    /// <param name="Driver">The driver</param>
    /// <param name="Value">Value to apply to it</param>
    internal readonly record struct DriverValue<T>(DriverId Driver, T Value);

    /// <summary>
    /// Tell the boards the steps per mm and microstepping of their drivers (M92, M350, M584)
    /// </summary>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="values">Drivers with their steps per mm, microstepping and interpolation flag</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the boards reported, empty if they were all happy</returns>
    public static async ValueTask<IList<string>> SetStepsPerMmAndMicrosteppingAsync(
        LinkInterface linkInterface,
        IEnumerable<DriverValue<(float StepsPerMm, int Microstepping, bool Interpolated)>> values,
        CancellationToken cancellationToken = default)
    {
        List<string> replies = [];
        foreach ((byte board, ushort bitmap, (float StepsPerMm, int Microstepping, bool Interpolated)[] ordered) in GroupByBoard(values))
        {
            CanMessageMultipleDrivesRequestStepsPerUnitAndMicrostepping message = new()
            {
                DriversToUpdate = bitmap
            };
            for (int i = 0; i < ordered.Length; i++)
            {
                // Bit 15 of the microstepping field is the interpolation flag; the board masks the
                // rest down to 10 bits when it reads it back out
                ushort microstepping = (ushort)((ordered[i].Microstepping & 0x03FF) | (ordered[i].Interpolated ? 0x8000 : 0));
                message.Values[i].Set(ordered[i].StepsPerMm, microstepping);
            }

            await SendAsync(linkInterface, board, message, replies, cancellationToken);
        }
        return replies;
    }

    /// <summary>
    /// Set the motor current of a number of drivers (M906)
    /// </summary>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="values">Drivers and their currents in mA</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the boards reported, empty if they were all happy</returns>
    public static async ValueTask<IList<string>> SetMotorCurrentsAsync(LinkInterface linkInterface, IEnumerable<DriverValue<float>> values,
                                                                      CancellationToken cancellationToken = default)
    {
        List<string> replies = [];
        foreach ((byte board, ushort bitmap, float[] ordered) in GroupByBoard(values))
        {
            CanMessageMultipleDrivesRequestMotorCurrents message = new()
            {
                DriversToUpdate = bitmap
            };
            for (int i = 0; i < ordered.Length; i++)
            {
                message.Values[i] = ordered[i];
            }

            await SendAsync(linkInterface, board, message, replies, cancellationToken);
        }
        return replies;
    }

    /// <summary>
    /// Enable, idle or disable a number of drivers (M17, M18, M84)
    /// </summary>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="values">Drivers and the state to put each in, with the idle current percentage</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the boards reported, empty if they were all happy</returns>
    public static async ValueTask<IList<string>> SetDriverStatesAsync(LinkInterface linkInterface,
                                                                     IEnumerable<DriverValue<(ushort Mode, ushort IdlePercent)>> values,
                                                                     CancellationToken cancellationToken = default)
    {
        List<string> replies = [];
        foreach ((byte board, ushort bitmap, (ushort Mode, ushort IdlePercent)[] ordered) in GroupByBoard(values))
        {
            CanMessageMultipleDrivesRequestDriverStateControl message = new()
            {
                DriversToUpdate = bitmap
            };
            for (int i = 0; i < ordered.Length; i++)
            {
                message.Values[i].Set(ordered[i].Mode, ordered[i].IdlePercent);
            }

            await SendAsync(linkInterface, board, message, replies, cancellationToken);
        }
        return replies;
    }

    /// <summary>
    /// Set the standstill current percentage of a number of drivers (M917)
    /// </summary>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="values">Drivers and their standstill current percentages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the boards reported, empty if they were all happy</returns>
    public static async ValueTask<IList<string>> SetStandstillCurrentFactorAsync(LinkInterface linkInterface,
                                                                                IEnumerable<DriverValue<float>> values,
                                                                                CancellationToken cancellationToken = default)
    {
        List<string> replies = [];
        foreach ((byte board, ushort bitmap, float[] ordered) in GroupByBoard(values))
        {
            CanMessageMultipleDrivesRequestStandstillCurrentFactor message = new()
            {
                DriversToUpdate = bitmap
            };
            for (int i = 0; i < ordered.Length; i++)
            {
                message.Values[i] = ordered[i];
            }

            await SendAsync(linkInterface, board, message, replies, cancellationToken);
        }
        return replies;
    }

    /// <summary>
    /// Set the pressure advance of a number of extruder drivers (M572)
    /// </summary>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="values">Drivers and their pressure advance in seconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the boards reported, empty if they were all happy</returns>
    public static async ValueTask<IList<string>> SetPressureAdvanceAsync(LinkInterface linkInterface, IEnumerable<DriverValue<float>> values,
                                                                        CancellationToken cancellationToken = default)
    {
        List<string> replies = [];
        foreach ((byte board, ushort bitmap, float[] ordered) in GroupByBoard(values))
        {
            CanMessageMultipleDrivesRequestPressureAdvanceV1 message = new()
            {
                DriversToUpdate = bitmap
            };
            for (int i = 0; i < ordered.Length; i++)
            {
                message.Values[i] = ordered[i];
            }

            await SendAsync(linkInterface, board, message, replies, cancellationToken);
        }
        return replies;
    }

    /// <summary>
    /// Send one message and collect anything the board said about it
    /// </summary>
    /// <typeparam name="TMessage">Type of the CAN message</typeparam>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="board">CAN address to send to</param>
    /// <param name="message">The message</param>
    /// <param name="replies">Where to add the board's reply if it made one</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private static async ValueTask SendAsync<TMessage>(LinkInterface linkInterface, byte board, TMessage message, List<string> replies,
                                                       CancellationToken cancellationToken)
        where TMessage : struct, ICanMessage<TMessage>
    {
        CanResponse response = await linkInterface.SendCanMessageAsync(board, in message, CanMessageType.StandardReply,
                                                                       cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.PayloadString))
        {
            replies.Add(response.PayloadString);
        }
    }

    /// <summary>
    /// Group a set of per-driver values by the board that carries them
    /// </summary>
    /// <typeparam name="T">Type of the value</typeparam>
    /// <param name="values">Drivers and their values</param>
    /// <returns>One entry per board and message, with the bitmap of its local drivers and the values in bitmap order</returns>
    /// <exception cref="ArgumentOutOfRangeException">A driver number is outside what the bitmap can address</exception>
    /// <remarks>
    /// A board with more drivers than one message can carry is split across several, which is why
    /// this can yield more than one entry per board
    /// </remarks>
    public static IEnumerable<(byte Board, ushort DriverBitmap, T[] Values)> GroupByBoard<T>(IEnumerable<DriverValue<T>> values)
    {
        foreach (IGrouping<int, DriverValue<T>> board in values.GroupBy(value => value.Driver.Board))
        {
            if (board.Key is < 0 or > CanId.MaxCanAddress)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"CAN address {board.Key} is out of range");
            }

            // The receiver takes the n'th value for the n'th set bit, so ascending driver order is
            // what makes the bitmap and the values line up. Duplicates would set the same bit twice
            // and leave a value with no bit to belong to, so the last one given wins
            DriverValue<T>[] ordered = [.. board
                .GroupBy(value => value.Driver.Port)
                .Select(driver => driver.Last())
                .OrderBy(value => value.Driver.Port)];

            foreach (DriverValue<T>[] chunk in ordered.Chunk(MaxDriversPerMessage))
            {
                ushort bitmap = 0;
                foreach (DriverValue<T> value in chunk)
                {
                    if (value.Driver.Port is < 0 or > MaxLocalDriver)
                    {
                        throw new ArgumentOutOfRangeException(nameof(values), $"Driver {value.Driver} is out of range");
                    }
                    bitmap |= (ushort)(1 << value.Driver.Port);
                }

                yield return ((byte)board.Key, bitmap, [.. chunk.Select(value => value.Value)]);
            }
        }
    }
}
