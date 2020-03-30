using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Helper class to keep track of individual model collection subtypes
    /// </summary>
    public static class ModelCollection
    {
        /// <summary>
        /// List of types that are derived from this class
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Type> _derivedTypes = new ConcurrentDictionary<Type, Type>();

        /// <summary>
        /// Check if the given type is derived from a <see cref="ModelCollection{T}"/> and return the corresponding item type
        /// </summary>
        /// <param name="type">Type to check</param>
        /// <param name="itemType">Item type</param>
        /// <returns>Whether the item type could be found</returns>
        public static bool GetItemType(Type type, out Type itemType) => _derivedTypes.TryGetValue(type, out itemType);

        /// <summary>
        /// Register another model collection type
        /// </summary>
        /// <param name="type">Specific collection type</param>
        /// <param name="itemType">Item type</param>
        internal static void RegisterType(Type type, Type itemType) => _derivedTypes.TryAdd(type, itemType);
    }

    /// <summary>
    /// Generic container for object model arrays
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    public class ModelCollection<T> : ObservableCollection<T>, ICloneable
    {
        /// <summary>
        /// Constructor of this class
        /// </summary>
        public ModelCollection() => ModelCollection.RegisterType(GetType(), typeof(T));

        /// <summary>
        /// Removes all items from the collection
        /// </summary>
        protected override void ClearItems()
        {
            List<T> removed = new List<T>(this);
            base.ClearItems();
            base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed));
        }

        /// <summary>
        /// Raises the change event handler
        /// </summary>
        /// <param name="e">Event arguments</param>
        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Reset)
            {
                // Do not propagate Reset events...
                base.OnCollectionChanged(e);
            }
        }

        /// <summary>
        /// Create a clone of this list
        /// </summary>
        /// <returns>Cloned list</returns>
        public object Clone()
        {
            ModelCollection<T> clone = new ModelCollection<T>();
            foreach (T item in this)
            {
                if (item is ICloneable cloneableItem)
                {
                    object clonedItem = cloneableItem.Clone();
                    clone.Add((T)clonedItem);
                }
                else
                {
                    clone.Add(item);
                }
            }
            return clone;
        }

        /// <summary>
        /// Assign this collection from another one
        /// </summary>
        /// <param name="from">Element to assign this instance from</param>
        public virtual void Assign(ModelCollection<T> from)
        {
            ModelCollectionHelper.Assign(this, typeof(T), from);
        }

        /// <summary>
        /// Update this collection from a given JSON array
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        public virtual void UpdateFromJson(JsonElement jsonElement)
        {
            ModelCollectionHelper.UpdateFromJson(this, typeof(T), jsonElement, false);
        }
    }

    /// <summary>
    /// Internal untyped helper class for dealing with model collections
    /// </summary>
    internal static class ModelCollectionHelper
    {
        /// <summary>
        /// Assign items to a given list
        /// </summary>
        /// <param name="list">List to assign to</param>
        /// <param name="itemType">Item type</param>
        /// <param name="from">List to assign from</param>
        public static void Assign(IList list, Type itemType, IList from)
        {
            // Assigning null values is not supported
            if (from == null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            // Delete obsolete items
            for (int i = list.Count; i > from.Count; i--)
            {
                list.RemoveAt(i - 1);
            }

            // Update common items
            if (itemType.IsSubclassOf(typeof(ModelObject)))
            {
                for (int i = 0; i < Math.Min(list.Count, from.Count); i++)
                {
                    ModelObject item = (ModelObject)list[i];
                    ModelObject sourceItem = (ModelObject)from[i];
                    if (item == null || sourceItem == null)
                    {
                        list[i] = sourceItem;
                    }
                    else if (sourceItem.GetType() != item.GetType())
                    {
                        list[i] = sourceItem.Clone();
                    }
                    else
                    {
                        item.Assign(sourceItem);
                    }
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(list.Count, from.Count); i++)
                {
                    list[i] = from[i];
                }
            }

            // Add missing items
            for (int i = list.Count; i < from.Count; i++)
            {
                list.Add(from[i]);
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
            int arrayLength = jsonElement.GetArrayLength();

            // Delete obsolete items
            for (int i = list.Count; i > arrayLength; i--)
            {
                list.RemoveAt(i - 1);
            }

            if (itemType.IsSubclassOf(typeof(ModelObject)))
            {
                // Update model items
                for (int i = 0; i < Math.Min(list.Count, arrayLength); i++)
                {
                    ModelObject item = (ModelObject)list[i];
                    JsonElement jsonItem = jsonElement[i];
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        if (item != null)
                        {
                            list[i] = null;
                        }
                    }
                    else if (item == null)
                    {
                        item = (ModelObject)Activator.CreateInstance(itemType);
                        list[i] = item.UpdateFromJson(jsonItem, ignoreSbcProperties);
                    }
                    else
                    {
                        ModelObject updatedInstance = item.UpdateFromJson(jsonItem, ignoreSbcProperties);
                        if (updatedInstance != item)
                        {
                            list[i] = updatedInstance;
                        }
                    }
                }

                // Add missing items
                for (int i = list.Count; i < arrayLength; i++)
                {
                    JsonElement jsonItem = jsonElement[i];
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        list.Add(null);
                    }
                    else
                    {
                        ModelObject newItem = (ModelObject)Activator.CreateInstance(itemType);
                        newItem = newItem.UpdateFromJson(jsonElement[i], ignoreSbcProperties);
                        list.Add(newItem);
                    }
                }
            }
            else
            {
                // Update items
                for (int i = 0; i < Math.Min(list.Count, arrayLength); i++)
                {
                    object item = list[i];
                    JsonElement jsonItem = jsonElement[i];
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        if (item != null)
                        {
                            list[i] = null;
                        }
                    }
                    else
                    {
                        object newItem = JsonSerializer.Deserialize(jsonItem.GetRawText(), itemType);
                        if (item != newItem)
                        {
                            list[i] = newItem;
                        }
                    }
                }

                // Add missing items
                for (int i = list.Count; i < arrayLength; i++)
                {
                    object newItem = JsonSerializer.Deserialize(jsonElement[i].GetRawText(), itemType);
                    list.Add(newItem);
                }
            }
        }

        /// <summary>
        /// Find the diffs between two lists
        /// </summary>
        /// <param name="oldList">Old list</param>
        /// <param name="newList">New list</param>
        /// <param name="itemType">Item type</param>
        /// <returns>Differences of the lists or null if they are equal</returns>
        public static IList FindDiffs(IList oldList, IList newList, Type itemType)
        {
            bool hadDiffs = (oldList.Count != newList.Count);
            IList diffs = new object[newList.Count];

            if (itemType.IsSubclassOf(typeof(ModelObject)))
            {
                for (int i = 0; i < newList.Count; i++)
                {
                    if (i < oldList.Count)
                    {
                        ModelObject oldItem = (ModelObject)oldList[i], newItem = (ModelObject)newList[i];
                        if (oldItem == null || newItem == null || oldItem.GetType() != newItem.GetType())
                        {
                            if (oldItem != newItem)
                            {
                                hadDiffs = true;
                                diffs[i] = newItem;
                            }
                            else
                            {
                                diffs[i] = new Dictionary<string, object>();
                            }
                        }
                        else
                        {
                            object diff = newItem.MakePatch(oldItem);
                            if (diff != null)
                            {
                                hadDiffs = true;
                                diffs[i] = diff;
                            }
                            else
                            {
                                diffs[i] = new Dictionary<string, object>();
                            }
                        }
                    }
                    else
                    {
                        diffs[i] = newList[i];
                    }
                }
            }
            else if (ModelCollection.GetItemType(itemType, out Type subItemType))
            {
                for (int i = 0; i < newList.Count; i++)
                {
                    if (i < oldList.Count)
                    {
                        IList oldItem = (IList)oldList[i], newItem = (IList)newList[i];
                        if (oldItem == null || newItem == null || oldItem.GetType() != newItem.GetType())
                        {
                            if (oldItem != newItem)
                            {
                                hadDiffs = true;
                                diffs[i] = newItem;
                            }
                            else
                            {
                                diffs[i] = new Dictionary<string, object>();
                            }
                        }
                        else
                        {
                            object diff = FindDiffs(oldItem, newItem, subItemType);
                            if (diff != null)
                            {
                                hadDiffs = true;
                                diffs[i] = diff;
                            }
                            else
                            {
                                diffs[i] = new Dictionary<string, object>();
                            }
                        }
                    }
                    else
                    {
                        diffs[i] = newList[i];
                    }
                }
            }
            else
            {
                diffs = newList;
                if (!hadDiffs)
                {
                    for (int i = 0; i < newList.Count; i++)
                    {
                        if (!oldList[i].Equals(newList[i]))
                        {
                            hadDiffs = true;
                            break;
                        }
                    }
                }
            }
            return hadDiffs ? diffs : null;
        }
    }
}
