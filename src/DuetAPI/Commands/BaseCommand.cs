using System;
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
        protected BaseCommand()
        {
            Command = GetType().UnderlyingSystemType.Name;
        }
        
        /// <summary>
        /// Name of the command.
        /// In the .NET library this is automatically set to the actual class name representing the command name.
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// The connection ID this command was received from. It is automatically overwritten by the control server
        /// once the full command has been deserialized. If this is 0, the command comes from an internal request.
        /// </summary>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Invokes the command implementation
        /// </summary>
        /// <returns>Result of the command</returns>
        public virtual Task<object> Invoke()
        {
            throw new NotImplementedException($"{Command} not implemented");
        }
    }
    
    /// <summary>
    /// Base class of commands that do not return a result
    /// </summary>
    public abstract class Command : BaseCommand
    {
        /// <summary>
        /// Reserved for the actual command implementation in the control server
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public virtual Task Execute()
        {
            throw new NotImplementedException($"{Command} not implemented");
        }

        /// <summary>
        /// Invokes the command implementation
        /// </summary>
        /// <returns>null</returns>
        public override async Task<object> Invoke()
        {
            await Execute();
            return null;
        }
    }
    
    /// <summary>
    /// Base class of a command that returns a result
    /// </summary>
    /// <typeparam name="T">Type of the command result</typeparam>
    public abstract class Command<T> : BaseCommand
    {
        /// <summary>
        /// Reserved for the actual command implementation in the control server
        /// </summary>
        /// <returns>Command result</returns>
        public virtual Task<T> Execute()
        {
            throw new NotImplementedException($"{Command}<{nameof(T)}> not implemented");
        }

        /// <summary>
        /// Invokes the command implementation
        /// </summary>
        /// <returns>Command result</returns>
        public override async Task<object> Invoke() => await Execute();
    }
}