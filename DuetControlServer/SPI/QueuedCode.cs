using DuetAPI;
using DuetControlServer.Commands;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Struct that represents a queued code item
    /// </summary>
    public struct QueuedCode
    {
        /// <summary>
        /// Code item to execute
        /// </summary>
        public Code Code;

        /// <summary>
        /// Task source to resolve when the code has finished
        /// </summary>
        public TaskCompletionSource<Message> Task;
    }
}
