using DuetAPI.Connection.InitMessages;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Context for JSON handling of connection classes
    /// </summary>
    [JsonSerializable(typeof(CodeStreamInitMessage))]
    [JsonSerializable(typeof(CommandInitMessage))]
    [JsonSerializable(typeof(InterceptInitMessage))]
    [JsonSerializable(typeof(PluginServiceInitMessage))]
    [JsonSerializable(typeof(ServerInitMessage))]
    [JsonSerializable(typeof(SubscribeInitMessage))]
    [JsonSourceGenerationOptions(DictionaryKeyPolicy = JsonKnownNamingPolicy.CamelCase, PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    public sealed partial class ConnectionContext : JsonSerializerContext
    {
        static ConnectionContext() => Default = new ConnectionContext(CreateJsonSerializerOptions(Default));

        private static JsonSerializerOptions CreateJsonSerializerOptions(ConnectionContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
