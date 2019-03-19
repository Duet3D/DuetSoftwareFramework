using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Command interpreter for client requests
    /// </summary>
    public class Command : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Code),
            typeof(Flush),
            typeof(GetFileInfo),
            typeof(GetMachineModel),
            typeof(ResolvePath),
            typeof(SimpleCode)
        };
        
        /// <summary>
        /// Constructor of the command interpreter
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Command(Connection conn, ClientInitMessage initMessage) : base(conn, initMessage)
        {
        }
        
        /// <summary>
        /// Reads incoming command requests and processes them. See <see cref="DuetAPI.Commands"/> namespace for a list
        /// of supported commands. The actual implementations can be found in <see cref="DuetControlServer.Commands"/>.
        /// </summary>
        /// <returns></returns>
        public override async Task Process()
        {
            do
            {
                try
                {
                    // Read another command
                    BaseCommand command = await Connection.ReceiveCommand();
                    if (!SupportedCommands.Contains(command.GetType()))
                    {
                        throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                    }
                    
                    // Execute it and send back the result
                    object result = await command.Execute();
                    await Connection.SendResponse(result);
                }
                catch (Exception e)
                {
                    if (Connection.IsConnected)
                    {
                        // Inform the client about this error
                        await Connection.SendResponse(e);
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
