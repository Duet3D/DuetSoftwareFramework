using DuetControlServer.Commands;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Dummy stage for codes ready to be sent to the firmware.
    /// This class is not used by the pipeline itself but indirectly from the SPI channel processor
    /// </summary>
    /// <seealso cref="SPI.Channel.Processor"/>
    public class Firmware : PipelineBase
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="processor">Channel processor</param>
        public Firmware(ChannelProcessor processor) : base(PipelineStage.Firmware, processor) { }

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync()
        {
            return SPI.Interface.FlushAsync(Processor.Channel);
        }

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync(Code code, bool evaluateExpressions = true, bool evaluateAll = true)
        {
            return SPI.Interface.FlushAsync(code, evaluateExpressions, evaluateAll);
        }

        /// <summary>
        /// Process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override Task ProcessCodeAsync(Code code) => Task.CompletedTask;
    }
}
