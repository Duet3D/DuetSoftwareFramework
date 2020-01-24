using DuetAPI.Machine;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
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
        }

        /// <summary>
        /// Called when a new tool is being added to the object model
        /// </summary>
        /// <param name="tool">New tool</param>
        /// <returns>Asynchronous task</returns>
        public static async Task ToolAdded(Tool tool)
        {
            using (await _lock.LockAsync())
            {
                int extruderDrive = tool.FilamentExtruder;
                if (extruderDrive >= 0 && _filamentMapping.TryGetValue(extruderDrive, out string filamentName) && tool.Filament != filamentName)
                {
                    // Tell RepRapFirmware about the loaded filament
                    await SPI.Interface.AssignFilament(extruderDrive, filamentName);
                }
                tool.PropertyChanged += ToolPropertyChanged;
            }
        }

        /// <summary>
        /// Called when a tool is being removed from the object model
        /// </summary>
        /// <param name="tool">Tool being removed</param>
        public static void ToolRemoved(Tool tool) => tool.PropertyChanged -= ToolPropertyChanged;

        /// <summary>
        /// Called when a tool property has changed
        /// </summary>
        /// <param name="sender">Changed tool</param>
        /// <param name="e">Information about the changed property</param>
        private static async void ToolPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Tool.Filament))
            {
                Tool tool = (Tool)sender;
                using (await _lock.LockAsync())
                {
                    if (!_filamentMapping.TryGetValue(tool.FilamentExtruder, out string filament) || filament != tool.Filament)
                    {
                        _filamentMapping[tool.FilamentExtruder] = tool.Filament;
                        await SaveMapping();
                    }
                }
            }
        }

        /// <summary>
        /// Save the filament mapping to a file
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task SaveMapping()
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
