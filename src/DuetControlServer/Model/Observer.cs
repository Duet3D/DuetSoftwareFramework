using DuetAPI.Machine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Node of an object model path pointing to a list item
    /// </summary>
    public class ItemPathNode
    {
        /// <summary>
        /// Name of the list
        /// </summary>
        public string Name;

        /// <summary>
        /// Index of the item
        /// </summary>
        public int Index;

        /// <summary>
        /// Count of the list owning this item
        /// </summary>
        public int Count;

        /// <summary>
        /// Convert an item node to a string (for debugging)
        /// </summary>
        /// <returns>String representation of this node</returns>
        public override string ToString() => $"{Name}[{Index} of {Count}]";
    }

    /// <summary>
    /// Type of path modification
    /// </summary>
    public enum PropertyPathChangeType
    {
        /// <summary>
        /// Property has changed
        /// </summary>
        /// <remarks>Value is the property value</remarks>
        Property,

        /// <summary>
        /// Object collection has changed (e.g. tools)
        /// </summary>
        /// <remarks>Value is the number of new items</remarks>
        ObjectCollection,

        /// <summary>
        /// Value collection has changed (e.g. heater temperatures)
        /// </summary>
        /// <remarks>Value is the collection itself</remarks>
        ValueCollection,

        /// <summary>
        /// Growing collection has changed (messages or job layers)
        /// </summary>
        /// <remarks>If value is null, the list has been cleared, else only the added items are passed</remarks>
        GrowingCollection
    }

    /// <summary>
    /// Delegate to call when a property is being changed
    /// </summary>
    /// <param name="path">Path to the value that changed</param>
    /// <param name="changeType">Type of the modification</param>
    /// <param name="value">New value</param>
    public delegate void PropertyPathChanged(object[] path, PropertyPathChangeType changeType, object value);

    /// <summary>
    /// Static class that observes the main machine model and calls an event whenever a deep value has changed
    /// </summary>
    public static class Observer
    {
        /// <summary>
        /// Event to call when a deep value has changed
        /// </summary>
        public static event PropertyPathChanged OnPropertyPathChanged;

        /// <summary>
        /// Initializes the observer to keep track of deep changes in the object model
        /// </summary>
        public static void Init()
        {
            Provider.Get.Channels.AutoPause.PropertyChanged += PropertyChanged("channels", "autoPause");
            Provider.Get.Channels.AUX.PropertyChanged += PropertyChanged("channels", "aux");
            Provider.Get.Channels.CodeQueue.PropertyChanged += PropertyChanged("channels", "codeQueue");
            Provider.Get.Channels.Daemon.PropertyChanged += PropertyChanged("channels", "daemon");
            Provider.Get.Channels.File.PropertyChanged += PropertyChanged("channels", "file");
            Provider.Get.Channels.HTTP.PropertyChanged += PropertyChanged("channels", "http");
            Provider.Get.Channels.LCD.PropertyChanged += PropertyChanged("channels", "lcd");
            Provider.Get.Channels.SPI.PropertyChanged += PropertyChanged("channels", "spi");
            Provider.Get.Channels.Telnet.PropertyChanged += PropertyChanged("channels", "telnet");
            Provider.Get.Channels.USB.PropertyChanged += PropertyChanged("channels", "usb");

            Provider.Get.Electronics.PropertyChanged += PropertyChanged("electronics");
            Provider.Get.Electronics.Firmware.PropertyChanged += PropertyChanged("electronics", "firmware");
            Provider.Get.Electronics.VIn.PropertyChanged += PropertyChanged("electronics", "vIn");
            Provider.Get.Electronics.McuTemp.PropertyChanged += PropertyChanged("electronics", "mcuTemp");
            Provider.Get.Electronics.ExpansionBoards.CollectionChanged += ObjectCollectionChanged("electronics", "expansionBoards");

            Provider.Get.Fans.CollectionChanged += ObjectCollectionChanged("fans");

            Provider.Get.Heat.PropertyChanged += PropertyChanged("heat");
            Provider.Get.Heat.Beds.CollectionChanged += ObjectCollectionChanged("heat", "beds");
            Provider.Get.Heat.Chambers.CollectionChanged += ObjectCollectionChanged("heat", "chambers");
            Provider.Get.Heat.Extra.CollectionChanged += ObjectCollectionChanged("heat", "extra");
            Provider.Get.Heat.Heaters.CollectionChanged += ObjectCollectionChanged("heat", "heaters");

            Provider.Get.HttpEndpoints.CollectionChanged += ObjectCollectionChanged("httpEndpoints");

            Provider.Get.Job.PropertyChanged += PropertyChanged("job");
            Provider.Get.Job.ExtrudedRaw.CollectionChanged += ValueCollectionChanged("job", "extrudedRaw");
            Provider.Get.Job.File.PropertyChanged += PropertyChanged("job", "file");
            Provider.Get.Job.Layers.CollectionChanged += GrowingCollectionChanged("job", "layers");
            Provider.Get.Job.TimesLeft.PropertyChanged += PropertyChanged("job", "timesLeft");

            Provider.Get.Lasers.CollectionChanged += ObjectCollectionChanged("lasers");

            Provider.Get.MessageBox.PropertyChanged += PropertyChanged("messageBox");
            Provider.Get.MessageBox.AxisControls.CollectionChanged += ValueCollectionChanged("messageBox", "axisControls");

            Provider.Get.Messages.CollectionChanged += GrowingCollectionChanged("messages");

            Provider.Get.Move.PropertyChanged += PropertyChanged("move");
            Provider.Get.Move.CurrentMove.PropertyChanged += PropertyChanged("move", "currentMove");
            Provider.Get.Move.Geometry.PropertyChanged += PropertyChanged("move", "geometry");
            Provider.Get.Move.Geometry.Anchors.CollectionChanged += ValueCollectionChanged("move", "geometry", "anchors");
            Provider.Get.Move.Geometry.Diagonals.CollectionChanged += ValueCollectionChanged("move", "geometry", "diagonals");
            Provider.Get.Move.Geometry.EndstopAdjustments.CollectionChanged += ValueCollectionChanged("move", "geometry", "endstopAdjustments");
            Provider.Get.Move.Geometry.Tilt.CollectionChanged += ValueCollectionChanged("move", "geometry", "tilt");
            Provider.Get.Move.Axes.CollectionChanged += ObjectCollectionChanged("move", "axes");
            Provider.Get.Move.Extruders.CollectionChanged += ObjectCollectionChanged("move", "extruders");
            Provider.Get.Move.Drives.CollectionChanged += ObjectCollectionChanged("move", "drives");
            Provider.Get.Move.WorkplaceCoordinates.CollectionChanged += ValueCollectionChanged("move", "workplaceCoordinates");

            Provider.Get.Network.PropertyChanged += PropertyChanged("network");
            Provider.Get.Network.Interfaces.CollectionChanged += ObjectCollectionChanged("network", "interfaces");

            Provider.Get.Scanner.PropertyChanged += PropertyChanged("scanner");

            Provider.Get.Sensors.Endstops.CollectionChanged += ObjectCollectionChanged("sensors", "endstops");
            Provider.Get.Sensors.Probes.CollectionChanged += ObjectCollectionChanged("sensors", "probes");

            Provider.Get.Spindles.CollectionChanged += ObjectCollectionChanged("spindles");

            Provider.Get.State.PropertyChanged += PropertyChanged("state");
            Provider.Get.State.Beep.PropertyChanged += PropertyChanged("state", "beep");

            Provider.Get.Storages.CollectionChanged += ObjectCollectionChanged("storages");

            Provider.Get.Tools.CollectionChanged += ObjectCollectionChanged("tools");

            Provider.Get.UserVariables.CollectionChanged += DictionaryChanged<string, string>("userVariables");

            Provider.Get.UserSessions.CollectionChanged += ObjectCollectionChanged("userSessions");
        }

        private static PropertyChangedEventHandler PropertyChanged(params object[] path)
        {
            return (sender, e) =>
            {
                string propertyName = JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName);
                object value = sender.GetType().GetProperty(e.PropertyName).GetValue(sender);
                OnPropertyPathChanged?.Invoke(AddToPath(path, propertyName), PropertyPathChangeType.Property, value);
            };
        }

        private static NotifyCollectionChangedEventHandler ValueCollectionChanged(params object[] path)
        {
            return (sender, e) =>
            {
                IList senderList = (IList)sender;
                OnPropertyPathChanged?.Invoke(path, PropertyPathChangeType.ValueCollection, senderList);
            };
        }

        private static NotifyCollectionChangedEventHandler ObjectCollectionChanged(params object[] path)
        {
            return (sender, e) =>
            {
                IList senderList = (IList)sender;
                OnPropertyPathChanged?.Invoke(path, PropertyPathChangeType.ObjectCollection, senderList.Count);

                if (e.OldItems != null)
                {
                    foreach (object oldItem in e.OldItems)
                    {
                        UnregisterItemPropertyChanged(oldItem);
                        if (oldItem is ExpansionBoard board)
                        {
                            UnregisterItemPropertyChanged(board.Firmware);
                            UnregisterItemPropertyChanged(board.VIn);
                            UnregisterItemPropertyChanged(board.McuTemp);
                        }
                        else if (oldItem is Fan fan)
                        {
                            UnregisterItemPropertyChanged(fan.Thermostatic);
                        }
                        else if (oldItem is BedOrChamber bedOrChamber)
                        {
                            UnregisterItemPropertyChanged(bedOrChamber.Heaters);
                            UnregisterItemPropertyChanged(bedOrChamber.Active);
                            UnregisterItemPropertyChanged(bedOrChamber.Standby);
                        }
                        else if (oldItem is Heater heater)
                        {
                            UnregisterItemPropertyChanged(heater.Model);
                        }
                        else if (oldItem is Axis axis)
                        {
                            UnregisterItemPropertyChanged(axis.Drives);
                        }
                        else if (oldItem is Extruder extruder)
                        {
                            UnregisterItemPropertyChanged(extruder.Drives);
                        }
                        else if (oldItem is Drive drive)
                        {
                            UnregisterItemPropertyChanged(drive.Microstepping);
                        }
                        else if (oldItem is Probe probe)
                        {
                            UnregisterItemPropertyChanged(probe.SecondaryValues);
                            UnregisterItemPropertyChanged(probe.Offsets);
                        }
                        else if (oldItem is Tool tool)
                        {
                            UnregisterItemPropertyChanged(tool.Active);
                            UnregisterItemPropertyChanged(tool.Standby);
                            UnregisterItemPropertyChanged(tool.Fans);
                            UnregisterItemPropertyChanged(tool.Heaters);
                            UnregisterItemPropertyChanged(tool.Extruders);
                            UnregisterItemPropertyChanged(tool.Mix);
                            UnregisterItemPropertyChanged(tool.Axes);
                            UnregisterItemPropertyChanged(tool.Offsets);
                        }
                    }
                }

                if (e.NewItems != null)
                {
                    foreach (object newItem in e.NewItems)
                    {
                        int index = senderList.IndexOf(newItem);
                        RegisterItemPropertyChanged(path, newItem, index, senderList);
                        if (newItem is ExpansionBoard board)
                        {
                            RegisterItemPropertyChanged(path, board.Firmware, index, senderList, "firmware");
                            RegisterItemPropertyChanged(path, board.Firmware, index, senderList, "vIn");
                            RegisterItemPropertyChanged(path, board.Firmware, index, senderList, "mcuTemp");
                        }
                        else if (newItem is Fan fan)
                        {
                            RegisterItemPropertyChanged(path, fan.Thermostatic, index, senderList, "thermostatic");
                        }
                        else if (newItem is BedOrChamber bedOrChamber)
                        {
                            RegisterItemPropertyChanged(path, bedOrChamber.Active, index, senderList, "active");
                            RegisterItemPropertyChanged(path, bedOrChamber.Standby, index, senderList, "standby");
                            RegisterItemPropertyChanged(path, bedOrChamber.Heaters, index, senderList, "heaters");
                        }
                        else if (newItem is Heater heater)
                        {
                            RegisterItemPropertyChanged(path, heater.Model, index, senderList, "model");
                        }
                        else if (newItem is Axis axis)
                        {
                            RegisterItemPropertyChanged(path, axis.Drives, index, senderList, "drives");
                        }
                        else if (newItem is Extruder extruder)
                        {
                            RegisterItemPropertyChanged(path, extruder.Drives, index, senderList, "drives");
                        }
                        else if (newItem is Drive drive)
                        {
                            RegisterItemPropertyChanged(path, drive.Microstepping, index, senderList, "microstepping");
                        }
                        else if (newItem is Probe probe)
                        {
                            RegisterItemPropertyChanged(path, probe.SecondaryValues, index, senderList, "secondaryValues");
                            RegisterItemPropertyChanged(path, probe.Offsets, index, senderList, "offsets");
                        }
                        else if (newItem is Tool tool)
                        {
                            RegisterItemPropertyChanged(path, tool.Active, index, senderList, "active");
                            RegisterItemPropertyChanged(path, tool.Standby, index, senderList, "standby");
                            RegisterItemPropertyChanged(path, tool.Fans, index, senderList, "fans");
                            RegisterItemPropertyChanged(path, tool.Heaters, index, senderList, "heaters");
                            RegisterItemPropertyChanged(path, tool.Extruders, index, senderList, "extruders");
                            RegisterItemPropertyChanged(path, tool.Mix, index, senderList, "mix");
                            RegisterItemPropertyChanged(path, tool.Axes, index, senderList, "axes");
                            RegisterItemPropertyChanged(path, tool.Offsets, index, senderList, "offsets");
                        }
                    }
                }
            };
        }

        private static NotifyCollectionChangedEventHandler GrowingCollectionChanged(params object[] path)
        {
            return (sender, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    OnPropertyPathChanged?.Invoke(path, PropertyPathChangeType.GrowingCollection, e.NewItems);
                }
                else if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    OnPropertyPathChanged?.Invoke(path, PropertyPathChangeType.GrowingCollection, null);
                }
            };
        }

        private static NotifyCollectionChangedEventHandler DictionaryChanged<Ta, Tb>(params object[] path)
        {
            return (sender, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace)
                {
                    foreach (var item in e.NewItems)
                    {
                        KeyValuePair<Ta, Tb> kv = (KeyValuePair<Ta, Tb>)item;
                        OnPropertyPathChanged?.Invoke(AddToPath(path, kv.Key), PropertyPathChangeType.Property, kv.Value);
                    }
                }
                else if (e.Action == NotifyCollectionChangedAction.Remove || e.Action == NotifyCollectionChangedAction.Reset)
                {
                    foreach (var item in e.OldItems)
                    {
                        KeyValuePair<Ta, Tb> kv = (KeyValuePair<Ta, Tb>)item;
                        OnPropertyPathChanged?.Invoke(AddToPath(path, kv.Key), PropertyPathChangeType.Property, null);
                    }
                }
            };
        }

        private static object[] AddToPath(object[] path, params object[] toAdd)
        {
            object[] newPath = new object[path.Length + toAdd.Length];
            path.CopyTo(newPath, 0);
            toAdd.CopyTo(newPath, path.Length);
            return newPath;
        }

        private static readonly Dictionary<object, NotifyCollectionChangedEventHandler> _collectionChangedHandlers = new Dictionary<object, NotifyCollectionChangedEventHandler>();
        private static readonly Dictionary<object, PropertyChangedEventHandler> _propertyChangedHandlers = new Dictionary<object, PropertyChangedEventHandler>();

        private static void RegisterItemPropertyChanged(object[] path, object item, int index, IList list, params object[] subPath)
        {
            // Whenever a change occurs, we need to know the item index as well as the current list count
            object[] fullPath = (subPath.Length > 0) ? AddToPath(path, subPath) : (object[])path.Clone();
            ItemPathNode itemPathNode = new ItemPathNode
            {
                Name = (string)path[^1],
                Index = index,
                Count = list.Count
            };
            fullPath[path.Length - 1] = itemPathNode;

            // Subscribe to changes
            if (item is INotifyCollectionChanged ncc)
            {
                void collectionChangeHandler(object sender, NotifyCollectionChangedEventArgs e)
                {
                    itemPathNode.Count = list.Count;
                    OnPropertyPathChanged?.Invoke(fullPath, PropertyPathChangeType.ValueCollection, sender);
                }
                ncc.CollectionChanged += collectionChangeHandler;
                _collectionChangedHandlers.Add(item, collectionChangeHandler);
            }
            else if (item is INotifyPropertyChanged npc)
            {
                void propertyChangeHandler(object sender, PropertyChangedEventArgs e)
                {
                    itemPathNode.Count = list.Count;
                    string propertyName = JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName);
                    object value = sender.GetType().GetProperty(e.PropertyName).GetValue(sender);
                    OnPropertyPathChanged?.Invoke(AddToPath(fullPath, propertyName), PropertyPathChangeType.Property, value);
                }
                npc.PropertyChanged += propertyChangeHandler;
                _propertyChangedHandlers.Add(item, propertyChangeHandler);
            }
            else
            {
                throw new ArgumentException("Item type implements neither INotifyPropertyChanged nor INotifyCollectionChanged");
            }
        }

        private static void UnregisterItemPropertyChanged(object item)
        {
            if (item is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged -= _collectionChangedHandlers[item];
                _collectionChangedHandlers.Remove(item);
            }
            else if (item is INotifyPropertyChanged npc)
            {
                npc.PropertyChanged -= _propertyChangedHandlers[item];
                _propertyChangedHandlers.Remove(item);
            }
            else
            {
                throw new ArgumentException("Item type implements neither INotifyPropertyChanged nor INotifyCollectionChanged");
            }
        }
    }
}
