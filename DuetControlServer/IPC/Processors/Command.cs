using System;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    public class Command : Base
    {
        public Command(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
        }
        
        // Deal with incoming command requests and process them.
        // See DuetAPI.Commands namespace for a list of supported instructions
        // The actual implementations can be found in DuetControlServer.Commands
        public override async Task Process()
        {
            do
            {
                try
                {
                    BaseCommand command = await Connection.ReceiveCommand();
                    object result = await command.Execute();
                    await Connection.Send(result);
                }
                catch (Exception e)
                {
                    if (Connection.IsConnected)
                    {
                        // Inform the client about this error
                        await Connection.Send(e);
                        Console.WriteLine(e);
                    }
                    else
                    {
                        throw;
                    }
                }
            } while (Connection.IsConnected);
        }
    }
}
