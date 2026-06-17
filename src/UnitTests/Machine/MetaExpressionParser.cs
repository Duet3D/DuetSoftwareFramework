using System;
using System.Collections.Generic;
using DuetAPI;
using DuetControlServer.Codes.Meta.Parsing;
using NUnit.Framework;

namespace UnitTests.Machine
{
    /// <summary>
    /// Tests for the SBC-side meta expression evaluator. These pin down bit-for-bit parity with the firmware parser:
    /// operator precedence, numeric promotion (int vs. float), coercion, literals, escaping and error wording.
    /// </summary>
    [TestFixture]
    public class MetaExpressionParserTests
    {
        private static object Eval(string expression, IExpressionEvaluationContext context = null)
        {
            Assert.That(MetaExpressionParser.TryEvaluate(expression, context, out object value), Is.True, "expected the expression to be resolved on the SBC");
            return value;
        }

        private static void AssertForwards(string expression, IExpressionEvaluationContext context = null)
        {
            Assert.That(MetaExpressionParser.TryEvaluate(expression, context, out _), Is.False, "expected the expression to be forwarded to the firmware");
        }

        #region Numbers
        [Test]
        public void IntegerLiteral()
        {
            object result = Eval("42");
            Assert.That(result, Is.EqualTo(42));
            Assert.That(result, Is.TypeOf<int>());
        }

        [Test]
        public void LargeIntegerBecomesUnsigned()
        {
            object result = Eval("3000000000");
            Assert.That(result, Is.EqualTo(3000000000u));
            Assert.That(result, Is.TypeOf<uint>());
        }

        [Test]
        public void HugeIntegerBecomesFloat()
        {
            object result = Eval("9000000000");
            Assert.That(result, Is.TypeOf<float>());
        }

        [Test]
        public void FloatLiteral()
        {
            object result = Eval("3.5");
            Assert.That(result, Is.EqualTo(3.5f));
            Assert.That(result, Is.TypeOf<float>());
        }

        [Test]
        public void ExponentLiteral()
        {
            Assert.That(Eval("1e3"), Is.EqualTo(1000.0f));
            Assert.That(Eval("1.5e-1"), Is.EqualTo(0.15f));
        }

        [Test]
        public void HexLiteral()
        {
            object result = Eval("0xFF");
            Assert.That(result, Is.EqualTo(255));
            Assert.That(result, Is.TypeOf<int>());
        }
        #endregion

        #region Arithmetic
        [Test]
        public void IntegerArithmeticStaysInteger()
        {
            object result = Eval("2 * 3");
            Assert.That(result, Is.EqualTo(6));
            Assert.That(result, Is.TypeOf<int>());
        }

        [Test]
        public void DivisionAlwaysProducesFloat()
        {
            object result = Eval("6 / 2");
            Assert.That(result, Is.EqualTo(3.0f));
            Assert.That(result, Is.TypeOf<float>());
        }

        [Test]
        public void MixedArithmeticPromotesToFloat()
        {
            object result = Eval("2 + 1.5");
            Assert.That(result, Is.EqualTo(3.5f));
            Assert.That(result, Is.TypeOf<float>());
        }

        [Test]
        public void OperatorPrecedence()
        {
            Assert.That(Eval("1 + 2 * 3"), Is.EqualTo(7));
            Assert.That(Eval("2 * 3 + 1"), Is.EqualTo(7));
            Assert.That(Eval("(1 + 2) * 3"), Is.EqualTo(9));
            Assert.That(Eval("10 - 2 - 3"), Is.EqualTo(5));
        }

        [Test]
        public void UnaryOperators()
        {
            Assert.That(Eval("-5"), Is.EqualTo(-5));
            Assert.That(Eval("-3.5"), Is.EqualTo(-3.5f));
            Assert.That(Eval("- -5"), Is.EqualTo(5));
            Assert.That(Eval("!true"), Is.EqualTo(false));
            Assert.That(Eval("!(1 > 2)"), Is.EqualTo(true));
        }
        #endregion

        #region Comparisons and logic
        [Test]
        public void NumericComparisons()
        {
            Assert.That(Eval("1 < 2"), Is.EqualTo(true));
            Assert.That(Eval("2 <= 2"), Is.EqualTo(true));
            Assert.That(Eval("3 > 4"), Is.EqualTo(false));
            Assert.That(Eval("3 >= 3"), Is.EqualTo(true));
            Assert.That(Eval("1 = 1"), Is.EqualTo(true));
            Assert.That(Eval("1 == 2"), Is.EqualTo(false));
            Assert.That(Eval("1 != 2"), Is.EqualTo(true));
            Assert.That(Eval("1 < 2.0"), Is.EqualTo(true));
            Assert.That(Eval("3 == 3.0"), Is.EqualTo(true));
        }

        [Test]
        public void BooleanComparisons()
        {
            Assert.That(Eval("true > false"), Is.EqualTo(true));
            Assert.That(Eval("false < true"), Is.EqualTo(true));
            Assert.That(Eval("true == true"), Is.EqualTo(true));
        }

        [Test]
        public void StringComparisons()
        {
            Assert.That(Eval("\"abc\" == \"abc\""), Is.EqualTo(true));
            Assert.That(Eval("\"abc\" != \"abd\""), Is.EqualTo(true));
        }

        [Test]
        public void LogicalOperators()
        {
            Assert.That(Eval("true & false"), Is.EqualTo(false));
            Assert.That(Eval("true | false"), Is.EqualTo(true));
            Assert.That(Eval("true && true"), Is.EqualTo(true));
            Assert.That(Eval("false || false"), Is.EqualTo(false));
        }

        [Test]
        public void LogicalAndComparisonPrecedence()
        {
            Assert.That(Eval("1 < 2 & 3 < 4"), Is.EqualTo(true));
            Assert.That(Eval("1 + 2 < 4"), Is.EqualTo(true));
        }

        [Test]
        public void TernaryOperator()
        {
            Assert.That(Eval("1 < 2 ? 10 : 20"), Is.EqualTo(10));
            Assert.That(Eval("1 > 2 ? 10 : 20"), Is.EqualTo(20));
            Assert.That(Eval("true ? 1 : false ? 2 : 3"), Is.EqualTo(1));
            Assert.That(Eval("false ? 1 : true ? 2 : 3"), Is.EqualTo(2));
        }
        #endregion

        #region Null
        [Test]
        public void NullComparisons()
        {
            Assert.That(Eval("null == null"), Is.EqualTo(true));
            Assert.That(Eval("null == 1"), Is.EqualTo(false));
            Assert.That(Eval("1 == null"), Is.EqualTo(false));
            Assert.That(Eval("null != 1"), Is.EqualTo(true));
        }

        [Test]
        public void NullLiteral()
        {
            Assert.That(Eval("null"), Is.Null);
        }
        #endregion

        #region Strings and characters
        [Test]
        public void StringLiteral()
        {
            Assert.That(Eval("\"hello\""), Is.EqualTo("hello"));
        }

        [Test]
        public void StringEscapedQuotes()
        {
            Assert.That(Eval("\"say \"\"hi\"\"\""), Is.EqualTo("say \"hi\""));
        }

        [Test]
        public void StringSingleQuoteForcesLowerCase()
        {
            Assert.That(Eval("\"'ABC\""), Is.EqualTo("aBC"));
        }

        [Test]
        public void CharacterLiteral()
        {
            object result = Eval("'x'");
            Assert.That(result, Is.EqualTo('x'));
            Assert.That(result, Is.TypeOf<char>());
        }

        [Test]
        public void Concatenation()
        {
            Assert.That(Eval("\"a\" ^ \"b\""), Is.EqualTo("ab"));
            Assert.That(Eval("\"x\" ^ 5"), Is.EqualTo("x5"));
            Assert.That(Eval("1 ^ 2"), Is.EqualTo("12"));
            Assert.That(Eval("\"v\" ^ true"), Is.EqualTo("vtrue"));
        }

        [Test]
        public void StringIndexing()
        {
            object result = Eval("\"abc\"[1]");
            Assert.That(result, Is.EqualTo('b'));
            Assert.That(result, Is.TypeOf<char>());
        }

        [Test]
        public void StringLength()
        {
            Assert.That(Eval("#\"hello\""), Is.EqualTo(5));
        }
        #endregion

        #region Arrays
        [Test]
        public void ArrayLiteral()
        {
            Assert.That(Eval("[1, 2, 3]"), Is.EqualTo(new object[] { 1, 2, 3 }));
        }

        [Test]
        public void EmptyArray()
        {
            Assert.That(Eval("[]"), Is.EqualTo(Array.Empty<object>()));
        }

        [Test]
        public void SingleElementArray()
        {
            Assert.That(Eval("[5]"), Is.EqualTo(new object[] { 5 }));
            Assert.That(Eval("{5,}"), Is.EqualTo(new object[] { 5 }));
        }

        [Test]
        public void BracesAreGroupingNotArray()
        {
            Assert.That(Eval("{5}"), Is.EqualTo(5));
            Assert.That(Eval("{1 + 2}"), Is.EqualTo(3));
        }

        [Test]
        public void ArrayIndexing()
        {
            Assert.That(Eval("[10, 20, 30][1]"), Is.EqualTo(20));
            Assert.That(Eval("[10, 20, 30][1 + 1]"), Is.EqualTo(30));
        }

        [Test]
        public void ArrayLength()
        {
            Assert.That(Eval("#[1, 2, 3, 4]"), Is.EqualTo(4));
        }

        [Test]
        public void ArrayConcatenation()
        {
            Assert.That(Eval("[1, 2] ^ [3, 4]"), Is.EqualTo(new object[] { 1, 2, 3, 4 }));
        }
        #endregion

        #region Built-in functions
        [Test]
        public void MathFunctions()
        {
            Assert.That(Eval("sqrt(16)"), Is.EqualTo(4.0f));
            Assert.That(Eval("abs(-5)"), Is.EqualTo(5));
            Assert.That(Eval("abs(-5)"), Is.TypeOf<int>());
            Assert.That(Eval("abs(-2.5)"), Is.EqualTo(2.5f));
            Assert.That((float)Eval("sin(radians(90))"), Is.EqualTo(1.0f).Within(1e-6));
            Assert.That((float)Eval("cos(0)"), Is.EqualTo(1.0f).Within(1e-6));
            Assert.That((float)Eval("degrees(pi)"), Is.EqualTo(180.0f).Within(1e-4));
            Assert.That(Eval("square(3)"), Is.EqualTo(9.0f));
            Assert.That((float)Eval("exp(0)"), Is.EqualTo(1.0f).Within(1e-6));
            Assert.That((float)Eval("log(1)"), Is.EqualTo(0.0f).Within(1e-6));
            Assert.That((float)Eval("atan2(1, 1)"), Is.EqualTo(MathF.PI / 4).Within(1e-6));
            Assert.That(Eval("isnan(0)"), Is.EqualTo(false));
        }

        [Test]
        public void RoundingFunctions()
        {
            Assert.That(Eval("floor(3.7)"), Is.EqualTo(3));
            Assert.That(Eval("floor(3.7)"), Is.TypeOf<int>());
            Assert.That(Eval("ceil(3.2)"), Is.EqualTo(4));
            Assert.That(Eval("round(2.5)"), Is.EqualTo(2));
            Assert.That(Eval("round(3.5)"), Is.EqualTo(4));
        }

        [Test]
        public void ModAndPow()
        {
            Assert.That(Eval("mod(7, 3)"), Is.EqualTo(1));
            Assert.That(Eval("mod(7, 3)"), Is.TypeOf<int>());
            Assert.That(Eval("mod(5.5, 2)"), Is.EqualTo(1.5f));
            Assert.That(Eval("mod(5, 0)"), Is.EqualTo(0));
            Assert.That(Eval("pow(2, 10)"), Is.EqualTo(1024));
            Assert.That(Eval("pow(2, 10)"), Is.TypeOf<int>());
            Assert.That(Eval("pow(2, -1)"), Is.EqualTo(0.5f));
            Assert.That(Eval("pow(2.0, 3)"), Is.EqualTo(8.0f));
        }

        [Test]
        public void MinMaxFunctions()
        {
            Assert.That(Eval("max(3, 7, 5)"), Is.EqualTo(7));
            Assert.That(Eval("min(3, 7, 5)"), Is.EqualTo(3));
            Assert.That(Eval("max([3, 9, 5])"), Is.EqualTo(9));
            Assert.That(Eval("min(2, 1.5)"), Is.EqualTo(1.5f));
            Assert.That(Eval("max(2, 1.5)"), Is.TypeOf<float>());
        }

        [Test]
        public void ArrayFunctions()
        {
            Assert.That(Eval("vector(3, 0)"), Is.EqualTo(new object[] { 0, 0, 0 }));
            Assert.That(Eval("take([1, 2, 3, 4], 2)"), Is.EqualTo(new object[] { 1, 2 }));
            Assert.That(Eval("drop([1, 2, 3, 4], 2)"), Is.EqualTo(new object[] { 3, 4 }));
            Assert.That(Eval("take([1, 2], 5)"), Is.EqualTo(new object[] { 1, 2 }));
            Assert.That(Eval("#vector(4, 1)"), Is.EqualTo(4));
        }

        [Test]
        public void StringFunctions()
        {
            Assert.That(Eval("take(\"hello\", 3)"), Is.EqualTo("hel"));
            Assert.That(Eval("drop(\"hello\", 3)"), Is.EqualTo("lo"));
            Assert.That(Eval("find(\"hello\", \"ll\")"), Is.EqualTo(2));
            Assert.That(Eval("find(\"hello\", 'e')"), Is.EqualTo(1));
            Assert.That(Eval("find(\"hello\", \"z\")"), Is.EqualTo(-1));
        }

        [Test]
        public void NestedFunctionCalls()
        {
            Assert.That(Eval("max(abs(-3), sqrt(4))"), Is.EqualTo(3.0f));
            Assert.That(Eval("floor(sqrt(10))"), Is.EqualTo(3));
        }

        [Test]
        public void FunctionArgumentCountIsChecked()
        {
            Assert.Throws<CodeParserException>(() => Eval("sqrt(1, 2)"));
            Assert.Throws<CodeParserException>(() => Eval("atan2(1)"));
        }

        [Test]
        public void UnknownFunctionStillForwards()
        {
            AssertForwards("fileexists(\"0:/sys/config.g\")");
        }

        [Test]
        public void ExistsResolvesAgainstContext()
        {
            TestContext context = new();
            context.Identifiers["volumes[0].freeSpace"] = 1234;
            Assert.That(Eval("exists(volumes[0].freeSpace)", context), Is.EqualTo(true));
            Assert.That(Eval("exists(volumes[5].freeSpace)", context), Is.EqualTo(false));
        }

        [Test]
        public void ExistsCanBeUsedInLogic()
        {
            TestContext context = new();
            context.Identifiers["volumes"] = new object[] { 1 };
            Assert.That(Eval("exists(volumes) && exists(missing)", context), Is.EqualTo(false));
        }

        [Test]
        public void ExistsOfForwardedPathForwards()
        {
            TestContext context = new();
            context.ForwardPaths.Add("move.axes[0].machinePosition");
            AssertForwards("exists(move.axes[0].machinePosition)", context);
        }

        [Test]
        public void ExistsOfConstantOrFunctionThrows()
        {
            Assert.Throws<CodeParserException>(() => Eval("exists(true)"));
            Assert.Throws<CodeParserException>(() => Eval("exists(sin(1))"));
        }

        [Test]
        public void LengthOfObjectModelArray()
        {
            TestContext context = new();
            context.Identifiers["sensors.analog"] = new object[] { 1, 2, 3 };
            context.Identifiers["network.name"] = "duet3";
            Assert.That(Eval("#sensors.analog", context), Is.EqualTo(3));
            Assert.That(Eval("#network.name", context), Is.EqualTo(5));
        }

        [Test]
        public void LengthOfFunctionResult()
        {
            Assert.That(Eval("#take([1, 2, 3, 4], 2)"), Is.EqualTo(2));
        }
        #endregion

        #region Constants
        [Test]
        public void BooleanConstants()
        {
            Assert.That(Eval("true"), Is.EqualTo(true));
            Assert.That(Eval("false"), Is.EqualTo(false));
        }

        [Test]
        public void PiConstant()
        {
            Assert.That(Eval("pi"), Is.EqualTo((float)Math.PI));
        }

        [Test]
        public void ContextSensitiveConstants()
        {
            TestContext context = new() { Iterations = 4, LineNumber = 17 };
            Assert.That(Eval("iterations", context), Is.EqualTo(4));
            Assert.That(Eval("line", context), Is.EqualTo(17));
        }

        [Test]
        public void IterationsOutsideLoopThrows()
        {
            CodeParserException ex = Assert.Throws<CodeParserException>(() => Eval("iterations"));
            Assert.That(ex.Message, Does.Contain("not inside a loop"));
        }

        [Test]
        public void FirmwareStateConstantsAreForwarded()
        {
            AssertForwards("result");
            AssertForwards("input");
        }
        #endregion

        #region Local resolution and forwarding
        [Test]
        public void UnknownIdentifierIsForwarded()
        {
            AssertForwards("move.axes[0].machinePosition");
        }

        [Test]
        public void UnknownFunctionIsForwarded()
        {
            AssertForwards("frobnicate(1)");
        }

        [Test]
        public void PartiallyLocalExpressionIsForwarded()
        {
            AssertForwards("1 + move.axes[0].machinePosition");
            AssertForwards("move.axes[0].userPosition > 5 ? 1 : 2");
        }

        [Test]
        public void IdentifierResolverReceivesEvaluatedPath()
        {
            TestContext context = new();
            context.Identifiers["move.axes[2].userPosition"] = 12.5f;
            Assert.That(Eval("move.axes[1 + 1].userPosition", context), Is.EqualTo(12.5f));
        }

        [Test]
        public void FunctionResolverReceivesEvaluatedArguments()
        {
            TestContext context = new();
            context.Functions["max"] = args => Math.Max((int)args[0], (int)args[1]);
            Assert.That(Eval("max(3, 7)", context), Is.EqualTo(7));
        }

        [Test]
        public void ShortCircuitAndAvoidsForwarding()
        {
            Assert.That(Eval("false & foo.bar"), Is.EqualTo(false));
        }

        [Test]
        public void ShortCircuitOrAvoidsForwarding()
        {
            Assert.That(Eval("true | foo.bar"), Is.EqualTo(true));
        }

        [Test]
        public void TernaryAvoidsForwardingNonTakenBranch()
        {
            Assert.That(Eval("true ? 1 : foo.bar"), Is.EqualTo(1));
            Assert.That(Eval("false ? foo.bar : 2"), Is.EqualTo(2));
        }
        #endregion

        #region Errors
        [Test]
        public void IncompleteExpressionThrows()
        {
            Assert.Throws<CodeParserException>(() => Eval("1 +"));
        }

        [Test]
        public void UnbalancedBracketThrows()
        {
            Assert.Throws<CodeParserException>(() => Eval("(1 + 2"));
            Assert.Throws<CodeParserException>(() => Eval("[1, 2"));
        }

        [Test]
        public void TrailingCharactersThrow()
        {
            Assert.Throws<CodeParserException>(() => Eval("1 2"));
        }

        [Test]
        public void TypeMismatchThrows()
        {
            Assert.Throws<CodeParserException>(() => Eval("1 + true"));
            Assert.Throws<CodeParserException>(() => Eval("\"a\" < 1"));
        }

        [Test]
        public void ArrayIndexOutOfBoundsThrows()
        {
            CodeParserException ex = Assert.Throws<CodeParserException>(() => Eval("[1, 2][5]"));
            Assert.That(ex.Message, Does.Contain("array index out of bounds"));
        }
        #endregion

        /// <summary>
        /// Minimal in-memory context for exercising context-sensitive constants and identifier/function resolution
        /// </summary>
        private sealed class TestContext : IExpressionEvaluationContext
        {
            public int? Iterations { get; set; }
            public int LineNumber { get; set; }

            public Dictionary<string, object> Identifiers { get; } = new();
            public Dictionary<string, Func<object[], object>> Functions { get; } = new();
            public HashSet<string> ForwardPaths { get; } = new();

            public bool TryResolveIdentifier(string path, bool wantExists, bool wantArrayLength, out object value)
            {
                value = null;
                if (ForwardPaths.Contains(path))
                {
                    return false;
                }
                if (wantExists)
                {
                    value = Identifiers.ContainsKey(path);
                    return true;
                }
                if (!Identifiers.TryGetValue(path, out value))
                {
                    return false;
                }
                if (wantArrayLength)
                {
                    value = value switch
                    {
                        string s => s.Length,
                        Array a => a.Length,
                        _ => throw new InvalidOperationException("not an array or string")
                    };
                }
                return true;
            }

            public bool TryCallFunction(string name, object[] arguments, bool wantArrayLength, out object value)
            {
                if (Functions.TryGetValue(name, out Func<object[], object> fn))
                {
                    value = fn(arguments);
                    return true;
                }
                value = null;
                return false;
            }
        }
    }
}
