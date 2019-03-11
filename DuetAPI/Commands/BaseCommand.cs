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
        /// once the full command has been deserialized.
        /// </summary>
        public int SourceConnection { get; set; }

        /// <summary>
        /// Reserved for internal use in the control server
        /// </summary>
        /// <returns>Command result (if any)</returns>
        public virtual Task<object> Execute()
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
        /// Reserved for internal use in the control server
        /// </summary>
        /// <returns>null</returns>
        public sealed override async Task<object> Execute()
        {
            await Run();
            return null;
        }

        /// <summary>
        /// Reserved for internal use in the control server. This is invoked by <see cref="Execute"/>
        /// </summary>
        protected virtual Task Run()
        {
            throw new NotImplementedException($"{Command} not implemented");
        }
    }
    
    /// <summary>
    /// Base class of a command that return a result
    /// </summary>
    /// <typeparam name="T">Type of the command result</typeparam>
    public abstract class Command<T> : BaseCommand
    {
        /// <summary>
        /// Reserved for internal use in the control server
        /// </summary>
        /// <returns>Command result</returns>
        public sealed override async Task<object> Execute() => await Run();

        /// <summary>
        /// Reserved for internal use in the control server. This is invoked by <see cref="Execute"/>
        /// </summary>
        /// <returns>Command result</returns>
        protected virtual Task<T> Run()
        {
            throw new NotImplementedException($"{Command}<{nameof(T)}> not implemented");
        }
    }
}