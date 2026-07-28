using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetControlServer;

/// <summary>
/// Serialization context for the settings file
/// </summary>
/// <remarks>
/// No naming policy is applied because the settings file uses the property names as they are declared
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
public sealed partial class SettingsContext : JsonSerializerContext
{
    static SettingsContext() => Default = new SettingsContext(CreateJsonSerializerOptions(Default));

    private static JsonSerializerOptions CreateJsonSerializerOptions(SettingsContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
