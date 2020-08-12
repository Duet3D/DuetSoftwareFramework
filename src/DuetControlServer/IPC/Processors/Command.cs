using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using DuetControlServer.Commands;

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
            typeof(AddHttpEndpoint),
            typeof(AddUserSession),
            typeof(Code),
            typeof(GetFileInfo),
            typeof(GetMachineModel),
            typeof(Flush),
            typeof(LockMachineModel),
            typeof(PatchMachineModel),
            typeof(RemoveHttpEndpoint),
            typeof(RemoveUserSession),
            typeof(ResolvePath),
            typeof(SetMachineModel),
            typeof(SimpleCode),
            typeof(SyncMachineModel),
            typeof(UnlockMachineModel),
            typeof(WriteMessage)
        };
        
        /// <summary>
        /// Constructor of the command interpreter
        /// </summary>
        /// <param name="conn">Connection instance</param>
        public Command(Connection conn) : base(conn) => conn.Logger.Debug("Command processor added");

        /// <summary>
        /// Reads incoming command requests and processes them. See <see cref="DuetAPI.Commands"/> namespace for a list
        /// of supported commands. The actual implementations can be found in <see cref="Commands"/>.
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Process()
        {
            DuetAPI.Commands.BaseCommand command = null;
            Type commandType;
            do
            {
                try
                {
                    // Read another command from the IPC connection
                    command = await Connection.ReceiveCommand();
                    commandType = command.GetType();

                    // Make sure it is actually supported and permitted
                    if (!SupportedCommands.Contains(commandType))
                    {
                        throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                    }
                    Connection.CheckPermissions(commandType);

                    // Execute it and send back the result
                    object result = await command.Invoke();
                    await Connection.SendResponse(result);
                }
                catch (SocketException)
                {
                    // Connection has been terminated
                    break;
                }
                catch (Exception e)
                {
                    // Send errors back to the client
                    if (!(e is OperationCanceledException))
                    {
                        if (e is UnauthorizedAccessException)
                        {
                            Connection.Logger.Error("Insufficient permissions to execute {0}", command.Command);
                        }
                        else
                        {
                            Connection.Logger.Error(e, "Failed to execute {0}", command.Command);
                        }
                    }
                    await Connection.SendResponse(e);
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }
    }
}
