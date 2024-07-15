using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetWebServer.Singletons;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace DuetWebServer.Authorization
{
    /// <summary>
    /// Options for session key based authentication handlers
    /// </summary>
    public class SessionKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions { }

    /// <summary>
    /// Authentication handler for session keys
    /// </summary>
    /// <remarks>
    /// Create a new controller instance
    /// </remarks>
    /// <param name="options">Options</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="encoder">URL encoder</param>
    /// <param name="configuration">Launch configuration</param>
    /// <param name="sessionStorage">Session storage singleton</param>
    public class SessionKeyAuthenticationHandler(IOptionsMonitor<SessionKeyAuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration, ISessionStorage sessionStorage) : AuthenticationHandler<SessionKeyAuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>
        /// Name of this authentication scheme
        /// </summary>
        public const string SchemeName = "SessionKey";

        /// <summary>
        /// App settings
        /// </summary>
        private readonly Settings _settings = configuration.Get<Settings>() ?? new();

        /// <summary>
        /// Try to authenticate a request
        /// </summary>
        /// <returns>Authentication result</returns>
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.TryGetValue("X-Session-Key", out StringValues sessionKeys))
            {
                foreach (string? sessionKey in sessionKeys)
                {
                    if (sessionKey is not null)
                    {
                        AuthenticationTicket? ticket = sessionStorage.GetTicketFromKey(sessionKey);
                        if (ticket is not null)
                        {
                            // Got a ticket, success!
                            return AuthenticateResult.Success(ticket);
                        }
                    }
                }
            }
            else
            {
                // Check for IP address authorization
                string ipAddress = Context.Connection.RemoteIpAddress!.ToString();
                AuthenticationTicket? ticket = sessionStorage.GetTicketFromIpAddress(ipAddress);

                // Make a new session if no ticket could be found and no password is set
                if (ticket is null)
                {
                    try
                    {
                        using CommandConnection connection = await BuildConnection();
                        if (await connection.CheckPassword(Defaults.Password))
                        {
                            // No password set - assign a new ticket so that replies are saved
                            int sessionId = await connection.AddUserSession(AccessLevel.ReadWrite, SessionType.HTTP, ipAddress);
                            ticket = sessionStorage.MakeSessionTicket(sessionId, ipAddress, true);
                        }
                    }
                    catch
                    {
                        // Failed to check default password...
                        return AuthenticateResult.NoResult();
                    }
                }

                // Deal with the result
                return (ticket is not null) ? AuthenticateResult.Success(ticket) : AuthenticateResult.NoResult();
            }
            return AuthenticateResult.Fail("Missing X-Session-Key header");
        }

        /// <summary>
        /// Build a new command connection to DCS
        /// </summary>
        /// <returns>Command connection</returns>
        private async Task<CommandConnection> BuildConnection()
        {
            CommandConnection connection = new();
            await connection.Connect(_settings.SocketPath);
            return connection;
        }
    }
}
