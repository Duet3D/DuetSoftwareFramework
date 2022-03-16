using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuetAPI.ObjectModel
{
    /// <summary>
    /// Base class for machine model properties
    /// </summary>
    public class ModelObject : IModelObject, INotifyPropertyChanging
    {
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
        /// Cached dictionary of derived types vs JSON property names vs property descriptors
        /// </summary>
        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> _propertyInfos = new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        /// <summary>
        /// Static constructor that caches the JSON properties of each derived type
        /// </summary>
        static ModelObject()
        {
            Type[] assemblyTypes = Assembly.GetExecutingAssembly().GetTypes();
            foreach (Type type in assemblyTypes)
            {
                if (!type.IsGenericType && type.IsSubclassOf(typeof(ModelObject)))
                {
                    RegisterJsonType(type);
                }
            }
        }

        /// <summary>
        /// Function to add custom JSON types. This must be invoked from types with generic type arguments
        /// </summary>
        /// <param name="type">Type to register</param>
        static protected void RegisterJsonType(Type type)
        {
            Dictionary<string, PropertyInfo> jsonProperties = new Dictionary<string, PropertyInfo>();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                {
                    if (Attribute.IsDefined(property, typeof(JsonPropertyNameAttribute)))
                    {
                        JsonPropertyNameAttribute attribute = (JsonPropertyNameAttribute)Attribute.GetCustomAttribute(property, typeof(JsonPropertyNameAttribute));
                        jsonProperties.Add(attribute.Name, property);
                    }
                    else
                    {
                        string jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        jsonProperties.Add(jsonName, property);
                    }
                }
            }
            _propertyInfos.Add(type, jsonProperties);
        }

        /// <summary>
        /// Get the cached JSON properties of this type
        /// </summary>
        /// <returns>Properties of this type</returns>
        [JsonIgnore]
        public Dictionary<string, PropertyInfo> JsonProperties => _propertyInfos[GetType()];

        /// <summary>
        /// Assign the properties from another instance
        /// </summary>
        /// <param name="from">Other instance</param>
        public void Assign(object from)
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
            IEnumerable<PropertyInfo> properties = _propertyInfos[myType].Values;
            foreach (PropertyInfo property in properties)
            {
                if (typeof(IModelObject).IsAssignableFrom(property.PropertyType))
                {
                    IModelObject myValue = (IModelObject)property.GetValue(this);
                    IModelObject otherValue = (IModelObject)property.GetValue(from);
                    if (property.SetMethod != null)
                    {
                        property.SetValue(this, otherValue?.Clone());
                    }
                    else if (myValue != null && otherValue != null)
                    {
                        myValue.Assign(otherValue);
                    }
                }
                else if (property.SetMethod != null)
                {
                    property.SetValue(this, property.GetValue(from));
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
            IEnumerable<PropertyInfo> properties = _propertyInfos[myType].Values;
            foreach (PropertyInfo property in properties)
            {
                if (typeof(IModelObject).IsAssignableFrom(property.PropertyType))
                {
                    IModelObject myValue = (IModelObject)property.GetValue(this);
                    if (property.SetMethod != null)
                    {
                        property.SetValue(clone, myValue?.Clone());
                    }
                    else
                    {
                        IModelObject clonedValue = (IModelObject)property.GetValue(clone);
                        if (myValue != null && clonedValue != null)
                        {
                            clonedValue.Assign(myValue);
                        }
                    }
                }
                else if (property.SetMethod != null)
                {
                    if (typeof(ICloneable).IsAssignableFrom(property.PropertyType))
                    {
                        ICloneable myValue = (ICloneable)property.GetValue(this);
                        property.SetValue(clone, myValue?.Clone());
                    }
                    else
                    {
                        property.SetValue(clone, property.GetValue(this));
                    }
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
        public object FindDifferences(IModelObject other)
        {
            // Check the types
            Type myType = GetType(), otherType = other?.GetType();
            if (myType != otherType)
            {
                // Types differ, return the entire instance
                return this;
            }

            // Look for differences
            Dictionary<string, object> diffs = null;
            var properties = _propertyInfos[myType];
            foreach (var jsonProperty in properties)
            {
                object myValue = jsonProperty.Value.GetValue(this);
                object otherValue = jsonProperty.Value.GetValue(other);
                if (otherValue == null || myValue == null || otherValue.GetType() != myValue.GetType())
                {
                    if (otherValue != myValue)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }
                        diffs.Add(jsonProperty.Key, myValue);
                    }
                }
                else if (typeof(IModelObject).IsAssignableFrom(jsonProperty.Value.PropertyType))
                {
                    object diff = ((IModelObject)myValue).FindDifferences((IModelObject)otherValue);
                    if (diff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }
                        diffs.Add(jsonProperty.Key, diff);
                    }
                }
                else if (!myValue.Equals(otherValue))
                {
                    if (diffs == null)
                    {
                        diffs = new Dictionary<string, object>();
                    }
                    diffs.Add(jsonProperty.Key, myValue);
                }
            }
            return diffs;
        }

        /// <summary>
        /// Create a UTF8-encoded JSON patch to bring an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>JSON patch</returns>
        public byte[] MakeUtf8Patch(ModelObject old)
        {
            object diffs = FindDifferences(old);
            return JsonSerializer.SerializeToUtf8Bytes(diffs, Utility.JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Create a string-encoded JSON patch to bring an old instance to this state
        /// </summary>
        /// <param name="old">Old object state</param>
        /// <returns>JSON patch</returns>
        public string MakeStringPatch(ModelObject old)
        {
            object diffs = FindDifferences(old);
            return JsonSerializer.Serialize(diffs, Utility.JsonHelper.DefaultJsonOptions);
        }

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        /// <exception cref="JsonException">Failed to deserialize data</exception>
        public virtual IModelObject UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            Dictionary<string, PropertyInfo> properties = JsonProperties;
            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {
                if (properties.TryGetValue(jsonProperty.Name, out PropertyInfo property))
                {
                    if (ignoreSbcProperties && Attribute.IsDefined(property, typeof(SbcPropertyAttribute)))
                    {
                        // Skip this field if it must not be updated
                        continue;
                    }

                    if (jsonProperty.Value.ValueKind == JsonValueKind.Null)
                    {
                        if (property.SetMethod != null)
                        {
                            property.SetValue(this, null);
                        }
                        else if (typeof(IModelObject).IsAssignableFrom(property.PropertyType))
                        {
                            IModelObject propertyValue = (IModelObject)property.GetValue(this);
                            propertyValue.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                        }
#if VERIFY_OBJECT_MODEL
                        else
                        {
                            Console.WriteLine("[warn] Tried to set unsettable property {0} to null", jsonProperty.Name);
                        }
#endif
                    }
                    else if (typeof(IModelObject).IsAssignableFrom(property.PropertyType))
                    {
                        object propertyValue = property.GetValue(this), newPropertyValue = propertyValue;
                        if (propertyValue == null)
                        {
                            newPropertyValue = Activator.CreateInstance(property.PropertyType);
                        }
                        newPropertyValue = (newPropertyValue as IModelObject).UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                        if (propertyValue != newPropertyValue)
                        {
                            if (property.SetMethod != null)
                            {
                                property.SetValue(this, newPropertyValue);
                            }
#if VERIFY_OBJECT_MODEL
                            else
                            {
                                Console.WriteLine("[warn] Tried to assign unsettable property {0} = {1}", jsonProperty.Name, jsonProperty.Value.GetRawText());
                            }
#endif
                        }
                    }
                    else
                    {
                        try
                        {
                            object deserializedValue = JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), property.PropertyType);
                            if (property.SetMethod != null)
                            {
                                property.SetValue(this, deserializedValue);

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
    }
}
