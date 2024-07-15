using System;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Attribute to define the permissions of each command
    /// </summary>
    /// <remarks>
    /// Constructor for this attribute type
    /// </remarks>
    /// <param name="requiredPermissions"></param>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RequiredPermissionsAttribute(SbcPermissions requiredPermissions) : Attribute
    {
        /// <summary>
        /// Required permissions for the given command
        /// </summary>
        private readonly SbcPermissions _requiredPermissions = requiredPermissions;

        /// <summary>
        /// Check if the given permissions are sufficient
        /// </summary>
        /// <param name="permissions">Permissions to check</param>
        /// <returns>True if permission is granted</returns>
        public bool Check(SbcPermissions permissions)
        {
            foreach (Enum value in Enum.GetValues(typeof(SbcPermissions)))
            {
                if (_requiredPermissions.HasFlag(value) && permissions.HasFlag(value))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
