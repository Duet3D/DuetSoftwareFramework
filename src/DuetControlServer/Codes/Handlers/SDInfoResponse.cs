using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Details about a storage device as reported by M21 S2
/// </summary>
public sealed class SDInfoDetails
{
    /// <summary>
    /// Index of the storage device
    /// </summary>
    public int Slot { get; set; }

    /// <summary>
    /// Whether the storage device is present
    /// </summary>
    public int Present { get; set; }

    /// <summary>
    /// Capacity of the storage device (in bytes)
    /// </summary>
    public long? Capacity { get; set; }

    /// <summary>
    /// Size of the partition (in bytes)
    /// </summary>
    public long? PartitionSize { get; set; }

    /// <summary>
    /// Free space of the storage device (in bytes)
    /// </summary>
    public long? Free { get; set; }

    /// <summary>
    /// Speed of the storage device (in bytes/s)
    /// </summary>
    public int? Speed { get; set; }
}

/// <summary>
/// Serialization context for M-code responses that are not part of the object model
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SDInfoDetails))]
public sealed partial class MCodeResponseContext : JsonSerializerContext
{
    static MCodeResponseContext() => Default = new MCodeResponseContext(CreateJsonSerializerOptions(Default));

    private static JsonSerializerOptions CreateJsonSerializerOptions(MCodeResponseContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
