using System.Threading.Tasks;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Flush"/> command
    /// </summary>
    public class Flush : DuetAPI.Commands.Flush
    {
        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public override Task Execute() => SPI.Interface.Flush(Channel);
    }
}
