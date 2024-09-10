using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System;
using System.ComponentModel;
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
    /// <param name="configuration">App configuration</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="modelProvider">Model provider singleton</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    public sealed class ModelObserver(IConfiguration configuration, ILogger<ModelObserver> logger, IModelProvider modelProvider, ISessionStorage sessionStorage) : BackgroundService
    {
        /// <summary>
        /// Configured CORS policy for cross-origin requests
        /// </summary>
        public static readonly CorsPolicy CorsPolicy = new CorsPolicyBuilder()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .Build();

        /// <summary>
        /// Connection to resolve file paths
        /// </summary>
        private static CommandConnection? _commandConnection;

        /// <summary>
        /// App settings
        /// </summary>
        private readonly Settings _settings = configuration.Get<Settings>() ?? new();

        /// <summary>
        /// Check the origin of an incoming WebSocket request and set the status on error
        /// </summary>
        /// <param name="httpContext">HTTP context to check</param>
        /// <returns>True if the origin is allowed</returns>
        public static bool CheckWebSocketOrigin(HttpContext httpContext)
        {
            if (httpContext.Request.Headers.ContainsKey(CorsConstants.Origin))
            {
                if (httpContext.Request.Headers.TryGetValue(HeaderNames.Host, out StringValues hostValue) &&
                    Uri.TryCreate(httpContext.Request.Headers[CorsConstants.Origin], UriKind.Absolute, out Uri? uri) &&
                    (string.Equals(uri.Host, hostValue, StringComparison.InvariantCultureIgnoreCase) ||
                     string.Equals($"{uri.Host}:{uri.Port}", hostValue, StringComparison.InvariantCultureIgnoreCase)))
                {
                    // Origin matches Host, request is legit
                    return true;
                }

                if (!CorsPolicy.Origins.Any(origin =>
                    {
                        Regex corsRegex = new('^' + Regex.Escape(origin).Replace("\\*", ".*").Replace("\\?", ".") + '$', RegexOptions.IgnoreCase);
                        return corsRegex.IsMatch(httpContext.Request.Headers[CorsConstants.Origin]!);
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
        /// Synchronize all registered endpoints and user sessions
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            ObjectModel model;
            try
            {
                do
                {
                    try
                    {
                        // Establish connections to DCS
                        using SubscribeConnection subscribeConnection = new();
                        using CommandConnection commandConnection = new();
                        await subscribeConnection.Connect(DuetAPI.Connection.SubscriptionMode.Patch, [
                            "directories/www",
                            "messages/**",
                            "network/corsSite",
                            "sbc/dsf/httpEndpoints/**"
                        ], _settings.SocketPath, cancellationToken);
                        await commandConnection.Connect(_settings.SocketPath, cancellationToken);
                        logger.LogInformation("Connections to DuetControlServer established");

                        // Get the machine model and keep it up-to-date
                        model = await subscribeConnection.GetObjectModel(cancellationToken);

                        // Initialize CORS site
                        if (!string.IsNullOrEmpty(model.Network.CorsSite))
                        {
                            logger.LogInformation("Changing CORS policy to accept site '{corsSite}'", model.Network.CorsSite);
                            CorsPolicy.Origins.Add(model.Network.CorsSite);
                        }

                        // Initialize HTTP endpoints
                        lock (modelProvider.Endpoints)
                        {
                            modelProvider.Endpoints.Clear();
                            foreach (HttpEndpoint ep in model.SBC!.DSF.HttpEndpoints)
                            {
                                string fullPath = (ep.Namespace == HttpEndpoint.RepRapFirmwareNamespace) ? $"{ep.EndpointType}/rr_{ep.Path}" : $"{ep.EndpointType}/machine/{ep.Namespace}/{ep.Path}";
                                modelProvider.Endpoints[fullPath] = ep;
                                logger.LogInformation("Registered HTTP endpoint {endpoint}", fullPath);
                            }
                        }

                        // Keep track of the web directory
                        _commandConnection = commandConnection;
                        model.Directories.PropertyChanged += Directories_PropertyChanged;
                        modelProvider.WebDirectory = await commandConnection.ResolvePath(model.Directories.Web, cancellationToken);

                        do
                        {
                            // Cache incoming messages
                            foreach (Message message in model.Messages)
                            {
                                sessionStorage.CacheMessage(message.ToString());
                            }
                            model.Messages.Clear();

                            // Wait for more updates
                            using JsonDocument jsonPatch = await subscribeConnection.GetObjectModelPatch(cancellationToken);
                            model.UpdateFromJson(jsonPatch.RootElement, false);

                            // Increment sequence numbers
                            if (jsonPatch.RootElement.TryGetProperty("messages", out JsonElement messagesElement))
                            {
                                lock (modelProvider)
                                {
                                    modelProvider.ReplySeq += messagesElement.GetArrayLength();
                                }
                            }

                            // Check for updated CORS site
                            if (jsonPatch.RootElement.TryGetProperty("network", out JsonElement networkElement) && networkElement.TryGetProperty("corsSite", out _))
                            {
                                CorsPolicy.Origins.Clear();
                                if (!string.IsNullOrEmpty(model.Network.CorsSite))
                                {
                                    logger.LogInformation("Changing CORS policy to accept site '{corsSite}'", model.Network.CorsSite);
                                    CorsPolicy.Origins.Add(model.Network.CorsSite);
                                }
                                else
                                {
                                    logger.LogInformation("Reset CORS policy");
                                }
                            }

                            // Check if the HTTP sessions have changed and rebuild them on demand
                            if (jsonPatch.RootElement.TryGetProperty("sbc", out _))
                            {
                                logger.LogInformation("New number of custom HTTP endpoints: {numEndpoints}", model.SBC!.DSF.HttpEndpoints.Count);

                                lock (modelProvider.Endpoints)
                                {
                                    modelProvider.Endpoints.Clear();
                                    foreach (HttpEndpoint ep in model.SBC!.DSF.HttpEndpoints)
                                    {
                                        string fullPath = $"{ep.EndpointType}/machine/{ep.Namespace}/{ep.Path}";
                                        modelProvider.Endpoints[fullPath] = ep;
                                        logger.LogInformation("Registered HTTP {endpoint} endpoint via /machine/{namespace}/{path}", ep.EndpointType, ep.Namespace, ep.Path);
                                    }
                                }
                            }
                        }
                        while (!cancellationToken.IsCancellationRequested);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning(e, "Failed to synchronize machine model");
                        await Task.Delay(_settings.ModelRetryDelay, cancellationToken);
                    }
                }
                while (!cancellationToken.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                // suppressed
            }
        }

        /// <summary>
        /// Handler for property changes of the Directories namespace in the machine model
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Information about the changed property</param>
        private async void Directories_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Directories.Web))
            {
                Directories directories = (Directories)sender!;
                modelProvider.WebDirectory = await _commandConnection!.ResolvePath(directories.Web);
            }
        }
    }
}
