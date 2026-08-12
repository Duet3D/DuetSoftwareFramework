using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Codes.Meta.Parsing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DuetAPI;
using DuetAPI.Utility;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DcsModel = DuetControlServer.Model.ObjectModel;
using DcsFilter = DuetControlServer.Model.Filter;

namespace UnitTests.Machine
{
    /// <summary>
    /// Integration tests for the SBC-side expression evaluator wired against a real object model and filter (the
    /// production <see cref="Expressions.ExpressionContext"/>), as opposed to the parser-only tests that use an
    /// in-memory context. These verify resolution over real model data - object model paths, variables, the
    /// live-collection race guard, and # / exists
    /// </summary>
    [TestFixture]
    public class LocalExpressionEvaluation
    {
        private sealed class TestLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        private DcsModel _model;
        private DcsFilter _filter;
        private Expressions _expressions;
        private VariableStore _variableStore;
        private VariableSet _variables;
        private LastCodeResult _lastCodeResult;

        [SetUp]
        public void SetUp()
        {
            IOptions<Settings> settings = Options.Create(new Settings());
            _model = new DcsModel(new TestLifetime(), NullLogger<DcsModel>.Instance, settings);
            _filter = new DcsFilter(_model);
            _variableStore = new VariableStore(_model);
            _variables = new VariableSet();
            _lastCodeResult = new LastCodeResult();
            _expressions = new Expressions(_filter, _model, _variableStore, _lastCodeResult);

            // volumes carries the SBC-property flag, move does not; both resolve
            _model.Volumes.Add(new Volume { FreeSpace = 12345 });
            _model.Move.Axes.Add(new Axis { Letter = 'X', MachinePosition = 42.5f });
        }

        private bool TryEval(string expression, out object value)
        {
            IExpressionEvaluationContext context = new Expressions.ExpressionContext(() => null, 0, _lastCodeResult.Get(CodeChannel.Trigger), _filter, _variables, _model);
            using (_model.AccessReadOnly())
            {
                return MetaExpressionParser.TryEvaluate(expression, context, out value);
            }
        }

        [Test]
        public void SbcScalarResolvesLocally()
        {
            Assert.That(TryEval("volumes[0].freeSpace", out object value), Is.True);
            Assert.That(value, Is.EqualTo(12345L));
        }

        [Test]
        public void SbcScalarInComparisonResolvesLocally()
        {
            Assert.That(TryEval("volumes[0].freeSpace > 1000", out object value), Is.True);
            Assert.That(value, Is.EqualTo(true));
        }

        [Test]
        public void NonSbcScalarResolves()
        {
            // The whole object model resolves here; it used to be only the SBC-owned branches, because
            // the firmware answered for the rest
            Assert.That(TryEval("move.axes[0].machinePosition", out object value), Is.True);
            Assert.That(value, Is.EqualTo(42.5f));
        }

        [Test]
        public void LiveCollectionIsCopiedRatherThanHandedOut()
        {
            // The collection is a live reference the update task mutates, so what escapes is a copy of it
            // holding stand-ins for the objects inside
            Assert.That(TryEval("volumes", out object volumes), Is.True);
            Assert.That(volumes, Is.InstanceOf<object[]>());
            Assert.That(((object[])volumes), Has.Length.EqualTo(1));
            Assert.That(((object[])volumes)[0]?.ToString(), Is.EqualTo("{object}"));
        }

        [Test]
        public void LengthOfSbcCollectionResolvesLocally()
        {
            Assert.That(TryEval("#volumes", out object value), Is.True);
            Assert.That(value, Is.EqualTo(1));
        }

        [Test]
        public void ExistsOnSbcPath()
        {
            Assert.That(TryEval("exists(volumes[0].freeSpace)", out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));

            Assert.That(TryEval("exists(volumes[9])", out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));
        }

        [Test]
        public void ExistsOnNonSbcPath()
        {
            Assert.That(TryEval("exists(move.axes[0])", out object value), Is.True);
            Assert.That(value, Is.EqualTo(true));
        }

        [Test]
        public void SbcPathsAreDetected()
        {
            Assert.That(_expressions.IsSbcExpression("volumes", false), Is.True);
            Assert.That(_expressions.IsSbcExpression("volumes[0].freeSpace", false), Is.True);
            Assert.That(_expressions.IsSbcExpression("messages", false), Is.True);
            Assert.That(_expressions.IsSbcExpression("directories.system", false), Is.True);

            // Case-insensitive since the object model is addressed in camelCase
            Assert.That(_expressions.IsSbcExpression("Directories.Web", false), Is.True);

            // Special variables are always resolved locally
            Assert.That(_expressions.IsSbcExpression("iterations", false), Is.True);
            Assert.That(_expressions.IsSbcExpression("line", false), Is.True);
        }

        [Test]
        public void NonSbcPathsAreNotDetected()
        {
            Assert.That(_expressions.IsSbcExpression("move.axes[0].machinePosition", false), Is.False);
            Assert.That(_expressions.IsSbcExpression("heat.heaters[0].current", false), Is.False);
            Assert.That(_expressions.IsSbcExpression("directories.filaments", false), Is.False);
            Assert.That(_expressions.IsSbcExpression("state", false), Is.False);
        }

        [Test]
        public void LocalVariableResolves()
        {
            _variables.TryCreateVariable("foo", 42);
            Assert.That(TryEval("var.foo", out object value), Is.True);
            Assert.That(value, Is.EqualTo(42));

            Assert.That(TryEval("var.foo + 1", out object sum), Is.True);
            Assert.That(sum, Is.EqualTo(43));
        }

        [Test]
        public void ParameterResolvesSeparatelyFromVariable()
        {
            _variables.TryCreateVariable("S", "local");
            _variables.SetParameters(new Dictionary<string, object> { ["S"] = "parameter" });

            Assert.That(TryEval("var.S", out object local), Is.True);
            Assert.That(local, Is.EqualTo("local"));

            Assert.That(TryEval("param.S", out object parameter), Is.True);
            Assert.That(parameter, Is.EqualTo("parameter"));
        }

        [Test]
        public void UnknownVariableIsAnError()
        {
            // RepRapFirmware throws rather than evaluating to null, and so does this
            Assert.That(() => TryEval("var.nope", out _), Throws.TypeOf<CodeParserException>());
            Assert.That(() => TryEval("param.nope", out _), Throws.TypeOf<CodeParserException>());
        }

        [Test]
        public void ExistsOnVariableAndParameter()
        {
            _variables.TryCreateVariable("here", true);
            _variables.SetParameters(new Dictionary<string, object> { ["P"] = 1 });

            Assert.That(TryEval("exists(var.here)", out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));

            Assert.That(TryEval("exists(var.gone)", out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));

            Assert.That(TryEval("exists(param.P)", out object parameter), Is.True);
            Assert.That(parameter, Is.EqualTo(true));

            Assert.That(TryEval("exists(param.Q)", out object noParameter), Is.True);
            Assert.That(noParameter, Is.EqualTo(false));
        }

        [Test]
        public void LengthOfStringVariable()
        {
            _variables.TryCreateVariable("text", "hello");
            Assert.That(TryEval("#var.text", out object value), Is.True);
            Assert.That(value, Is.EqualTo(5));
        }

        [Test]
        public async Task GlobalVariableResolves()
        {
            // global is not an SBC property, so it has to resolve without the caller opting into the whole mirror
            await _variableStore.TryCreateGlobalAsync("speed", 1234, default);

            Assert.That(TryEval("global.speed", out object value), Is.True);
            Assert.That(value, Is.EqualTo(1234));

            Assert.That(TryEval("exists(global.speed)", out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));

            Assert.That(TryEval("exists(global.nope)", out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));
        }

        [Test]
        public async Task GlobalVariableRoundTripsItsType()
        {
            await _variableStore.TryCreateGlobalAsync("text", "hello", default);
            await _variableStore.TryCreateGlobalAsync("flag", true, default);
            await _variableStore.TryCreateGlobalAsync("ratio", 1.5f, default);
            await _variableStore.TryCreateGlobalAsync("nothing", null, default);

            Assert.That(TryEval("global.text", out object text), Is.True);
            Assert.That(text, Is.EqualTo("hello"));

            Assert.That(TryEval("global.flag", out object flag), Is.True);
            Assert.That(flag, Is.EqualTo(true));

            Assert.That(TryEval("global.ratio", out object ratio), Is.True);
            Assert.That(ratio, Is.EqualTo(1.5));

            Assert.That(TryEval("global.nothing", out object nothing), Is.True);
            Assert.That(nothing, Is.Null);

            // A null keeps the key, so it exists and is not an unknown variable
            Assert.That(TryEval("exists(global.nothing)", out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));
        }

        [Test]
        public async Task GlobalCreateAndAssignDoNotDoEachOthersJob()
        {
            Assert.That(await _variableStore.TryCreateGlobalAsync("once", 1, default), Is.True);
            Assert.That(await _variableStore.TryCreateGlobalAsync("once", 2, default), Is.False);
            Assert.That(await _variableStore.TryAssignGlobalAsync("once", 3, default), Is.True);
            Assert.That(await _variableStore.TryAssignGlobalAsync("never", 4, default), Is.False);

            Assert.That(TryEval("global.once", out object value), Is.True);
            Assert.That(value, Is.EqualTo(3));
        }

        [Test]
        public void ArrayVariableIsIndexedAndMeasured()
        {
            _variables.TryCreateVariable("speeds", new object?[] { 10, 20, 30 });

            Assert.That(TryEval("var.speeds[1]", out object element), Is.True);
            Assert.That(element, Is.EqualTo(20));

            Assert.That(TryEval("#var.speeds", out object length), Is.True);
            Assert.That(length, Is.EqualTo(3));

            Assert.That(TryEval("var.speeds[1] + var.speeds[2]", out object sum), Is.True);
            Assert.That(sum, Is.EqualTo(50));
        }

        [Test]
        public void NestedArrayVariableIsIndexed()
        {
            _variables.TryCreateVariable("grid", new object?[] { new object?[] { 1, 2 }, new object?[] { 3, 4 } });

            Assert.That(TryEval("var.grid[1][0]", out object element), Is.True);
            Assert.That(element, Is.EqualTo(3));
        }

        [Test]
        public void IndexPastTheEndOfAVariableIsAnError()
        {
            _variables.TryCreateVariable("speeds", new object?[] { 10, 20 });
            Assert.That(() => TryEval("var.speeds[5]", out _), Throws.TypeOf<CodeParserException>());

            // exists() answers rather than throwing, as it does for a name that is not there
            Assert.That(TryEval("exists(var.speeds[5])", out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));

            Assert.That(TryEval("exists(var.speeds[1])", out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));
        }

        [Test]
        public void StringVariableIsIndexed()
        {
            _variables.TryCreateVariable("text", "hello");
            Assert.That(TryEval("var.text[1]", out object element), Is.True);
            Assert.That(element, Is.EqualTo('e'));
        }

        [Test]
        public async Task GlobalArrayRoundTripsAndIsIndexed()
        {
            await _variableStore.TryCreateGlobalAsync("speeds", new object?[] { 1, "two", 3.5f, null }, default);

            Assert.That(TryEval("global.speeds[1]", out object element), Is.True);
            Assert.That(element, Is.EqualTo("two"));

            Assert.That(TryEval("#global.speeds", out object length), Is.True);
            Assert.That(length, Is.EqualTo(4));

            Assert.That(TryEval("global.speeds[3]", out object nothing), Is.True);
            Assert.That(nothing, Is.Null);
        }

        [Test]
        public void AssignToAnArrayElement()
        {
            _variables.TryCreateVariable("speeds", new object?[] { 10, 20, 30 });

            Assert.That(_variables.TryAssignVariableElement("speeds", [1], 99), Is.EqualTo(VariableAssignment.Assigned));
            Assert.That(TryEval("var.speeds[1]", out object element), Is.True);
            Assert.That(element, Is.EqualTo(99));

            Assert.That(_variables.TryAssignVariableElement("speeds", [5], 0), Is.EqualTo(VariableAssignment.IndexOutOfRange));
            Assert.That(_variables.TryAssignVariableElement("nope", [0], 0), Is.EqualTo(VariableAssignment.UnknownVariable));

            _variables.TryCreateVariable("scalar", 1);
            Assert.That(_variables.TryAssignVariableElement("scalar", [0], 0), Is.EqualTo(VariableAssignment.NotAnArray));
        }

        [Test]
        public async Task AssignToAGlobalArrayElement()
        {
            await _variableStore.TryCreateGlobalAsync("speeds", new object?[] { 10, 20 }, default);

            Assert.That(await _variableStore.TryAssignGlobalElementAsync("speeds", [0], 99, default), Is.EqualTo(VariableAssignment.Assigned));
            Assert.That(TryEval("global.speeds[0]", out object element), Is.True);
            Assert.That(element, Is.EqualTo(99));

            Assert.That(await _variableStore.TryAssignGlobalElementAsync("speeds", [7], 0, default), Is.EqualTo(VariableAssignment.IndexOutOfRange));
            Assert.That(await _variableStore.TryAssignGlobalElementAsync("nope", [0], 0, default), Is.EqualTo(VariableAssignment.UnknownVariable));
        }

        [Test]
        public void IndexedNamesAreSplitOnce()
        {
            Assert.That(VariableStore.TrySplitIndexedName("speeds", out string name, out IReadOnlyList<string> indices), Is.True);
            Assert.That(name, Is.EqualTo("speeds"));
            Assert.That(indices, Is.Empty);

            Assert.That(VariableStore.TrySplitIndexedName("grid[1][20]", out name, out indices), Is.True);
            Assert.That(name, Is.EqualTo("grid"));
            Assert.That(indices, Is.EqualTo(new[] { "1", "20" }));

            // What is inside the brackets is handed back as written, for the caller to evaluate
            Assert.That(VariableStore.TrySplitIndexedName("speeds[var.i + 1]", out name, out indices), Is.True);
            Assert.That(name, Is.EqualTo("speeds"));
            Assert.That(indices, Is.EqualTo(new[] { "var.i + 1" }));
            Assert.That(VariableStore.TryParseIndices(indices, out _), Is.False);

            Assert.That(VariableStore.TryParseIndices(["4"], out IReadOnlyList<int> parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(new[] { 4 }));

            // A field of a variable is not a thing, and neither is a name that does not close its brackets
            Assert.That(VariableStore.TrySplitIndexedName("speeds.first", out _, out _), Is.False);
            Assert.That(VariableStore.TrySplitIndexedName("speeds[0", out _, out _), Is.False);
            Assert.That(VariableStore.TrySplitIndexedName("speeds[0]x", out _, out _), Is.False);
            Assert.That(VariableStore.TrySplitIndexedName(string.Empty, out _, out _), Is.False);
        }

        [Test]
        public void ScalarCollectionIsSnapshotted()
        {
            // A collection of scalars is copied under the lock, so the whole of it can be handed out
            _model.Move.Axes[0].WorkplaceOffsets.Add(1.5f);
            _model.Move.Axes[0].WorkplaceOffsets.Add(2.5f);

            Assert.That(TryEval("#move.axes[0].workplaceOffsets", out object length), Is.True);
            Assert.That(length, Is.EqualTo(2));

            Assert.That(TryEval("move.axes[0].workplaceOffsets[1]", out object element), Is.True);
            Assert.That(element, Is.EqualTo(2.5f));

            Assert.That(TryEval("move.axes[0].workplaceOffsets", out object whole), Is.True);
            Assert.That(whole, Is.EqualTo(new object[] { 1.5f, 2.5f }));
        }

        [Test]
        public void CharComparesAgainstAString()
        {
            // move.axes[].letter is a char, and a char compared against a string is converted to one
            Assert.That(TryEval("move.axes[0].letter == \"X\"", out object equal), Is.True);
            Assert.That(equal, Is.EqualTo(true));

            Assert.That(TryEval("move.axes[0].letter == \"Y\"", out object notEqual), Is.True);
            Assert.That(notEqual, Is.EqualTo(false));

            Assert.That(TryEval("move.axes[0].letter != \"Y\"", out object inverted), Is.True);
            Assert.That(inverted, Is.EqualTo(true));

            // A char converts to a string wherever one is wanted
            Assert.That(TryEval("move.axes[0].letter ^ \"1\"", out object concatenated), Is.True);
            Assert.That(concatenated, Is.EqualTo("X1"));
        }

        [Test]
        public void CharAgainstACharIsRefusedAsInTheFirmware()
        {
            // Two chars are left alone by the type balancing and the equality operator has no case for
            // them, so RepRapFirmware refuses this and so does this parser. The message names the types,
            // which is what tells the reader to write == "X" instead
            Assert.That(() => TryEval("move.axes[0].letter == 'X'", out _),
                        Throws.TypeOf<CodeParserException>().With.Message.Contains("got char"));
        }

        [Test]
        public void ObjectResolvesAsAStandIn()
        {
            // RepRapFirmware prints an object as "{object}" and does nothing else with one, so that is
            // what an expression naming an object holds. The object itself must not escape: the update
            // task mutates it after the model lock is released
            Assert.That(TryEval("move", out object model), Is.True);
            Assert.That(model?.ToString(), Is.EqualTo("{object}"));

            Assert.That(TryEval("move.axes[0]", out object axis), Is.True);
            Assert.That(axis?.ToString(), Is.EqualTo("{object}"));

            // A collection of them is an array of stand-ins
            Assert.That(TryEval("move.axes", out object axes), Is.True);
            Assert.That(axes, Is.InstanceOf<object[]>());
            Assert.That(((object[])axes)[0]?.ToString(), Is.EqualTo("{object}"));

            // Comparing them is refused in RepRapFirmware's words
            Assert.That(() => TryEval("move == move", out _),
                        Throws.TypeOf<CodeParserException>().With.Message.Contains("cannot compare objects"));
        }

        [Test]
        public void ObjectsCannotBeAssignedToAVariable()
        {
            // A stand-in holds nothing, so storing one - or an array of them - would give a macro
            // "{object}" where it expected the machine. Both are refused where the assignment is made
            Assert.That(TryEval("move", out object model), Is.True);
            Assert.That(ObjectModelValue.OccursIn(model), Is.True);

            Assert.That(TryEval("move.axes", out object axes), Is.True);
            Assert.That(ObjectModelValue.OccursIn(axes), Is.True);

            // Nested, because an array of arrays of them is no more useful
            Assert.That(ObjectModelValue.OccursIn(new object?[] { new object?[] { model } }), Is.True);

            // What can be stored is unaffected
            Assert.That(TryEval("move.axes[0].machinePosition", out object position), Is.True);
            Assert.That(ObjectModelValue.OccursIn(position), Is.False);
            Assert.That(ObjectModelValue.OccursIn(new object?[] { 1, "two", null }), Is.False);
        }

        [Test]
        public void ArrayRendersInSquareBrackets()
        {
            // RepRapFirmware writes an array literal as {1,2,3} and prints one as [1,2,3]
            Assert.That(TryEval("\"\" ^ {1,2,3}", out object rendered), Is.True);
            Assert.That(rendered, Is.EqualTo("[1,2,3]"));

            Assert.That(TryEval("\"\" ^ {1,}", out object single), Is.True);
            Assert.That(single, Is.EqualTo("[1]"));
        }

        [Test]
        public void DriverIdCollectionResolves()
        {
            _model.Move.Axes[0].Drivers.Add(new DriverId(1, 2));

            Assert.That(TryEval("move.axes[0].drivers[0]", out object driver), Is.True);
            Assert.That(driver, Is.EqualTo(new DriverId(1, 2)));

            Assert.That(TryEval("#move.axes[0].drivers", out object count), Is.True);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void EnumResolvesAsItsObjectModelName()
        {
            // state.status is "processing" in the object model, and that is what a macro compares against
            _model.State.Status = MachineStatus.Processing;

            Assert.That(TryEval("state.status", out object status), Is.True);
            Assert.That(status, Is.EqualTo("processing"));

            Assert.That(TryEval("state.status == \"processing\"", out object equal), Is.True);
            Assert.That(equal, Is.EqualTo(true));
        }

        [Test]
        public void ResultFollowsTheChannelItIsReadOn()
        {
            // What a macro checks after running a code: M98 P"probe.g" then if result != 0
            Assert.That(_lastCodeResult.Get(CodeChannel.Trigger), Is.EqualTo(LastCodeResult.Ok));

            _lastCodeResult.Set(CodeChannel.Trigger, new Message(MessageType.Error, "it failed"));
            Assert.That(_lastCodeResult.Get(CodeChannel.Trigger), Is.EqualTo(LastCodeResult.Error));
            Assert.That(TryEval("result", out object failed), Is.True);
            Assert.That(failed, Is.EqualTo(LastCodeResult.Error));

            _lastCodeResult.Set(CodeChannel.Trigger, new Message(MessageType.Warning, "careful"));
            Assert.That(TryEval("result == 1", out object warned), Is.True);
            Assert.That(warned, Is.EqualTo(true));

            // Another channel's codes do not change what this one sees
            _lastCodeResult.Set(CodeChannel.HTTP, new Message(MessageType.Error, "elsewhere"));
            Assert.That(TryEval("result", out object unchanged), Is.True);
            Assert.That(unchanged, Is.EqualTo(LastCodeResult.Warning));

            _lastCodeResult.Set(CodeChannel.Trigger, new Message(MessageType.Success, "done"));
            Assert.That(TryEval("result", out object succeeded), Is.True);
            Assert.That(succeeded, Is.EqualTo(LastCodeResult.Ok));
        }

        [Test]
        public void UnknownPathsAreNotSbcExpressions()
        {
            Assert.That(_expressions.IsSbcExpression("foo", false), Is.False);
            Assert.That(_expressions.IsSbcExpression("move.foo.bar", false), Is.False);
            Assert.That(_expressions.IsSbcExpression(string.Empty, false), Is.False);
            Assert.That(_expressions.IsSbcExpression("move..axes", false), Is.False);

            // No custom functions are registered without plugins
            Assert.That(_expressions.IsSbcExpression("foo", true), Is.False);
        }
    }
}
