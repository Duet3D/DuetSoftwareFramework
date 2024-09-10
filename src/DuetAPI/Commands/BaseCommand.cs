using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Base class of a command.
    /// When an instance of this class is processed in the control server, the connection identifier of the channel it was received from is assigned.
    /// </summary>
    public class BaseCommand
    {
        /// <summary>
        /// Creates a new instance of the BaseCommand
        /// </summary>
        protected BaseCommand() => Command = GetType().UnderlyingSystemType.Name;

        /// <summary>
        /// Name of the command to execute
        /// </summary>
        [JsonPropertyOrder(-1)]
        public string Command { get; set; }

        /// <summary>
        /// Invokes the command implementation
        /// </summary>
        /// <returns>Result of the command</returns>
        public virtual Task<object?> Invoke() => throw new NotImplementedException($"{Command} not implemented");
    }
}