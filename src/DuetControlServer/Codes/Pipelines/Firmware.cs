using DuetControlServer.Commands;
using DuetControlServer.Files;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Dummy stage for codes ready to be sent to the firmware.
    /// This class is not used by the pipeline itself but indirectly from the SPI channel processor
    /// </summary>
    /// <seealso cref="SPI.Channel.Processor"/>
    /// <param name="processor">Channel processor</param>
    public class Firmware(ChannelProcessor processor) : PipelineBase(PipelineStage.Firmware, processor)
    {
        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="flushAll">Flush everything</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync(bool flushAll, CancellationToken cancellationToken = default) => SPI.Interface.FlushAsync(Processor.Channel, flushAll, cancellationToken);

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default) => SPI.Interface.FlushAsync(file, cancellationToken);

        /// <summary>
        /// Wait for the pipeline stage to become idle
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public override Task<bool> FlushAsync(Code code, bool evaluateExpressions = true, bool evaluateAll = true, CancellationToken cancellationToken = default) => SPI.Interface.FlushAsync(code, evaluateExpressions, evaluateAll, cancellationToken);

        /// <summary>
        /// Process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override Task ProcessCodeAsync(Code code) => Task.CompletedTask;
    }
}
