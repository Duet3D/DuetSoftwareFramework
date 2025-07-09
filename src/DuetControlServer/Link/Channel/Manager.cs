using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Link.Channel;

/// <summary>
/// Class used to manage access to channel processors
/// </summary>
/// <param name="provider">Service provider to use for creating channel processors</param>
[DiagnosticsPriority(-6)]
public class Manager(IServiceProvider provider) : IAsyncDiagnostics, IEnumerable<Processor>
{
    /// <summary>
    /// List of different channels
    /// </summary>
    /// <remarks>
    /// This has to be initialized lazily to resolve circular dependencies
    /// </remarks>
    private readonly Lazy<Processor[]> _channels = new(() => [.. Inputs.ValidChannels.Select(channel => ActivatorUtilities.CreateInstance<Processor>(provider, channel))]);

    /// <summary>
    /// Last channel that started processing stuff
    /// </summary>
    private CodeChannel _nextChannel = CodeChannel.HTTP;

    /// <summary>
    /// Index operator for easy access via a <see cref="CodeChannel"/> value
    /// </summary>
    /// <param name="channel">Channel to retrieve information about</param>
    /// <returns>Information about the code channel</returns>
    public Processor this[CodeChannel channel]
    {
        get => _channels.Value[(int)channel];
        set => _channels.Value[(int)channel] = value;
    }

    /// <summary>
    /// Check if a code channel is waiting for acknowledgement
    /// </summary>
    /// <param name="channel">Channel to query</param>
    /// <returns>Whether the channel is awaiting acknowledgement</returns>
    public bool IsWaitingForAcknowledgment(CodeChannel channel) => _channels.Value[(int)channel].IsWaitingForAcknowledgment;

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        foreach (Processor channel in _channels.Value)
        {
            await channel.PrintDiagnosticsAsync(builder, cancellationToken);
        }
    }

    /// <summary>
    /// Process requests in the G-code channel processors
    /// </summary>
    public void Spin()
    {
        // Iterate over all the available channels
        bool overlapped = false;
        CodeChannel channel = _nextChannel;
        while (channel != _nextChannel || !overlapped)
        {
            Processor channelProcessor = this[channel];
            using (channelProcessor.Lock())
            {
                channelProcessor.Spin();
            }

            channel++;
            if (channel == CodeChannel.Unknown)
            {
                channel = CodeChannel.HTTP;
                overlapped = true;
            }
        }

        // Let the following code channel start next time, no channel is preferred
        _nextChannel++;
        if (_nextChannel == CodeChannel.Unknown)
        {
            _nextChannel = CodeChannel.HTTP;
        }
    }

    /// <summary>
    /// Try to process a code reply
    /// </summary>
    /// <param name="flags">Message type flags</param>
    /// <param name="reply">Message content</param>
    /// <returns>Whether the reply could be handled</returns>
    public bool HandleReply(MessageTypeFlags flags, string reply)
    {
        foreach (Processor channel in _channels.Value)
        {
            MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)channel.Channel);
            if (flags.HasFlag(channelFlag))
            {
                using (channel.Lock())
                {
                    return channel.HandleReply(flags, reply);
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Implementation of the GetEnumerator method
    /// </summary>
    /// <returns>Enumerator</returns>
    IEnumerator IEnumerable.GetEnumerator() => _channels.Value.GetEnumerator();

    /// <summary>
    /// Implementation of the GetEnumerator method
    /// </summary>
    /// <returns>Enumerator</returns>
    IEnumerator<Processor> IEnumerable<Processor>.GetEnumerator() => ((IEnumerable<Processor>)_channels.Value).GetEnumerator();
}
