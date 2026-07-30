namespace CanMessageGenerator.Expressions;

/// <summary>
/// An expression of the neutral method-body language. Expressions are parsed once from the schema and
/// rendered separately into C++ and C#, so a method body only ever exists in one place.
/// </summary>
public abstract record Expr;

/// <summary>An integer literal. <see cref="Raw"/> preserves the original spelling (e.g. hexadecimal).</summary>
public sealed record NumberExpr(long Value, string Raw) : Expr;

/// <summary>A boolean literal.</summary>
public sealed record BoolExpr(bool Value) : Expr;

/// <summary>A bare name: a parameter, a local, a struct member, a constant or a type.</summary>
public sealed record IdentExpr(string Name) : Expr;

/// <summary>A call: an intrinsic such as <c>sizeof</c> or <c>strnlen</c>, or a method of the enclosing struct.</summary>
public sealed record CallExpr(string Name, List<Expr> Args) : Expr;

/// <summary>Array indexing.</summary>
public sealed record IndexExpr(Expr Target, Expr Index) : Expr;

/// <summary>Member access.</summary>
public sealed record MemberExpr(Expr Target, string Name) : Expr;

/// <summary>A prefix operator: <c>-</c>, <c>~</c> or <c>!</c>.</summary>
public sealed record UnaryExpr(string Op, Expr Operand) : Expr;

/// <summary>A binary operator.</summary>
public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;

/// <summary>A conditional expression.</summary>
public sealed record TernaryExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr;

/// <summary>A parenthesised expression; kept so that the rendered output preserves the author's grouping.</summary>
public sealed record ParenExpr(Expr Inner) : Expr;

/// <summary>
/// A statement of the neutral method-body language.
/// </summary>
public abstract record Stmt;

/// <summary><c>return &lt;value&gt;;</c>, or a bare <c>return;</c> when <see cref="Value"/> is null.</summary>
public sealed record ReturnStmt(Expr? Value) : Stmt;

/// <summary><c>&lt;target&gt; = &lt;value&gt;;</c></summary>
public sealed record AssignStmt(Expr Target, Expr Value) : Stmt;

/// <summary><c>&lt;target&gt; |= &lt;value&gt;;</c></summary>
public sealed record OrAssignStmt(Expr Target, Expr Value) : Stmt;

/// <summary>A little-endian store into a possibly unaligned field.</summary>
public sealed record StoreLeStmt(Expr Target, Expr Value) : Stmt;

/// <summary><c>++&lt;target&gt;;</c></summary>
public sealed record IncrementStmt(Expr Target) : Stmt;

/// <summary>A local variable declaration.</summary>
public sealed record LetStmt(string Name, string Type, Expr Value) : Stmt;

/// <summary>A conditional statement.</summary>
public sealed record IfStmt(Expr Condition, List<Stmt> Then, List<Stmt>? Else) : Stmt;

/// <summary>A counted loop over the half-open range <c>[From, To)</c>.</summary>
public sealed record ForRangeStmt(string Var, Expr From, Expr To, List<Stmt> Body) : Stmt;
