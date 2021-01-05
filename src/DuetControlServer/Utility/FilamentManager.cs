using DuetAPI.ObjectModel;
using DuetControlServer.FileExecution;
using DuetControlServer.Files;
using DuetControlServer.Model;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

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
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

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
        /// <returns>Asynchronous task</returns>
        public static async Task Init()
        {
            if (Settings.NoSpi)
            {
                // Do not deal with filament mapping if no SPI task is running
                return;
            }

            string filename = await FilePath.ToPhysicalAsync(FilamentsCsvFile, FileDirectory.System);
            if (File.Exists(filename))
            {
                using FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read);
                using StreamReader reader = new StreamReader(fs);

                string line = reader.ReadLine();
                if (line != null && line.StartsWith(FilamentsCsvHeader))
                {
                    // Second line holds the CSV column headers...
                    _ = await reader.ReadLineAsync();

                    // This is then followed by the actual mapping
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        string[] args = line.Split(',');
                        if (args.Length == 2 && int.TryParse(args[0], out int extruder) && extruder >= 0)
                        {
                            _filamentMapping.Add(extruder, args[1]);
                        }
                    }
                }
            }

            Provider.Get.Move.Extruders.CollectionChanged += Extruders_CollectionChanged;
        }

        /// <summary>
        /// Called when the extruders have changed
        /// </summary>
        /// <param name="sender">Extruder list</param>
        /// <param name="e">Event arguments</param>
        private static void Extruders_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            using (_lock.Lock(Program.CancellationToken))
            {
                if (e.OldItems != null)
                {
                    foreach (object extruderObject in e.OldItems)
                    {
                        if (extruderObject is Extruder extruder)
                        {
                            // Extruder removed
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
                            int extruderIndex = Provider.Get.Move.Extruders.IndexOf(extruder);
                            if (_filamentMapping.TryGetValue(extruderIndex, out string filament) && extruder.Filament != filament)
                            {
                                // Extruder added. Tell RepRapFirmware about the loaded filament
                                _logger.Debug("Assigning filament {0} to extruder drive {1}", filament, extruderIndex);
                                SPI.Interface.AssignFilament(extruderIndex, filament);
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
                using (_lock.Lock(Program.CancellationToken))
                {
                    int extruderIndex = Provider.Get.Move.Extruders.IndexOf(extruder);
                    if (!_filamentMapping.TryGetValue(extruderIndex, out string filament) || filament != extruder.Filament)
                    {
                        if (!string.IsNullOrEmpty(filament) && Macro.RunningConfig)
                        {
                            // Booting RRF, tell it about the loaded filament
                            _logger.Debug("Assigning filament {0} to extruder drive {1}", filament, extruderIndex);
                            SPI.Interface.AssignFilament(extruderIndex, filament);
                        }
                        else
                        {
                            // Filament changed
                            _logger.Debug("Filament {0} has been assigned to extruder drive {1}", extruder.Filament, extruderIndex);
                            _filamentMapping[extruderIndex] = extruder.Filament;
                            SaveMapping();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Send the current filament mapping to RRF
        /// </summary>
        public static void RefreshMapping()
        {
            lock (_filamentMapping)
            {
                foreach (int extruder in _filamentMapping.Keys)
                {
                    SPI.Interface.AssignFilament(extruder, _filamentMapping[extruder]);
                }
            }
        }

        /// <summary>
        /// Save the filament mapping to a file
        /// </summary>
        private static async void SaveMapping()
        {
            using (await _lock.LockAsync(Program.CancellationToken))
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
