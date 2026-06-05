using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel;

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

    /// <inheritdoc />
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

    /// <inheritdoc />
    [AllowNull]
    [MaybeNull]
    public object this[object key]
    {
        get => this[(string)key];
        set => this[(string)key] = (JsonElement?)value;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, JsonElement?>> GetEnumerator() => _dictionary.GetEnumerator();

    /// <inheritdoc />
    public ICollection<string> Keys => _dictionary.Keys;

    /// <inheritdoc />
    public ICollection<JsonElement?> Values => _dictionary.Values;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsFixedSize => false;

    /// <inheritdoc />
    ICollection IDictionary.Keys => _dictionary.Keys;

    /// <inheritdoc />
    ICollection IDictionary.Values => _dictionary.Values;

    /// <inheritdoc />
    public bool IsSynchronized => false;

    /// <inheritdoc />
    public object SyncRoot => _dictionary;

    /// <inheritdoc />
    public int Count => _dictionary.Count;

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Add(object key, object? value) => Add((string)key, (JsonElement?)value);

    /// <inheritdoc />
    public void Add(KeyValuePair<string, JsonElement?> item) => Add(item.Key, item.Value);

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public object Clone()
    {
        JsonModelDictionary clone = new(NullRemovesItems);
        foreach (KeyValuePair<string, JsonElement?> kv in _dictionary)
        {
            clone.Add(kv);
        }
        return clone;
    }

    /// <inheritdoc />
    public bool ContainsKey(string key) => _dictionary.ContainsKey(key);

    /// <inheritdoc />
    public bool Contains(object key) => ContainsKey((string)key);

    /// <inheritdoc />
    public void CopyTo(Array array, int index)
    {
        List<string> keys = new(_dictionary.Keys);
        for (int i = 0; i < Count; i++)
        {
            string key = keys[i];
            array.SetValue(new KeyValuePair<string, JsonElement?>(key, _dictionary[key]), i + index);
        }
    }

    /// <inheritdoc />
    public void CopyTo(KeyValuePair<string, JsonElement?>[] array, int arrayIndex) => CopyTo((Array)array, arrayIndex);

    /// <inheritdoc />
    public bool Contains(KeyValuePair<string, JsonElement?> item) => _dictionary.TryGetValue(item.Key, out JsonElement? value) && Equals(value, item.Value);

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();

    /// <inheritdoc />
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

    /// <inheritdoc />
    public bool Remove(KeyValuePair<string, JsonElement?> item) => Contains(item) && Remove(item.Key);

    /// <inheritdoc />
    public bool TryGetValue(string key, [NotNullWhen(true)] out JsonElement? value) => _dictionary.TryGetValue(key, out value);

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

    /// <inheritdoc />
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
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(JsonModelDictionary);
    }

    /// <inheritdoc />
    public override JsonModelDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // We don't have the information about the nullRemovesItems flag here
        throw new NotSupportedException();
    }

    /// <inheritdoc />
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
