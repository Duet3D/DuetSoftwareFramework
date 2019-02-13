using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Commands
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum InterceptionType
    {
        Pre,
        Post
    }

    // Enter interception mode
    public class Intercept : EmptyResponseCommand
    {
        public InterceptionType Type { get; set; }
    }
}