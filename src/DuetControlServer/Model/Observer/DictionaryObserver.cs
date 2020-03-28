using System.Collections.Generic;
using System.Collections.Specialized;

namespace DuetControlServer.Model
{
    public static partial class Observer
    {
        /// <summary>
        /// Creates a new event change handler for dictionaries
        /// </summary>
        /// <typeparam name="Ta">Key type</typeparam>
        /// <typeparam name="Tb">Value type</typeparam>
        /// <param name="path">Path to the dictionary</param>
        /// <returns>Change handler</returns>
        private static NotifyCollectionChangedEventHandler DictionaryChanged<Ta, Tb>(params object[] path)
        {
            return (sender, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace)
                {
                    foreach (var item in e.NewItems)
                    {
                        KeyValuePair<Ta, Tb> kv = (KeyValuePair<Ta, Tb>)item;
                        OnPropertyPathChanged?.Invoke(AddToPath(path, kv.Key), PropertyChangeType.Property, kv.Value);
                    }
                }
                else if (e.Action == NotifyCollectionChangedAction.Remove || e.Action == NotifyCollectionChangedAction.Reset)
                {
                    foreach (var item in e.OldItems)
                    {
                        KeyValuePair<Ta, Tb> kv = (KeyValuePair<Ta, Tb>)item;
                        OnPropertyPathChanged?.Invoke(AddToPath(path, kv.Key), PropertyChangeType.Property, null);
                    }
                }
            };
        }
    }
}
