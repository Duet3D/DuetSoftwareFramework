using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Base class for machine model properties
    /// </summary>
    public class ModelObject : ICloneable, INotifyPropertyChanging, INotifyPropertyChanged
    {
        /// <summary>
        /// Cached dictionary of derived types vs JSON property names vs property descriptors
        /// </summary>
        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> _propertyInfos = new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        /// <summary>
        /// Get the cached JSON properties of this type
        /// </summary>
        /// <returns>Properties of this type</returns>
        public readonly Dictionary<string, PropertyInfo> JsonProperties;

        /// <summary>
        /// Default constructor to be called from derived classes
        /// </summary>
        public ModelObject()
        {
            lock (_propertyInfos)
            {
                Type type = GetType();
                if (!_propertyInfos.TryGetValue(type, out JsonProperties))
                {
                    JsonProperties = new Dictionary<string, PropertyInfo>();
                    foreach (PropertyInfo property in type.GetProperties())
                    {
                        if (!Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                        {
                            if (Attribute.IsDefined(property, typeof(JsonPropertyNameAttribute)))
                            {
                                JsonPropertyNameAttribute attribute = (JsonPropertyNameAttribute)Attribute.GetCustomAttribute(property, typeof(JsonPropertyNameAttribute));
                                JsonProperties.Add(attribute.Name, property);
                            }
                            else
                            {
                                string jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                                JsonProperties.Add(jsonName, property);
                            }
                        }
                    }
                    _propertyInfos.Add(type, JsonProperties);
                }
            }
        }

        /// <summary>
        /// Event that is triggered when a property is being changed
        /// </summary>
        public event PropertyChangingEventHandler PropertyChanging;

        /// <summary>
        /// Event that is triggered when a property has been changed
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Method to update a property value internally
        /// </summary>
        /// <param name="propertyStorage">Reference to the variable that holds the current value</param>
        /// <param name="value">New property value</param>
        /// <param name="propertyName">Name of the property</param>
        protected void SetPropertyValue<T>(ref T propertyStorage, T value, [CallerMemberName] string propertyName = "")
        {
            if (!Equals(propertyStorage, value))
            {
                PropertyChanging?.Invoke(this, new PropertyChangingEventArgs(propertyName));
                propertyStorage = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>
        /// Method to assign the value of one type to another
        /// </summary>
        /// <param name="from">Other object</param>
        public void Assign(ModelObject from)
        {
            // Assigning null values is not supported
            if (from == null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            // Validate the types
            Type myType = GetType();
            if (from.GetType() != myType)
            {
                throw new ArgumentException("Types do not match", nameof(from));
            }

            // Assign property values
            foreach (PropertyInfo property in GetType().GetProperties())
            {
                if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    ModelObject oldValue = (ModelObject)property.GetValue(this);
                    ModelObject newValue = (ModelObject)property.GetValue(from);
                    if (oldValue == null || newValue == null)
                    {
                        property.SetValue(this, newValue);
                    }
                    else if (oldValue.GetType() != newValue.GetType())
                    {
                        property.SetValue(this, newValue.Clone());
                    }
                    else
                    {
                        oldValue.Assign(newValue);
                    }
                }
                else if (ModelCollection.GetItemType(property.PropertyType, out Type itemType))
                {
                    IList oldModelCollection = (IList)property.GetValue(this);
                    IList newModelCollection = (IList)property.GetValue(from);
                    if (ModelGrowingCollection.TypeMatches(property.PropertyType))
                    {
                        ModelGrowingCollectionHelper.Assign(oldModelCollection, newModelCollection);
                    }
                    else
                    {
                        ModelCollectionHelper.Assign(oldModelCollection, itemType, newModelCollection);
                    }
                }
                else
                {
                    object newValue = property.GetValue(from);
                    property.SetValue(this, newValue);
                }
            }
        }

        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns>Cloned object</returns>
        public object Clone()
        {
            // Make a new clone
            Type myType = GetType();
            ModelObject clone = (ModelObject)Activator.CreateInstance(myType);

            // Assign cloned property values
            foreach (PropertyInfo property in GetType().GetProperties())
            {
                if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    ModelObject value = (ModelObject)property.GetValue(this);
                    ModelObject cloneValue = (ModelObject)property.GetValue(clone);
                    if (value == null || cloneValue == null)
                    {
                        property.SetValue(clone, value);
                    }
                    else if (value.GetType() != cloneValue.GetType())
                    {
                        property.SetValue(clone, value.Clone());
                    }
                    else
                    {
                        cloneValue.Assign(value);
                    }
                }
                else if (ModelCollection.GetItemType(property.PropertyType, out Type itemType))
                {
                    IList collection = (IList)property.GetValue(this);
                    IList clonedCollection = (IList)property.GetValue(clone);
                    if (ModelGrowingCollection.TypeMatches(property.PropertyType))
                    {
                        ModelGrowingCollectionHelper.Assign(clonedCollection, collection);
                    }
                    else
                    {
                        ModelCollectionHelper.Assign(clonedCollection, itemType, collection);
                    }
                }
                else if (property.PropertyType.IsAssignableFrom(typeof(ICloneable)))
                {
                    ICloneable value = (ICloneable)property.GetValue(this);
                    property.SetValue(clone, value?.Clone());
                }
                else
                {
                    object value = property.GetValue(this);
                    property.SetValue(clone, value);
                }
            }

            return clone;
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <returns>Updated instance</returns>
        public ModelObject UpdateFromJson(JsonElement jsonElement) => UpdateFromJson(jsonElement, false);

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        internal virtual ModelObject UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {
                if (JsonProperties.TryGetValue(jsonProperty.Name, out PropertyInfo property))
                {
                    if (ignoreSbcProperties && Attribute.IsDefined(property, typeof(LinuxPropertyAttribute)))
                    {
                        // Skip this field if it must not be updated
                        continue;
                    }

                    if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                    {
                        ModelObject modelObject = (ModelObject)property.GetValue(this);
                        if (jsonProperty.Value.ValueKind == JsonValueKind.Null)
                        {
                            if (modelObject != null)
                            {
                                if (property.SetMethod != null)
                                {
                                    property.SetValue(this, null);
                                }
#if VERIFY_OBJECT_MODEL
                                else
                                {
                                    Console.WriteLine("[warn] Tried to set unsettable property {0} to null", jsonProperty.Name);
                                }
#endif
                            }
                        }
                        else if (modelObject == null)
                        {
                            modelObject = (ModelObject)Activator.CreateInstance(property.PropertyType);
                            modelObject = modelObject.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                            if (property.SetMethod != null)
                            {
                                property.SetValue(this, modelObject);
                            }
#if VERIFY_OBJECT_MODEL
                            else
                            {
                                Console.WriteLine("[warn] Tried to assign unsettable property {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
                            }
#endif
                        }
                        else
                        {
                            ModelObject updatedInstance = modelObject.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                            if (updatedInstance != modelObject)
                            {
                                if (property.SetMethod != null)
                                {
                                    property.SetValue(this, updatedInstance);
                                }
#if VERIFY_OBJECT_MODEL
                                else
                                {
                                    Console.WriteLine("[warn] Tried to assign unsettable property {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
                                }
#endif
                            }
                        }
                    }
                    else if (ModelCollection.GetItemType(property.PropertyType, out Type itemType))
                    {
                        IList modelCollection = (IList)property.GetValue(this);
                        if (ModelGrowingCollection.TypeMatches(property.PropertyType))
                        {
                            ModelGrowingCollectionHelper.UpdateFromJson(modelCollection, itemType, jsonProperty.Value, ignoreSbcProperties);
                        }
                        else
                        {
                            ModelCollectionHelper.UpdateFromJson(modelCollection, itemType, jsonProperty.Value, ignoreSbcProperties);
                        }
                    }
                    else if (property.PropertyType == typeof(bool) && jsonProperty.Value.ValueKind == JsonValueKind.Number)
                    {
                        try
                        {
                            if (property.SetMethod != null)
                            {
                                property.SetValue(this, Convert.ToBoolean(jsonProperty.Value.GetInt32()));
#if VERIFY_OBJECT_MODEL
                                Console.WriteLine("[warn] Updating bool value from number {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
#endif
                            }
#if VERIFY_OBJECT_MODEL
                            else
                            {
                                Console.WriteLine("[warn] Tried to assign unsettable property {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
                            }
#endif
                        }
                        catch (FormatException e)
                        {
                            throw new JsonException($"Failed to deserialize property [{GetType().Name}].{property.Name} (type bool) from JSON {jsonProperty.Value.GetRawText()}", e);
                        }
                    }
                    else
                    {
                        try
                        {
                            object newValue = JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), property.PropertyType);
                            if (property.SetMethod != null)
                            {
                                property.SetValue(this, newValue);

                            }
#if VERIFY_OBJECT_MODEL
                            else
                            {
                                Console.WriteLine("[warn] Tried to assign unsettable property {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
                            }
#endif
                        }
                        catch (JsonException e)
                        {
                            throw new JsonException($"Failed to deserialize property [{GetType().Name}].{property.Name} (type {property.PropertyType.Name}) from JSON {jsonProperty.Value.GetRawText()}", e);
                        }
                    }
                }
#if VERIFY_OBJECT_MODEL
                else if (jsonProperty.Name != "seqs")
                {
                    Console.WriteLine("[warn] Missing property: {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
                }
#endif
            }
            return this;
        }

        /// <summary>
        /// Create a UTF8-encoded JSON patch to bring an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>JSON patch</returns>
        public byte[] MakeUtf8Patch(ModelObject old)
        {
            object diffs = MakePatch(old);
            return JsonSerializer.SerializeToUtf8Bytes(diffs, Utility.JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Create a string-encoded JSON patch to bring an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>JSON patch</returns>
        public string MakeStringPatch(ModelObject old)
        {
            object diffs = MakePatch(old);
            return JsonSerializer.Serialize(diffs, Utility.JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Create a patch to update an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>Differences between this and other or null if they are equal</returns>
        internal object MakePatch(ModelObject old)
        {
            // Need a valid other instance
            if (old == null)
            {
                throw new ArgumentNullException(nameof(old));
            }

            // Check the types
            Type myType = GetType(), otherType = GetType();
            if (myType != otherType)
            {
                // Types differ. Serialize every property of the other instance
                return old;
            }

            // Look for differences
            Dictionary<string, object> diffs = null;
            foreach (KeyValuePair<string, PropertyInfo> jsonProperty in JsonProperties)
            {
                object oldValue = jsonProperty.Value.GetValue(old), newValue = jsonProperty.Value.GetValue(this);
                if (oldValue == null || newValue == null || oldValue.GetType() != newValue.GetType())
                {
                    if (oldValue != newValue)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }
                        diffs.Add(jsonProperty.Key, newValue);
                    }
                }
                else if (jsonProperty.Value.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    object diff = ((ModelObject)newValue).MakePatch((ModelObject)oldValue);
                    if (diff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }
                        diffs.Add(jsonProperty.Key, diff);
                    }
                }
                else if (ModelCollection.GetItemType(jsonProperty.Value.PropertyType, out Type itemType))
                {
                    object listDiff = ModelCollectionHelper.FindDiffs((IList)oldValue, (IList)newValue, itemType);
                    if (listDiff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }
                        diffs.Add(jsonProperty.Key, listDiff);
                    }
                }
                else if (!newValue.Equals(oldValue))
                {
                    if (diffs == null)
                    {
                        diffs = new Dictionary<string, object>();
                    }
                    diffs.Add(jsonProperty.Key, newValue);
                }
            }
            return diffs;
        }
    }
}
