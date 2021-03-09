using System.Collections.Generic;
using System.ComponentModel;

namespace DuetControlServer.Model
{
    public static partial class Observer
    {
        /// <summary>
        /// Dictionary of model objects vs property change handlers
        /// </summary>
        private static readonly Dictionary<object, PropertyChangedEventHandler> _dictionaryChangedHandlers = new();

        /// <summary>
        /// Function to generate a property change handler for model dictionaries
        /// </summary>
        /// <param name="path">Property path</param>
        /// <returns>Property change handler</returns>
        private static PropertyChangedEventHandler DictionaryChanged(object[] path)
        {
            return (sender, e) =>
            {
                object value = sender.GetType().GetProperty("Item").GetValue(sender, new object[] { e.PropertyName });
                OnPropertyPathChanged?.Invoke(AddToPath(path, e.PropertyName), PropertyChangeType.Property, value);
            };
        }

        /// <summary>
        /// Subscribe to changes of the given model dictionary
        /// </summary>
        /// <param name="dictionary">Dictionary to subscribe to</param>
        /// <param name="path">Dictionary path</param>
        private static void SubscribeToModelDictionary(object dictionary, object[] path)
        {
            PropertyChangedEventHandler changeHandler = DictionaryChanged(path);
            (dictionary as INotifyPropertyChanged).PropertyChanged += changeHandler;
            _dictionaryChangedHandlers[dictionary] = changeHandler;
        }

        /// <summary>
        /// Unsubscribe to changes of the given model dictionary
        /// </summary>
        /// <param name="dictionary">Dictionary to unsubscribe from</param>
        private static void UnsubscribeFromModelDictionary(object dictionary)
        {
            if (_modelDictionaryChangedHandlers.TryGetValue(dictionary, out PropertyChangedEventHandler changeHandler))
            {
                (dictionary as INotifyPropertyChanged).PropertyChanged -= changeHandler;
                _modelDictionaryChangedHandlers.Remove(dictionary);
            }
        }
    }
}
