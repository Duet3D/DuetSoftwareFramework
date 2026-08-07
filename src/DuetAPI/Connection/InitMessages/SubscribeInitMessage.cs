using System.Collections.Generic;

namespace DuetAPI.Connection.InitMessages;

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
    /// Optional list of filter paths for <see cref="SubscriptionMode.Patch"/> mode
    /// </summary>
    /// <remarks>
    /// The style of a filter is similar to XPath. For example, if you want to monitor only the current heater temperatures,
    /// you can use the filter expression "heat/heaters[*]/current". Wildcards are supported either for full names or indices.
    /// To get updates for an entire namespace, the ** wildcard can be used (for example heat/** for everything heat-related),
    /// however it can be only used at the end of a filter expression
    /// </remarks>
    public List<string> Filters { get; set; } = [];

    /// <summary>
    /// Whether object model fields flagged as verbose are required
    /// </summary>
    /// <remarks>
    /// Verbose fields are read once when DCS starts up and then only while at least one subscriber asks for
    /// them, so a client that displays them has to set this for the lifetime of its connection
    /// </remarks>
    public bool Verbose { get; set; }

    /// <summary>
    /// Whether object model fields flagged as obsolete are required
    /// </summary>
    /// <remarks>
    /// Handled like <see cref="Verbose"/>: read once when DCS starts up and then only while at least one
    /// subscriber asks for them. Only of interest to clients that still read deprecated fields
    /// </remarks>
    public bool Obsolete { get; set; }
}
