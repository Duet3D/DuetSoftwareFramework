using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
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
        /// In addition to these commands, the commands of the <see cref="Command"/> interpreter are supported too
        /// </remarks>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Ignore),
            typeof(Resolve)
        };
        
        private static readonly ConcurrentDictionary<Interception, InterceptionMode> _interceptors = new ConcurrentDictionary<Interception, InterceptionMode>();
        
        private readonly AsyncCollection<BaseCommand> _commandQueue = new AsyncCollection<BaseCommand>();
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Constructor of the interception processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Interception(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
            _interceptors.TryAdd(this, (initMessage as InterceptInitMessage).InterceptionMode);
        }
        
        /// <summary>
        /// Waits for commands to be received and enqueues them in a concurrent queue so that a <see cref="Code"/>
        /// can decide when to resume/resolve the execution.
        /// </summary>
        /// <returns>Task that represents the lifecycle of the connection</returns>
        public override async Task Process()
        {
            try
            {
                do
                {
                    BaseCommand command = await Connection.ReceiveCommand();
                    if (command == null)
                    {
                        break;
                    }

                    await EnqueueCommand(command);
                } while (!Program.CancelSource.IsCancellationRequested);
            }
            finally
            {
                // When the connection is terminated, enqueue an Ignore command for safety to avoid a deadlock after an abnormal termination
                await EnqueueCommand(new Ignore());

                _interceptors.TryRemove(this, out InterceptionMode dummy);
            }
        }

        /// <summary>
        /// Checks if the command request is allowed and enqueues it internally if that is the case.
        /// If it is illegal, an exception is thrown that can be sent back to the client.
        /// </summary>
        /// <param name="command">Command to enqueue</param>
        /// <returns>Task</returns>
        /// <exception cref="ArgumentException">Thrown if the command type is illegal</exception>
        private async Task EnqueueCommand(BaseCommand command)
        {
            if (SupportedCommands.Contains(command.GetType()) || Command.SupportedCommands.Contains(command.GetType()))
            {
                await _commandQueue.AddAsync(command);
            }
            else
            {
                throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
            }
        }

        /// <summary>
        /// Called by the <see cref="Code"/> implementation to check if the client wants to intercept a G/M/T-code
        /// </summary>
        /// <param name="code">Code to intercept</param>
        /// <returns>null if not intercepted or a <see cref="CodeResult"/> instance if resolved</returns>
        private async Task<CodeResult> Intercept(Code code)
        {
            // Avoid race conditions. A client can deal with only one code at once!
            using (await _lock.LockAsync(Program.CancelSource.Token))
            {
                // Send it to the interceptor
                await Connection.SendResponse(code);

                // Keep on processing commands from the interceptor until a handling result is returned.
                // This must be either an Ignore or a Resolve instruction!
                try
                {
                    while (await _commandQueue.OutputAvailableAsync(Program.CancelSource.Token))
                    {
                        BaseCommand command = await _commandQueue.TakeAsync(Program.CancelSource.Token);

                        // Code is ignored. Don't do anything with it but acknowledge the request
                        if (command is Ignore)
                        {
                            await Connection.SendResponse();
                            break;
                        }

                        // Code is resolved with a given result and the request is acknowledged
                        if (command is Resolve)
                        {
                            await Connection.SendResponse();

                            if ((command as Resolve).Content == null)
                            {
                                return new CodeResult();
                            }
                            return new CodeResult((command as Resolve).Type, (command as Resolve).Content);
                        }

                        // Deal with other requests
                        object result = command.Invoke();
                        await Connection.SendResponse(result);
                    }
                }
                catch (Exception e)
                {
                    if (Connection.IsConnected)
                    {
                        // Notify the client
                        await Connection.SendResponse(e);
                    }
                    else
                    {
                        Console.WriteLine("Intercept error: " + e.Message);
                    }
                }
            }
            return null;
        }
        
        /// <summary>
        /// Called by the <see cref="Code"/> class to intercept a code.
        /// This method goes through each connected interception channel and notifies the clients.
        /// </summary>
        /// <param name="code">Code to intercept</param>
        /// <param name="type">Type of the interception</param>
        /// <returns>null if not intercepted and a CodeResult otherwise</returns>
        public static async Task<CodeResult> Intercept(Code code, InterceptionMode type)
        {
            foreach (var pair in _interceptors)
            {
                if (code.SourceConnection != pair.Key.Connection.Id && pair.Value == type)
                {
                    CodeResult result = await pair.Key.Intercept(code);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
            return null;
        }
    }
}
