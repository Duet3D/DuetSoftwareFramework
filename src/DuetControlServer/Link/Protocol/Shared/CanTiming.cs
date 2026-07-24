using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
namespace DuetControlServer.Link.Protocol.Shared;

// TODO either auto generate this from CANlib or autogenerate CANlib from this

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 10)]
public struct CanTiming
{
    public static uint ClockFrequency => 48000000;
    public static uint DefaultCanBitRate => 1000000;
    public static float DefaultNormalSamplePoint => 0.78F;
    public static float DefaultDataSamplePoint => 0.78F;

    public ushort period;
    public ushort nTseg1;
    public ushort nJumpWidth;
    private ushort _bitField1;
    private ushort _bitField2;

    public ushort DataRateMultiplier
    {
        readonly get => (ushort)(_bitField1 & 0x0F);
        set => _bitField1 = (ushort)((_bitField1 & 0xFFF0) | (value & 0x0F));
    }

    public ushort DTseg1
    {
        readonly get => (ushort)((_bitField1 >> 4) & 0xFF);
        set => _bitField1 = (ushort)((_bitField1 & 0xF00F) | ((value & 0xFF) << 4));
    }

    public ushort DJumpWidth
    {
        readonly get => (ushort)(_bitField2 & 0xFF);
        set => _bitField2 = (ushort)((_bitField2 & 0xFF00) | (value & 0xFF));
    }

    public bool IsValid()
    {
        return period >= 24 && period <= 4800
            && nTseg1 != 0 && nTseg1 <= period - 2u
            && nTseg1 + nJumpWidth + 1 <= period;
    }

    public bool IsUsingBrs()
    {
        return DataRateMultiplier != 0 && DataRateMultiplier != 0x0F;
    }

    public void SetDefaults(uint bitRate)
    {
        uint DefaultNormalSamplePointTimes1024 = (uint)(DefaultNormalSamplePoint * 1024);

        period = (ushort)((ClockFrequency + (bitRate / 2)) / bitRate);
        nTseg1 = (ushort)(period * DefaultNormalSamplePointTimes1024 / 1024 - 1u);
        nJumpWidth = (ushort)(period - (nTseg1 + 1));                        // this is the maximum possible, as recommended by CiA
        DataRateMultiplier = 0x0F;                                          // disable BRS
    }

    public void SetNormalSamplePoint(float samplePoint)
    {
        nTseg1 = (ushort)(period * samplePoint - 1u);                       // tseg1 excludes the 1-clock sync phase for historical reasons, hence the -1
        nJumpWidth = (ushort)(period - (nTseg1 + 1u));
    }

    // Set the arbitration phase jump width. The bit rate and sample point must be set first.
    public void SetNormalJumpWidth(float jw)
    {
        nJumpWidth = Math.Clamp((ushort)(period * jw), (ushort)1, (ushort)(period - (nTseg1 + 1u)));
    }

    // Enable bit rate switching and set the default data phase sample point and jump width
    public void EnableBrs(byte bitRateMultiplier)
    {
        uint DefaultDataSamplePointTimes1024 = (uint)(DefaultDataSamplePoint * 1024);
        ushort dataBitPeriod = (ushort)(period / bitRateMultiplier);
        DataRateMultiplier = (ushort)(bitRateMultiplier - 1);
        DTseg1 = (ushort)(((dataBitPeriod * DefaultDataSamplePointTimes1024) / 1024) - 1);
        DJumpWidth = (ushort)(dataBitPeriod - (DTseg1 + 1));
    }

    // Set the data phase sample point and set maximum jump width. The period must be set first.
    public void SetDataSamplePoint(float samplePoint)
    {
        ushort dataBitPeriod = (ushort)(period / (DataRateMultiplier + 1));
        DTseg1 = (ushort)(dataBitPeriod * samplePoint - 1);               // tseg1 excludes the 1-clock sync phase for historical reasons, hence the -1
        DJumpWidth = (ushort)(dataBitPeriod - (DTseg1 + 1));
    }

    // Set the data phase sample point directly and set maximum jump width
    public void SetDataSamplePointDirect(ushort samplePoint)
    {
        ushort dataBitPeriod = (ushort)(period / (DataRateMultiplier + 1));
        DTseg1 = samplePoint;               // tseg1 excludes the 1-clock sync phase for historical reasons, hence the -1
        DJumpWidth = (ushort)(dataBitPeriod - (DTseg1 + 1));
    }

    // Set the data phase jump width. The bit rate and sample point must be set first.
    public void SetDataJumpWidth(float jw)
    {
        ushort dataBitPeriod = (ushort)(period / (DataRateMultiplier + 1));
        DJumpWidth = Math.Clamp((ushort)(dataBitPeriod * jw), (ushort)1, (ushort)(dataBitPeriod - (DTseg1 + 1)));
    }
}