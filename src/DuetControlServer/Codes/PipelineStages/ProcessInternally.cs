using DuetAPI.Commands;
using DuetAPI.Connection;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Code stage where codes are processed internally (if possible)
    /// </summary>
    public class ProcessInternally : PipelineStage
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipeline">Corresponding pipeline</param>
        public ProcessInternally(Pipeline pipeline) : base(Codes.PipelineStage.ProcessInternally, pipeline) { }

        /// <summary>
        /// Process an incoming code
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
                    await Pipeline.WriteCodeAsync(code, resolved ? Codes.PipelineStage.Executed : Codes.PipelineStage.ProcessInternally);
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Pipeline.Logger.Error(e, "Failed to execute code {0} on internal processing stage", code);
                    }
                    Processor.CancelCode(code, e);
                }
            }
            else
            {
                IPC.Processors.CodeInterception.GetCodeBeingIntercepted(code.Connection, out InterceptionMode mode);
                await Pipeline.WriteCodeAsync(code, (mode != InterceptionMode.Post) ? Codes.PipelineStage.Post : Codes.PipelineStage.Firmware);
            }
        }
    }
}
