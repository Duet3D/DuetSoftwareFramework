using System;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// The hand-written half of <see cref="CanTiming"/>: the helpers that derive the timing fields from a bit
/// rate or a sample point.
/// </summary>
/// <remarks>
/// The layout — the fields, the bitfields and the constants — is generated, and the probe checks it against
/// CANlib's <c>CanSettings.h</c>. These live here because they need floating-point maths and clamping that
/// the schema's expression language does not cover; they are a port of CANlib's own definitions.
/// </remarks>
public partial struct CanTiming
{
    /// <summary>How far we sample into the bit during the arbitration and CRC phases</summary>
    public const float DefaultNormalSamplePoint = 0.78F;

    /// <summary>How far we sample into the bit during the data phase when BRS is used</summary>
    public const float DefaultDataSamplePoint = 0.78F;

    /// <summary>True if the arbitration phase timings are self-consistent</summary>
    public readonly bool IsValid() =>
        Period >= 24 && Period <= 4800
        && NTseg1 != 0 && NTseg1 <= Period - 2u
        && NTseg1 + NJumpWidth + 1 <= Period;

    /// <summary>True if bit rate switching is in use</summary>
    public readonly bool IsUsingBrs() => DataRateMultiplier is not (0 or 0x0F);

    /// <summary>
    /// Set the bit rate to the requested value, set the sample point and jump width to default values, and
    /// disable BRS.
    /// </summary>
    public void SetDefaults(uint bitRate)
    {
        const uint DefaultNormalSamplePointTimes1024 = (uint)(DefaultNormalSamplePoint * 1024);

        Period = (ushort)((ClockFrequency + (bitRate / 2)) / bitRate);
        NTseg1 = (ushort)((Period * DefaultNormalSamplePointTimes1024 / 1024) - 1u);
        NJumpWidth = (ushort)(Period - (NTseg1 + 1));       // the maximum possible, as recommended by CiA
        DataRateMultiplier = 0x0F;                          // disable BRS
    }

    /// <summary>Set the arbitration phase sample point and the maximum jump width. Set the period first.</summary>
    public void SetNormalSamplePoint(float samplePoint)
    {
        // tseg1 excludes the 1-clock sync phase for historical reasons, hence the -1
        NTseg1 = (ushort)((Period * samplePoint) - 1u);
        NJumpWidth = (ushort)(Period - (NTseg1 + 1u));
    }

    /// <summary>Set the arbitration phase jump width. Set the bit rate and sample point first.</summary>
    public void SetNormalJumpWidth(float jw) =>
        NJumpWidth = Math.Clamp((ushort)(Period * jw), (ushort)1, (ushort)(Period - (NTseg1 + 1u)));

    /// <summary>Enable bit rate switching and set the default data phase sample point and jump width</summary>
    public void EnableBrs(byte bitRateMultiplier)
    {
        const uint DefaultDataSamplePointTimes1024 = (uint)(DefaultDataSamplePoint * 1024);

        ushort dataBitPeriod = (ushort)(Period / bitRateMultiplier);
        DataRateMultiplier = (byte)(bitRateMultiplier - 1);
        DTseg1 = (byte)((dataBitPeriod * DefaultDataSamplePointTimes1024 / 1024) - 1);
        DJumpWidth = (byte)(dataBitPeriod - (DTseg1 + 1));
    }

    /// <summary>Set the data phase sample point and the maximum jump width. Set the period first.</summary>
    public void SetDataSamplePoint(float samplePoint)
    {
        ushort dataBitPeriod = (ushort)(Period / (DataRateMultiplier + 1));
        DTseg1 = (byte)((dataBitPeriod * samplePoint) - 1);
        DJumpWidth = (byte)(dataBitPeriod - (DTseg1 + 1));
    }

    /// <summary>Set the data phase sample point directly and set the maximum jump width</summary>
    public void SetDataSamplePointDirect(byte samplePoint)
    {
        ushort dataBitPeriod = (ushort)(Period / (DataRateMultiplier + 1));
        DTseg1 = samplePoint;
        DJumpWidth = (byte)(dataBitPeriod - (DTseg1 + 1));
    }

    /// <summary>Set the data phase jump width. Set the bit rate and sample point first.</summary>
    public void SetDataJumpWidth(float jw)
    {
        ushort dataBitPeriod = (ushort)(Period / (DataRateMultiplier + 1));
        DJumpWidth = Math.Clamp((byte)(dataBitPeriod * jw), (byte)1, (byte)(dataBitPeriod - (DTseg1 + 1)));
    }
}
