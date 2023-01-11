using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Generic container for object model arrays
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    public class ModelCollection<T> : ObservableCollection<T>, IModelCollection
    {
        /// <summary>
        /// Removes all items from the collection
        /// </summary>
        protected override void ClearItems()
        {
            List<T> removed = new(this);
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
        /// Assign the properties from another instance.
        /// This is required to update model properties which do not have a setter
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(object from)
        {
            // Validate the types
            if (from is not ModelCollection<T> other)
            {
                throw new ArgumentException("Types do not match", nameof(from));
            }

            // Delete obsolete items
            for (int i = Count; i > other.Count; i--)
            {
                RemoveAt(i - 1);
            }

            // Update common items
            Type itemType = typeof(T);
            if (typeof(IModelObject).IsAssignableFrom(itemType))
            {
                for (int i = 0; i < Math.Min(Count, other.Count); i++)
                {
                    IModelObject myItem = (IModelObject)this[i]!;
                    IModelObject otherItem = (IModelObject)other[i]!;
                    if (myItem is null || otherItem is null)
                    {
                        this[i] = (T)otherItem!;
                    }
                    else if (myItem.GetType() != otherItem.GetType())
                    {
                        this[i] = (T)otherItem.Clone();
                    }
                    else
                    {
                        myItem.Assign(otherItem);
                    }
                }
            }
            else if (itemType.IsArray)
            {
                for (int i = 0; i < Math.Min(Count, other.Count); i++)
                {
                    if (this[i] is null || other[i] is null)
                    {
                        this[i] = other[i];
                    }
                    else
                    {
                        IList listItem = (IList)this[i]!, fromItem = (IList)other[i]!;
                        if (listItem.Count != fromItem.Count)
                        {
                            this[i] = other[i];
                        }
                        else
                        {
                            for (int k = 0; k < listItem.Count; k++)
                            {
                                if (!Equals(listItem[k], fromItem[k]))
                                {
                                    this[i] = other[i];
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(Count, other.Count); i++)
                {
                    if (!Equals(this[i], other[i]))
                    {
                        this[i] = other[i];
                    }
                }
            }

            // Add missing items
            for (int i = Count; i < other.Count; i++)
            {
                Add(other[i]);
            }
        }

        /// <summary>
        /// Create a clone of this list
        /// </summary>
        /// <returns>Cloned list</returns>
        public object Clone()
        {
            ModelCollection<T> clone = new();
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
        public object? FindDifferences(IModelObject other)
        {
            // Check the types
            if (other is not ModelCollection<T> otherList)
            {
                // Types differ, return the entire instance
                return this;
            }
            Type itemType = typeof(T);

            // Compare the collections
            bool hadDiffs = (Count != otherList.Count);
            IList diffs = new object[Count];
            if (typeof(IModelObject).IsAssignableFrom(itemType))
            {
                for (int i = 0; i < Count; i++)
                {
                    if (i < otherList.Count)
                    {
                        IModelObject myItem = (IModelObject)this[i]!, otherItem = (IModelObject)otherList[i]!;
                        if (otherItem is null || myItem is null || otherItem.GetType() != myItem.GetType())
                        {
                            hadDiffs = myItem != otherItem;
                            diffs[i] = myItem;
                        }
                        else
                        {
                            object? diff = myItem.FindDifferences(otherItem);
                            if (diff is not null)
                            {
                                hadDiffs = true;
                                diffs[i] = diff;
                            }
                            else
                            {
                                diffs[i] = new Dictionary<string, object?>();
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
                        if (!this[i]!.Equals(otherList[i]))
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
        public IModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            UpdateFromJson(jsonElement, ignoreSbcProperties, 0, true);
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
            int arrayLength = jsonElement.GetArrayLength();

            // Delete obsolete items when the last update has been processed
            if (last)
            {
                for (int i = Count; i > offset + arrayLength; i--)
                {
                    RemoveAt(i - 1);
                }
            }

            Type itemType = typeof(T);
            if (typeof(IModelObject).IsAssignableFrom(itemType))
            {
                // Update model items
                for (int i = offset; i < Math.Min(Count, offset + arrayLength); i++)
                {
                    IModelObject item = (IModelObject)this[i]!;
                    JsonElement jsonItem = jsonElement[i - offset];
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        if (this[i] is not null)
                        {
                            this[i] = default!;
                        }
                    }
                    else
                    {
                        item ??= (IModelObject)Activator.CreateInstance(itemType)!;
                        T? updatedItem = (T?)item.UpdateFromJson(jsonItem, ignoreSbcProperties)!;
                        if (!ReferenceEquals(this[i], updatedItem))
                        {
                            this[i] = updatedItem;
                        }
                    }
                }

                // Add missing items
                for (int i = Count; i < offset + arrayLength; i++)
                {
                    JsonElement jsonItem = jsonElement[i - offset];
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        Add(default!);
                    }
                    else
                    {
                        IModelObject? newItem = (IModelObject?)Activator.CreateInstance(itemType)!;
                        Add((T?)newItem.UpdateFromJson(jsonElement[i]!, ignoreSbcProperties)!);
                    }
                }
            }
            else
            {
                // Update items
                for (int i = 0; i < Math.Min(Count, offset + arrayLength); i++)
                {
                    JsonElement jsonItem = jsonElement[i - offset];
                    if (jsonItem.ValueKind == JsonValueKind.Null)
                    {
                        if (this[i] is not null)
                        {
                            this[i] = default!;
                        }
                    }
                    else if (itemType == typeof(bool) && jsonItem.ValueKind == JsonValueKind.Number)
                    {
                        try
                        {
                            bool itemValue = Convert.ToBoolean(jsonItem.GetInt32());
                            if (!Equals(this[i], itemValue))
                            {
                                this[i] = (T)(object)itemValue;
                            }
                        }
                        catch (FormatException e)
                        {
                            throw new JsonException($"Failed to deserialize item type bool from JSON {jsonItem.GetRawText()}", e);
                        }
                    }
                    else
                    {
                        try
                        {
                            T itemValue = JsonSerializer.Deserialize<T>(jsonItem.GetRawText(), Utility.JsonHelper.DefaultJsonOptions)!;
                            if (itemType.IsArray)
                            {
                                IList listItem = (IList)this[i]!, newItem = (IList)itemValue;
                                if (listItem is null || listItem.Count != newItem.Count)
                                {
                                    this[i] = itemValue;
                                }
                                else
                                {
                                    for (int k = 0; k < listItem.Count; k++)
                                    {
                                        if (!Equals(listItem[k], newItem[k]))
                                        {
                                            this[i] = itemValue;
                                            break;
                                        }
                                    }
                                }
                            }
                            else if (!Equals(this[i], itemValue))
                            {
                                this[i] = itemValue;
                            }
                        }
                        catch (JsonException e) when (ObjectModel.DeserializationFailed(this, itemType, jsonItem.Clone(), e))
                        {
                            // suppressed
                        }
                    }
                }

                // Add missing items
                for (int i = Count; i < offset + arrayLength; i++)
                {
                    JsonElement jsonItem = jsonElement[i - offset];
                    if (itemType == typeof(bool) && jsonItem.ValueKind == JsonValueKind.Number)
                    {
                        try
                        {
                            Add((T)(object)Convert.ToBoolean(jsonItem.GetInt32()));
                        }
                        catch (FormatException e)
                        {
                            throw new JsonException($"Failed to deserialize item type bool from JSON {jsonItem.GetRawText()}", e);
                        }
                    }
                    else
                    {
                        try
                        {
                            T newItem = JsonSerializer.Deserialize<T>(jsonItem.GetRawText(), Utility.JsonHelper.DefaultJsonOptions)!;
                            Add(newItem);
                        }
                        catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(T), jsonItem.Clone(), e))
                        {
                            // suppressed
                        }
                    }
                }
            }
        }
    }
}
