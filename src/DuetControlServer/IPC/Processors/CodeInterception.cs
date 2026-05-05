using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.IPC.Processors;

/// <summary>
/// Interception processor that notifies clients about G/M/T-codes or comments being processed
/// </summary>
public sealed class CodeInterception : IProcessor
{
    /// <summary>
    /// List of specific commands supported in this mode
    /// </summary>
    /// <remarks>
    /// In addition to these commands, commands of the <see cref="Command"/> interpreter are supported while a code is being intercepted
    /// </remarks>
    public static Type[] SupportedCommands { get; } =
    [
        typeof(Cancel),
        typeof(Ignore),
        typeof(Resolve),
        typeof(Rewrite)
    ];

    /// <summary>
    /// List of all supported commands
    /// </summary>
    public static Type[] AllSupportedCommands { get; } = [.. Command.SupportedCommands, .. SupportedCommands];

    /// <summary>
    /// Logger instance
    /// </summary>
    private static ILogger? _logger;

    /// <summary>
    /// Set the logger (called during initialization)
    /// </summary>
    public static void SetLogger(ILogger<CodeInterception> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Dictionary of interception mode vs item containers (connection vs queue of codes being intercepted)
    /// </summary>
    private static readonly Dictionary<InterceptionMode, List<CodeInterception>> _connections = new()
    {
        { InterceptionMode.Pre, new List<CodeInterception>() },
        { InterceptionMode.Post, new List<CodeInterception>() },
        { InterceptionMode.Executed, new List<CodeInterception>() }
    };

    /// <summary>
    /// Mode of this interceptor
    /// </summary>
    private readonly InterceptionMode _mode;

    /// <summary>
    /// List of input channels where codes may be intercepted
    /// </summary>
    private readonly CodeChannel[] _channels;

    /// <summary>
    /// Automatically flush the code channel when a code from the filter list is handled
    /// </summary>
    private readonly bool _autoFlush;

    /// <summary>
    /// Automatically evaluate passed expressions. Requires <see cref="_autoFlush"/> to be true
    /// </summary>
    private readonly bool _autoEvaluateExpressions;

    /// <summary>
    /// List of codes that may be intercepted
    /// </summary>
    private readonly List<string> _filters;

    /// <summary>
    /// Defines if priority codes may be intercepted
    /// </summary>
    private readonly bool _priorityCodes;

    /// <summary>
    /// Lock to guard against the case of multiple codes being sent to this interceptor at once
    /// </summary>
    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Monitor for exchanging data during interceptions
    /// </summary>
    private readonly AsyncMonitor _codeMonitor = new();

    /// <summary>
    /// Current code being intercepted
    /// </summary>
    private volatile Code? _codeBeingIntercepted;

    /// <summary>
    /// Interception command to resolve the code being intercepted
    /// </summary>
    private BaseCommand? _interceptionResult;

    /// <summary>
    /// Code processor
    /// </summary>
    private readonly CodeProcessor _codeProcessor;

    /// <summary>
    /// Settings
    /// </summary>
    private readonly Settings _settings;

    /// <summary>
    /// Connection to the IPC client served by this processor
    /// </summary>
    public Connection Connection { get; }

    /// <summary>
    /// Constructor of the interception processor
    /// </summary>
    /// <param name="conn">Connection instance</param>
    /// <param name="initMessage">Initialization message</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="settings">Settings</param>
    public CodeInterception(Connection conn, ClientInitMessage initMessage, CodeProcessor codeProcessor, IOptions<Settings> settings)
    {
        Connection = conn;
        InterceptInitMessage interceptInitMessage = (InterceptInitMessage)initMessage;
        _mode = interceptInitMessage.InterceptionMode;
        _channels = (interceptInitMessage.Channels is { Count: > 0 }) ? [.. interceptInitMessage.Channels] : Inputs.ValidChannels;
        _autoFlush = interceptInitMessage.AutoFlush && (interceptInitMessage.Filters?.Count ?? 0) > 0;
        _autoEvaluateExpressions = _autoFlush && interceptInitMessage.AutoEvaluateExpressions;
        _filters = interceptInitMessage.Filters ?? [];
        _priorityCodes = interceptInitMessage.PriorityCodes;

        _codeProcessor = codeProcessor;
        _settings = settings.Value;
    }

    /// <summary>
    /// Waits for commands to be received and enqueues them in a concurrent queue so that a <see cref="Code"/>
    /// can decide when to cancel/resume/resolve the execution.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that represents the lifecycle of the connection</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        lock (_connections[_mode])
        {
            _connections[_mode].Add(this);
        }
        _logger?.LogDebug("Interception processor registered for IPC#{0}", Connection.Id);

        using (await _codeMonitor.EnterAsync(cancellationToken))
        {
            try
            {
                do
                {
                    // Wait for the next code to be intercepted
                    do
                    {
                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(_settings.SocketPollInterval);

                        try
                        {
                            await _codeMonitor.WaitAsync(cancellationToken);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            Connection.Poll();
                        }
                    }
                    while (true);

                    try
                    {
                        // Send it to the client
                        await Connection.SendCommandAsync(_codeBeingIntercepted!);

                        // Keep processing incoming commands until a final action for the code has been received
                        do
                        {
                            // Read another command from the IPC connection
                            BaseCommand command = await Connection.ReceiveCommandAsync(AllSupportedCommands, cancellationToken);
                            Type commandType = command.GetType();
                            if (Command.SupportedCommands.Contains(commandType))
                            {
                                // Make sure it is permitted
                                Connection.CheckPermissions(commandType);

                                // Execute regular commands here
                                object? result = await command.InvokeAsync(cancellationToken);
                                await Connection.SendResponseAsync(result);
                            }
                            else if (SupportedCommands.Contains(commandType))
                            {
                                // Make sure it is permitted
                                Connection.CheckPermissions(commandType);

                                // Send other commands to the task intercepting the code
                                _interceptionResult = command;
                                _codeMonitor.Pulse();
                                break;
                            }
                            else
                            {
                                // Take care of unsupported commands
                                throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                            }
                        }
                        while (!cancellationToken.IsCancellationRequested);
                    }
                    catch (SocketException)
                    {
                        // Client has closed the connection while we're waiting for a result. Carry on...
                        _interceptionResult = null;
                        _codeMonitor.Pulse();
                        throw;
                    }
                }
                while (!cancellationToken.IsCancellationRequested);
            }
            finally
            {
                lock (_connections[_mode])
                {
                    _connections[_mode].Remove(this);
                }
                _logger?.LogDebug("Interception processor unregistered for IPC#{0}", Connection.Id);
            }
        }
    }

    /// <summary>
    /// Check if the connection may intercept the given code
    /// </summary>
    /// <param name="code">Code to check</param>
    /// <returns>Whether the code may be intercepted</returns>
    private bool CanIntercept(Code code)
    {
        if (!Connection.IsConnected || code.Connection == Connection ||
            code.Flags.HasFlag(CodeFlags.IsPrioritized) != _priorityCodes || !_channels.Contains(code.Channel))
        {
            return false;
        }

        if (_filters.Count > 0)
        {
            string shortCodeString = (code.Type == CodeType.Comment) ? "Q0" : code.ToShortString();
            foreach (string filter in _filters)
            {
                if (filter.Equals(shortCodeString, StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }

                int asteriskIndex = filter.IndexOf('*');
                if (asteriskIndex >= 0 && filter[..asteriskIndex].Equals(shortCodeString[..asteriskIndex], StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Called by the <see cref="Code"/> implementation to check if the client wants to intercept a G/M/T-code
    /// </summary>
    /// <param name="code">Code to intercept</param>
    /// <param name="cancellationToken">Cancellation token to cancel the interception</param>
    /// <returns>True if the code has been resolved</returns>
    /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
    private async Task<bool> InterceptAsync(Code code, CancellationToken cancellationToken = default)
    {
        // Flush the code channel here if required and expand expression values if requested
        if (_autoFlush && !await _codeProcessor.FlushAsync(code, _autoEvaluateExpressions, _autoEvaluateExpressions, cancellationToken: cancellationToken))
        {
            throw new OperationCanceledException();
        }

        // Deal with the client
        using (await _lock.LockAsync(code.CancellationToken))
        {
            using (await _codeMonitor.EnterAsync(code.CancellationToken))
            {
                try
                {
                    // Send the code being intercepted to the IPC client
                    _codeBeingIntercepted = code;
                    _codeMonitor.Pulse();

                    try
                    {
                        // Wait for the IPC client to send back a result
                        await _codeMonitor.WaitAsync(cancellationToken);

                        // Code is cancelled. This invokes an OperationCanceledException on the code's task
                        if (_interceptionResult is Cancel)
                        {
                            throw new OperationCanceledException();
                        }

                        // Code is resolved with a given result and the request is acknowledged
                        if (_interceptionResult is Resolve resolveCommand)
                        {
                            code.Result = new Message(resolveCommand.Type, resolveCommand.Content);
                            return true;
                        }

                        // Code is rewritten
                        if (_interceptionResult is Rewrite rewriteCommand)
                        {
                            code.CopyFrom(rewriteCommand.Code);
                        }

                        // Code is ignored. Don't do anything
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _logger?.LogError(e, "Interception processor for IPC#{0} caught an exception", Connection.Id);
                    }
                }
                finally
                {
                    // No longer intercepting a code...
                    _codeBeingIntercepted = null;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Called by the <see cref="Code"/> class to intercept a code.
    /// This method goes through each connected interception channel and notifies the clients.
    /// </summary>
    /// <param name="code">Code to intercept</param>
    /// <param name="type">Type of the interception</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the code has been resolved</returns>
    /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
    public static async ValueTask<bool> InterceptAsync(Code code, InterceptionMode type, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // Don't intercept any more codes if the operation is supposed to be cancelled
            return false;
        }

        List<CodeInterception> processors = [];
        lock (_connections[type])
        {
            processors.AddRange(_connections[type]);
        }

        foreach (CodeInterception processor in processors)
        {
            if (processor.CanIntercept(code))
            {
                try
                {
                    _logger?.LogDebug("Intercepting code {0} ({1}) via IPC#{2}", code, type, processor.Connection.Id);
                    if (await processor.InterceptAsync(code, cancellationToken))
                    {
                        _logger?.LogDebug("Code has been resolved by IPC#{0}", processor.Connection.Id);
                        return true;
                    }
                    _logger?.LogDebug("Code has been ignored by IPC#{0}", processor.Connection.Id);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug("Code has been cancelled by IPC#{0}", processor.Connection.Id);
                    throw;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if the given connection is currently intercepting a code
    /// </summary>
    /// <param name="connection">Connection ID to check</param>
    /// <returns>True if the connection is intercepting a code</returns>
    public static bool IsInterceptingConnection(Connection? connection)
    {
        foreach (List<CodeInterception> processorList in _connections.Values)
        {
            lock (processorList)
            {
                foreach (CodeInterception processor in processorList)
                {
                    if (processor.Connection != connection)
                    {
                        continue;
                    }

                    return processor._codeBeingIntercepted is not null;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Get the code being intercepted by a given connection
    /// </summary>
    /// <param name="connection">Connection to look up</param>
    /// <param name="mode">Mode of the corresponding interceptor</param>
    /// <returns>Code being intercepted</returns>
    public static Code? GetCodeBeingIntercepted(Connection? connection, out InterceptionMode mode)
    {
        if (connection is not null)
        {
            foreach (List<CodeInterception> processorList in _connections.Values)
            {
                lock (processorList)
                {
                    foreach (CodeInterception processor in processorList)
                    {
                        if (processor.Connection != connection)
                        {
                            continue;
                        }

                        mode = processor._mode;
                        return processor._codeBeingIntercepted;
                    }
                }
            }
        }
        mode = InterceptionMode.Pre;
        return null;
    }

    /// <summary>
    /// Print diagnostics
    /// </summary>
    /// <param name="builder">String builder to write to</param>
    public static void Diagnostics(StringBuilder builder)
    {
        foreach (List<CodeInterception> processorList in _connections.Values)
        {
            lock (processorList)
            {
                foreach (CodeInterception processor in processorList)
                {
                    if (processor._codeBeingIntercepted is not null)
                    {
                        builder.AppendLine($"IPC connection #{processor.Connection.Id} is intercepting {processor._codeBeingIntercepted} ({processor._mode})");
                    }
                }
            }
        }
    }
}
