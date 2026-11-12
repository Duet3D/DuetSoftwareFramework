using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetEmulation.Can;

/// <summary>
/// A board's standard reply to a request, put together from its fragments
/// </summary>
/// <param name="Fragments">The frames that carried it, in fragment order</param>
public sealed record StandardReply(IReadOnlyList<CanFrame> Fragments)
{
    // The bitfields ahead of the data words and text
    private const int HeaderLength = 4;

    /// <summary>The result, from the last fragment</summary>
    public CodeResult Result => Fragments[^1].AsStandardReply().ResultCode;

    /// <summary>The text of every fragment, joined</summary>
    public string Text => string.Concat(Fragments.Select(FragmentText));

    private static string FragmentText(CanFrame frame)
    {
        int start = HeaderLength + frame.AsStandardReply().NumWords * sizeof(uint);
        ReadOnlySpan<byte> text = frame.Data.AsSpan(Math.Min(start, frame.Data.Length));
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }
}
