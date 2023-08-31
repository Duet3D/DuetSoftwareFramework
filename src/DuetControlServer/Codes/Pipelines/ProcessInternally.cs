using DuetAPI.Commands;
using DuetAPI.Connection;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Code stage where codes are processed internally (if possible)
    /// </summary>
    public class ProcessInternally : PipelineBase
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="processor">Chnanel processor</param>
        public ProcessInternally(ChannelProcessor processor) : base(PipelineStage.ProcessInternally, processor) { }

        /// <summary>
        /// Try to process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override async Task ProcessCodeAsync(Commands.Code code)
        {
            if (!code.Flags.HasFlag(CodeFlags.IsInternallyProcessed))
            {
                try
                {
                    bool resolved = await code.ProcessInternally();
                    code.Flags |= CodeFlags.IsInternallyProcessed;
                    await Processor.WriteCodeAsync(code, resolved ? PipelineStage.Executed : PipelineStage.Post);
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Processor.Logger.Error(e, "Failed to execute code {0} on internal processing stage", code);
                    }
                    Codes.Processor.CancelCode(code, e);
                }
            }
            else
            {
                IPC.Processors.CodeInterception.GetCodeBeingIntercepted(code.Connection, out InterceptionMode mode);
                await Processor.WriteCodeAsync(code, (mode != InterceptionMode.Post) ? PipelineStage.Post : PipelineStage.Firmware);
            }
        }
    }
}
