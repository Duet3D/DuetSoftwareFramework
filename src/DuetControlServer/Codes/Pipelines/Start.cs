using DuetAPI.Commands;
using Nito.AsyncEx;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Initial scheduler for codes being started
    /// </summary>
    public class Start : PipelineStage
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipeline">Corresponding pipeline</param>
        public Start(PipelineChannel pipeline) : base(Codes.PipelineStage.Start, pipeline) { }

        /// <summary>
        /// Counter for unbuffered codes
        /// </summary>
        private AsyncCountdownEvent _unbufferedCodesCounter = new(0);

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
                    Pipeline.Logger.Debug("Starting code {0} (prioritized)", code);
                }
                else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    Pipeline.Logger.Debug("Starting code {0} (macro code)", code);
                }
                else if (SPI.Interface.IsWaitingForAcknowledgment(code.Channel))
                {
                    Pipeline.Logger.Debug("Starting code {0} (acknowledgment)", code);
                }
                else
                {
                    Pipeline.Logger.Debug("Starting code {0}", code);
                }

                // Code execution may begin, send it to the Pre stage
                await Pipeline.WriteCodeAsync(code, Codes.PipelineStage.Pre);
            }
            catch (Exception e)
            {
                Processor.CancelCode(code, e);
            }
        }
    }
}
