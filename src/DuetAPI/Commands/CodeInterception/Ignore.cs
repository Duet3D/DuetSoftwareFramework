using DuetAPI.Connection;
using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Ignore the code to intercept and allow it to be processed without any modifications
    /// </summary>
    /// <remarks>
    /// This command is only permitted in <see cref="ConnectionMode.Intercept"/> mode
    /// </remarks>
    [RequiredPermissions(SbcPermissions.CodeInterceptionRead | SbcPermissions.CodeInterceptionReadWrite)]
    public class Ignore : Command { }
}