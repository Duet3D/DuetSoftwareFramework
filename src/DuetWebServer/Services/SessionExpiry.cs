using DuetWebServer.Singletons;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetWebServer.Services
{
    /// <summary>
    /// Service to automatically remove expired sessions
    /// </summary>
    public class SessionExpiry : BackgroundService
    {
        /// <summary>
        /// App settings
        /// </summary>
        private readonly Settings _settings;

        /// <summary>
        /// Session storage singleton
        /// </summary>
        private readonly ISessionStorage _sessionStorage;

        /// <summary>
        /// Constructor of this service class
        /// </summary>
        /// <param name="configuration">App configuration</param>
        /// <param name="sessionStorage">Session storage</param>
        public SessionExpiry(IConfiguration configuration, ISessionStorage sessionStorage)
        {
            _settings = configuration.Get<Settings>();
            _sessionStorage = sessionStorage;
        }

        /// <summary>
        /// Maintain active HTTP sessions once per second
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            try
            {
                do
                {
                    _sessionStorage.MaintainSessions(TimeSpan.FromMilliseconds(_settings.SessionTimeout), _settings.SocketPath);
                    await Task.Delay(1000, cancellationToken);
                }
                while (!cancellationToken.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                // unhandled
            }
        }
    }
}
