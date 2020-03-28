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
        /// Cached dictionary of derived types vs properties
        /// </summary>
        private static readonly Dictionary<Type, PropertyInfo[]> _propertyInfos = new Dictionary<Type, PropertyInfo[]>();

        /// <summary>
        /// Default constructor to be called from derived classes
        /// </summary>
        public ModelObject()
        {
            lock (_propertyInfos)
            {
                Type myType = GetType();
                if (!_propertyInfos.ContainsKey(myType))
                {
                    PropertyInfo[] myProperties = myType.GetProperties();
                    _propertyInfos.Add(myType, myProperties);
                }
            }
        }

        /// <summary>
        /// Get the cached properties of this type
        /// </summary>
        /// <returns>Properties of this type</returns>
        public PropertyInfo[] GetProperties() => _propertyInfos[GetType()];

        /// <summary>
        /// Get the value of a given property (case-sensitive!)
        /// </summary>
        /// <param name="propertyName">Name of the property to query</param>
        /// <returns>Property value or null if it does not exist</returns>
        public object GetPropertyValue(string propertyName)
        {
            foreach (PropertyInfo property in _propertyInfos[GetType()])
            {
                if (property.Name == propertyName)
                {
                    return property.GetValue(this);
                }
            }
            return null;
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
            foreach (PropertyInfo property in _propertyInfos[myType])
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
                else if (property.PropertyType.IsGenericType &&
                         property.PropertyType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                {
                    IList oldModelCollection = (IList)property.GetValue(this);
                    IList newModelCollection = (IList)property.GetValue(from);
                    Type itemType = property.PropertyType.GetGenericArguments()[0];
                    ModelCollectionHelper.Assign(oldModelCollection, itemType, newModelCollection);
                }
                else if (property.PropertyType.IsGenericType &&
                         property.PropertyType.GetGenericTypeDefinition() == typeof(ModelGrowingCollection<>))
                {
                    IList oldGrowingModelCollection = (IList)property.GetValue(this);
                    IList newGrowingModelCollection = (IList)property.GetValue(from);
                    ModelGrowingCollectionHelper.Assign(oldGrowingModelCollection, newGrowingModelCollection);
                }
                else if (property.PropertyType.BaseType.IsGenericType &&
                         property.PropertyType.BaseType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                {
                    IList oldModelCollection = (IList)property.GetValue(this);
                    IList newModelCollection = (IList)property.GetValue(from);
                    Type itemType = property.PropertyType.BaseType.GetGenericArguments()[0];
                    ModelCollectionHelper.Assign(oldModelCollection, itemType, newModelCollection);
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
            foreach (PropertyInfo property in _propertyInfos[myType])
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
                else if (property.PropertyType.IsGenericType &&
                         property.PropertyType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                {
                    IList collection = (IList)property.GetValue(this);
                    IList clonedCollection = (IList)property.GetValue(clone);
                    Type itemType = property.PropertyType.GetGenericArguments()[0];
                    ModelCollectionHelper.Assign(clonedCollection, itemType, collection);
                }
                else if (property.PropertyType.IsGenericType &&
                         property.PropertyType.GetGenericTypeDefinition() == typeof(ModelGrowingCollection<>))
                {
                    IList growingCollection = (IList)property.GetValue(this);
                    IList clonedGrowingCollection = (IList)property.GetValue(clone);
                    ModelGrowingCollectionHelper.Assign(growingCollection, clonedGrowingCollection);
                }
                else if (property.PropertyType.BaseType.IsGenericType &&
                         property.PropertyType.BaseType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                {
                    IList collection = (IList)property.GetValue(this);
                    IList clonedCollection = (IList)property.GetValue(clone);
                    Type itemType = property.PropertyType.BaseType.GetGenericArguments()[0];
                    ModelCollectionHelper.Assign(clonedCollection, itemType, collection);
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
        public virtual ModelObject UpdateFromJson(JsonElement jsonElement) => UpdateFromJson(jsonElement, false);

        /// <summary>
        /// Update this instance from a given JSON element
        /// </summary>
        /// <param name="jsonElement">Element to update this intance from</param>
        /// <param name="ignoreSbcProperties">Whether SBC properties are ignored</param>
        /// <returns>Updated instance</returns>
        internal virtual ModelObject UpdateFromJson(JsonElement jsonElement, bool ignoreSbcProperties)
        {
            PropertyInfo[] properties = _propertyInfos[GetType()];
            foreach (JsonProperty jsonProperty in jsonElement.EnumerateObject())
            {
                foreach (PropertyInfo property in properties)
                {
                    if (property.Name.Equals(jsonProperty.Name, StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (ignoreSbcProperties && Attribute.IsDefined(property, typeof(LinuxPropertyAttribute)))
                        {
                            break;
                        }

                        if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                        {
                            ModelObject modelObject = (ModelObject)property.GetValue(this);
                            if (jsonProperty.Value.ValueKind == JsonValueKind.Null)
                            {
                                if (modelObject != null)
                                {
                                    property.SetValue(this, null);
                                }
                            }
                            else if (modelObject == null || property.PropertyType != modelObject.GetType())
                            {
                                modelObject = (ModelObject)Activator.CreateInstance(property.PropertyType);
                                modelObject = modelObject.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                                property.SetValue(this, modelObject);
                            }
                            else
                            {
                                ModelObject updatedInstance = modelObject.UpdateFromJson(jsonProperty.Value, ignoreSbcProperties);
                                if (updatedInstance != modelObject)
                                {
                                    property.SetValue(this, updatedInstance);
                                }
                            }
                        }
                        else if (property.PropertyType.IsGenericType &&
                                 property.PropertyType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                        {
                            IList modelCollection = (IList)property.GetValue(this);
                            Type itemType = property.PropertyType.GetGenericArguments()[0];
                            ModelCollectionHelper.UpdateFromJson(modelCollection, itemType, jsonProperty.Value, ignoreSbcProperties);
                        }
                        else if (property.PropertyType.IsGenericType &&
                                 property.PropertyType.GetGenericTypeDefinition() == typeof(ModelGrowingCollection<>))
                        {
                            IList modelCollection = (IList)property.GetValue(this);
                            Type itemType = property.PropertyType.GetGenericArguments()[0];
                            ModelGrowingCollectionHelper.UpdateFromJson(modelCollection, itemType, jsonProperty.Value, ignoreSbcProperties);
                        }
                        else if (property.PropertyType.BaseType.IsGenericType &&
                                 property.PropertyType.BaseType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                        {
                            IList modelCollection = (IList)property.GetValue(this);
                            Type itemType = property.PropertyType.BaseType.GetGenericArguments()[0];
                            ModelCollectionHelper.UpdateFromJson(modelCollection, itemType, jsonProperty.Value, ignoreSbcProperties);
                        }
                        else
                        {
                            object newValue = JsonSerializer.Deserialize(jsonProperty.Value.GetRawText(), property.PropertyType);
                            property.SetValue(this, newValue);
                        }
                        break;
                    }
                }
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
        /// Create a patch to bring an old instance to this state
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
            foreach (PropertyInfo property in _propertyInfos[otherType])
            {
                if (Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                {
                    continue;
                }

                object oldValue = property.GetValue(old), newValue = property.GetValue(this);
                if (oldValue == null || newValue == null || oldValue.GetType() != newValue.GetType())
                {
                    if (oldValue != newValue)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = newValue;
                    }
                }
                else if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    object diff = ((ModelObject)newValue).MakePatch((ModelObject)oldValue);
                    if (diff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = diff;
                    }
                }
                else if (property.PropertyType.IsGenericType &&
                         (property.PropertyType.GetGenericTypeDefinition() == typeof(ModelCollection<>) ||
                          property.PropertyType.GetGenericTypeDefinition() == typeof(ModelGrowingCollection<>)))
                {
                    Type itemType = property.PropertyType.GetGenericArguments()[0];
                    object listDiff = ModelCollectionHelper.FindDiffs((IList)oldValue, (IList)newValue, itemType);
                    if (listDiff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = listDiff;
                    }
                }
                else if (property.PropertyType.BaseType.IsGenericType &&
                         property.PropertyType.BaseType.GetGenericTypeDefinition() == typeof(ModelCollection<>))
                {
                    Type itemType = property.PropertyType.BaseType.GetGenericArguments()[0];
                    object listDiff = ModelCollectionHelper.FindDiffs((IList)oldValue, (IList)newValue, itemType);
                    if (listDiff != null)
                    {
                        if (diffs == null)
                        {
                            diffs = new Dictionary<string, object>();
                        }

                        string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                        diffs[propertyName] = listDiff;
                    }
                }
                else if (!newValue.Equals(oldValue))
                {
                    if (diffs == null)
                    {
                        diffs = new Dictionary<string, object>();
                    }

                    string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    diffs[propertyName] = newValue;
                }
            }
            return diffs;
        }
    }
}
