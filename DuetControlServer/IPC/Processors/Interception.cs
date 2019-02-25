using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    public class Interception : Base
    {
        private static readonly ConcurrentDictionary<Interception, InterceptionMode> interceptors = new ConcurrentDictionary<Interception, InterceptionMode>();
        
        private readonly BufferBlock<BaseCommand> pendingCommands = new BufferBlock<BaseCommand>();
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        public Interception(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
            interceptors.TryAdd(this, (initMessage as InterceptInitMessage).InterceptionMode);
        }
        
        // Attempt to read another code.
        // This must happen here because we need to know when the sockets are closed
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
                    await Connection.Send(e);
                }
                else
                {
                    interceptors.TryRemove(this, out InterceptionMode dummy);
                    throw;
                }
            }
        }

        // Handle an instruction from the interceptor
        private async Task EnqueueCommand(BaseCommand command)
        {
            // Only allow certain commands to be run
            if (command is Ignore ||
                command is Resolve ||
                command is Code ||
                command is Flush ||
                command is SimpleCode)
            {
                await pendingCommands.SendAsync(command);
            }
            else
            {
                throw new ArgumentException($"Invalid command {command.GetType().Name} (wrong mode?)");
            }
        }

        // Notify the interceptors about a code that is supposed to be executed and wait for the result
        private async Task<CodeResult> Intercept(Code code)
        {
            // Avoid race conditions
            await semaphore.WaitAsync(Program.CancelSource.Token);
            
            // Send it to the interceptor
            await Connection.Send(code);
            
            // Keep on processing commands from the interceptor until a handling result is returned.
            // This must be either an Ignore or a Resolve instruction
            try
            {
                while (await pendingCommands.OutputAvailableAsync(Program.CancelSource.Token))
                {
                    BaseCommand command = await pendingCommands.ReceiveAsync(Program.CancelSource.Token);

                    // Code is ignored. Don't do anything with it
                    if (command is Ignore)
                    {
                        await Connection.Send(null);
                        break;
                    }

                    // Code is supposed to be resolved with a given result
                    if (command is Resolve)
                    {
                        await Connection.Send(null);
                        semaphore.Release();
                        return new CodeResult()
                        {
                            new Message
                            {
                                Type = (command as Resolve).Type,
                                Content = (command as Resolve).Content
                            }
                        };
                    }

                    // Execute the incoming command and pass the result
                    //object result = command.Execute();
                    //await Connection.Send(result);
                }
            }
            catch (Exception e)
            {
                if (Connection.IsConnected)
                {
                    // Notify the client
                    await Connection.Send(e);
                }
                else
                {
                    Console.WriteLine("Intercept error: " + e.Message);
                }
            }
            
            semaphore.Release();
            return null;
        }
        
        // Called when a G/M/T-code is supposed to be intercepted. If it is resolved, a CodeResult instance is returned
        public static async Task<CodeResult> Intercept(Code code, InterceptionMode type)
        {
            foreach (var pair in interceptors)
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
