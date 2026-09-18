using DuetAPI;
using DuetAPI.ObjectModel;
using System;
using System.Linq;

namespace DuetControlServer.Codes;

/// <summary>
/// How the last code on each channel ended, which meta G-code reads as <c>result</c>
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a macro check the code it just ran - <c>M98 P"probe.g"</c> followed by
/// <c>if result != 0</c>. It is per channel because that is the granularity a macro can reason about:
/// codes from other channels interleave with these, but not within one channel's own stack.
/// </para>
/// <para>
/// RepRapFirmware keeps the same value on its GCodeBuffer and sets it where it handles a reply, which
/// is the point this is set from as well. It starts at zero rather than "nothing yet", as RRF's does,
/// so that a macro reading it before anything has run sees success rather than an error
/// </para>
/// </remarks>
public sealed class LastCodeResult
{
    /// <summary>
    /// Value meta G-code sees for a code that succeeded
    /// </summary>
    public const int Ok = 0;

    /// <summary>
    /// Value meta G-code sees for a code that warned
    /// </summary>
    public const int Warning = 1;

    /// <summary>
    /// Value meta G-code sees for a code that failed
    /// </summary>
    public const int Error = 2;

    /// <summary>
    /// Value meta G-code sees when the user dismissed a message box
    /// </summary>
    /// <remarks>Not produced yet: M291 is not implemented</remarks>
    public const int MessageBoxCancelled = -1;

    private readonly int[] _results = [.. Enum.GetValues<CodeChannel>().Select(_ => Ok)];

    /// <summary>
    /// Record how a code ended
    /// </summary>
    /// <param name="channel">Channel it ran on</param>
    /// <param name="result">Result it produced, if any</param>
    public void Set(CodeChannel channel, Message? result)
    {
        _results[(int)channel] = result?.Type switch
        {
            MessageType.Warning => Warning,
            MessageType.Error => Error,
            _ => Ok
        };
    }

    /// <summary>
    /// Get how the last code on a channel ended
    /// </summary>
    /// <param name="channel">Channel to ask about</param>
    /// <returns>Result of the last code</returns>
    public int Get(CodeChannel channel) => _results[(int)channel];
}
