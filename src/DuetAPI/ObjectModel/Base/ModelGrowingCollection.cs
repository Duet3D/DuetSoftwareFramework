using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Helper class to keep track of individual model collection subtypes
    /// </summary>
    public static class ModelGrowingCollection
    {
        /// <summary>
        /// List of types that are derived from this class
        /// </summary>
        private static readonly List<Type> _derivedTypes = new();

        /// <summary>
        /// Check if the given type is derived from a <see cref="ModelCollection{T}"/>
        /// </summary>
        /// <param name="type">Type to check</param>
        /// <returns>Whether the type is derived</returns>
        public static bool TypeMatches(Type type)
        {
            lock (_derivedTypes)
            {
                return _derivedTypes.Contains(type);
            }
        }

        /// <summary>
        /// Register another growing model collection type
        /// </summary>
        /// <param name="type">Specific collection type</param>
        internal static void RegisterType(Type type)
        {
            lock (_derivedTypes)
            {
                if (!_derivedTypes.Contains(type))
                {
                    _derivedTypes.Add(type);
                }
            }
        }
    }

    /// <summary>
    /// Generic list container to which items can be added or which can be cleared only
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    public class ModelGrowingCollection<T> : ModelCollection<T>
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        public ModelGrowingCollection() : base() => ModelGrowingCollection.RegisterType(GetType());

        /// <summary>
        /// Called after the collection has been changed but before the corresponding event has been raised
        /// </summary>
        /// <param name="e">Event arguments</param>
        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                case NotifyCollectionChangedAction.Reset:
                    base.OnCollectionChanged(e);
                    break;

                // Other modification types are not supported so don't propagate other change events
            }
        }

        /// <summary>
        /// Update this collection from a given JSON array
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        public override void UpdateFromJson(JsonElement jsonElement)
        {
            ModelGrowingCollectionHelper.UpdateFromJson(this, typeof(T), jsonElement, false);
        }
    }

    /// <summary>
    /// Internal untyped helper class for dealing with growing model collections
    /// </summary>
    internal static class ModelGrowingCollectionHelper
    {
        /// <summary>
        /// Assign items to a given list
        /// </summary>
        /// <param name="list">List to assign to</param>
        /// <param name="from">List to assign from</param>
        public static void Assign(IList list, IList from)
        {
            // Assigning null values is not supported
            if (from == null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            // Assign the list items
            list.Clear();
            foreach (object item in from)
            {
                list.Add(item);
            }
        }

        /// <summary>
        /// Update a list from a given JSON array
        /// </summary>
        /// <param name="list">List to update</param>
        /// <param name="itemType">Item type</param>
        /// <param name="jsonElement">Element to update the intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        public static void UpdateFromJson(IList list, Type itemType, JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.GetArrayLength() == 0)
            {
                list.Clear();
            }
            else if (itemType.IsSubclassOf(typeof(ModelObject)))
            {
                foreach (JsonElement jsonItem in jsonElement.EnumerateArray())
                {
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        list.Add(null);
                    }
                    else
                    {
                        ModelObject newItem = (ModelObject)Activator.CreateInstance(itemType);
                        newItem = newItem.UpdateFromJson(jsonItem, ignoreSbcProperties);
                        list.Add(newItem);
                    }
                }
            }
            else
            {
                foreach (JsonElement jsonItem in jsonElement.EnumerateArray())
                {
                    object newItem = JsonSerializer.Deserialize(jsonItem.GetRawText(), itemType);
                    list.Add(newItem);
                }
            }
        }
    }
}
