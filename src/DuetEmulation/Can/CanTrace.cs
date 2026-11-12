using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetEmulation.Traces;

namespace DuetEmulation.Can;

/// <summary>
/// The frames on the emulated CAN bus so far, read from the file the bus logger writes
/// (<c>Renode/Peripherals/Common/CanBusLogger.cs</c>): one JSON object per line, in the order the hub
/// saw them. Reading the trace does not advance emulated time
/// </summary>
/// <remarks>
/// A mark divides the frames a test has already looked at from those it has not: the queries consider
/// only the frames sent since the mark, so a test sets it before the code it is checking
/// </remarks>
public sealed class CanTrace(string path)
{
    private readonly Lock _lock = new();
    private readonly JsonLinesFile _file = new(path);
    private readonly List<CanFrame> _frames = [];
    private int _mark;

    /// <summary>The file the bus logger writes</summary>
    public string Path => _file.Path;

    /// <summary>Every frame recorded so far</summary>
    public IReadOnlyList<CanFrame> All
    {
        get
        {
            lock (_lock)
            {
                Update();
                return [.. _frames];
            }
        }
    }

    /// <summary>The frames recorded since the mark</summary>
    public IReadOnlyList<CanFrame> SinceMark
    {
        get
        {
            lock (_lock)
            {
                Update();
                return _frames[_mark..];
            }
        }
    }

    /// <summary>Make the queries ignore the frames recorded so far</summary>
    public void SetMark()
    {
        lock (_lock)
        {
            Update();
            _mark = _frames.Count;
        }
    }

    /// <summary>The frames since the mark that match</summary>
    /// <param name="type">Message type, or null for any</param>
    /// <param name="source">Sending board, or null for any</param>
    /// <param name="destination">Receiving board, or null for any</param>
    /// <param name="response">Whether a response is wanted, a request, or null for either</param>
    /// <param name="where">Any further condition</param>
    public IReadOnlyList<CanFrame> Find(CanMessageType? type = null, byte? source = null, byte? destination = null,
                                        bool? response = null, Func<CanFrame, bool>? where = null) =>
        [.. SinceMark.Where(f => (type is null || f.MessageType == type) && (source is null || f.Source == source) &&
                                 (destination is null || f.Destination == destination) &&
                                 (response is null || f.IsResponse == response) && (where is null || where(f)))];

    /// <summary>The messages of type <typeparamref name="T"/> since the mark that match</summary>
    /// <param name="source">Sending board, or null for any</param>
    /// <param name="destination">Receiving board, or null for any</param>
    /// <param name="response">Whether a response is wanted, a request, or null for either</param>
    public IReadOnlyList<CanFrame> Find<T>(byte? source = null, byte? destination = null, bool? response = null)
        where T : struct, ICanMessage<T> =>
        Find(T.MessageType, source, destination, response);

    /// <summary>
    /// The standard reply to a request, once every fragment of it has been recorded, or null until then
    /// </summary>
    public StandardReply? ReplyTo(CanFrame request)
    {
        List<CanFrame> fragments = [.. All
            .Where(f => f.Is<CanMessageStandardReply>() && f.IsResponse && f.Source == request.Destination &&
                        f.Destination == request.Source && f.RequestId == request.RequestId)
            .OrderBy(f => f.AsStandardReply().FragmentNumber)];
        return fragments.Any(f => !f.AsStandardReply().MoreFollows) ? new StandardReply(fragments) : null;
    }

    /// <summary>The frames as text, one per line</summary>
    public static string Dump(IEnumerable<CanFrame> frames) => string.Join('\n', frames);

    private void Update()
    {
        foreach (string line in _file.ReadNewLines())
        {
            _frames.Add(Parse(line));
        }
    }

    /// <summary>Parse one line the bus logger wrote</summary>
    internal static CanFrame Parse(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement record = document.RootElement;
        return new CanFrame(
            TimeSpan.FromMicroseconds(record.GetProperty("t").GetDouble()),
            record.GetProperty("machine").GetString() ?? string.Empty,
            record.GetProperty("id").GetUInt32(),
            Convert.FromHexString(record.GetProperty("data").GetString() ?? string.Empty),
            record.GetProperty("fd").GetBoolean(),
            record.GetProperty("brs").GetBoolean());
    }

    /// <summary>Format a frame as the bus logger does, for tests of the trace itself</summary>
    internal static string Format(CanFrame frame) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{{\"t\":{frame.Time.TotalMicroseconds:F3},\"machine\":\"{frame.Machine}\",\"id\":{frame.Id},\"ext\":true," +
                      $"\"fd\":{(frame.IsFd ? "true" : "false")},\"brs\":{(frame.BitRateSwitch ? "true" : "false")},\"rtr\":false," +
                      $"\"data\":\"{Convert.ToHexStringLower(frame.Data)}\"}}");

    /// <summary>Build the CAN id of a message, for tests of the trace itself</summary>
    internal static uint MakeId(CanMessageType type, byte source, byte destination, bool response) =>
        ((uint)type << (int)CanId.MessageTypeShift) | ((uint)source << (int)CanId.SrcAddressShift) |
        ((uint)destination << (int)CanId.DstAddressShift) | (response ? CanId.ResponseBit : 0);
}
