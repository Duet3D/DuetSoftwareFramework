using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using Nito.AsyncEx;

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
        
        private static readonly ConcurrentDictionary<Interception, InterceptionMode> _interceptors = new ConcurrentDictionary<Interception, InterceptionMode>();
        
        private readonly AsyncCollection<Code> _codeQueue = new AsyncCollection<Code>();
        private readonly AsyncCollection<BaseCommand> _commandQueue = new AsyncCollection<BaseCommand>();

        private readonly InterceptionMode _mode;
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Constructor of the interception processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Interception(Connection conn, ClientInitMessage initMessage) : base(conn)
        {
            InterceptInitMessage interceptInitMessage = (InterceptInitMessage)initMessage;
            _mode = interceptInitMessage.InterceptionMode;

            _interceptors.TryAdd(this, _mode);
        }
        
        /// <summary>
        /// Waits for commands to be received and enqueues them in a concurrent queue so that a <see cref="Code"/>
        /// can decide when to cancel/resume/resolve the execution.
        /// </summary>
        /// <returns>Task that represents the lifecycle of the connection</returns>
        public override async Task Process()
        {
            try
            {
                do
                {
                    // Read another code from the interceptor
                    if (await _codeQueue.OutputAvailableAsync(Program.CancelSource.Token))
                    {
                        Code code = await _codeQueue.TakeAsync(Program.CancelSource.Token);
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
                        if (command == null)
                        {
                            break;
                        }

                        if (Command.SupportedCommands.Contains(command.GetType()))
                        {
                            // Interpret regular Command codes here
                            object result = command.Invoke();
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
                    while (!Program.CancelSource.IsCancellationRequested);

                    // Stop if the connection has been terminated
                    if (command == null)
                    {
                        break;
                    }
                }
                while (!Program.CancelSource.IsCancellationRequested);
            }
            catch (SocketException)
            {
                // IPC client has closed the connection
            }
            finally
            {
                _commandQueue.CompleteAdding();
                _interceptors.TryRemove(this, out _);
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
            // Avoid race conditions. A client can deal with only one code at once!
            using (await _lock.LockAsync(Program.CancelSource.Token))
            {
                // Send it to the IPC client
                await _codeQueue.AddAsync(code);

                // Keep on processing commands from the interceptor until a handling result is returned.
                // This must be either a Cancel, Ignore, or Resolve instruction!
                try
                {
                    if (await _commandQueue.OutputAvailableAsync(Program.CancelSource.Token))
                    {
                        BaseCommand command = await _commandQueue.TakeAsync(Program.CancelSource.Token);

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
                    Console.WriteLine($"[err] Interception handler enocuntered an exception: {e}");
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
        public static async Task<bool> Intercept(Code code, InterceptionMode type)
        {
            foreach (var pair in _interceptors)
            {
                if (code.SourceConnection != pair.Key.Connection.Id && pair.Value == type)
                {
                    if (await pair.Key.Intercept(code))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
