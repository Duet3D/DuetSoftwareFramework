using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Utility;

/// <summary>
/// Context for JSON handling of basic types that belong to neither the object model nor a specific command.
/// These are the values command results and expression evaluation may yield, plus the untyped containers
/// used for partial object model queries
/// </summary>
// Scalars
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(uint))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(char))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(DriverId))]
[JsonSerializable(typeof(JsonElement))]
// Values of a JsonModelDictionary are nullable, and the nullable wrapper is a distinct type to the resolver
[JsonSerializable(typeof(JsonElement?))]
// Arrays
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(uint[]))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DriverId[]))]
// Untyped containers
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(List<object?>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class CommonContext : JsonSerializerContext
{
    static CommonContext() => Default = new CommonContext(CreateJsonSerializerOptions(Default));

    private static JsonSerializerOptions CreateJsonSerializerOptions(CommonContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
