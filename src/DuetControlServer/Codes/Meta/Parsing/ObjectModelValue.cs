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

    /// <summary>
    /// Check whether a value is an object, or an array holding one at any depth
    /// </summary>
    /// <param name="value">Value to check</param>
    /// <returns>True if an object occurs anywhere in it</returns>
    /// <remarks>
    /// <para>
    /// Assigning one to a variable is refused, and so is assigning an array of them: what would be
    /// stored holds nothing, so a macro reading it back gets eight characters where it expected the
    /// machine.
    /// </para>
    /// <para>
    /// RepRapFirmware refuses the first and stores the second as pointers into its object model, which
    /// makes <c>var a = move.axes</c> followed by <c>var.a[0].letter</c> work there. Doing the same
    /// here would mean holding a reference to a model object that the update task mutates and that a
    /// reconfiguration can detach from the model - a variable that then reads stale values rather than
    /// failing - and a global could not hold one at all, because it is serialised for the clients.
    /// A path re-resolved on each read is what would fit; nothing here does that yet
    /// </para>
    /// </remarks>
    public static bool OccursIn(object? value)
    {
        if (value is ObjectModelValue)
        {
            return true;
        }
        if (value is object?[] array)
        {
            foreach (object? element in array)
            {
                if (OccursIn(element))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
