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
    /// <param name="configuration">App configuration</param>
    /// <param name="sessionStorage">Session storage</param>
    public class SessionExpiry(IConfiguration configuration, ISessionStorage sessionStorage) : BackgroundService
    {
        /// <summary>
        /// App settings
        /// </summary>
        private readonly Settings _settings = configuration.Get<Settings>() ?? new();

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
                    sessionStorage.MaintainSessions(TimeSpan.FromMilliseconds(_settings.SessionTimeout), _settings.SocketPath);
                    await Task.Delay(1000, cancellationToken);
                }
                while (!cancellationToken.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                // suppressed
            }
        }
    }
}
