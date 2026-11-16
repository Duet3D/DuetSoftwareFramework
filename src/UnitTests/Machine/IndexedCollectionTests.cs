using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// The operations on an object model collection indexed by item number, where an unconfigured number is a null entry
/// </summary>
[TestFixture]
public class IndexedCollectionTests
{
    [Test]
    public void GetAtIsNullOutsideTheCollectionAndForAGap()
    {
        StaticModelCollection<Tool?> tools = [null, new Tool { Number = 1 }];

        Assert.Multiple(() =>
        {
            Assert.That(tools.GetAt(-1), Is.Null, "a negative number");
            Assert.That(tools.GetAt(0), Is.Null, "a gap");
            Assert.That(tools.GetAt(1)?.Number, Is.EqualTo(1), "an item");
            Assert.That(tools.GetAt(2), Is.Null, "past the end");
        });
    }

    [Test]
    public void SetAtPadsWithNullsUpToTheNumber()
    {
        StaticModelCollection<Tool?> tools = [];
        Tool tool = new() { Number = 2 };

        Assert.That(tools.SetAt(2, tool), Is.SameAs(tool), "the item is returned");
        Assert.That(tools, Is.EqualTo(new Tool?[] { null, null, tool }), "padded to the number");

        Tool replacement = new() { Number = 0 };
        tools.SetAt(0, replacement);
        Assert.That(tools, Is.EqualTo(new Tool?[] { replacement, null, tool }), "a lower number replaces its slot without growing");
    }

    [Test]
    public void GetOrAddAtCreatesOnlyAMissingItem()
    {
        StaticModelCollection<Fan?> fans = [];
        int created = 0;
        Fan first = fans.GetOrAddAt(1, () => { created++; return new Fan(); });
        Fan second = fans.GetOrAddAt(1, () => { created++; return new Fan(); });

        Assert.Multiple(() =>
        {
            Assert.That(fans.GetAt(0), Is.Null, "first item left null");
            Assert.That(second, Is.SameAs(first), "the existing item is returned");
            Assert.That(created, Is.EqualTo(1), "only the missing item is created");
            Assert.That(fans, Has.Count.EqualTo(2), "padded to the number");
        });
    }

    [Test]
    public void ClearAtLeavesTheNumbersAboveUnchanged()
    {
        Tool one = new() { Number = 1 }, two = new() { Number = 2 };
        StaticModelCollection<Tool?> tools = [new Tool(), one, two];

        tools.ClearAt(1);
        Assert.That(tools, Is.EqualTo(new Tool?[] { tools[0], null, two }), "the slot is emptied in place");

        tools.ClearAt(7);
        Assert.That(tools, Has.Count.EqualTo(3), "a number past the end changes nothing");
    }

    [Test]
    public void ClearAtWithTrimEndsTheCollectionAtTheHighestItemLeft()
    {
        Tool zero = new() { Number = 0 };
        StaticModelCollection<Tool?> tools = [zero, null, new Tool { Number = 2 }];

        tools.ClearAt(2, trim: true);
        Assert.That(tools, Is.EqualTo(new Tool?[] { zero }), "the gap below the cleared item goes too");

        tools.ClearAt(0, trim: true);
        Assert.That(tools, Is.Empty, "clearing the last item empties the collection");
    }
}
