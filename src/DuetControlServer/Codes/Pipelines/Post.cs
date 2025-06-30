using DuetAPI.Commands;
using DuetAPI.Connection;
using Microsoft.Extensions.Hosting;
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
    /// <summary>
    /// Process an incoming code
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <returns>Asynchronous task</returns>
    public override async Task ProcessCodeAsync(Commands.Code code)
    {
        if (!code.Flags.HasFlag(CodeFlags.IsPostProcessed))
        {
            try
            {
                bool resolved = await IPC.Processors.CodeInterception.InterceptAsync(code, InterceptionMode.Post);
                code.Flags |= CodeFlags.IsPostProcessed;
                await ChannelProcessor.WriteCodeAsync(code, resolved ? PipelineStage.Executed : PipelineStage.Firmware);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException)
                {
                    ChannelProcessor.Logger.Error(e, "Failed to execute code {0} on post stage", code);
                }
                CodeProcessor.CancelCode(code, e);
            }
        }
        else
        {
            await ChannelProcessor.WriteCodeAsync(code, PipelineStage.Firmware);
        }
    }
}
