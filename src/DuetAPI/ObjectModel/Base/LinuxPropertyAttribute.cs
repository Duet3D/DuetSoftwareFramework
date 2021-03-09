using System;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Attribute used to mark properties that are overridden by the control server
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class LinuxPropertyAttribute : Attribute { }
}
