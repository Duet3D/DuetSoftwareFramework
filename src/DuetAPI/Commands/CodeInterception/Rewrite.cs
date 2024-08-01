using DuetAPI.Connection;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Rewrite the code being intercepted. This can be used to modify the code before it is executed.
    /// </summary>
    /// <remarks>
    /// This command is only permitted in <see cref="ConnectionMode.Intercept"/> mode
    /// </remarks>
    [RequiredPermissions(SbcPermissions.CodeInterceptionReadWrite)]
    public class Rewrite : Command
    {
        /// <summary>
        /// Type of the resolving message
        /// </summary>
        public Code Code { get; set; } = new();
    }
}