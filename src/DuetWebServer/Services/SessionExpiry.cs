using DuetAPI.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetWebServer.Services
{
    /// <summary>
    /// Service to automatically remove expired sessions
    /// </summary>
    public class SessionExpiry : IHostedService, IDisposable
    {
        /// Configuration of this application
        /// </summary>
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Task representing the lifecycle of this service
        /// </summary>
        private Task _task;

        /// <summary>
        /// Cancellation token source that is triggered when the service is supposed to shut down
        /// </summary>
        private readonly CancellationTokenSource _stopRequest = new();

        /// <summary>
        /// Constructor of this service class
        /// </summary>
        /// <param name="configuration">App configuration</param>
        /// <param name="logFactory">Log factory</param>
        public SessionExpiry(IConfiguration configuration, ILoggerFactory logFactory)
        {
            _configuration = configuration;
            Authorization.Sessions.SetLogFactory(logFactory);
        }

        /// <summary>
        /// Dispose this instance
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _stopRequest.Dispose();
        }

        /// <summary>
        /// Start this service
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _task = Task.Run(Execute, cancellationToken);
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
        /// Maintain active HTTP sessions once per second
        /// </summary>
        public async Task Execute()
        {
            TimeSpan sessionTimeout = TimeSpan.FromMilliseconds(_configuration.GetValue("SessionTimeout", 8000));
            try
            {
                do
                {
                    Authorization.Sessions.MaintainSessions(sessionTimeout, _configuration.GetValue("SocketPath", Defaults.FullSocketPath));
                    await Task.Delay(1000, _stopRequest.Token);
                }
                while (!_stopRequest.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                // unhandled
            }
        }
    }
}
