using DuetAPI.Commands;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.Link;

/// <summary>
/// Tests for the generated generic message types. Each one is a typed face over
/// <see cref="CanGenericWriter"/> and <see cref="CanGenericParser"/>, so what matters is that every property
/// reaches the right table entry with the right type, that the bytes are the same as going through the
/// letter-keyed path directly, and that a message can be taken from a command and then adjusted.
/// </summary>
[TestFixture]
public class CanGenericMessageTests
{
    [Test]
    public void ProducesTheSameBytesAsTheLetterKeyedWriter()
    {
        CanMessageM950Fan message = new()
        {
            F = 3,
            Q = 25000,
            C = "out0",
            K = 2.0f
        };

        CanMessageGeneric body = default;
        CanGenericWriter.SetUInt(ref body, CanGenericTables.M950FanParams, 'F', 3);
        CanGenericWriter.SetUInt(ref body, CanGenericTables.M950FanParams, 'Q', 25000);
        CanGenericWriter.SetString(ref body, CanGenericTables.M950FanParams, 'C', "out0");
        CanGenericWriter.SetFloat(ref body, CanGenericTables.M950FanParams, 'K', 2.0f);

        Assert.That(message.Generic.ParamMap, Is.EqualTo(body.ParamMap));
        Assert.That(CanGenericParser.GetData(message.Generic, CanMessageM950Fan.ParamTable),
            Is.EqualTo(CanGenericParser.GetData(body, CanGenericTables.M950FanParams)));
    }

    [Test]
    public void ReadsBackWhatWasSetAndNullForWhatWasNot()
    {
        CanMessageM950Fan message = new();
        Assert.That(message.F, Is.Null, "a message with no parameters carries none of them");

        message.F = 3;
        message.C = "out0";

        Assert.That(message.F, Is.EqualTo((ushort)3));
        Assert.That(message.C, Is.EqualTo("out0"));
        Assert.That(message.Q, Is.Null);
        Assert.That(message.K, Is.Null);
    }

    [Test]
    public void SettingIsOrderIndependent()
    {
        CanMessageM950Fan forwards = new() { F = 3, C = "out0", K = 2.0f };
        CanMessageM950Fan backwards = new() { K = 2.0f, C = "out0", F = 3 };

        Assert.That(backwards.Generic.ParamMap, Is.EqualTo(forwards.Generic.ParamMap));
        Assert.That(backwards.GetActualDataLength(), Is.EqualTo(forwards.GetActualDataLength()));
        Assert.That(CanGenericParser.GetData(backwards.Generic, CanMessageM950Fan.ParamTable),
            Is.EqualTo(CanGenericParser.GetData(forwards.Generic, CanMessageM950Fan.ParamTable)));
    }

    [Test]
    public void CarriesTheMessageTypeOfItsTable()
    {
        Assert.That(CanMessageM950Fan.MessageType, Is.EqualTo(CanMessageType.M950Fan));
        Assert.That(CanMessageM569Point1.MessageType, Is.EqualTo(CanMessageType.M569P1));
        Assert.That(CanMessageM122P1.MessageType, Is.EqualTo(CanMessageType.TestReport));
        Assert.That(CanMessageM150.MessageType, Is.EqualTo(CanMessageType.WriteLedStrip));
    }

    /// <summary>
    /// The whole point of the typed message is that a developer builds one from a command and then overrides
    /// whatever the main board fills in itself, without having to know the table or the message type.
    /// </summary>
    [Test]
    public void TakesItsParametersFromACommandAndStillLetsThemBeSet()
    {
        // Z is not in M950FanParams and must be ignored rather than rejected: the main board consumes
        // parameters of its own from the same command
        Code code = new("M950 F3 C\"out0\" Q25000 Z1");
        CanMessageM950Fan message = new();
        message.FromCode(code);

        Assert.That(message.F, Is.EqualTo((ushort)3));
        Assert.That(message.Q, Is.EqualTo((ushort)25000));
        Assert.That(message.C, Is.EqualTo("out0"));
        Assert.That(message.K, Is.Null, "not mentioned by the command");

        message.F = 1;
        Assert.That(message.F, Is.EqualTo((ushort)1));
        Assert.That(message.Q, Is.EqualTo((ushort)25000), "and the rest of the message is untouched");
        Assert.That(message.C, Is.EqualTo("out0"));
    }

    [Test]
    public void ClearsBackToAnEmptyMessage()
    {
        Code code = new("M950 F3 C\"out0\"");
        CanMessageM950Fan message = new();
        message.FromCode(code);
        message.Clear();

        Assert.That(message.Generic.ParamMap, Is.Zero);
        Assert.That(message.F, Is.Null);
        Assert.That(message.GetActualDataLength(), Is.EqualTo(4u), "just the request ID and parameter map");
    }

    /// <summary>
    /// The send path sizes the payload from GetActualDataLength, so a message type that reported the struct
    /// size would pad every message out to the full 60-byte data area. Its own answer has to match what was
    /// packed, including for the variable-length parameters, whose size can only be had from the data.
    /// </summary>
    [Test]
    public void ReportsTheDataLengthItActuallyPacked()
    {
        CanMessageM950Fan fan = new() { F = 3, Q = 25000, C = "out0", K = 2.0f };
        Assert.That(fan.GetActualDataLength(), Is.EqualTo(13u + 4u), "and is not the struct size");

        // Arrays and a string are the cases where the length depends on the data rather than the table alone
        CanMessageM569 driver = new() { P = 2, Y = [1, 2, 3], T = [1.0f, 0.0f] };
        Assert.That(driver.GetActualDataLength(), Is.EqualTo(1u + 4u + 9u + 4u));

        CanMessageM569Point7 brake = new() { P = 1, C = "!io2.out", V = 24.0f, S = 200 };
        Assert.That(brake.GetActualDataLength(), Is.EqualTo(1u + 9u + 4u + 2u + 4u));

        CanMessageM950Fan empty = new();
        Assert.That(empty.GetActualDataLength(), Is.EqualTo(4u), "just the request ID and parameter map");
    }

    /// <summary>
    /// The typed message exists only to name the message type; it must serialize to exactly the bytes the
    /// bare generic body would, because that is what the expansion board reads.
    /// </summary>
    [Test]
    public void SerializesToTheSameBytesAsTheBareBody()
    {
        CanMessageM950Fan message = new() { F = 3, C = "out0", K = 2.0f };

        byte[] typed = new byte[message.GetActualDataLength()];
        CanMessageSerializer.Serialize(message, typed);

        CanMessageGeneric body = message.Generic;
        byte[] bare = new byte[CanGenericLayout.ActualDataLength(body.Data, body.ParamMap, CanMessageM950Fan.ParamTable)];
        CanMessageSerializer.Serialize(body, bare);

        Assert.That(typed, Is.EqualTo(bare));

        // F:uint16, then C null-terminated, then K, with the parameter map ahead of them. requestId is the
        // low 12 bits of the leading word and paramMap the top 20, so 0b1101 lands at bit 12
        Assert.That(typed, Is.EqualTo(new byte[]
        {
            0x00, 0xD0, 0x00, 0x00,                         // request ID 0, paramMap 0b1101 << 12
            0x03, 0x00,                                     // F = 3
            (byte)'o', (byte)'u', (byte)'t', (byte)'0', 0,  // C = "out0"
            0x00, 0x00, 0x00, 0x40                          // K = 2.0f
        }));
    }

    [Test]
    public void HandlesArrayAndDriverParameters()
    {
        CanMessageM569 message = new() { P = 2, Y = [1, 2, 3], T = [1.0f, 0.0f] };

        Assert.That(message.P, Is.EqualTo((byte)2));
        Assert.That(message.Y, Is.EqualTo(new uint[] { 1, 2, 3 }));
        Assert.That(message.T, Is.EqualTo(new[] { 1.0f, 0.0f }));
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
        CanMessageM569Point1 message = new() { S = 200 };
        Assert.That(message.Generic.ParamMap, Is.EqualTo(1u << 8));
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
        CanMessageM915 m915 = new() { d = 0b101, S = -3 };
        Assert.That(m915.d, Is.EqualTo((ushort)0b101), "the driver bitmap");
        Assert.That(m915.S, Is.EqualTo((sbyte)-3));

        CanMessageConfigureFilamentMonitor monitor = new() { d = 2, S = 1 };
        Assert.That(monitor.d, Is.EqualTo((byte)2), "the local driver");
        Assert.That(monitor.S, Is.EqualTo((byte)1));
    }

    /// <summary>
    /// A property type pins the width of a fixed-size parameter, but nothing about a string or an array can
    /// say how much room is left, so those are still checked when they are set.
    /// </summary>
    [Test]
    public void RejectsAValueThatWouldOverflowTheMessage()
    {
        CanMessageM655 message = new();
        Assert.Throws<CanGenericParamException>(() => message.A = new string('x', 60));

        CanMessageM569 driver = new();
        Assert.Throws<CanGenericParamException>(() => driver.Y = [1, 2, 3, 4], "Y allows 3 elements");
    }
}
