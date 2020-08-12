using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Cancel a code in <see cref="Connection.InterceptionMode"/>
    /// </summary>
    [RequiredPermissions(SbcPermissions.CodeInterceptionReadWrite)]
    public class Cancel : Command { }
}
