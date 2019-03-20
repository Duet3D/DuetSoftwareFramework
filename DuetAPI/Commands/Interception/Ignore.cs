using DuetAPI.Connection;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Ignore the code to intercept and allow it to be processed without any modifications.
    /// This command is only permitted in Interception mode!
    /// </summary>
    /// <seealso cref="ConnectionMode.Intercept"/>
    public class Ignore : Command { }
}