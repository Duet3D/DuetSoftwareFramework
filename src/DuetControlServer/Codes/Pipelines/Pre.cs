using DuetAPI.Commands;
using DuetAPI.Connection;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Pipeline element for sending codes to code interceptors (pre stage)
    /// </summary>
    public class Pre : PipelineBase
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="processor">Channel processor</param>
        public Pre(ChannelProcessor processor) : base(PipelineStage.Pre, processor) { }

        /// <summary>
        /// Process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override async Task ProcessCodeAsync(Commands.Code code)
        {
            if (!code.Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                try
                {
                    bool resolved = await IPC.Processors.CodeInterception.Intercept(code, InterceptionMode.Pre);
                    code.Flags |= CodeFlags.IsPreProcessed;
                    await Processor.WriteCodeAsync(code, resolved ? PipelineStage.Executed : PipelineStage.ProcessInternally);
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Processor.Logger.Error(e, "Failed to execute code {0} on pre stage", code);
                    }
                    Codes.Processor.CancelCode(code, e);
                }
            }
            else
            {
                await Processor.WriteCodeAsync(code, PipelineStage.ProcessInternally);
            }
        }
    }
}
