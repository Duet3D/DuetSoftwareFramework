using DuetAPI.ObjectModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Partial class implementation of the observer for model objects
    /// </summary>
    public static partial class Observer
    {
        /// <summary>
        /// Handler to unregister events from variable model object instances
        /// </summary>
        /// <param name="sender">Parent object</param>
        /// <param name="e">Event arguments</param>
        private static void VariableModelObjectChanging(object sender, PropertyChangingEventArgs e)
        {
            if (sender.GetType().GetProperty(e.PropertyName).GetValue(sender) is ModelObject modelMember)
            {
                // Prevent memory leaks in case variable model objects are replaced
                UnsubscribeFromModelObject(modelMember);
            }
        }

        /// <summary>
        /// Dictionary of model objects vs property change handlers
        /// </summary>
        private static readonly Dictionary<ModelObject, PropertyChangedEventHandler> _propertyChangedHandlers = new Dictionary<ModelObject, PropertyChangedEventHandler>();

        /// <summary>
        /// Function to generate a property change handler
        /// </summary>
        /// <param name="hasVariableModelObjects">Whether this instance has any variable model objects</param>
        /// <param name="path">Property path</param>
        /// <returns>Property change handler</returns>
        private static PropertyChangedEventHandler PropertyChanged(bool hasVariableModelObjects, object[] path)
        {
            return (sender, e) =>
            {
                string propertyName = JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName);
                object value = sender.GetType().GetProperty(e.PropertyName).GetValue(sender);
                OnPropertyPathChanged?.Invoke(AddToPath(path, propertyName), PropertyChangeType.Property, value);

                if (hasVariableModelObjects && value is ModelObject modelMember)
                {
                    // Subscribe to variable ModelObject events again
                    SubscribeToModelObject(modelMember, AddToPath(path, propertyName));
                }
            };
        }

        /// <summary>
        /// Subscribe to changes of the given model object
        /// </summary>
        /// <param name="model">Object to subscribe to</param>
        /// <param name="path">Collection path</param>
        private static void SubscribeToModelObject(ModelObject model, object[] path)
        {
            if (model == null)
            {
                return;
            }

            bool hasVariableModelObjects = false;
            foreach (PropertyInfo property in model.GetType().GetProperties())
            {
                string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                object value = property.GetValue(model);
                if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    if (value != null)
                    {
                        SubscribeToModelObject((ModelObject)value, AddToPath(path, propertyName));
                    }
                    hasVariableModelObjects |= (property.SetMethod != null);
                }
                else if (ModelCollection.GetItemType(property.PropertyType, out Type itemType))
                {
                    if (ModelGrowingCollection.TypeMatches(property.PropertyType))
                    {
                        SubscribeToGrowingModelCollection(value, propertyName, path);
                    }
                    else
                    {
                        SubscribeToModelCollection(value, itemType, propertyName, path);
                    }
                }
                else if (property.PropertyType.IsGenericType && typeof(ModelDictionary<>) == property.PropertyType.GetGenericTypeDefinition())
                {
                    SubscribeToModelDictionary(value, AddToPath(path, propertyName));
                }
            }

            PropertyChangedEventHandler changeHandler = PropertyChanged(hasVariableModelObjects, path);
            model.PropertyChanged += changeHandler;
            _propertyChangedHandlers[model] = changeHandler;

            if (hasVariableModelObjects)
            {
                // This is barely needed so only register it where it is actually required.
                // It makes sure that events are removed again when a ModelObject instance is replaced
                model.PropertyChanging += VariableModelObjectChanging;
            }
        }

        /// <summary>
        /// Unsubscribe from model object changes
        /// </summary>
        /// <param name="model">Model object to unsubscribe from</param>
        private static void UnsubscribeFromModelObject(ModelObject model)
        {
            if (model == null)
            {
                return;
            }

            PropertyChangedEventHandler changeHandler = _propertyChangedHandlers[model];
            if (changeHandler == null)
            {
                // Already unregistered - don't bother to continue
                return;
            }
            model.PropertyChanged -= changeHandler;
            _propertyChangedHandlers.Remove(model);

            bool unregisterPropertyChanging = false;
            foreach (PropertyInfo property in model.GetType().GetProperties())
            {
                object value = property.GetValue(model);
                if (property.PropertyType.IsSubclassOf(typeof(ModelObject)))
                {
                    if (value != null)
                    {
                        UnsubscribeFromModelObject((ModelObject)value);
                    }
                    unregisterPropertyChanging |= (property.SetMethod != null);
                }
                else if (ModelCollection.GetItemType(property.PropertyType, out Type itemType))
                {
                    if (ModelGrowingCollection.TypeMatches(property.PropertyType))
                    {
                        UnsubscribeFromGrowingModelCollection(value);
                    }
                    else
                    {
                        UnsubscribeFromModelCollection(value, property.PropertyType.GetGenericArguments()[0]);
                    }
                }
                else if (property.PropertyType.IsGenericType && typeof(ModelDictionary<>) == property.PropertyType.GetGenericTypeDefinition())
                {
                    UnsubscribeFromModelDictionary(value);
                }
            }

            if (unregisterPropertyChanging)
            {
                // Same here - unregister the event handler only where required
                model.PropertyChanging -= VariableModelObjectChanging;
            }
        }
    }
}
