using DuetAPI;
using System;
using System.Collections;
using System.Collections.Generic;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Class used to hold internal information about all the code channels
    /// </summary>
    public class ChannelStore : IEnumerable<ChannelInformation>
    {
        private readonly ChannelInformation[] _channels;

        /// <summary>
        /// Constructor of the channel store
        /// </summary>
        public ChannelStore()
        {
            CodeChannel[] channels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

            _channels = new ChannelInformation[channels.Length];
            foreach (CodeChannel channel in channels)
            {
                this[channel] = new ChannelInformation(channel);
            }
        }

        /// <summary>
        /// Index operator for easy access via a CodeChannel value
        /// </summary>
        /// <param name="channel">Channel to retrieve information about</param>
        /// <returns>Information about the code channel</returns>
        public ChannelInformation this[CodeChannel channel]
        {
            get => _channels[(int)channel];
            set => _channels[(int)channel] = value;
        }

        /// <summary>
        /// Reset busy channels
        /// </summary>
        public void ResetBlockedChannels()
        {
            foreach (ChannelInformation channel in _channels)
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
        IEnumerator<ChannelInformation> IEnumerable<ChannelInformation>.GetEnumerator() => ((IEnumerable<ChannelInformation>)_channels).GetEnumerator();
    }
}
