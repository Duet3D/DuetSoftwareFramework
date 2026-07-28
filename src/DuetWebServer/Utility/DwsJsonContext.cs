using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetWebServer;

/// <summary>
/// Response body of GET /machine/connect
/// </summary>
public sealed class SessionKeyResponse
{
    /// <summary>
    /// Session key to use for authentication
    /// </summary>
    public string SessionKey { get; set; } = string.Empty;
}

/// <summary>
/// Response body of a successful GET /rr_connect request
/// </summary>
public sealed class RepRapFirmwareConnectResponse
{
    /// <summary>
    /// API level of the emulated interface
    /// </summary>
    public int ApiLevel { get; set; }

    /// <summary>
    /// Error code (0 = success)
    /// </summary>
    public int Err { get; set; }

    /// <summary>
    /// Whether the RepRapFirmware interface is emulated
    /// </summary>
    public bool IsEmulated { get; set; }

    /// <summary>
    /// Session timeout (in ms)
    /// </summary>
    public int SessionTimeout { get; set; }

    /// <summary>
    /// Emulated board type
    /// </summary>
    public string BoardType { get; set; } = string.Empty;
}

/// <summary>
/// Response body of GET /rr_thumbnail
/// </summary>
public sealed class ThumbnailResponse
{
    /// <summary>
    /// Name of the G-code file
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// File offset of the returned thumbnail chunk
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Base64-encoded thumbnail chunk
    /// </summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// File offset of the next thumbnail chunk or 0 if complete
    /// </summary>
    public int Next { get; set; }

    /// <summary>
    /// Error code (0 = success)
    /// </summary>
    public int Err { get; set; }
}

/// <summary>
/// Instruction for patching plugin data via PATCH /machine/plugin
/// </summary>
public sealed class PluginPatchInstruction
{
    /// <summary>
    /// Plugin to change
    /// </summary>
    public string Plugin { get; set; } = string.Empty;

    /// <summary>
    /// Key to change
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Target value
    /// </summary>
    public JsonElement Value { get; set; }
}

/// <summary>
/// Source-generated JSON context for DuetWebServer request and response bodies
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionKeyResponse))]
[JsonSerializable(typeof(RepRapFirmwareConnectResponse))]
[JsonSerializable(typeof(ThumbnailResponse))]
[JsonSerializable(typeof(PluginPatchInstruction))]
public sealed partial class DwsJsonContext : JsonSerializerContext
{
    static DwsJsonContext() => Default = new DwsJsonContext(CreateJsonSerializerOptions(Default));

    private static JsonSerializerOptions CreateJsonSerializerOptions(DwsJsonContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
