using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Native;
using NUnit.Framework;
using UnitTests.Utility;

namespace UnitTests.Link;

/// <summary>
/// The link records against the native structs they are read out of
/// </summary>
/// <remarks>
/// <para>
/// Every record arrives as bytes the native side wrote and this side reinterprets, and the reader
/// walks the buffer by adding sizes: a record that is one size here and another there does not fail,
/// it hands back the next record read from the middle of this one. Nothing downstream can tell that
/// from a genuine event.
/// </para>
/// <para>
/// The sizes below are the ones <c>SBC/LinkEvents.h</c> holds the native side to with
/// <c>static_assert</c>. Each is checked twice over: the fields have to add up to the size the
/// attribute declares, because <c>Size</c> sets the size rather than checking it and quietly pads a
/// struct whose fields no longer fill it, and that total has to be the native number
/// </para>
/// </remarks>
[TestFixture]
public class LinkEventsLayout
{
    /// <summary>Every record that crosses the boundary, against the size the native side asserts</summary>
    private static readonly object[] Records =
    [
        new object[] { typeof(InboundEventHeader), 4 },
        new object[] { typeof(MessageEvent), 8 },
        new object[] { typeof(CanResponseEvent), 16 },
        new object[] { typeof(CodeBufferEvent), 8 },
        new object[] { typeof(ConnectionEstablishedEvent), 8 },
        new object[] { typeof(RequestCompletedEvent), 12 },
        new object[] { typeof(LogEvent), 8 },
        new object[] { typeof(MalformedPacketEvent), 12 },
        new object[] { typeof(MoveCompletedEvent), 16 },
        new object[] { typeof(MoveFailedEvent), 12 },
        new object[] { typeof(MotionStoppedDriverEntry), 4 },
        new object[] { typeof(MotionStoppedEvent), 16 },

        // The controller writes these two, so the size to match is the one the wire format declares
        // in lib/DuetSpiInterface MessageFormats.h rather than anything the native side asserts
        new object[] { typeof(CanMessagesSentEvent), 8 },
        new object[] { typeof(CanMessageSentEntry), 4 },

        // Native LinkEvents.h has no static_assert for this one; the size is its own field list,
        // an InboundEventHeader and a uint32_t
        new object[] { typeof(OutboundSeqEvent), 8 }
    ];

    [TestCaseSource(nameof(Records))]
    public void RecordsFillTheirDeclaredSize(Type record, int nativeSize)
    {
        int size = PackedStructSize.OfFields(record);

        Assert.Multiple(() =>
        {
            Assert.That(size, Is.EqualTo(Marshal.SizeOf(record)), $"the fields fill {record.Name}, leaving no padding");
            Assert.That(size, Is.EqualTo(nativeSize), $"sizeof({record.Name})");
        });
    }

    [Test]
    public void EveryRecordIsChecked()
    {
        // A record added to LinkEvents.cs and not to the table above would be the one case this
        // fixture is here for and the one case it never looked at
        IEnumerable<Type> declared = typeof(InboundEventHeader).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(InboundEventHeader).Namespace
                           && type.IsValueType && !type.IsEnum
                           && type.StructLayoutAttribute?.Size > 0);

        Assert.That(declared, Is.EquivalentTo(Records.Cast<object[]>().Select(record => record[0])));
    }

    [Test]
    public void TheClockStatsFieldsLandWhereTheNativeOnesDo()
    {
        // The one record the runtime is handed a pointer to rather than a copy of, so what has to
        // match is where DuetSbc_GetClockStats writes. It is declared Pack = 1 here and unpacked
        // natively: the offsets agree because every field after the leading double is 4 bytes wide,
        // but the native struct is 32 bytes to the 28 here, its double asking for a tail it does not
        // fill. Nothing is written into that tail, which is why the difference is survivable
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.OffsetOf<NativeClockStats>(nameof(NativeClockStats.DriftPpm)), Is.EqualTo((IntPtr)0));
            Assert.That(Marshal.OffsetOf<NativeClockStats>(nameof(NativeClockStats.NumSamples)), Is.EqualTo((IntPtr)8));
            Assert.That(Marshal.OffsetOf<NativeClockStats>(nameof(NativeClockStats.PeakResidualNs)), Is.EqualTo((IntPtr)12));
            Assert.That(Marshal.OffsetOf<NativeClockStats>(nameof(NativeClockStats.NumBackwardClamps)), Is.EqualTo((IntPtr)16));
            Assert.That(Marshal.OffsetOf<NativeClockStats>(nameof(NativeClockStats.NumRejectedSamples)), Is.EqualTo((IntPtr)20));
            Assert.That(Marshal.OffsetOf<NativeClockStats>(nameof(NativeClockStats.Synced)), Is.EqualTo((IntPtr)24));
        });
    }
}
