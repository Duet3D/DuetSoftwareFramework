using DuetAPI.Commands;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Initial pipeline element for codes being started
/// </summary>
/// <param name="channelManager">Channel manager</param>
/// <param name="channelProcessor">Channel processor</param>
/// <param name="codeProcessor">Code processor</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="settings">Settings</param>
public sealed class Start(Manager channelManager, ChannelProcessor channelProcessor, CodeProcessor codeProcessor, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
    : PipelineBase(PipelineStage.Start, channelProcessor, codeProcessor, lifetime, settings)
{
    /// <inheritdoc />
    public override async ValueTask ProcessCodeAsync(PipelineStackItem stackItem, Commands.Code code)
    {
        try
        {
            // Wait for pending unbuffered codes of this stack level to finish first unless we're dealing with a priority code.
            // Deeper levels must not wait because their codes are executed first and the unbuffered code may be waiting for them
            if (!code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                await stackItem.UnbufferedCodesCounter.WaitAsync(code.CancellationToken);
            }

            // Make sure other codes wait for this code to complete first if it is marked "Unbuffered"
            if (code.Flags.HasFlag(CodeFlags.Unbuffered))
            {
                stackItem.UnbufferedCodesCounter.AddCount(1);
                _ = code.Task.ContinueWith(_ => stackItem.UnbufferedCodesCounter.Signal());
            }

            // Log it
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code} (prioritized)", code);
            }
            else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code} (macro code)", code);
            }
            else if (channelManager.IsWaitingForAcknowledgment(code.Channel))
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code} (acknowledgment)", code);
            }
            else
            {
                ChannelProcessor.Logger.LogDebug("Starting code {Code}", code);
            }

            // Code execution may begin, send it to the Pre stage
            await ChannelProcessor.WriteCodeAsync(code, PipelineStage.Pre);
        }
        catch (Exception e)
        {
            CodeProcessor.CancelCode(code, e);
        }
    }
}
