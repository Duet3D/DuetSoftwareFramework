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
        /// Process an incoming code
        /// </summary>
        /// <param name="code">Code to process</param>
        /// <returns>Asynchronous task</returns>
        public override Task ProcessCodeAsync(Commands.Code code) => null;
    }
}
