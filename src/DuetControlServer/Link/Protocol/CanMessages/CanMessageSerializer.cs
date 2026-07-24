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
    /// Allocation-free: the payload is copied into a zero-initialized value on the stack, so payloads
    /// shorter than <typeparamref name="T"/> are zero-padded without touching the heap. Callers on the
    /// hot path (e.g. unsolicited message handling) should prefer this generic overload and switch on the
    /// CAN message type themselves rather than going through a boxed, runtime-typed dispatch.
    /// </remarks>
    /// <typeparam name="T">Target CAN message type.</typeparam>
    /// <param name="payload">Raw payload bytes.</param>
    /// <returns>Deserialized message body.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> is not blittable.</exception>
    /// <exception cref="ArgumentException">Thrown if payload is longer than the target type.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> payload) where T : struct, ICanMessage<T>
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new InvalidOperationException($"Cannot deserialize {typeof(T).FullName} because it is not blittable");
        }

        int size = Unsafe.SizeOf<T>();
        if (payload.Length > size)
        {
            throw new ArgumentException($"Payload too long for {typeof(T).Name}: expected at most {size} bytes but got {payload.Length}", nameof(payload));
        }

        T result = default;
        payload.CopyTo(MemoryMarshal.AsBytes(new Span<T>(ref result)));
        return result;
    }

    /// <summary>
    /// Serialize a CAN message body into <paramref name="destination"/>, truncated to its actual data length.
    /// </summary>
    /// <remarks>
    /// Variable-length messages report fewer bytes than <c>sizeof(T)</c> via
    /// <see cref="ICanMessage{TSelf}.GetActualDataLength"/>; only that many leading bytes are written.
    /// <paramref name="destination"/> must be exactly <see cref="ICanMessage{TSelf}.GetActualDataLength"/> bytes long.
    /// </remarks>
    /// <typeparam name="T">CAN message body type.</typeparam>
    /// <param name="message">Message body to serialize.</param>
    /// <param name="destination">Buffer to write into.</param>
    public static void Serialize<T>(in T message, Span<byte> destination) where T : struct, ICanMessage<T>
    {
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in message));
        source[..destination.Length].CopyTo(destination);
    }
}
