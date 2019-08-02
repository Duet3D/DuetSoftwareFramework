using DuetAPI;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.WriteMessage"/> command
    /// </summary>
    public class WriteMessage : DuetAPI.Commands.WriteMessage
    {
        /// <summary>
        /// Write an arbitrary message
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override async Task Execute()
        {
            Message msg = new Message(Type, Content);
            if (LogMessage)
            {
                await Utility.Logger.Log(msg);
            }
            if (OutputMessage)
            {
                await Model.Provider.Output(msg);
            }
            if (!LogMessage && !OutputMessage)
            {
                Console.WriteLine(msg.ToString());
            }
        }
    }
}
