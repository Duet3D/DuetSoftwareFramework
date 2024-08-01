using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for intercepting G/M/T-codes from the control server
    /// </summary>
    /// <seealso cref="ConnectionMode.Intercept"/>
    public sealed class InterceptConnection : BaseCommandConnection
    {
        /// <summary>
        /// Creates a new connection in intercepting mode
        /// </summary>
        public InterceptConnection() : base(ConnectionMode.Intercept) { }

        /// <summary>
        /// Mode of the interceptor
        /// </summary>
        public InterceptionMode Mode { get; private set; }

        /// <summary>
        /// List of input channels where codes may be intercepted. If the list is empty, all available channels are used
        /// </summary>
        public List<CodeChannel> Channels { get; set; } = [];

        /// <summary>
        /// Automatically flush the code channel before notifying the client in case a code filter is specified
        /// </summary>
        /// <remarks>
        /// This option makes extra Flush calls in the interceptor implementation obsolete.
        /// It is highly recommended to enable this in order to avoid potential deadlocks when dealing with macros!
        /// </remarks>
        public bool AutoFlush { get; set; } = true;

        /// <summary>
        /// Automatically evaluate expression parameters to their final values before sending it over to the client.
        /// This requires <see cref="AutoFlush"/> to be true and happens when the remaining codes have been processed.
        /// </summary>
        public bool AutoEvaluateExpressions { get; set; } = true;

        /// <summary>
        /// List of G/M/T-codes to filter or Q0 for comments
        /// </summary>
        /// <remarks>
        /// This may only specify the code type and major/minor number (e.g. G1)
        /// </remarks>
        public List<string> Filters { get; set; } = [];

        /// <summary>
        /// Defines if priority codes may be intercepted (e.g. M122 or M999)
        /// </summary>
        /// <seealso cref="CodeFlags.IsPrioritized"/>
        public bool PriortyCodes { get; private set; }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Interception mode</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        [Obsolete("Use the new overload to specify the code channels to intercept")]
        public Task Connect(InterceptionMode mode, string socketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Channels.Clear();
            Channels.AddRange(Inputs.ValidChannels);
            Filters.Clear();
            PriortyCodes = false;

            InterceptInitMessage initMessage = new() { InterceptionMode = mode };
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Interception mode</param>
        /// <param name="channels">List of input channels where codes may be intercepted or null for all available channels</param>
        /// <param name="filters">Optional list of codes that may be intercepted</param>
        /// <param name="priorityCodes">Define if priority codes may be intercepted</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        public Task Connect(InterceptionMode mode, IEnumerable<CodeChannel>? channels = null, IEnumerable<string>? filters = null, bool priorityCodes = false, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Channels.Clear();
            Channels.AddRange(channels ?? Inputs.ValidChannels);
            Filters.Clear();
            if (filters is not null)
            {
                Filters.AddRange(filters);
            }
            PriortyCodes = priorityCodes;

            InterceptInitMessage initMessage = new() {
                InterceptionMode = mode,
                Channels = Channels,
                AutoFlush = AutoFlush,
                AutoEvaluateExpressions = AutoEvaluateExpressions,
                Filters = Filters,
                PriorityCodes = priorityCodes
            };
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Wait for a code to be intercepted and read it
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Code being intercepted or null if the connection has been closed</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CodeInterceptionRead"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public ValueTask<Code> ReceiveCode(CancellationToken cancellationToken = default) => Receive<Code>(cancellationToken);

        /// <summary>
        /// When intercepting a code wait for all previous codes of the given channel to finish
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if all pending codes could be flushed</returns>
        /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="SbcPermissions.CommandExecution"/>
        public Task<bool> Flush(CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new Flush(), cancellationToken);
        }

        /// <summary>
        /// Instruct the control server to cancel the last received code (in intercepting mode)
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="Cancel"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public ValueTask CancelCode(CancellationToken cancellationToken = default) => Send(new Cancel(), cancellationToken);

        /// <summary>
        /// Instruct the control server to ignore the last received code (in intercepting mode)
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="Ignore"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionRead"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public ValueTask IgnoreCode(CancellationToken cancellationToken = default) => Send(new Ignore(), cancellationToken);

        /// <summary>
        /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode)
        /// </summary>
        /// <param name="type">Type of the resolving message</param>
        /// <param name="content">Content of the resolving message</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="Message"/>
        /// <seealso cref="Resolve"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public ValueTask ResolveCode(MessageType type, string content, CancellationToken cancellationToken = default)
        {
            return Send(new Resolve { Content = content, Type = type }, cancellationToken);
        }

        /// <summary>
        /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode)
        /// </summary>
        /// <param name="message">Message to resolve the code with</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="Message"/>
        /// <seealso cref="Resolve"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public ValueTask ResolveCode(Message message, CancellationToken cancellationToken = default)
        {
            return Send(new Resolve { Content = message.Content, Type = message.Type }, cancellationToken);
        }

        /// <summary>
        /// Rewrite the code being intercepted. This effectively modifies the code before it is processed further
        /// </summary>
        /// <param name="code">Updated code</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="Message"/>
        /// <seealso cref="Resolve"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public ValueTask RewriteCode(Code code, CancellationToken cancellationToken = default)
        {
            return Send(new Rewrite { Code = code }, cancellationToken);
        }
    }
}