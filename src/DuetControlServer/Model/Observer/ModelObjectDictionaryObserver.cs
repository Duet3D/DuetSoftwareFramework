using DuetAPI.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;

namespace DuetControlServer.Model
{
    public static partial class Observer
    {
        /// <summary>
        /// Dictionary of model object dictionaries vs property change handlers
        /// </summary>
        private static readonly Dictionary<object, PropertyChangedEventHandler> _modelDictionaryChangedHandlers = new();

        /// <summary>
        /// Function to generate a property change handler for model dictionaries
        /// </summary>
        /// <param name="sender">Dictionary source</param>
        /// <param name="e">Property change event arguments</param>
        /// <returns>Property change handler</returns>
        private static void ModelDictionaryChanging(object sender, PropertyChangingEventArgs e)
        {
            ModelObject value = (ModelObject)sender.GetType().GetProperty("Item").GetValue(sender, new object[] { e.PropertyName });
            if (value != null)
            {
                UnsubscribeFromModelObject(value);
            }
        }

        /// <summary>
        /// Function to generate a property change handler for model dictionaries
        /// </summary>
        /// <param name="path">Property path</param>
        /// <returns>Property change handler</returns>
        private static PropertyChangedEventHandler ModelDictionaryChanged(object[] path)
        {
            return (sender, e) =>
            {
                object[] itemPath = AddToPath(path, e.PropertyName);
                ModelObject value = (ModelObject)sender.GetType().GetProperty("Item").GetValue(sender, new object[] { e.PropertyName });
                if (value != null)
                {
                    SubscribeToModelObject(value, itemPath);
                }
                OnPropertyPathChanged?.Invoke(itemPath, PropertyChangeType.Property, value);
            };
        }

        /// <summary>
        /// Subscribe to changes of the given model dictionary
        /// </summary>
        /// <param name="dictionary">Dictionary to subscribe to</param>
        /// <param name="path">Dictionary path</param>
        private static void SubscribeToModelObjectDictionary(object dictionary, object[] path)
        {
            (dictionary as INotifyPropertyChanging).PropertyChanging += ModelDictionaryChanging;

            PropertyChangedEventHandler changeHandler = ModelDictionaryChanged(path);
            (dictionary as INotifyPropertyChanged).PropertyChanged += changeHandler;
            _modelDictionaryChangedHandlers[dictionary] = changeHandler;
        }

        /// <summary>
        /// Unsubscribe to changes of the given model dictionary
        /// </summary>
        /// <param name="dictionary">Dictionary to unsubscribe from</param>
        private static void UnsubscribeFromModelObjectDictionary(object dictionary)
        {
            (dictionary as INotifyPropertyChanging).PropertyChanging -= ModelDictionaryChanging;

            if (_modelDictionaryChangedHandlers.TryGetValue(dictionary, out PropertyChangedEventHandler changeHandler))
            {
                (dictionary as INotifyPropertyChanged).PropertyChanged -= changeHandler;
                _modelDictionaryChangedHandlers.Remove(dictionary);
            }
        }
    }
}
