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
    public sealed class Command : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(GetFileInfo),
            typeof(ResolvePath),
            typeof(Code),
            typeof(EvaluateExpression),
            typeof(Flush),
            typeof(SimpleCode),
            typeof(WriteMessage),
            typeof(AddHttpEndpoint),
            typeof(RemoveHttpEndpoint),
            typeof(GetObjectModel),
            typeof(LockObjectModel),
            typeof(PatchObjectModel),
            typeof(SetObjectModel),
            typeof(SetUpdateStatus),
            typeof(SyncObjectModel),
            typeof(UnlockObjectModel),
            typeof(InstallPlugin),
            typeof(SetPluginData),
            typeof(StartPlugin),
            typeof(StopPlugin),
            typeof(UninstallPlugin),
            typeof(AddUserSession),
            typeof(RemoveUserSession)
        };

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static Command() => AddSupportedCommands(SupportedCommands);

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

                    // Shut down the socket if this was the last command
                    if (Program.CancellationToken.IsCancellationRequested)
                    {
                        Connection.Close();
                    }
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
                        else if (command != null)
                        {
                            Connection.Logger.Error(e, "Failed to execute {0}", command.Command);
                        }
                        else
                        {
                            Connection.Logger.Error(e, "Failed to execute command");
                        }
                    }
                    await Connection.SendResponse(e);
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }
    }
}
