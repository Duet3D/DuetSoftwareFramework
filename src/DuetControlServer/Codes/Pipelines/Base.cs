using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using System.Threading.Channels;

namespace DuetControlServer.Codes.Pipelines
{
    /// <summary>
    /// Base class for a code pipeline
    /// </summary>
    public abstract class Base
    {
        /// <summary>
        /// Stage of this pipeline
        /// </summary>
        public Stage Stage { get; }

        /// <summary>
        /// Pending codes per channel
        /// </summary>
        protected static Channel<Code>[] _pendingCodes = new Channel<Code>[Inputs.Total];

        /// <summary>
        /// Constructor of this base class
        /// </summary>
        /// <param name="stage">Stage of this pipeline</param>
        public Base(Stage stage)
        {
            Stage = stage;
            for (int i = 0; i < Inputs.Total; i++)
            {
                _pendingCodes[i] = Channel.CreateBounded<Code>(new BoundedChannelOptions(Settings.MaxCodesPerInput)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
            }
        }
    }
}
