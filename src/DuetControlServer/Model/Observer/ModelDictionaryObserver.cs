using DuetAPI.ObjectModel;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Partial class implementation of the observer for model objects
    /// </summary>
    public static partial class Observer
    {
        /// <summary>
        /// Handler to unregister events from model dictionary instances
        /// </summary>
        /// <param name="sender">Parent object</param>
        /// <param name="e">Event arguments</param>
        private static void ModelDictionaryChanging(object sender, PropertyChangingEventArgs e)
        {
            if (sender is IDictionary dictionary && dictionary.Contains(e.PropertyName) && dictionary[e.PropertyName] is ModelObject modelItem)
            {
                // Prevent memory leaks in case variable model objects are replaced
                UnsubscribeFromModelObject(modelItem);
            }
        }

        /// <summary>
        /// Dictionary of model objects vs property change handlers
        /// </summary>
        private static readonly Dictionary<IModelDictionary, PropertyChangedEventHandler> _dictionaryChangedHandlers = new();

        /// <summary>
        /// Function to generate a property change handler
        /// </summary>
        /// <param name="hasVariableModelObjects">Whether this instance has any variable model objects</param>
        /// <param name="path">Property path</param>
        /// <returns>Property change handler</returns>
        private static PropertyChangedEventHandler DictionaryChanged(object[] path)
        {
            return (sender, e) =>
            {
                if (sender is IDictionary dictionary)
                {
                    object value = dictionary[e.PropertyName];
                    OnPropertyPathChanged?.Invoke(AddToPath(path, e.PropertyName), PropertyChangeType.Property, value);

                    if (value is ModelObject modelValue)
                    {
                        SubscribeToModelObject(modelValue, AddToPath(path, e.PropertyName));
                    }
                }
            };
        }

        /// <summary>
        /// Subscribe to changes of the given model object
        /// </summary>
        /// <param name="modelObject">Object to subscribe to</param>
        /// <param name="path">Collection path</param>
        private static void SubscribeToModelDictionary(IModelDictionary modelDictionary, object[] path)
        {
            PropertyChangedEventHandler changeHandler = DictionaryChanged(path);
            modelDictionary.PropertyChanged += changeHandler;
            _dictionaryChangedHandlers[modelDictionary] = changeHandler;

            if (GetItemType(modelDictionary.GetType()).IsSubclassOf(typeof(ModelObject)))
            {
                foreach (object key in modelDictionary)
                {
                    if (modelDictionary[key] is ModelObject modelItem)
                    {
                        SubscribeToModelObject(modelItem, AddToPath(path, key));
                    }
                }
                modelDictionary.PropertyChanging += ModelDictionaryChanging;
            }
        }

        /// <summary>
        /// Unsubscribe from model object changes
        /// </summary>
        /// <param name="modelObject">Model object to unsubscribe from</param>
        private static void UnsubscribeFromModelDictionary(IModelDictionary modelDictionary)
        {
            if (_dictionaryChangedHandlers.TryGetValue(modelDictionary, out PropertyChangedEventHandler changeHandler))
            {
                modelDictionary.PropertyChanged -= changeHandler;
                _dictionaryChangedHandlers.Remove(modelDictionary);
            }

            if (GetItemType(modelDictionary.GetType()).IsSubclassOf(typeof(ModelObject)))
            {
                foreach (object key in modelDictionary)
                {
                    if (modelDictionary[key] is ModelObject modelItem)
                    {
                        UnsubscribeFromModelObject(modelItem);
                    }
                }
                modelDictionary.PropertyChanging -= ModelDictionaryChanging;
            }
        }
    }
}
