using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Codes.Meta.Parsing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Threading;
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

        [SetUp]
        public void SetUp()
        {
            IOptions<Settings> settings = Options.Create(new Settings());
            _model = new DcsModel(new TestLifetime(), NullLogger<DcsModel>.Instance, settings);
            _filter = new DcsFilter(_model);
            _expressions = new Expressions(_filter, _model, null);

            // volumes is an SBC-only branch, move is owned by the firmware
            _model.Volumes.Add(new Volume { FreeSpace = 12345 });
            _model.Move.Axes.Add(new Axis { Letter = 'X', MachinePosition = 42.5f });
        }

        private bool TryEval(string expression, bool evaluateAllFields, out object value)
        {
            IExpressionEvaluationContext context = new Expressions.ExpressionContext(_expressions, () => null, 0, _filter, evaluateAllFields);
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
    }
}
