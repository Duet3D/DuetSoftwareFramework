using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Helpers for interpreting CAN payload bytes as strongly typed CAN message bodies.
/// </summary>
public static class CanMessageSerializer
{
    private delegate ICanMessage DeserializeDelegate(ReadOnlySpan<byte> payload);

    private static readonly Lazy<Dictionary<CanMessageType, DeserializeDelegate>> _deserializers =
        new(BuildDeserializerMap);

    /// <summary>
    /// Deserialize a payload as a concrete CAN message body type.
    /// </summary>
    /// <typeparam name="T">Target CAN message type.</typeparam>
    /// <param name="payload">Raw payload bytes.</param>
    /// <returns>Deserialized message body.</returns>
    /// <exception cref="InvalidOperationException">Thrown if <typeparamref name="T"/> is not blittable.</exception>
    /// <exception cref="ArgumentException">Thrown if payload is longer than the target type.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> payload) where T : struct, ICanMessage
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

        if (payload.Length == size)
        {
            return MemoryMarshal.Read<T>(payload);
        }

        byte[] buffer = new byte[size];
        payload.CopyTo(buffer);
        return MemoryMarshal.Read<T>(buffer);
    }

    /// <summary>
    /// Deserialize a payload to an <see cref="ICanMessage"/> based on CAN message type.
    /// </summary>
    /// <param name="messageType">CAN message type.</param>
    /// <param name="payload">Raw payload bytes.</param>
    /// <returns>Deserialized message body.</returns>
    /// <exception cref="NotSupportedException">Thrown when no CLR type is registered for <paramref name="messageType"/>.</exception>
    [return: NotNull]
    public static ICanMessage Deserialize(CanMessageType messageType, ReadOnlySpan<byte> payload)
    {
        if (!TryDeserialize(messageType, payload, out ICanMessage? message))
        {
            throw new NotSupportedException($"No ICanMessage type is registered for CAN message type {messageType}");
        }

        return message ?? throw new NotSupportedException($"No ICanMessage type is registered for CAN message type {messageType}");
    }

    /// <summary>
    /// Attempt to deserialize a payload based on CAN message type.
    /// </summary>
    /// <param name="messageType">CAN message type.</param>
    /// <param name="payload">Raw payload bytes.</param>
    /// <param name="message">Deserialized message body if successful.</param>
    /// <returns><c>true</c> if a registered message type was found; otherwise <c>false</c>.</returns>
    public static bool TryDeserialize(CanMessageType messageType, ReadOnlySpan<byte> payload, [MaybeNullWhen(false)] out ICanMessage? message)
    {
        if (_deserializers.Value.TryGetValue(messageType, out DeserializeDelegate? deserializer))
        {
            message = deserializer(payload);
            return true;
        }

        message = null;
        return false;
    }

    private static Dictionary<CanMessageType, DeserializeDelegate> BuildDeserializerMap()
    {
        Dictionary<CanMessageType, DeserializeDelegate> result = [];
        Type canMessageInterface = typeof(ICanMessage);
        MethodInfo makeDeserializerMethod = typeof(CanMessageSerializer).GetMethod(nameof(MakeDeserializer), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (Type type in canMessageInterface.Assembly.GetTypes())
        {
            if (!canMessageInterface.IsAssignableFrom(type) || !type.IsValueType || type.IsAbstract)
            {
                continue;
            }

            PropertyInfo? messageTypeProperty = type.GetProperty(nameof(ICanMessage.MessageType), BindingFlags.Public | BindingFlags.Static);
            if (messageTypeProperty?.PropertyType != typeof(CanMessageType))
            {
                continue;
            }

            if (messageTypeProperty.GetValue(null) is not CanMessageType messageType)
            {
                continue;
            }

            MethodInfo closedFactory = makeDeserializerMethod.MakeGenericMethod(type);
            DeserializeDelegate deserializer = (DeserializeDelegate)closedFactory.Invoke(null, null)!;
            result[messageType] = deserializer;
        }

        return result;
    }

    private static DeserializeDelegate MakeDeserializer<T>() where T : struct, ICanMessage
        => static payload => Deserialize<T>(payload);
}