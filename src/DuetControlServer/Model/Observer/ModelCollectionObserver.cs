using DuetAPI.ObjectModel;
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
        private static readonly Dictionary<IModelCollection, NotifyCollectionChangedEventHandler> _collectionChangeHandlers = new();

        /// <summary>
        /// Function to generate an object collection change handler
        /// </summary>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Collection path</param>
        /// <returns>Property change handler</returns>
        private static NotifyCollectionChangedEventHandler CollectionChanged(string collectionName, params object[] path)
        {
            return (sender, e) =>
            {
                if (collectionName == "restorePoints")
                {
                    Console.WriteLine("Something changed!");
                }

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
                        if (e.NewItems[0] is ModelObject newObjectItem)
                        {
                            nodePath = AddToPath(path, new ItemPathNode(collectionName, e.NewStartingIndex, senderList));
                            SubscribeToModelObject(newObjectItem, nodePath);
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
                        if (e.OldItems[0] is ModelObject oldObjectItem)
                        {
                            UnsubscribeFromModelObject(oldObjectItem);
                        }
                        nodePath = AddToPath(path, new ItemPathNode(collectionName, e.NewStartingIndex, senderList));
                        if (e.NewItems[0] is ModelObject replaceObjectItem)
                        {
                            SubscribeToModelObject(replaceObjectItem, nodePath);
                        }
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        for (int i = Math.Max(0, e.OldStartingIndex); i < senderList.Count; i++)
                        {
                            itemNeedsPatch[i] = true;
                        }
                        foreach (object item in e.OldItems)
                        {
                            if (item is ModelObject objectItem)
                            {
                                UnsubscribeFromModelObject(objectItem);
                            }
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
        /// Function to generate a growing collection change handler
        /// </summary>
        /// <param name="path">Path to the growing collection</param>
        /// <returns>Change handler</returns>
        private static NotifyCollectionChangedEventHandler GrowingCollectionChanged(params object[] path)
        {
            return (_, e) =>
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
        /// Subscribe to changes of the given model collection
        /// </summary>
        /// <param name="modelCollection">Collection to subscribe to</param>
        /// <param name="collectionName">Name of the collection</param>
        /// <param name="path">Path of the subscription</param>
        private static void SubscribeToModelCollection(IModelCollection modelCollection, string collectionName, object[] path)
        {
            NotifyCollectionChangedEventHandler changeHandler = (modelCollection is IGrowingModelCollection) ? GrowingCollectionChanged(AddToPath(path, collectionName)) : CollectionChanged(collectionName, path);
            modelCollection.CollectionChanged += changeHandler;
            _collectionChangeHandlers[modelCollection] = changeHandler;

            Type itemType = GetItemType(modelCollection.GetType());
            if (itemType != null && itemType.IsAssignableTo(typeof(ModelObject)))
            {
                IList list = (IList)modelCollection;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is ModelObject item)
                    {
                        SubscribeToModelObject(item, AddToPath(path, new ItemPathNode(collectionName, i, list)));
                    }
                }
            }
        }

        /// <summary>
        /// Unsubscribe from changes of a model collection
        /// </summary>
        /// <param name="modelCollection">Collection to unsubscribe from</param>
        private static void UnsubscribeFromModelCollection(IModelCollection modelCollection)
        {
            if (_collectionChangeHandlers.TryGetValue(modelCollection, out NotifyCollectionChangedEventHandler changeHandler))
            {
                modelCollection.CollectionChanged -= changeHandler;
                _collectionChangeHandlers.Remove(modelCollection);
            }

            Type itemType = GetItemType(modelCollection.GetType());
            if (itemType != null && itemType.IsAssignableTo(typeof(IModelObject)))
            {
                IList list = (IList)modelCollection;
                foreach (object listItem in list)
                {
                    if (listItem is ModelObject item)
                    {
                        UnsubscribeFromModelObject(item);
                    }
                }
            }
        }
    }
}
