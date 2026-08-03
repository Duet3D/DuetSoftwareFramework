using DuetAPI.Commands;
using DuetAPI.Connection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Code stage where codes are processed internally (if possible)
/// </summary>
/// <param name="channelProcessor">Channel processor</param>
/// <param name="codeProcessor">Code processor</param>
/// <param name="lifetime">Application lifetime</param>
/// <param name="settings">Application settings</param>
public sealed class ProcessInternally(ChannelProcessor channelProcessor, CodeProcessor codeProcessor, IOptions<Settings> settings, IHostApplicationLifetime lifetime)
    : PipelineBase(PipelineStage.ProcessInternally, channelProcessor, codeProcessor, lifetime, settings)
{
    /// <inheritdoc />
    public override async ValueTask ProcessCodeAsync(Commands.Code code)
    {
        if (!code.Flags.HasFlag(CodeFlags.IsInternallyProcessed))
        {
            try
            {
                bool resolved = await code.ProcessInternally();
                code.Flags |= CodeFlags.IsInternallyProcessed;
                await ChannelProcessor.WriteCodeAsync(code, resolved ? PipelineStage.Executed : PipelineStage.Post);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                {
                    ChannelProcessor.Logger.LogError(e, "Failed to execute code {Code} on internal processing stage", code);
                }
                CodeProcessor.CancelCode(code, e);
            }
        }
        else
        {
            IPC.Processors.CodeInterception.GetCodeBeingIntercepted(code.Connection, out InterceptionMode mode);
            await ChannelProcessor.WriteCodeAsync(code, (mode != InterceptionMode.Post) ? PipelineStage.Post : PipelineStage.Executed);
        }
    }
}
