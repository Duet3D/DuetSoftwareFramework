using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Generic list container to which messages can only be added or cleared
    /// </summary>
    public class MessageCollection : ObservableCollection<Message>, IModelCollection
    {
        /// <summary>
        /// Removes all items from the collection
        /// </summary>
        protected override void ClearItems()
        {
            List<Message> removed = new(this);
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

        /// <summary>
        /// Create a clone of this list
        /// </summary>
        /// <returns>Cloned list</returns>
        public object Clone()
        {
            MessageCollection clone = [];
            foreach (Message item in this)
            {
                clone.Add((Message)item.Clone());
            }
            return clone;
        }

        private static readonly MessageContext _messageContext = new(Utility.JsonHelper.DefaultJsonOptions);

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public void UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            foreach (JsonElement item in jsonElement.EnumerateArray())
            {
                try
                {
                    Add((Message)JsonSerializer.Deserialize(item, typeof(Message), _messageContext)!);
                }
                catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(Message), item, e))
                {
                    // suppressed
                }
            }
        }

        /// <summary>
        /// Update this collection from a given JSON array
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <param name="offset">Index offset</param>
        /// <param name="last">Whether this is the last update</param>
        public void UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties, int offset = 0, bool last = true) => UpdateFromJson(jsonElement, ignoreSbcProperties);

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

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                try
                {
                    Add((Message)JsonSerializer.Deserialize(ref reader, typeof(Message), _messageContext)!);
                }
                catch (JsonException e) when (ObjectModel.DeserializationFailed(this, typeof(Message), JsonElement.ParseValue(ref reader), e))
                {
                    // suppressed
                }
            }
        }

        /// <summary>
        /// Update this instance from a given JSON reader
        /// </summary>
        /// <param name="reader">JSON reader</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public void UpdateFromJsonReader(ref Utf8JsonReader reader, bool ignoreSbcProperties) => UpdateFromJsonReader(ref reader, ignoreSbcProperties, 0, true);
    }
}
