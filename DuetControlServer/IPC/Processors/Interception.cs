using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;

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
        public static readonly Type[] SupportedCommands =
        {
            typeof(Ignore),
            typeof(Resolve)
        };
        
        private static readonly ConcurrentDictionary<Interception, InterceptionMode> _interceptors = new ConcurrentDictionary<Interception, InterceptionMode>();
        
        private readonly BufferBlock<BaseCommand> _commandQueue = new BufferBlock<BaseCommand>();
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);    // like a Mutex but with async/await methods

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
                BaseCommand command = await Connection.ReceiveCommand();
                await EnqueueCommand(command);
            }
            catch (Exception e)
            {
                if (Connection.IsConnected)
                {
                    // Inform the client about this error
                    await Connection.SendResponse(e);
                }
                else
                {
                    _interceptors.TryRemove(this, out InterceptionMode dummy);
                    throw;
                }
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
                await _commandQueue.SendAsync(command);
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
            await _mutex.WaitAsync(Program.CancelSource.Token);
            
            // Send it to the interceptor
            await Connection.SendResponse(code);
            
            // Keep on processing commands from the interceptor until a handling result is returned.
            // This must be either an Ignore or a Resolve instruction!
            try
            {
                while (await _commandQueue.OutputAvailableAsync(Program.CancelSource.Token))
                {
                    BaseCommand command = await _commandQueue.ReceiveAsync(Program.CancelSource.Token);

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
                        _mutex.Release();
                        return new CodeResult
                        {
                            new Message
                            {
                                Type = (command as Resolve).Type,
                                Content = (command as Resolve).Content
                            }
                        };
                    }
                    
                    // Deal with other requests
                    object result = command.Execute();
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
            
            _mutex.Release();
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
                if (pair.Value == type)
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
