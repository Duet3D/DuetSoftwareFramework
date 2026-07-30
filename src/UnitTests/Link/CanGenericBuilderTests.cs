using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for the generated typed builders. The builders are a thin typed face over
/// <see cref="CanGenericWriter"/>, so what matters is that each method reaches the right table entry with
/// the right type, and that the bytes are the same as going through the letter-keyed writer directly.
/// </summary>
[TestFixture]
public class CanGenericBuilderTests
{
    [Test]
    public void ProducesTheSameBytesAsTheLetterKeyedWriter()
    {
        M950FanBuilder builder = new();
        builder.F(3).Q(25000).C("out0").K(2.0f);

        CanGenericWriter writer = new(CanGenericTables.M950FanParams);
        writer.AddUInt('F', 3);
        writer.AddUInt('Q', 25000);
        writer.AddString('C', "out0");
        writer.AddFloat('K', 2.0f);

        Assert.That(builder.Message.ParamMap, Is.EqualTo(writer.Message.ParamMap));
        Assert.That(builder.ActualDataLength, Is.EqualTo(writer.ActualDataLength));
        CanGenericParser parser = new(builder.Message, CanGenericTables.M950FanParams);
        Assert.That(parser.GetUInt('F'), Is.EqualTo(3u));
        Assert.That(parser.GetUInt('Q'), Is.EqualTo(25000u));
        Assert.That(parser.GetString('C'), Is.EqualTo("out0"));
        Assert.That(parser.GetFloat('K'), Is.EqualTo(2.0f));
    }

    [Test]
    public void ChainingIsOrderIndependent()
    {
        M950FanBuilder forwards = new();
        forwards.F(3).C("out0").K(2.0f);

        M950FanBuilder backwards = new();
        backwards.K(2.0f).C("out0").F(3);

        Assert.That(backwards.Message.ParamMap, Is.EqualTo(forwards.Message.ParamMap));
        Assert.That(backwards.ActualDataLength, Is.EqualTo(forwards.ActualDataLength));
    }

    [Test]
    public void CarriesTheMessageTypeOfItsTable()
    {
        Assert.That(M950FanBuilder.MessageType, Is.EqualTo(CanMessageType.M950Fan));
        Assert.That(M569Point1Builder.MessageType, Is.EqualTo(CanMessageType.M569P1));
        Assert.That(M122P1Builder.MessageType, Is.EqualTo(CanMessageType.TestReport));
        Assert.That(M150Builder.MessageType, Is.EqualTo(CanMessageType.WriteLedStrip));
    }

    [Test]
    public void HandlesArrayAndDriverParameters()
    {
        M569Builder builder = new();
        builder.P(2).Y(1, 2, 3).T(1.0f, 0.0f);

        CanGenericParser parser = new(builder.Message, CanGenericTables.M569Params);
        Assert.That(parser.GetUInt('P'), Is.EqualTo(2u));
        Assert.That(parser.GetUIntArray('Y'), Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(parser.GetFloatArray('T'), Is.EqualTo(new[] { 1.0f, 0.0f }));
    }

    /// <summary>
    /// An entry outside A..Z still occupies its table position, which is what keeps the parameters after it
    /// on the bits the receiver expects.
    /// </summary>
    [Test]
    public void EntriesOutsideAtoZStillHoldTheirTablePosition()
    {
        Assert.That(CanGenericTables.M569Point1Params[7].Letter, Is.EqualTo('h'), "the retired entry");
        Assert.That(CanGenericTables.M569Point1Params[7].CanComeFromGCode, Is.False);
        Assert.That(CanGenericTables.M569Point1Params[8].Letter, Is.EqualTo('S'), "the entry after it");

        // S therefore sits at bit 8, not bit 7
        M569Point1Builder builder = new();
        builder.S(200);
        Assert.That(builder.Message.ParamMap, Is.EqualTo(1u << 8));
    }

    /// <summary>
    /// M915's 'd' and ConfigureFilamentMonitor's 'd' carry a driver number that RepRapFirmware fills in
    /// itself; they are outside A..Z only so that a G-code command cannot supply them. They still have to be
    /// settable, and they are the first entry of their tables, so getting this wrong would put every other
    /// parameter of those two messages at the wrong offset.
    /// </summary>
    [Test]
    public void ParametersTheCallerSuppliesAreStillSettable()
    {
        M915Builder m915 = new();
        m915.d(0b101).S(-3);

        CanGenericParser parser = new(m915.Message, CanGenericTables.M915Params);
        Assert.That(parser.GetUInt('d'), Is.EqualTo(0b101u), "the driver bitmap");
        Assert.That(parser.GetInt('S'), Is.EqualTo(-3));

        ConfigureFilamentMonitorBuilder monitor = new();
        monitor.d(2).S(1);
        CanGenericParser monitorParser = new(monitor.Message, CanGenericTables.ConfigureFilamentMonitorParams);
        Assert.That(monitorParser.GetUInt('d'), Is.EqualTo(2u), "the local driver");
        Assert.That(monitorParser.GetUInt('S'), Is.EqualTo(1u));
    }
}
