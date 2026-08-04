using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Helpers for interpreting CAN payload bytes as strongly typed CAN message bodies.
/// </summary>
public static class CanMessageSerializer
{
    /// <summary>
    /// Deserialize a payload as a concrete CAN message body type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocation-free: the payload is copied into a zero-initialized value on the stack, so payloads
    /// shorter than <typeparamref name="T"/> are zero-padded without touching the heap. Callers on the
    /// hot path (e.g. unsolicited message handling) should prefer this generic overload and switch on the
    /// CAN message type themselves rather than going through a boxed, runtime-typed dispatch.
    /// </para>
    /// <para>
    /// A payload longer than <typeparamref name="T"/> keeps its leading bytes and the rest is ignored.
    /// CAN messages grow by appending - that is why they carry reserved fields and why a changed layout
    /// gets a new message type - so trailing bytes are either data a newer firmware appended or a
    /// variable-length tail this side does not model. <c>CanMessageBoardStatusV1</c> is the second:
    /// its fixed part is followed by one entry per analog handle the board reports, which is data for
    /// a reader that wants it rather than a sign that anything is wrong. Refusing the message would
    /// throw away the part that was understood.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">Target CAN message type.</typeparam>
    /// <param name="payload">Raw payload bytes.</param>
    /// <returns>Deserialized message body.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> is not blittable.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> payload) where T : struct, ICanMessageBody<T>
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new InvalidOperationException($"Cannot deserialize {typeof(T).FullName} because it is not blittable");
        }

        int size = Unsafe.SizeOf<T>();
        T result = default;
        payload[..Math.Min(payload.Length, size)].CopyTo(MemoryMarshal.AsBytes(new Span<T>(ref result)));
        return result;
    }

    /// <summary>
    /// Serialize a CAN message body into <paramref name="destination"/>, truncated to its actual data length.
    /// </summary>
    /// <remarks>
    /// Variable-length messages report fewer bytes than <c>sizeof(T)</c> via
    /// <see cref="ICanMessageBody{TSelf}.GetActualDataLength"/>; only that many leading bytes are written.
    /// <paramref name="destination"/> must be exactly <see cref="ICanMessageBody{TSelf}.GetActualDataLength"/> bytes long.
    /// </remarks>
    /// <typeparam name="T">CAN message body type.</typeparam>
    /// <param name="message">Message body to serialize.</param>
    /// <param name="destination">Buffer to write into.</param>
    public static void Serialize<T>(in T message, Span<byte> destination) where T : struct, ICanMessageBody<T>
    {
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in message));
        source[..destination.Length].CopyTo(destination);
    }
}
