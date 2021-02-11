using DuetAPI.ObjectModel;
using DuetAPIClient;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DuetWebServer.Services
{
    /// <summary>
    /// Class used to observe the machine model
    /// </summary>
    public sealed class ModelObserver : IHostedService, IDisposable
    {
        /// <summary>
        /// Configured CORS policy for cross-origin requests
        /// </summary>
        public static readonly CorsPolicy CorsPolicy = (new CorsPolicyBuilder())
            .AllowAnyHeader()
            .AllowAnyMethod()
            .Build();

        /// <summary>
        /// Dictionary of registered third-party paths vs third-party HTTP endpoints
        /// </summary>
        public static readonly Dictionary<string, HttpEndpoint> Endpoints = new Dictionary<string, HttpEndpoint>();

        /// <summary>
        /// Dictionary holding the current user sessions in the form IP vs Id
        /// </summary>
        public static readonly Dictionary<string, int> UserSessions = new Dictionary<string, int>();

        /// <summary>
        /// Path to the web directory
        /// </summary>
        public static string WebDirectory { get; private set; }

        /// <summary>
        /// Delegate for an event that is triggered when the path of the web directory changes
        /// </summary>
        /// <param name="webDirectory">New web directory</param>
        public delegate void WebDirectoryChanged(string webDirectory);

        /// <summary>
        /// Conenction to resolve file paths
        /// </summary>
        private static CommandConnection _commandConnection;

        /// <summary>
        /// Event that is triggered whenever the web directory path changes
        /// </summary>
        public static event WebDirectoryChanged OnWebDirectoryChanged;

        /// <summary>
        /// Configuration of this application
        /// </summary>
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Check the origin of an incoming WebSocket request and set the status on error
        /// </summary>
        /// <param name="httpContext">HTTP context to check</param>
        /// <returns>True if the origin is allowed</returns>
        public static bool CheckWebSocketOrigin(HttpContext httpContext)
        {
            if (httpContext.Request.Headers.ContainsKey(CorsConstants.Origin))
            {
                if (httpContext.Request.Headers.ContainsKey(HeaderNames.Host) &&
                    Uri.TryCreate(httpContext.Request.Headers[CorsConstants.Origin], UriKind.Absolute, out Uri uri) &&
                    (string.Equals(uri.Host, httpContext.Request.Headers[HeaderNames.Host], StringComparison.InvariantCultureIgnoreCase) ||
                     string.Equals($"{uri.Host}:{uri.Port}", httpContext.Request.Headers[HeaderNames.Host], StringComparison.InvariantCultureIgnoreCase)))
                {
                    // Origin matches Host, request is legit
                    return true;
                }

                if (!CorsPolicy.Origins.Any(origin =>
                    {
                        Regex corsRegex = new Regex('^' + Regex.Escape(origin).Replace("\\*", ".*").Replace("\\?", ".") + '$', RegexOptions.IgnoreCase);
                        return corsRegex.IsMatch(httpContext.Request.Headers[CorsConstants.Origin]);
                    }))
                {
                    // Origin does not match Host (if applicable) and no CORS policy allows the specified Origin
                    httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Task representing the lifecycle of this service
        /// </summary>
        private Task _task;

        /// <summary>
        /// Cancellation token source that is triggered when the service is supposed to shut down
        /// </summary>
        private readonly CancellationTokenSource _stopRequest = new CancellationTokenSource();

        /// <summary>
        /// Constructor of this service class
        /// </summary>
        /// <param name="configuration">App configuration</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="hostAppLifetime">Host app lifetime provider</param>
        public ModelObserver(IConfiguration configuration, ILogger<ModelObserver> logger, IHostApplicationLifetime hostAppLifetime)
        {
            _logger = logger;
            _configuration = configuration;

            WebDirectory = configuration.GetValue("DefaultWebDirectory", "/opt/dsf/sd/www");
        }

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            _stopRequest.Dispose();
        }

        /// <summary>
        /// Start this service
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _task = Task.Run(Execute);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stop this service
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopRequest.Cancel();
            await _task;
        }

        /// <summary>
        /// Synchronize all registered endpoints and user sessions
        /// </summary>
        public async Task Execute()
        {
            string unixSocket = _configuration.GetValue("SocketPath", DuetAPI.Connection.Defaults.FullSocketPath);
            int retryDelay = _configuration.GetValue("ModelRetryDelay", 5000);

            ObjectModel model;
            try
            {
                do
                {
                    try
                    {
                        // Establish connections to DCS
                        using SubscribeConnection subscribeConnection = new SubscribeConnection();
                        using CommandConnection commandConnection = new CommandConnection();
                        await subscribeConnection.Connect(DuetAPI.Connection.SubscriptionMode.Patch, new string[] {
                            "directories/www",
                            "httpEndpoints/**",
                            "network/corsSite",
                            "userSessions/**"
                        }, unixSocket);
                        await commandConnection.Connect(unixSocket);
                        _logger.LogInformation("Connections to DuetControlServer established");

                        // Get the machine model and keep it up-to-date
                        model = await subscribeConnection.GetObjectModel(_stopRequest.Token);
                        if (!string.IsNullOrEmpty(model.Network.CorsSite))
                        {
                            _logger.LogInformation("Changing CORS policy to accept site '{0}'", model.Network.CorsSite);
                            CorsPolicy.Origins.Add(model.Network.CorsSite);
                        }
                        lock (Endpoints)
                        {
                            Endpoints.Clear();
                            foreach (HttpEndpoint ep in model.HttpEndpoints)
                            {
                                string fullPath = (ep.Namespace == HttpEndpoint.RepRapFirmwareNamespace) ? $"{ep.EndpointType}/rr_{ep.Path}" : $"{ep.EndpointType}/machine/{ep.Namespace}/{ep.Path}";
                                Endpoints[fullPath] = ep;
                                _logger.LogInformation("Registered HTTP endpoint {0}", fullPath);
                            }
                        }

                        // Keep track of the web directory
                        _commandConnection = commandConnection;
                        model.Directories.PropertyChanged += Directories_PropertyChanged;
                        string wwwDirectory = await commandConnection.ResolvePath(model.Directories.Web);
                        OnWebDirectoryChanged?.Invoke(wwwDirectory);

                        do
                        {
                            // Wait for more updates
                            using JsonDocument jsonPatch = await subscribeConnection.GetObjectModelPatch(_stopRequest.Token);
                            model.UpdateFromJson(jsonPatch.RootElement);

                            // Check for updated CORS site
                            if (jsonPatch.RootElement.TryGetProperty("network", out _))
                            {
                                CorsPolicy.Origins.Clear();
                                if (!string.IsNullOrEmpty(model.Network.CorsSite))
                                {
                                    _logger.LogInformation("Changing CORS policy to accept site '{0}'", model.Network.CorsSite);
                                    CorsPolicy.Origins.Add(model.Network.CorsSite);
                                }
                                else
                                {
                                    _logger.LogInformation("Reset CORS policy");
                                }
                            }

                            // Check if the HTTP sessions have changed and rebuild them on demand
                            if (jsonPatch.RootElement.TryGetProperty("httpEndpoints", out _))
                            {
                                _logger.LogInformation("New number of custom HTTP endpoints: {0}", model.HttpEndpoints.Count);

                                lock (Endpoints)
                                {
                                    Endpoints.Clear();
                                    foreach (HttpEndpoint ep in model.HttpEndpoints)
                                    {
                                        string fullPath = $"{ep.EndpointType}/machine/{ep.Namespace}/{ep.Path}";
                                        Endpoints[fullPath] = ep;
                                        _logger.LogInformation("Registered HTTP {0} endpoint via /machine/{1}/{2}", ep.EndpointType, ep.Namespace, ep.Path);
                                    }
                                }
                            }

                            // Rebuild the list of user sessions on demand
                            if (jsonPatch.RootElement.TryGetProperty("userSessions", out _))
                            {
                                lock (UserSessions)
                                {
                                    UserSessions.Clear();
                                    foreach (UserSession session in model.UserSessions)
                                    {
                                        UserSessions[session.Origin] = session.Id;
                                    }
                                }
                            }
                        }
                        while (!_stopRequest.IsCancellationRequested);
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        _logger.LogWarning(e, "Failed to synchronize machine model");
                        await Task.Delay(retryDelay, _stopRequest.Token);
                    }
                }
                while (!_stopRequest.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                // unhandled
            }
        }

        /// <summary>
        /// Handler for property changes of the Directories namespace in the machine model
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Information about the changed property</param>
        private async void Directories_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Directories.Web))
            {
                Directories directories = (Directories)sender;
                string path = await _commandConnection.ResolvePath(directories.Web);
                OnWebDirectoryChanged?.Invoke(path);
            }
        }
    }
}
