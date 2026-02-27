using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Files.Parser;
using DuetControlServer.Link;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// Class that processes M-codes in the control server
/// </summary>
/// <param name="codeProcessor">Code processor</param>
/// <param name="commandFactory">Command factory</param>
/// <param name="eventLogger">Event logger</param>
/// <param name="fileInfoParser">File info parser</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="filter">Filter for JSON queries</param>
/// <param name="diagnosticsProvider">Diagnostics provider</param>
/// <param name="jobProcessor">Job processor</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="mqtt">MQTT provider</param>
/// <param name="logger">Logger</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="settings">Settings</param>
public class MCodeHandler(
    CodeProcessor codeProcessor,
    CommandFactory commandFactory,
    DiagnosticsProvider diagnosticsProvider,
    EventLogger eventLogger,
    FileInfoParser fileInfoParser,
    FilePathResolver filePathResolver,
    Filter filter,
    LinkInterface linkInterface,
    Model.ObjectModel model,
    MQTT mqtt,
    JobProcessor jobProcessor,
    ILogger<MCodeHandler> logger,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime,
    IOptions<Settings> settings) : ICodeHandler
{
    private MessageLoggerProvider? _messageLoggerProvider;
    /// <summary>
    /// Process an M-code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    public async ValueTask<Message?> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.IsFromFileChannel && jobProcessor.IsSimulating && code.MajorNumber is not 0 and not 1 and not 2)
        {
            // Ignore most M-codes from files in simulation mode...
            return null;
        }

        switch (code.MajorNumber)
        {
            // Stop or Unconditional stop
            // Sleep or Conditional stop
            // Program End
            case 0:
            case 1:
            case 2:
                if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
                {
                    // Attempt to cancel the print from any channel other than File2
                    if (code.Channel != CodeChannel.File2)
                    {
                        using (await jobProcessor.LockAsync(cancellationToken))
                        {
                            if (jobProcessor.IsFileSelected)
                            {
                                // M0/M1/M2 is permitted from inside a job file, but only permitted from elsewhere if the job is already paused
                                if (!code.IsFromFileChannel && !jobProcessor.IsPaused)
                                {
                                    return new Message(MessageType.Error, "Pause the print before attempting to cancel it");
                                }

                                // Invalidate the print file and make sure no more codes are read from it
                                jobProcessor.Cancel();
                            }
                        }
                    }

                    // Reassign the code's cancellation token to ensure M0/M1/M2 is forwarded to RRF
                    if (code.IsFromFileChannel)
                    {
                        code.ResetCancellationToken();
                    }
                    break;
                }
                throw new OperationCanceledException();

            // List SD card
            case 20:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    // Resolve the directory
                    if (!code.TryGetString('P', out string? virtualDirectory))
                    {
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            virtualDirectory = model.Directories.GCodes;
                        }
                    }
                    string physicalDirectory = await filePathResolver.ToPhysicalAsync(virtualDirectory, cancellationToken: cancellationToken);

                    // Make sure to stay within limits if it is a request from the firmware
                    int maxSize = -1;
                    if (code.Flags.HasFlag(CodeFlags.IsFromFirmware))
                    {
                        maxSize = settings.Value.MaxMessageLength;
                    }

                    // Check if JSON file lists were requested
                    int startAt = Math.Max(code.GetInt('R', 0), 0), type = code.GetInt('S', 0), maxItems = code.GetInt('C', -1);
                    if (type == 2)
                    {
                        string json = FileLists.GetFiles(virtualDirectory, physicalDirectory, startAt, true, maxSize, maxItems, code.ExplicitLineNumber);
                        return new Message(MessageType.Success, json);
                    }
                    if (type == 3)
                    {
                        string json = FileLists.GetFileList(virtualDirectory, physicalDirectory, startAt, maxSize, maxItems, code.ExplicitLineNumber);
                        return new Message(MessageType.Success, json);
                    }

                    // Print standard G-code response
                    Compatibility compatibility;
                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        compatibility = model.Inputs[code.Channel]?.Compatibility ?? Compatibility.RepRapFirmware;
                    }

                    StringBuilder result = new();
                    if (compatibility == Compatibility.Default || compatibility == Compatibility.RepRapFirmware)
                    {
                        result.AppendLine("GCode files:");
                    }
                    else if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                    {
                        result.AppendLine("Begin file list:");
                    }

                    bool itemFound = false;
                    foreach (string file in Directory.EnumerateFileSystemEntries(physicalDirectory))
                    {
                        string filename = Path.GetFileName(file);
                        if (maxSize > 0 && result.Length + filename.Length + 3 > maxSize)
                        {
                            // Stay within limits...
                            break;
                        }

                        if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                        {
                            result.AppendLine(filename);
                        }
                        else
                        {
                            if (itemFound)
                            {
                                result.Append(',');
                            }
                            result.Append($"\"{filename}\"");
                        }
                        itemFound = true;
                    }

                    if (compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP)
                    {
                        if (!itemFound)
                        {
                            result.AppendLine("NONE");
                        }
                        result.Append("End file list");
                    }

                    return new Message(MessageType.Success, result.ToString());
                }
                throw new OperationCanceledException();

            // Initialize SD card
            case 21:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (code.GetInt('P', 0) == 0)
                    {
                        // M21 (P0) will always work because it's always mounted
                        return new Message();
                    }
                    throw new NotSupportedException();
                }
                throw new OperationCanceledException();

            // Release SD card
            case 22:
                throw new NotSupportedException();

            // Select a file to print
            case 23:
            case 32:
                if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
                {
                    if (code.Channel != CodeChannel.File2)
                    {
                        string fileName = code.GetUnprecedentedString();
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            return new Message(MessageType.Error, "Filename expected");
                        }

                        string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, FileDirectory.GCodes, cancellationToken);
                        if (!File.Exists(physicalFile))
                        {
                            return new Message(MessageType.Error, $"Could not find file {fileName}");
                        }

                        using (await jobProcessor.LockAsync(cancellationToken))
                        {
                            if (!code.IsFromFileChannel && (jobProcessor.IsProcessing || jobProcessor.IsPaused))
                            {
                                return new Message(MessageType.Error, "Cannot set file to print, because a file is already being printed");
                            }
                            await jobProcessor.SelectFileAsync(fileName, physicalFile, false, cancellationToken);
                        }
                    }

                    // Let RRF do everything else
                    break;
                }
                throw new OperationCanceledException();

            // Resume a file print
            case 24:
                if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
                {
                    if (code.Channel != CodeChannel.File2)
                    {
                        using (await jobProcessor.LockAsync(cancellationToken))
                        {
                            if (!jobProcessor.IsFileSelected)
                            {
                                return new Message(MessageType.Error, "Cannot print, because no file is selected!");
                            }
                        }
                    }

                    // Let RepRapFirmware process this request so it can invoke resume.g. When M24 completes, the file is resumed
                    break;
                }
                throw new OperationCanceledException();

            // Set SD position
            case 26:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    // Wait for inputs[].motionSystem to be up-to-date
                    await model.WaitForFullUpdateAsync(cancellationToken);

                    int motionSystem;
                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        motionSystem = model.Inputs[code.Channel]?.MotionSystem ?? 0;
                    }

                    using (await jobProcessor.LockAsync(cancellationToken))
                    {
                        if (!jobProcessor.IsFileSelected)
                        {
                            return new Message(MessageType.Error, "Not printing a file");
                        }

                        if (code.TryGetLong('S', out long newPosition))
                        {
                            if (newPosition < 0L || newPosition > jobProcessor.FileLength)
                            {
                                return new Message(MessageType.Error, "Position is out of range");
                            }

                            await jobProcessor.SetFilePositionAsync(motionSystem, newPosition, cancellationToken);
                        }
                    }

                    // P parameter is handled by RRF if present
                    break;
                }
                throw new OperationCanceledException();

            // Report SD print status
            case 27:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    // Wait for inputs[].motionSystem to be up-to-date
                    await model.WaitForFullUpdateAsync(cancellationToken);
                    int motionSystem;
                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        motionSystem = model.Inputs[code.Channel]?.MotionSystem ?? 0;
                    }

                    using (await jobProcessor.LockAsync(cancellationToken))
                    {
                        if (jobProcessor.IsFileSelected)
                        {
                            long filePosition = await jobProcessor.GetFilePositionAsync(motionSystem, cancellationToken);
                            return new Message(MessageType.Success, $"SD printing byte {filePosition}/{jobProcessor.FileLength}");
                        }
                        return new Message(MessageType.Success, "Not SD printing.");
                    }
                }
                throw new OperationCanceledException();

            // Begin write to SD card
            case 28:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    int numChannel = (int)code.Channel;
                    using (await codeProcessor.FileLocks[numChannel].LockAsync(cancellationToken))
                    {
                        if (codeProcessor.FilesBeingWritten[numChannel] is not null)
                        {
                            return new Message(MessageType.Error, "Another file is already being written to");
                        }

                        string file = code.GetUnprecedentedString();
                        if (string.IsNullOrWhiteSpace(file))
                        {
                            return new Message(MessageType.Error, "Filename expected");
                        }

                        string prefix = await model.IsEmulatingMarlinAsync(code.Channel, cancellationToken) ? "ok\n" : string.Empty;
                        string physicalFile = await filePathResolver.ToPhysicalAsync(file, FileDirectory.GCodes, cancellationToken), parentDirectory = Path.GetDirectoryName(physicalFile)!;
                        try
                        {
                            if (!Directory.Exists(parentDirectory))
                            {
                                Directory.CreateDirectory(parentDirectory);
                            }

                            FileStream fileStream = new(physicalFile, FileMode.Create, FileAccess.Write, FileShare.Read, settings.Value.FileBufferSize);
                            StreamWriter writer = new(fileStream, Encoding.UTF8, settings.Value.FileBufferSize);
                            codeProcessor.FilesBeingWritten[numChannel] = writer;
                            return new Message(MessageType.Success, prefix + $"Writing to file: {file}");
                        }
                        catch (Exception e)
                        {
                            logger.LogDebug(e, "Failed to open file for writing");
                            return new Message(MessageType.Error, prefix + $"Can't open file {file} for writing.");
                        }
                    }
                }
                throw new OperationCanceledException();

            // End write to SD card
            case 29:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    int numChannel = (int)code.Channel;
                    using (await codeProcessor.FileLocks[numChannel].LockAsync(cancellationToken))
                    {
                        StreamWriter? writer = codeProcessor.FilesBeingWritten[numChannel];
                        if (writer is not null)
                        {
                            Stream stream = writer.BaseStream;
                            await writer.DisposeAsync();
                            codeProcessor.FilesBeingWritten[numChannel] = null;
                            await stream.DisposeAsync();

                            if (await model.IsEmulatingMarlinAsync(code.Channel, cancellationToken))
                            {
                                return new Message(MessageType.Success, "Done saving file.");
                            }
                            return new Message();
                        }
                        break;
                    }
                }
                throw new OperationCanceledException();

            // Delete a file on the SD card
            case 30:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    string file = code.GetUnprecedentedString();
                    string physicalFile = await filePathResolver.ToPhysicalAsync(file, cancellationToken: cancellationToken);

                    try
                    {
                        File.Delete(physicalFile);
                        return new Message();
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Failed to delete file");
                        return new Message(MessageType.Error, $"Failed to delete file {file}: {e.Message}");
                    }
                }
                throw new OperationCanceledException();

            // For case 32, see case 23

            // Return file information
            case 36:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (code.Parameters.Count > 0)
                    {
                        string virtualFilename = string.Empty;
                        try
                        {
                            if ((code.MinorNumber ?? 0) <= 0)
                            {
                                // Get fileinfo
                                virtualFilename = code.GetUnprecedentedString();
                                string physicalFilename = await filePathResolver.ToPhysicalAsync(virtualFilename, FileDirectory.GCodes, cancellationToken);
                                GCodeFileInfo info = await fileInfoParser.ParseAsync(physicalFilename, false, cancellationToken);

                                string json = JsonSerializer.Serialize(info, JsonHelper.DefaultJsonOptions);
                                return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":0," : "{\"err\":0,") + json[1..]);
                            }
                            else if (code.MinorNumber == 1 || code.MinorNumber == 2)
                            {
                                // Get thumbnail or file fragment
                                virtualFilename = code.GetString('P');
                                string physicalFilename = await filePathResolver.ToPhysicalAsync(virtualFilename, FileDirectory.GCodes, cancellationToken);

                                string json = await fileInfoParser.ParseFileFragment(physicalFilename, code.GetLong('S'), code.MinorNumber == 1, code.ExplicitLineNumber);
                                return new Message(MessageType.Success, json);
                            }
                            else
                            {
                                throw new NotSupportedException();
                            }
                        }
                        catch (Exception e) when (e is not MissingParameterException and not InvalidParameterTypeException)
                        {
                            logger.LogDebug(e, "Failed to return file information");
                            return new Message(MessageType.Warning, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":1,\"fileName:" : "{\"err\":1,\"fileName:") + JsonSerializer.Serialize(virtualFilename, JsonHelper.DefaultJsonOptions) + "}");
                        }
                    }
                    else
                    {
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (model.Job.File.FileName != null)
                            {
                                string json = JsonSerializer.Serialize(model.Job.File, JsonHelper.DefaultJsonOptions);
                                return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":0," : "{\"err\":0,") + json[1..]);
                            }
                        }
                        return new Message(MessageType.Warning, (code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber},\"err\":1}}" : "{\"err\":1}");
                    }
                }
                throw new OperationCanceledException();

            // Simulate file
            case 37:
                if (await codeProcessor.FlushAsync(code, syncFileStreams: true, cancellationToken: cancellationToken))
                {
                    if (code.Channel != CodeChannel.File2)
                    {
                        string fileName = code.GetString('P');
                        string physicalFile = await filePathResolver.ToPhysicalAsync(fileName, FileDirectory.GCodes, cancellationToken);
                        if (!File.Exists(physicalFile))
                        {
                            return new Message(MessageType.Error, $"GCode file \"{fileName}\" not found");
                        }

                        using (await jobProcessor.LockAsync(cancellationToken))
                        {
                            if (!code.IsFromFileChannel && (jobProcessor.IsProcessing || jobProcessor.IsPaused))
                            {
                                return new Message(MessageType.Error, "Cannot set file to simulate, because a file is already being printed");
                            }

                            await jobProcessor.SelectFileAsync(fileName, physicalFile, true, cancellationToken);
                            // Simulation is started when M37 has been processed by the firmware
                        }
                    }

                    // Let RRF do everything else
                    break;
                }
                throw new OperationCanceledException();

            // Compute CRC32 checksum of target file
            case 38:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    string file = code.GetUnprecedentedString(), physicalFile = await filePathResolver.ToPhysicalAsync(file, cancellationToken: cancellationToken);
                    try
                    {
                        await using FileStream stream = new(physicalFile, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
                        uint checksum = await CRC32.CalculateAsync(stream, settings.Value.FileBufferSize, cancellationToken);
                        return new Message(MessageType.Success, checksum.ToString("x8"));
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Failed to compute CRC32 checksum");
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException!;
                        }
                        return new Message(MessageType.Error, $"Could not compute CRC32 checksum for file {file}: {e.Message}");
                    }
                }
                throw new OperationCanceledException();

            // Report SD card information
            case 39:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        int index = code.GetInt('P', 0);
                        if (code.GetInt('S', 0) == 2)
                        {
                            if (index < 0 || index >= model.Volumes.Count)
                            {
                                return new Message(MessageType.Success, ((code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber}," : "{") + $"\"SDinfo\":{{\"slot\":{index},present:0}}}}");
                            }

                            Volume storage = model.Volumes[index];
                            var output = new
                            {
                                SDinfo = new
                                {
                                    slot = index,
                                    present = 1,
                                    capacity = storage.Capacity,
                                    partitionSize = storage.PartitionSize,
                                    free = storage.FreeSpace,
                                    speed = storage.Speed
                                }
                            };

                            if (code.ExplicitLineNumber != null)
                            {
                                return new Message(MessageType.Success, $"{{\"line\":{code.ExplicitLineNumber},{JsonSerializer.Serialize(output)[1..]}");
                            }
                            return new Message(MessageType.Success, JsonSerializer.Serialize(output));
                        }
                        else
                        {
                            if (index < 0 || index >= model.Volumes.Count)
                            {
                                return new Message(MessageType.Error, $"Bad SD slot number: {index}");
                            }

                            Volume storage = model.Volumes[index];
                            return new Message(MessageType.Success, $"SD card in slot {index}: capacity {storage.Capacity / (1000 * 1000 * 1000):F2}Gb, partition size {storage.PartitionSize / (1000 * 1000 * 1000):F2}Gb,free space {storage.FreeSpace / (1000 * 1000 * 1000):F2}Gb, speed {storage.Speed / (1000 * 1000):F2}MBytes/sec");
                        }
                    }
                }
                throw new OperationCanceledException();

            // Flag current macro file as (not) pausable
            case 98:
                {
                    if (code.TryGetInt('R', out int rParam))
                    {
                        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                        {
                            await linkInterface.SetMacroPausableAsync(code.Channel, rParam == 1, cancellationToken);
                        }
                        else
                        {
                            throw new OperationCanceledException();
                        }
                    }
                    break;
                }

            // Set Debug Level
            // We only support some options for M111 P-1:
            // - S"<level>" sets the log level; accepted values are trace, debug, info, warn, error, fatal, off (and their long forms)
            // - Onnn can be used to turn on/off logging via generic messages (accessible then e.g. via web UI)
            case 111:
                {
                    if (code.TryGetInt('P', out int pParam) && pParam == -1)
                    {
                        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                        {
                            bool seen = false;
                            if (code.TryGetString('S', out string? levelString))
                            {
                                // Parse the log level using shared helper that supports short aliases
                                if (LogLevelHelper.TryParseLogLevel(levelString, out LogLevel level))
                                {
                                    // Writing settings.Value.LogLevel is all that's needed: the dynamic
                                    // logging filter in Program.cs reads it directly on every IsEnabled() call.
                                    settings.Value.LogLevel = level;
                                    logger.LogInformation("Log level changed to {Level}", level);
                                    seen = true;
                                }
                                else
                                {
                                    return new Message(MessageType.Error, $"Invalid log level '{levelString}'. Valid values: {LogLevelHelper.ValidLogLevels}");
                                }
                            }
                            if (code.TryGetBool('O', out bool oParam))
                            {
                                if (oParam)
                                {
                                    if (_messageLoggerProvider == null)
                                    {
                                        // Only add this provider once and don't allow higher log level than debug, else we may get recursion
                                        LogLevel minimumLevel = settings.Value.LogLevel > LogLevel.Trace ? settings.Value.LogLevel : LogLevel.Debug;
                                        _messageLoggerProvider = new MessageLoggerProvider(model, minimumLevel);
                                        loggerFactory.AddProvider(_messageLoggerProvider);
                                    }
                                }
                                else
                                {
                                    _messageLoggerProvider?.Dispose();
                                    _messageLoggerProvider = null;
                                }
                                seen = true;
                            }

                            if (seen)
                            {
                                return new Message();
                            }
                            return new Message(MessageType.Success, $"Current DCS log level: {settings.Value.LogLevel}");
                        }
                    }
                    break;
                }

            // Emergency Stop
            case 112:
                if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    // Wait for potential firmware updates to complete first
                    await linkInterface.WaitForUpdateAsync(cancellationToken);

                    // Perform emergency stop but don't wait longer than 4.5s
                    Task stopTask = linkInterface.EmergencyStopAsync(cancellationToken);
                    Task completedTask = await Task.WhenAny(stopTask, Task.Delay(4500, lifetime.ApplicationStopped));
                    if (stopTask != completedTask)
                    {
                        // Halt timed out, shut down this program
                        lifetime.StopApplication();
                        return new Message(MessageType.Error, "Halt timed out, stopping DCS");
                    }

                    // RRF halted
                    using (await model.AccessReadWriteAsync(cancellationToken))
                    {
                        model.State.Status = MachineStatus.Halted;
                    }
                    return new Message();
                }
                throw new OperationCanceledException();

            // Publish MQTT message
            case 118:
                {
                    if (code.TryGetInt('P', out int pParam) && pParam == 6)
                    {
                        if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                        {
                            return await mqtt.PublishAsync(code);
                        }
                        throw new OperationCanceledException();
                    }
                    break;
                }

            // Immediate DSF diagnostics
            case 122:
                if (code.GetInt('B', 0) == 0 && code.GetUnprecedentedString() == "DSF")
                {
                    string diagnostics = await diagnosticsProvider.PrintAsync();
                    return new Message(MessageType.Success, diagnostics);
                }
                break;

            // Query object model
            case 409:
                {
                    if (code.TryGetInt('I', out int iVal) && iVal > 0)
                    {
                        return new Message(MessageType.Error, "M409 I1 is reserved for internal purposes only");
                    }

                    if (code.TryGetString('K', out string? key) && (!code.TryGetInt('R', out int rParam) || rParam == 0))
                    {
                        string trimmedKey = key.TrimStart('#');
                        if (!trimmedKey.StartsWith("network") && !trimmedKey.StartsWith("plugins") && !trimmedKey.StartsWith("sbc") && !trimmedKey.StartsWith("volumes"))
                        {
                            // Only return query results for network/plugins/sbc/volume keys as part of M409
                            break;
                        }

                        // Wait until pending codes have finished
                        if (!await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                        {
                            throw new OperationCanceledException();
                        }

                        // Retrieve filtered OM data. At present, flags are ignored
                        code.TryGetString('F', out string? flags);
                        using JsonDocument queryResult = JsonSerializer.SerializeToDocument(filter.GetFiltered(key + ".**"), JsonHelper.DefaultJsonOptions);

                        // Get down to the requested depth
                        JsonElement result = queryResult.RootElement;
                        if (key is not null)
                        {
                            foreach (string depth in key.Split('.'))
                            {
                                if (result.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var subItem in result.EnumerateObject())
                                    {
                                        result = subItem.Value;
                                        break;
                                    }
                                }
                            }
                        }

                        // Generate final OM response
                        object finalResult;
                        if (result.ValueKind == JsonValueKind.Array)
                        {
                            finalResult = new
                            {
                                key,
                                flags = flags ?? string.Empty,
                                result,
                                next = 0
                            };
                        }
                        else
                        {
                            finalResult = new
                            {
                                key,
                                flags = flags ?? string.Empty,
                                result
                            };
                        }

                        string json = JsonSerializer.Serialize(finalResult, JsonHelper.DefaultJsonOptions);
                        return new Message(MessageType.Success, (code.ExplicitLineNumber != null) ? $"{{\"line\":{code.ExplicitLineNumber}," + json : json);
                    }
                    else
                    {
                        break;
                    }
                }

            // Create Directory on SD-Card
            case 470:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    string path = code.GetString('P'), physicalPath = await filePathResolver.ToPhysicalAsync(path, cancellationToken: cancellationToken);
                    try
                    {
                        Directory.CreateDirectory(physicalPath);
                        return new Message();
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Failed to create directory");
                        return new Message(MessageType.Error, $"Failed to create directory {path}: {e.Message}");
                    }
                }
                throw new OperationCanceledException();

            // Rename File/Directory on SD-Card
            case 471:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    string from = code.GetString('S'), to = code.GetString('T');
                    try
                    {
                        string source = await filePathResolver.ToPhysicalAsync(from, cancellationToken: cancellationToken), destination = await filePathResolver.ToPhysicalAsync(to, cancellationToken: cancellationToken);
                        if (File.Exists(source))
                        {
                            if (File.Exists(destination) && code.GetBool('D', false))
                            {
                                File.Delete(destination);
                            }
                            File.Move(source, destination);
                        }
                        else if (Directory.Exists(source))
                        {
                            if (Directory.Exists(destination) && code.GetBool('D', false))
                            {
                                // This could be recursive but at the moment we mimic RRF's behaviour
                                Directory.Delete(destination);
                            }
                            Directory.Move(source, destination);
                        }
                        else
                        {
                            throw new FileNotFoundException();
                        }
                        return new Message();
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Failed to rename file or directory");
                        return new Message(MessageType.Error, $"Failed to rename file or directory {from} to {to}: {e.Message}");
                    }
                }
                throw new OperationCanceledException();

            // Delete file/directory
            case 472:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    string path = code.GetString('P'), physicalPath = await filePathResolver.ToPhysicalAsync(path, cancellationToken: cancellationToken);
                    try
                    {
                        if (Directory.Exists(physicalPath))
                        {
                            _ = code.TryGetBool('R', out bool recursive);
                            Directory.Delete(physicalPath, recursive);
                        }
                        else
                        {
                            File.Delete(physicalPath);
                        }
                        return new Message();
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Failed to delete file or directory");
                        return new Message(MessageType.Error, $"Failed to delete file or directory {path}: {e.Message}");
                    }
                }
                throw new OperationCanceledException();

            // Print settings
            case 503:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    string configFile = await filePathResolver.ToPhysicalAsync(FilePathResolver.ConfigFile, FileDirectory.System, cancellationToken);
                    if (File.Exists(configFile))
                    {
                        string content = await File.ReadAllTextAsync(configFile, cancellationToken);
                        return new Message(MessageType.Success, content);
                    }

                    string configFileFallback = await filePathResolver.ToPhysicalAsync(FilePathResolver.ConfigFileFallback, FileDirectory.System, cancellationToken);
                    if (File.Exists(configFileFallback))
                    {
                        string content = await File.ReadAllTextAsync(configFileFallback, cancellationToken);
                        return new Message(MessageType.Success, content);
                    }
                    return new Message(MessageType.Error, "Configuration file not found");
                }
                throw new OperationCanceledException();

            // Set configuration file folder
            case 505:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (code.TryGetString('P', out string? directory))
                    {
                        await using (await linkInterface.LockAllMovementSystemsAndWaitForStandstill(code.Channel))
                        {
                            string physicalDirectory = await filePathResolver.ToPhysicalAsync(directory, "sys", cancellationToken);
                            if (Directory.Exists(physicalDirectory))
                            {
                                string virtualDirectory = await filePathResolver.ToVirtualAsync(physicalDirectory, cancellationToken);
                                using (await model.AccessReadWriteAsync(cancellationToken))
                                {
                                    model.Directories.System = virtualDirectory;
                                }
                                return new Message();
                            }
                        }
                        return new Message(MessageType.Error, "Directory not found");
                    }

                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        return new Message(MessageType.Success, $"Sys file path is {model.Directories.System}");
                    }
                }
                throw new OperationCanceledException();

            // Set Name
            case 550:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (code.TryGetString('P', out string? newName))
                    {
                        if (newName.Length > 40)
                        {
                            return new Message(MessageType.Error, "Machine name is too long");
                        }

                        // Strip letters and digits from the machine name
                        string machineName = string.Empty;
                        foreach (char c in Environment.MachineName)
                        {
                            if (char.IsLetterOrDigit(c))
                            {
                                machineName += c;
                            }
                        }

                        // Strip letters and digits from the desired name
                        string desiredName = string.Empty;
                        foreach (char c in newName)
                        {
                            if (char.IsLetterOrDigit(c))
                            {
                                desiredName += c;
                            }
                        }

                        // Make sure the subset of letters and digits is equal
                        if (!machineName.Equals(desiredName, StringComparison.CurrentCultureIgnoreCase))
                        {
                            return new Message(MessageType.Error, "Machine name must consist of the same letters and digits as configured by the Linux hostname");
                        }

                        // Hostname is legit - pass this code on to RRF so it can update the name too
                    }
                    break;
                }
                throw new OperationCanceledException();

            // Set Password
            case 551:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (code.TryGetString('P', out string? password))
                    {
                        using (await model.AccessReadWriteAsync(cancellationToken))
                        {
                            model.Password = password;
                        }
                    }
                    break;
                }
                throw new OperationCanceledException();

            // Configure network protocols
            case 586:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    // Configure MQTT
                    if (code.MinorNumber == 4)
                    {
                        return mqtt.Configure(code);
                    }
                    else if (code.TryGetInt('P', out int pParam) && pParam == 4)
                    {
                        return await mqtt.ConfigureProtocolAsync(code);
                    }

                    // Set CORS site
                    if (code.TryGetString('C', out string? corsSite))
                    {
                        using (await model.AccessReadWriteAsync(cancellationToken))
                        {
                            model.Network.CorsSite = string.IsNullOrWhiteSpace(corsSite) ? null : corsSite;
                        }
                        return new Message();
                    }

                    // Report CORS state
                    using (await model.AccessReadOnlyAsync(cancellationToken))
                    {
                        if (string.IsNullOrEmpty(model.Network.CorsSite))
                        {
                            return new Message(MessageType.Success, "CORS disabled");
                        }
                        return new Message(MessageType.Success, $"CORS enabled for site '{model.Network.CorsSite}'");
                    }
                }
                throw new OperationCanceledException();

            // Set IP address (reserved in SBC mode)
            case 552:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    return new Message(MessageType.Error, "M552 is reserved for SBC mode");
                }
                throw new OperationCanceledException();

            // Fork input reader
            case 606:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (code.TryGetInt('S', out int sParam) && sParam == 1)
                    {
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (model.Inputs[CodeChannel.File2] is null)
                            {
                                // Command not supported. Let RRF decide what to do
                                break;
                            }
                        }

                        // Try to fork the file and report an error if anything went wrong
                        using (await jobProcessor.LockAsync(cancellationToken))
                        {
                            Message result = await jobProcessor.ForkAsync(cancellationToken);
                            if (result.Type != MessageType.Success)
                            {
                                return result;
                            }
                        }
                    }

                    // Let RRF carry on
                    break;
                }
                throw new OperationCanceledException();

            // Start/stop event logging to SD card
            case 929:
                if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                {
                    if (!code.TryGetInt('S', out int sParam))
                    {
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (model.State.LogLevel == EventLogLevel.Off)
                            {
                                return new Message(MessageType.Success, "Event logging is disabled");
                            }
                            return new Message(MessageType.Success, $"Event logging is enabled at log level {model.State.LogLevel.ToString().ToLowerInvariant()}");
                        }
                    }

                    if (sParam > 0 && sParam < 4)
                    {
                        EventLogLevel logLevel = sParam switch
                        {
                            1 => EventLogLevel.Warn,
                            2 => EventLogLevel.Info,
                            3 => EventLogLevel.Debug,
                            _ => EventLogLevel.Off
                        };

                        string defaultLogFile = EventLogger.DefaultLogFile;
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (!string.IsNullOrEmpty(model.State.LogFile))
                            {
                                defaultLogFile = model.State.LogFile;
                            }
                        }

                        await eventLogger.StartAsync(code.GetString('P', defaultLogFile), logLevel);
                    }
                    else
                    {
                        await eventLogger.StopAsync();
                    }
                    return new Message();
                }
                throw new OperationCanceledException();

            // Update the firmware
            case 997:
                if (code.GetIntArray('S', [0]).Contains(0) && code.GetInt('B', 0) == 0)
                {
                    if (await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                    {
                        // Get the IAP and Firmware files
                        string? iapFile, firmwareFile;
                        using (await model.AccessReadOnlyAsync(cancellationToken))
                        {
                            if (model.Boards.Count == 0)
                            {
                                return new Message(MessageType.Error, "No boards have been detected");
                            }

                            // There are now two different IAP binaries, check which one to use
                            iapFile = model.Boards[0].IapFileNameSBC;
                            if (!code.TryGetString('P', out firmwareFile))
                            {
                                firmwareFile = model.Boards[0].FirmwareFileName;
                            }
                        }

                        if (string.IsNullOrEmpty(iapFile) || string.IsNullOrEmpty(firmwareFile))
                        {
                            return new Message(MessageType.Error, "Cannot update firmware because IAP and firmware filenames are unknown");
                        }

                        string physicalIapFile = await filePathResolver.ToPhysicalAsync(iapFile, FileDirectory.Firmware, cancellationToken);
                        if (!File.Exists(physicalIapFile))
                        {
                            string fallbackIapFile = await filePathResolver.ToPhysicalAsync($"0:/firmware/{iapFile}", cancellationToken: cancellationToken);
                            if (!File.Exists(fallbackIapFile))
                            {
                                fallbackIapFile = await filePathResolver.ToPhysicalAsync(iapFile, FileDirectory.System, cancellationToken);
                                if (!File.Exists(fallbackIapFile))
                                {
                                    return new Message(MessageType.Error, $"Failed to find IAP file {iapFile}");
                                }
                            }
                            logger.LogWarning("Using fallback IAP file {File}", fallbackIapFile);
                            physicalIapFile = fallbackIapFile;
                        }

                        string physicalFirmwareFile = await filePathResolver.ToPhysicalAsync(firmwareFile, FileDirectory.Firmware, cancellationToken);
                        if (!File.Exists(physicalFirmwareFile))
                        {
                            string fallbackFirmwareFile = await filePathResolver.ToPhysicalAsync($"0:/firmware/{firmwareFile}", cancellationToken: cancellationToken);
                            if (!File.Exists(fallbackFirmwareFile))
                            {
                                fallbackFirmwareFile = await filePathResolver.ToPhysicalAsync(firmwareFile, FileDirectory.System, cancellationToken);
                                if (!File.Exists(fallbackFirmwareFile))
                                {
                                    return new Message(MessageType.Error, $"Failed to find firmware file {firmwareFile}");
                                }
                            }
                            logger.LogWarning("Using fallback firmware file {File}", fallbackFirmwareFile);
                            physicalFirmwareFile = fallbackFirmwareFile;
                        }

                        // Stop all the plugins
                        Commands.StopPlugins stopCommand = commandFactory.Create<Commands.StopPlugins>();
                        await stopCommand.ExecuteAsync(cancellationToken);

                        // Update the firmware
                        await using FileStream iapStream = new(physicalIapFile, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
                        await using FileStream firmwareStream = new(physicalFirmwareFile, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize);
                        if (Path.GetExtension(firmwareFile) == ".uf2")
                        {
                            await using MemoryStream unpackedFirmwareStream = await Firmware.UnpackUF2Async(firmwareStream);
                            await linkInterface.UpdateFirmware(iapStream, unpackedFirmwareStream, lifetime.ApplicationStopped);
                        }
                        else
                        {
                            await linkInterface.UpdateFirmware(iapStream, firmwareStream, lifetime.ApplicationStopped);
                        }

                        // Terminate the program once this code has finished
                        _ = code.Task.ContinueWith(async task =>
                        {
                            await task;
                            lifetime.StopApplication();
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);

                        // Done
                        return new Message();
                    }
                    throw new OperationCanceledException();
                }
                break;

            // Request resend of line
            case 998:
                throw new NotSupportedException();

            // Reset controller
            case 999:
                if (code.Parameters.Count == 0)
                {
                    if (code.Flags.HasFlag(CodeFlags.IsPrioritized) || await codeProcessor.FlushAsync(code, cancellationToken: cancellationToken))
                    {
                        // Wait for potential firmware updates to complete first
                        await linkInterface.WaitForUpdateAsync();

                        // Perform firmware reset but don't wait longer than 4.5s
                        Task resetTask = linkInterface.ResetFirmwareAsync(lifetime.ApplicationStopping);
                        Task completedTask = await Task.WhenAny(resetTask, Task.Delay(4500, lifetime.ApplicationStopped));
                        if (resetTask != completedTask)
                        {
                            // Reset timed out, stop this program
                            lifetime.StopApplication();
                            return new Message(MessageType.Error, "Reset timed out, stopping DCS");
                        }

                        // Terminate the program once this code has finished
                        _ = code.Task.ContinueWith(async task =>
                        {
                            await task;
                            lifetime.StopApplication();
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);

                        // Firmware reset
                        return new Message();
                    }
                    throw new OperationCanceledException();
                }
                break;
        }
        return null;
    }

    /// <summary>
    /// React to an executed M-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    /// <remarks>This method shall be used only to update values that are time-critical. Others are supposed to be updated via the object model</remarks>
    public async ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.Result is null || code.Result.Type != MessageType.Success)
        {
            return;
        }

        switch (code.MajorNumber)
        {
            // Stop or Unconditional stop
            // Sleep or Conditional stop
            // Resume print
            // Select file and start SD print
            // Simulate file
            case 0:
            case 1:
            case 24:
            case 32:
            case 37:
                using (await jobProcessor.LockAsync(cancellationToken))
                {
                    // Start sending file instructions to RepRapFirmware or finish the cancellation process
                    jobProcessor.Resume();
                }
                break;

            // Pop
            case 121:
                await model.WaitForFullUpdateAsync(cancellationToken);        // This may change inputs[].active, so sync the OM here
                break;

            // Diagnostics
            case 122:
                if (code.GetInt('B', 0) == 0 && code.GetInt('P', 0) == 0 && code.GetUnprecedentedString() != "DSF" && !string.IsNullOrEmpty(code.Result.Content))
                {
                    // Append our own diagnostics to RRF's M122 output
                    string diagnostics = await diagnosticsProvider.PrintAsync();
                    code.Result.Append(MessageType.Success, diagnostics);
                }
                break;

            // Send/receive data
            case 260:
            case 261:
                if (code.File != null && code.TryGetString('V', out string? varName))
                {
                    // These codes can create local variables, so keep track of them
                    using (await code.File.LockAsync(cancellationToken))
                    {
                        code.File.AddLocalVariable(varName);
                    }
                }
                break;

            // Query object model
            case 409:
                if (code.HasParameter('I') && !string.IsNullOrWhiteSpace(code.Result.Content))
                {
                    // Clear output of M409 K"..." I1 case an outdated firmware version is used with this DSF build
                    code.Result.Content = string.Empty;
                }
                break;

            // Select movement queue number
            case 596:
                logger.LogDebug("Requesting full model update after M596");
                await model.WaitForFullUpdateAsync(cancellationToken);        // This changes inputs[].active, so sync the OM here
                logger.LogDebug("Requested full model update after M596");
                break;

            // Fork input reader
            case 606:
                if (code.TryGetInt('S', out int sParam) && sParam == 1)
                {
                    logger.LogDebug("Requesting full model update after M606 S1");
                    await model.WaitForFullUpdateAsync(cancellationToken);    // This changes inputs[].active, so sync the OM here
                    logger.LogDebug("Requested full model update after M606 S1");

                    Link.Channel.Processor.StartCopiedMacros();
                    using (await jobProcessor.LockAsync(cancellationToken))
                    {
                        jobProcessor.StartSecondJob();
                    }
                }
                break;
        }
    }
}
