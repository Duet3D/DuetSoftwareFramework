using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for packing and unpacking the generic CAN messages.
/// </summary>
/// <remarks>
/// The expected byte sequences here are worked out from the format CANlib's <c>CanMessageGenericParser</c>
/// reads: a paramMap bit per table position, and the present values packed in table order with no padding.
/// Asserting the raw bytes rather than only round-tripping through our own parser is what pins the format
/// down — a writer and parser that agreed with each other but not with CANlib would still be wrong.
/// </remarks>
[TestFixture]
public class CanGenericWriterTests
{
    [Test]
    public void PacksParametersInTableOrderWithTheMatchingParamMap()
    {
        // M950FanParams is F:uint16, Q:pwmFreq, C:reducedString, K:float
        CanGenericWriter writer = new(CanGenericTables.M950FanParams);
        writer.AddUInt('F', 3);
        writer.AddUInt('Q', 25000);
        writer.AddString('C', "out0");
        writer.AddFloat('K', 2.0f);

        Assert.That(writer.Message.ParamMap, Is.EqualTo(0b1111u), "all four parameters present");
        Assert.That(writer.GetData(), Is.EqualTo(new byte[]
        {
            0x03, 0x00,                                     // F = 3
            0xA8, 0x61,                                     // Q = 25000
            (byte)'o', (byte)'u', (byte)'t', (byte)'0', 0,  // C = "out0"
            0x00, 0x00, 0x00, 0x40                          // K = 2.0f
        }));
        Assert.That(writer.ActualDataLength, Is.EqualTo(13u + 4u), "data plus the request ID and param map");
    }

    /// <summary>
    /// The receiver finds a value by its position in the table, so a parameter added out of order has to be
    /// inserted at that position rather than appended.
    /// </summary>
    [Test]
    public void InsertsOutOfOrderParametersAtTheirTablePosition()
    {
        CanGenericWriter inOrder = new(CanGenericTables.M950FanParams);
        inOrder.AddUInt('F', 3);
        inOrder.AddString('C', "out0");
        inOrder.AddFloat('K', 2.0f);

        CanGenericWriter reversed = new(CanGenericTables.M950FanParams);
        reversed.AddFloat('K', 2.0f);
        reversed.AddString('C', "out0");
        reversed.AddUInt('F', 3);

        Assert.That(reversed.Message.ParamMap, Is.EqualTo(inOrder.Message.ParamMap));
        Assert.That(reversed.GetData(), Is.EqualTo(inOrder.GetData()));
    }

    [Test]
    public void SetsOnlyTheBitsOfThePresentParameters()
    {
        // Skipping F and Q means C is the third entry, so only bit 2 is set and the data starts with C
        CanGenericWriter writer = new(CanGenericTables.M950FanParams);
        writer.AddString('C', "e0heat");

        Assert.That(writer.Message.ParamMap, Is.EqualTo(0b0100u));
        Assert.That(writer.GetData(), Is.EqualTo(new byte[]
        {
            (byte)'e', (byte)'0', (byte)'h', (byte)'e', (byte)'a', (byte)'t', 0
        }));
    }

    [Test]
    public void PacksArraysAsALengthByteFollowedByElements()
    {
        // M569Params has Y:uint8Array[3] at index 8 and T:floatArray[4] at index 9
        CanGenericWriter writer = new(CanGenericTables.M569Params);
        writer.AddDriverId('P', 2);
        writer.AddUIntArray('Y', [1, 2, 3]);
        writer.AddFloatArray('T', [1.0f, 0.0f]);

        Assert.That(writer.Message.ParamMap, Is.EqualTo(0b1100000001u));
        Assert.That(writer.GetData(), Is.EqualTo(new byte[]
        {
            0x02,                                           // P = driver 2
            0x03, 0x01, 0x02, 0x03,                         // Y = 3 elements
            0x02, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00  // T = 2 floats
        }));
    }

    [Test]
    public void RoundTripsEveryParameterTypeThroughTheParser()
    {
        // M308V1Params covers float, int16, uint8, char, reducedString and float16 in one table
        CanGenericWriter writer = new(CanGenericTables.M308V1Params);
        writer.AddFloat('T', 100000.0f);
        writer.AddInt('L', -273);
        writer.AddUInt('S', 1);
        writer.AddChar('K', 'B');
        writer.AddString('Y', "thermistor");
        writer.AddFloat('U', 0.5f);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M308V1Params);
        Assert.That(parser.GetFloat('T'), Is.EqualTo(100000.0f));
        Assert.That(parser.GetInt('L'), Is.EqualTo(-273));
        Assert.That(parser.GetUInt('S'), Is.EqualTo(1u));
        Assert.That(parser.GetChar('K'), Is.EqualTo('B'));
        Assert.That(parser.GetString('Y'), Is.EqualTo("thermistor"));
        Assert.That(parser.GetFloat('U'), Is.EqualTo(0.5f));

        Assert.That(parser.Has('B'), Is.False, "a parameter that was not added");
        Assert.That(parser.GetFloat('B'), Is.Null);
    }

    [Test]
    public void RoundTripsArraysThroughTheParser()
    {
        CanGenericWriter writer = new(CanGenericTables.M122P1Params);
        writer.AddFloatArray('T', [-10.0f, 80.0f]);
        writer.AddFloatArray('V', [11.0f, 25.5f]);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M122P1Params);
        Assert.That(parser.GetFloatArray('T'), Is.EqualTo(new[] { -10.0f, 80.0f }));
        Assert.That(parser.GetFloatArray('V'), Is.EqualTo(new[] { 11.0f, 25.5f }));
        Assert.That(parser.GetFloatArray('W'), Is.Null);
    }

    [Test]
    public void RoundTripsAStringFollowedByAnotherParameter()
    {
        // A string is the only variable-length scalar, so the parameter after one is the case where a
        // parser that did not skip the terminator would go wrong
        CanGenericWriter writer = new(CanGenericTables.M569Point7Params);
        writer.AddDriverId('P', 1);
        writer.AddString('C', "!io2.out");
        writer.AddFloat('V', 24.0f);
        writer.AddUInt('S', 200);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M569Point7Params);
        Assert.That(parser.GetUInt('P'), Is.EqualTo(1u));
        Assert.That(parser.GetString('C'), Is.EqualTo("!io2.out"));
        Assert.That(parser.GetFloat('V'), Is.EqualTo(24.0f));
        Assert.That(parser.GetUInt('S'), Is.EqualTo(200u));
    }

    [Test]
    public void RejectsAValueThatDoesNotFitItsParameter()
    {
        CanGenericWriter writer = new(CanGenericTables.M950FanParams);
        Assert.Throws<CanGenericParamException>(() => writer.AddUInt('F', 0x1_0000), "uint16 parameter");

        CanGenericWriter m915 = new(CanGenericTables.M915Params);
        Assert.Throws<CanGenericParamException>(() => m915.AddInt('S', 200), "int8 parameter");
    }

    [Test]
    public void RejectsTheWrongTypeForAParameter()
    {
        CanGenericWriter writer = new(CanGenericTables.M950FanParams);
        Assert.Throws<CanGenericParamException>(() => writer.AddFloat('F', 1.0f), "F is a uint16");
        Assert.Throws<CanGenericParamException>(() => writer.AddUInt('C', 1), "C is a string");
    }

    [Test]
    public void RejectsUnknownDuplicateAndRetiredParameters()
    {
        CanGenericWriter writer = new(CanGenericTables.M950FanParams);
        Assert.Throws<CanGenericParamException>(() => writer.AddUInt('Z', 1), "not in the table");

        writer.AddUInt('F', 1);
        Assert.Throws<CanGenericParamException>(() => writer.AddUInt('F', 2), "already set");

        // M569Point1Params reserves 'h' for a parameter that is no longer used
        CanGenericWriter m569p1 = new(CanGenericTables.M569Point1Params);
        Assert.Throws<CanGenericParamException>(() => m569p1.AddFloat('h', 1.0f), "retired entry");
    }

    [Test]
    public void RejectsAnArrayLongerThanTheTableAllows()
    {
        CanGenericWriter writer = new(CanGenericTables.M569Params);
        Assert.Throws<CanGenericParamException>(() => writer.AddUIntArray('Y', [1, 2, 3, 4]), "Y allows 3");
    }

    [Test]
    public void RejectsAMessageThatWouldOverflowTheDataArea()
    {
        CanGenericWriter writer = new(CanGenericTables.M655Params);
        Assert.Throws<CanGenericParamException>(() => writer.AddString('A', new string('x', 60)));
    }
}
