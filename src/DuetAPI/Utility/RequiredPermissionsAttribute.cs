using System;

namespace DuetAPI.Utility
{
    /// <summary>
    /// Attribute to define the permissions of each command
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RequiredPermissionsAttribute : Attribute
    {
        /// <summary>
        /// Required permissions for the given command
        /// </summary>
        private readonly SbcPermissions _requiredPermissions;

        /// <summary>
        /// Constructor for this attribute type
        /// </summary>
        /// <param name="requiredPermissions"></param>
        public RequiredPermissionsAttribute(SbcPermissions requiredPermissions) => _requiredPermissions = requiredPermissions;

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
