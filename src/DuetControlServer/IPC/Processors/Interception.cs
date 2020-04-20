using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Interception processor that notifies clients about G/M/T-codes being processed
    /// </summary>
    public class Interception : Base
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
        /// Class to hold intercepting connections and a corresponding lock
        /// </summary>
        private class ConnectionContainer
        {
            /// <summary>
            /// Asynchronous lock for this interception type
            /// </summary>
            private readonly AsyncLock _lock = new AsyncLock();

            /// <summary>
            /// Lock this connection container
            /// </summary>
            /// <returns>Disposable lock</returns>
            public IDisposable Lock() => _lock.Lock(Program.CancellationToken);

            /// <summary>
            /// Lock this connection container asynchronously
            /// </summary>
            /// <returns>Disposable lock</returns>
            public AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync(Program.CancellationToken);

            /// <summary>
            /// Connection intercepting a running code
            /// </summary>
            public int InterceptingConnection = -1;

            /// <summary>
            /// Current code being intercepted
            /// </summary>
            public Code CodeBeingIntercepted;

            /// <summary>
            /// List of intercepting connections
            /// </summary>
            public readonly List<Interception> Connections = new List<Interception>();
        }

        /// <summary>
        /// Dictionary of interception mode vs item containers
        /// </summary>
        private static readonly Dictionary<InterceptionMode, ConnectionContainer> _connections = new Dictionary<InterceptionMode, ConnectionContainer>
        {
            { InterceptionMode.Pre, new ConnectionContainer() },
            { InterceptionMode.Post, new ConnectionContainer() },
            { InterceptionMode.Executed, new ConnectionContainer() }
        };

        /// <summary>
        /// Mode of this interceptor
        /// </summary>
        private readonly InterceptionMode _mode;

        /// <summary>
        /// Codes that have been queued for this interceptor
        /// </summary>
        private readonly AsyncCollection<Code> _codeQueue = new AsyncCollection<Code>();

        /// <summary>
        /// Commands that have been queued by the processor for the codes being intercepted
        /// </summary>
        private readonly AsyncCollection<BaseCommand> _commandQueue = new AsyncCollection<BaseCommand>();

        /// <summary>
        /// Constructor of the interception processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Interception(Connection conn, ClientInitMessage initMessage) : base(conn)
        {
            InterceptInitMessage interceptInitMessage = (InterceptInitMessage)initMessage;
            _mode = interceptInitMessage.InterceptionMode;
        }

        /// <summary>
        /// Waits for commands to be received and enqueues them in a concurrent queue so that a <see cref="Code"/>
        /// can decide when to cancel/resume/resolve the execution.
        /// </summary>
        /// <returns>Task that represents the lifecycle of the connection</returns>
        public override async Task Process()
        {
            using (await _connections[_mode].LockAsync())
            {
                _connections[_mode].Connections.Add(this);
            }
            Connection.Logger.Debug("Interception processor registered");

            try
            {
                do
                {
                    // Read another code from the interceptor
                    if (await _codeQueue.OutputAvailableAsync(Program.CancellationToken))
                    {
                        Code code = await _codeQueue.TakeAsync(Program.CancellationToken);
                        await Connection.Send(code);
                    }
                    else
                    {
                        break;
                    }

                    // Keep processing commands until an action for the code has been received
                    BaseCommand command;
                    do
                    {
                        // Read another command from the IPC connection
                        command = await Connection.ReceiveCommand();
                        if (Command.SupportedCommands.Contains(command.GetType()))
                        {
                            // Interpret regular Command codes here
                            object result = await command.Invoke();
                            await Connection.SendResponse(result);
                        }
                        else if (SupportedCommands.Contains(command.GetType()))
                        {
                            // Send other commands to the task intercepting the code
                            await _commandQueue.AddAsync(command);
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
                while (!Program.CancellationToken.IsCancellationRequested);
            }
            catch (SocketException)
            {
                // IPC client has closed the connection
            }
            finally
            {
                _commandQueue.CompleteAdding();
                using (await _connections[_mode].LockAsync())
                {
                    _connections[_mode].Connections.Remove(this);
                }
                Connection.Logger.Debug("Interception processor unregistered");
            }
        }

        /// <summary>
        /// Called by the <see cref="Code"/> implementation to check if the client wants to intercept a G/M/T-code
        /// </summary>
        /// <param name="code">Code to intercept</param>
        /// <returns>True if the code has been resolved</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        private async Task<bool> Intercept(Code code)
        {
            // Send it to the IPC client
            await _codeQueue.AddAsync(code);

            // Keep on processing commands from the interceptor until a handling result is returned.
            // This must be either a Cancel, Ignore, or Resolve instruction!
            try
            {
                if (await _commandQueue.OutputAvailableAsync(Program.CancellationToken))
                {
                    BaseCommand command = await _commandQueue.TakeAsync(Program.CancellationToken);

                    // Code is cancelled. This invokes an OperationCanceledException on the code's task.
                    if (command is Cancel)
                    {
                        throw new OperationCanceledException();
                    }

                    // Code is resolved with a given result and the request is acknowledged
                    if (command is Resolve resolveCommand)
                    {
                        code.Result = (resolveCommand.Content == null) ? new CodeResult() : new CodeResult(resolveCommand.Type, resolveCommand.Content);
                        return true;
                    }

                    // Code is ignored. Don't do anything
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                _codeQueue.CompleteAdding();
                Connection.Logger.Error(e, "Interception processor caught an exception");
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
            List<Interception> processors = new List<Interception>();
            using (await _connections[type].LockAsync())
            {
                processors.AddRange(_connections[type].Connections);
            }

            foreach (Interception processor in processors)
            {
                if (processor.Connection.IsConnected && code.SourceConnection != processor.Connection.Id)
                {
                    lock (_connections[type])
                    {
                        _connections[type].InterceptingConnection = processor.Connection.Id;
                        _connections[type].CodeBeingIntercepted = code;
                    }

                    try
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
                    finally
                    {
                        using (await _connections[type].LockAsync())
                        {
                            _connections[type].InterceptingConnection = -1;
                            _connections[type].CodeBeingIntercepted = null;
                        }
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
        public static bool IsInterceptingConnection(int connection)
        {
            foreach (ConnectionContainer connections in _connections.Values)
            {
                using (connections.Lock())
                {
                    if (connections.InterceptingConnection == connection)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Get the code being intercepted from a given connection
        /// </summary>
        /// <param name="connection">Connection ID to look up</param>
        /// <returns>Code being intercepted</returns>
        public static Code GetCodeBeingIntercepted(int connection)
        {
            foreach (ConnectionContainer connections in _connections.Values)
            {
                using (connections.Lock())
                {
                    if (connections.InterceptingConnection == connection)
                    {
                        return connections.CodeBeingIntercepted;
                    }
                }
            }
            return null;
        }
    }
}
