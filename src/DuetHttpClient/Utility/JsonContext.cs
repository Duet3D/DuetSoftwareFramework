using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetHttpClient.Utility
{
    /// <summary>
    /// Context for JSON handling of the main object model classes
    /// </summary>
    [JsonSerializable(typeof(Connector.Responses.PollConnectResponse))]
    [JsonSerializable(typeof(Connector.Responses.ErrResponse))]
    [JsonSerializable(typeof(Connector.Responses.FileListResponse))]
    [JsonSerializable(typeof(Connector.Responses.FileNode[]))]
    [JsonSerializable(typeof(Connector.Responses.GcodeReply))]
    [JsonSerializable(typeof(Connector.Responses.RestConnectResponse))]
    [JsonSourceGenerationOptions(PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal sealed partial class JsonContext : JsonSerializerContext
    {
        static JsonContext() => Default = new JsonContext(CreateJsonSerializerOptions(Default));

        private static JsonSerializerOptions CreateJsonSerializerOptions(JsonContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}