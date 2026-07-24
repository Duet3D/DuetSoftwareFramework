using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Interface for command classes that can serialize their result directly to UTF-8 JSON,
/// avoiding intermediate copies of the data being returned
/// </summary>
public interface IRawJsonCommand
{
    /// <summary>
    /// Execute the command and serialize its result to UTF-8 JSON
    /// </summary>
    /// <param name="destination">Buffer writer to write the serialized result to</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    ValueTask ExecuteRawJsonAsync(IBufferWriter<byte> destination, CancellationToken cancellationToken = default);
}
