namespace DuetControlServer.Codes.Meta.Parsing;

/// <summary>
/// Stands for an object model object in an expression
/// </summary>
/// <remarks>
/// <para>
/// An expression may name an object rather than a value - <c>echo move</c>, or an array of them in
/// <c>move.axes</c>. RepRapFirmware carries the object itself and renders it as <c>{object}</c>,
/// which is all it does with one: objects cannot be compared, added or indexed into as values.
/// </para>
/// <para>
/// Carrying the object itself is not open to the evaluator here, because the object model is read
/// under a lock that is released before the result is used and the update task mutates the objects in
/// place. Since the only thing that can be done with one is to print those eight characters, this
/// stands in for it and holds nothing at all
/// </para>
/// </remarks>
public sealed class ObjectModelValue
{
    /// <summary>
    /// The one instance there needs to be
    /// </summary>
    public static ObjectModelValue Instance { get; } = new();

    private ObjectModelValue() { }

    /// <summary>
    /// Render as RepRapFirmware renders an object
    /// </summary>
    /// <returns>Always <c>{object}</c></returns>
    public override string ToString() => "{object}";
}
