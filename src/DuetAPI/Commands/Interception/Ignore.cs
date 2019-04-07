using DuetAPI.Connection;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Ignore the code to intercept and allow it to be processed without any modifications
    /// </summary>
    /// <remarks>
    /// This command is only permitted in <see cref="ConnectionMode.Intercept"/> mode
    /// </remarks>
    public class Ignore : Command { }
}