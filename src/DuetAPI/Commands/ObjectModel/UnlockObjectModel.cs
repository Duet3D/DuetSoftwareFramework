using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Unlock the machine model after obtaining read/write access.
    /// This is mandatory after <see cref="LockObjectModel"/> has been invoked
    /// </summary>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class UnlockObjectModel : Command { }
}
