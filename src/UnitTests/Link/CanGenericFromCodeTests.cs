using System.Collections.Immutable;
using DuetAPI.Commands;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for taking the parameters of a generic CAN message from a G-code command, which is what
/// <see cref="CanGenericWriter.FromCode"/> exists for. These go through the letter-keyed path, since the
/// conversions are per table entry rather than per message type; that the generated messages hand their own
/// table to it is covered by <see cref="CanGenericMessageTests"/>.
/// </summary>
[TestFixture]
public class CanGenericFromCodeTests
{
    [Test]
    public void TakesOnlyTheParametersTheTableDeclares()
    {
        // Z is not in M950FanParams and must be ignored rather than rejected: the main board consumes
        // parameters of its own from the same command
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        Code code = new("M950 F3 C\"out0\" Q25000 Z1");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, table, code);

        Assert.That(message.ParamMap, Is.EqualTo(0b0111u), "F, Q and C but not K");
        Assert.That(CanGenericParser.GetUInt(message, table, 'F'), Is.EqualTo(3u));
        Assert.That(CanGenericParser.GetUInt(message, table, 'Q'), Is.EqualTo(25000u));
        Assert.That(CanGenericParser.GetString(message, table, 'C'), Is.EqualTo("out0"));
        Assert.That(CanGenericParser.Has(message, table, 'K'), Is.False);
    }

    /// <summary>
    /// The expansion board only knows its own ports, so a reduced string has to lose the board address
    /// before it goes on the bus. RepRapFirmware does the same.
    /// </summary>
    [TestCase("1.out2", "out2")]
    [TestCase("!1.out2", "!out2")]
    [TestCase("^121.io3.in", "^io3.in")]
    [TestCase("out2", "out2")]
    [TestCase("!io2.out", "!io2.out")]
    [TestCase("e0heat", "e0heat")]
    public void StripsTheBoardAddressFromAReducedString(string typed, string expected)
    {
        Code code = new($"M950 F0 C\"{typed}\"");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, CanGenericTables.M950FanParams, code);

        Assert.That(CanGenericParser.GetString(message, CanGenericTables.M950FanParams, 'C'), Is.EqualTo(expected));
    }

    [Test]
    public void ConvertsEachParameterToTheTypeTheTableDeclares()
    {
        // M308V1Params: T:float, L:int16, S:uint8, K:char, Y:reducedString, U:float16
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M308V1Params;
        Code code = new("M308 S1 T100000 L-273 K\"B\" Y\"thermistor\" U0.5");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, table, code);

        Assert.That(CanGenericParser.GetFloat(message, table, 'T'), Is.EqualTo(100000.0f));
        Assert.That(CanGenericParser.GetInt(message, table, 'L'), Is.EqualTo(-273));
        Assert.That(CanGenericParser.GetUInt(message, table, 'S'), Is.EqualTo(1u));
        Assert.That(CanGenericParser.GetChar(message, table, 'K'), Is.EqualTo('B'));
        Assert.That(CanGenericParser.GetString(message, table, 'Y'), Is.EqualTo("thermistor"));
        Assert.That(CanGenericParser.GetFloat(message, table, 'U'), Is.EqualTo(0.5f));
    }

    [Test]
    public void TakesTheLocalPortOfADriverId()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M569Params;
        Code code = new("M569 P1.2 S1");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, table, code);

        Assert.That(CanGenericParser.GetUInt(message, table, 'P'), Is.EqualTo(2u), "the local driver, not the board");
        Assert.That(CanGenericParser.GetUInt(message, table, 'S'), Is.EqualTo(1u));
    }

    [Test]
    public void PacksArrayParameters()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M569Params;
        Code code = new("M569 P0 Y1:2:3 T0.1:0.2:0.3:0.4");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, table, code);

        Assert.That(CanGenericParser.GetUIntArray(message, table, 'Y'), Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(CanGenericParser.GetFloatArray(message, table, 'T'), Is.EqualTo(new[] { 0.1f, 0.2f, 0.3f, 0.4f }));
    }

    /// <summary>
    /// RepRapFirmware clamps a value that is too large for its field rather than rejecting the command, and
    /// keeps at most as many array elements as the table allows.
    /// </summary>
    [Test]
    public void ClampsOversizedValuesAndTruncatesLongArrays()
    {
        Code clamped = new("M915 S200 F300");
        CanMessageGeneric m915 = default;
        CanGenericWriter.FromCode(ref m915, CanGenericTables.M915Params, clamped);
        Assert.That(CanGenericParser.GetInt(m915, CanGenericTables.M915Params, 'S'), Is.EqualTo(127), "S is an int8");
        Assert.That(CanGenericParser.GetUInt(m915, CanGenericTables.M915Params, 'F'), Is.EqualTo(255u), "F is a uint8");

        Code longArray = new("M569 P0 Y1:2:3:4:5");
        CanMessageGeneric m569 = default;
        CanGenericWriter.FromCode(ref m569, CanGenericTables.M569Params, longArray);
        Assert.That(CanGenericParser.GetUIntArray(m569, CanGenericTables.M569Params, 'Y'), Is.EqualTo(new uint[] { 1, 2, 3 }), "Y allows 3 elements");
    }

    /// <summary>
    /// RepRapFirmware's G-code parser reads an unsigned value with <c>strtoul</c>, which wraps a negative
    /// literal around to a huge unsigned value rather than rejecting it; a uint8/uint16 field then clamps
    /// that the same way it clamps an oversized positive value.
    /// </summary>
    [Test]
    public void WrapsNegativeValuesForUnsignedFieldsInsteadOfThrowing()
    {
        Code code = new("M915 F-1 H-1");
        CanMessageGeneric m915 = default;
        CanGenericWriter.FromCode(ref m915, CanGenericTables.M915Params, code);
        Assert.That(CanGenericParser.GetUInt(m915, CanGenericTables.M915Params, 'F'), Is.EqualTo(255u), "F is a uint8, clamped after wrapping");
        Assert.That(CanGenericParser.GetUInt(m915, CanGenericTables.M915Params, 'H'), Is.EqualTo(65535u), "H is a uint16, clamped after wrapping");

        Code uint32Code = new("M950 E0 U10 Q-1");
        CanMessageGeneric led = default;
        CanGenericWriter.FromCode(ref led, CanGenericTables.M950LedParams, uint32Code);
        Assert.That(CanGenericParser.GetUInt(led, CanGenericTables.M950LedParams, 'Q'), Is.EqualTo(uint.MaxValue), "Q is a uint32 with no field-width clamp");
    }

    /// <summary>
    /// A retired entry has a lowercase letter precisely so that it can never be matched against a command,
    /// even if the user types the uppercase one.
    /// </summary>
    [Test]
    public void NeverTakesAValueForARetiredEntry()
    {
        Code code = new("M569.1 P0 H5 S200");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, CanGenericTables.M569Point1Params, code);

        Assert.That(message.ParamMap & (1u << 7), Is.Zero, "the retired 'h' at index 7");
        Assert.That(CanGenericParser.GetUInt(message, CanGenericTables.M569Point1Params, 'S'), Is.EqualTo(200u), "the parameter after it is unaffected");
    }

    [Test]
    public void ProducesAnEmptyMessageWhenTheCommandMentionsNothingInTheTable()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        Code code = new("M950 Z1");
        CanMessageGeneric message = default;
        CanGenericWriter.FromCode(ref message, table, code);

        Assert.That(message.ParamMap, Is.Zero);
        Assert.That(CanGenericLayout.DataLength(message.Data, message.ParamMap, table), Is.Zero);
        Assert.That(CanGenericLayout.ActualDataLength(message.Data, message.ParamMap, table), Is.EqualTo(4u),
            "just the request ID and parameter map");
    }

    /// <summary>
    /// A command does not have to be the first thing a message is built from: the parameters it mentions
    /// replace what is there, and the ones it does not are left alone. That is what lets the caller fill in
    /// the values only it knows, such as a driver number, before or after reading the command.
    /// </summary>
    [Test]
    public void ReplacesOnlyTheParametersTheCommandMentions()
    {
        ImmutableArray<CanParamDescriptor> table = CanGenericTables.M950FanParams;
        CanMessageGeneric message = default;
        CanGenericWriter.SetUInt(ref message, table, 'F', 7);
        CanGenericWriter.SetFloat(ref message, table, 'K', 2.0f);

        Code code = new("M950 F3 C\"out0\"");
        CanGenericWriter.FromCode(ref message, table, code);

        Assert.That(CanGenericParser.GetUInt(message, table, 'F'), Is.EqualTo(3u), "replaced by the command");
        Assert.That(CanGenericParser.GetString(message, table, 'C'), Is.EqualTo("out0"), "added by the command");
        Assert.That(CanGenericParser.GetFloat(message, table, 'K'), Is.EqualTo(2.0f), "kept, since the command does not mention it");
    }

    [Test]
    public void RejectsAStringThatWouldOverflowTheMessage()
    {
        Code code = new($"M655 A\"{new string('x', 70)}\"");
        CanMessageGeneric message = default;
        Assert.Throws<CanGenericParamException>(() => CanGenericWriter.FromCode(ref message, CanGenericTables.M655Params, code));
    }
}
