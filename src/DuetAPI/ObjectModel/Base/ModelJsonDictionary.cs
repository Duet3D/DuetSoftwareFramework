using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Class for holding string keys and JSON values
    /// </summary>
    /// <remarks>
    /// Single properties may not be deleted again from this class
    /// </remarks>
    public sealed class ModelJsonDictionary : INotifyPropertyChanging, INotifyPropertyChanged, IDictionary<string, JsonElement>
    {
        /// <summary>
        /// Internal storage for key/value pairs
        /// </summary>
        private readonly Dictionary<string, JsonElement> _dictionary = new();

        /// <summary>
        /// Index operator
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        public JsonElement this[string key]
        {
            get => _dictionary[key];
            set
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(key));
                _dictionary[key] = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
            }
        }

        /// <summary>
        /// List of keys
        /// </summary>
        public ICollection<string> Keys => _dictionary.Keys;

        /// <summary>
        /// List of values
        /// </summary>
        public ICollection<JsonElement> Values => _dictionary.Values;

        /// <summary>
        /// Number of added items
        /// </summary>
        public int Count => _dictionary.Count;

        /// <summary>
        /// Whether the dictionary is read-only
        /// </summary>
        public bool IsReadOnly => false;

        /// <summary>
        /// Event to call when a value is set
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Event to call when a value is being changed
        /// </summary>
        public event PropertyChangingEventHandler PropertyChanging;

        /// <summary>
        /// Add a new item
        /// </summary>
        /// <param name="key">Key to add</param>
        /// <param name="value">Value to add</param>
        public void Add(string key, JsonElement value)
        {
            PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(key));
            _dictionary.Add(key, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
        }

        /// <summary>
        /// Add a new key-value pair
        /// </summary>
        /// <param name="item">Item to add</param>
        public void Add(KeyValuePair<string, JsonElement> item)
        {
            PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(item.Key));
            _dictionary.Add(item.Key, item.Value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(item.Key));
        }

        /// <summary>
        /// Clear the items
        /// </summary>
        public void Clear() => _dictionary.Clear();

        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns></returns>
        public object Clone()
        {
            ModelJsonDictionary clone = new();
            foreach (KeyValuePair<string, JsonElement> kv in _dictionary)
            {
                clone.Add(kv.Key, kv.Value.Clone());
            }
            return clone;
        }

        /// <summary>
        /// Check if a key-value pair is present
        /// </summary>
        /// <param name="item"></param>
        /// <returns>Whether the key-value pair is present</returns>
        public bool Contains(KeyValuePair<string, JsonElement> item) => _dictionary.Any(kv => kv.Equals(item));

        /// <summary>
        /// Check if a key is present
        /// </summary>
        /// <param name="key">Key to check</param>
        /// <returns>Whether the key is present</returns>
        public bool ContainsKey(string key) => _dictionary.ContainsKey(key);

        /// <summary>
        /// Copy the collection to another array
        /// </summary>
        /// <param name="array">Destination array</param>
        /// <param name="arrayIndex">Start index</param>
        public void CopyTo(KeyValuePair<string, JsonElement>[] array, int arrayIndex) => throw new NotSupportedException();

        /// <summary>
        /// Get an enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => _dictionary.GetEnumerator();

        /// <summary>
        /// Remove a key
        /// </summary>
        /// <param name="key">Key to remove</param>
        /// <returns>Whether the key could be found</returns>
        public bool Remove(string key) => throw new NotSupportedException();

        /// <summary>
        /// Remove a key-value pair
        /// </summary>
        /// <param name="item">Key-value pair</param>
        /// <returns>Whether the key-value pair could be found</returns>
        public bool Remove(KeyValuePair<string, JsonElement> item) => throw new NotSupportedException();

        /// <summary>
        /// Try to get a value
        /// </summary>
        /// <param name="key">Key to look up</param>
        /// <param name="value">Retrieved value</param>
        /// <returns>Whether the key could be found</returns>
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out JsonElement value) => _dictionary.TryGetValue(key, out value);

        /// <summary>
        /// Get an enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <returns>Updated instance</returns>
        public void UpdateFromJson(JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
            {
                Clear();
            }
            else
            {
                foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
                {
                    if (!TryGetValue(jsonProperty.Name, out JsonElement value) || !value.Equals(jsonProperty.Value))
                    {
                        this[jsonProperty.Name] = jsonProperty.Value.Clone();
                    }
                }
            }
        }
    }
}
