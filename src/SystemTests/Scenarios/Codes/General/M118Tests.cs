using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Codes.Handlers;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M118, echoing a message</summary>
[TestFixture]
[GCode("M118")]
public class M118Tests : BenchFixture
{
    /// <summary>
    /// M118 routes its message to the requesting channel, so the reply carries the text.
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 118: without P the type is GenericMessage and platform.Message sends
    /// the text to every destination, including the channel the code came from
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M118EchoesTheMessage()
    {
        await using Bench bench = await Bench.StartAsync();
        string reply = await bench.Host.ExecuteCodeAsync("M118 S\"M118 bench message\"");
        Assert.That(reply, Reply.IsSuccess("M118 bench message"),
                    "M118 S sends the message to the requesting channel (RRF GCodes2.cpp case 118, platform.Message with GenericMessage)");
    }

    /// <summary>A P that names no destination is refused</summary>
    /// <remarks>GCodes2.cpp case 118 answers any other P with "Invalid message type"</remarks>
    [TestCase(8)]
    [TestCase(-1)]
    public async Task M118RefusesAPThatNamesNoDestination(int destination)
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync($"M118 P{destination} S\"hello\""),
                    Reply.IsError(CodeErrors.InvalidEnumValue<MCodeHandler.MessageDestination>('P')));
    }
}
