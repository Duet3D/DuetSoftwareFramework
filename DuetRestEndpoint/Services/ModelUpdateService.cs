using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetRestEndpoint.Services
{
    /// <summary>
    /// Service worker that keeps the internal JSON-based machine model up-to-date.
    /// Note that this service does not - at any point - deserialize the object model.
    /// This is due to performance reasons.
    /// </summary>
    public class ModelUpdateService : BackgroundService
    {
        /// <summary>
        /// Path to the UNIX socket. This is assigned by the Startup class
        /// </summary>
        public static string SocketPath;

        private readonly ILogger _logger;
        private readonly DuetAPIClient.Connection _connection = new DuetAPIClient.Connection(DuetAPI.Connection.ConnectionType.Subscribe);

        /// <summary>
        /// Create a new instance of the model background service
        /// </summary>
        /// <param name="logger">Logger</param>
        public ModelUpdateService(ILogger<ModelUpdateService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Start the model service
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Base task</returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting model subscription...");
            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// Represents the full lifecycle of this service
        /// </summary>
        /// <param name="cancellationToken">Invoked when the service is being terminated</param>
        /// <returns>Service task</returns>
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            do
            {
                // Attempt to connect to the main service
                try
                {
                    ModelProvider.IsConnected = false;
                    await _connection.Connect( SocketPath, cancellationToken);
                    ModelProvider.IsConnected = true;
                    _logger.LogInformation("Connected to DCS");
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {
                        _logger.LogError($"Could not connect to DCS: {e.Message}");
                        await Task.Delay(3000, cancellationToken);
                    }
                }

                // Keep reading updates
                while (_connection.IsConnected)
                {
                    try
                    {
                        // Receive the full object model
                        JObject model = await _connection.GetMachineModelPatch(cancellationToken);
                        ModelProvider.Set(model);

                        // Keep reading updates from the socket
                        do
                        {
                            JObject diff = await _connection.GetMachineModelPatch(cancellationToken);
                            ModelProvider.Update(diff);
                        }
                        while (!cancellationToken.IsCancellationRequested);
                    }
                    catch (Exception e)
                    {
                        if (!(e is OperationCanceledException))
                        {
                            _logger.LogWarning($"Failed to read data from DCS: {e.Message}");
                            await Task.Delay(3000, cancellationToken);
                        }
                    }
                }
            }
            while (!cancellationToken.IsCancellationRequested);

            // Close the connection again before the service is terminated
            if (_connection.IsConnected)
            {
                _connection.Close();
            }
        }

        /// <summary>
        /// Stop the model service
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Base task</returns>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping model subscription...");
            return base.StopAsync(cancellationToken);
        }
    }
}
