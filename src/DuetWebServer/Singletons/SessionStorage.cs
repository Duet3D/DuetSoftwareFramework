using DuetAPIClient;
using DuetWebServer.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace DuetWebServer.Singletons
{
    /// <summary>
    /// Interface for accessing the session storage singleton
    /// </summary>
    public interface ISessionStorage
    {
        /// <summary>
        /// Check if the given session key provides the requested access to the given policy
        /// </summary>
        /// <param name="key">Session key</param>
        /// <param name="readWrite">If readWrite or readOnly policy is requested</param>
        /// <returns>True if access is granted</returns>
        public bool CheckSessionKey(string key, bool readWrite);

        /// <summary>
        /// Make a new session key and register it if the session ID is valid
        /// </summary>
        /// <param name="sessionId">DSF session ID</param>
        /// <param name="ipAddress">Optional IP address to store for IP address-based authentification</param>
        /// <param name="readWrite">Whether the client has read-write or read-only access</param>
        /// <returns>Authentication ticket</returns>
        public string MakeSessionKey(int sessionId, string ipAddress, bool readWrite);

        /// <summary>
        /// Make a new session ticket and register it if the session ID is valid
        /// </summary>
        /// <param name="sessionId">DSF session ID</param>
        /// <param name="ipAddress">Optional IP address to store for IP address-based authentification</param>
        /// <param name="readWrite">Whether the client has read-write or read-only access</param>
        /// <returns>Authentication ticket</returns>
        public AuthenticationTicket MakeSessionTicket(int sessionId, string ipAddress, bool readWrite);

        /// <summary>
        /// Get a session ID from the given key
        /// </summary>
        /// <param name="key">Key to query</param>
        /// <returns>Session ID or -1</returns>
        public int GetSessionId(string key);

        /// <summary>
        /// Get a ticket from the given key
        /// </summary>
        /// <param name="key">Key to query</param>
        /// <returns>Authentication ticket or null</returns>
        public AuthenticationTicket? GetTicketFromKey(string key);

        /// <summary>
        /// Get a ticket from the given IP address
        /// </summary>
        /// <param name="ipAddress">IP address to query</param>
        /// <returns>Authentication ticket or null</returns>
        public AuthenticationTicket? GetTicketFromIpAddress(string ipAddress);

        /// <summary>
        /// Remove a session ticket returning the corresponding session ID
        /// </summary>
        /// <returns>Session ID or 0 if none was found</returns>
        public int RemoveTicket(ClaimsPrincipal user);

        /// <summary>
        /// Set whether a given socket is connected over WebSocket
        /// </summary>
        /// <param name="key">Session key</param>
        /// <param name="webSocketConnected">Whether a WebSocket is connected</param>
        public void SetWebSocketState(string key, bool webSocketConnected);

        /// <summary>
        /// Set whether a potentially long-running HTTP request has started or finished
        /// </summary>
        /// <param name="user">Principal user</param>
        /// <param name="requestStarted">Whether a WebSocket is connected</param>
        public void SetLongRunningHttpRequest(ClaimsPrincipal user, bool requestStarted);

        /// <summary>
        /// Remove sessions that are no longer active
        /// </summary>
        /// <param name="sessionTimeout">Timeout for HTTP sessions</param>
        /// <param name="socketPath">API socket path</param>
        public void MaintainSessions(TimeSpan sessionTimeout, string socketPath);

        /// <summary>
        /// Cache an incoming generic message
        /// </summary>
        /// <param name="message">Message to cache</param>
        public void CacheMessage(string message);

        /// <summary>
        /// Retrieve the cached messages of a given user
        /// </summary>
        /// <returns>Cached messages</returns>
        public string GetCachedMessages(ClaimsPrincipal user);
    }

    /// <summary>
    /// Storage singleton for internal HTTP sessions
    /// </summary>
    public class SessionStorage : ISessionStorage
    {
        /// <summary>
        /// Internal logger instance
        /// </summary>
        private readonly ILogger<SessionStorage> _logger;

        /// <summary>
        /// Constructor of the session storage singleton
        /// </summary>
        /// <param name="logger">Logger instance</param>
        public SessionStorage(ILogger<SessionStorage> logger) => _logger = logger;

        /// <summary>
        /// Internal session wrapper around auth tickets
        /// </summary>
        private sealed class Session
        {
            public AuthenticationTicket Ticket { get; }

            public string Key => Ticket.Principal.FindFirst("key")!.Value;

            public int SessionId => Convert.ToInt32(Ticket.Principal.FindFirst("sessionId")!.Value);

            public string IpAddress => Ticket.Principal.FindFirst("ipAddress")!.Value;

            public DateTime LastQueryTime { get; set; } = DateTime.Now;

            public int NumWebSocketsConnected { get; set; }

            public int NumRunningRequests { get; set; }

            private readonly StringBuilder _cachedMessages = new();

            public void CacheMessage(string message)
            {
                if (!string.IsNullOrEmpty(IpAddress))
                {
                    lock (_cachedMessages)
                    {
                        _cachedMessages.AppendLine(message.TrimEnd());
                    }
                }
            }

            public string GetCachedMessages()
            {
                string result;
                lock (_cachedMessages)
                {
                    result = _cachedMessages.ToString();
                    _cachedMessages.Clear();
                }
                return result;
            }

            public Session(AuthenticationTicket ticket) => Ticket = ticket;
        }

        /// <summary>
        /// List of active sessions
        /// </summary>
        private readonly List<Session> _sessions = new();

        /// <summary>
        /// Check if the given session key provides the requested access to the given policy
        /// </summary>
        /// <param name="key">Session key</param>
        /// <param name="readWrite">If readWrite or readOnly policy is requested</param>
        /// <returns>True if access is granted</returns>
        public bool CheckSessionKey(string key, bool readWrite)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Key == key)
                    {
                        string? access = item.Ticket.Principal.FindFirstValue("access");
                        if ((readWrite && access == Policies.ReadWrite) || (!readWrite && (access == Policies.ReadOnly || access == Policies.ReadWrite)))
                        {
                            item.LastQueryTime = DateTime.Now;
                            return true;
                        }
                        return false;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Make a new session key and register it if the session ID is valid
        /// </summary>
        /// <param name="sessionId">DSF session ID</param>
        /// <param name="ipAddress">Optional IP address if the request came from a RRF HTTP request</param>
        /// <param name="readWrite">Whether the client has read-write or read-only access</param>
        /// <returns>Authentication ticket</returns>
        public string MakeSessionKey(int sessionId, string ipAddress, bool readWrite)
        {
            lock (_sessions)
            {
                string sessionKey = Guid.NewGuid().ToString("N");
                ClaimsIdentity identity = new(new[] {
                    new Claim("access", readWrite ? Policies.ReadWrite : Policies.ReadOnly),
                    new Claim("key", sessionKey),
                    new Claim("sessionId", sessionId.ToString()),
                    new Claim("ipAddress", ipAddress)
                }, nameof(SessionKeyAuthenticationHandler));
                AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), SessionKeyAuthenticationHandler.SchemeName);
                if (sessionId > 0)
                {
                    _sessions.Add(new(ticket));
                    _logger?.LogInformation("Session {0} added ({1})", sessionKey, readWrite ? "readWrite" : "readOnly");
                }
                return sessionKey;
            }
        }

        /// <summary>
        /// Make a new session ticket and register it if the session ID is valid
        /// </summary>
        /// <param name="sessionId">DSF session ID</param>
        /// <param name="ipAddress">Optional IP address if the request came from a RRF HTTP request</param>
        /// <param name="readWrite">Whether the client has read-write or read-only access</param>
        /// <returns>Authentication ticket</returns>
        public AuthenticationTicket MakeSessionTicket(int sessionId, string ipAddress, bool readWrite)
        {
            lock (_sessions)
            {
                string sessionKey = Guid.NewGuid().ToString("N");
                ClaimsIdentity identity = new(new[] {
                    new Claim("access", readWrite ? Policies.ReadWrite : Policies.ReadOnly),
                    new Claim("key", sessionKey),
                    new Claim("sessionId", sessionId.ToString()),
                    new Claim("ipAddress", ipAddress)
                }, nameof(SessionKeyAuthenticationHandler));
                AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), SessionKeyAuthenticationHandler.SchemeName);
                if (sessionId > 0)
                {
                    _sessions.Add(new(ticket));
                    _logger?.LogInformation("Session {0} added ({1})", sessionKey, readWrite ? "readWrite" : "readOnly");
                }
                return ticket;
            }
        }

        /// <summary>
        /// Get a session ID from the given key
        /// </summary>
        /// <param name="key">Key to query</param>
        /// <returns>Session ID or -1</returns>
        public int GetSessionId(string key)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Key == key)
                    {
                        item.LastQueryTime = DateTime.Now;
                        return item.SessionId;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// Get a ticket from the given key
        /// </summary>
        /// <param name="key">Key to query</param>
        /// <returns>Authentication ticket or null</returns>
        public AuthenticationTicket? GetTicketFromKey(string key)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Key == key)
                    {
                        item.LastQueryTime = DateTime.Now;
                        return item.Ticket;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get a ticket from the given IP address
        /// </summary>
        /// <param name="ipAddress">IP address to query</param>
        /// <returns>Authentication ticket or null</returns>
        public AuthenticationTicket? GetTicketFromIpAddress(string ipAddress)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.IpAddress == ipAddress)
                    {
                        item.LastQueryTime = DateTime.Now;
                        return item.Ticket;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Remove a session ticket returning the corresponding session ID
        /// </summary>
        /// <returns>Session ID or 0 if none was found</returns>
        public int RemoveTicket(ClaimsPrincipal user)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Ticket.Principal == user)
                    {
                        _logger?.LogInformation("Session {0} removed", user.FindFirstValue("key"));
                        _sessions.Remove(item);
                        return item.SessionId;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Set whether a given socket is connected over WebSocket
        /// </summary>
        /// <param name="key">Session key</param>
        /// <param name="webSocketConnected">Whether a WebSocket is connected</param>
        public void SetWebSocketState(string key, bool webSocketConnected)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Key == key)
                    {
                        item.LastQueryTime = DateTime.Now;
                        if (webSocketConnected)
                        {
                            item.NumWebSocketsConnected++;
                            _logger?.LogInformation("Session {0} registered a WebSocket connection", key);
                        }
                        else
                        {
                            item.NumWebSocketsConnected--;
                            _logger?.LogInformation("Session {0} unregistered a WebSocket connection", key);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Set whether a potentially long-running HTTP request has started or finished
        /// </summary>
        /// <param name="user">Principal user</param>
        /// <param name="requestStarted">Whether a WebSocket is connected</param>
        public void SetLongRunningHttpRequest(ClaimsPrincipal user, bool requestStarted)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Ticket.Principal == user)
                    {
                        item.LastQueryTime = DateTime.Now;
                        if (requestStarted)
                        {
                            item.NumRunningRequests++;
                            _logger?.LogInformation("Session {0} started a long-running request", user.FindFirstValue("key"));
                        }
                        else
                        {
                            item.NumRunningRequests--;
                            _logger?.LogInformation("Session {0} finished a long-running request", user.FindFirstValue("key"));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Remove sessions that are no longer active
        /// </summary>
        /// <param name="sessionTimeout">Timeout for HTTP sessions</param>
        /// <param name="socketPath">API socket path</param>
        public void MaintainSessions(TimeSpan sessionTimeout, string socketPath)
        {
            lock (_sessions)
            {
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    Session item = _sessions[i];
                    if (item.NumWebSocketsConnected == 0 && item.NumRunningRequests == 0 && DateTime.Now - item.LastQueryTime > sessionTimeout)
                    {
                        // Session expired
                        _sessions.RemoveAt(i);
                        _logger?.LogInformation("Session {0} expired", item.Key);

                        // Attempt to remove it again from DCS
                        _ = Task.Run(async () => await UnregisterExpiredSession(item.SessionId, socketPath));
                    }
                }
            }
        }

        /// <summary>
        /// Unregister an expired HTTP session from the object model
        /// </summary>
        /// <param name="sessionId">Session ID</param>
        /// <param name="socketPath">API socket path</param>
        /// <returns></returns>
        private async Task UnregisterExpiredSession(int sessionId, string socketPath)
        {
            try
            {
                using CommandConnection connection = new();
                await connection.Connect(socketPath);
                await connection.RemoveUserSession(sessionId);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to unregister expired user session");
            }
        }

        /// <summary>
        /// Cache an incoming generic message
        /// </summary>
        /// <param name="message">Message to cache</param>
        public void CacheMessage(string message)
        {
            lock (_sessions)
            {
                foreach (Session session in _sessions)
                {
                    session.CacheMessage(message);
                }
            }
        }

        /// <summary>
        /// Retrieve the cached messages of a given user
        /// </summary>
        /// <returns>Cached messages</returns>
        public string GetCachedMessages(ClaimsPrincipal user)
        {
            lock (_sessions)
            {
                foreach (Session item in _sessions)
                {
                    if (item.Ticket.Principal == user)
                    {
                        return item.GetCachedMessages();
                    }
                }
            }
            return string.Empty;
        }
    }
}
