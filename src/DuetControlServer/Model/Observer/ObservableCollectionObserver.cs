using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Partial class implementation of the observer for model collections
    /// </summary>
    public static partial class Observer
    {
        /// <summary>
        /// Dictionary of collections vs change handlers
        /// </summary>
        private static readonly Dictionary<ICollection, NotifyCollectionChangedEventHandler> _observableCollectionChangeHandlers = [];

        /// <summary>
        /// Function to generate an object collection change handler
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Collection path</param>
        /// <returns>Property change handler</returns>
        private static NotifyCollectionChangedEventHandler ObservableCollectionChanged(string collectionName, params object[] path)
        {
            return (sender, e) =>
            {
                // Notify clients that something has been changed in this collection
                IList senderList = (IList)sender!;

                // Unsubscribe from old items, subscribe to new items, and figure out which items need to be patched
                bool[] itemNeedsPatch = new bool[senderList.Count];
                object[] nodePath;
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        if (e.NewStartingIndex == senderList.Count - 1)
                        {
                            // Item added
                            itemNeedsPatch[e.NewStartingIndex] = true;
                        }
                        else
                        {
                            // Item inserted
                            for (int i = e.NewStartingIndex; i < senderList.Count; i++)
                            {
                                itemNeedsPatch[i] = true;
                            }
                        }
                        break;
                    case NotifyCollectionChangedAction.Move:
                        for (int i = Math.Min(e.OldStartingIndex, e.NewStartingIndex); i <= Math.Max(e.OldStartingIndex, e.NewStartingIndex); i++)
                        {
                            itemNeedsPatch[i] = true;
                        }
                        break;
                    case NotifyCollectionChangedAction.Replace:
                        itemNeedsPatch[e.NewStartingIndex] = true;
                        nodePath = AddToPath(path, new ItemPathNode(collectionName, e.NewStartingIndex, senderList));
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        for (int i = Math.Max(0, e.OldStartingIndex); i < senderList.Count; i++)
                        {
                            itemNeedsPatch[i] = true;
                        }
                        nodePath = AddToPath(path, new ItemPathNode(collectionName, senderList.Count, senderList));
                        OnPropertyPathChanged?.Invoke(nodePath, PropertyChangeType.Collection, null);
                        break;
                    case NotifyCollectionChangedAction.Reset:
                        // This is NOT used because e.OldItems is not populated...
                        break;
                }

                // Tell clients what items need to be patched
                for (int i = 0; i < senderList.Count; i++)
                {
                    if (itemNeedsPatch[i])
                    {
                        nodePath = AddToPath(path, new ItemPathNode(collectionName, i, senderList));
                        OnPropertyPathChanged?.Invoke(nodePath, PropertyChangeType.Collection, senderList[i]);
                    }
                }
            };
        }

        /// <summary>
        /// Subscribe to changes of the given model collection
        /// </summary>
        /// <param name="observableCollection">Collection to subscribe to</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Path of the subscription</param>
        private static void SubscribeToObservableCollection<T>(ObservableCollection<T> observableCollection, string collectionName, object[] path)
        {
            NotifyCollectionChangedEventHandler changeHandler = ObservableCollectionChanged(collectionName, path);
            observableCollection.CollectionChanged += changeHandler;
            _observableCollectionChangeHandlers[observableCollection] = changeHandler;
        }

        /// <summary>
        /// Unsubscribe from changes of a model collection
        /// </summary>
        /// <param name="observableCollection">Collection to unsubscribe from</param>
        private static void UnsubscribeFromObservableCollection<T>(ObservableCollection<T> observableCollection)
        {
            if (_observableCollectionChangeHandlers.TryGetValue(observableCollection, out NotifyCollectionChangedEventHandler? changeHandler))
            {
                observableCollection.CollectionChanged -= changeHandler;
                _observableCollectionChangeHandlers.Remove(observableCollection);
            }
        }
    }
}
