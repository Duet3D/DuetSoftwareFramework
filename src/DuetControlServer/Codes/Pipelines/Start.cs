using DuetAPI.Commands;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
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
    /// <summary>
    /// Counter for unbuffered codes
    /// </summary>
    private readonly AsyncCountdownEvent _unbufferedCodesCounter = new(0);

    /// <summary>
    /// Process an incoming code
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ProcessCodeAsync(Commands.Code code)
    {
        try
        {
            // Wait for pending unbuffered codes to finish first unless we're dealing with a priority code
            if (!code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                await _unbufferedCodesCounter.WaitAsync(code.CancellationToken);
            }

            // Make sure other codes wait for this code to complete first if it is marked "Unbuffered"
            if (code.Flags.HasFlag(CodeFlags.Unbuffered))
            {
                _unbufferedCodesCounter.AddCount(1);
            }

            // Log it
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                ChannelProcessor.Logger.Debug("Starting code {0} (prioritized)", code);
            }
            else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                ChannelProcessor.Logger.Debug("Starting code {0} (macro code)", code);
            }
            else if (channelManager.IsWaitingForAcknowledgment(code.Channel))
            {
                ChannelProcessor.Logger.Debug("Starting code {0} (acknowledgment)", code);
            }
            else
            {
                ChannelProcessor.Logger.Debug("Starting code {0}", code);
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
