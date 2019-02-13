using DuetAPI.Commands;
using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DuetControlServer.IPC.Worker
{
    public class Command : Base
    {
        public Command(Socket socket) : base(socket) { }

        // Deal with incoming command requests and process them.
        // See Commands namespace for the actual implementations
        public override async Task<Base> Work()
        {
            try
            {
                BaseCommand command = await ReceiveCommand();
                object result = await command.Execute();
                await Send(result);
            }
            catch (Exception e)
            {
                if (!Socket.Connected)
                {
                    throw;
                }

                // Inform the client about this error
                await Send(e);
            }
            return this;
        }
    }
}
