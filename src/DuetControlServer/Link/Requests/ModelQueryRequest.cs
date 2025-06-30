using System.Threading.Tasks;

namespace DuetControlServer.Link;

/// <summary>
/// Object model query request
/// </summary>
/// <param name="key">Key to query</param>
/// <param name="flags">Query flags</param>
public class ModelQueryRequest(string key, string flags)
{
    /// <summary>
    /// Key to query
    /// </summary>
    public string Key = key;

    /// <summary>
    /// Flags to query
    /// </summary>
    public string Flags = flags;

    /// <summary>
    /// Whether the model query has been sent
    /// </summary>
    public bool QuerySent = false;

    /// <summary>
    /// Task to complete when the query has finished
    /// </summary>
    public TaskCompletionSource<byte[]> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
