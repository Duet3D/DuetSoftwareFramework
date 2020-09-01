using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Lock the object model for read/write access.
    /// This may be used to update the machine model and to change array items
    /// </summary>
    /// <seealso cref="UnlockObjectModel"/>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class LockObjectModel : Command { }
}
