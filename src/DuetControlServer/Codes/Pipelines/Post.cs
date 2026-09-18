using DuetAPI.Commands;
using DuetAPI.Connection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines;

/// <summary>
/// Pipeline element for sending codes to code interceptors (post stage)
/// </summary>
/// <param name="channelProcessor">Channel processor</param>
/// <param name="codeProcessor">Code processor</param>
/// <param name="settings">Application settings</param>
/// <param name="lifetime">Application lifetime</param>
public sealed class Post(ChannelProcessor channelProcessor, CodeProcessor codeProcessor, IOptions<Settings> settings, IHostApplicationLifetime lifetime)
    : PipelineBase(PipelineStage.Post, channelProcessor, codeProcessor, lifetime, settings)
{
    /// <inheritdoc />
    public override async ValueTask ProcessCodeAsync(Commands.Code code)
    {
        if (!code.Flags.HasFlag(CodeFlags.IsPostProcessed))
        {
            try
            {
                bool resolved = await IPC.Processors.CodeInterception.InterceptAsync(code, InterceptionMode.Post);
                code.Flags |= CodeFlags.IsPostProcessed;
                if (!resolved)
                {
                    code.ResolveAsUnsupported();
                }
                await ChannelProcessor.WriteCodeAsync(code, PipelineStage.Executed);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                {
                    ChannelProcessor.Logger.LogError(e, "Failed to execute code {Code} on post stage", code);
                }
                CodeProcessor.CancelCode(code, e);
            }
        }
        else
        {
            code.ResolveAsUnsupported();
            await ChannelProcessor.WriteCodeAsync(code, PipelineStage.Executed);
        }
    }
}
