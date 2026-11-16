using System;
using System.Collections.Generic;

namespace DuetControlServer.Model;

/// <summary>
/// Operations on an object model collection indexed by item number, such as <c>tools[]</c>, <c>heat.heaters[]</c> or
/// <c>fans[]</c>, where an unconfigured number is a null entry
/// </summary>
/// <remarks>The caller must hold the object model lock, and the write lock for the operations that change the collection</remarks>
internal static class IndexedCollection
{
    /// <summary>
    /// The item with a number, or null if the number is out of range or nothing has it
    /// </summary>
    /// <param name="collection">The collection</param>
    /// <param name="index">The item number</param>
    /// <returns>The item, or null</returns>
    public static T? GetAt<T>(this IList<T?> collection, int index) where T : class
        => (index >= 0 && index < collection.Count) ? collection[index] : null;

    /// <summary>
    /// Store an item under its number, padding the collection with nulls up to it
    /// </summary>
    /// <param name="collection">The collection</param>
    /// <param name="index">The item number</param>
    /// <param name="item">The item</param>
    /// <returns>The item</returns>
    public static T SetAt<T>(this IList<T?> collection, int index, T item) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        while (collection.Count <= index)
        {
            collection.Add(null);
        }
        collection[index] = item;
        return item;
    }

    /// <summary>
    /// The item with a number, created and stored under it if there is none
    /// </summary>
    /// <param name="collection">The collection</param>
    /// <param name="index">The item number</param>
    /// <param name="create">Creates the missing item</param>
    /// <returns>The item</returns>
    public static T GetOrAddAt<T>(this IList<T?> collection, int index, Func<T> create) where T : class
        => collection.GetAt(index) ?? collection.SetAt(index, create());

    /// <summary>
    /// Empty the slot of an item number, leaving the numbers of the items above it unchanged
    /// </summary>
    /// <param name="collection">The collection</param>
    /// <param name="index">The item number, which need not be in range</param>
    /// <param name="trim">Whether to drop the nulls the collection then ends with, so that it ends at the highest item left</param>
    public static void ClearAt<T>(this IList<T?> collection, int index, bool trim = false) where T : class
    {
        if (index >= 0 && index < collection.Count)
        {
            collection[index] = null;
        }
        if (trim)
        {
            while (collection.Count > 0 && collection[^1] is null)
            {
                collection.RemoveAt(collection.Count - 1);
            }
        }
    }
}
