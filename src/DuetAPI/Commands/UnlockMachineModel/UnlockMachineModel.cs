namespace DuetAPI.Commands
{
    /// <summary>
    /// Unlock the machine model after obtaining read/write access.
    /// This is mandatory after <see cref="LockMachineModel"/> has been invoked
    /// </summary>
    public class UnlockMachineModel : Command { }
}
