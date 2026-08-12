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

        [SetUp]
        public void SetUp()
        {
            IOptions<Settings> settings = Options.Create(new Settings());
            _model = new DcsModel(new TestLifetime(), NullLogger<DcsModel>.Instance, settings);
            _filter = new DcsFilter(_model);
            _variableStore = new VariableStore(_model);
            _variables = new VariableSet();
            _expressions = new Expressions(_filter, _model, _variableStore);

            // volumes carries the SBC-property flag, move does not; both resolve
            _model.Volumes.Add(new Volume { FreeSpace = 12345 });
            _model.Move.Axes.Add(new Axis { Letter = 'X', MachinePosition = 42.5f });
        }

        private bool TryEval(string expression, out object value)
        {
            IExpressionEvaluationContext context = new Expressions.ExpressionContext(() => null, 0, _filter, _variables, _model);
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
        public void LiveCollectionIsRefusedToAvoidRace()
        {
            // The whole collection is a live reference mutated by the SPI task, so it must not be handed out
            Assert.That(TryEval("volumes", out _), Is.False);
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
