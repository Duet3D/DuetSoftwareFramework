using System.Collections.Generic;
using System.Collections.Specialized;

namespace DuetControlServer.Model
{
    public static partial class Observer
    {
        /// <summary>
        /// Dictionary of growing collections vs change handlers
        /// </summary>
        private static readonly Dictionary<object, NotifyCollectionChangedEventHandler> _growingCollectionChangeHandlers = new();

        /// <summary>
        /// Function to generate a growing collection change handler
        /// </summary>
        /// <param name="path">Path to the growing collection</param>
        /// <returns>Change handler</returns>
        private static NotifyCollectionChangedEventHandler GrowingCollectionChanged(params object[] path)
        {
            return (sender, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    OnPropertyPathChanged?.Invoke(path, PropertyChangeType.GrowingCollection, e.NewItems);
                }
                else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex == -1)
                {
                    OnPropertyPathChanged?.Invoke(path, PropertyChangeType.GrowingCollection, null);
                }
            };
        }

        /// <summary>
        /// Subscribe to changes of the given growing model collection
        /// </summary>
        /// <param name="growingModelCollection">Growing model collection to subscribe to</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Path to the growing collection</param>
        private static void SubscribeToGrowingModelCollection(object growingModelCollection, string collectionName, params object[] path)
        {
            INotifyCollectionChanged ncc = (INotifyCollectionChanged)growingModelCollection;

            NotifyCollectionChangedEventHandler changeHandler = GrowingCollectionChanged(AddToPath(path, collectionName));
            ncc.CollectionChanged += changeHandler;
            _growingCollectionChangeHandlers.Add(growingModelCollection, changeHandler);
        }

        /// <summary>
        /// Unsubscribe from changes of the given growing model collection
        /// </summary>
        /// <param name="growingModelCollecion">Growing model collection to unsubscribe from</param>
        private static void UnsubscribeFromGrowingModelCollection(object growingModelCollecion)
        {
            NotifyCollectionChangedEventHandler changeHandler = _growingCollectionChangeHandlers[growingModelCollecion];
            if (changeHandler != null)
            {
                INotifyCollectionChanged ncc = (INotifyCollectionChanged)growingModelCollecion;
                ncc.CollectionChanged -= changeHandler;
                _growingCollectionChangeHandlers.Remove(growingModelCollecion);
            }
        }
    }
}
