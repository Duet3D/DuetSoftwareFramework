using DuetControlServer.Codes.Meta;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>Snapshots of a <see cref="VariableSet"/>, which a pause rewinds the job file with</summary>
[TestFixture]
public class VariableSetTests
{
    [Test]
    public void RestoringASnapshotPutsBackWhatHasChangedSince()
    {
        VariableSet variables = new();
        variables.TryCreateVariable("assigned", 1, depth: 1);
        variables.TryCreateVariable("deleted", 2, depth: 2);
        var snapshot = variables.Snapshot();

        variables.TryAssignVariable("assigned", 10);
        variables.DeleteVariables(depth: 2);
        variables.TryCreateVariable("created", 3);
        variables.Restore(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(variables.TryGetVariable("assigned", out object value), Is.True);
            Assert.That(value, Is.EqualTo(1));
            Assert.That(variables.TryGetVariable("deleted", out value), Is.True);
            Assert.That(value, Is.EqualTo(2));
            Assert.That(variables.TryGetVariable("created", out _), Is.False);
            Assert.That(variables.TryCreateVariable("created", 4), Is.True, "it can be created again");
        });
    }

    [Test]
    public void AssigningAnElementLeavesASnapshotAlone()
    {
        VariableSet variables = new();
        variables.TryCreateVariable("nested", new object[] { new object[] { 1, 2 }, 3 });
        var snapshot = variables.Snapshot();

        Assert.That(variables.TryAssignVariableElement("nested", [0, 1], 20), Is.EqualTo(VariableAssignment.Assigned));
        variables.TryGetVariable("nested", out object changed);
        variables.Restore(snapshot);
        variables.TryGetVariable("nested", out object restored);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.EqualTo(new object[] { new object[] { 1, 20 }, 3 }));
            Assert.That(restored, Is.EqualTo(new object[] { new object[] { 1, 2 }, 3 }));
        });
    }

    [Test]
    public void AnElementIsAssignedInPlaceWhileThereIsNoSnapshot()
    {
        VariableSet variables = new();
        object[] array = [1, 2];
        variables.TryCreateVariable("array", array);

        Assert.That(variables.TryAssignVariableElement("array", [1], 20), Is.EqualTo(VariableAssignment.Assigned));

        variables.TryGetVariable("array", out object value);
        Assert.That(value, Is.SameAs(array));
        Assert.That(array, Is.EqualTo(new object[] { 1, 20 }));
    }

    [TestCase(new[] { 0, 1, 0 }, VariableAssignment.NotAnArray)]
    [TestCase(new[] { 1, 0 }, VariableAssignment.NotAnArray)]
    [TestCase(new int[0], VariableAssignment.NotAnArray)]
    [TestCase(new[] { 2 }, VariableAssignment.IndexOutOfRange)]
    [TestCase(new[] { 0, -1 }, VariableAssignment.IndexOutOfRange)]
    public void ARefusedElementIsNotAssigned(int[] indices, VariableAssignment expected)
    {
        foreach (bool snapshot in new[] { false, true })
        {
            VariableSet variables = new();
            object[] array = [new object[] { 1, 2 }, 3];
            variables.TryCreateVariable("array", array);
            if (snapshot)
            {
                variables.Snapshot();
            }

            Assert.That(variables.TryAssignVariableElement("array", indices, 20), Is.EqualTo(expected));
            variables.TryGetVariable("array", out object value);
            Assert.That(value, Is.EqualTo(new object[] { new object[] { 1, 2 }, 3 }));
        }
    }

    [Test]
    public void ABlockDeletesTheVariablesItAndTheBlocksInItCreated()
    {
        VariableSet variables = new();
        variables.TryCreateVariable("outer", 1, depth: 1);
        variables.TryCreateVariable("block", 2, depth: 2);
        variables.TryCreateVariable("inner", 3, depth: 3);
        variables.TryAssignVariable("outer", 10);

        variables.DeleteVariables(depth: 2);

        Assert.Multiple(() =>
        {
            Assert.That(variables.TryGetVariable("outer", out object value), Is.True);
            Assert.That(value, Is.EqualTo(10));
            Assert.That(variables.TryGetVariable("block", out _), Is.False);
            Assert.That(variables.TryGetVariable("inner", out _), Is.False);
        });
    }

    [Test]
    public void RestoringASnapshotKeepsTheParameters()
    {
        VariableSet variables = new();
        var snapshot = variables.Snapshot();
        variables.SetParameters(new System.Collections.Generic.Dictionary<string, object> { ["P"] = 5 });

        variables.Restore(snapshot);

        Assert.That(variables.TryGetParameter("P", out object value), Is.True);
        Assert.That(value, Is.EqualTo(5));
    }
}
