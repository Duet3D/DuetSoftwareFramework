using DuetAPI.Commands;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Initial pipeline element for codes being started
    /// </summary>
    /// <param name="processor">Channel processor</param>
    public class Start(ChannelProcessor processor) : PipelineBase(PipelineStage.Start, processor)
    {
        /// <summary>
        /// Process an incoming code
        /// </summary>
        /// <param name="stackItem">Stack item holding the code</param>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override async Task ProcessCodeAsync(PipelineStackItem stackItem, Commands.Code code)
        {
            try
            {
                // Wait for pending unbuffered codes of this stack level to finish first unless we're dealing with a priority code.
                // Deeper levels must not wait because their codes are executed first and the unbuffered code may be waiting for them
                if (!code.Flags.HasFlag(CodeFlags.IsPrioritized))
                {
                    await stackItem.UnbufferedCodesCounter.WaitAsync(code.CancellationToken);
                }

                // Make sure other codes wait for this code to complete first if it is marked "Unbuffered"
                if (code.Flags.HasFlag(CodeFlags.Unbuffered))
                {
                    stackItem.UnbufferedCodesCounter.AddCount(1);
                    _ = code.Task.ContinueWith(_ => stackItem.UnbufferedCodesCounter.Signal());
                }

                // Log it
                if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
                {
                    Processor.Logger.Debug("Starting code {0} (prioritized)", code);
                }
                else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    Processor.Logger.Debug("Starting code {0} (macro code)", code);
                }
                else if (SPI.Interface.IsWaitingForAcknowledgment(code.Channel))
                {
                    Processor.Logger.Debug("Starting code {0} (acknowledgment)", code);
                }
                else
                {
                    Processor.Logger.Debug("Starting code {0}", code);
                }

                // Code execution may begin, send it to the Pre stage
                await Processor.WriteCodeAsync(code, PipelineStage.Pre);
            }
            catch (Exception e)
            {
                Codes.Processor.CancelCode(code, e);
            }
        }
    }
}
