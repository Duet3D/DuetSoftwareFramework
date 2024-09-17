using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Context for JSON handling of the main object model classes
    /// </summary>
    [JsonSerializable(typeof(ObjectModel))]
    [JsonSourceGenerationOptions(PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    public sealed partial class ObjectModelContext : JsonSerializerContext
    {
        static ObjectModelContext() => Default = new ObjectModelContext(CreateJsonSerializerOptions(Default));

        private static JsonSerializerOptions CreateJsonSerializerOptions(ObjectModelContext defaultContext) => new(defaultContext.GeneratedSerializerOptions!)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}