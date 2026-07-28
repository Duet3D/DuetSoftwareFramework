namespace DuetAPI.ObjectModel;

/// <summary>
/// Kind of an object model property as seen by the generated property accessors
/// </summary>
public enum ModelPropertyKind
{
    /// <summary>
    /// Scalar value, enum, or any other type that cannot be traversed further
    /// </summary>
    Value,

    /// <summary>
    /// Raw JSON value that is traversed by JSON property name
    /// </summary>
    JsonElement,

    /// <summary>
    /// Nested model object
    /// </summary>
    ModelObject,

    /// <summary>
    /// Collection of model items indexed by position
    /// </summary>
    ModelCollection,

    /// <summary>
    /// Dictionary of model items indexed by key
    /// </summary>
    ModelDictionary,

    /// <summary>
    /// Plain observable collection of values
    /// </summary>
    ObservableCollection
}
