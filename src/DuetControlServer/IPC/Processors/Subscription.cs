using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.Model;
using Nito.AsyncEx;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Subscription processor that notifies clients about object model changes.
    /// There is no point in deserializing the object model here so only the JSON representation is kept here.
    /// </summary>
    public class Subscription : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Acknowledge)
        };
        
        /// <summary>
        /// List of active subscribers
        /// </summary>
        private static readonly List<Subscription> _subscriptions = new List<Subscription>();

        /// <summary>
        /// Checks if any subscribers are connected
        /// </summary>
        /// <returns>True if subscribers are connected</returns>
        public static bool AreClientsConnected()
        {
            lock (_subscriptions)
            {
                return _subscriptions.Count > 0;
            }
        }

        /// <summary>
        /// Mode of this subscriber
        /// </summary>
        private readonly SubscriptionMode _mode;

        /// <summary>
        /// List of filters (in Patch mode)
        /// </summary>
        private readonly object[][] _filters;

        /// <summary>
        /// Dictionary of updated fields (in Patch mode)
        /// </summary>
        private readonly Dictionary<string, object> _patch = new Dictionary<string, object>();
        
        /// <summary>
        /// Constructor of the subscription processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public Subscription(Connection conn, ClientInitMessage initMessage) : base(conn)
        {
            SubscribeInitMessage subscribeInitMessage = (SubscribeInitMessage)initMessage;
            _mode = subscribeInitMessage.SubscriptionMode;
            if (!string.IsNullOrEmpty(subscribeInitMessage.Filter))
            {
                _filters = Filter.ConvertFilters(subscribeInitMessage.Filter);
            }

            lock (_subscriptions)
            {
                _subscriptions.Add(this);
            }
            conn.Logger.Debug("Subscription processor registered in {0} mode", _mode);
        }

        /// <summary>
        /// Task that keeps pushing model updates to the client
        /// </summary>
        /// <returns>Task that represents the lifecycle of a connection</returns>
        public override async Task Process()
        {
            try
            {
                // Subscribe to changes in Patch mode
                if (_mode == SubscriptionMode.Patch)
                {
                    Observer.OnPropertyPathChanged += MachineModelPropertyChanged;
                }

                // Get the requested machine model
                byte[] jsonData;
                using (await Provider.AccessReadOnlyAsync())
                {
                    if (_mode == SubscriptionMode.Full || _filters == null)
                    {
                        jsonData = JsonSerializer.SerializeToUtf8Bytes(Provider.Get, JsonHelper.DefaultJsonOptions);
                    }
                    else
                    {
                        Dictionary<string, object> patchModel = new Dictionary<string, object>();
                        foreach (object[] filter in _filters)
                        {
                            Dictionary<string, object> partialModel = Filter.GetFiltered(_filters);
                            Filter.MergeFiltered(patchModel, partialModel);
                        }
                        jsonData = JsonSerializer.SerializeToUtf8Bytes(patchModel, JsonHelper.DefaultJsonOptions);
                    }
                }

                do
                {
                    // Send new JSON data
                    if (jsonData != null)
                    {
                        await Connection.Send(jsonData);
                        jsonData = null;

                        // Wait for an acknowledgement from the client
                        BaseCommand command = await Connection.ReceiveCommand();
                        if (!SupportedCommands.Contains(command.GetType()))
                        {
                            // Take care of unsupported commands
                            throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                        }
                    }

                    // Wait for an object model update to complete
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
                    cts.CancelAfter(Settings.SocketPollInterval);
                    try
                    {
                        await Provider.WaitForUpdate(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!Program.CancellationToken.IsCancellationRequested)
                        {
                            Connection.Poll();
                            continue;
                        }
                        Connection.Logger.Debug("Subscriber connection requested to terminate");
                        throw;
                    }

                    // Get the (diff) JSON
                    if (_mode == SubscriptionMode.Patch)
                    {
                        lock (_patch)
                        {
                            if (_patch.Count > 0)
                            {
                                jsonData = JsonSerializer.SerializeToUtf8Bytes(_patch, JsonHelper.DefaultJsonOptions);
                                _patch.Clear();
                            }
                        }
                    }
                    else
                    {
                        using (await Provider.AccessReadOnlyAsync())
                        {
                            jsonData = JsonSerializer.SerializeToUtf8Bytes(Provider.Get, JsonHelper.DefaultJsonOptions);
                        }
                    }
                }
                while (!Program.CancellationToken.IsCancellationRequested);
            }
            catch (Exception e)
            {
                // Don't throw this exception if the connection has been termianted
                if (!(e is SocketException))
                {
                    throw;
                }
            }
            finally
            {
                lock (_subscriptions)
                {
                    _subscriptions.Remove(this);
                }

                if (_mode == SubscriptionMode.Patch)
                {
                    Observer.OnPropertyPathChanged -= MachineModelPropertyChanged;
                }
                Connection.Logger.Debug("Subscription processor unregistered");
            }
        }


        /// <summary>
        /// Check if the change of the given path has to be recorded
        /// </summary>
        /// <param name="path">Change path</param>
        /// <returns>True if a filter applies</returns>
        private bool CheckFilters(object[] path)
        {
            if (_filters == null || path.Length == 0)
            {
                return true;
            }

            foreach (object[] filter in _filters)
            {
                if (Filter.PathMatches(path, filter))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Get the object from a path node
        /// </summary>
        /// <param name="path">Path node</param>
        /// <returns>Item at the given path</returns>
        private object GetPathNode(object[] path)
        {
            Dictionary<string, object> currentDictionary = _patch;
            IList currentList = null;

            for (int i = 0; i < path.Length - 1; i++)
            {
                object pathItem = path[i];
                if (pathItem is string pathString)
                {
                    if (currentDictionary.TryGetValue(pathString, out object child))
                    {
                        if (child is Dictionary<string, object> childDictionary)
                        {
                            currentDictionary = childDictionary;
                            currentList = null;
                        }
                        else if (child is IList childList)
                        {
                            currentList = childList;
                            currentDictionary = null;
                        }
                        else
                        {
                            throw new InvalidOperationException($"Invalid child type {child.GetType().Name} for path {string.Join('/', path)}");
                        }
                    }
                    else
                    {
                        Dictionary<string, object> newNode = new Dictionary<string, object>();
                        currentDictionary.Add(pathString, newNode);
                        currentDictionary = newNode;
                    }
                }
                else if (pathItem is ItemPathNode pathNode)
                {
                    if (currentDictionary.TryGetValue(pathNode.Name, out object nodeList))
                    {
                        currentList = (IList)nodeList;

                        for (int k = currentList.Count; k > pathNode.Count; k--)
                        {
                            currentList.RemoveAt(k - 1);
                        }
                    }
                    else
                    {
                        currentList = new List<object>(pathNode.Count);
                        currentDictionary.Add(pathNode.Name, currentList);
                    }

                    Type itemType = (path[i + 1] is string) ? typeof(Dictionary<string, object>) : typeof(List<object>);
                    for (int k = currentList.Count; k < pathNode.Count; k++)
                    {
                        object newItem = Activator.CreateInstance(itemType);
                        currentList.Add(newItem);
                    }

                    return currentList[pathNode.Index];
                }
            }

            if (currentDictionary != null)
            {
                return currentDictionary;
            }
            return currentList;
        }

        /// <summary>
        /// Method that is called when a property of the machine model has changed
        /// </summary>
        /// <param name="path">Path to the property</param>
        /// <param name="changeType">Type of the change</param>
        /// <param name="value">New value</param>
        private void MachineModelPropertyChanged(object[] path, PropertyChangeType changeType, object value)
        {
            if (!CheckFilters(path))
            {
                return;
            }

            lock (_patch)
            {
                try
                {
                    object node = GetPathNode(path);
                    if (!(node is Dictionary<string, object>) && !(node is IList))
                    {
                        // No need to perform an update because the underlying object is about to be fully transferred anyway
                        return;
                    }

                    switch (changeType)
                    {
                        case PropertyChangeType.Property:
                            // Set new property value
                            Dictionary<string, object> propertyNode = (Dictionary<string, object>)node;
                            string propertyName = (string)path[^1];

                            propertyNode[propertyName] = value;
                            break;

                        case PropertyChangeType.ObjectCollection:
                            // Update an object collection
                            Dictionary<string, object> objectCollectionNode = (Dictionary<string, object>)node;
                            ItemPathNode pathNode = (ItemPathNode)path[^1];

                            List<object> objectCollectionList;
                            if (objectCollectionNode.TryGetValue(pathNode.Name, out object objectCollection))
                            {
                                objectCollectionList = (List<object>)objectCollection;

                                for (int k = objectCollectionList.Count; k > pathNode.Count; k--)
                                {
                                    objectCollectionList.RemoveAt(k - 1);
                                }
                            }
                            else
                            {
                                objectCollectionList = new List<object>(pathNode.Count);
                                objectCollectionNode.Add(pathNode.Name, objectCollectionList);
                            }

                            Type itemType = (value is IList) ? typeof(List<object>) : typeof(Dictionary<string, object>);
                            for (int k = objectCollectionList.Count; k < pathNode.Count; k++)
                            {
                                object newItem = Activator.CreateInstance(itemType);
                                objectCollectionList.Add(newItem);
                            }

                            if (pathNode.Index < pathNode.Count)
                            {
                                objectCollectionList[pathNode.Index] = value;
                            }
                            break;

                        case PropertyChangeType.GrowingCollection:
                            // Add new items or clear list
                            if (node is Dictionary<string, object> parent)
                            {
                                string collectionName = (string)path[^1];
                                if (value == null && collectionName.Equals(nameof(MachineModel.Messages), StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // Don't clear messages - they are volatile anyway
                                    break;
                                }

                                IList growingCollection;
                                if (parent.TryGetValue(collectionName, out object obj))
                                {
                                    growingCollection = (IList)obj;
                                }
                                else
                                {
                                    growingCollection = new List<object>();
                                    parent.Add(collectionName, growingCollection);
                                }

                                if (value != null)
                                {
                                    foreach (object newItem in (IList)value)
                                    {
                                        growingCollection.Add(newItem);
                                    }
                                }
                                else
                                {
                                    growingCollection.Clear();
                                }
                            }
                            else
                            {
                                throw new ArgumentException($"Invalid growing collection parent type {node.GetType().Name}");
                            }
                            break;
                    }
                }
                catch (Exception e)
                {
                    Connection.Logger.Error(e, "Failed to record {0} = {1} ({2})", string.Join('/', path), value, changeType);
                }
            }
        }
    }
}
