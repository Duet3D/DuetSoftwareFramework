using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Common interface implemented by all CAN message body structs that mirror the
/// definitions in CANlib's <c>CanMessageFormats.h</c>.
/// </summary>
/// <remarks>
/// These structs are hand-written for now but are intended to become the output of a
/// generator driven by a neutral schema (see the CANlib submodule in <c>lib/CANlib</c>).
/// The layout conventions are: <c>[StructLayout(LayoutKind.Sequential, Pack = 1)]</c> to match
/// <c>__attribute__((packed))</c>, bitfields emulated via properties over a backing integer field
/// (lowest declared C++ field occupies the least-significant bits, matching little-endian targets),
/// and fixed C arrays represented as blittable <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/>
/// buffers so they can be (de)serialized with <c>MemoryMarshal</c>.
/// </remarks>
public interface ICanMessage
{
    /// <summary>
    /// CAN message type identifying this message (placed in the CAN id)
    /// </summary>
    static abstract CanMessageType MessageType { get; }
}
