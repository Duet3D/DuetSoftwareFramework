using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Dummy stage for codes ready to be sent to the firmware.
/// This class is not used by the pipeline itself but indirectly from the SPI channel processor
/// </summary>
/// <seealso cref="Link.Channel.Processor"/>
/// <param name="channelProcessor">Channel processor</param>
/// <param name="codeProcessor">Code processor</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="settings">Application settings</param>
/// <param name="lifetime">Application lifetime</param>
public sealed class Firmware(
    ChannelProcessor channelProcessor,
    CodeProcessor codeProcessor,
    LinkInterface linkInterface,
    IHostApplicationLifetime lifetime,
    IOptions<Settings> settings) : PipelineBase(PipelineStage.Firmware, channelProcessor, codeProcessor, lifetime, settings)
{
    /// <summary>
    /// Wait for the pipeline stage to become idle
    /// </summary>
    /// <param name="flushAll">Flush everything</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public override Task<bool> FlushAsync(bool flushAll, CancellationToken cancellationToken = default)
    {
        return linkInterface.FlushAsync(ChannelProcessor.Channel, flushAll, cancellationToken);
    }

    /// <summary>
    /// Wait for the pipeline stage to become idle
    /// </summary>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public override Task<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        return linkInterface.FlushAsync(file, cancellationToken);
    }

    /// <summary>
    /// Wait for the pipeline stage to become idle
    /// </summary>
    /// <param name="code">Code waiting for the flush</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public override Task<bool> FlushAsync(Code code, CancellationToken cancellationToken = default)
    {
        return linkInterface.FlushAsync(code, cancellationToken);
    }

    /// <summary>
    /// Process an incoming code
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <returns>Asynchronous task</returns>
    public override Task ProcessCodeAsync(Code code) => Task.CompletedTask;
}
