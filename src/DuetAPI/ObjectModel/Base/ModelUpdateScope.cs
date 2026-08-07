using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Extent of a JSON update telling which missing properties may be reset to null
/// </summary>
/// <remarks>
/// Firmware responses omit null values unless they are explicitly requested, so a missing property only
/// means "null" if the request asked for the category of values that property belongs to
/// </remarks>
[Flags]
public enum ModelUpdateScope
{
    /// <summary>
    /// Update holds changed values only, so nothing is reset
    /// </summary>
    Patch = 0,

    /// <summary>
    /// Update holds every live value, see <see cref="LiveAttribute"/>
    /// </summary>
    Live = 1,

    /// <summary>
    /// Update is a complete snapshot of the keys it contains
    /// </summary>
    Full = 2,

    /// <summary>
    /// Update holds verbose values, see <see cref="VerboseAttribute"/>
    /// </summary>
    Verbose = 4,

    /// <summary>
    /// Update holds values of properties flagged as obsolete
    /// </summary>
    Obsolete = 8
}
