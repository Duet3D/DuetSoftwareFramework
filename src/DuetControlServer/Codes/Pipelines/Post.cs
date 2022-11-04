using DuetAPI.Commands;
using DuetAPI.Connection;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Pipeline element for sending codes to code interceptors (post stage)
    /// </summary>
    public class Post : PipelineBase
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="processor">Channel processor</param>
        public Post(ChannelProcessor processor) : base(PipelineStage.Post, processor) { }

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
                    bool resolved = await IPC.Processors.CodeInterception.Intercept(code, InterceptionMode.Post);
                    code.Flags |= CodeFlags.IsPostProcessed;
                    await Processor.WriteCodeAsync(code, resolved ? PipelineStage.Executed : PipelineStage.Firmware);
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Processor.Logger.Error(e, "Failed to execute code {0} on post stage", code);
                    }
                    Codes.Processor.CancelCode(code, e);
                }
            }
            else
            {
                await Processor.WriteCodeAsync(code, PipelineStage.Firmware);
            }
        }
    }
}
