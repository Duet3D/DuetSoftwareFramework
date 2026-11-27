using System;
using System.Collections.Concurrent;
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
        return ToJsonObject(message, new Extent(0, frame.Data.Length), depth: 0);
    }

    // Where a value lies in the payload, and how long the payload is, so that what the frame does not carry is left out
    private readonly record struct Extent(int Offset, int PayloadLength)
    {
        public static readonly Extent Unknown = new(0, int.MaxValue);

        public bool Holds(int size) => Offset + size <= PayloadLength;

        public Extent At(int offset) => this with { Offset = Offset + offset };
    }

    // The public fields and readable properties of a struct, recursively for nested structs; a field
    // past the end of the payload is left out, and its computed properties only with the struct
    private static JsonObject ToJsonObject(object value, Extent extent, int depth)
    {
        JsonObject result = [];
        Type type = value.GetType();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            Extent fieldExtent = FieldExtent(type, field, extent);
            object? fieldValue = field.GetValue(value);
            if (fieldValue?.GetType().GetCustomAttribute<InlineArrayAttribute>() is null && !fieldExtent.Holds(SizeOf(field.FieldType)))
            {
                continue;
            }
            result[field.Name] = ToJson(fieldValue, fieldExtent, depth + 1);
        }
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead && !result.ContainsKey(property.Name))
            {
                result[property.Name] = ToJson(property.GetValue(value), Extent.Unknown, depth + 1);
            }
        }
        return result;
    }

    private static Extent FieldExtent(Type type, FieldInfo field, Extent extent)
    {
        try
        {
            return extent.At(Marshal.OffsetOf(type, field.Name).ToInt32());
        }
        catch (ArgumentException)
        {
            // A layout Marshal cannot describe: assume the frame carries it
            return Extent.Unknown;
        }
    }

    private static readonly ConcurrentDictionary<Type, int> Sizes = new();

    // The size of a type as it lies in a message
    private static int SizeOf(Type type) => Sizes.GetOrAdd(type, t =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!);

    // The elements of an inline array buffer that the payload holds, such as a message's per-drive values
    private static JsonArray InlineArray(object buffer, int length, Extent extent, int depth)
    {
        Type element = buffer.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)[0].FieldType;
        int size = SizeOf(element);
        int present = Enumerable.Range(0, length).TakeWhile(i => extent.At(i * size).Holds(size)).Count();
        MethodInfo read = typeof(CanFrameDecoder).GetMethod(nameof(ReadInlineArray), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(buffer.GetType(), element);
        object?[] elements = (object?[])read.Invoke(null, [buffer, present])!;
        return new JsonArray([.. elements.Select((e, i) => ToJson(e, extent.At(i * size), depth + 1))]);
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

    private static JsonNode? ToJson(object? value, int depth = 0) => ToJson(value, Extent.Unknown, depth);

    private static JsonNode? ToJson(object? value, Extent extent, int depth) => value switch
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
        _ when value.GetType().GetCustomAttribute<InlineArrayAttribute>() is InlineArrayAttribute inline => InlineArray(value, inline.Length, extent, depth),
        _ when depth < 6 && value.GetType().IsValueType => ToJsonObject(value, extent, depth),
        _ => JsonValue.Create(value.ToString())
    };
}
