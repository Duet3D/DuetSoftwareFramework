using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Model;
using DuetControlServer.SPI.Communication.Shared;

namespace DuetControlServer.IPC.Processors
{
    /// <summary>
    /// Subscription processor that notifies clients about object model changes.
    /// There is no point in deserializing the object model here so only the JSON representation is kept here.
    /// </summary>
    public sealed class ModelSubscription : Base
    {
        /// <summary>
        /// List of supported commands in this mode
        /// </summary>
        public static readonly Type[] SupportedCommands =
        {
            typeof(Acknowledge)
        };

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static ModelSubscription() => AddSupportedCommands(SupportedCommands);

        /// <summary>
        /// List of active subscribers
        /// </summary>
        private static readonly List<ModelSubscription> _subscriptions = new();

        /// <summary>
        /// Check if there are any clients waiting for generic messages
        /// </summary>
        public static bool HasClientsWaitingForMessages
        {
            get
            {
                lock (_subscriptions)
                {
                    foreach (ModelSubscription subscription in _subscriptions)
                    {
                        if (subscription._mode == SubscriptionMode.Full || subscription._channel == null)
                        {
                            return true;
                        }

                        MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)subscription._channel);
                        if (MessageTypeFlags.GenericMessage.HasFlag(channelFlag))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Mode of this subscriber
        /// </summary>
        private readonly SubscriptionMode _mode;

        /// <summary>
        /// Optional code channel or null if only generic messages are supposed to be recorded
        /// </summary>
        private readonly CodeChannel? _channel;

        /// <summary>
        /// List of filters (in Patch mode)
        /// </summary>
        private readonly object[][] _filters;

        /// <summary>
        /// Dictionary of updated fields (in Patch mode)
        /// </summary>
        private readonly Dictionary<string, object> _patch = new();

        /// <summary>
        /// Memory stream holding the JSON patch in UTF-8 format
        /// </summary>
        private readonly MemoryStream _patchStream = new();
        
        /// <summary>
        /// Constructor of the subscription processor
        /// </summary>
        /// <param name="conn">Connection instance</param>
        /// <param name="initMessage">Initialization message</param>
        public ModelSubscription(Connection conn, ClientInitMessage initMessage) : base(conn)
        {
            SubscribeInitMessage subscribeInitMessage = (SubscribeInitMessage)initMessage;
            _mode = subscribeInitMessage.SubscriptionMode;
            _channel = subscribeInitMessage.Channel;
            if (subscribeInitMessage.Filters != null)
            {
                _filters = Filter.ConvertFilters(subscribeInitMessage.Filters);
            }
#pragma warning disable CS0618 // Type or member is obsolete
            else if (!string.IsNullOrEmpty(subscribeInitMessage.Filter))
            {
                _filters = Filter.ConvertFilters(subscribeInitMessage.Filter);
            }
#pragma warning restore CS0618 // Type or member is obsolete
            else
            {
                _filters = Array.Empty<object[]>();
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
                    if (_mode == SubscriptionMode.Full || _filters.Length == 0)
                    {
                        jsonData = JsonSerializer.SerializeToUtf8Bytes(Provider.Get, JsonHelper.DefaultJsonOptions);
                    }
                    else
                    {
                        Dictionary<string, object> patchModel = new();
                        foreach (object[] filter in _filters)
                        {
                            Dictionary<string, object> partialModel = Filter.GetFiltered(filter);
                            Filter.MergeFiltered(patchModel, partialModel);
                        }

                        _patchStream.SetLength(0);
                        SerializeJsonChanges(patchModel);
                        jsonData = _patchStream.ToArray();
                    }
                }

                BaseCommand command;
                Type commandType;
                do
                {
                    // Send new JSON data
                    if (jsonData != null)
                    {
                        await Connection.Send(jsonData);
                        jsonData = null;

                        // Wait for an acknowledgement from the client
                        command = await Connection.ReceiveCommand();
                        commandType = command.GetType();

                        // Make sure the command is supported and permitted
                        if (!SupportedCommands.Contains(commandType))
                        {
                            throw new ArgumentException($"Invalid command {command.Command} (wrong mode?)");
                        }
                        Connection.CheckPermissions(commandType);
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
                                _patchStream.SetLength(0);
                                SerializeJsonChanges(_patch);
                                _patch.Clear();
                                jsonData = _patchStream.ToArray();
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
            if (_filters.Length == 0 || path.Length == 0)
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
        /// Get the dictionary or list object using a path node to record properties
        /// </summary>
        /// <param name="root">Root dictionary</param>
        /// <param name="path">Path node</param>
        /// <returns>Item at the given path</returns>
        public static object GetPathNode(Dictionary<string, object> root, object[] path)
        {
            Dictionary<string, object> currentDictionary = root;
            List<object> currentList = null;

            for (int i = 0; i < path.Length - 1; i++)
            {
                object pathItem = path[i];
                if (pathItem is string pathString)
                {
                    // Get the requested dictionary
                    if (currentDictionary.TryGetValue(pathString, out object child))
                    {
                        if (child is Dictionary<string, object> childDictionary)
                        {
                            currentDictionary = childDictionary;
                        }
                        else
                        {
                            // Stop here if the node type is unsupported
                            return null;
                        }
                    }
                    else
                    {
                        Dictionary<string, object> newNode = new();
                        currentDictionary.Add(pathString, newNode);
                        currentDictionary = newNode;
                    }
                    currentList = null;
                }
                else if (pathItem is ItemPathNode pathNode)
                {
                    // Get the requested list
                    if (currentDictionary.TryGetValue(pathNode.Name, out object nodeObject))
                    {
                        if (nodeObject is List<object> nodeList)
                        {
                            for (int k = nodeList.Count; k > pathNode.List.Count; k--)
                            {
                                nodeList.RemoveAt(k - 1);
                            }
                            currentList = nodeList;
                        }
                        else
                        {
                            // Stop here if the node type is unsupported
                            return null;
                        }
                    }
                    else
                    {
                        currentList = new List<object>(pathNode.List.Count);
                        currentDictionary.Add(pathNode.Name, currentList);
                    }

                    // Add missing items to the current list
                    bool itemsAreObjects = path[i + 1] is string;
                    for (int k = currentList.Count; k < pathNode.List.Count; k++)
                    {
                        if (pathNode.List[k] == null)
                        {
                            currentList.Add(null);
                        }
                        else if (itemsAreObjects)
                        {
                            currentList.Add(new Dictionary<string, object>());
                        }
                        else
                        {
                            currentList.Add(new List<object>());
                        }
                    }

                    // Try to move on to the next node
                    nodeObject = currentList[pathNode.Index];
                    if (nodeObject is Dictionary<string, object> nextDictionary)
                    {
                        currentDictionary = nextDictionary;
                        currentList = null;
                    }
                    else if (nodeObject is List<object> nextList)
                    {
                        currentDictionary = null;
                        currentList = nextList;
                    }
                    else
                    {
                        // Stop here if the node type is unsupported
                        return null;
                    }
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
                    object node = GetPathNode(_patch, path);
                    if (node == null)
                    {
                        // Skip this update if the underlying object is about to be fully transferred anyway
                        return;
                    }

                    switch (changeType)
                    {
                        case PropertyChangeType.Property:
                            // Set new property value
                            if (node is Dictionary<string, object> propertyNode)
                            {
                                string propertyName = (string)path[^1];
                                propertyNode[propertyName] = value;
                            }
                            break;

                        case PropertyChangeType.Collection:
                            // Update a collection
                            ItemPathNode pathNode = (ItemPathNode)path[^1];
                            if (node is not List<object> objectCollectionList)
                            {
                                if (pathNode.Name.Equals(nameof(ObjectModel.Job.Layers)) && Connection.ApiVersion < 11)
                                {
                                    // Don't record job.layers[] any more; the data type has changed and sending it to outdated clients would result in memory leaks
                                    break;
                                }

                                Dictionary<string, object> objectCollectionNode = (Dictionary<string, object>)node;
                                if (objectCollectionNode.TryGetValue(pathNode.Name, out object objectCollection))
                                {
                                    objectCollectionList = (List<object>)objectCollection;

                                    for (int k = objectCollectionList.Count; k > pathNode.List.Count; k--)
                                    {
                                        objectCollectionList.RemoveAt(k - 1);
                                    }
                                }
                                else
                                {
                                    objectCollectionList = new List<object>(pathNode.List.Count);
                                    objectCollectionNode.Add(pathNode.Name, objectCollectionList);
                                }
                            }

                            Type itemType = (value is IList) ? typeof(List<object>) : typeof(Dictionary<string, object>);
                            for (int k = objectCollectionList.Count; k < pathNode.List.Count; k++)
                            {
                                if (pathNode.List[k] == null)
                                {
                                    objectCollectionList.Add(null);
                                }
                                else
                                {
                                    object newItem = Activator.CreateInstance(itemType);
                                    objectCollectionList.Add(newItem);
                                }
                            }

                            if (pathNode.Index < pathNode.List.Count)
                            {
                                objectCollectionList[pathNode.Index] = value;
                            }
                            break;

                        case PropertyChangeType.GrowingCollection:
                            // Add new items or clear list
                            if (node is Dictionary<string, object> parent)
                            {
                                string collectionName = (string)path[^1];
                                if (collectionName.Equals(nameof(ObjectModel.Messages), StringComparison.InvariantCultureIgnoreCase))
                                {
                                    if (value == null)
                                    {
                                        // Don't clear messages - they are volatile anyway
                                        break;
                                    }

                                    if (_channel != null)
                                    {
                                        MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)_channel);
                                        if (!MessageTypeFlags.GenericMessage.HasFlag(channelFlag))
                                        {
                                            // This message isn't meant for this subscriber, skip it
                                            break;
                                        }
                                    }
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

        /// <summary>
        /// Record a new message based on the message flags
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="message"></param>
        public static void RecordMessage(MessageTypeFlags flags, Message message)
        {
            lock (_subscriptions)
            {
                foreach (ModelSubscription subscription in _subscriptions)
                {
                    if (subscription._mode == SubscriptionMode.Patch && subscription._channel != null)
                    {
                        MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)subscription._channel);
                        if (flags.HasFlag(channelFlag))
                        {
                            subscription.RecordMessage(message);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Record a new message
        /// </summary>
        /// <param name="message"></param>
        private void RecordMessage(Message message)
        {
            MachineModelPropertyChanged(new object[] { "messages" }, PropertyChangeType.GrowingCollection, new Message[] { message });
        }

        /// <summary>
        /// Write JSON changes to the JSON memory stream
        /// </summary>
        /// <param name="changes">Changes to write</param>
        /// <returns></returns>
        private void SerializeJsonChanges(Dictionary<string, object> changes)
        {
            using Utf8JsonWriter writer = new(_patchStream);
            void WriteData(Dictionary<string, object> data)
            {
                writer.WriteStartObject();
                foreach (var kv in data)
                {
                    writer.WritePropertyName(kv.Key);
                    if (kv.Value is Dictionary<string, object> childNode)
                    {
                        WriteData(childNode);
                    }
                    else
                    {
                        JsonSerializer.Serialize(writer, kv.Value, JsonHelper.DefaultJsonOptions);
                    }
                }
                writer.WriteEndObject();
            }
            WriteData(changes);
        }
    }
}
