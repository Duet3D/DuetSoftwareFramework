using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public List<CodeChannel> Channels { get; } = new List<CodeChannel>();

        /// <summary>
        /// List of G/M/T-codes to filter or Q0 for comments
        /// </summary>
        /// <remarks>
        /// This may only specify the code type and major/minor number (e.g. G1)
        /// </remarks>
        public List<string> Filters { get; } = new List<string>();

        /// <summary>
        /// Defines if priority codes may be intercepted (e.g. M122 or M999)
        /// </summary>
        /// <seealso cref="DuetAPI.Commands.CodeFlags.IsPrioritized"/>
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
        [Obsolete]
        public Task Connect(InterceptionMode mode, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Channels.Clear();
            Channels.AddRange(Enum.GetValues(typeof(CodeChannel)).Cast<CodeChannel>());
            Filters.Clear();
            PriortyCodes = false;

            InterceptInitMessage initMessage = new InterceptInitMessage { InterceptionMode = mode };
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="mode">Interception mode</param>
        /// <param name="channels">Optional list of input channels where codes may be intercepted</param>
        /// <param name="filters">Optional list of codes that may be intercepted</param>
        /// <param name="priortyCodes">Define if priorty codes may be intercepted</param>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        public Task Connect(InterceptionMode mode, IEnumerable<CodeChannel> channels = null, IEnumerable<string> filters = null, bool priortyCodes = false, string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            Mode = mode;
            Channels.Clear();
            if (channels == null)
            {
                Channels.AddRange(Enum.GetValues(typeof(CodeChannel)).Cast<CodeChannel>());
            }
            else
            {
                Channels.AddRange(channels);
            }
            Filters.Clear();
            if (filters != null)
            {
                Filters.AddRange(filters);
            }
            PriortyCodes = priortyCodes;

            InterceptInitMessage initMessage = new InterceptInitMessage { InterceptionMode = mode, Channels = Channels, Filters = Filters, PriortyCodes = priortyCodes };
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
        public Task<Code> ReceiveCode(CancellationToken cancellationToken = default) => Receive<Code>(cancellationToken);

        /// <summary>
        /// Instruct the control server to cancel the last received code (in intercepting mode)
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="Cancel"/>
        /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
        public Task CancelCode(CancellationToken cancellationToken = default) => Send(new Cancel(), cancellationToken);

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
        public Task IgnoreCode(CancellationToken cancellationToken = default) => Send(new Ignore(), cancellationToken);

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
        public Task ResolveCode(MessageType type, string content, CancellationToken cancellationToken = default)
        {
            return Send(new Resolve { Content = content, Type = type }, cancellationToken);
        }
    }
}