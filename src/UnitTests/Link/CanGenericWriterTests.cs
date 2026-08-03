using System.Collections.Immutable;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for packing and unpacking the generic CAN messages through the letter-keyed path, which is what
/// the generated message types are a typed face over.
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
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        CanMessageGeneric message = default;
        CanGenericWriter.SetUInt(ref message, table, 'F', 3);
        CanGenericWriter.SetUInt(ref message, table, 'Q', 25000);
        CanGenericWriter.SetString(ref message, table, 'C', "out0");
        CanGenericWriter.SetFloat(ref message, table, 'K', 2.0f);

        Assert.That(message.ParamMap, Is.EqualTo(0b1111u), "all four parameters present");
        Assert.That(CanGenericParser.GetData(message, table), Is.EqualTo(new byte[]
        {
            0x03, 0x00,                                     // F = 3
            0xA8, 0x61,                                     // Q = 25000
            (byte)'o', (byte)'u', (byte)'t', (byte)'0', 0,  // C = "out0"
            0x00, 0x00, 0x00, 0x40                          // K = 2.0f
        }));
        Assert.That(CanGenericLayout.ActualDataLength(message.Data, message.ParamMap, table), Is.EqualTo(13u + 4u),
            "data plus the request ID and param map");
    }

    /// <summary>
    /// The receiver finds a value by its position in the table, so a parameter set out of order has to be
    /// inserted at that position rather than appended.
    /// </summary>
    [Test]
    public void InsertsOutOfOrderParametersAtTheirTablePosition()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;

        CanMessageGeneric inOrder = default;
        CanGenericWriter.SetUInt(ref inOrder, table, 'F', 3);
        CanGenericWriter.SetString(ref inOrder, table, 'C', "out0");
        CanGenericWriter.SetFloat(ref inOrder, table, 'K', 2.0f);

        CanMessageGeneric reversed = default;
        CanGenericWriter.SetFloat(ref reversed, table, 'K', 2.0f);
        CanGenericWriter.SetString(ref reversed, table, 'C', "out0");
        CanGenericWriter.SetUInt(ref reversed, table, 'F', 3);

        Assert.That(reversed.ParamMap, Is.EqualTo(inOrder.ParamMap));
        Assert.That(CanGenericParser.GetData(reversed, table), Is.EqualTo(CanGenericParser.GetData(inOrder, table)));
    }

    [Test]
    public void SetsOnlyTheBitsOfThePresentParameters()
    {
        // Skipping F and Q means C is the third entry, so only bit 2 is set and the data starts with C
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        CanMessageGeneric message = default;
        CanGenericWriter.SetString(ref message, table, 'C', "e0heat");

        Assert.That(message.ParamMap, Is.EqualTo(0b0100u));
        Assert.That(CanGenericParser.GetData(message, table), Is.EqualTo(new byte[]
        {
            (byte)'e', (byte)'0', (byte)'h', (byte)'e', (byte)'a', (byte)'t', 0
        }));
    }

    [Test]
    public void PacksArraysAsALengthByteFollowedByElements()
    {
        // M569Params has Y:uint8Array[3] at index 8 and T:floatArray[4] at index 9
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M569Params;
        CanMessageGeneric message = default;
        CanGenericWriter.SetDriverId(ref message, table, 'P', 2);
        CanGenericWriter.SetUIntArray(ref message, table, 'Y', [1, 2, 3]);
        CanGenericWriter.SetFloatArray(ref message, table, 'T', [1.0f, 0.0f]);

        Assert.That(message.ParamMap, Is.EqualTo(0b1100000001u));
        Assert.That(CanGenericParser.GetData(message, table), Is.EqualTo(new byte[]
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
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M308V1Params;
        CanMessageGeneric message = default;
        CanGenericWriter.SetFloat(ref message, table, 'T', 100000.0f);
        CanGenericWriter.SetInt(ref message, table, 'L', -273);
        CanGenericWriter.SetUInt(ref message, table, 'S', 1);
        CanGenericWriter.SetChar(ref message, table, 'K', 'B');
        CanGenericWriter.SetString(ref message, table, 'Y', "thermistor");
        CanGenericWriter.SetFloat(ref message, table, 'U', 0.5f);

        Assert.That(CanGenericParser.GetFloat(message, table, 'T'), Is.EqualTo(100000.0f));
        Assert.That(CanGenericParser.GetInt(message, table, 'L'), Is.EqualTo(-273));
        Assert.That(CanGenericParser.GetUInt(message, table, 'S'), Is.EqualTo(1u));
        Assert.That(CanGenericParser.GetChar(message, table, 'K'), Is.EqualTo('B'));
        Assert.That(CanGenericParser.GetString(message, table, 'Y'), Is.EqualTo("thermistor"));
        Assert.That(CanGenericParser.GetFloat(message, table, 'U'), Is.EqualTo(0.5f));

        Assert.That(CanGenericParser.Has(message, table, 'B'), Is.False, "a parameter that was not set");
        Assert.That(CanGenericParser.GetFloat(message, table, 'B'), Is.Null);
    }

    [Test]
    public void RoundTripsArraysThroughTheParser()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M122P1Params;
        CanMessageGeneric message = default;
        CanGenericWriter.SetFloatArray(ref message, table, 'T', [-10.0f, 80.0f]);
        CanGenericWriter.SetFloatArray(ref message, table, 'V', [11.0f, 25.5f]);

        Assert.That(CanGenericParser.GetFloatArray(message, table, 'T'), Is.EqualTo(new[] { -10.0f, 80.0f }));
        Assert.That(CanGenericParser.GetFloatArray(message, table, 'V'), Is.EqualTo(new[] { 11.0f, 25.5f }));
        Assert.That(CanGenericParser.GetFloatArray(message, table, 'W'), Is.Null);
    }

    [Test]
    public void RoundTripsAStringFollowedByAnotherParameter()
    {
        // A string is the only variable-length scalar, so the parameter after one is the case where a
        // parser that did not skip the terminator would go wrong
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M569Point7Params;
        CanMessageGeneric message = default;
        CanGenericWriter.SetDriverId(ref message, table, 'P', 1);
        CanGenericWriter.SetString(ref message, table, 'C', "!io2.out");
        CanGenericWriter.SetFloat(ref message, table, 'V', 24.0f);
        CanGenericWriter.SetUInt(ref message, table, 'S', 200);

        Assert.That(CanGenericParser.GetUInt(message, table, 'P'), Is.EqualTo(1u));
        Assert.That(CanGenericParser.GetString(message, table, 'C'), Is.EqualTo("!io2.out"));
        Assert.That(CanGenericParser.GetFloat(message, table, 'V'), Is.EqualTo(24.0f));
        Assert.That(CanGenericParser.GetUInt(message, table, 'S'), Is.EqualTo(200u));
    }

    [Test]
    public void RejectsAValueThatDoesNotFitItsParameter()
    {
        CanMessageGeneric fan = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetUInt(ref fan, CanGenericTables.M950FanParams, 'F', 0x1_0000), "uint16 parameter");

        CanMessageGeneric m915 = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetInt(ref m915, CanGenericTables.M915Params, 'S', 200), "int8 parameter");
    }

    [Test]
    public void RejectsTheWrongTypeForAParameter()
    {
        CanMessageGeneric message = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetFloat(ref message, CanGenericTables.M950FanParams, 'F', 1.0f), "F is a uint16");
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetUInt(ref message, CanGenericTables.M950FanParams, 'C', 1), "C is a string");
    }

    [Test]
    public void RejectsALetterTheTableDoesNotDeclare()
    {
        CanMessageGeneric message = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetUInt(ref message, CanGenericTables.M950FanParams, 'Z', 1));
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.Remove(ref message, CanGenericTables.M950FanParams, 'Z'));
    }

    /// <summary>
    /// Setting a parameter that is already present replaces it, which is what lets a caller override one
    /// value of a message taken from a command without rebuilding the rest of it. The replacement has to
    /// keep the parameters after it packed against it, including when its size changes.
    /// </summary>
    [Test]
    public void ReplacesAParameterThatIsAlreadyPresent()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        CanMessageGeneric message = default;
        CanGenericWriter.SetUInt(ref message, table, 'F', 3);
        CanGenericWriter.SetString(ref message, table, 'C', "out0");
        CanGenericWriter.SetFloat(ref message, table, 'K', 2.0f);

        CanGenericWriter.SetUInt(ref message, table, 'F', 1);
        Assert.That(CanGenericParser.GetUInt(message, table, 'F'), Is.EqualTo(1u));

        // A longer string has to push K along, and a shorter one pull it back
        CanGenericWriter.SetString(ref message, table, 'C', "out1234");
        Assert.That(CanGenericParser.GetString(message, table, 'C'), Is.EqualTo("out1234"));
        Assert.That(CanGenericParser.GetFloat(message, table, 'K'), Is.EqualTo(2.0f));

        CanGenericWriter.SetString(ref message, table, 'C', "o");
        Assert.That(CanGenericParser.GetString(message, table, 'C'), Is.EqualTo("o"));
        Assert.That(CanGenericParser.GetFloat(message, table, 'K'), Is.EqualTo(2.0f));

        Assert.That(message.ParamMap, Is.EqualTo(0b1101u), "F, C and K, each still present exactly once");
        Assert.That(CanGenericParser.GetData(message, table), Is.EqualTo(new byte[]
        {
            0x01, 0x00,                     // F = 1
            (byte)'o', 0,                   // C = "o"
            0x00, 0x00, 0x00, 0x40          // K = 2.0f
        }));
    }

    /// <summary>
    /// Assigning null takes a parameter back out, since a generic message says which parameters it carries
    /// rather than giving every one of them a value.
    /// </summary>
    [Test]
    public void RemovesAParameterSetToNull()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        CanMessageGeneric message = default;
        CanGenericWriter.SetUInt(ref message, table, 'F', 3);
        CanGenericWriter.SetString(ref message, table, 'C', "out0");
        CanGenericWriter.SetFloat(ref message, table, 'K', 2.0f);

        CanGenericWriter.SetString(ref message, table, 'C', null);

        Assert.That(message.ParamMap, Is.EqualTo(0b1001u), "F and K, no longer C");
        Assert.That(CanGenericParser.Has(message, table, 'C'), Is.False);
        Assert.That(CanGenericParser.GetUInt(message, table, 'F'), Is.EqualTo(3u));
        Assert.That(CanGenericParser.GetFloat(message, table, 'K'), Is.EqualTo(2.0f), "and K moved back to where C was");

        Assert.That(CanGenericWriter.Remove(ref message, table, 'C'), Is.False, "removing an absent parameter is not an error");
    }

    /// <summary>
    /// A letter outside A..Z is out of reach of a G-code command, not out of reach of the sender: M915's 'd'
    /// carries a driver bitmap that RepRapFirmware fills in itself. The writer must therefore let it be set,
    /// and it has to land on bit 0 like any other first table entry.
    /// </summary>
    [Test]
    public void AcceptsAParameterThatOnlyTheSenderCanSupply()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M915Params;
        CanMessageGeneric message = default;
        CanGenericWriter.SetUInt(ref message, table, 'd', 0b101);
        CanGenericWriter.SetInt(ref message, table, 'S', -3);

        Assert.That(message.ParamMap, Is.EqualTo(0b11u));
        Assert.That(CanGenericParser.GetData(message, table), Is.EqualTo(new byte[] { 0x05, 0x00, 0xFD }));
    }

    [Test]
    public void RejectsAnArrayLongerThanTheTableAllows()
    {
        CanMessageGeneric message = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetUIntArray(ref message, CanGenericTables.M569Params, 'Y', [1, 2, 3, 4]), "Y allows 3");
    }

    [Test]
    public void RejectsAMessageThatWouldOverflowTheDataArea()
    {
        CanMessageGeneric message = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetString(ref message, CanGenericTables.M655Params, 'A', new string('x', 60)));
    }

    /// <summary>
    /// A parameter must not be marked present in <c>paramMap</c> until its value has actually been written:
    /// otherwise a rejected value would leave the map claiming a parameter the data area does not contain,
    /// shifting every later parameter's computed offset for the receiver. A rejected replacement must not
    /// lose the value it was replacing either.
    /// </summary>
    [Test]
    public void LeavesTheMessageAloneWhenAValueIsRejected()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        CanMessageGeneric width = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetUInt(ref width, table, 'F', 0x1_0000), "F is a uint16");
        Assert.That(width.ParamMap, Is.Zero, "the rejected value must not be marked present");
        CanGenericWriter.SetUInt(ref width, table, 'F', 3);
        Assert.That(width.ParamMap, Is.EqualTo(0b1u), "F can still be set correctly afterwards");

        CanMessageGeneric overflow = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetString(ref overflow, CanGenericTables.M655Params, 'A', new string('x', 60)));
        Assert.That(overflow.ParamMap, Is.Zero, "an overflowing write must not be marked present");

        CanGenericWriter.SetString(ref overflow, CanGenericTables.M655Params, 'A', "short");
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.SetString(ref overflow, CanGenericTables.M655Params, 'A', new string('x', 60)));
        Assert.That(CanGenericParser.GetString(overflow, CanGenericTables.M655Params, 'A'), Is.EqualTo("short"),
            "a replacement that does not fit must leave the value it was replacing in place");
    }
}
