using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetControlServer.Link;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Pipeline element for sending codes to code interceptors (pre stage)
/// </summary>
/// <param name="channelProcessor">Channel processor</param>
/// <param name="codeProcessor">Code processor</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="settings">Application settings</param>
public sealed class Pre(ChannelProcessor channelProcessor, CodeProcessor codeProcessor, LinkInterface linkInterface, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
    : PipelineBase(PipelineStage.Pre, channelProcessor, codeProcessor, lifetime, settings)
{
    /// <inheritdoc />
    public override async Task ProcessCodeAsync(Commands.Code code)
    {
        if (!code.Flags.HasFlag(CodeFlags.IsPreProcessed))
        {
            try
            {
                bool resolved = await IPC.Processors.CodeInterception.InterceptAsync(code, InterceptionMode.Pre);
                code.Flags |= CodeFlags.IsPreProcessed;
#if false // TODO: do we need to do anything now RRF is removed?
                if (resolved)
                {
                    await linkInterface.SetLastCodeResultAsync(code);
                }
#endif
                await ChannelProcessor.WriteCodeAsync(code, resolved ? PipelineStage.Executed : PipelineStage.ProcessInternally);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                {
                    ChannelProcessor.Logger.LogError(e, "Failed to execute code {Code} on pre stage", code);
                }
                CodeProcessor.CancelCode(code, e);
            }
        }
        else
        {
            await ChannelProcessor.WriteCodeAsync(code, PipelineStage.ProcessInternally);
        }
    }
}
