using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Return information from an expansion board.
/// Mirrors <c>CanMessageMovementLinearShaped</c> in CANlib's <c>CanMessageFormats.h</c>:
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 20)]
public struct CanMessageMovementLinearShaped : ICanMessage<CanMessageMovementLinearShaped>
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.MovementLinearShaped;

    public static byte SeqMask => 0x0F;
    public uint WhenToExecute;
    public uint AccelerationClocks;
    public uint SteadyClocks;
    public uint DecelerationClocks;

    /// <summary>
    /// Backing 32 bits holding <c>extruderDrives : 8, numDrivers : 4, seq : 4, zero1 : 8, usePressureAdvance : 1, useLateInputShaping : 1, zero2 : 6</c>
    /// </summary>
    private uint _bits;

    public byte ExtruderDrives
    {
        readonly get => (byte)(_bits & 0xFF);
        set => _bits = (_bits & 0xFFFFFF00) | (uint)(value & 0xFF);
    }

    public byte NumDrivers
    {
        readonly get => (byte)((_bits >> 8) & 0x0F);
        set => _bits = (_bits & 0xFFFFF0FF) | ((uint)value & 0x0F) << 8;
    }

    public byte Seq
    {
        readonly get => (byte)((_bits >> 12) & 0x0F);
        set => _bits = (_bits & 0xFFFF0FFF) | ((uint)value & 0x0F) << 12;
    }

    public bool UsePressureAdvance
    {
        readonly get => (_bits & 0x01000000u) != 0;
        set => _bits = value ? (_bits | 0x01000000u) : (_bits & ~0x01000000u);
    }

    public bool UseLateInputShaping
    {
        readonly get => (_bits & 0x02000000u) != 0;
        set => _bits = value ? (_bits | 0x02000000u) : (_bits & ~0x02000000u);
    }
}
