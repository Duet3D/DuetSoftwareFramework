using DuetAPI.Utility;

namespace DuetAPI.Commands
{
    /// <summary>
    /// Set an atomic property in the machine model. Make sure to acquire the read/write lock first!
    /// Returns true if the field could be updated
    /// </summary>
    /// <remarks>
    /// No third-party plugin should use this interface. It is solely intended for interal usage
    /// </remarks>
    /// <seealso cref="LockObjectModel"/>
    /// <seealso cref="UnlockObjectModel"/>
    [RequiredPermissions(SbcPermissions.ObjectModelReadWrite)]
    public class SetObjectModel : Command<bool>
    {
        /// <summary>
        /// Path to the property in the machine model
        /// </summary>
        /// <seealso cref="Connection.InitMessages.SubscribeInitMessage.Filter"/>
        public string PropertyPath { get; set; } = string.Empty;

        /// <summary>
        /// String representation of the JSON value to set
        /// </summary>
        public string Value { get; set; } = string.Empty;
    }
}
