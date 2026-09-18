using System.Globalization;
using System.Text.Json.Nodes;

namespace DuetCanMessage.SourceGenerators.Expressions;

/// <summary>
/// Parser for the neutral expression language: a C-like infix expression grammar with the usual
/// precedence, plus a handful of intrinsic calls (<c>sizeof</c>, <c>countof</c>, <c>strnlen</c>,
/// <c>popcount</c>, <c>loadLE</c> and the integer casts).
/// </summary>
public static class ExprParser
{
    public static Expr Parse(string source)
    {
        Lexer lexer = new(source);
        Expr result = ParseTernary(lexer);
        lexer.Expect(TokenKind.End);
        return result;
    }

    private static Expr ParseTernary(Lexer lexer)
    {
        Expr condition = ParseBinary(lexer, 0);
        if (!lexer.TryEat("?"))
        {
            return condition;
        }
        Expr whenTrue = ParseTernary(lexer);
        lexer.EatOperator(":");
        Expr whenFalse = ParseTernary(lexer);
        return new TernaryExpr(condition, whenTrue, whenFalse);
    }

    /// <summary>Binary operators from lowest to highest precedence.</summary>
    private static readonly string[][] Levels =
    [
        ["||"],
        ["&&"],
        ["|"],
        ["^"],
        ["&"],
        ["==", "!="],
        ["<=", ">=", "<", ">"],
        ["<<", ">>"],
        ["+", "-"],
        ["*", "/", "%"]
    ];

    private static Expr ParseBinary(Lexer lexer, int level)
    {
        if (level >= Levels.Length)
        {
            return ParseUnary(lexer);
        }

        Expr left = ParseBinary(lexer, level + 1);
        while (true)
        {
            string? op = Levels[level].FirstOrDefault(candidate => lexer.PeekOperator(candidate));
            if (op is null)
            {
                return left;
            }
            lexer.EatOperator(op);
            Expr right = ParseBinary(lexer, level + 1);
            left = new BinaryExpr(op, left, right);
        }
    }

    private static Expr ParseUnary(Lexer lexer)
    {
        foreach (string op in (string[])["!", "~", "-"])
        {
            if (lexer.PeekOperator(op))
            {
                lexer.EatOperator(op);
                return new UnaryExpr(op, ParseUnary(lexer));
            }
        }
        return ParsePostfix(lexer);
    }

    private static Expr ParsePostfix(Lexer lexer)
    {
        Expr expr = ParsePrimary(lexer);
        while (true)
        {
            if (lexer.PeekOperator("["))
            {
                lexer.EatOperator("[");
                Expr index = ParseTernary(lexer);
                lexer.EatOperator("]");
                expr = new IndexExpr(expr, index);
            }
            else if (lexer.PeekOperator("."))
            {
                lexer.EatOperator(".");
                expr = new MemberExpr(expr, lexer.EatIdentifier());
            }
            else
            {
                return expr;
            }
        }
    }

    private static Expr ParsePrimary(Lexer lexer)
    {
        if (lexer.PeekOperator("("))
        {
            lexer.EatOperator("(");
            Expr inner = ParseTernary(lexer);
            lexer.EatOperator(")");
            return new ParenExpr(inner);
        }

        Token token = lexer.Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
                if (token.Text.Contains('.'))
                {
                    return new NumberExpr(0, token.Text, IsFloat: true);
                }
                long value = token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? long.Parse(token.Text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : long.Parse(token.Text, CultureInfo.InvariantCulture);
                return new NumberExpr(value, token.Text);

            case TokenKind.Identifier:
                if (token.Text is "true" or "false")
                {
                    return new BoolExpr(token.Text == "true");
                }
                if (!lexer.PeekOperator("("))
                {
                    return new IdentExpr(token.Text);
                }
                lexer.EatOperator("(");
                List<Expr> args = [];
                if (!lexer.PeekOperator(")"))
                {
                    do
                    {
                        args.Add(ParseTernary(lexer));
                    }
                    while (lexer.TryEat(","));
                }
                lexer.EatOperator(")");
                return new CallExpr(token.Text, args);

            default:
                throw new InvalidDataException($"unexpected token '{token.Text}' in expression");
        }
    }

    /// <summary>Parse a list of statements from the schema's JSON representation.</summary>
    public static List<Stmt> ParseBody(JsonArray? body)
    {
        List<Stmt> result = [];
        foreach (JsonNode? node in body ?? [])
        {
            result.Add(ParseStatement(node!.AsObject()));
        }
        return result;
    }

    private static Stmt ParseStatement(JsonObject o)
    {
        if (o["return"] is { } ret)
        {
            string text = ret.GetValue<string>();
            return new ReturnStmt(string.IsNullOrEmpty(text) ? null : Parse(text));
        }
        if (o["set"] is { } target)
        {
            return new AssignStmt(Parse(target.GetValue<string>()), Parse(Require(o, "to")));
        }
        if (o["orWith"] is { } orTarget)
        {
            return new OrAssignStmt(Parse(orTarget.GetValue<string>()), Parse(Require(o, "value")));
        }
        if (o["storeLE"] is { } storeTarget)
        {
            return new StoreLeStmt(Parse(storeTarget.GetValue<string>()), Parse(Require(o, "to")));
        }
        if (o["incr"] is { } incrTarget)
        {
            return new IncrementStmt(Parse(incrTarget.GetValue<string>()));
        }
        if (o["let"] is { } name)
        {
            return new LetStmt(name.GetValue<string>(), Require(o, "type"), Parse(Require(o, "value")));
        }
        if (o["if"] is { } condition)
        {
            return new IfStmt(
                Parse(condition.GetValue<string>()),
                ParseBody(o["then"]?.AsArray()),
                o["else"] is JsonArray e ? ParseBody(e) : null);
        }
        if (o["forRange"] is JsonObject range)
        {
            return new ForRangeStmt(
                Require(range, "var"),
                Parse(Require(range, "from")),
                Parse(Require(range, "to")),
                ParseBody(o["body"]?.AsArray()));
        }
        throw new InvalidDataException($"unrecognised statement: {o.ToJsonString()}");
    }

    private static string Require(JsonObject o, string key) =>
        o[key]?.GetValue<string>() ?? throw new InvalidDataException($"statement is missing '{key}': {o.ToJsonString()}");
}

internal enum TokenKind { Identifier, Number, Operator, End }

internal readonly record struct Token(TokenKind Kind, string Text);

/// <summary>
/// Tokeniser for the neutral expression language.
/// </summary>
internal sealed class Lexer
{
    private static readonly string[] Operators =
    [
        "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
        "+", "-", "*", "/", "%", "&", "|", "^", "~", "!", "<", ">", "?", ":", "(", ")", "[", "]", ",", "."
    ];

    private readonly List<Token> _tokens = [];
    private int _position;

    public Lexer(string source)
    {
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }
                _tokens.Add(new Token(TokenKind.Identifier, source[start..i]));
                continue;
            }
            if (char.IsDigit(c))
            {
                int start = i;
                bool hex = c == '0' && i + 1 < source.Length && (source[i + 1] is 'x' or 'X');
                if (hex)
                {
                    i += 2;
                    while (i < source.Length && Uri.IsHexDigit(source[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    while (i < source.Length && char.IsDigit(source[i]))
                    {
                        i++;
                    }
                    // A '.' is only part of the number when a digit follows it; otherwise it is member access
                    if (i + 1 < source.Length && source[i] == '.' && char.IsDigit(source[i + 1]))
                    {
                        i++;
                        while (i < source.Length && char.IsDigit(source[i]))
                        {
                            i++;
                        }
                    }
                }
                // 'f' and 'F' are only suffixes on a decimal literal; in hex they are digits
                char[] suffixes = hex ? ['u', 'U', 'l', 'L'] : ['u', 'U', 'l', 'L', 'f', 'F'];
                while (i < source.Length && Array.IndexOf(suffixes, source[i]) >= 0)
                {
                    i++;                                        // tolerate C-style numeric suffixes
                }
                _tokens.Add(new Token(TokenKind.Number, source[start..i].TrimEnd(suffixes)));
                continue;
            }

            string? op = Operators.FirstOrDefault(candidate => source.AsSpan(i).StartsWith(candidate));
            if (op is null)
            {
                throw new InvalidDataException($"unexpected character '{c}' in expression \"{source}\"");
            }
            _tokens.Add(new Token(TokenKind.Operator, op));
            i += op.Length;
        }
        _tokens.Add(new Token(TokenKind.End, ""));
    }

    public Token Peek() => _tokens[_position];

    public Token Next() => _tokens[_position++];

    public bool PeekOperator(string op) => Peek() is { Kind: TokenKind.Operator } t && t.Text == op;

    public void EatOperator(string op)
    {
        if (!PeekOperator(op))
        {
            throw new InvalidDataException($"expected '{op}' but found '{Peek().Text}'");
        }
        _position++;
    }

    public bool TryEat(string op)
    {
        if (!PeekOperator(op))
        {
            return false;
        }
        _position++;
        return true;
    }

    public string EatIdentifier()
    {
        Token t = Next();
        return t.Kind == TokenKind.Identifier ? t.Text : throw new InvalidDataException($"expected an identifier but found '{t.Text}'");
    }

    public void Expect(TokenKind kind)
    {
        if (Peek().Kind != kind)
        {
            throw new InvalidDataException($"unexpected trailing '{Peek().Text}' in expression");
        }
    }
}
