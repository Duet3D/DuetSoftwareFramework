using DuetAPI.Commands;
using DuetAPI.Connection;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Send incoming codes to preprocessors (pre stage)
    /// </summary>
    public class Post : PipelineStage
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipeline">Corresponding pipeline</param>
        public Post(Pipeline pipeline) : base(Codes.PipelineStage.Post, pipeline) { }

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
                    await Pipeline.WriteCodeAsync(code, resolved ? Codes.PipelineStage.Executed : Codes.PipelineStage.Firmware);
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Pipeline.Logger.Error(e, "Failed to execute code {0} on post stage", code);
                    }
                    Processor.CancelCode(code, e);
                }
            }
            else
            {
                await Pipeline.WriteCodeAsync(code, Codes.PipelineStage.Firmware);
            }
        }
    }
}
