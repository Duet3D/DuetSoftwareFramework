﻿using DuetAPI.Machine;
using DuetControlServer.Files;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Provides functions for filament management
    /// </summary>
    public static class FilamentManager
    {
        /// <summary>
        /// Name of the filament storage file
        /// </summary>
        private const string FilamentsCsvFile = "filaments.csv";

        /// <summary>
        /// First line identifying the filament file
        /// </summary>
        private const string FilamentsCsvHeader = "RepRapFirmware filament assignment file v1";

        /// <summary>
        /// Lock for this class
        /// </summary>
        private static readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Mapping of extruder vs filament
        /// </summary>
        private static readonly Dictionary<int, string> _filamentMapping = new Dictionary<int, string>();

        /// <summary>
        /// Initialize this class
        /// </summary>
        public static void Init()
        {
            string filename = FilePath.ToPhysicalAsync(FilamentsCsvFile, FileDirectory.System).Result;
            if (File.Exists(filename))
            {
                using FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read);
                using StreamReader reader = new StreamReader(fs);

                string line = reader.ReadLine();
                if (line != null && line.StartsWith(FilamentsCsvHeader))
                {
                    // Second line holds the CSV column headers...
                    _ = reader.ReadLine();

                    // This is then followed by the actual mapping
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] args = line.Split(',');
                        if (args.Length == 2 && int.TryParse(args[0], out int extruder) && extruder >= 0)
                        {
                            _filamentMapping.Add(extruder, args[1]);
                        }
                    }
                }
            }

            Model.Provider.Get.Move.Extruders.CollectionChanged += Extruders_CollectionChanged;
        }

        /// <summary>
        /// Called when the extruders have changed
        /// </summary>
        /// <param name="sender">Extruder list</param>
        /// <param name="e">Event arguments</param>
        private static void Extruders_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            using (_lock.Lock())
            {
                if (e.OldItems != null)
                {
                    foreach (object extruderObject in e.OldItems)
                    {
                        if (extruderObject is Extruder extruder)
                        {
                            extruder.PropertyChanged -= ExtruderPropertyChanged;
                        }
                    }
                }

                if (e.NewItems != null)
                {
                    foreach (object extruderObject in e.NewItems)
                    {
                        if (extruderObject is Extruder extruder)
                        {
                            int extruderDrive = Model.Provider.Get.Move.Extruders.IndexOf(extruder);
                            if (_filamentMapping.TryGetValue(extruderDrive, out string filamentName) && extruder.Filament != filamentName)
                            {
                                // Tell RepRapFirmware about the loaded filament
                                SPI.Interface.AssignFilament(extruderDrive, filamentName);
                            }
                            extruder.PropertyChanged += ExtruderPropertyChanged;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Called when a tool property has changed
        /// </summary>
        /// <param name="sender">Changed tool</param>
        /// <param name="e">Information about the changed property</param>
        private static void ExtruderPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Extruder.Filament))
            {
                Extruder extruder = (Extruder)sender;
                using (_lock.Lock())
                {
                    int extruderIndex = Model.Provider.Get.Move.Extruders.IndexOf(extruder);
                    if (!_filamentMapping.TryGetValue(extruderIndex, out string filament) || filament != extruder.Filament)
                    {
                        _filamentMapping[extruderIndex] = extruder.Filament;
                        SaveMapping();
                    }
                }
            }
        }

        /// <summary>
        /// Save the filament mapping to a file
        /// </summary>
        private static async void SaveMapping()
        {
            using (await _lock.LockAsync())
            {
                string filename = await FilePath.ToPhysicalAsync(FilamentsCsvFile, FileDirectory.System);
                using FileStream fs = new FileStream(filename, FileMode.Create, FileAccess.Write);
                using StreamWriter writer = new StreamWriter(fs);

                writer.WriteLine($"{FilamentsCsvHeader} generated at {DateTime.Now:yyyy-MM-dd HH:mm}");
                writer.WriteLine("extruder,filament");
                foreach (var pair in _filamentMapping)
                {
                    await writer.WriteLineAsync($"{pair.Key},{pair.Value}");
                }
            }
        }
    }
}
