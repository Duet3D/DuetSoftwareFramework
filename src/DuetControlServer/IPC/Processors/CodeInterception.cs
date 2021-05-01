using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Interception processor that notifies clients about G/M/T-codes or comments being processed
    /// </summary>
    public sealed class CodeInterception : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        /// <remarks>
        /// In addition to these commands, commands of the <see cref="Command"/> interpreter are supported while a code is being intercepted
        /// </remarks>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Cancel),
            typeof(Ignore),
            typeof(Resolve)
        };

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static CodeInterception() => AddSupportedCommands(SupportedCommands);

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
        private readonly List<CodeChannel> _channels;

        /// <summary>
        /// List of codes that may be intercepted
        /// </summary>
        private readonly List<string> _filters;

        /// <summary>
        /// Defines if priority codes may be intercepted
        /// </summary>
        private readonly bool _priorityCodes;

        /// <summary>
        /// Monitor for exchanging data during interceptions
        /// </summary>
        private readonly AsyncMonitor _codeMonitor = new();

        /// <summary>
        /// Current code being intercepted
        /// </summary>
        private volatile Code _codeBeingIntercepted;

        /// <summary>
        /// Interception command to resolve the code being intercepted
        /// </summary>
        private BaseCommand _interceptionResult;

        /// <summary>
        /// Constructor of the interception processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public CodeInterception(Connection conn, ClientInitMessage initMessage) : base(conn)
        {
            InterceptInitMessage interceptInitMessage = (InterceptInitMessage)initMessage;
            _mode = interceptInitMessage.InterceptionMode;
            _channels = (interceptInitMessage.Channels != null) ? interceptInitMessage.Channels.ToList() : new List<CodeChannel>(Enum.GetValues(typeof(CodeChannel)).Cast<CodeChannel>());
            _filters = interceptInitMessage.Filters ?? new List<string>();
            _priorityCodes = interceptInitMessage.PriortyCodes;
        }

        /// <summary>
        /// Waits for commands to be received and enqueues them in a concurrent queue so that a <see cref="Code"/>
        /// can decide when to cancel/resume/resolve the execution.
        /// </summary>
        /// <returns>Task that represents the lifecycle of the connection</returns>
        public override async Task Process()
        {
            lock (_connections[_mode])
            {
                _connections[_mode].Add(this);
            }
            Connection.Logger.Debug("Interception processor registered");

            using (await _codeMonitor.EnterAsync(Program.CancellationToken))
            {
                try
                {
                    do
                    {
                        // Wait for the next code to be intercepted
                        do
                        {
                            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
                            cts.CancelAfter(Settings.SocketPollInterval);

                            try
                            {
                                await _codeMonitor.WaitAsync(Program.CancellationToken);
                                break;
                            }
                            catch (OperationCanceledException)
                            {
                                if (Program.CancellationToken.IsCancellationRequested)
                                {
                                    throw;
                                }
                                Connection.Poll();
                            }
                        }
                        while (true);

                        try
                        {
                            // Send it to the client. Reassign it to a different variable first to hide some internal fields
                            DuetAPI.Commands.Code codeToSend = _codeBeingIntercepted;
                            await Connection.Send(codeToSend);

                            // Keep processing incoming commands until a final action for the code has been received
                            BaseCommand command;
                            Type commandType;
                            do
                            {
                                // Read another command from the IPC connection
                                command = await Connection.ReceiveCommand();
                                commandType = command.GetType();
                                if (Command.SupportedCommands.Contains(commandType))
                                {
                                    // Make sure it is permitted
                                    Connection.CheckPermissions(commandType);

                                    // Execute regular commands here
                                    object result = await command.Invoke();
                                    await Connection.SendResponse(result);
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
                            while (!Program.CancellationToken.IsCancellationRequested);

                            // Stop if the connection has been terminated
                            if (command == null)
                            {
                                break;
                            }
                        }
                        catch (SocketException)
                        {
                            // Client has closed the connection while we're waiting for a result. Carry on...
                            _interceptionResult = null;
                            _codeMonitor.Pulse();
                            throw;
                        }
                    }
                    while (!Program.CancellationToken.IsCancellationRequested);
                }
                finally
                {
                    lock (_connections[_mode])
                    {
                        _connections[_mode].Remove(this);
                    }
                    Connection.Logger.Debug("Interception processor unregistered");
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
            if (!Connection.IsConnected || code.Flags.HasFlag(CodeFlags.IsPrioritized) != _priorityCodes)
            {
                return false;
            }

            if (!_channels.Contains(code.Channel))
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
                    if (asteriskIndex >= 0 && filter.Substring(0, asteriskIndex).Equals(shortCodeString.Substring(0, asteriskIndex), StringComparison.InvariantCultureIgnoreCase))
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
        /// <returns>True if the code has been resolved</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        private async Task<bool> Intercept(Code code)
        {
            using (await _codeMonitor.EnterAsync(Program.CancellationToken))
            {
                // Send it to the IPC client
                _codeBeingIntercepted = code;
                _codeMonitor.Pulse();

                // Wait for a code result to be set by the interceptor
                await _codeMonitor.WaitAsync(Program.CancellationToken);
                try
                {
                    // Code is cancelled. This invokes an OperationCanceledException on the code's task.
                    if (_interceptionResult is Cancel)
                    {
                        throw new OperationCanceledException();
                    }

                    // Code is resolved with a given result and the request is acknowledged
                    if (_interceptionResult is Resolve resolveCommand)
                    {
                        code.Result = (resolveCommand.Content == null) ? new CodeResult() : new CodeResult(resolveCommand.Type, resolveCommand.Content);
                        return true;
                    }

                    // Code is ignored. Don't do anything
                }
                catch (Exception e) when (!(e is OperationCanceledException))
                {
                    Connection.Logger.Error(e, "Interception processor caught an exception");
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
        /// <returns>True if the code has been resolved</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public static async Task<bool> Intercept(Code code, InterceptionMode type)
        {
            if (Program.CancellationToken.IsCancellationRequested)
            {
                // Don't intercept any more codes if the application is being shut down
                return false;
            }

            List<CodeInterception> processors = new();
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
                        processor.Connection.Logger.Debug("Intercepting code {0} ({1})", code, type);
                        if (await processor.Intercept(code))
                        {
                            processor.Connection.Logger.Debug("Code has been resolved");
                            return true;
                        }
                        processor.Connection.Logger.Debug("Code has been ignored");
                    }
                    catch (OperationCanceledException)
                    {
                        processor.Connection.Logger.Debug("Code has been cancelled");
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
        public static bool IsInterceptingConnection(Connection connection)
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

                        return (processor._codeBeingIntercepted != null);
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Get the code being intercepted by a given connection
        /// </summary>
        /// <param name="connection">Connection to look up</param>
        /// <returns>Code being intercepted</returns>
        public static Code GetCodeBeingIntercepted(Connection connection)
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

                        return processor._codeBeingIntercepted;
                    }
                }
            }
            return null;
        }
    }
}
