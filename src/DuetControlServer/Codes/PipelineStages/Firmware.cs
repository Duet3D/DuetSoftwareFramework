using DuetControlServer.Commands;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.PipelineStages
{
    /// <summary>
    /// Dummy stage for codes ready to be sent to the firmware.
    /// This class is not used by the pipeline itself but indirectly from the SPI channel processor
    /// </summary>
    /// <seealso cref="SPI.Channel.Processor"/>
    public class Firmware : PipelineStage
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        /// <param name="pipeline">Corresponding pipeline</param>
        public Firmware(Pipeline pipeline) : base(Codes.PipelineStage.Firmware, pipeline) { }

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
        public override Task ProcessCodeAsync(Commands.Code code) => null;
    }
}
