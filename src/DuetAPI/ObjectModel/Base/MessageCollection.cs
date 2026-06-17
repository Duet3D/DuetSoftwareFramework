using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Generic list container to which messages can only be added or cleared
/// </summary>
public class MessageCollection : ObservableCollection<Message>, IModelCollection
{
    /// <inheritdoc />
    protected override void ClearItems()
    {
        List<Message> removed = new(this);
        base.ClearItems();
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed));
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Assign(IStaticModelObject from)
    {
        // Validate the types
        if (from is not MessageCollection other)
        {
            throw new ArgumentException("Types do not match", nameof(from));
        }

        // Clear existing items
        ClearItems();

        // Add other items
        foreach (Message item in other)
        {
            Add((Message)item.Clone());
        }
    }

    /// <inheritdoc />
    public object Clone()
    {
        MessageCollection clone = [];
        foreach (Message item in this)
        {
            clone.Add((Message)item.Clone());
        }
        return clone;
    }

    /// <inheritdoc />
    public void UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
    {
        foreach (JsonElement item in jsonElement.EnumerateArray())
        {
            try
            {
                Add(JsonSerializer.Deserialize(item, ObjectModelContext.Default.Message)!);
            }
            catch (Exception e) when (ObjectModel.DeserializationFailed(this, typeof(Message), item, e))
            {
                // suppressed
            }
        }
    }

    /// <inheritdoc />
    public void UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties, int offset = 0, bool last = true) => UpdateFromJson(jsonElement, ignoreSbcProperties);

    /// <inheritdoc />
    public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties, int offset = 0, bool last = true)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("expected start of array");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            // Save the reader state in case this item fails to deserialize
            Utf8JsonReader itemStart = reader;
            try
            {
                Add(JsonSerializer.Deserialize(ref reader, ObjectModelContext.Default.Message)!);
            }
            catch (Exception e) when (ObjectModel.DeserializationFailed(this, typeof(Message), JsonElement.ParseValue(ref itemStart), e))
            {
                // Resume after the failed item, ParseValue has already advanced the saved reader past it
                reader = itemStart;
            }
        }
    }

    /// <inheritdoc />
    public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties) => UpdateFromJsonReader(ref reader, ignoreSbcProperties, 0, true);
}
