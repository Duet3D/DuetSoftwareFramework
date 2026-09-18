using DuetAPI;
using DuetAPI.Utility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DuetControlServer.Codes.Meta.Parsing;

/// <summary>
/// Recursive-descent evaluator for meta G-code expressions. This is a C# port of the firmware ExpressionParser so
/// that DSF can evaluate expressions itself instead of forwarding them to the firmware. Operator precedence, type
/// coercion, numeric promotion rules and error messages are kept identical to the firmware to avoid behavioural drift.
/// Where an error is about the type of an operand, the message says which types were involved: the firmware cannot,
/// because it has no printable form of its type codes, and "unexpected operand type" alone leaves the reader guessing.
///
/// Values are represented as boxed CLR objects matching the firmware type codes:
/// null (none/null), <see cref="bool"/>, <see cref="char"/>, <see cref="int"/> (int32), <see cref="uint"/> (uint32),
/// <see cref="long"/> (uint64), <see cref="float"/>, <see cref="string"/>, <see cref="DateTime"/>,
/// <see cref="DriverId"/> and <c>object?[]</c> (array)
///
/// When the expression references something that cannot be resolved on the SBC, evaluation is abandoned (without
/// throwing) and <see cref="TryEvaluate"/> returns false so the caller forwards the original expression to the
/// firmware. Only genuine syntax errors throw a <see cref="CodeParserException"/>.
/// </summary>
public sealed class MetaExpressionParser
{
    // Binary operators in order of appearance; for the multi-character operators <=, >=, != and ==/&&/|| this is the first character
    private const string Operators = "?^&|!=<>+-*/";
    private static readonly byte[] Priorities = [1, 2, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6];
    private const byte UnaryPriority = 10;

    private readonly string _text;
    private readonly IExpressionEvaluationContext _context;
    private int _pos;

    // Set when an identifier or function cannot be resolved on the SBC. Once set, evaluation unwinds without doing
    // further work, so no operator runs on the resulting placeholder value and no spurious coercion error is raised
    private bool _mustForward;

    /// <summary>
    /// Creates a new parser for the given expression text
    /// </summary>
    /// <param name="text">Expression to evaluate</param>
    /// <param name="context">Context for resolving identifiers, functions and context-sensitive constants</param>
    public MetaExpressionParser(string text, IExpressionEvaluationContext? context = null)
    {
        _text = text ?? string.Empty;
        _context = context ?? ForwardingExpressionContext.Instance;
    }

    /// <summary>
    /// Try to evaluate an expression entirely on the SBC
    /// </summary>
    /// <param name="expression">Expression to evaluate</param>
    /// <param name="context">Evaluation context</param>
    /// <param name="value">Resolved value if the expression could be evaluated locally</param>
    /// <returns>True if the expression was fully evaluated on the SBC, false if it must be forwarded to the firmware</returns>
    /// <exception cref="CodeParserException">Syntax error</exception>
    public static bool TryEvaluate(string expression, IExpressionEvaluationContext? context, out object? value)
    {
        MetaExpressionParser parser = new(expression, context);
        object? result = parser.Parse();
        if (parser._mustForward)
        {
            value = null;
            return false;
        }
        parser.CheckForExtraCharacters();
        value = result;
        return true;
    }

    /// <summary>
    /// Parse and evaluate an expression. Check <see cref="MustForward"/> afterwards to find out whether the result is valid
    /// </summary>
    /// <param name="evaluate">Whether the value is actually needed (false while parsing a non-taken branch)</param>
    /// <returns>Resolved value, or null if the expression must be forwarded</returns>
    public object? Parse(bool evaluate = true)
    {
        object? result = null;
        ParseInternal(ref result, evaluate, 0);
        return result;
    }

    /// <summary>
    /// Whether the last <see cref="Parse"/> hit something that cannot be resolved on the SBC
    /// </summary>
    public bool MustForward => _mustForward;

    /// <summary>
    /// Throw if there are non-whitespace characters left after the expression
    /// </summary>
    public void CheckForExtraCharacters()
    {
        if (SkipWhiteSpace() != '\0')
        {
            ThrowParseException("Unexpected characters after expression");
        }
    }

    #region Recursive descent
    // Evaluate an expression internally, stopping before any binary operator with priority 'priority' or lower
    private void ParseInternal(ref object? val, bool evaluate, byte priority)
    {
        // Start by looking for a unary operator or opening bracket
        char c = SkipWhiteSpace();
        switch (c)
        {
            case '"':
                val = ParseQuotedString();
                break;

            case '\'':
                val = ParseCharacter();
                break;

            case '-':
                AdvancePointer();
                ParseInternal(ref val, evaluate, UnaryPriority);
                if (_mustForward)
                {
                    return;
                }
                switch (val)
                {
                    case int i:
                        val = -i;
                        break;
                    case float f:
                        val = -f;
                        break;
                    default:
                        if (evaluate)
                        {
                            ThrowParseException($"expected numeric value after '-', got {TypeName(val)}");
                        }
                        break;
                }
                break;

            case '+':
                AdvancePointer();
                ParseInternal(ref val, evaluate, UnaryPriority);
                if (_mustForward)
                {
                    return;
                }
                switch (val)
                {
                    case uint u:
                        // Convert enumeration to integer
                        val = (int)u;
                        break;
                    case int:
                    case float:
                        break;
                    case DateTime dt:
                        // Unary + converts a DateTime to a seconds count
                        val = (int)(long)(dt - DateTime.UnixEpoch).TotalSeconds;
                        break;
                    default:
                        if (evaluate)
                        {
                            ThrowParseException($"expected numeric or enumeration value after '+', got {TypeName(val)}");
                        }
                        break;
                }
                break;

            case '#':
                AdvancePointer();
                if (IsAlpha(SkipWhiteSpace()))
                {
                    // Probably applying # to an object model array, so resolve it asking for the length directly
                    ParseIdentifierExpression(ref val, evaluate, true, false);
                }
                else
                {
                    ParseInternal(ref val, evaluate, UnaryPriority);
                    if (_mustForward)
                    {
                        return;
                    }
                    ApplyLengthOperator(ref val, evaluate);
                }
                break;

            case '{':
                AdvancePointer();
                ParseExpectKet(ref val, evaluate, '}');
                break;

            case '[':
                AdvancePointer();
                ParseExpectKet(ref val, evaluate, ']');
                break;

            case '(':
                AdvancePointer();
                ParseExpectKet(ref val, evaluate, ')');
                break;

            case '!':
                AdvancePointer();
                ParseInternal(ref val, evaluate, UnaryPriority);
                if (_mustForward)
                {
                    return;
                }
                ConvertToBool(ref val, evaluate);
                val = !(bool)val!;
                break;

            default:
                if (IsDigit(c))
                {
                    val = ParseNumber();
                }
                else if (IsAlpha(c))
                {
                    ParseIdentifierExpression(ref val, evaluate, false, false);
                }
                else
                {
                    ThrowParseException("expected an expression");
                }
                break;
        }

        if (_mustForward)
        {
            return;
        }

        // Check for trailing index expressions
        while (SkipWhiteSpace() == '[')
        {
            AdvancePointer();
            object? indexExpr = null;
            ParseInternal(ref indexExpr, evaluate, 0);
            if (_mustForward)
            {
                return;
            }
            ConvertToUnsigned(ref indexExpr, evaluate);
            uint indexValue = (uint)indexExpr!;
            if (CurrentCharacter() != ']')
            {
                ThrowParseException("expected ']'");
            }
            AdvancePointer();
            switch (val)
            {
                case object?[] array:
                    if (indexValue >= array.Length)
                    {
                        ThrowParseException(ExpressionErrors.ArrayIndexOutOfRange);
                    }
                    val = array[indexValue];
                    break;

                case string s:
                    if (indexValue >= s.Length)
                    {
                        ThrowParseException(ExpressionErrors.ArrayIndexOutOfRange);
                    }
                    val = s[(int)indexValue];
                    break;

                default:
                    if (evaluate)
                    {
                        ThrowParseException("left operand of [ ] is not an array or string");
                    }
                    val = null;
                    break;
            }
        }

        // See if it is followed by a binary operator
        while (true)
        {
            char opChar = SkipWhiteSpace();
            if (opChar == '\0')
            {
                return;
            }

            int index = Operators.IndexOf(opChar);
            if (index < 0)
            {
                return;
            }
            byte opPrio = Priorities[index];
            if (opPrio <= priority)
            {
                return;
            }

            AdvancePointer();                               // skip the [first] operator character

            // Handle >= and <= and !=
            bool invert = false;
            if (opChar == '!')
            {
                if (CurrentCharacter() != '=')
                {
                    ThrowParseException("expected '='");
                }
                invert = true;
                AdvancePointer();
                opChar = '=';
            }
            else if ((opChar == '>' || opChar == '<') && CurrentCharacter() == '=')
            {
                invert = true;
                AdvancePointer();
                opChar = (char)(opChar ^ ('>' ^ '<'));      // change < to > or vice versa
            }

            // Allow == && || as alternatives to = & |
            if ((opChar == '=' || opChar == '&' || opChar == '|') && CurrentCharacter() == opChar)
            {
                AdvancePointer();
            }

            // Handle operators that do not always evaluate their second operand
            switch (opChar)
            {
                case '&':
                    ConvertToBool(ref val, evaluate);
                    {
                        bool left = (bool)val!;
                        object? val2 = null;
                        ParseInternal(ref val2, evaluate && left, opPrio);
                        if (_mustForward)
                        {
                            return;
                        }
                        if (left)
                        {
                            ConvertToBool(ref val2, evaluate);
                            val = val2;
                        }
                    }
                    break;

                case '|':
                    ConvertToBool(ref val, evaluate);
                    {
                        bool left = (bool)val!;
                        object? val2 = null;
                        ParseInternal(ref val2, evaluate && !left, opPrio);
                        if (_mustForward)
                        {
                            return;
                        }
                        if (!left)
                        {
                            ConvertToBool(ref val2, evaluate);
                            val = val2;
                        }
                    }
                    break;

                case '?':
                    ConvertToBool(ref val, evaluate);
                    {
                        bool cond = (bool)val!;
                        object? whenTrue = null, whenFalse = null;
                        ParseInternal(ref whenTrue, evaluate && cond, opPrio);
                        if (_mustForward)
                        {
                            return;
                        }
                        if (CurrentCharacter() != ':')
                        {
                            ThrowParseException("expected ':'");
                        }
                        AdvancePointer();
                        ParseInternal(ref whenFalse, evaluate && !cond, (byte)(opPrio - 1));
                        if (_mustForward)
                        {
                            return;
                        }
                        val = cond ? whenTrue : whenFalse;
                    }
                    return;

                default:
                    // Binary operators that always evaluate both operands
                    {
                        object? val2 = null;
                        ParseInternal(ref val2, evaluate, opPrio);
                        if (_mustForward)
                        {
                            return;
                        }
                        ApplyBinaryOperator(ref val, ref val2, opChar, invert, evaluate);
                    }
                    break;
            }
        }
    }

    // Apply an arithmetic, comparison or concatenation operator to val and val2, storing the result in val
    private void ApplyBinaryOperator(ref object? val, ref object? val2, char opChar, bool invert, bool evaluate)
    {
        switch (opChar)
        {
            case '+':
                if (val is DateTime dtAdd)
                {
                    if (val2 is uint au)
                    {
                        val = dtAdd.AddSeconds(au);
                    }
                    else if (val2 is int ai)
                    {
                        val = dtAdd.AddSeconds(ai);
                    }
                    else if (evaluate)
                    {
                        ThrowParseException("invalid operand types");
                    }
                }
                else
                {
                    BalanceNumericTypes(ref val, ref val2, evaluate);
                    val = (val is float) ? (object)((float)val! + (float)val2!) : (int)val! + (int)val2!;
                }
                break;

            case '-':
                if (val is DateTime dtSub)
                {
                    if (val2 is DateTime dt2)
                    {
                        val = (int)(long)(dtSub - dt2).TotalSeconds;
                    }
                    else if (val2 is uint su)
                    {
                        val = dtSub.AddSeconds(-(double)su);
                    }
                    else if (val2 is int si)
                    {
                        val = dtSub.AddSeconds(-si);
                    }
                    else if (evaluate)
                    {
                        ThrowParseException("invalid operand types");
                    }
                }
                else
                {
                    BalanceNumericTypes(ref val, ref val2, evaluate);
                    val = (val is float) ? (object)((float)val! - (float)val2!) : (int)val! - (int)val2!;
                }
                break;

            case '*':
                BalanceNumericTypes(ref val, ref val2, evaluate);
                val = (val is float) ? (object)((float)val! * (float)val2!) : (int)val! * (int)val2!;
                break;

            case '/':
                ConvertToFloat(ref val, evaluate);
                ConvertToFloat(ref val2, evaluate);
                val = (float)val! / (float)val2!;
                break;

            case '>':
            case '<':
                {
                    BalanceTypes(ref val, ref val2, evaluate);
                    bool less = opChar == '<';
                    bool result;
                    switch (val)
                    {
                        case int i:
                            result = less ? i < (int)val2! : i > (int)val2!;
                            break;
                        case float f:
                            result = less ? f < (float)val2! : f > (float)val2!;
                            break;
                        case DateTime dt:
                            result = less ? dt < (DateTime)val2! : dt > (DateTime)val2!;
                            break;
                        case bool b:
                            result = less ? (!b && (bool)val2!) : (b && !(bool)val2!);
                            break;
                        default:
                            if (evaluate)
                            {
                                ThrowParseException($"expected numeric or Boolean operands to comparison operator, got {TypeNames(val, val2)}");
                            }
                            result = false;
                            break;
                    }
                    val = invert ? !result : result;
                }
                break;

            case '=':
                {
                    bool result;
                    // Before balancing, handle comparisons with null
                    if (val is null)
                    {
                        result = val2 is null;
                    }
                    else if (val2 is null)
                    {
                        result = false;
                    }
                    else
                    {
                        BalanceTypes(ref val, ref val2, evaluate);
                        switch (val)
                        {
                            case ObjectModelValue:
                                ThrowParseException("cannot compare objects");
                                result = false;
                                break;
                            case int i:
                                result = i == (int)val2!;
                                break;
                            case uint u:
                                result = u == (uint)val2!;
                                break;
                            case float f:
                                result = f == (float)val2!;
                                break;
                            case DateTime dt:
                                result = dt == (DateTime)val2!;
                                break;
                            case bool b:
                                result = b == (bool)val2!;
                                break;
                            case string s:
                                result = s == (string)val2!;
                                break;
                            default:
                                if (evaluate)
                                {
                                    ThrowParseException($"unexpected operand type to equality operator: expected int, uint, float, datetime, bool or string, got {TypeNames(val, val2)}");
                                }
                                result = false;
                                break;
                        }
                    }
                    val = invert ? !result : result;
                }
                break;

            case '^':
                val = Concat(val, val2);
                break;
        }
    }

    // Parse the content after an opening bracket and expect the matching closing bracket
    private void ParseExpectKet(ref object? rslt, bool evaluate, char closingBracket)
    {
        SkipWhiteSpace();
        if (closingBracket == ']' && CurrentCharacter() == closingBracket)      // empty array
        {
            AdvancePointer();
            rslt = Array.Empty<object?>();
            return;
        }

        ParseInternal(ref rslt, evaluate, 0);
        if (_mustForward)
        {
            return;
        }
        if (CurrentCharacter() == closingBracket)
        {
            AdvancePointer();
            if (closingBracket == ']')                                          // single-element array
            {
                rslt = new object?[] { rslt };
            }
        }
        else if (CurrentCharacter() == ',' && (closingBracket == ']' || closingBracket == '}'))     // {e,} is a single-element array, {e} is a simple expression
        {
            ParseGeneralArray(ref rslt, evaluate, closingBracket);
        }
        else
        {
            ThrowParseException($"expected '{closingBracket}'");
        }
    }

    // Parse the rest of an array literal. The first element is parsed and a comma found but not skipped
    private void ParseGeneralArray(ref object? firstElementAndResult, bool evaluate, char closingBracket)
    {
        List<object?> elements = [firstElementAndResult];
        do
        {
            AdvancePointer();                       // skip the comma
            if (SkipWhiteSpace() == closingBracket)
            {
                break;                              // allow a trailing comma, which also distinguishes a 1-element array from a bracketed value
            }
            object? element = null;
            ParseInternal(ref element, evaluate, 0);
            if (_mustForward)
            {
                return;
            }
            elements.Add(element);
        }
        while (CurrentCharacter() == ',');

        if (CurrentCharacter() != closingBracket)
        {
            ThrowParseException($"expected '{closingBracket}'");
        }
        AdvancePointer();
        firstElementAndResult = elements.ToArray();
    }

    // Parse an identifier: a named constant, a function call, or an object model / variable path
    private void ParseIdentifierExpression(ref object? rslt, bool evaluate, bool applyLengthOperator, bool applyExists)
    {
        char c = CurrentCharacter();
        if (!IsAlpha(c))
        {
            ThrowParseException("expected an identifier");
        }

        StringBuilder path = new();
        bool isIdentifierCharacter = true;
        do
        {
            AdvancePointer();
            if (c == '[')
            {
                object? indexValue = null;
                ParseInternal(ref indexValue, evaluate, 0);
                if (_mustForward)
                {
                    return;
                }
                if (CurrentCharacter() != ']')
                {
                    ThrowParseException("expected ']'");
                }
                if (indexValue is not int)
                {
                    if (evaluate)
                    {
                        ThrowParseException("expected integer expression");
                    }
                    indexValue = 0;
                }
                AdvancePointer();                               // skip the ']'
                path.Append('[').Append(((int)indexValue!).ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                path.Append(c);
            }

            // Get the next character, skipping white space that is not inside an identifier
            bool hadIdentifierSpace = false;
            while (true)
            {
                c = CurrentCharacter();
                if (c != ' ' && c != '\t')
                {
                    break;
                }
                hadIdentifierSpace = isIdentifierCharacter;
                AdvancePointer();
            }
            isIdentifierCharacter = IsAlnum(c) || c == '_';
            if (isIdentifierCharacter && hadIdentifierSpace)
            {
                break;                                          // don't allow spaces inside identifiers
            }
        }
        while (isIdentifierCharacter || c == '.' || c == '[');

        string identifier = path.ToString();

        // Check for the names of constants
        if (TryGetNamedConstant(identifier, applyExists, out rslt))
        {
            return;
        }

        // Check whether it is a function call
        if (SkipWhiteSpace() == '(')
        {
            if (applyExists)
            {
                ThrowParseException(ExpressionErrors.InvalidExists);
            }

            AdvancePointer();

            // exists() takes an identifier that may not exist, so its argument is parsed as a path rather than evaluated
            if (identifier == "exists")
            {
                bool innerLength = SkipWhiteSpace() == '#';
                if (innerLength)
                {
                    AdvancePointer();
                }
                ParseIdentifierExpression(ref rslt, evaluate, innerLength, true);
                if (_mustForward)
                {
                    return;
                }
                if (CurrentCharacter() != ')')
                {
                    ThrowParseException("expected ')'");
                }
                AdvancePointer();
                return;
            }

            List<object?> arguments = [];
            if (SkipWhiteSpace() != ')')
            {
                while (true)
                {
                    object? argument = null;
                    ParseInternal(ref argument, evaluate, 0);
                    if (_mustForward)
                    {
                        return;
                    }
                    arguments.Add(argument);
                    if (SkipWhiteSpace() != ',')
                    {
                        break;
                    }
                    AdvancePointer();
                }
            }
            if (CurrentCharacter() != ')')
            {
                ThrowParseException("expected ')'");
            }
            AdvancePointer();
            if (evaluate)
            {
                // Built-in functions are evaluated here; environment-specific ones (e.g. fileexists) are left to the context
                if (!TryEvaluateBuiltinFunction(identifier, arguments, applyLengthOperator, out rslt) &&
                    !_context.TryCallFunction(identifier, arguments.ToArray(), applyLengthOperator, out rslt))
                {
                    _mustForward = true;
                    rslt = null;
                }
            }
            else
            {
                rslt = null;
            }
            return;
        }

        // It is an object model field or a variable
        if (evaluate)
        {
            if (!_context.TryResolveIdentifier(identifier, applyExists, applyLengthOperator, out rslt))
            {
                _mustForward = true;
                rslt = null;
            }
        }
        else
        {
            rslt = null;
        }
    }

    // Resolve a named constant. Context-sensitive constants are read from the evaluation context
    private bool TryGetNamedConstant(string identifier, bool applyExists, out object? result)
    {
        result = null;
        switch (identifier)
        {
            case "true":
                result = true;
                break;
            case "false":
                result = false;
                break;
            case "null":
                result = null;
                break;
            case "pi":
                result = (float)Math.PI;
                break;
            case "iterations":
                result = _context.Iterations ?? throw MakeParseException("'iterations' used when not inside a loop");
                break;
            case "line":
                result = _context.LineNumber;
                break;
            case "result":
                result = _context.Result;
                break;
            default:
                // 'input' is the value entered in an M291 message box, which is not implemented yet
                return false;
        }

        if (applyExists)
        {
            ThrowParseException(ExpressionErrors.InvalidExists);
        }
        return true;
    }
    #endregion

    #region Operators on values
    // Concatenate val and val2. Two arrays produce a combined array; otherwise both are appended as strings
    private static object? Concat(object? val, object? val2)
    {
        if (val is object?[] array1 && val2 is object?[] array2)
        {
            object?[] result = new object?[array1.Length + array2.Length];
            Array.Copy(array1, 0, result, 0, array1.Length);
            Array.Copy(array2, 0, result, array1.Length, array2.Length);
            return result;
        }
        return AppendAsString(val) + AppendAsString(val2);
    }

    private void ApplyLengthOperator(ref object? val, bool evaluate)
    {
        switch (val)
        {
            case string s:
                val = s.Length;
                break;
            case object?[] array:
                val = array.Length;
                break;
            default:
                if (evaluate)
                {
                    ThrowParseException($"expected object model value or string after '#, got {TypeName(val)}");
                }
                val = 0;
                break;
        }
    }
    #endregion

    #region Built-in functions
    // Evaluate one of the built-in meta G-code functions. Returns false if the name is not a built-in, so the caller
    // can fall back to environment-specific functions. Throws CodeParserException for argument errors
    private bool TryEvaluateBuiltinFunction(string name, List<object?> args, bool wantArrayLength, out object? result)
    {
        switch (name)
        {
            case "abs":
                result = Abs(Arg(args, name, 1)[0]);
                break;
            case "sin":
                result = MathF.Sin(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "cos":
                result = MathF.Cos(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "tan":
                result = MathF.Tan(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "asin":
                result = MathF.Asin(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "acos":
                result = MathF.Acos(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "atan":
                result = MathF.Atan(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "atan2":
                {
                    object?[] a = Arg(args, name, 2);
                    result = MathF.Atan2(FloatArg(a[0]), FloatArg(a[1]));
                }
                break;
            case "degrees":
                result = FloatArg(Arg(args, name, 1)[0]) * (180.0f / MathF.PI);
                break;
            case "radians":
                result = FloatArg(Arg(args, name, 1)[0]) * (MathF.PI / 180.0f);
                break;
            case "sqrt":
                result = MathF.Sqrt(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "square":
                {
                    float f = FloatArg(Arg(args, name, 1)[0]);
                    result = f * f;
                }
                break;
            case "exp":
                result = MathF.Exp(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "log":
                result = MathF.Log(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "isnan":
                result = float.IsNaN(FloatArg(Arg(args, name, 1)[0]));
                break;
            case "floor":
                result = FloatToIntOrFloat(MathF.Floor(FloatArg(Arg(args, name, 1)[0])));
                break;
            case "ceil":
                result = FloatToIntOrFloat(MathF.Ceiling(FloatArg(Arg(args, name, 1)[0])));
                break;
            case "round":
                result = FloatToIntOrFloat(MathF.Round(FloatArg(Arg(args, name, 1)[0]), MidpointRounding.ToEven));
                break;
            case "mod":
                result = Mod(Arg(args, name, 2));
                break;
            case "pow":
                result = Pow(Arg(args, name, 2));
                break;
            case "max":
                result = MinMaxFunction(args, isMax: true);
                break;
            case "min":
                result = MinMaxFunction(args, isMax: false);
                break;
            case "random":
                result = Random(Arg(args, name, 1)[0]);
                break;
            case "vector":
                {
                    object?[] a = Arg(args, name, 2);
                    if (a[0] is not int count || count < 0)
                    {
                        ThrowParseException(ExpressionErrors.ExpectedNonNegativeInt);
                    }
                    object?[] vector = new object?[(int)a[0]!];
                    Array.Fill(vector, a[1]);
                    result = vector;
                }
                break;
            case "take":
                result = Take(Arg(args, name, 2), drop: false);
                break;
            case "drop":
                result = Take(Arg(args, name, 2), drop: true);
                break;
            case "find":
                result = Find(Arg(args, name, 2));
                break;
            case "datetime":
                result = DateTimeFunction(Arg(args, name, 1)[0]);
                break;
            default:
                result = null;
                return false;
        }

        if (wantArrayLength)
        {
            ApplyLengthOperator(ref result, true);
        }
        return true;
    }

    // Verify the argument count and return the argument list
    private object?[] Arg(List<object?> args, string name, int count)
    {
        if (args.Count != count)
        {
            ThrowParseException($"{name}() expects {count} argument{(count == 1 ? "" : "s")}");
        }
        return [.. args];
    }

    private float FloatArg(object? value)
    {
        ConvertToFloat(ref value, true);
        return (float)value!;
    }

    private static object FloatToIntOrFloat(float f) => (f >= int.MinValue && f <= int.MaxValue) ? (object)(int)f : f;

    private object Abs(object? value)
    {
        switch (value)
        {
            case int i:
                return Math.Abs(i);
            case float f:
                return MathF.Abs(f);
            default:
                ThrowParseException("expected numeric operand");
                return 0;
        }
    }

    private object Mod(object?[] args)
    {
        object? a = args[0], b = args[1];
        BalanceNumericTypes(ref a, ref b, true);
        if (a is float fa)
        {
            return fa % (float)b!;
        }
        int ib = (int)b!;
        return (ib == 0) ? 0 : (int)a! % ib;
    }

    private object Pow(object?[] args)
    {
        object? a = args[0], b = args[1];
        BalanceNumericTypes(ref a, ref b, true);
        bool integerResult = b is int bi && bi >= 0;
        ConvertToFloat(ref a, true);
        ConvertToFloat(ref b, true);
        float res = MathF.Pow((float)a!, (float)b!);
        return (integerResult && MathF.Abs(res) <= int.MaxValue) ? (object)(int)MathF.Round(res, MidpointRounding.ToEven) : res;
    }

    private object? MinMaxFunction(List<object?> args, bool isMax)
    {
        object?[] elements;
        if (args.Count == 1)
        {
            // A single operand must be an array, which is then reduced to its minimum/maximum
            if (args[0] is not object?[] array)
            {
                ThrowParseException("operand is not an array");
                return null;
            }
            if (array.Length == 0)
            {
                ThrowParseException("array has no elements");
            }
            elements = array;
        }
        else
        {
            if (args.Count == 0)
            {
                ThrowParseException("expected an expression");
            }
            elements = [.. args];
        }

        object? accumulator = elements[0];
        for (int i = 1; i < elements.Length; i++)
        {
            object? next = elements[i];
            BalanceNumericTypes(ref accumulator, ref next, true);
            if (accumulator is float fa)
            {
                float fn = (float)next!;
                accumulator = isMax ? MathF.Max(fa, fn) : MathF.Min(fa, fn);
            }
            else
            {
                int ia = (int)accumulator!, iNext = (int)next!;
                accumulator = isMax ? Math.Max(ia, iNext) : Math.Min(ia, iNext);
            }
        }
        return accumulator;
    }

    private object Random(object? value)
    {
        long limit = value switch
        {
            uint u => u,
            int i when i > 0 => i,
            _ => throw MakeParseException("expected positive integer")
        };
        return (int)System.Random.Shared.NextInt64(limit);
    }

    private object Take(object?[] args, bool drop)
    {
        object? source = args[0];
        ConvertToUnsigned(ref args[1], true);
        uint count = (uint)args[1]!;
        switch (source)
        {
            case object?[] array:
                {
                    int n = (int)Math.Min(count, (uint)array.Length);
                    object?[] result = new object?[drop ? array.Length - n : n];
                    Array.Copy(array, drop ? n : 0, result, 0, result.Length);
                    return result;
                }
            case string s:
                {
                    int n = (int)Math.Min(count, (uint)s.Length);
                    return drop ? s[n..] : s[..n];
                }
            default:
                ThrowParseException("first operand of function is not an array or string");
                return string.Empty;
        }
    }

    private object Find(object?[] args)
    {
        if (args[0] is not string haystack)
        {
            ThrowParseException("first operand of function is not a string");
            return -1;
        }
        return args[1] switch
        {
            char c => haystack.IndexOf(c),
            string needle => haystack.IndexOf(needle, StringComparison.Ordinal),
            _ => throw MakeParseException("incompatible operand types")
        };
    }

    private object DateTimeFunction(object? value)
    {
        switch (value)
        {
            case int i:
                return DateTime.UnixEpoch.AddSeconds(Math.Max(i, 0));
            case uint u:
                return DateTime.UnixEpoch.AddSeconds(u);
            case long l:
                return DateTime.UnixEpoch.AddSeconds(l);
            case ulong ul:
                return DateTime.UnixEpoch.AddSeconds(ul);
            case DateTime dt:
                return dt;
            case string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed):
                return parsed;
            default:
                ThrowParseException("can't convert value to DateTime");
                return DateTime.UnixEpoch;
        }
    }
    #endregion

    #region Type coercion
    // First convert any Uint64 or Uint32 operands to float, then bring both operands to a common numeric type
    private void BalanceNumericTypes(ref object? val1, ref object? val2, bool evaluate)
    {
        if (val1 is uint or long or ulong)
        {
            ConvertToFloat(ref val1, evaluate);
        }
        if (val2 is uint or long or ulong)
        {
            ConvertToFloat(ref val2, evaluate);
        }

        if (val1 is float)
        {
            ConvertToFloat(ref val2, evaluate);
        }
        else if (val2 is float)
        {
            ConvertToFloat(ref val1, evaluate);
        }
        else if (val1 is not int || val2 is not int)
        {
            if (evaluate)
            {
                ThrowParseException($"expected numeric operands, got {TypeNames(val1, val2)}");
            }
            val1 = 0;
            val2 = 0;
        }
    }

    // Balance types for a comparison operator
    private void BalanceTypes(ref object? val1, ref object? val2, bool evaluate)
    {
        if (val1 is uint or long or ulong)
        {
            ConvertToFloat(ref val1, evaluate);
        }
        if (val2 is uint or long or ulong)
        {
            ConvertToFloat(ref val2, evaluate);
        }

        if (val1?.GetType() == val2?.GetType() || (val1 is string && val2 is string))    // common case
        {
            // nothing to do
        }
        else if (val1 is float)
        {
            ConvertToFloat(ref val2, evaluate);
        }
        else if (val2 is float)
        {
            ConvertToFloat(ref val1, evaluate);
        }
        else if (val2 is string && TypeHasNoLiterals(val1))
        {
            ConvertToString(ref val1, evaluate);
        }
        else if (val1 is string && TypeHasNoLiterals(val2))
        {
            ConvertToString(ref val2, evaluate);
        }
        else
        {
            if (evaluate)
            {
                ThrowParseException($"cannot convert operands to same type: {TypeNames(val1, val2)}");
            }
            val1 = 0;
            val2 = 0;
        }
    }

    // Types that have no literal representation and must be converted to string when compared with a string
    private static bool TypeHasNoLiterals(object? val) => val is char or DateTime or DriverId;

    // Name a value's type the way the meta G-code documentation names it, for an error message. RepRapFirmware
    // has no printable form of its type codes, so its wording stops at "unexpected operand type" and leaves the
    // reader to work out which operand and what it held
    private static string TypeName(object? val) => val switch
    {
        null => "null",
        bool => "bool",
        char => "char",
        string => "string",
        int or long => "int",
        uint or ulong => "uint",
        float or double => "float",
        DateTime => "datetime",
        DriverId => "driver id",
        ObjectModelValue => "object",
        object?[] => "array",
        _ => val.GetType().Name
    };

    // Name the types of both operands, saying it once when they are the same
    private static string TypeNames(object? val1, object? val2)
    {
        string name1 = TypeName(val1), name2 = TypeName(val2);
        return (name1 == name2) ? name1 : $"{name1} and {name2}";
    }

    private void ConvertToFloat(ref object? val, bool evaluate)
    {
        switch (val)
        {
            case float:
                return;
            case uint u:
                val = (float)u;
                break;
            case long l:
                val = (float)l;
                break;
            case ulong ul:
                val = (float)ul;
                break;
            case int i:
                val = (float)i;
                break;
            default:
                if (evaluate)
                {
                    ThrowParseException("expected numeric operand");
                }
                val = 0.0f;
                break;
        }
    }

    private void ConvertToUnsigned(ref object? val, bool evaluate)
    {
        switch (val)
        {
            case uint:
                break;
            case int i when i >= 0:
                val = (uint)i;
                break;
            default:
                if (evaluate)
                {
                    ThrowParseException(ExpressionErrors.ExpectedNonNegativeInt);
                }
                val = 0u;
                break;
        }
    }

    private void ConvertToBool(ref object? val, bool evaluate)
    {
        if (val is not bool)
        {
            if (evaluate)
            {
                ThrowParseException($"expected Boolean operand, got {TypeName(val)}");
            }
            val = false;
        }
    }

    private void ConvertToString(ref object? val, bool evaluate)
    {
        if (val is not string)
        {
            val = evaluate ? AppendAsString(val) : string.Empty;
        }
    }

    // Produce the string representation of a value as used by string concatenation and echo output
    private static string AppendAsString(object? val)
    {
        switch (val)
        {
            case null:
                return "null";
            case bool b:
                return b ? "true" : "false";
            case char c:
                return c.ToString();
            case string s:
                return s;
            case float f:
                return f.ToString(CultureInfo.InvariantCulture);
            case int or uint or long or ulong:
                return Convert.ToString(val, CultureInfo.InvariantCulture)!;
            case DateTime dt:
                return dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            case ObjectModelValue om:
                return om.ToString();
            case object?[] array:
                {
                    StringBuilder sb = new("[");
                    for (int i = 0; i < array.Length; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }
                        sb.Append(AppendAsString(array[i]));
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
            default:
                return val.ToString() ?? string.Empty;
        }
    }
    #endregion

    #region Literals
    // Parse a number. The initial character is a decimal digit
    private object ParseNumber()
    {
        int start = _pos;
        bool isFloat = false;
        if (CurrentCharacter() == '0' && (PeekCharacter() == 'x' || PeekCharacter() == 'X'))
        {
            AdvancePointer();
            AdvancePointer();
            int hexStart = _pos;
            while (IsHexDigit(CurrentCharacter()))
            {
                AdvancePointer();
            }
            if (_pos == hexStart)
            {
                ThrowParseException("expected hexadecimal digits");
            }
            ulong hexValue = Convert.ToUInt64(_text[hexStart.._pos], 16);
            return FitInteger(hexValue);
        }

        while (IsDigit(CurrentCharacter()))
        {
            AdvancePointer();
        }
        if (CurrentCharacter() == '.')
        {
            isFloat = true;
            AdvancePointer();
            while (IsDigit(CurrentCharacter()))
            {
                AdvancePointer();
            }
        }
        if (CurrentCharacter() == 'e' || CurrentCharacter() == 'E')
        {
            isFloat = true;
            AdvancePointer();
            if (CurrentCharacter() == '+' || CurrentCharacter() == '-')
            {
                AdvancePointer();
            }
            while (IsDigit(CurrentCharacter()))
            {
                AdvancePointer();
            }
        }

        string token = _text[start.._pos];
        if (isFloat)
        {
            return float.Parse(token, CultureInfo.InvariantCulture);
        }
        if (ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong integerValue))
        {
            return FitInteger(integerValue);
        }
        return float.Parse(token, CultureInfo.InvariantCulture);
    }

    // Select the narrowest type that holds the value: int32, else uint32, else float
    private static object FitInteger(ulong value)
    {
        if (value <= int.MaxValue)
        {
            return (int)value;
        }
        if (value <= uint.MaxValue)
        {
            return (uint)value;
        }
        return (float)value;
    }

    // Parse a quoted string, given that the current character is a double quote
    private string ParseQuotedString()
    {
        StringBuilder str = new();
        AdvancePointer();
        while (true)
        {
            char c = CurrentCharacter();
            AdvancePointer();
            if (c == '\0')
            {
                ThrowParseException("unterminated string");
            }
            if (c < ' ')
            {
                ThrowParseException("control character in string");
            }
            if (c == '"')
            {
                if (CurrentCharacter() != c)
                {
                    return str.ToString();
                }
                AdvancePointer();
            }
            else if (c == '\'')
            {
                if (IsAlpha(CurrentCharacter()))
                {
                    // Single quote before an alphabetic character forces that character to lower case
                    c = char.ToLowerInvariant(CurrentCharacter());
                    AdvancePointer();
                }
                else if (CurrentCharacter() == c)
                {
                    // Two quotes represent one
                    AdvancePointer();
                }
            }
            str.Append(c);
        }
    }

    // Parse a character literal, given that the current character is a single quote
    private char ParseCharacter()
    {
        AdvancePointer();
        char result = CurrentCharacter();
        AdvancePointer();
        if (CurrentCharacter() != '\'')
        {
            ThrowParseException("expected \"'\"");
        }
        AdvancePointer();
        return result;
    }
    #endregion

    #region Character helpers
    private char CurrentCharacter() => (_pos < _text.Length) ? _text[_pos] : '\0';

    private char PeekCharacter() => (_pos + 1 < _text.Length) ? _text[_pos + 1] : '\0';

    private void AdvancePointer()
    {
        if (_pos < _text.Length)
        {
            _pos++;
        }
    }

    private char SkipWhiteSpace()
    {
        while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t'))
        {
            _pos++;
        }
        return CurrentCharacter();
    }

    private static bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsAlnum(char c) => IsAlpha(c) || IsDigit(c);

    private static bool IsHexDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    #endregion

    #region Errors
    private void ThrowParseException(string message) => throw MakeParseException(message);

    private CodeParserException MakeParseException(string message) => new($"{message} (column {_pos + 1})");
    #endregion
}
