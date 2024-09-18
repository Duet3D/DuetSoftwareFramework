using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetPiManagementPlugin
{
    [JsonSerializable(typeof(Network.Protocols.AspNetConfig))]
    [JsonSerializable(typeof(Network.WifiScanResult))]
    [JsonSourceGenerationOptions(PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    public partial class JsonContext : JsonSerializerContext
    {
        static JsonContext() => Default = new JsonContext(CreateJsonSerializerOptions(Default));

        private static JsonSerializerOptions CreateJsonSerializerOptions(JsonContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}