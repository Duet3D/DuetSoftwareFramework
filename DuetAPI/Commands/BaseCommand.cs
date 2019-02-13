using System;
using System.Threading.Tasks;

namespace DuetAPI.Commands
{
    // Base class of a command
    public abstract class BaseCommand : JsonObject
    {
        public string Command { get; }

        public BaseCommand()
        {
            Command = GetType().UnderlyingSystemType.Name;
        }

        public virtual Task<object> Execute()
        {
            throw new NotImplementedException();
        }
    }

    // Base class of a command that is expected to return a value of type T
    public class Command<T> : BaseCommand
    {
        public virtual new Task<T> Execute()
        {
            throw new NotImplementedException();
        }
    }

    // Base class of a command that does not return a value (i.e. null)
    public class EmptyResponseCommand : BaseCommand
    {
        public override Task<object> Execute() => null;
    }
}