using DuetAPI.Commands;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for building a generic CAN message out of a G-code command, which is what
/// <see cref="CanMessageGenericConstructor"/> exists for.
/// </summary>
[TestFixture]
public class CanMessageGenericConstructorTests
{
    [Test]
    public void TakesOnlyTheParametersTheTableDeclares()
    {
        // Z is not in M950FanParams and must be ignored rather than rejected: the main board consumes
        // parameters of its own from the same command
        Code code = new("M950 F3 C\"out0\" Q25000 Z1");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M950FanParams, code);

        Assert.That(writer.Message.ParamMap, Is.EqualTo(0b0111u), "F, Q and C but not K");

        CanGenericParser parser = new(writer.Message, CanGenericTables.M950FanParams);
        Assert.That(parser.GetUInt('F'), Is.EqualTo(3u));
        Assert.That(parser.GetUInt('Q'), Is.EqualTo(25000u));
        Assert.That(parser.GetString('C'), Is.EqualTo("out0"));
        Assert.That(parser.Has('K'), Is.False);
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
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M950FanParams, code);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M950FanParams);
        Assert.That(parser.GetString('C'), Is.EqualTo(expected));
    }

    [Test]
    public void ConvertsEachParameterToTheTypeTheTableDeclares()
    {
        // M308V1Params: T:float, L:int16, S:uint8, K:char, Y:reducedString, U:float16
        Code code = new("M308 S1 T100000 L-273 K\"B\" Y\"thermistor\" U0.5");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M308V1Params, code);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M308V1Params);
        Assert.That(parser.GetFloat('T'), Is.EqualTo(100000.0f));
        Assert.That(parser.GetInt('L'), Is.EqualTo(-273));
        Assert.That(parser.GetUInt('S'), Is.EqualTo(1u));
        Assert.That(parser.GetChar('K'), Is.EqualTo('B'));
        Assert.That(parser.GetString('Y'), Is.EqualTo("thermistor"));
        Assert.That(parser.GetFloat('U'), Is.EqualTo(0.5f));
    }

    [Test]
    public void TakesTheLocalPortOfADriverId()
    {
        Code code = new("M569 P1.2 S1");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M569Params, code);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M569Params);
        Assert.That(parser.GetUInt('P'), Is.EqualTo(2u), "the local driver, not the board");
        Assert.That(parser.GetUInt('S'), Is.EqualTo(1u));
    }

    [Test]
    public void PacksArrayParameters()
    {
        Code code = new("M569 P0 Y1:2:3 T0.1:0.2:0.3:0.4");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M569Params, code);

        CanGenericParser parser = new(writer.Message, CanGenericTables.M569Params);
        Assert.That(parser.GetUIntArray('Y'), Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(parser.GetFloatArray('T'), Is.EqualTo(new[] { 0.1f, 0.2f, 0.3f, 0.4f }));
    }

    /// <summary>
    /// RepRapFirmware clamps a value that is too large for its field rather than rejecting the command, and
    /// keeps at most as many array elements as the table allows.
    /// </summary>
    [Test]
    public void ClampsOversizedValuesAndTruncatesLongArrays()
    {
        Code clamped = new("M915 S200 F300");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M915Params, clamped);
        CanGenericParser parser = new(writer.Message, CanGenericTables.M915Params);
        Assert.That(parser.GetInt('S'), Is.EqualTo(127), "S is an int8");
        Assert.That(parser.GetUInt('F'), Is.EqualTo(255u), "F is a uint8");

        Code longArray = new("M569 P0 Y1:2:3:4:5");
        CanGenericWriter arrayWriter = CanMessageGenericConstructor.FromCode(CanGenericTables.M569Params, longArray);
        CanGenericParser arrayParser = new(arrayWriter.Message, CanGenericTables.M569Params);
        Assert.That(arrayParser.GetUIntArray('Y'), Is.EqualTo(new uint[] { 1, 2, 3 }), "Y allows 3 elements");
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
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M915Params, code);
        CanGenericParser parser = new(writer.Message, CanGenericTables.M915Params);
        Assert.That(parser.GetUInt('F'), Is.EqualTo(255u), "F is a uint8, clamped after wrapping");
        Assert.That(parser.GetUInt('H'), Is.EqualTo(65535u), "H is a uint16, clamped after wrapping");

        Code uint32Code = new("M950 E0 U10 Q-1");
        CanGenericWriter uint32Writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M950LedParams, uint32Code);
        CanGenericParser uint32Parser = new(uint32Writer.Message, CanGenericTables.M950LedParams);
        Assert.That(uint32Parser.GetUInt('Q'), Is.EqualTo(uint.MaxValue), "Q is a uint32 with no field-width clamp");
    }

    /// <summary>
    /// A retired entry has a lowercase letter precisely so that it can never be matched against a command,
    /// even if the user types the uppercase one.
    /// </summary>
    [Test]
    public void NeverTakesAValueForARetiredEntry()
    {
        Code code = new("M569.1 P0 H5 S200");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M569Point1Params, code);

        Assert.That(writer.Message.ParamMap & (1u << 7), Is.Zero, "the retired 'h' at index 7");
        CanGenericParser parser = new(writer.Message, CanGenericTables.M569Point1Params);
        Assert.That(parser.GetUInt('S'), Is.EqualTo(200u), "the parameter after it is unaffected");
    }

    [Test]
    public void ProducesAnEmptyMessageWhenTheCommandMentionsNothingInTheTable()
    {
        Code code = new("M950 Z1");
        CanGenericWriter writer = CanMessageGenericConstructor.FromCode(CanGenericTables.M950FanParams, code);

        Assert.That(writer.Message.ParamMap, Is.Zero);
        Assert.That(writer.DataLength, Is.Zero);
        Assert.That(writer.ActualDataLength, Is.EqualTo(4u), "just the request ID and parameter map");
    }

    [Test]
    public void RejectsAStringThatWouldOverflowTheMessage()
    {
        Code code = new($"M655 A\"{new string('x', 70)}\"");
        Assert.Throws<CanGenericParamException>(() => CanMessageGenericConstructor.FromCode(CanGenericTables.M655Params, code));
    }
}
