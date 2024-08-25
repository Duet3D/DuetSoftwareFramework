using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Base class of commands that do not return a result
    /// </summary>
    public abstract class Command : BaseCommand
    {
        /// <summary>
        /// Reserved for the actual command implementation in the control server
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public virtual Task Execute() => throw new NotImplementedException($"{Command} not implemented");

        /// <summary>
        /// Invokes the command implementation
        /// </summary>
        /// <returns>null</returns>
        public override async Task<object?> Invoke()
        {
            await Execute();
            return null;
        }
    }

    /// <summary>
    /// Base class of a command that returns a result
    /// </summary>
    /// <typeparam name="T">Type of the command result</typeparam>
    [JsonDerivedType(typeof(Code))]
    public abstract class Command<T> : BaseCommand
    {
        /// <summary>
        /// Reserved for the actual command implementation in the control server
        /// </summary>
        /// <returns>Command result</returns>
        public virtual Task<T> Execute() => throw new NotImplementedException($"{Command}<{nameof(T)}> not implemented");

        /// <summary>
        /// Invokes the command implementation
        /// </summary>
        /// <returns>Command result</returns>
        public override async Task<object?> Invoke() => await Execute();
    }
}