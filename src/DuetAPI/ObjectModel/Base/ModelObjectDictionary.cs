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
    /// Class for holding model object instances using a string-type key. Values may be deleted again by assigning null to them
    /// </summary>
    /// <remarks>
    /// When this class is (de-)serialized, the key names are kept and they are not converted based on the property naming scheme.
    /// This rule does not apply to child items though.
    /// 
    /// In a future version this should implement Assign() and UpdateFromJson() like the other classes.
    /// </remarks>
    /// <typeparam name="TValue">Value type</typeparam>
    [JsonConverter(typeof(MutableModelDictionaryConverter))]
    public sealed class ModelObjectDictionary<TValue> : INotifyPropertyChanging, INotifyPropertyChanged, IDictionary<string, TValue> where TValue : ModelObject
    {
        /// <summary>
        /// Internal storage for key/value pairs
        /// </summary>
        private readonly Dictionary<string, TValue> _dictionary = new();

        /// <summary>
        /// Index operator
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        public TValue this[string key]
        {
            get
            {
                if (_dictionary.TryGetValue(key, out TValue item))
                {
                    return item;
                }
                return default;
            }

            set
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(key));
                if (value == null)
                {
                    _dictionary.Remove(key);
                }
                else
                {
                    _dictionary[key] = value;
                }
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
        public ICollection<TValue> Values => _dictionary.Values;

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
        /// <exception cref="ArgumentNullException">Value is null when deleteNullValues is set to true</exception>
        public void Add(string key, TValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(key));
            _dictionary.Add(key, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
        }

        /// <summary>
        /// Add a new key-value pair
        /// </summary>
        /// <param name="item">Item to add</param>
        /// <exception cref="ArgumentNullException">item.Value is null when deleteNullValues is set to true</exception>
        public void Add(KeyValuePair<string, TValue> item)
        {
            if (item.Value == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(item.Key));
            _dictionary.Add(item.Key, item.Value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(item.Key));
        }

        /// <summary>
        /// Clear the items
        /// </summary>
        public void Clear()
        {
            foreach (string key in _dictionary.Keys.ToList())
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(key));
                _dictionary.Remove(key);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
            }
        }

        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns></returns>
        public object Clone()
        {
            ModelObjectDictionary<TValue> clone = new();
            foreach (KeyValuePair<string, TValue> kv in _dictionary)
            {
                if (kv.Value is ICloneable cloneable)
                {
                    clone.Add(kv.Key, (TValue)cloneable.Clone());
                }
                else
                {
                    clone.Add(kv);
                }
            }
            return clone;
        }

        /// <summary>
        /// Check if a key-value pair is present
        /// </summary>
        /// <param name="item"></param>
        /// <returns>Whether the key-value pair is present</returns>
        public bool Contains(KeyValuePair<string, TValue> item) => _dictionary.Any(kv => kv.Equals(item));

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
        public void CopyTo(KeyValuePair<string, TValue>[] array, int arrayIndex) => throw new NotSupportedException();

        /// <summary>
        /// Get an enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

        /// <summary>
        /// Remove a key
        /// </summary>
        /// <param name="key">Key to remove</param>
        /// <returns>Whether the key could be found</returns>
        public bool Remove(string key)
        {
            if (_dictionary.ContainsKey(key))
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(key));
                _dictionary.Remove(key);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Remove a key-value pair
        /// </summary>
        /// <param name="item">Key-value pair</param>
        /// <returns>Whether the key-value pair could be found</returns>
        public bool Remove(KeyValuePair<string, TValue> item)
        {
            if (_dictionary.Contains(item))
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(item.Key));
                _dictionary.Remove(item.Key);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(item.Key));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Try to get a value
        /// </summary>
        /// <param name="key">Key to look up</param>
        /// <param name="value">Retrieved value</param>
        /// <returns>Whether the key could be found</returns>
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out TValue value) => _dictionary.TryGetValue(key, out value);

        /// <summary>
        /// Get an enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();
    }

    /// <summary>
    /// Converter factory class for <see cref="ModelObjectDictionary{TValue}"/> types
    /// </summary>
    public sealed class MutableModelDictionaryConverter : JsonConverterFactory
    {
        /// <summary>
        /// Checks if the given type can be converted from or to JSON
        /// </summary>
        /// <param name="typeToConvert"></param>
        /// <returns></returns>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsGenericType && typeof(ModelObjectDictionary<>) == typeToConvert.GetGenericTypeDefinition();
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
            Type converterType = typeof(MutableModelDictionaryConverterInner<,>).MakeGenericType(type, itemType);
            return (JsonConverter)Activator.CreateInstance(converterType);
        }

        /// <summary>
        /// Method to create a converter for a specific <see cref="ModelObjectDictionary{TValue}"/> type
        /// </summary>
        /// <typeparam name="T">Dictionary type</typeparam>
        /// <typeparam name="TItem">Item type</typeparam>
        private sealed class MutableModelDictionaryConverterInner<T, TItem> : JsonConverter<T> where T : IDictionary<string, TItem>, new()
        {
            /// <summary>
            /// Checks if the given type can be converted
            /// </summary>
            /// <param name="typeToConvert">Type to convert</param>
            /// <returns>Whether the type can be converted</returns>
            public override bool CanConvert(Type typeToConvert)
            {
                return typeToConvert.IsGenericType && typeof(ModelObjectDictionary<>) == typeToConvert.GetGenericTypeDefinition();
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
                T result = new();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        continue;
                    }
                    else if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string key = reader.GetString();
                        result.Add(key, (TItem)(object)JsonSerializer.Deserialize<TItem>(ref reader, options));
                    }
                    else if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }
                    else
                    {
                        throw new JsonException("Invalid token type");
                    }
                }
                return result;
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
