using DuetWebServer.Singletons;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DuetWebServer.Services;

/// <summary>
/// Service to automatically remove expired sessions
/// </summary>
/// <param name="settings">Application settings</param>
/// <param name="sessionStorage">Session storage</param>
public class SessionExpiry(IOptionsMonitor<Settings> settings, ISessionStorage sessionStorage) : BackgroundService
{
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
                Settings currentSettings = settings.CurrentValue;
                sessionStorage.MaintainSessions(TimeSpan.FromMilliseconds(currentSettings.SessionTimeout), currentSettings.SocketPath);
                await Task.Delay(1000, cancellationToken);
            }
            while (!cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }
}
