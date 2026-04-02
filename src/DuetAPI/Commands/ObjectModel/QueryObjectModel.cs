using System.Text.Json;
using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Query the object model using a key and flags, returning a formatted JSON response
/// compatible with the M409 response format without going through the code execution pipeline
/// </summary>
[RequiredPermissions(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite)]
public partial class QueryObjectModel : Command<JsonElement>
{
    /// <summary>
    /// Object model key path to query (e.g. "heat", "move.axes", "" for root)
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// RRF-compatible flags string controlling response content:
    /// 'f' = only include live (frequently changing) properties,
    /// 'n' = include null values,
    /// 'v' = include verbose properties,
    /// 'o' = include obsolete properties,
    /// 'a' followed by digits = array start index,
    /// 'd' followed by digits = max depth
    /// </summary>
    public string Flags { get; set; } = string.Empty;
}
