using CanMessageGenerator.Expressions;
using CanMessageGenerator.Model;

namespace CanMessageGenerator.Emit;

/// <summary>
/// Renders the neutral expression language as C#.
/// </summary>
/// <remarks>
/// Unlike the C++ emitter, this one resolves <c>sizeof</c> and <c>countof</c> to integer literals. The
/// sizes come from the same layout model that the generated C++ static_asserts and the generated
/// conformance tests check, so the literals cannot drift away from the C++ <c>sizeof</c> expressions.
/// </remarks>
public sealed class CSharpExprEmitter(EmitContext context)
{
    public string Render(Expr e) => e switch
    {
        NumberExpr n => RenderNumber(n),
        BoolExpr b => b.Value ? "true" : "false",
        IdentExpr i => RenderIdent(i.Name),
        ParenExpr p => $"({Render(p.Inner)})",
        UnaryExpr u => $"{u.Op}{Render(u.Operand)}",
        BinaryExpr b => b.Op is "<<" or ">>"
            // C# shift counts must be int, whereas the schema (like C++) allows any integer type
            ? $"{Render(b.Left)} {b.Op} {(b.Right is NumberExpr ? Render(b.Right) : $"(int)({Render(b.Right)})")}"
            : $"{Render(b.Left)} {b.Op} {Render(b.Right)}",
        TernaryExpr t => $"{Render(t.Condition)} ? {Render(t.WhenTrue)} : {Render(t.WhenFalse)}",
        IndexExpr x => $"{Render(x.Target)}[{RenderIndex(x.Index)}]",
        MemberExpr m => $"{Render(m.Target)}.{Naming.Pascal(m.Name)}",
        CallExpr c => RenderCall(c),
        _ => throw new InvalidOperationException($"cannot render {e.GetType().Name} as C#")
    };

    private static string RenderNumber(NumberExpr n) => n.Raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? n.Raw : n.Value.ToString();

    /// <summary>
    /// Inline array indexers take an <c>int</c>. The schema's index expressions are typically unsigned
    /// (<c>size_t</c> in C++), so everything except a literal gets an explicit cast.
    /// </summary>
    private string RenderIndex(Expr index) => index is NumberExpr || (index is IdentExpr i && context.IsIntLocal(i.Name))
        ? Render(index)
        : $"(int)({Render(index)})";

    private string RenderIdent(string name) => context.Classify(name) switch
    {
        SymbolKind.Local => name,
        SymbolKind.Field or SymbolKind.Array or SymbolKind.BitField => Naming.Pascal(name),
        SymbolKind.StructConstant => CSharpEmitter.ConstantName(context.Owner!, context.Owner!.Constants.First(c => c.Name == name)),
        SymbolKind.SchemaConstant => $"CanLimits.{name}",
        SymbolKind.Method => Naming.Pascal(name),
        SymbolKind.TypeName => Types.CSharp(context.Schema, context.ResolveType(name)),
        SymbolKind.TemplateParam => Types.CSharp(context.Schema, context.ResolveType(name)),
        _ => name
    };

    private string RenderCall(CallExpr c)
    {
        switch (c.Name)
        {
            case "sizeof":
                return SizeOf(c.Args[0]).ToString();

            case "countof":
                return CountOf(c.Args[0]).ToString();

            case "strnlen":
            {
                string target = Render(c.Args[0]);
                string limit = c.Args.Count > 1 ? $"(int)({Render(c.Args[1])})" : CountOf(c.Args[0]).ToString();
                return $"CanText.Strnlen({target}, {limit})";
            }

            case "popcount":
                return $"BitOperations.PopCount({Render(c.Args[0])})";

            case "loadLE":
                // C# lays these structs out with Pack = 1 and reads unaligned fields directly
                return Render(c.Args[0]);

            case "elem":
                return $"{Render(c.Args[0])}[0]";

            case "min":
                return $"Math.Min({Render(c.Args[0])}, {Render(c.Args[1])})";

            case "max":
                return $"Math.Max({Render(c.Args[0])}, {Render(c.Args[1])})";

            case "clamp":
                return $"Math.Clamp({Render(c.Args[0])}, {Render(c.Args[1])}, {Render(c.Args[2])})";

            default:
                if (Types.IsPrimitive(c.Name) && c.Args.Count == 1)
                {
                    return Cast(c.Name, c.Args[0]);
                }
                return $"{Naming.Pascal(c.Name)}({string.Join(", ", c.Args.Select(Render))})";
        }
    }

    /// <summary>Render a cast, turning a boolean operand into 0 or 1 because C# has no bool-to-int conversion.</summary>
    public string Cast(string type, Expr operand)
    {
        string csharpType = Types.CSharp(context.Schema, type);
        return IsBool(operand)
            ? $"({Render(operand)} ? ({csharpType})1 : ({csharpType})0)"
            : $"({csharpType})({Render(operand)})";
    }

    /// <summary>A conservative check for whether an expression has boolean type.</summary>
    public bool IsBool(Expr e) => e switch
    {
        BoolExpr => true,
        ParenExpr p => IsBool(p.Inner),
        UnaryExpr { Op: "!" } => true,
        BinaryExpr b => b.Op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||",
        TernaryExpr t => IsBool(t.WhenTrue) && IsBool(t.WhenFalse),
        IdentExpr i => context.BitField(i.Name)?.Bool == true
                       || (context.Classify(i.Name) == SymbolKind.Field && context.Member(i.Name)?.Type == "bool")
                       || context.IsBoolLocal(i.Name),
        _ => false
    };

    private int SizeOf(Expr arg) => arg switch
    {
        IdentExpr { Name: "self" } => Math.Max(context.Owner?.Size ?? 0, 1),
        CallExpr { Name: "elem" } c => ElementSize(c.Args[0]),
        IdentExpr i => context.SizeOfSymbol(i.Name),
        _ => throw new InvalidDataException($"cannot evaluate sizeof({arg}) at generation time")
    };

    private int ElementSize(Expr arg)
    {
        MemberDef member = ArrayMember(arg);
        return Types.SizeOf(context.Schema, context.ResolveType(member.Type));
    }

    private int CountOf(Expr arg) => ArrayMember(arg).ResolvedLength;

    private MemberDef ArrayMember(Expr arg)
    {
        string name = arg switch
        {
            IdentExpr i => i.Name,
            _ => throw new InvalidDataException($"expected an array member but found {arg}")
        };
        MemberDef member = context.Member(name) ?? throw new InvalidDataException($"unknown array member '{name}'");
        return member.Kind == MemberKind.Array ? member : throw new InvalidDataException($"'{name}' is not an array");
    }
}

/// <summary>
/// Renders the neutral statement language as C#.
/// </summary>
public sealed class CSharpStatementEmitter(EmitContext context, string returnType)
{
    private readonly CSharpExprEmitter _expr = new(context);

    public void Write(CodeWriter writer, List<Stmt> body)
    {
        foreach (Stmt s in body)
        {
            Write(writer, s);
        }
    }

    private void Write(CodeWriter writer, Stmt s)
    {
        switch (s)
        {
            case IfStmt i:
                using (writer.Block($"if ({_expr.Render(i.Condition)})", "}"))
                {
                    writer.Outdent();
                    writer.Line("{");
                    writer.Indent();
                    Write(writer, i.Then);
                }
                if (i.Else is not null)
                {
                    using (writer.Block("else", "}"))
                    {
                        writer.Outdent();
                        writer.Line("{");
                        writer.Indent();
                        Write(writer, i.Else);
                    }
                }
                break;

            case ForRangeStmt f:
                using (writer.Block($"for (int {f.Var} = {_expr.Render(f.From)}; {f.Var} < {_expr.Render(f.To)}; {f.Var}++)", "}"))
                {
                    writer.Outdent();
                    writer.Line("{");
                    writer.Indent();
                    context.Locals.Add(f.Var);
                    context.MarkIntLocal(f.Var);
                    Write(writer, f.Body);
                }
                break;

            default:
                writer.Line(Render(s));
                break;
        }
    }

    public string Render(Stmt s)
    {
        switch (s)
        {
            case ReturnStmt { Value: null }:
                return "return;";

            case ReturnStmt r:
                return $"return {Convert(returnType, r.Value!)};";

            case AssignStmt a:
                return $"{_expr.Render(a.Target)} = {Convert(TargetType(a.Target), a.Value)};";

            case StoreLeStmt st:
                return $"{_expr.Render(st.Target)} = {Convert(TargetType(st.Target), st.Value)};";

            case OrAssignStmt o:
            {
                string type = TargetType(o.Target);
                return $"{_expr.Render(o.Target)} |= {Convert(type, o.Value)};";
            }

            case IncrementStmt i:
                return $"{_expr.Render(i.Target)}++;";

            case LetStmt l:
                context.Locals.Add(l.Name);
                if (_expr.IsBool(l.Value))
                {
                    context.MarkBoolLocal(l.Name);
                }
                return $"{Types.CSharp(context.Schema, l.Type)} {l.Name} = {Convert(l.Type, l.Value)};";

            default:
                throw new InvalidOperationException($"{s.GetType().Name} cannot be rendered on a single line");
        }
    }

    /// <summary>Render an expression, inserting the cast that C#'s stricter numeric rules require.</summary>
    private string Convert(string type, Expr value)
    {
        if (type is "void")
        {
            return _expr.Render(value);
        }
        if (type is "bool" || !Types.IsPrimitive(type))
        {
            return _expr.Render(value);                         // non-numeric targets take the value as-is
        }
        if (value is BoolExpr or NumberExpr && !_expr.IsBool(value))
        {
            return _expr.Render(value);                         // a literal already has the right type
        }
        return _expr.Cast(type, value);
    }

    /// <summary>The schema type of an assignment target, used to pick the cast.</summary>
    private string TargetType(Expr target)
    {
        switch (target)
        {
            case IdentExpr i:
            {
                BitFieldDef? bits = context.BitField(i.Name);
                if (bits is not null)
                {
                    return bits.Bool ? "bool" : CSharpEmitter.BitFieldSchemaType(bits);
                }
                MemberDef? member = context.Member(i.Name);
                return member is not null ? context.ResolveType(member.Type) : "void";
            }

            case MemberExpr m:
            {
                // Find the field in whichever struct declares it; the schema has no duplicate field names
                // within a struct, and cross-struct name reuse is resolved by matching the member name.
                MemberDef? member = context.Schema.Structs.SelectMany(x => x.FlatMembers).FirstOrDefault(x => x.Name == m.Name);
                BitFieldDef? bits = context.Schema.Structs.SelectMany(x => x.AllBitFields).FirstOrDefault(x => x.Name == m.Name);
                if (member is not null)
                {
                    return member.Type;
                }
                return bits is not null ? (bits.Bool ? "bool" : CSharpEmitter.BitFieldSchemaType(bits)) : "void";
            }

            case IndexExpr x:
            {
                MemberDef? member = target is IndexExpr { Target: IdentExpr id } ? context.Member(id.Name) : null;
                return member is not null ? context.ResolveType(member.Type) : "void";
            }

            default:
                return "void";
        }
    }
}
