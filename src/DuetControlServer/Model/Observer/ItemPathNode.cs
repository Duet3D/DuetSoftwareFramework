using System;
using System.Collections;

namespace DuetControlServer.Model;

/// <summary>
/// Node of an object model path pointing to a list item
/// </summary>
/// <remarks>
/// This is necessary for the case of model items changing in a collection
/// </remarks>
/// <param name="name">List name</param>
/// <param name="index">Index of the changed item</param>
/// <param name="list">Reference to the list</param>
public class ItemPathNode(string name, int index, IList list)
{
    /// <summary>
    /// Name of the list
    /// </summary>
    public readonly string Name = name;

    /// <summary>
    /// Index of the item
    /// </summary>
    public readonly int Index = index;

    /// <summary>
    /// Internal list reference
    /// </summary>
    public readonly IList List = list;

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is ItemPathNode other && other.Name == Name && other.Index == Index && other.List.Count == List.Count;
    }

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name.GetHashCode(), Index.GetHashCode(), List.Count.GetHashCode());

    /// <summary>
    /// Convert an item node to a string (for debugging)
    /// </summary>
    /// <returns>String representation of this node</returns>
    public override string ToString() => $"{Name}[{Index} of {List.Count}]";
}
