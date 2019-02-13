using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Commands
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SubscriptionType
    {
        Patch,
        Full
    }

    // Enter Subscription mode and receive the full object model
    // If Patch is chosen, the client will only receive the updates since the last update.
    // If Full is chosen, the full object model is pushed over after every update.
    public class Subscribe : EmptyResponseCommand
    {
        public SubscriptionType Type { get; set; }
    }
}