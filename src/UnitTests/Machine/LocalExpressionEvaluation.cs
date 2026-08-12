using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Codes.Meta.Parsing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DuetAPI;
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
    /// in-memory context. These verify the SBC vs. all-fields gating, the live-collection race guard, and # / exists
    /// over real model data
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

        [SetUp]
        public void SetUp()
        {
            IOptions<Settings> settings = Options.Create(new Settings());
            _model = new DcsModel(new TestLifetime(), NullLogger<DcsModel>.Instance, settings);
            _filter = new DcsFilter(_model);
            _variableStore = new VariableStore(_model);
            _variables = new VariableSet();
            _expressions = new Expressions(_filter, _model, _variableStore);

            // volumes is an SBC-only branch, move is owned by the firmware
            _model.Volumes.Add(new Volume { FreeSpace = 12345 });
            _model.Move.Axes.Add(new Axis { Letter = 'X', MachinePosition = 42.5f });
        }

        private bool TryEval(string expression, bool evaluateAllFields, out object value)
        {
            IExpressionEvaluationContext context = new Expressions.ExpressionContext(_expressions, () => null, 0, _filter, _variables, _model, evaluateAllFields);
            using (_model.AccessReadOnly())
            {
                return MetaExpressionParser.TryEvaluate(expression, context, out value);
            }
        }

        [Test]
        public void SbcScalarResolvesLocally()
        {
            Assert.That(TryEval("volumes[0].freeSpace", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(12345L));
        }

        [Test]
        public void SbcScalarInComparisonResolvesLocally()
        {
            Assert.That(TryEval("volumes[0].freeSpace > 1000", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(true));
        }

        [Test]
        public void NonSbcScalarForwardsInDefaultMode()
        {
            Assert.That(TryEval("move.axes[0].machinePosition", false, out _), Is.False);
        }

        [Test]
        public void NonSbcScalarResolvesWhenEvaluatingAllFields()
        {
            Assert.That(TryEval("move.axes[0].machinePosition", true, out object value), Is.True);
            Assert.That(value, Is.EqualTo(42.5f));
        }

        [Test]
        public void LiveCollectionForwardsToAvoidRace()
        {
            // The whole collection is a live reference mutated by the SPI task, so it must not be handed out
            Assert.That(TryEval("volumes", false, out _), Is.False);
        }

        [Test]
        public void LengthOfSbcCollectionResolvesLocally()
        {
            Assert.That(TryEval("#volumes", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(1));
        }

        [Test]
        public void ExistsOnSbcPath()
        {
            Assert.That(TryEval("exists(volumes[0].freeSpace)", false, out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));

            Assert.That(TryEval("exists(volumes[9])", false, out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));
        }

        [Test]
        public void ExistsOnNonSbcPathForwardsInDefaultMode()
        {
            Assert.That(TryEval("exists(move.axes[0])", false, out _), Is.False);
        }

        [Test]
        public void ExistsOnNonSbcPathResolvesWhenEvaluatingAllFields()
        {
            Assert.That(TryEval("exists(move.axes[0])", true, out object value), Is.True);
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
            Assert.That(TryEval("var.foo", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(42));

            Assert.That(TryEval("var.foo + 1", false, out object sum), Is.True);
            Assert.That(sum, Is.EqualTo(43));
        }

        [Test]
        public void ParameterResolvesSeparatelyFromVariable()
        {
            _variables.TryCreateVariable("S", "local");
            _variables.SetParameters(new Dictionary<string, object> { ["S"] = "parameter" });

            Assert.That(TryEval("var.S", false, out object local), Is.True);
            Assert.That(local, Is.EqualTo("local"));

            Assert.That(TryEval("param.S", false, out object parameter), Is.True);
            Assert.That(parameter, Is.EqualTo("parameter"));
        }

        [Test]
        public void UnknownVariableIsAnError()
        {
            // RepRapFirmware throws rather than evaluating to null, and so does this
            Assert.That(() => TryEval("var.nope", false, out _), Throws.TypeOf<CodeParserException>());
            Assert.That(() => TryEval("param.nope", false, out _), Throws.TypeOf<CodeParserException>());
        }

        [Test]
        public void ExistsOnVariableAndParameter()
        {
            _variables.TryCreateVariable("here", true);
            _variables.SetParameters(new Dictionary<string, object> { ["P"] = 1 });

            Assert.That(TryEval("exists(var.here)", false, out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));

            Assert.That(TryEval("exists(var.gone)", false, out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));

            Assert.That(TryEval("exists(param.P)", false, out object parameter), Is.True);
            Assert.That(parameter, Is.EqualTo(true));

            Assert.That(TryEval("exists(param.Q)", false, out object noParameter), Is.True);
            Assert.That(noParameter, Is.EqualTo(false));
        }

        [Test]
        public void LengthOfStringVariable()
        {
            _variables.TryCreateVariable("text", "hello");
            Assert.That(TryEval("#var.text", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(5));
        }

        [Test]
        public async Task GlobalVariableResolves()
        {
            // global is not an SBC property, so it has to resolve without the caller opting into the whole mirror
            await _variableStore.TryCreateGlobalAsync("speed", 1234, default);

            Assert.That(TryEval("global.speed", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(1234));

            Assert.That(TryEval("exists(global.speed)", false, out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));

            Assert.That(TryEval("exists(global.nope)", false, out object missing), Is.True);
            Assert.That(missing, Is.EqualTo(false));
        }

        [Test]
        public async Task GlobalVariableRoundTripsItsType()
        {
            await _variableStore.TryCreateGlobalAsync("text", "hello", default);
            await _variableStore.TryCreateGlobalAsync("flag", true, default);
            await _variableStore.TryCreateGlobalAsync("ratio", 1.5f, default);
            await _variableStore.TryCreateGlobalAsync("nothing", null, default);

            Assert.That(TryEval("global.text", false, out object text), Is.True);
            Assert.That(text, Is.EqualTo("hello"));

            Assert.That(TryEval("global.flag", false, out object flag), Is.True);
            Assert.That(flag, Is.EqualTo(true));

            Assert.That(TryEval("global.ratio", false, out object ratio), Is.True);
            Assert.That(ratio, Is.EqualTo(1.5));

            Assert.That(TryEval("global.nothing", false, out object nothing), Is.True);
            Assert.That(nothing, Is.Null);

            // A null keeps the key, so it exists and is not an unknown variable
            Assert.That(TryEval("exists(global.nothing)", false, out object present), Is.True);
            Assert.That(present, Is.EqualTo(true));
        }

        [Test]
        public async Task GlobalCreateAndAssignDoNotDoEachOthersJob()
        {
            Assert.That(await _variableStore.TryCreateGlobalAsync("once", 1, default), Is.True);
            Assert.That(await _variableStore.TryCreateGlobalAsync("once", 2, default), Is.False);
            Assert.That(await _variableStore.TryAssignGlobalAsync("once", 3, default), Is.True);
            Assert.That(await _variableStore.TryAssignGlobalAsync("never", 4, default), Is.False);

            Assert.That(TryEval("global.once", false, out object value), Is.True);
            Assert.That(value, Is.EqualTo(3));
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
