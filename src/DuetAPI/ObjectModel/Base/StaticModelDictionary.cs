using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class for holding string keys and custom values
    /// </summary>
    /// <remarks>
    /// Key names are NOT converted to camel-case (unlike regular class properties)
    /// </remarks>
    /// <param name="nullRemovesItems">Defines if setting items to null effectively removes them</param>
    [JsonConverter(typeof(StaticModelDictionaryConverter))]
    public sealed class StaticModelDictionary<TValue>(bool nullRemovesItems) : IDictionary<string, TValue>, IModelDictionary where TValue : IStaticModelObject, new()
    {
        /// <summary>
        /// Flags if keys can be removed again by setting their value to null
        /// </summary>
        [JsonIgnore]
        public bool NullRemovesItems { get; } = nullRemovesItems;

        /// <summary>
        /// Internal storage for key/value pairs
        /// </summary>
        private readonly Dictionary<string, TValue> _dictionary = [];

        /// <summary>
        /// Event that is called when the entire directory is cleared. Only used if <see cref="NullRemovesItems"/> is false
        /// </summary>
        public event EventHandler? DictionaryCleared;

        /// <summary>
        /// Event that is called when a key has been changed
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Event that is called when a key is being changed
        /// </summary>
        public event PropertyChangingEventHandler? PropertyChanging;

        /// <summary>
        /// Get an element from the dictionary
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        [return: MaybeNull]
        private TValue GetValue(string key)
        {
            if (NullRemovesItems)
            {
                return _dictionary.TryGetValue(key, out TValue? result) ? result : default;
            }
            return _dictionary[key];
        }

        /// <summary>
        /// Index operator
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        [AllowNull]
        public TValue this[string key]
        {
            get => GetValue(key)!;
            set
            {
                PropertyChanging?.Invoke(this, new(key));
                if (NullRemovesItems && value is null)
                {
                    _dictionary.Remove(key);
                }
                else
                {
                    _dictionary[key] = value!;
                }
                PropertyChanged?.Invoke(this, new(key));
            }
        }

        /// <summary>
        /// Basic index operator
        /// </summary>
        /// <param name="key">Key object</param>
        /// <returns>Value if found</returns>
        [AllowNull]
        [MaybeNull]
        public object this[object key]
        {
            get => this[(string)key];
            set => this[(string)key] = (TValue?)value;
        }

        /// <summary>
        /// Get an enumerator for this instance
        /// </summary>
        /// <returns>Enumerator instance</returns>
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

        /// <summary>
        /// List of keys
        /// </summary>
        public ICollection<string> Keys => _dictionary.Keys;

        /// <summary>
        /// List of values
        /// </summary>
        public ICollection<TValue> Values => _dictionary.Values;

        /// <summary>
        /// Whether the dictionary is read-only
        /// </summary>
        public bool IsReadOnly => false;

        /// <summary>
        /// Whether this dictionary has a fixed size
        /// </summary>
        public bool IsFixedSize => false;

        /// <summary>
        /// Collection of dictionary keys
        /// </summary>
        ICollection IDictionary.Keys => _dictionary.Keys;

        /// <summary>
        /// Collection of dictionary values
        /// </summary>
        ICollection IDictionary.Values => _dictionary.Values;

        /// <summary>
        /// If this is thread-safe
        /// </summary>
        public bool IsSynchronized => false;

        /// <summary>
        /// Synchronization root
        /// </summary>
        public object SyncRoot => _dictionary;

        /// <summary>
        /// Returns the number of items in this collection
        /// </summary>
        public int Count => _dictionary.Count;

        /// <summary>
        /// Add a new item
        /// </summary>
        /// <param name="key">Key to add</param>
        /// <param name="value">Value to add</param>
        public void Add(string key, TValue value)
        {
            if (NullRemovesItems && value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            PropertyChanging?.Invoke(this, new(key));
            _dictionary.Add(key, value);
            PropertyChanged?.Invoke(this, new(key));
        }

        /// <summary>
        /// Add a new item
        /// </summary>
        /// <param name="key">Key to add</param>
        /// <param name="value">Value to add</param>
        public void Add(object key, object? value) => Add((string)key, (TValue)value!);

        /// <summary>
        /// Add a new item
        /// </summary>
        /// <param name="item">Item to add</param>
        public void Add(KeyValuePair<string, TValue> item) => Add(item.Key, item.Value);

        /// <summary>
        /// Assign the properties from another instance
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(IStaticModelObject from)
        {
            // Validate the types
            if (from is not StaticModelDictionary<TValue> other)
            {
                throw new ArgumentException("Types do not match", nameof(from));
            }
            if (NullRemovesItems != other.NullRemovesItems)
            {
                throw new ArgumentException("Incompatible item null handling");
            }

            // Check if this dictionary needs to cleared first
            foreach (string key in Keys.ToList())
            {
                if (!other.ContainsKey(key))
                {
                    Clear();
                    break;
                }
            }

            // Update items
            foreach (var kv in other)
            {
                if (TryGetValue(kv.Key, out TValue? existingItem))
                {
                    if (existingItem is null)
                    {
                        if (kv.Value is not null)
                        {
                            this[kv.Key] = kv.Value;
                        }
                    }
                    else if (!existingItem.Equals(kv.Value))
                    {
                        if (kv.Value is ICloneable cloneableItem)
                        {
                            this[kv.Key] = (TValue)cloneableItem.Clone();

                        }
                        else
                        {
                            this[kv.Key] = kv.Value;
                        }
                    }
                }
                else
                {
                    Add(kv);
                }
            }
        }

        /// <summary>
        /// Clear this dictionary
        /// </summary>
        public void Clear()
        {
            if (NullRemovesItems)
            {
                List<string> keys = new(_dictionary.Keys);
                foreach (string key in keys)
                {
                    Remove(key);
                }
            }
            else
            {
                _dictionary.Clear();
                DictionaryCleared?.Invoke(this, new EventArgs());
            }
        }

        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns></returns>
        public object Clone()
        {
            StaticModelDictionary<TValue> clone = new(NullRemovesItems);
            foreach (KeyValuePair<string, TValue> kv in _dictionary)
            {
                if (kv.Value is ICloneable cloneableItem)
                {
                    clone.Add(kv.Key, (TValue)cloneableItem);
                }
                else
                {
                    clone.Add(kv);
                }
            }
            return clone;
        }

        /// <summary>
        /// Check if a key is present
        /// </summary>
        /// <param name="key">Key to check</param>
        /// <returns>Whether the key is present</returns>
        public bool ContainsKey(string key) => _dictionary.ContainsKey(key);

        /// <summary>
        /// Check if a key is present
        /// </summary>
        /// <param name="key">Key to check</param>
        /// <returns>Whether the key is present</returns>
        public bool Contains(object key) => ContainsKey((string)key);

        /// <summary>
        /// Copy this instance to another array
        /// </summary>
        /// <param name="array">Destination array</param>
        /// <param name="index">Start index</param>
        public void CopyTo(Array array, int index)
        {
            List<string> keys = new(_dictionary.Keys);
            for (int i = 0; i < Count; i++)
            {
                string key = keys[i];
                array.SetValue(new KeyValuePair<string, TValue>(key, _dictionary[key]), i + index);
            }
        }

        /// <summary>
        /// Copy this instance to another dictionary
        /// </summary>
        /// <param name="array">Destination array</param>
        /// <param name="arrayIndex">Start iondex</param>
        public void CopyTo(KeyValuePair<string, TValue>[] array, int arrayIndex) => CopyTo(array, arrayIndex);

        /// <summary>
        /// Check if a key-value pair exists
        /// </summary>
        /// <param name="item">Item to check</param>
        /// <returns>If the item exists in the dictionary</returns>
        public bool Contains(KeyValuePair<string, TValue> item) => _dictionary.TryGetValue(item.Key, out TValue? value) && Equals(value, item.Value);

        /// <summary>
        /// Get an enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();

        /// <summary>
        /// Get an enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        IDictionaryEnumerator IDictionary.GetEnumerator() => (IDictionaryEnumerator)GetEnumerator();

        /// <summary>
        /// Remove a key (only supported if <see cref="NullRemovesItems"/> is true)
        /// </summary>
        /// <param name="key">Key to remove</param>
        /// <returns>Whether the key could be found</returns>
        public bool Remove(string key)
        {
            if (NullRemovesItems)
            {
                if (_dictionary.TryGetValue(key, out _))
                {
                    PropertyChanging?.Invoke(this, new(key));
                    _dictionary.Remove(key);
                    PropertyChanged?.Invoke(this, new(key));
                    return true;
                }
                return false;
            }
            throw new NotSupportedException();
        }

        /// <summary>
        /// Remove a key (only supported if <see cref="NullRemovesItems"/> is true)
        /// </summary>
        /// <param name="key">Key to remove</param>
        /// <returns>Whether the key could be found</returns>
        public void Remove(object key) => Remove((string)key);

        /// <summary>
        /// Remove a key-value pair if applicable
        /// </summary>
        /// <param name="item">Item to remove</param>
        /// <returns>If the key-value pair was present</returns>
        public bool Remove(KeyValuePair<string, TValue> item) => Contains(item) && Remove(item.Key);

        /// <summary>
        /// Try to get a value
        /// </summary>
        /// <param name="key">Key to look up</param>
        /// <param name="value">Retrieved value</param>
        /// <returns>Whether the key could be found</returns>
        public bool TryGetValue(string key, out TValue value) => _dictionary.TryGetValue(key, out value!);

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        /// <remarks>Accepts null as the JSON value to clear existing items</remarks>
        public void UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {
                if (NullRemovesItems && jsonProperty.Value.ValueKind == JsonValueKind.Null)
                {
                    Remove(jsonProperty.Name);
                }
                else
                {
                    try
                    {
                        TValue newValue = new();
                        newValue.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                        if (!TryGetValue(jsonProperty.Name, out TValue? value) || !ReferenceEquals(value, newValue))
                        {
                            this[jsonProperty.Name] = newValue;
                        }
                    }
                    catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(TValue), jsonProperty.Value.Clone(), e))
                    {
                        // suppressed
                    }
                }
            }
        }

        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string key = reader.GetString()!;
                    reader.Skip();

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        if (NullRemovesItems)
                        {
                            Remove(key);
                        }
                        else
                        {
                            this[key] = default;
                        }
                    }
                    else if (TryGetValue(key, out TValue? item))
                    {
                        if (item is null)
                        {
                            if (reader.TokenType != JsonTokenType.Null)
                            {
                                item = new();
                                item.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                                this[key] = item;
                            }
                        }
                        else
                        {
                            item.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                        }
                    }
                    else
                    {
                        TValue newItem = new();
                        newItem.UpdateFromJsonReader(ref reader, ignoreSbcProperties);
                        Add(key, newItem);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Converter factory class for <see cref="StaticModelDictionary{TValue}"/> types
    /// </summary>
    public sealed class StaticModelDictionaryConverter : JsonConverterFactory
    {
        /// <summary>
        /// Checks if the given type can be converted from or to JSON
        /// </summary>
        /// <param name="typeToConvert"></param>
        /// <returns></returns>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsGenericType && typeof(StaticModelDictionary<>) == typeToConvert.GetGenericTypeDefinition();
        }

        /// <summary>
        /// Creates a converter for the given type
        /// </summary>
        /// <param name="type">Target type</param>
        /// <param name="options">Conversion options</param>
        /// <returns>Converter instance</returns>
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
        {
            Type itemType = type.GetGenericArguments().First();
            Type converterType = typeof(ModelDictionaryConverterInner<,>).MakeGenericType(type, itemType);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }

        /// <summary>
        /// Method to create a converter for a specific <see cref="StaticModelDictionary{TValue}"/> type
        /// </summary>
        /// <typeparam name="T">Dictionary type</typeparam>
        /// <typeparam name="TValue">Value type</typeparam>
        private sealed class ModelDictionaryConverterInner<T, TValue> : JsonConverter<T> where T : IDictionary<string, TValue>
        {
            /// <summary>
            /// Checks if the given type can be converted
            /// </summary>
            /// <param name="typeToConvert">Type to convert</param>
            /// <returns>Whether the type can be converted</returns>
            public override bool CanConvert(Type typeToConvert)
            {
                return typeToConvert.IsGenericType && typeof(StaticModelDictionary<>) == typeToConvert.GetGenericTypeDefinition();
            }

            /// <summary>
            /// Read from JSON
            /// </summary>
            /// <param name="reader">JSON reader</param>
            /// <param name="typeToConvert">Type to convert</param>
            /// <param name="options">Read options</param>
            /// <returns>Read value</returns>
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                // We don't have the information about the nullRemovesItems flag here
                throw new NotSupportedException();
            }

            /// <summary>
            /// Write a CodeParameter to JSON
            /// </summary>
            /// <param name="writer">JSON writer</param>
            /// <param name="value">Value to serialize</param>
            /// <param name="options">Write options</param>
            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                foreach (var kv in value)
                {
                    writer.WritePropertyName(kv.Key);
                    JsonSerializer.Serialize(writer, kv.Value, options);
                }
                writer.WriteEndObject();
            }
        }
    }
}
