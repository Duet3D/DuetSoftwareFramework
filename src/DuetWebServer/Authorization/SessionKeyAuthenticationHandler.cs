﻿using DuetAPI.Connection;
using DuetAPIClient;
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
    public class SessionKeyAuthenticationHandler : AuthenticationHandler<SessionKeyAuthenticationSchemeOptions>
    {
        /// <summary>
        /// Name of this authentication scheme
        /// </summary>
        public const string SchemeName = "SessionKey";

        /// <summary>
        /// App configuration
        /// </summary>
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Create a new controller instance
        /// </summary>
        /// <param name="configuration">Launch configuration</param>
        /// <param name="logger">Logger instance</param>
        public SessionKeyAuthenticationHandler(IOptionsMonitor<SessionKeyAuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock, IConfiguration configuration)
            : base(options, logger, encoder, clock)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Try to authenticate a request
        /// </summary>
        /// <returns>Authentication result</returns>
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.TryGetValue("X-Session-Key", out StringValues sessionKeys))
            {
                foreach (string sessionKey in sessionKeys)
                {
                    AuthenticationTicket ticket = Sessions.GetTicket(sessionKey);
                    if (ticket != null)
                    {
                        // Got a ticket, success!
                        return AuthenticateResult.Success(ticket);
                    }
                }
            }
            else
            {
                try
                {
                    CommandConnection connection = await BuildConnection();
                    if (await connection.CheckPassword(Defaults.Password))
                    {
                        // No password set - assign an anonymous ticket
                        return AuthenticateResult.Success(Sessions.AnonymousTicket);
                    }
                }
                catch
                {
                    // Failed to check default password...
                    return AuthenticateResult.NoResult();
                }
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
            await connection.Connect(_configuration.GetValue("SocketPath", Defaults.FullSocketPath));
            return connection;
        }
    }
}