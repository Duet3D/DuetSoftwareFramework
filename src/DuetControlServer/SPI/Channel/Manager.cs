using DuetAPI;
using DuetControlServer.SPI.Communication.Shared;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.SPI.Channel
{
    /// <summary>
    /// Class used to manage access to channel processors
    /// </summary>
    public class Manager : IEnumerable<Processor>
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// List of different channels
        /// </summary>
        private readonly Processor[] _channels;

        /// <summary>
        /// Constructor of the channel store
        /// </summary>
        public Manager()
        {
            CodeChannel[] channels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

            _channels = new Processor[channels.Length];
            foreach (CodeChannel channel in channels)
            {
                this[channel] = new Processor(channel);
            }
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public async Task Diagnostics(StringBuilder builder)
        {
            foreach (Processor channel in _channels)
            {
                using (await channel.LockAsync())
                {
                    channel.Diagnostics(builder);
                }
            }
        }

        /// <summary>
        /// Index operator for easy access via a <see cref="CodeChannel"/> value
        /// </summary>
        /// <param name="channel">Channel to retrieve information about</param>
        /// <returns>Information about the code channel</returns>
        public Processor this[CodeChannel channel]
        {
            get => _channels[(int)channel];
            set => _channels[(int)channel] = value;
        }

        /// <summary>
        /// Get a channel that is currently idle in order to process a priority code
        /// </summary>
        /// <returns></returns>
        public async Task<CodeChannel> GetIdleChannel()
        {
            foreach (Processor channel in _channels)
            {
                using (await channel.LockAsync())
                {
                    if (channel.BufferedCodes.Count == 0)
                    {
                        return channel.Channel;
                    }
                }
            }

            _logger.Warn("Failed to find an idle channel, using fallback {0}", nameof(CodeChannel.Trigger));
            return CodeChannel.Trigger;
        }

        /// <summary>
        /// Process requests in the G-code channel processors
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task Run()
        {
            bool dataProcessed;
            do
            {
                dataProcessed = false;
                foreach (Channel.Processor channel in _channels)
                {
                    using (await channel.LockAsync())
                    {
                        if (!channel.IsBlocked)
                        {
                            if (channel.Run())
                            {
                                // Something could be processed
                                dataProcessed = true;
                            }
                            else
                            {
                                // Cannot do any more on this channel
                                channel.IsBlocked = true;
                            }
                        }
                    }
                }
            }
            while (dataProcessed);
        }

        /// <summary>
        /// Try to process a code reply
        /// </summary>
        /// <param name="flags">Message type flags</param>
        /// <param name="reply">Message content</param>
        /// <returns>Whether the reply could be handled</returns>
        public async Task<bool> HandleReply(MessageTypeFlags flags, string reply)
        {
            foreach (Processor channel in _channels)
            {
                MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)channel.Channel);
                if (flags.HasFlag(channelFlag))
                {
                    using (await channel.LockAsync())
                    {
                        return channel.HandleReply(flags, reply);
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Reset busy channels
        /// </summary>
        public void ResetBlockedChannels()
        {
            foreach (Processor channel in _channels)
            {
                channel.IsBlocked = false;
            }
        }

        /// <summary>
        /// Implementation of the GetEnumerator method
        /// </summary>
        /// <returns>Enumerator</returns>
        IEnumerator IEnumerable.GetEnumerator() => _channels.GetEnumerator();

        /// <summary>
        /// Implementation of the GetEnumerator method
        /// </summary>
        /// <returns>Enumerator</returns>
        IEnumerator<Processor> IEnumerable<Processor>.GetEnumerator() => ((IEnumerable<Processor>)_channels).GetEnumerator();
    }
}
