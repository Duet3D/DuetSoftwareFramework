using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetEmulation.Can;

/// <summary>
/// Any CAN frame's payload as the fields of its message, decoded with the message types DSF generates
/// from the CAN schema, so that a capture can show what a frame says without the reader knowing its type
/// </summary>
public static class CanFrameDecoder
{
    private static readonly IReadOnlyDictionary<CanMessageType, Func<CanFrame, JsonObject>> Decoders = BuildDecoders();

    /// <summary>
    /// The frame's message: a generic message's parameters by G-code letter, any other message's
    /// fields by name, or null for a type DSF has no layout for
    /// </summary>
    public static JsonObject? Decode(CanFrame frame)
    {
        if (!Decoders.TryGetValue(frame.MessageType, out Func<CanFrame, JsonObject>? decode))
        {
            return null;
        }
        try
        {
            return decode(frame);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or TargetInvocationException)
        {
            // A payload too short or malformed for its layout is shown raw only
            return null;
        }
    }

    private static Dictionary<CanMessageType, Func<CanFrame, JsonObject>> BuildDecoders()
    {
        Dictionary<CanMessageType, Func<CanFrame, JsonObject>> decoders = [];
        MethodInfo decodeFields = typeof(CanFrameDecoder).GetMethod(nameof(DecodeFields), BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo decodeParameters = typeof(CanFrameDecoder).GetMethod(nameof(DecodeParameters), BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (Type type in typeof(ICanMessage<>).Assembly.GetTypes().Where(t => t.IsValueType && !t.IsGenericTypeDefinition))
        {
            bool isMessage = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICanMessage<>));
            if (!isMessage)
            {
                continue;
            }
            bool isGeneric = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICanGenericMessage<>));
            CanMessageType messageType = (CanMessageType)type.GetProperty("MessageType", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            MethodInfo method = (isGeneric ? decodeParameters : decodeFields).MakeGenericMethod(type);
            Func<CanFrame, JsonObject> decode = method.CreateDelegate<Func<CanFrame, JsonObject>>();
            // A type that is also a generic message decodes best by its parameters
            if (isGeneric || !decoders.ContainsKey(messageType))
            {
                decoders[messageType] = decode;
            }
        }
        return decoders;
    }

    private static JsonObject DecodeParameters<T>(CanFrame frame) where T : struct, ICanGenericMessage<T>
    {
        JsonObject parameters = [];
        foreach ((char letter, object value) in frame.Parameters<T>())
        {
            parameters[letter.ToString()] = ToJson(value);
        }
        return parameters;
    }

    private static JsonObject DecodeFields<T>(CanFrame frame) where T : struct, ICanMessage<T>
    {
        object message = CanMessageSerializer.Deserialize<T>(frame.Data);
        return ToJsonObject(message, depth: 0);
    }

    // The public fields and readable properties of a message, recursively for nested structs
    private static JsonObject ToJsonObject(object value, int depth)
    {
        JsonObject result = [];
        Type type = value.GetType();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            result[field.Name] = ToJson(field.GetValue(value), depth + 1);
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead && !result.ContainsKey(property.Name))
            {
                result[property.Name] = ToJson(property.GetValue(value), depth + 1);
            }
        }
        return result;
    }

    // The elements of an inline array buffer, such as a message's per-drive values
    private static JsonArray InlineArray(object buffer, int length, int depth)
    {
        Type element = buffer.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)[0].FieldType;
        MethodInfo read = typeof(CanFrameDecoder).GetMethod(nameof(ReadInlineArray), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(buffer.GetType(), element);
        object?[] elements = (object?[])read.Invoke(null, [buffer, length])!;
        return new JsonArray([.. elements.Select(e => ToJson(e, depth + 1))]);
    }

    private static object?[] ReadInlineArray<TBuffer, TElement>(object boxed, int length) where TBuffer : struct
    {
        TBuffer buffer = (TBuffer)boxed;
        Span<TElement> elements = MemoryMarshal.CreateSpan(ref Unsafe.As<TBuffer, TElement>(ref buffer), length);
        object?[] result = new object?[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = elements[i];
        }
        return result;
    }

    private static JsonNode? ToJson(object? value, int depth = 0) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        char c => JsonValue.Create(c.ToString()),
        string s => JsonValue.Create(s),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Enum e => JsonValue.Create(e.ToString()),
        ulong u => JsonValue.Create(u),
        sbyte or byte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
        Array array => new JsonArray([.. array.Cast<object?>().Select(v => ToJson(v, depth + 1))]),
        System.Collections.IEnumerable sequence when value is not ImmutableArray<CanParamDescriptor> =>
            new JsonArray([.. sequence.Cast<object?>().Select(v => ToJson(v, depth + 1))]),
        _ when value.GetType().GetCustomAttribute<InlineArrayAttribute>() is InlineArrayAttribute inline => InlineArray(value, inline.Length, depth),
        _ when depth < 6 && value.GetType().IsValueType => ToJsonObject(value, depth),
        _ => JsonValue.Create(value.ToString())
    };
}
