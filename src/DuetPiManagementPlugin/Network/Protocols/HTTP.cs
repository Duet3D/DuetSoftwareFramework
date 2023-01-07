using DuetAPI.ObjectModel;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin.Network.Protocols
{
    /// <summary>
    /// Protocol management for HTTP/HTTPS
    /// </summary>
    public static class HTTP
    {
        /// <summary>
        /// Configured HTTP port
        /// </summary>
        private static int _httpPort = 80;

        /// <summary>
        /// Configured HTTPS port
        /// </summary>
        private static int _httpsPort = 443;

        /// <summary>
        /// Internal representation of the ASP.NET JSON config
        /// </summary>
        private sealed class AspNetConfig
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

        /// <summary>
        /// Initialize the protocol configuration
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Init()
        {
            if (File.Exists("/opt/dsf/conf/http.json"))
            {
                AspNetConfig config;
                await using (FileStream configStream = new("/opt/dsf/conf/http.json", FileMode.Open, FileAccess.Read))
                {
                    config = (await JsonSerializer.DeserializeAsync<AspNetConfig>(configStream, cancellationToken: Program.CancellationToken))!;
                }

                bool dwsEnabled = await Command.ExecQuery("systemctl", "is-enabled -q duetwebserver.service");
                if (config?.Kestrel?.Endpoints?.Http?.Url is not null && Uri.TryCreate(config.Kestrel.Endpoints.Http.Url.Replace('*', 'x'), UriKind.Absolute, out Uri? url))
                {
                    _httpPort = url.Port;
                    if (dwsEnabled)
                    {
                        await Manager.SetProtocol(NetworkProtocol.HTTP, true);
                    }
                }
                if (config?.Kestrel?.Endpoints?.Https?.Url is not null && Uri.TryCreate(config.Kestrel.Endpoints.Https.Url.Replace('*', 'x'), UriKind.Absolute, out url))
                {
                    _httpsPort = url.Port;
                    if (dwsEnabled)
                    {
                        await Manager.SetProtocol(NetworkProtocol.HTTPS, true);
                    }
                }
            }
        }

        /// <summary>
        /// Configure the HTTP(S) server
        /// </summary>
        /// <param name="enabled">Enable HTTP(S)</param>
        /// <param name="port">Port</param>
        /// <param name="secure">Configure HTTPS</param>
        /// <returns>Configuration result</returns>
        public static async Task<Message> Configure(bool? enabled, int port, bool secure)
        {
            // If we need to enable HTTPS, weed a valid certificate first...
            if (secure && !File.Exists("/opt/dsf/conf/https.pfx"))
            {
                using Process genCertProcess = Process.Start(Path.Combine(Directory.GetCurrentDirectory(), "gen-https-cert.sh"));
                await genCertProcess.WaitForExitAsync(Program.CancellationToken);
            }

            // Check whether HTTP(S) should be enabled or not
            bool useHTTP = Manager.IsEnabled(NetworkProtocol.HTTP);
            bool useHTTPS = Manager.IsEnabled(NetworkProtocol.HTTPS);
            bool enableService = false, disableService = false;
            if (secure)
            {
                if (enabled is not null && enabled != useHTTPS)
                {
                    enableService = !useHTTP && !useHTTPS && enabled.Value;
                    disableService = !useHTTP && useHTTPS && !enabled.Value;
                    useHTTPS = enabled.Value;
                    await Manager.SetProtocol(NetworkProtocol.HTTPS, useHTTPS);
                }
                else if (port <= 0 || port == _httpsPort)
                {
                    // No changes requested, don't do anything
                    return new Message();
                }
            }
            else
            {
                if (enabled is not null && enabled != useHTTP)
                {
                    enableService = !useHTTP && !useHTTPS && enabled.Value;
                    disableService = useHTTP && !useHTTPS && !enabled.Value;
                    useHTTP = enabled.Value;
                    await Manager.SetProtocol(NetworkProtocol.HTTP, useHTTP);
                }
                else if (port <= 0 || port == _httpPort)
                {
                    // No changes requested, don't do anything
                    return new Message();
                }
            }

            // Do we need to disable DWS?
            if (disableService)
            {
                string stopOutput = await Command.Execute("systemctl", "stop duetwebserver.service");
                string disableOutput = await Command.Execute("systemctl", "disable duetwebserver.service");
                return new Message(MessageType.Success, string.Join('\n', stopOutput, disableOutput).Trim());
            }

            // Choose the config template to use
            string templateFile;
            if (useHTTP && useHTTPS)
            {
                templateFile = Path.Combine(Directory.GetCurrentDirectory(), "http-mixed.json");
            }
            else if (useHTTP)
            {
                templateFile = Path.Combine(Directory.GetCurrentDirectory(), "http-simple.json");
            }
            else
            {
                templateFile = Path.Combine(Directory.GetCurrentDirectory(), "http-secure.json");
            }

            // Copy the template file and replace the port variables
            await using (FileStream templateStream = new(templateFile, FileMode.Open, FileAccess.Read))
            {
                using StreamReader reader = new(templateStream);
                await using FileStream configStream = new("/opt/dsf/conf/http.json", FileMode.Create, FileAccess.Write);
                await using StreamWriter writer = new(configStream);

                while (!reader.EndOfStream)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line is null)
                    {
                        break;
                    }

                    line = line.Replace("{httpPort}", _httpPort.ToString());
                    line = line.Replace("{httpsPort}", _httpsPort.ToString());
                    await writer.WriteLineAsync(line);
                }
            }

            // Enable the service if it was disabled before
            if (enableService)
            {
                string enableOutput = await Command.Execute("systemctl", "enable -q duetwebserver.service");
                string startOutput = await Command.Execute("systemctl", "start duetwebserver.service");
                return new Message(MessageType.Success, string.Join('\n', enableOutput, startOutput).Trim());
            }

            // Restart the service
            string restartOutput = await Command.Execute("systemctl", "restart duetwebserver.service");
            return new Message(MessageType.Success, restartOutput.TrimEnd());
        }

        /// <summary>
        /// Report the current state of the HTTP and HTTPS protocols
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Report(StringBuilder builder)
        {
            // HTTP
            if (Manager.IsEnabled(NetworkProtocol.HTTP))
            {
                builder.AppendLine($"HTTP is enabled on port {_httpPort}");
            }
            else
            {
                builder.AppendLine("HTTP is disabled");
            }

            // HTTPS
            if (Manager.IsEnabled(NetworkProtocol.HTTPS))
            {
                builder.AppendLine($"HTTPS is enabled on port {_httpsPort}");
            }
            else
            {
                builder.AppendLine("HTTPS is disabled");
            }

            // CORS
            ObjectModel model = await Program.Connection.GetObjectModel(Program.CancellationToken);
            if (string.IsNullOrEmpty(model.Network.CorsSite))
            {
                builder.AppendLine("CORS disabled");
            }
            else
            {
                builder.AppendLine($"CORS enabled for site '{model.Network.CorsSite}'");
            }
        }
    }
}
