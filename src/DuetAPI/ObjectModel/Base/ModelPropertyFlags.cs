using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Attributes of an object model property as seen by the generated property accessors
/// </summary>
[Flags]
public enum ModelPropertyFlags
{
    /// <summary>
    /// No attributes apply to this property
    /// </summary>
    None = 0,

    /// <summary>
    /// Property can be assigned a new value
    /// </summary>
    HasSetter = 1,

    /// <summary>
    /// Property is only available in SBC mode, see <see cref="SbcPropertyAttribute"/>
    /// </summary>
    SbcProperty = 2,

    /// <summary>
    /// Property is updated live, see <see cref="LiveAttribute"/>
    /// </summary>
    Live = 4,

    /// <summary>
    /// Property is only reported in verbose responses, see <see cref="VerboseAttribute"/>
    /// </summary>
    Verbose = 8,

    /// <summary>
    /// Property is obsolete and only reported on demand
    /// </summary>
    Obsolete = 16
}
