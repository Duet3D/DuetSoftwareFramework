using DuetAPI.Machine;
using System;
using System.Collections;
using System.Collections.Generic;
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
        private static readonly Dictionary<object, NotifyCollectionChangedEventHandler> _collectionChangeHandlers = new Dictionary<object, NotifyCollectionChangedEventHandler>();

        /// <summary>
        /// Function to generate a value collection change handler
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Collection path</param>
        /// <returns>Property change handler</returns>
        private static NotifyCollectionChangedEventHandler ValueCollectionChanged(string collectionName, params object[] path)
        {
            object[] collectionPath = AddToPath(path, collectionName);
            return (sender, e) =>
            {
                IList senderList = (IList)sender;
                OnPropertyPathChanged?.Invoke(collectionPath, PropertyChangeType.Property, senderList);
            };
        }

        /// <summary>
        /// Function to generate an object collection change handler
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Collection path</param>
        /// <returns>Property change handler</returns>
        private static NotifyCollectionChangedEventHandler ObjectCollectionChanged(string collectionName, params object[] path)
        {
            return (sender, e) =>
            {
                // Notify clients that something has been changed in this collection
                IList senderList = (IList)sender;

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
                        nodePath = AddToPath(path, new ItemPathNode(collectionName, e.NewStartingIndex, senderList));
                        SubscribeToModelObject((ModelObject)e.NewItems[0], nodePath);
                        break;
                    case NotifyCollectionChangedAction.Move:
                        for (int i = Math.Min(e.OldStartingIndex, e.NewStartingIndex); i <= Math.Max(e.OldStartingIndex, e.NewStartingIndex); i++)
                        {
                            itemNeedsPatch[i] = true;
                        }
                        break;
                    case NotifyCollectionChangedAction.Replace:
                        itemNeedsPatch[e.NewStartingIndex] = true;
                        UnsubscribeFromModelObject((ModelObject)e.OldItems[0]);
                        nodePath = AddToPath(path, new ItemPathNode(collectionName, e.NewStartingIndex, senderList));
                        SubscribeToModelObject((ModelObject)e.NewItems[0], nodePath);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        for (int i = Math.Max(0, e.OldStartingIndex); i < senderList.Count; i++)
                        {
                            itemNeedsPatch[i] = true;
                        }
                        foreach (object item in e.OldItems)
                        {
                            UnsubscribeFromModelObject((ModelObject)item);
                        }
                        if (senderList.Count == 0)
                        {
                            nodePath = AddToPath(path, new ItemPathNode(collectionName, 0, senderList));
                            OnPropertyPathChanged?.Invoke(nodePath, PropertyChangeType.ObjectCollection, null);
                        }
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
                        OnPropertyPathChanged?.Invoke(nodePath, PropertyChangeType.ObjectCollection, senderList[i]);
                    }
                }
            };
        }

        /// <summary>
        /// Subscribe to changes of the given model collection
        /// </summary>
        /// <param name="modelCollection">Collection to subscribe to</param>
        /// <param name="itemType">Type of the items</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Path of the subscription</param>
        private static void SubscribeToModelCollection(object modelCollection, Type itemType, string collectionName, object[] path)
        {
            INotifyCollectionChanged ncc = (INotifyCollectionChanged)modelCollection;
            if (itemType.IsSubclassOf(typeof(ModelObject)))
            {
                NotifyCollectionChangedEventHandler changeHandler = ObjectCollectionChanged(collectionName, path);
                ncc.CollectionChanged += changeHandler;
                _collectionChangeHandlers[modelCollection] = changeHandler;

                IList list = (IList)modelCollection;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is ModelObject item)
                    {
                        SubscribeToModelObject(item, AddToPath(path, new ItemPathNode(collectionName, i, list)));
                    }
                }
            }
            else
            {
                NotifyCollectionChangedEventHandler changeHandler = ValueCollectionChanged(collectionName, path);
                ncc.CollectionChanged += changeHandler;
                _collectionChangeHandlers[modelCollection] = changeHandler;
            }
        }

        /// <summary>
        /// Unsubscribe from changes of a model collection
        /// </summary>
        /// <param name="modelCollection">Collection to unsubscribe from</param>
        /// <param name="itemType">Item type</param>
        private static void UnsubscribeFromModelCollection(object modelCollection, Type itemType)
        {
            NotifyCollectionChangedEventHandler changeHandler = _collectionChangeHandlers[modelCollection];
            if (changeHandler == null)
            {
                return;
            }

            INotifyCollectionChanged ncc = (INotifyCollectionChanged)modelCollection;
            ncc.CollectionChanged -= changeHandler;
            _collectionChangeHandlers.Remove(modelCollection);

            if (itemType.IsSubclassOf(typeof(ModelObject)))
            {
                foreach (object item in (IList)modelCollection)
                {
                    if (item != null)
                    {
                        UnsubscribeFromModelObject((ModelObject)item);
                    }
                }
            }
        }
    }
}
