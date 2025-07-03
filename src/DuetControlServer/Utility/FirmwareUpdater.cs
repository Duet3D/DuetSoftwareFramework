using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Utility;

/// <summary>
/// Firmware updater for Duet boards
/// </summary>
/// <param name="codeFactory">Code factory</param>
/// <param name="filePath">File path resolver</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="settings">Settings</param>
public class FirmwareUpdater(CodeFactory codeFactory, FilePathResolver filePath, Model.ObjectModel model, IHostApplicationLifetime lifetime, IOptions<Settings> settings)
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Update the firmware from this instance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task UpdateFirmwareAsync(CancellationToken cancellationToken = default)
    {
        // Get the different firmware filenames
        Dictionary<string, string?> firmwareVersions = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (Board board in model.Boards)
            {
                if (!string.IsNullOrEmpty(board.FirmwareFileName) && !firmwareVersions.ContainsKey(board.FirmwareFileName))
                {
                    firmwareVersions.Add(board.FirmwareFileName, null);
                }
            }
        }

        // Get the available firmware versions
        foreach (string firmwareFile in firmwareVersions.Keys)
        {
            string firmwareFilename = await filePath.ToPhysicalAsync(firmwareFile, FileDirectory.Firmware, cancellationToken);
            if (!File.Exists(firmwareFilename))
            {
                firmwareFilename = await filePath.ToPhysicalAsync(firmwareFile, FileDirectory.System, cancellationToken);
            }

            if (File.Exists(firmwareFilename))
            {
                firmwareVersions[firmwareFile] = await Firmware.GetFirmwareVersionAsync(firmwareFilename, settings.Value.FileBufferSize);
            }
        }

        // Check which boards are not update to date
        List<Board> outdatedBoards = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (Board board in model.Boards)
            {
                if (!string.IsNullOrEmpty(board.FirmwareFileName) && firmwareVersions.TryGetValue(board.FirmwareFileName, out string? newVersion))
                {
                    if (board.FirmwareVersion != newVersion)
                    {
                        outdatedBoards.Add((Board)board.Clone());
                    }
                }
                else
                {
                    Console.WriteLine("Warning: Failed to get corresponding firmware version for {0}, RRF version {1}, firmware filename '{2}'",
                        (board.CanAddress != 0) ? $"{board.Name} @ {board.CanAddress}" : board.Name, board.FirmwareVersion, board.FirmwareFileName);
                }
            }
        }

        if (outdatedBoards.Count == 0)
        {
            Console.WriteLine("All boards are up-to-date!");
            lifetime.StopApplication();
            return;
        }

        Console.WriteLine((outdatedBoards.Count == 1) ? "There is {0} outdated board:" : "There are {0} outdated boards:", outdatedBoards.Count);
        foreach (Board board in outdatedBoards)
        {
            string newVersion = firmwareVersions[board.FirmwareFileName!] ?? "n/a";
            string boardName = string.IsNullOrEmpty(board.Name) ? $"Duet 3 Expansion {board.ShortName}" : board.Name;
            Console.WriteLine("- {0} ({1} -> {2}){3}", boardName, board.FirmwareVersion, newVersion, (board.CanAddress ?? 0) > 0 ? $" @ CAN address {board.CanAddress}" : string.Empty);
        }

        // Determine which boards are supposed to be updated
        List<Board> boardsToUpdate = [];
        if (Console.IsInputRedirected)
        {
            // DCS does not start in update-only mode if Settings.AutoUpdateFirmware is false
            boardsToUpdate.AddRange(outdatedBoards);
        }
        else
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(true);
            }

            Console.Write("Would you like to update them all (Y/n)? ");

            char key = char.ToUpper(Console.ReadKey().KeyChar);
            if (key != '\r')
            {
                Console.WriteLine();
            }

            if (key == '\r' || key == 'Y')
            {
                boardsToUpdate.AddRange(outdatedBoards);
            }
            else
            {
                foreach (Board board in outdatedBoards)
                {
                    string newVersion = firmwareVersions[board.FirmwareFileName!] ?? "n/a";
                    string boardName = string.IsNullOrEmpty(board.Name) ? $"Duet 3 Expansion {board.ShortName}" : board.Name;
                    Console.Write("Would you like to update {0} ({1} -> {2}){3} (Y/n)? ", boardName, board.FirmwareVersion, newVersion, (board.CanAddress ?? 0) > 0 ? $" @ CAN address {board.CanAddress}" : string.Empty);
                    key = char.ToUpper(Console.ReadKey().KeyChar);
                    if (key != '\r')
                    {
                        Console.WriteLine();
                    }

                    if (key == '\r' || key == 'Y')
                    {
                        boardsToUpdate.Add(board);
                    }
                }
            }
        }

        // Update expansion boards
        foreach (Board board in boardsToUpdate)
        {
            if (board.CanAddress > 0)
            {
                Console.Write("Updating firmware on board #{0}... ", board.CanAddress);
                try
                {
                    // Start the update process
                    Commands.Code updateCode = codeFactory.Create();
                    updateCode.Channel = DuetAPI.CodeChannel.Trigger;
                    updateCode.Type = CodeType.MCode;
                    updateCode.MajorNumber = 997;
                    updateCode.Parameters =
                    [
                        new('B', board.CanAddress)
                    ];

                    Message result = await updateCode.ExecuteAsync(cancellationToken) ?? new Message();

                    // Unlike with M997, we need to wait for RRF to complete the update process
                    while (true)
                    {
                        await Task.Delay(2000, cancellationToken);

                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (model.State.Status != MachineStatus.Updating)
                            {
                                // Update complete
                                break;
                            }
                        }
                    }

                    Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: {0}", e.Message);
                    _logger.Debug(e);
                }
            }
        }

        // Update mainboard
        if (boardsToUpdate.Any(board => (board.CanAddress ?? 0) == 0))
        {
            Console.Write("Updating firmware on mainboard... ");
            try
            {
                Commands.Code updateCode = codeFactory.Create();
                updateCode.Channel = DuetAPI.CodeChannel.Trigger;
                updateCode.Type = CodeType.MCode;
                updateCode.MajorNumber = 997;

                Message result = await updateCode.ExecuteAsync(cancellationToken) ?? new Message();
                Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                _logger.Debug(e);
            }
        }
        else if (boardsToUpdate.Count > 0)
        {
            Console.Write("Resetting mainboard... ");
            try
            {
                Commands.Code updateCode = codeFactory.Create();
                updateCode.Channel = DuetAPI.CodeChannel.Trigger;
                updateCode.Type = CodeType.MCode;
                updateCode.MajorNumber = 999;

                Message result = await updateCode.ExecuteAsync(cancellationToken) ?? new Message();
                Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                _logger.Debug(e);
            }
        }

        // Done
        lifetime.StopApplication();
    }

    /// <summary>
    /// Update the firmware using a remote DCS instance
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task UpdateFirmwareRemotelyAsync(CancellationToken cancellationToken)
    {
        // Connect to the remote DCS instance first
        using CommandConnection commandConnection = new();
        using SubscribeConnection subscribeConnection = new();
        ObjectModel objectModel;
        try
        {
            await commandConnection.ConnectAsync(settings.Value.FullSocketPath, cancellationToken);
            await commandConnection.SyncObjectModelAsync(cancellationToken);

            await subscribeConnection.ConnectAsync(SubscriptionMode.Patch, ["boards/**", "directories/**", "state/status"], settings.Value.FullSocketPath, cancellationToken);
            objectModel = await subscribeConnection.GetObjectModelAsync(cancellationToken);
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: Failed to connect to DCS ({0})", e.Message);
            _logger.Debug(e);
            return;
        }

        // Get the different firmware filenames
        Dictionary<string, string?> firmwareVersions = [];
        foreach (Board board in objectModel.Boards)
        {
            if (!string.IsNullOrEmpty(board.FirmwareFileName) && !firmwareVersions.ContainsKey(board.FirmwareFileName))
            {
                firmwareVersions.Add(board.FirmwareFileName, null);
            }
        }

        // Get the available firmware versions
        foreach (string firmwareFile in firmwareVersions.Keys)
        {
            string firmwareFilename = await commandConnection.ResolvePathAsync(Path.Combine(objectModel.Directories.Firmware, firmwareFile), cancellationToken);
            if (!File.Exists(firmwareFilename))
            {
                firmwareFilename = await commandConnection.ResolvePathAsync(Path.Combine(objectModel.Directories.System, firmwareFile), cancellationToken);
            }

            if (File.Exists(firmwareFilename))
            {
                firmwareVersions[firmwareFile] = await Firmware.GetFirmwareVersionAsync(firmwareFilename, settings.Value.FileBufferSize);
            }
        }

        // Check which boards are not update to date
        List<Board> outdatedBoards = [];
        foreach (Board board in objectModel.Boards)
        {
            if (!string.IsNullOrEmpty(board.FirmwareFileName) && firmwareVersions.TryGetValue(board.FirmwareFileName, out string? newVersion))
            {
                if (board.FirmwareVersion != newVersion)
                {
                    outdatedBoards.Add((Board)board.Clone());
                }
            }
            else
            {
                Console.WriteLine("Warning: Failed to get corresponding firmware version for {0}, RRF version {1}, firmware filename '{2}'",
                    (board.CanAddress != 0) ? $"{board.Name} @ {board.CanAddress}" : board.Name, board.FirmwareVersion, board.FirmwareFileName);
            }
        }

        if (outdatedBoards.Count == 0)
        {
            Console.WriteLine("All boards are up-to-date!");
            return;
        }

        Console.WriteLine((outdatedBoards.Count == 1) ? "There is {0} outdated board:" : "There are {0} outdated boards:", outdatedBoards.Count);
        foreach (Board board in outdatedBoards)
        {
            string newVersion = firmwareVersions[board.FirmwareFileName!] ?? "n/a";
            string boardName = string.IsNullOrEmpty(board.Name) ? $"Duet 3 Expansion {board.ShortName}" : board.Name;
            Console.WriteLine("- {0} ({1} -> {2}){3}", boardName, board.FirmwareVersion, newVersion, (board.CanAddress ?? 0) > 0 ? $" @ CAN address {board.CanAddress}" : string.Empty);
        }

        // Determine which boards are supposed to be updated
        List<Board> boardsToUpdate = [];
        if (Console.IsInputRedirected)
        {
            // DCS does not start in update-only mode if Settings.AutoUpdateFirmware is false
            boardsToUpdate.AddRange(outdatedBoards);
        }
        else
        {
            Console.Write("Would you like to update them all (Y/n)? ");

            char key = char.ToUpper(Console.ReadKey().KeyChar);
            if (key != '\r')
            {
                Console.WriteLine();
            }

            if (key == '\r' || key == 'Y')
            {
                boardsToUpdate.AddRange(outdatedBoards);
            }
            else
            {
                foreach (Board board in outdatedBoards)
                {
                    string newVersion = firmwareVersions[board.FirmwareFileName!] ?? "n/a";
                    string boardName = string.IsNullOrEmpty(board.Name) ? $"Duet 3 Expansion {board.ShortName}" : board.Name;
                    Console.Write("Would you like to update {0} ({1} -> {2}){3} (Y/n)? ", boardName, board.FirmwareVersion, newVersion, (board.CanAddress ?? 0) > 0 ? $" @ CAN address {board.CanAddress}" : string.Empty);
                    key = char.ToUpper(Console.ReadKey().KeyChar);
                    if (key != '\r')
                    {
                        Console.WriteLine();
                    }

                    if (key == '\r' || key == 'Y')
                    {
                        boardsToUpdate.Add(board);
                    }
                }
            }
        }

        // Update expansion boards
        foreach (Board board in boardsToUpdate)
        {
            if (board.CanAddress > 0)
            {
                Console.Write("Updating firmware on board #{0}... ", board.CanAddress);
                try
                {
                    // Start the update process
                    Code updateCode = codeFactory.Create();
                    updateCode.Channel = DuetAPI.CodeChannel.Trigger;
                    updateCode.Type = CodeType.MCode;
                    updateCode.MajorNumber = 997;
                    updateCode.Parameters =
                    [
                        new('B', board.CanAddress)
                    ];

                    Message result = await commandConnection.PerformCodeAsync(updateCode, cancellationToken);

                    // Unlike with M997, we need to wait for RRF to complete the update process
                    while (true)
                    {
                        await Task.Delay(2000, cancellationToken);

                        using JsonDocument patch = await subscribeConnection.GetObjectModelPatchAsync(cancellationToken);
                        objectModel.UpdateFromJson(patch.RootElement);

                        if (objectModel.State.Status != MachineStatus.Updating)
                        {
                            // Update complete
                            break;
                        }
                    }

                    Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: {0}", e.Message);
                    _logger.Debug(e);
                }
            }
        }

        // Update mainboard
        if (boardsToUpdate.Any(board => (board.CanAddress ?? 0) == 0))
        {
            Console.Write("Updating firmware on mainboard... ");
            try
            {
                Code updateCode = codeFactory.Create();
                updateCode.Channel = DuetAPI.CodeChannel.Trigger;
                updateCode.Type = CodeType.MCode;
                updateCode.MajorNumber = 997;

                Message result = await commandConnection.PerformCodeAsync(updateCode, cancellationToken);
                Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                _logger.Debug(e);
            }
        }
        else if (boardsToUpdate.Count > 0)
        {
            Console.Write("Resetting mainboard... ");
            try
            {
                Code updateCode = new()
                {
                    Type = CodeType.MCode,
                    MajorNumber = 999
                };

                Message result = await commandConnection.PerformCodeAsync(updateCode, cancellationToken);
                Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: {0}", e.Message);
                _logger.Debug(e);
            }
        }
    }
}