using DuetAPI;
using DuetAPI.Commands;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace DuetControlServer.IPC.Worker
{
    public class Interception : Base
    {
        private static readonly ConcurrentDictionary<Interception, InterceptionType> interceptors = new ConcurrentDictionary<Interception, InterceptionType>();
        private BufferBlock<BaseCommand> pendingCommands = new BufferBlock<BaseCommand>();

        public Interception(Socket socket, InterceptionType type) : base(socket)
        {
            interceptors.TryAdd(this, type);
        }

        // Attempt to read another code.
        // This must happen here because we need to know when the sockets are closed
        public override async Task<Base> Work()
        {
            try
            {
                BaseCommand command = await ReceiveCommand();
                await EnqueueCommand(command);
            }
            catch (Exception e)
            {
                if (Socket.Connected)
                {
                    // Inform the client about this error
                    await Send(e);
                }
                else
                {
                    interceptors.TryRemove(this, out InterceptionType dummy);
                    throw;
                }
            }
            return this;
        }

        private async Task EnqueueCommand(BaseCommand command)
        {
            if (command is Ignore ||
                command is Resolve ||
                command is Code ||
                command is Flush ||
                command is SimpleCode)
            {
                // Only allow certain commands to be run
                await pendingCommands.SendAsync(command);
            }
            else
            {
                throw new ArgumentException($"Invalid command {command.GetType().Name} (wrong mode?)");
            }
        }

        private async Task<CodeResult> Intercept(Code code)
        {
            try
            {
                while (await pendingCommands.OutputAvailableAsync(Program.CancelSource.Token))
                {
                    BaseCommand command = await pendingCommands.ReceiveAsync(Program.CancelSource.Token);

                    // Code is ignored. Don't do anything with it
                    if (command is Ignore)
                    {
                        await Send(null);
                        return null;
                    }

                    // Code is supposed to be resolved with a given result
                    if (command is Resolve)
                    {
                        await Send(null);

                        return new CodeResult(code)
                        {
                            new Message
                            {
                                Type = (command as Resolve).Type,
                                Content = (command as Resolve).Content
                            }
                        };
                    }

                    // Execute the incoming command and pass the result
                    object result = command.Execute();
                    await Send(result);
                }
            }
            catch (Exception e)
            {
                if (Socket.Connected)
                {
                    // Notify the client
                    await Send(e);
                }
                else
                {
                    Console.WriteLine("Intercept error: " + e.Message);
                }
            }

            return null;
        }
        
        // Called when a G/M/T-code is supposed to be intercepted. If it is resolved, a CodeResult instance is returned
        public static async Task<CodeResult> Intercept(Code code, InterceptionType type)
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
