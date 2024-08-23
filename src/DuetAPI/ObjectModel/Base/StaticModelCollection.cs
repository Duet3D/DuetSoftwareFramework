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
    public class StaticModelCollection<T> : ObservableCollection<T>, IModelCollection where T : IStaticModelObject?, new()
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
        public StaticModelCollection(List<T> list) : base(list) { }

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

        /// <summary>
        /// Assign the properties from another instance.
        /// This is required to update model properties which do not have a setter
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(IStaticModelObject from)
        {
            // Validate the types
            if (from is not StaticModelCollection<T> other)
            {
                throw new ArgumentException("Types do not match", nameof(from));
            }

            // Delete obsolete items
            for (int i = Count; i > other.Count; i--)
            {
                RemoveAt(i - 1);
            }

            // Update common items
            for (int i = 0; i < Math.Min(Count, other.Count); i++)
            {
                T? myItem = this[i], otherItem = other[i]!;
                if (otherItem is null)
                {
                    if (myItem is not null)
                    {
                        this[i] = default!;
                    }
                }
                else
                {
                    if (myItem is null || myItem.GetType() != otherItem.GetType())
                    {
                        this[i] = (T)otherItem.Clone();
                    }
                    else
                    {
                        myItem.Assign(otherItem);
                    }
                }
            }

            // Add missing items
            for (int i = Count; i < other.Count; i++)
            {
                T? item = other[i];
                Add(item is null ? default! : (T)item.Clone());
            }
        }

        /// <summary>
        /// Create a clone of this list
        /// </summary>
        /// <returns>Cloned list</returns>
        public object Clone()
        {
            StaticModelCollection<T> clone = [];
            foreach (T? item in this)
            {
                clone.Add(item is null ? default! : (T)item.Clone());
            }
            return clone;
        }

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
                    Add(default!);
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
                    Add(default!);
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
