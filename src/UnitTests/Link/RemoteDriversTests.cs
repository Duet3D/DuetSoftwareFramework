using DuetAPI.Utility;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests.Link;

/// <summary>
/// Tests for splitting a per-driver setting across the boards that carry the drivers
/// </summary>
/// <remarks>
/// The ordering is the part worth proving. A <c>CanMessageMultipleDrivesRequest</c> says which drivers
/// it is for with a bitmap and then carries the values in a plain array, and the receiving board pairs
/// the n'th value with the n'th set bit. Values that are not in ascending driver order are therefore
/// not merely untidy: they are applied to the wrong motors
/// </remarks>
[TestFixture]
public class RemoteDriversTests
{
    private static RemoteDrivers.DriverValue<int> Value(int board, int port, int value)
        => new(new DriverId(board, port), value);

    [Test]
    public void DriversAreSplitByTheBoardThatCarriesThem()
    {
        List<(byte Board, ushort DriverBitmap, int[] Values)> groups =
        [
            .. RemoteDrivers.GroupByBoard<int>([Value(0, 0, 10), Value(1, 2, 20), Value(0, 1, 30)])
        ];

        Assert.That(groups, Has.Count.EqualTo(2));

        (byte board0, ushort bitmap0, int[] values0) = groups.Single(group => group.Board == 0);
        Assert.That(board0, Is.EqualTo(0));
        Assert.That(bitmap0, Is.EqualTo(0b011), "drivers 0 and 1");
        Assert.That(values0, Is.EqualTo(new[] { 10, 30 }));

        (_, ushort bitmap1, int[] values1) = groups.Single(group => group.Board == 1);
        Assert.That(bitmap1, Is.EqualTo(0b100), "driver 2");
        Assert.That(values1, Is.EqualTo(new[] { 20 }));
    }

    [Test]
    public void ValuesAreOrderedByDriverNumberRatherThanByHowTheyWereGiven()
    {
        // The bitmap has no way of saying which value belongs to which bit, so the board relies on
        // ascending order. Given out of order, driver 0 would otherwise be set to driver 2's value
        (_, ushort bitmap, int[] values) = RemoteDrivers.GroupByBoard<int>(
            [Value(0, 2, 22), Value(0, 0, 0), Value(0, 1, 11)]).Single();

        Assert.That(bitmap, Is.EqualTo(0b111));
        Assert.That(values, Is.EqualTo(new[] { 0, 11, 22 }));
    }

    [Test]
    public void TheSameDriverNamedTwiceKeepsTheLastValue()
    {
        // A bit can only be set once, so a repeated driver would otherwise leave one value in the
        // array with no bit of its own and shift every later value onto the wrong motor
        (_, ushort bitmap, int[] values) = RemoteDrivers.GroupByBoard<int>(
            [Value(0, 0, 1), Value(0, 1, 2), Value(0, 0, 99)]).Single();

        Assert.That(bitmap, Is.EqualTo(0b011));
        Assert.That(values, Is.EqualTo(new[] { 99, 2 }));
    }

    [Test]
    public void ABoardWithMoreDriversThanOneMessageHoldsIsSplitAcrossSeveral()
    {
        List<(byte Board, ushort DriverBitmap, int[] Values)> groups =
        [
            .. RemoteDrivers.GroupByBoard<int>([.. Enumerable.Range(0, 10).Select(driver => Value(1, driver, driver))])
        ];

        Assert.That(groups, Has.Count.EqualTo(2), "the value array holds eight");
        Assert.That(groups[0].DriverBitmap, Is.EqualTo(0b0000_0000_1111_1111));
        Assert.That(groups[0].Values, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
        Assert.That(groups[1].DriverBitmap, Is.EqualTo(0b0000_0011_0000_0000));
        Assert.That(groups[1].Values, Is.EqualTo(new[] { 8, 9 }));
        Assert.That(groups.Select(group => group.Board), Is.All.EqualTo((byte)1));
    }

    [Test]
    public void TheGroupingCarriesWhateverValueTypeAMessageNeeds()
    {
        // Driver state control sends a mode and an idle percentage per driver rather than one number,
        // so the grouping has to order a composite value by driver just as it does a scalar
        RemoteDrivers.DriverValue<(ushort Mode, ushort Idle)>[] states =
        [
            new(new DriverId(2, 1), (2, 60)),
            new(new DriverId(2, 0), (0, 30))
        ];

        (byte board, ushort bitmap, (ushort Mode, ushort Idle)[] values) = RemoteDrivers.GroupByBoard(states).Single();
        Assert.That(board, Is.EqualTo(2));
        Assert.That(bitmap, Is.EqualTo(0b011));
        Assert.That(values, Is.EqualTo(new[] { ((ushort)0, (ushort)30), ((ushort)2, (ushort)60) }));
    }

    [Test]
    public void NoDriversProducesNoMessages()
    {
        Assert.That(RemoteDrivers.GroupByBoard<int>([]), Is.Empty);
    }

    [Test]
    public void ADriverTheBitmapCannotAddressIsRejected()
    {
        // The bitmap is 16 bits wide, so driver 16 has no bit to set and would silently go nowhere
        Assert.That(() => RemoteDrivers.GroupByBoard<int>([Value(0, 16, 1)]).ToList(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ACanAddressOutsideTheBusIsRejected()
    {
        Assert.That(() => RemoteDrivers.GroupByBoard<int>([Value(500, 0, 1)]).ToList(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
