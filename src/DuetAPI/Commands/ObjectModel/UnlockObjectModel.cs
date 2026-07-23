using DuetAPI.Utility;
using System;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Unlock the machine model after obtaining read/write access.
    /// This is mandatory after <see cref="LockObjectModel"/> has been invoked
    /// </summary>
    [Obsolete("This command will be removed in v3.7")]
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class UnlockObjectModel : Command { }
}
