using DuetAPI.Utility;
using System.Collections.Generic;

namespace DuetAPI.Commands;

/// <summary>
/// Query the current object model
/// </summary>
/// <seealso cref="ObjectModel.ObjectModel"/>
[RequiredPermissions(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
public partial class GetObjectModel : Command<ObjectModel.ObjectModel>
{
    /// <summary>
    /// Optional object model key paths to retrieve, e.g. "network.interfaces" or "move.axes"
    /// </summary>
    /// <remarks>
    /// If any key paths are given, the returned instance holds only the requested parts and every other property
    /// is left at its default value. There is no way to tell those apart from values that are genuinely unset
    /// </remarks>
    /// <seealso cref="QueryObjectModel.Key"/>
    public List<string> Filters { get; set; } = [];
}
