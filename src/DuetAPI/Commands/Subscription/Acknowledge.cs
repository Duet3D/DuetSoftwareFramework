using DuetAPI.Connection;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Acknowledge a (partial) model update.
    /// </summary>
    /// <remarks>
    /// This command is only permitted in <see cref="ConnectionMode.Subscribe"/> mode
    /// </remarks>
    public class Acknowledge : Command { }
}
