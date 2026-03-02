using System.Text.Json.Serialization;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Internal representation of the ASP.NET JSON config
    /// </summary>
    public sealed class AspNetConfig
    {
        public sealed class KestrelConfig
        {
            public sealed class EndpointsConfig
            {
                public sealed class HttpConfig
                {
                    [JsonPropertyName("Url")]
                    public string? Url { get; set; }
                }

                [JsonPropertyName("Http")]
                public HttpConfig Http { get; set; } = new HttpConfig();

                [JsonPropertyName("Https")]
                public HttpConfig Https { get; set; } = new HttpConfig();
            }

            [JsonPropertyName("Endpoints")]
            public EndpointsConfig Endpoints { get; set; } = new EndpointsConfig();
        }

        [JsonPropertyName("Kestrel")]
        public KestrelConfig Kestrel { get; set; } = new KestrelConfig();
    }
}
