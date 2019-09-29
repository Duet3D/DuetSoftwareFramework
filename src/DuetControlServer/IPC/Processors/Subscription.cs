using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.Machine;
using DuetAPI.Utility;
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

        private readonly SubscriptionMode _mode;
        private readonly string[][] _filters;

        private readonly Dictionary<string, object> _patch = new Dictionary<string, object>();
        private readonly AsyncAutoResetEvent _updateAvailableEvent = new AsyncAutoResetEvent();
        
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
                string[] filterStrings = subscribeInitMessage.Filter.Split(',', '|', '\r', '\n', ' ');
                _filters = filterStrings.Select(filter => filter.Split('/')).ToArray();
            }

            lock (_subscriptions)
            {
                _subscriptions.Add(this);
            }
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
                    Model.Observer.OnPropertyPathChanged += MachineModelPropertyChanged;
                }

                // Serialize the whole machine model
                byte[] jsonData;
                using (await Model.Provider.AccessReadOnlyAsync())
                {
                    jsonData = JsonSerializer.SerializeToUtf8Bytes(Model.Provider.Get, JsonHelper.DefaultJsonOptions);
                }

                do
                {
                    // Send new JSON data
                    if (jsonData != null)
                    {
                        await Connection.Send(jsonData);

                        // Wait for an acknowledgement from the client
                        BaseCommand command = await Connection.ReceiveCommand();
                        if (command == null)
                        {
                            return;
                        }

                        if (!SupportedCommands.Contains(command.GetType()))
                        {
                            throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                        }
                    }

                    // Wait for an object model update to complete
                    await _updateAvailableEvent.WaitAsync(Program.CancelSource.Token);

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
                            else
                            {
                                jsonData = null;
                            }
                        }
                    }
                    else
                    {
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            jsonData = JsonSerializer.SerializeToUtf8Bytes(Model.Provider.Get, JsonHelper.DefaultJsonOptions);
                        }
                    }
                }
                while (!Program.CancelSource.IsCancellationRequested);
            }
            catch (Exception e)
            {
                // Socket exceptions are expected.
                // They occur when data is sent after the remote end had acknowledged an update but then terminated the connection
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
                    Model.Observer.OnPropertyPathChanged -= MachineModelPropertyChanged;
                }
            }
        }

        private bool CheckFilter(object[] path, string[] filter)
        {
            for (int i = 0; i < filter.Length; i++)
            {
                string arg = filter[i];
                if (arg == "**")
                {
                    return true;
                }

                if (i >= path.Length)
                {
                    return false;
                }

                object pathItem = path[i];
                if (pathItem is string stringItem)
                {
                    if (arg != "*" &&
                        !arg.Equals(stringItem, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return false;
                    }
                }
                else if (pathItem is Model.ItemPathNode listItem)
                {
                    if (!arg.Equals($"{listItem.Name}[*]", StringComparison.InvariantCultureIgnoreCase) &&
                        !arg.Equals($"{listItem.Name}[{listItem.Index}]", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return (path.Length == filter.Length);
        }

        private bool CheckFilter(object[] path)
        {
            if (_filters == null)
            {
                return true;
            }

            foreach (string[] filter in _filters)
            {
                if (CheckFilter(path, filter))
                {
                    return true;
                }
            }
            return false;
        }

        private object GetPathNode(object[] path)
        {
            Dictionary<string, object> currentDictionary = _patch;
            List<object> currentList = null;

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
                        else if (child is List<object> childList)
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
                else if (pathItem is Model.ItemPathNode itemPathNode)
                {
                    if (currentDictionary.TryGetValue(itemPathNode.Name, out object nodeList))
                    {
                        currentList = (List<object>)nodeList;
                    }
                    else
                    {
                        currentList = new List<object>(itemPathNode.Count);
                        currentDictionary.Add(itemPathNode.Name, currentList);
                    }

                    for (int k = currentList.Count; k > itemPathNode.Count; k--)
                    {
                        currentList.RemoveAt(k - 1);
                    }

                    for (int k = currentList.Count; k < itemPathNode.Count; k++)
                    {
                        currentList.Add(new Dictionary<string, object>());
                    }

                    currentDictionary = (Dictionary<string, object>)currentList[itemPathNode.Index];
                }
            }

            if (currentDictionary != null)
            {
                return currentDictionary;
            }
            return currentList;
        }

        private void MachineModelPropertyChanged(object[] path, Model.PropertyPathChangeType pathType, object value)
        {
            if (!CheckFilter(path))
            {
                return;
            }

            lock (_patch)
            {
                try
                {
                    object node = GetPathNode(path);
                    switch (pathType)
                    {
                        case Model.PropertyPathChangeType.Property:
                            // Set new property value
                            Dictionary<string, object> propertyNode = (Dictionary<string, object>)node;
                            string propertyName = (string)path[^1];
                            propertyNode[propertyName] = value;
                            break;

                        case Model.PropertyPathChangeType.ObjectCollection:
                            // Update number of object collection items
                            Dictionary<string, object> objectCollectionNode = (Dictionary<string, object>)node;
                            string objectCollectionName = (string)path[^1];
                            List<object> objectCollectionList;
                            if (objectCollectionNode.TryGetValue(objectCollectionName, out object objectCollection))
                            {
                                objectCollectionList = (List<object>)objectCollection;
                            }
                            else
                            {
                                objectCollectionList = new List<object>((int)value);
                                objectCollectionNode.Add(objectCollectionName, objectCollectionList);
                            }

                            for (int k = objectCollectionList.Count; k > (int)value; k--)
                            {
                                objectCollectionList.RemoveAt(k - 1);
                            }

                            for (int k = objectCollectionList.Count; k < (int)value; k++)
                            {
                                objectCollectionList.Add(new Dictionary<string, object>());
                            }
                            break;

                        case Model.PropertyPathChangeType.ValueCollection:
                            // Set new value list
                            if (node is Dictionary<string, object> valueNode)
                            {
                                string valueName = (string)path[^1];
                                valueNode[valueName] = value;
                            }
                            else if (node is List<object> valueCollection)
                            {
                                int index = (int)path[^1];
                                valueCollection[index] = value;
                            }
                            else
                            {
                                throw new ArgumentException($"Invalid value list type {node.GetType().Name}");
                            }
                            break;

                        case Model.PropertyPathChangeType.GrowingCollection:
                            // Add new items or clear list
                            if (node is Dictionary<string, object> parent)
                            {
                                string collectionName = (string)path[^1];
                                if (value == null && collectionName.Equals(nameof(MachineModel.Messages), StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // Don't clear messages - they are volatile anyway
                                    break;
                                }

                                List<object> growingCollection;
                                if (parent.TryGetValue(collectionName, out object obj))
                                {
                                    growingCollection = (List<object>)obj;
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
                    Console.WriteLine($"Failed to record {string.Join('/', path)} = {value} ({pathType}):");
                    Console.WriteLine(e);
                }
            }
        }

        /// <summary>
        /// Called to notify the subscribers about a model update
        /// </summary>
        public static void ModelUpdated()
        {
            lock (_subscriptions)
            {
                foreach (Subscription subscription in _subscriptions)
                {
                    // Inform every subscriber about new data
                    subscription._updateAvailableEvent.Set();
                }
            }
        }
    }
}
