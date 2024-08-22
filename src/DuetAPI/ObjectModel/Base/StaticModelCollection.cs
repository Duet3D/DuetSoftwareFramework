using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Generic container for model object arrays with static items
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    public class StaticModelCollection<T> : ObservableCollection<T?>, IModelCollection where T : IStaticModelObject?, new()
    {
        /// <summary>
        /// Default constructor
        /// </summary>
        public StaticModelCollection() : base() { }

        /// <summary>
        /// Overloading constructor that takes items for initialization
        /// </summary>
        /// <param name="collection">Collection to use for items</param>
        public StaticModelCollection(IEnumerable<T> collection) : base(collection) { }

        /// <summary>
        /// Overloading constructor that takes a list for initialization
        /// </summary>
        /// <param name="list">List to use for items</param>
        public StaticModelCollection(List<T?> list) : base(list) { }

        /// <summary>
        /// Removes all items from the collection
        /// </summary>
        protected override void ClearItems()
        {
            List<T?> removed = new(this);
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

#if false
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
            if (typeof(IStaticModelObject).IsAssignableFrom(itemType))
            {
                for (int i = 0; i < Math.Min(Count, other.Count); i++)
                {
                    IStaticModelObject myItem = (IStaticModelObject)this[i]!;
                    IStaticModelObject otherItem = (IStaticModelObject)other[i]!;
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
            ModelCollection<T> clone = [];
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
        public object? FindDifferences(IStaticModelObject other)
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
            if (typeof(IStaticModelObject).IsAssignableFrom(itemType))
            {
                for (int i = 0; i < Count; i++)
                {
                    if (i < otherList.Count)
                    {
                        IStaticModelObject myItem = (IStaticModelObject)this[i]!, otherItem = (IStaticModelObject)otherList[i]!;
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
#endif

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        /// <remarks>Accepts null as the JSON value to clear existing items</remarks>
        public IStaticModelObject? UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
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

            // Update model items
            for (int i = offset; i < Math.Min(Count, offset + arrayLength); i++)
            {
                T? item = this[i];
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
                    try
                    {
                        if (item == null)
                        {
                            item = new T();
                            item.UpdateFromJson(jsonItem, ignoreSbcProperties);
                            this[i] = item;
                        }
                        else
                        {
                            item.UpdateFromJson(jsonItem, ignoreSbcProperties);
                        }
                    }
                    catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(T), jsonItem, e))
                    {
                        // suppressed
                    }
                }
            }

            // Add missing items
            for (int i = Count; i < offset + arrayLength; i++)
            {
                JsonElement jsonItem = jsonElement[i - offset];
                if (jsonItem.ValueKind == JsonValueKind.Null)
                {
                    Add(default);
                }
                else
                {
                    try
                    {
                        T newItem = new();
                        newItem.UpdateFromJson(jsonItem, ignoreSbcProperties);
                        Add(newItem);
                    }
                    catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(T), jsonItem, e))
                    {
                        // suppressed
                    }
                }
            }
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>

        void IStaticModelObject.UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties) => UpdateFromJson(jsonElement, ignoreSbcProperties, 0, true);

        /// <summary>
        /// Update this collection from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <param name="offset">Index offset</param>
        /// <param name="last">Whether this is the last update</param>
        public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties, int offset = 0, bool last = true)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("expected start of array");
            }

            // Update or add items
            int i = offset;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    try
                    {
                        if (i >= Count)
                        {
                            T newItem = new();
                            newItem.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                            Add(newItem);
                        }
                        else
                        {
                            T? item = this[i];
                            if (item == null)
                            {
                                item = new T();
                                item.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                                this[i] = item;
                            }
                            else
                            {
                                item.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                            }
                        }
                        i++;
                    }
                    catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(T), JsonElement.ParseValue(ref reader), e))
                    {
                        // suppressed
                    }
                }
                else if (reader.TokenType == JsonTokenType.Null)
                {
                    Add(default);
                    i++;
                }
            }

            // Delete obsolete items when the last update has been processed
            if (last)
            {
                while (Count > i)
                {
                    RemoveAt(Count - 1);
                }
            }
        }

        /// <summary>
        /// Update this collection from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties) => UpdateFromJsonReader(ref reader, ignoreSbcProperties, 0, true);
    }
}
