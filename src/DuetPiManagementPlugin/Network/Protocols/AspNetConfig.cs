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
                    public string? Url { get; set; }
                }

                public HttpConfig Http { get; set; } = new HttpConfig();
                public HttpConfig Https { get; set; } = new HttpConfig();
            }
            public EndpointsConfig Endpoints { get; set; } = new EndpointsConfig();
        }
        public KestrelConfig Kestrel { get; set; } = new KestrelConfig();
    }
}
