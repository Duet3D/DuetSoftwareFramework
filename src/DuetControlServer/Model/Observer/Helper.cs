using System;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Partial class implementation of the observer for generic helpers
    /// </summary>
    public static partial class Observer
    {
        /// <summary>
        /// Retrieve the item type of a model collection type
        /// </summary>
        /// <param name="collectionType">Type of the collection</param>
        /// <returns>Item type or null if not found</returns>
        private static Type? GetItemType(Type collectionType)
        {
            for (Type? type = collectionType; type is not null; type = type.BaseType)
            {
                if (type.IsGenericType)
                {
                    return type.GetGenericArguments()[0];
                }
            }
            return null;
        }
    }
}
