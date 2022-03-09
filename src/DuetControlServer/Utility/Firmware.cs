using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetAPIClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Helper class for the firmware fields
    /// </summary>
    public static class Firmware
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UF2BlockHeader
        {
            public uint MagicStart0;
            public uint MagicStart1;
            public uint Flags;
            public uint TargetAddr;
            public uint PayloadSize;
            public uint BlockNo;
            public uint NumBlocks;
            public uint FileSize;  // or FamilyID
        }
        private const int UF2DataOffset = 32;
        private const int UF2DataMaxLength = 476;
        private const int UF2MagicEndOffset = 508;

        private const uint MagicStart0 = 0x0A324655;
        private const uint MagicStart1 = 0x9E5D5157;
        private const uint MagicEnd = 0x0AB16F30;
        private const uint FlagNoFlash = 0x00000001;

        /// <summary>
        /// Unpack the first file from the given UF2 stream
        /// </summary>
        /// <param name="stream">Data stream</param>
        /// <returns>Unpacked file</returns>
        /// <exception cref="IOException">Invalid UF2 data</exception>
        public static async Task<MemoryStream> UnpackUF2(Stream stream)
        {
            if (stream.Length % 512 != 0)
            {
                throw new IOException("UF2 file size must be a multiple of 512 bytes");
            }

            MemoryStream result = new();

            Memory<byte> blockBuffer = new byte[512];
            UF2BlockHeader block;
            do
            {
                // Read another 512-byte segment
                if (await stream.ReadAsync(blockBuffer) < 512)
                {
                    throw new IOException("Unexpected end in UF2 file");
                }

                // Cast it to a struct and verify the data
                block = MemoryMarshal.Cast<byte, UF2BlockHeader>(blockBuffer.Span)[0];
                if (block.MagicStart0 != MagicStart0 || block.MagicStart1 != MagicStart1)
                {
                    throw new IOException("Invalid magic start in UF2 block");
                }

                uint magicEnd = MemoryMarshal.Read<uint>(blockBuffer.Slice(UF2MagicEndOffset, sizeof(uint)).Span);
                if (magicEnd != MagicEnd)
                {
                    throw new IOException("Invalid magic end in UF2 block");
                }

                if (block.PayloadSize > UF2DataMaxLength)
                {
                    throw new IOException("Invalid payload size in UF2 block");
                }

                // Write the block payload to the result
                if (block.Flags != FlagNoFlash)
                {
                    await result.WriteAsync(blockBuffer.Slice(UF2DataOffset, (int)block.PayloadSize));
                }
            }
            while (block.BlockNo + 1 < block.NumBlocks);

            result.Seek(0, SeekOrigin.Begin);
            return result;
        }

        /// <summary>
        /// Offset in the firmware file pointing to the firmware identifier
        /// </summary>
        private const int FirmwareIdentifierOffset = 0x20;

        /// <summary>
        /// Offset in the firmware file where the load address is stored
        /// </summary>
        private const int FirmwareLoadOffset = 0x24;

        /// <summary>
        /// Maximum length of a possible firmware identifier string
        /// </summary>
        private const int MaxFirmwareStringLength = 128;

        /// <summary>
        /// Try to read the firmware version from a given firmware file
        /// </summary>
        /// <param name="filename">Firmware file</param>
        /// <returns>Firmware version or null if not found</returns>
        private static async Task<string> GetFirmwareVersion(string filename)
        {
            Stream firmwareFile = null;
            try
            {
                // Get a stream containing the binary content
                if (Path.GetExtension(filename) == ".uf2")
                {
                    await using FileStream fs = new(filename, FileMode.Open, FileAccess.Read);
                    firmwareFile = await UnpackUF2(fs);
                }
                else
                {
                    firmwareFile = new FileStream(filename, FileMode.Open, FileAccess.Read);
                }

                // Check if we can read the version and load addresses
                if (firmwareFile.Length < Math.Max(FirmwareIdentifierOffset, FirmwareLoadOffset) + sizeof(uint))
                {
                    return null;
                }

                // Read the identifier and load and start offsets
                using BinaryReader reader = new(firmwareFile, Encoding.UTF8);
                firmwareFile.Seek(FirmwareIdentifierOffset, SeekOrigin.Begin);
                uint versionAddress = reader.ReadUInt32();
                firmwareFile.Seek(FirmwareLoadOffset, SeekOrigin.Begin);
                uint loadAddress = reader.ReadUInt32();

                // Attempt to retrieve the firmware identifier
                if (versionAddress > loadAddress && versionAddress - loadAddress < firmwareFile.Length)
                {
                    firmwareFile.Seek(versionAddress - loadAddress, SeekOrigin.Begin);

                    int numCharsRead = 0;
                    StringBuilder builder = new();
                    while (firmwareFile.CanRead)
                    {
                        char c = reader.ReadChar();
                        if (c == '\0')
                        {
                            // Reached end of string
                            break;
                        }
                        if (numCharsRead++ >= MaxFirmwareStringLength)
                        {
                            // Overflow, result is invalid
                            return null;
                        }
                        if (c == ' ')
                        {
                            // We're only interested in the last space-delimited item 
                            builder.Clear();
                        }
                        else
                        {
                            builder.Append(c);
                        }
                    }
                    return (builder.Length > 0) ? builder.ToString() : null;
                }
            }
            finally
            {
                if (firmwareFile != null)
                {
                    await firmwareFile.DisposeAsync();
                }
            }
            return null;
        }

        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Update the firmware from this instance
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task UpdateFirmware()
        {
            // Get the different firmware filenames
            Dictionary<string, string> firmwareVersions = new();
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Board board in Model.Provider.Get.Boards)
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
                string firmwareFilename = await Files.FilePath.ToPhysicalAsync(firmwareFile, Files.FileDirectory.Firmware);
                if (!File.Exists(firmwareFilename))
                {
                    firmwareFilename = await Files.FilePath.ToPhysicalAsync(firmwareFile, Files.FileDirectory.System);
                }

                if (File.Exists(firmwareFilename))
                {
                    firmwareVersions[firmwareFile] = await GetFirmwareVersion(firmwareFilename);
                }
            }

            // Check which boards are not update to date
            List<Board> outdatedBoards = new();
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                foreach (Board board in Model.Provider.Get.Boards)
                {
                    string newVersion = firmwareVersions[board.FirmwareFileName];
                    if (!string.IsNullOrEmpty(board.FirmwareFileName) && board.FirmwareVersion != newVersion)
                    {
                        outdatedBoards.Add((Board)board.Clone());
                    }
                }
            }

            if (outdatedBoards.Count == 0)
            {
                Console.WriteLine("All boards are up-to-date!");
                await Program.ShutdownAsync();
                return;
            }

            Console.WriteLine((outdatedBoards.Count == 1) ? "There is {0} outdated board:" : "There are {0} outdated boards:", outdatedBoards.Count);
            foreach (Board board in outdatedBoards)
            {
                string newVersion = firmwareVersions[board.FirmwareFileName] ?? "n/a";
                string boardName = string.IsNullOrEmpty(board.Name) ? $"Duet 3 Expansion {board.ShortName}" : board.Name;
                Console.WriteLine("- {0} ({1} -> {2}){3}", boardName, board.FirmwareVersion, newVersion, (board.CanAddress ?? 0) > 0 ? $" @ CAN address {board.CanAddress}" : string.Empty);
            }

            // Determine which boards are supposed to be updated
            List<Board> boardsToUpdate = new();
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
                        string newVersion = firmwareVersions[board.FirmwareFileName] ?? "n/a";
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
                        Commands.Code updateCode = new()
                        {
                            Channel = DuetAPI.CodeChannel.Trigger,
                            Type = CodeType.MCode,
                            MajorNumber = 997,
                            Parameters = new()
                            {
                                new('B', board.CanAddress)
                            }
                        };
                        Message result = await updateCode.Execute();

                        // Unlike with M997, we need to wait for RRF to complete the update process
                        while (true)
                        {
                            await Task.Delay(2000, Program.CancellationToken);

                            using (await Model.Provider.AccessReadOnlyAsync())
                            {
                                if (Model.Provider.Get.State.Status != MachineStatus.Updating)
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
                    Commands.Code updateCode = new()
                    {
                        Channel = DuetAPI.CodeChannel.Trigger,
                        Type = CodeType.MCode,
                        MajorNumber = 997
                    };
                    Message result = await updateCode.Execute();
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
                Console.WriteLine("Resetting mainboard... ");
                try
                {
                    Commands.Code updateCode = new()
                    {
                        Channel = DuetAPI.CodeChannel.Trigger,
                        Type = CodeType.MCode,
                        MajorNumber = 999
                    };
                    Message result = await updateCode.Execute();
                    Console.WriteLine((result.Type == MessageType.Success) ? "Done!" : result.ToString());
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: {0}", e.Message);
                    _logger.Debug(e);
                }
            }

            // Done
            await Program.ShutdownAsync();
        }

        /// <summary>
        /// Update the firmware using a remote DCS instance
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task UpdateFirmwareRemotely()
        {
            // Connect to the remote DCS instance first
            using CommandConnection commandConnection = new();
            using SubscribeConnection subscribeConnection = new();
            ObjectModel objectModel;
            try
            {
                await commandConnection.Connect(Settings.FullSocketPath);
                await commandConnection.SyncObjectModel();

                await subscribeConnection.Connect(SubscriptionMode.Patch, new[] { "boards/**", "directories/**", "state/status" }, Settings.FullSocketPath);
                objectModel = await subscribeConnection.GetObjectModel();
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: Failed to connect to DCS ({0})", e.Message);
                _logger.Debug(e);
                return;
            }

            // Get the different firmware filenames
            Dictionary<string, string> firmwareVersions = new();
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
                string firmwareFilename = await commandConnection.ResolvePath(Path.Combine(objectModel.Directories.Firmware, firmwareFile));
                if (!File.Exists(firmwareFilename))
                {
                    firmwareFilename = await commandConnection.ResolvePath(Path.Combine(objectModel.Directories.System, firmwareFile));
                }

                if (File.Exists(firmwareFilename))
                {
                    firmwareVersions[firmwareFile] = await GetFirmwareVersion(firmwareFilename);
                }
            }

            // Check which boards are not update to date
            List<Board> outdatedBoards = new();
            foreach (Board board in objectModel.Boards)
            {
                string newVersion = firmwareVersions[board.FirmwareFileName];
                if (!string.IsNullOrEmpty(board.FirmwareFileName) && board.FirmwareVersion != newVersion)
                {
                    outdatedBoards.Add((Board)board.Clone());
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
                string newVersion = firmwareVersions[board.FirmwareFileName] ?? "n/a";
                string boardName = string.IsNullOrEmpty(board.Name) ? $"Duet 3 Expansion {board.ShortName}" : board.Name;
                Console.WriteLine("- {0} ({1} -> {2}){3}", boardName, board.FirmwareVersion, newVersion, (board.CanAddress ?? 0) > 0 ? $" @ CAN address {board.CanAddress}" : string.Empty);
            }

            // Determine which boards are supposed to be updated
            List<Board> boardsToUpdate = new();
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
                        string newVersion = firmwareVersions[board.FirmwareFileName] ?? "n/a";
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
                        Commands.Code updateCode = new()
                        {
                            Type = CodeType.MCode,
                            MajorNumber = 997,
                            Parameters = new()
                            {
                                new('B', board.CanAddress)
                            }
                        };
                        Message result = await commandConnection.PerformCode(updateCode);

                        // Unlike with M997, we need to wait for RRF to complete the update process
                        while (true)
                        {
                            await Task.Delay(2000, Program.CancellationToken);

                            using JsonDocument patch = await subscribeConnection.GetObjectModelPatch();
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
                    Commands.Code updateCode = new()
                    {
                        Type = CodeType.MCode,
                        MajorNumber = 997
                    };

                    Message result = await commandConnection.PerformCode(updateCode);
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
                Console.WriteLine("Resetting mainboard... ");
                try
                {
                    Commands.Code updateCode = new()
                    {
                        Type = CodeType.MCode,
                        MajorNumber = 999
                    };

                    Message result = await commandConnection.PerformCode(updateCode);
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
}
