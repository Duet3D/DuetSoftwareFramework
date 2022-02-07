using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Generic list container to which items can be added or which can be cleared only
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    public class ModelGrowingCollection<T> : ObservableCollection<T>, IGrowingModelCollection
    {
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
        /// Assign the properties from another instance.
        /// This is required to update model properties which do not have a setter
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(object from)
        {
            // Assigning null values is not supported
            if (from == null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            // Validate the types
            Type myType = GetType();
            if (from.GetType() != myType)
            {
                throw new ArgumentException("Types do not match", nameof(from));
            }

            // Clear existing items
            ClearItems();

            // Add other items
            ModelGrowingCollection<T> other = (ModelGrowingCollection<T>)from;
            foreach (T item in other)
            {
                if (item is ICloneable cloneableItem)
                {
                    object clonedItem = cloneableItem.Clone();
                    Add((T)clonedItem);
                }
                else
                {
                    Add(item);
                }
            }
        }

        /// <summary>
        /// Create a clone of this list
        /// </summary>
        /// <returns>Cloned list</returns>
        public object Clone()
        {
            ModelGrowingCollection<T> clone = new ModelGrowingCollection<T>();
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
        /// Create a dictionary or list of all the differences between this instance and another.
        /// This method outputs own property values that differ from the other instance
        /// </summary>
        /// <param name="other">Other instance</param>
        /// <returns>Object differences or null if both instances are equal</returns>
        public object FindDifferences(IModelObject other)
        {
            // Check the types
            Type myType = GetType(), otherType = other?.GetType();
            if (myType != otherType)
            {
                // Types differ, return the entire instance
                return this;
            }

            // Get the other instance
            Type itemType = typeof(T);
            ModelGrowingCollection<T> otherList = (ModelGrowingCollection<T>)other;

            bool hadDiffs = (Count != otherList.Count);
            IList diffs = new object[Count];
            if (typeof(IModelObject).IsAssignableFrom(itemType))
            {
                for (int i = 0; i < Count; i++)
                {
                    if (i < otherList.Count)
                    {
                        IModelObject myItem = (IModelObject)this[i], otherItem = (IModelObject)otherList[i];
                        if (otherItem == null || myItem == null || otherItem.GetType() != myItem.GetType())
                        {
                            hadDiffs = myItem != otherItem;
                            diffs[i] = myItem;
                        }
                        else
                        {
                            object diff = myItem.FindDifferences(otherItem);
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
                        diffs[i] = this[i];
                    }
                }
            }
            else
            {
                diffs = this;
                if (!hadDiffs)
                {
                    for (int i = 0; i < Count; i++)
                    {
                        if (!this[i].Equals(otherList[i]))
                        {
                            hadDiffs = true;
                            break;
                        }
                    }
                }
            }
            return hadDiffs ? diffs : null;
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        /// <remarks>Accepts null as the JSON value to clear existing items</remarks>
        public IModelObject UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                ClearItems();
            }
            else
            {
                foreach (JsonElement jsonItem in jsonElement.EnumerateArray())
                {
                    T itemValue = JsonSerializer.Deserialize<T>(jsonItem.GetRawText(), Utility.JsonHelper.DefaultJsonOptions);
                    Add(itemValue);
                }
            }
            return this;
        }

        /// <summary>
        /// Update this collection from a given JSON array
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <param name="offset">Index offset</param>
        /// <param name="last">Whether this is the last update</param>
        public void UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties, int offset = 0, bool last = true)
        {
            UpdateFromJson(jsonElement, ignoreSbcProperties);
        }
    }
}
