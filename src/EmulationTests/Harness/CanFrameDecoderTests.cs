using System;
using System.Text.Json.Nodes;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetEmulation.Can;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// Decoding CAN frames into the fields of their messages, without an emulator
/// </summary>
[TestFixture]
public class CanFrameDecoderTests
{
    private static CanFrame Frame(CanMessageType type, string hex, byte source = 1, byte destination = 0) =>
        new(TimeSpan.Zero, "test", ((uint)type << (int)CanId.MessageTypeShift) | ((uint)source << (int)CanId.SrcAddressShift) | destination,
            Convert.FromHexString(hex), IsFd: true, BitRateSwitch: true);

    [Test]
    public void ANestedStructIsDecodedIntoItsFields()
    {
        // An EXP3HC reporting that its endstop handle 1.0.0 went high, as RepRapFirmware's bench recorded it
        JsonObject message = CanFrameDecoder.Decode(Frame(CanMessageType.InputStateChangedV2, "010001000010796dffffff7f"))!;

        JsonNode handle = message["Results"]![0]!["Handle"]!;
        Assert.Multiple(() =>
        {
            Assert.That(handle["All"]!.GetValue<long>(), Is.EqualTo(0x1000));
            Assert.That(handle["Type"]!.GetValue<long>(), Is.EqualTo(1));
            Assert.That(handle["Major"]!.GetValue<long>(), Is.Zero);
            Assert.That(handle["Minor"]!.GetValue<long>(), Is.Zero);
            Assert.That(message["Results"]![0]!["Reading"]!.GetValue<long>(), Is.EqualTo(int.MaxValue));
        });
    }

    [Test]
    public void AnArrayHoldsOnlyTheElementsTheFrameCarries()
    {
        // NumHandles is 1, and the frame ends after the first of the seven results the layout has room for
        JsonObject message = CanFrameDecoder.Decode(Frame(CanMessageType.InputStateChangedV2, "010001000010796dffffff7f"))!;

        Assert.That(message["Results"]!.AsArray(), Has.Count.EqualTo(1));
    }

    [Test]
    public void AFieldPastTheEndOfTheFrameIsLeftOut()
    {
        // The same report cut short after its header: no result at all
        JsonObject message = CanFrameDecoder.Decode(Frame(CanMessageType.InputStateChangedV2, "01000100"))!;

        Assert.Multiple(() =>
        {
            Assert.That(message["NumHandles"]!.GetValue<long>(), Is.EqualTo(1));
            Assert.That(message["Results"]!.AsArray(), Is.Empty);
        });
    }

    [Test]
    public void AHalfPrecisionValueIsDecodedAsANumber()
    {
        CanMessageBoardStatusV1 report = new();
        report.ShortValues[0].Current = (Half)24.0;
        byte[] data = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<CanMessageBoardStatusV1>()];
        CanMessageSerializer.Serialize(report, data);

        JsonObject message = CanFrameDecoder.Decode(Frame(CanMessageBoardStatusV1.MessageType, Convert.ToHexString(data)))!;

        Assert.That(message["ShortValues"]![0]!["Current"]!.GetValue<float>(), Is.EqualTo(24.0f));
    }

    [Test]
    public void AFrameOfATypeWithNoLayoutDecodesToNull() =>
        Assert.That(CanFrameDecoder.Decode(Frame((CanMessageType)CanId.MessageTypeMask, "00")), Is.Null);
}
