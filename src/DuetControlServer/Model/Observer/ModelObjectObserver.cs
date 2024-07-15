using DuetAPI.ObjectModel;
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
        private static void VariableModelObjectChanging(object? sender, PropertyChangingEventArgs e)
        {
            if (sender?.GetType().GetProperty(e.PropertyName!)?.GetValue(sender) is ModelObject modelMember)
            {
                // Prevent memory leaks in case variable model objects are replaced
                UnsubscribeFromModelObject(modelMember);
            }
        }

        /// <summary>
        /// Dictionary of model objects vs property change handlers
        /// </summary>
        private static readonly Dictionary<ModelObject, PropertyChangedEventHandler> _propertyChangedHandlers = [];

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
                string propertyName = JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName!);
                object? value = sender?.GetType().GetProperty(e.PropertyName!)!.GetValue(sender);
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
        /// <param name="modelObject">Object to subscribe to</param>
        /// <param name="path">Collection path</param>
        private static void SubscribeToModelObject(ModelObject modelObject, object[] path)
        {
            bool hasVariableModelObjects = false;
            foreach (PropertyInfo property in modelObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetMethod!.GetParameters().Length != 0)
                {
                    continue;
                }
                string propertyName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                object? value = property.GetValue(modelObject);

                if (value is ModelObject objectValue)
                {
                    SubscribeToModelObject(objectValue, AddToPath(path, propertyName));
                }
                else if (value is IModelCollection collectionValue)
                {
                    SubscribeToModelCollection(collectionValue, propertyName, path);
                }
                else if (value is IModelDictionary dictionaryValue)
                {
                    SubscribeToModelDictionary(dictionaryValue, AddToPath(path, propertyName));
                }

                hasVariableModelObjects |= property.PropertyType.IsAssignableTo(typeof(ModelObject)) && (property.SetMethod is not null);
            }

            if (modelObject is INotifyPropertyChanged propChangeModel)
            {
                PropertyChangedEventHandler changeHandler = PropertyChanged(hasVariableModelObjects, path);
                propChangeModel.PropertyChanged += changeHandler;
                _propertyChangedHandlers[modelObject] = changeHandler;
            }

            if (hasVariableModelObjects)
            {
                // This is barely needed so only register it where it is actually required.
                // It makes sure that events are removed again when a ModelObject instance is replaced
                modelObject.PropertyChanging += VariableModelObjectChanging;
            }
        }

        /// <summary>
        /// Unsubscribe from model object changes
        /// </summary>
        /// <param name="modelObject">Model object to unsubscribe from</param>
        private static void UnsubscribeFromModelObject(ModelObject modelObject)
        {
            if (_propertyChangedHandlers.TryGetValue(modelObject, out PropertyChangedEventHandler? changeHandler))
            {
                modelObject.PropertyChanged -= changeHandler;
                _propertyChangedHandlers.Remove(modelObject);
            }

            bool hasVariableModelObjects = false;
            foreach (PropertyInfo property in modelObject.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetMethod!.GetParameters().Length != 0)
                {
                    continue;
                }

                object? value = property.GetValue(modelObject);
                if (value is ModelObject objectValue)
                {
                    UnsubscribeFromModelObject(objectValue);
                }
                else if (value is IModelCollection collectionValue)
                {
                    UnsubscribeFromModelCollection(collectionValue);
                }
                else if (value is IModelDictionary dictionaryValue)
                {
                    UnsubscribeFromModelDictionary(dictionaryValue);
                }

                hasVariableModelObjects |= property.PropertyType.IsAssignableTo(typeof(ModelObject)) && (property.SetMethod is not null);
            }

            if (hasVariableModelObjects)
            {
                // Same here - unregister the event handler only where required
                modelObject.PropertyChanging -= VariableModelObjectChanging;
            }
        }
    }
}
