using DuetAPI;
using DuetControlServer.Link.Protocol.Shared;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// Binary representation of the result of an evaluated expression
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct EvaluationResultHeader
{
    /// <summary>
    /// Type of the expression
    /// </summary>
    [FieldOffset(0)]
    public DataType Type;

    /// <summary>
    /// Channel where the evaluation was performed
    /// </summary>
    [FieldOffset(1)]
    public CodeChannel Channel;

    /// <summary>
    /// Length of the following expression
    /// </summary>
    [FieldOffset(2)]
    public ushort ExpressionLength;

    /// <summary>
    /// Value as integer
    /// </summary>
    [FieldOffset(4)]
    public int IntValue;

    /// <summary>
    /// Value as unsigned integer
    /// </summary>
    [FieldOffset(4)]
    public uint UIntValue;

    /// <summary>
    /// Value as float
    /// </summary>
    [FieldOffset(4)]
    public float FloatValue;
}

