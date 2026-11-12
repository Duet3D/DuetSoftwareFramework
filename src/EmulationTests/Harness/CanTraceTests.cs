using System;
using System.IO;
using System.Linq;
using System.Text;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetEmulation.Can;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// The CAN trace the harness reads from the bus logger's file, without an emulator
/// </summary>
[TestFixture]
public class CanTraceTests
{
    private string _path = null!;

    [SetUp]
    public void SetUp() => _path = Path.Combine(Path.GetTempPath(), $"can-trace-{Guid.NewGuid():N}.jsonl");

    [TearDown]
    public void TearDown() => File.Delete(_path);

    private static CanFrame Frame(double seconds, CanMessageType type, byte source, byte destination, bool response, byte[] data) =>
        new(TimeSpan.FromSeconds(seconds), source == 0 ? "main" : $"exp{source}", CanTrace.MakeId(type, source, destination, response),
            data, true, true);

    private void Append(params CanFrame[] frames)
    {
        foreach (CanFrame frame in frames)
        {
            File.AppendAllText(_path, CanTrace.Format(frame) + "\n");
        }
    }

    private static byte[] Reply(ushort requestId, CodeResult result, int fragment, bool moreFollows, string text)
    {
        CanMessageStandardReply reply = default;
        reply.RequestId = requestId;
        reply.ResultCode = result;
        reply.FragmentNumber = (byte)fragment;
        reply.MoreFollows = moreFollows;
        reply.TextString = text;
        byte[] data = new byte[reply.GetActualDataLength((uint)Encoding.UTF8.GetByteCount(text))];
        CanMessageSerializer.Serialize(reply, data);
        return data;
    }

    [Test]
    public void AFrameDecodesItsIdAndPayload()
    {
        Append(Frame(1.5, CanMessageType.StandardReply, 1, 0, true, Reply(0x123, CodeResult.Error, 0, false, "Driver 1.0 is not present")));

        CanFrame frame = new CanTrace(_path).All[0];
        Assert.Multiple(() =>
        {
            Assert.That(frame.Time, Is.EqualTo(TimeSpan.FromSeconds(1.5)));
            Assert.That(frame.MessageType, Is.EqualTo(CanMessageType.StandardReply));
            Assert.That(frame.Source, Is.EqualTo(1));
            Assert.That(frame.Destination, Is.EqualTo(0));
            Assert.That(frame.IsResponse, Is.True);
            Assert.That(frame.RequestId, Is.EqualTo(0x123));
            Assert.That(frame.AsStandardReply().ResultCode, Is.EqualTo(CodeResult.Error));
            Assert.That(() => frame.As<CanMessageM569>(), Throws.InvalidOperationException, "a frame is only its own type");
        });
    }

    [Test]
    public void QueriesSeeOnlyFramesSinceTheMark()
    {
        CanTrace trace = new(_path);
        Append(Frame(1, CanMessageType.M569, 0, 1, false, [0x01, 0x00]));
        trace.SetMark();
        Append(Frame(2, CanMessageType.M569, 0, 2, false, [0x02, 0x00]),
               Frame(3, CanMessageType.StandardReply, 2, 0, true, Reply(2, CodeResult.Ok, 0, false, "")));

        Assert.Multiple(() =>
        {
            Assert.That(trace.All, Has.Count.EqualTo(3));
            Assert.That(trace.Find<CanMessageM569>(), Has.Count.EqualTo(1));
            Assert.That(trace.Find<CanMessageM569>()[0].Destination, Is.EqualTo(2));
            Assert.That(trace.Find(response: true), Has.Count.EqualTo(1));
            Assert.That(trace.Find(source: 0, destination: 1), Is.Empty);
        });
    }

    [Test]
    public void AReplyIsCompleteOnceItsLastFragmentArrives()
    {
        CanTrace trace = new(_path);
        Append(Frame(1, CanMessageType.M569, 0, 1, false, [0x07, 0x00]),
               Frame(1.1, CanMessageType.StandardReply, 1, 0, true, Reply(7, CodeResult.Ok, 0, true, "Driver 1.0 runs ")));
        CanFrame request = trace.Find<CanMessageM569>().Single();
        Assert.That(trace.ReplyTo(request), Is.Null, "the reply is not complete while more fragments follow");

        // A reply to another request, which must not be mixed in
        Append(Frame(1.15, CanMessageType.StandardReply, 1, 0, true, Reply(8, CodeResult.Ok, 0, false, "other")),
               Frame(1.2, CanMessageType.StandardReply, 1, 0, true, Reply(7, CodeResult.Warning, 1, false, "forwards")));
        StandardReply? reply = trace.ReplyTo(request);
        Assert.That(reply, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(reply!.Text, Is.EqualTo("Driver 1.0 runs forwards"));
            Assert.That(reply.Result, Is.EqualTo(CodeResult.Warning));
        });
    }

    [Test]
    public void AnEarlierReplyWithTheSameRequestIdIsNotTheReply()
    {
        CanTrace trace = new(_path);
        // Request ids are 12 bits and wrap, so an old reply can carry the id of a new request
        Append(Frame(1, CanMessageType.StandardReply, 1, 0, true, Reply(9, CodeResult.Error, 0, false, "old")),
               Frame(2, CanMessageType.M569, 0, 1, false, [0x09, 0x00]));
        CanFrame request = trace.Find<CanMessageM569>().Single();
        Assert.That(trace.ReplyTo(request), Is.Null);

        Append(Frame(2.1, CanMessageType.StandardReply, 1, 0, true, Reply(9, CodeResult.Ok, 0, false, "new")));
        Assert.That(trace.ReplyTo(request)?.Text, Is.EqualTo("new"));
    }

    [Test]
    public void ALineTheLoggerIsStillWritingIsReadOnceComplete()
    {
        CanTrace trace = new(_path);
        string line = CanTrace.Format(Frame(1, CanMessageType.M569, 0, 1, false, [0x01, 0x00]));
        File.AppendAllText(_path, line[..10]);
        Assert.That(trace.All, Is.Empty);
        File.AppendAllText(_path, line[10..] + "\n");
        Assert.That(trace.All, Has.Count.EqualTo(1));
    }
}
