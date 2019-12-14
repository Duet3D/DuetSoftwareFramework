namespace DuetAPI.Commands
{
    /// <summary>
    /// Set an atomic property in the machine model. Make sure to acquire the read/write lock first!
    /// Returns true if the field could be updated
    /// </summary>
    /// <seealso cref="LockMachineModel"/>
    /// <seealso cref="UnlockMachineModel"/>
    public class SetMachineModel : Command<bool>
    {
        /// <summary>
        /// Path to the property in the machine model
        /// </summary>
        /// <seealso cref="Connection.InitMessages.SubscribeInitMessage.Filter"/>
        public string PropertyPath { get; set; }

        /// <summary>
        /// String representation of the value to set
        /// </summary>
        public string Value { get; set; }
    }
}
