using System;
using System.Collections.Generic;
using DuetEmulation.Boards;

namespace DuetEmulation;

/// <summary>What an emulated system is made of and how it runs</summary>
public sealed record SystemOptions
{
    /// <summary>The main board</summary>
    public required BoardType MainBoard { get; init; }

    /// <summary>The expansion boards, by CAN address</summary>
    public IReadOnlyDictionary<int, BoardType> ExpansionBoards { get; init; } = new Dictionary<int, BoardType>();

    /// <summary>Files on the main board's SD card, by path on the card ("sys/config.g")</summary>
    public IReadOnlyDictionary<string, byte[]> SdCardFiles { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>Where the run's logs, traces and images go; it is created, and emptied first</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Whether to run the Debug builds of the firmware (the presets with -debug)</summary>
    public bool Debug { get; init; } = Environment.GetEnvironmentVariable("DUET_EMULATION_DEBUG") is "1" or "true";

    /// <summary>
    /// How long each machine runs before the next one has a turn. A CAN frame reaches the other boards
    /// at the next turn, so this is the bus latency, but frames are timestamped with their time on the
    /// bus, so it does not disturb the boards' clock sync
    /// </summary>
    public TimeSpan Quantum { get; init; } = TimeSpan.FromMilliseconds(1);
}
