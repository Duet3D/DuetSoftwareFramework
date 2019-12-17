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
        private const string FilamentsCsvFile = "filaments.csv";
        private const string FilamentsCsvHeader = "RepRapFirmware filament assignment file v1";

        private static bool _mappingLoaded;
        private static readonly AsyncLock _lock = new AsyncLock();
        private static readonly Dictionary<int, string> _filamentMapping = new Dictionary<int, string>();

        /// <summary>
        /// Called when a new tool is being added to the object model
        /// </summary>
        /// <param name="tool">New tool</param>
        /// <returns>Asynchronous task</returns>
        public static async Task ToolAdded(Tool tool)
        {
            using (await _lock.LockAsync())
            {
                if (!_mappingLoaded)
                {
                    string filename = await FilePath.ToPhysicalAsync(FilamentsCsvFile, FileDirectory.System);
                    if (File.Exists(filename))
                    {
                        await LoadMapping(filename);
                        _mappingLoaded = true;
                    }
                }

                int extruderDrive = tool.FilamentExtruder;
                if (extruderDrive >= 0)
                {
                    if (FileExecution.MacroFile.RunningConfig)
                    {
                        if (_mappingLoaded && _filamentMapping.TryGetValue(extruderDrive, out string filamentName) && tool.Filament != filamentName)
                        {
                            // Tell RepRapFirmware about the loaded filament
                            await SPI.Interface.AssignFilament(extruderDrive, filamentName ?? string.Empty);
                        }
                    }
                    else
                    {
                        // Keep track of the loaded filament
                        await UpdateTool(tool);
                    }
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
                    await UpdateTool(tool);
                }
            }
        }

        /// <summary>
        /// Update the filament assigned to a tool
        /// </summary>
        /// <param name="tool">Tool to update</param>
        /// <returns>Asynchronous task</returns>
        private static async Task UpdateTool(Tool tool)
        {
            if (!_filamentMapping.TryGetValue(tool.FilamentExtruder, out string filament) || filament != tool.Filament)
            {
                _filamentMapping[tool.FilamentExtruder] = tool.Filament;

                string filename = await FilePath.ToPhysicalAsync(FilamentsCsvFile, FileDirectory.System);
                await SaveMapping(filename);
            }
        }

        /// <summary>
        /// Load the filament mapping from a filaments CSV file
        /// </summary>
        /// <param name="filename">Path to the file</param>
        /// <returns>Asynchronous task</returns>
        private static async Task LoadMapping(string filename)
        {
            using FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read);
            using StreamReader reader = new StreamReader(fs);

            string line = await reader.ReadLineAsync();
            if (line != null && line.StartsWith(FilamentsCsvHeader))
            {
                // Second line holds the CSV column headers...
                await reader.ReadLineAsync();

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

        /// <summary>
        /// Save the filament mapping to a file
        /// </summary>
        /// <param name="filename">Path to the file</param>
        /// <returns>Asynchronous task</returns>
        private static async Task SaveMapping(string filename)
        {
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
