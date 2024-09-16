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
    [JsonConverter(typeof(JsonModelDictionaryConverter))]
    public sealed class JsonModelDictionary(bool nullRemovesItems) : IDictionary<string, JsonElement?>, IModelDictionary
    {
        /// <summary>
        /// Flags if keys can be removed again by setting their value to null
        /// </summary>
        [JsonIgnore]
        public bool NullRemovesItems { get; } = nullRemovesItems;

        /// <summary>
        /// Internal storage for key/value pairs
        /// </summary>
        private readonly Dictionary<string, JsonElement?> _dictionary = [];

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
        private JsonElement? GetValue(string key)
        {
            if (NullRemovesItems)
            {
                return _dictionary.TryGetValue(key, out JsonElement? result) ? result : default;
            }
            return _dictionary[key];
        }

        /// <summary>
        /// Index operator
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        [AllowNull]
        public JsonElement? this[string key]
        {
            get => GetValue(key);
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
            set => this[(string)key] = (JsonElement?)value;
        }

        /// <summary>
        /// Get an enumerator for this instance
        /// </summary>
        /// <returns>Enumerator instance</returns>
        public IEnumerator<KeyValuePair<string, JsonElement?>> GetEnumerator() => _dictionary.GetEnumerator();

        /// <summary>
        /// List of keys
        /// </summary>
        public ICollection<string> Keys => _dictionary.Keys;

        /// <summary>
        /// List of values
        /// </summary>
        public ICollection<JsonElement?> Values => _dictionary.Values;

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
        public void Add(string key, JsonElement? value)
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
        public void Add(object key, object? value) => Add((string)key, (JsonElement?)value);

        /// <summary>
        /// Add a new item
        /// </summary>
        /// <param name="item">Item to add</param>
        public void Add(KeyValuePair<string, JsonElement?> item) => Add(item.Key, item.Value);

        /// <summary>
        /// Assign the properties from another instance
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(IStaticModelObject from)
        {
            // Validate the types
            if (from is not JsonModelDictionary other)
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
                if (TryGetValue(kv.Key, out JsonElement? existingItem))
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
                        this[kv.Key] = kv.Value;
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
            JsonModelDictionary clone = new(NullRemovesItems);
            foreach (KeyValuePair<string, JsonElement?> kv in _dictionary)
            {
                clone.Add(kv);
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
                array.SetValue(new KeyValuePair<string, JsonElement?>(key, _dictionary[key]), i + index);
            }
        }

        /// <summary>
        /// Copy this instance to another dictionary
        /// </summary>
        /// <param name="array">Destination array</param>
        /// <param name="arrayIndex">Start iondex</param>
        public void CopyTo(KeyValuePair<string, JsonElement?>[] array, int arrayIndex) => CopyTo(array, arrayIndex);

        /// <summary>
        /// Check if a key-value pair exists
        /// </summary>
        /// <param name="item">Item to check</param>
        /// <returns>If the item exists in the dictionary</returns>
        public bool Contains(KeyValuePair<string, JsonElement?> item) => _dictionary.TryGetValue(item.Key, out JsonElement? value) && Equals(value, item.Value);

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
        public bool Remove(KeyValuePair<string, JsonElement?> item) => Contains(item) && Remove(item.Key);

        /// <summary>
        /// Try to get a value
        /// </summary>
        /// <param name="key">Key to look up</param>
        /// <param name="value">Retrieved value</param>
        /// <returns>Whether the key could be found</returns>
        public bool TryGetValue(string key, out JsonElement? value) => _dictionary.TryGetValue(key, out value);

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
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                Clear();
            }
            else
            {
                foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
                {
                    if (NullRemovesItems && jsonProperty.Value.ValueKind == JsonValueKind.Null)
                    {
                        Remove(jsonProperty.Name);
                    }
                    else if (!TryGetValue(jsonProperty.Name, out JsonElement? value) || !value!.Equals(jsonProperty.Value))
                    {
                        this[jsonProperty.Name] = jsonProperty.Value.Clone();
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
            if (reader.TokenType == JsonTokenType.Null)
            {
                Clear();
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string key = reader.GetString()!;
                        JsonElement value = JsonElement.ParseValue(ref reader);
                        if (NullRemovesItems && value.ValueKind == JsonValueKind.Null)
                        {
                            Remove(key);
                        }
                        else if (!TryGetValue(key, out JsonElement? existingValue) || !existingValue!.Equals(value))
                        {
                            this[key] = value;
                        }
                    }
                }
            }
            else
            {
                throw new JsonException("expected null or start of object");
            }
        }
    }

    /// <summary>
    /// Converter factory class for <see cref="JsonModelDictionary"/> types
    /// </summary>
    public class JsonModelDictionaryConverter : JsonConverter<JsonModelDictionary>
    {
        /// <summary>
        /// Checks if the given type can be converted
        /// </summary>
        /// <param name="typeToConvert">Type to convert</param>
        /// <returns>Whether the type can be converted</returns>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert == typeof(JsonModelDictionary);
        }

        /// <summary>
        /// Read from JSON
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="typeToConvert">Type to convert</param>
        /// <param name="options">Read options</param>
        /// <returns>Read value</returns>
        public override JsonModelDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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
        public override void Write(Utf8JsonWriter writer, JsonModelDictionary value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kv in value)
            {
                writer.WritePropertyName(kv.Key);
                if (kv.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteRawValue(kv.Value.Value.GetRawText());
                }
            }
            writer.WriteEndObject();
        }
    }
}
