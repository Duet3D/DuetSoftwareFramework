using CanMessageGenerator.Expressions;
using CanMessageGenerator.Model;

namespace CanMessageGenerator.Emit;

/// <summary>
/// Renders the neutral expression language as C++.
/// </summary>
public sealed class CppExprEmitter(EmitContext context)
{
    public string Render(Expr e) => e switch
    {
        NumberExpr n => n.Raw,
        BoolExpr b => b.Value ? "true" : "false",
        IdentExpr i => RenderIdent(i.Name),
        ParenExpr p => $"({Render(p.Inner)})",
        UnaryExpr u => $"{u.Op}{Render(u.Operand)}",
        BinaryExpr b => $"{Render(b.Left)} {b.Op} {Render(b.Right)}",
        TernaryExpr t => $"{Render(t.Condition)} ? {Render(t.WhenTrue)} : {Render(t.WhenFalse)}",
        IndexExpr x => $"{Render(x.Target)}[{Render(x.Index)}]",
        MemberExpr m => $"{Render(m.Target)}.{m.Name}",
        CallExpr c => RenderCall(c),
        _ => throw new InvalidOperationException($"cannot render {e.GetType().Name} as C++")
    };

    private string RenderIdent(string name) => context.Classify(name) switch
    {
        SymbolKind.TypeName => Types.Cpp(context.Schema, name),
        SymbolKind.TemplateParam => name,
        _ => name
    };

    private string RenderCall(CallExpr c)
    {
        switch (c.Name)
        {
            case "sizeof":
                return $"sizeof({RenderSizeofArg(c.Args[0])})";

            case "countof":
                return $"ARRAY_SIZE({Render(c.Args[0])})";

            case "strnlen":
            {
                string target = Render(c.Args[0]);
                string limit = c.Args.Count > 1 ? Render(c.Args[1]) : $"ARRAY_SIZE({target})";
                return $"Strnlen({target}, {limit})";
            }

            case "popcount":
                return $"Bitmap<uint32_t>({Render(c.Args[0])}).CountSetBits()";

            case "loadLE":
                return $"{LoadStoreHelper("LoadLE", c.Args[0])}(&{Render(c.Args[0])})";

            case "elem":
                return $"{Render(c.Args[0])}[0]";

            case "min":
                return $"min<size_t>({Render(c.Args[0])}, {Render(c.Args[1])})";

            case "max":
                return $"max<size_t>({Render(c.Args[0])}, {Render(c.Args[1])})";

            case "clamp":
                return $"constrain<size_t>({Render(c.Args[0])}, {Render(c.Args[1])}, {Render(c.Args[2])})";

            default:
                if (Types.IsPrimitive(c.Name) && c.Args.Count == 1)
                {
                    return $"({Types.Cpp(context.Schema, c.Name)})({Render(c.Args[0])})";
                }
                return $"{c.Name}({string.Join(", ", c.Args.Select(Render))})";
        }
    }

    private string RenderSizeofArg(Expr arg) => arg switch
    {
        IdentExpr { Name: "self" } => "*this",
        IdentExpr i when context.Classify(i.Name) == SymbolKind.TypeName => Types.Cpp(context.Schema, i.Name),
        _ => Render(arg)
    };

    /// <summary>Pick the LoadLE/StoreLE helper that matches the type of the field being accessed.</summary>
    internal string LoadStoreHelper(string prefix, Expr target)
    {
        string field = target switch
        {
            IdentExpr i => i.Name,
            MemberExpr m => m.Name,
            _ => throw new InvalidDataException("loadLE/storeLE needs a field reference")
        };
        MemberDef? member = context.Member(field)
            ?? context.Schema.Structs.SelectMany(s => s.FlatMembers).FirstOrDefault(m => m.Name == field && m.Unaligned);
        string type = member?.Type ?? "u32";
        return prefix + type switch
        {
            "f32" => "F32",
            "i32" => "I32",
            "u32" => "U32",
            "i16" => "I16",
            "u16" => "U16",
            _ => throw new InvalidDataException($"no LoadLE/StoreLE helper for type '{type}'")
        };
    }
}

/// <summary>
/// Renders the neutral statement language as C++.
/// </summary>
public sealed class CppStatementEmitter(EmitContext context)
{
    private readonly CppExprEmitter _expr = new(context);

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
            {
                string var = f.Var;
                using (writer.Block($"for (size_t {var} = {_expr.Render(f.From)}; {var} < {_expr.Render(f.To)}; ++{var})", "}"))
                {
                    writer.Outdent();
                    writer.Line("{");
                    writer.Indent();
                    using (context.Scope(var))
                    {
                        Write(writer, f.Body);
                    }
                }
                break;
            }

            default:
                writer.Line(Render(s));
                break;
        }
    }

    /// <summary>Render a simple (non-compound) statement on one line.</summary>
    public string Render(Stmt s) => s switch
    {
        ReturnStmt r => r.Value is null ? "return;" : $"return {_expr.Render(r.Value)};",
        AssignStmt a => $"{_expr.Render(a.Target)} = {_expr.Render(a.Value)};",
        OrAssignStmt o => $"{_expr.Render(o.Target)} |= {_expr.Render(o.Value)};",
        IncrementStmt i => $"++{_expr.Render(i.Target)};",
        LetStmt l => $"const {Types.Cpp(context.Schema, l.Type)} {l.Name} = {_expr.Render(l.Value)};",
        StoreLeStmt st => $"{_expr.LoadStoreHelper("StoreLE", st.Target)}(&{_expr.Render(st.Target)}, {_expr.Render(st.Value)});",
        _ => throw new InvalidOperationException($"{s.GetType().Name} cannot be rendered on a single line")
    };
}
