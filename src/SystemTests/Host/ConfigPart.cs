using System;

namespace SystemTests.Host;

/// <summary>Where a <see cref="ConfigPart"/> goes in config.g</summary>
internal enum ConfigOrder
{
    /// <summary>In the order the parts were given</summary>
    InOrder,

    /// <summary>After every <see cref="InOrder"/> part, because selecting a geometry with M669 K would undo it</summary>
    AfterGeometry,

    /// <summary>Last, because configuring the geometry or the axes un-homes every axis</summary>
    Last
}

/// <summary>
/// One piece of a bench's config.g, with the files on the virtual SD card it depends on
/// </summary>
/// <remarks>
/// A plain string converts to a part that runs in order, so a scenario's own lines can be given
/// alongside the shared parts: <c>Bench.StartAsync(Machines.Xye, "M140 H0")</c>
/// </remarks>
internal class ConfigPart(string lines, ConfigOrder order = ConfigOrder.InOrder, Action<VirtualSd>? files = null)
{
    /// <summary>The G-code lines this part adds to config.g</summary>
    public string Lines { get; } = lines;

    /// <summary>Where the lines go in config.g</summary>
    public ConfigOrder Order { get; } = order;

    /// <summary>Writes the files the lines depend on, if any</summary>
    public Action<VirtualSd>? Files { get; } = files;

    public static implicit operator ConfigPart(string lines) => new(lines);

    public override string ToString() => Lines;
}
