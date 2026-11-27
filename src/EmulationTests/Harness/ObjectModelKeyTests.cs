using System;
using System.Text.Json.Nodes;
using DuetEmulation;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// Selecting part of an object model by an M409-style key, without an emulator
/// </summary>
[TestFixture]
public class ObjectModelKeyTests
{
    private static JsonNode Model() => JsonNode.Parse("""{"boards":[{"shortName":"MB6HC"},{"shortName":"EXP3HC","drivers":[{"status":1}]}],"state":{"status":"idle"}}""")!;

    [TestCase("state.status", "\"idle\"")]
    [TestCase("boards[1].shortName", "\"EXP3HC\"")]
    [TestCase("boards[1].drivers[0]", """{"status":1}""")]
    public void AKeySelectsThePartOfTheModelItNames(string key, string expected) =>
        Assert.That(ObjectModelKey.Select(Model(), key)!.ToJsonString(), Is.EqualTo(expected));

    [TestCase("boards[2]")]
    [TestCase("boards[0].drivers")]
    [TestCase("state.status.value")]
    [TestCase("network")]
    public void AKeyTheModelHasNothingAtSelectsNull(string key) =>
        Assert.That(ObjectModelKey.Select(Model(), key), Is.Null);

    [Test]
    public void AnEmptyKeySelectsTheWholeModel()
    {
        JsonNode model = Model();
        Assert.That(ObjectModelKey.Select(model, string.Empty), Is.SameAs(model));
    }

    [Test]
    public void ThePartSelectedCanGoIntoAnotherDocument()
    {
        JsonNode model = Model();
        JsonObject other = new() { ["board"] = ObjectModelKey.Select(model, "boards[1]") };

        Assert.That(other["board"]!["shortName"]!.GetValue<string>(), Is.EqualTo("EXP3HC"));
        Assert.That(model["boards"]![1], Is.Not.Null, "the model keeps its part");
    }

    [TestCase("boards[x]")]
    [TestCase("boards..drivers")]
    [TestCase("1boards")]
    public void AKeyThatIsNotOneIsRefused(string key) =>
        Assert.Throws<FormatException>(() => ObjectModelKey.Select(Model(), key));
}
