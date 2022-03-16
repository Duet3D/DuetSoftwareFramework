using System;
using System.Collections.Generic;

namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// Enter subscription mode and receive either the full object model or parts of it after every update
    /// </summary>
    public class SubscribeInitMessage : ClientInitMessage
    {
        /// <summary>
        /// Creates a new init message instance
        /// </summary>
        public SubscribeInitMessage() => Mode = ConnectionMode.Subscribe;

        /// <summary>
        /// Type of the subscription
        /// </summary>
        public SubscriptionMode SubscriptionMode { get; set; }

        /// <summary>
        /// Optional code channel to receive messages from or null if only generic messages are supposed to be received
        /// </summary>
        public CodeChannel? Channel { get; set; }

        /// <summary>
        /// Optional filter path for <see cref="SubscriptionMode.Patch"/> mode
        /// </summary>
        /// <remarks>
        /// Multiple filters can be used on one connection and they have to be delimited by one of these charaters: ['|', ',', ' ', '\r', '\n']
        /// </remarks>
        /// <seealso cref="Filters"/>
        [Obsolete("Use Filters instead")]
        public string Filter { get; set; }

        /// <summary>
        /// Optional list of filter paths for <see cref="SubscriptionMode.Patch"/> mode
        /// </summary>
        /// <remarks>
        /// The style of a filter is similar to XPath. For example, if you want to monitor only the current heater temperatures,
        /// you can use the filter expression "heat/heaters[*]/current". Wildcards are supported either for full names or indices.
        /// To get updates for an entire namespace, the ** wildcard can be used (for example heat/** for everything heat-related),
        /// however it can be only used at the end of a filter expression
        /// </remarks>
        public List<string> Filters { get; set; }
    }
}