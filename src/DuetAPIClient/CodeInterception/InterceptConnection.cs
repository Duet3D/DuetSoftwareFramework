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

namespace DuetAPIClient;

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
    /// <param name="channels">List of input channels where codes may be intercepted or null for all available channels</param>
    /// <param name="filters">Optional list of codes that may be intercepted</param>
    /// <param name="priorityCodes">Define if priority codes may be intercepted</param>
    /// <param name="socketPath">Optional path to the DCS UNIX socket file</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
    /// <exception cref="IOException">Connection mode is unavailable</exception>
    /// <exception cref="SocketException">Init message could not be processed</exception>
    public void Connect(InterceptionMode mode, IEnumerable<CodeChannel>? channels = null, IEnumerable<string>? filters = null, bool priorityCodes = false, string? socketPath = null)
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

        InterceptInitMessage initMessage = new()
        {
            InterceptionMode = mode,
            Channels = Channels,
            AutoFlush = AutoFlush,
            AutoEvaluateExpressions = AutoEvaluateExpressions,
            Filters = Filters,
            PriorityCodes = priorityCodes
        };
        Connect(initMessage, socketPath);
    }

    /// <summary>
    /// Establishes a connection to the given UNIX socket file asynchronously
    /// </summary>
    /// <param name="mode">Interception mode</param>
    /// <param name="channels">List of input channels where codes may be intercepted or null for all available channels</param>
    /// <param name="filters">Optional list of codes that may be intercepted</param>
    /// <param name="priorityCodes">Define if priority codes may be intercepted</param>
    /// <param name="socketPath">Optional path to the DCS UNIX socket file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
    /// <exception cref="IOException">Connection mode is unavailable</exception>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Init message could not be processed</exception>
    public async Task ConnectAsync(InterceptionMode mode, IEnumerable<CodeChannel>? channels = null, IEnumerable<string>? filters = null, bool priorityCodes = false, string? socketPath = null, CancellationToken cancellationToken = default)
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

        InterceptInitMessage initMessage = new()
        {
            InterceptionMode = mode,
            Channels = Channels,
            AutoFlush = AutoFlush,
            AutoEvaluateExpressions = AutoEvaluateExpressions,
            Filters = Filters,
            PriorityCodes = priorityCodes
        };
        await ConnectAsync(initMessage, socketPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for a code to be intercepted and read it
    /// </summary>
    /// <returns>Code being intercepted or null if the connection has been closed</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CodeInterceptionRead"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public Code ReceiveCode() => ReceiveCommand<Code>();

    /// <summary>
    /// Wait for a code to be intercepted and read it asynchronously
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Code being intercepted or null if the connection has been closed</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CodeInterceptionRead"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public async ValueTask<Code> ReceiveCodeAsync(CancellationToken cancellationToken = default)
    {
        return await ReceiveCommandAsync<Code>(cancellationToken).ConfigureAwait(false);
    } 

    /// <summary>
    /// When intercepting a code wait for all previous codes of the given channel to finish
    /// </summary>
    /// <returns>True if all pending codes could be flushed</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public bool Flush() => PerformCommand<bool>(new Flush());

    /// <summary>
    /// When intercepting a code wait for all previous codes of the given channel to finish asynchronously
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>True if all pending codes could be flushed</returns>
    /// <exception cref="InvalidOperationException">Requested code channel is disabled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="SbcPermissions.CommandExecution"/>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        return await PerformCommandAsync<bool>(new Flush(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Instruct the control server to cancel the last received code (in intercepting mode)
    /// </summary>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Cancel"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public void CancelCode() => SendCommand(new Cancel());

    /// <summary>
    /// Instruct the control server to cancel the last received code (in intercepting mode) asynchronously
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Cancel"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public async ValueTask CancelCodeAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(new Cancel(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Instruct the control server to ignore the last received code (in intercepting mode)
    /// </summary>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Ignore"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionRead"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public void IgnoreCode() => SendCommand(new Ignore());

    /// <summary>
    /// Instruct the control server to ignore the last received code (in intercepting mode) asynchronously
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Ignore"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionRead"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public async ValueTask IgnoreCodeAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(new Ignore(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode)
    /// </summary>
    /// <param name="type">Type of the resolving message</param>
    /// <param name="content">Content of the resolving message</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Message"/>
    /// <seealso cref="Resolve"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public void ResolveCode(MessageType type, string content) => SendCommand(new Resolve { Content = content, Type = type });

    /// <summary>
    /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode) asynchronously
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
    public async ValueTask ResolveCodeAsync(MessageType type, string content, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(new Resolve { Content = content, Type = type }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode)
    /// </summary>
    /// <param name="message">Message to resolve the code with</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Message"/>
    /// <seealso cref="Resolve"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public void ResolveCode(Message message) => SendCommand(new Resolve { Content = message.Content, Type = message.Type });

    /// <summary>
    /// Instruct the control server to resolve the last received code with the given message details (in intercepting mode) asynchronously
    /// </summary>
    /// <param name="message">Message to resolve the code with</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Message"/>
    /// <seealso cref="Resolve"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public async ValueTask ResolveCodeAsync(Message message, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(new Resolve { Content = message.Content, Type = message.Type }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrite the code being intercepted. This effectively modifies the code before it is processed further
    /// </summary>
    /// <param name="code">Updated code</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Message"/>
    /// <seealso cref="Resolve"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public void RewriteCode(Code code) => SendCommand(new Rewrite { Code = code });

    /// <summary>
    /// Rewrite the code being intercepted asynchronously. This effectively modifies the code before it is processed further
    /// </summary>
    /// <param name="code">Updated code</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
    /// <exception cref="SocketException">Command could not be processed</exception>
    /// <seealso cref="Message"/>
    /// <seealso cref="Resolve"/>
    /// <seealso cref="SbcPermissions.CodeInterceptionReadWrite"/>
    public async ValueTask RewriteCodeAsync(Code code, CancellationToken cancellationToken = default)
    {
        await SendCommandAsync(new Rewrite { Code = code }, cancellationToken).ConfigureAwait(false);
    }
}
