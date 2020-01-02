using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.Commands;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Static helper class to merge the RepRapFirmware object model with ours
    /// </summary>
    public static class Updater
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Monitor around the status response
        /// </summary>
        private static readonly AsyncMonitor _monitor = new AsyncMonitor();

        /// <summary>
        /// Type of the received status response
        /// </summary>
        private static byte _module;

        /// <summary>
        /// UTF-8 representation of the received status response
        /// </summary>
        private static readonly byte[] _json = new byte[SPI.Communication.Consts.BufferSize];

        /// <summary>
        /// Length of the received status response
        /// </summary>
        private static int _jsonLength;

        /// <summary>
        /// Monitor that is notified when the full object model has been updated
        /// </summary>
        private static readonly AsyncMonitor _updateEvent = new AsyncMonitor();

        /// <summary>
        /// Last-known Z height
        /// </summary>
        private static float _currentHeight;

        /// <summary>
        /// Indicates if the firmware is being updated
        /// </summary>
        private static bool _updatingFirmware;

        /// <summary>
        /// Wait for the model to be fully updated from RepRapFirmware
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForFullUpdate()
        {
            using (await _updateEvent.EnterAsync())
            {
                await _updateEvent.WaitAsync(Program.CancelSource.Token);
                Program.CancelSource.Token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Merge a received status response into the object model
        /// </summary>
        /// <param name="module">Module that is supposed to be merged</param>
        /// <param name="json">JSON data</param>
        /// <returns>Asynchronous task</returns>
        public static async Task ProcessResponse(byte module, Memory<byte> json)
        {
            using (await _monitor.EnterAsync(Program.CancelSource.Token))
            {
                _module = module;
                json.CopyTo(_json);
                _jsonLength = json.Length;

                _monitor.Pulse();
            }
        }

        /// <summary>
        /// Process status updates in the background
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            CancellationToken cancellationToken = Program.CancelSource.Token;

            do
            {
                // Wait for the next status response
                using (await _monitor.EnterAsync(cancellationToken))
                {
                    await _monitor.WaitAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Process it
                    try
                    {
                        ReadOnlyMemory<byte> json = _json.AsMemory().Slice(0, _jsonLength);
                        switch (_module)
                        {
                            case 2:
                                await ProcessAdvancedResponse(json);
                                break;

                            case 3:
                                await ProcessPrintResponse(json);
                                break;

                            case 5:
                                await ProcessConfigResponse(json);
                                break;
                        }

                        // Notify waiting clients about the model update
                        if (_module == 2)
                        {
                            _updateEvent.PulseAll();
                        }
                    }
                    catch (JsonException e)
                    {
                        _logger.Error(e, "Failed to merge JSON (module {0})", _module);
                    }
                }
            }
            while (true);
        }

        /// <summary>
        /// Process an advanced status reponse (type 2)
        /// </summary>
        /// <param name="json">JSON response in UTF-8 format</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ProcessAdvancedResponse(ReadOnlyMemory<byte> json)
        {
            AdvancedStatusResponse response = (AdvancedStatusResponse)JsonSerializer.Deserialize(json.Span, typeof(AdvancedStatusResponse), JsonHelper.DefaultJsonOptions);

            List<Tool> addedTools = new List<Tool>();
            using (await Provider.AccessReadWriteAsync())
            {
                // - Electronics -
                Provider.Get.Electronics.McuTemp.Current = response.mcutemp.cur;
                Provider.Get.Electronics.McuTemp.Min = response.mcutemp.min;
                Provider.Get.Electronics.McuTemp.Max = response.mcutemp.max;
                Provider.Get.Electronics.VIn.Current = response.vin.cur;
                Provider.Get.Electronics.VIn.Min = response.vin.min;
                Provider.Get.Electronics.VIn.Max = response.vin.max;

                // - Fans -
                for (int fan = 0; fan < response.@params.fanPercent.Count; fan++)
                {
                    Fan fanObj;
                    if (fan >= Provider.Get.Fans.Count)
                    {
                        fanObj = new Fan();
                        Provider.Get.Fans.Add(fanObj);
                    }
                    else
                    {
                        fanObj = Provider.Get.Fans[fan];
                    }

                    fanObj.Name = response.@params.fanNames[fan];
                    fanObj.Rpm = (response.sensors.fanRPM.Count > fan && response.sensors.fanRPM[fan] > 0) ? (int?)response.sensors.fanRPM[fan] : null;
                    fanObj.Value = response.@params.fanPercent[fan] / 100F;
                    fanObj.Thermostatic.Control = (response.controllableFans & (1 << fan)) == 0;
                }
                for (int fan = Provider.Get.Fans.Count; fan > response.@params.fanPercent.Count; fan--)
                {
                    Provider.Get.Fans.RemoveAt(fan - 1);
                }

                // - Move -
                Provider.Get.Move.Compensation = response.compensation;
                Provider.Get.Move.CurrentMove.RequestedSpeed = response.speeds.requested;
                Provider.Get.Move.CurrentMove.TopSpeed = response.speeds.top;
                Provider.Get.Move.SpeedFactor = response.@params.speedFactor / 100;
                Provider.Get.Move.BabystepZ = response.@params.babystep;
                Provider.Get.Move.CurrentWorkplace = response.coords.wpl;

                // Update drives and endstops
                for (int drive = 0; drive < Provider.Get.Move.Drives.Count; drive++)
                {
                    Drive driveObj = Provider.Get.Move.Drives[drive];

                    if (drive < response.axes)
                    {
                        driveObj.Position = response.coords.xyz[drive];
                    }
                    else if (drive < response.axes + response.coords.extr.Count)
                    {
                        driveObj.Position = response.coords.extr[drive - response.axes];
                    }
                    else
                    {
                        driveObj.Position = null;
                    }

                    Endstop endstopObj;
                    if (drive >= Provider.Get.Sensors.Endstops.Count)
                    {
                        endstopObj = new Endstop();
                        Provider.Get.Sensors.Endstops.Add(endstopObj);
                    }
                    else
                    {
                        endstopObj = Provider.Get.Sensors.Endstops[drive];
                    }
                    endstopObj.Triggered = (response.endstops & (1 << drive)) != 0;
                }

                // Update axes
                int axis;
                for (axis = 0; axis < response.totalAxes; axis++)
                {
                    Axis axisObj;
                    if (axis >= Provider.Get.Move.Axes.Count)
                    {
                        axisObj = new Axis();
                        Provider.Get.Move.Axes.Add(axisObj);

                        axisObj.Drives.Add(axis);
                    }
                    else
                    {
                        axisObj = Provider.Get.Move.Axes[axis];

                        if (axisObj.Drives[0] != axis)
                        {
                            axisObj.Drives[0] = axis;
                        }
                    }

                    axisObj.Letter = response.axisNames[axis];
                    if (axis < response.coords.xyz.Count)
                    {
                        axisObj.Homed = response.coords.axesHomed[axis] != 0;
                        axisObj.MachinePosition = response.coords.machine[axis];
                        axisObj.Visible = true;
                    }
                    else
                    {
                        axisObj.Homed = true;
                        axisObj.MachinePosition = null;
                        axisObj.Visible = false;
                    }
                    axisObj.MinEndstop = axis;
                    axisObj.MaxEndstop = axis;
                }
                for (axis = Provider.Get.Move.Axes.Count; axis > response.totalAxes; axis--)
                {
                    Provider.Get.Move.Axes.RemoveAt(axis - 1);
                }

                // Update extruder drives
                int extruder;
                for (extruder = 0; extruder < response.coords.extr.Count; extruder++)
                {
                    Extruder extruderObj;
                    if (extruder >= Provider.Get.Move.Extruders.Count)
                    {
                        extruderObj = new Extruder();
                        Provider.Get.Move.Extruders.Add(extruderObj);
                    }
                    else
                    {
                        extruderObj = Provider.Get.Move.Extruders[extruder];
                    }

                    extruderObj.Factor = response.@params.extrFactors[extruder] / 100F;
                    if (extruderObj.Drives.Count == 1)
                    {
                        int drive = response.coords.xyz.Count + extruder;
                        if (extruderObj.Drives[0] != drive)
                        {
                            extruderObj.Drives[0] = drive;
                        }
                    }
                    else
                    {
                        extruderObj.Drives.Add(response.coords.xyz.Count + extruder);
                    }
                }
                for (extruder = Provider.Get.Move.Extruders.Count; extruder > response.coords.extr.Count; extruder--)
                {
                    Provider.Get.Move.Extruders.RemoveAt(extruder - 1);
                }

                // - Heat -
                Provider.Get.Heat.ColdExtrudeTemperature = response.coldExtrudeTemp;
                Provider.Get.Heat.ColdRetractTemperature = response.coldRetractTemp;

                // Update heaters
                int heater;
                for (heater = 0; heater < response.temps.current.Count; heater++)
                {
                    Heater heaterObj;
                    if (heater >= Provider.Get.Heat.Heaters.Count)
                    {
                        heaterObj = new Heater();
                        Provider.Get.Heat.Heaters.Add(heaterObj);
                    }
                    else
                    {
                        heaterObj = Provider.Get.Heat.Heaters[heater];
                    }

                    heaterObj.Current = response.temps.current[heater];
                    heaterObj.Max = response.tempLimit;
                    heaterObj.Name = response.temps.names[heater];
                    heaterObj.Sensor = heater;
                    heaterObj.State = (HeaterState)response.temps.state[heater];
                }
                for (heater = Provider.Get.Heat.Heaters.Count; heater > response.temps.current.Count; heater--)
                {
                    Provider.Get.Heat.Heaters.RemoveAt(heater - 1);
                }

                // Update beds
                if (response.temps.bed != null)
                {
                    BedOrChamber bedObj;
                    if (Provider.Get.Heat.Beds.Count == 0)
                    {
                        bedObj = new BedOrChamber();
                        Provider.Get.Heat.Beds.Add(bedObj);
                    }
                    else
                    {
                        bedObj = Provider.Get.Heat.Beds[0];
                    }

                    if (bedObj.Heaters.Count == 0)
                    {
                        bedObj.Active.Add(response.temps.bed.active);
                        bedObj.Standby.Add(response.temps.bed.standby);
                        bedObj.Heaters.Add(response.temps.bed.heater);
                    }
                    else
                    {
                        if (bedObj.Active[0] != response.temps.bed.active) { bedObj.Active[0] = response.temps.bed.active; }
                        if (bedObj.Standby[0] != response.temps.bed.standby) { bedObj.Standby[0] = response.temps.bed.standby; }
                        if (bedObj.Heaters[0] != response.temps.bed.heater) { bedObj.Heaters[0] = response.temps.bed.heater; }
                    }
                }
                else if (Provider.Get.Heat.Beds.Count > 0)
                {
                    Provider.Get.Heat.Beds.Clear();
                }

                // Update chambers
                if (response.temps.chamber != null)
                {
                    BedOrChamber chamberObj;
                    if (Provider.Get.Heat.Chambers.Count == 0)
                    {
                        chamberObj = new BedOrChamber();
                        Provider.Get.Heat.Chambers.Add(chamberObj);
                    }
                    else
                    {
                        chamberObj = Provider.Get.Heat.Chambers[0];
                    }

                    if (chamberObj.Heaters.Count == 0)
                    {
                        chamberObj.Active.Add(response.temps.chamber.active);
                        chamberObj.Standby.Add(response.temps.chamber.standby);
                        chamberObj.Heaters.Add(response.temps.chamber.heater);
                    }
                    else
                    {
                        if (chamberObj.Active[0] != response.temps.chamber.active) { chamberObj.Active[0] = response.temps.chamber.active; }
                        if (chamberObj.Standby[0] != response.temps.chamber.standby) { chamberObj.Standby[0] = response.temps.chamber.standby; }
                        if (chamberObj.Heaters[0] != response.temps.chamber.heater) { chamberObj.Heaters[0] = response.temps.chamber.heater; }
                    }
                }
                else if (Provider.Get.Heat.Chambers.Count > 0)
                {
                    Provider.Get.Heat.Chambers.Clear();
                }

                // Update extra heaters
                int extra;
                for (extra = 0; extra < response.temps.extra.Count; extra++)
                {
                    ExtraHeater extraObj;
                    if (extra >= Provider.Get.Heat.Extra.Count)
                    {
                        extraObj = new ExtraHeater();
                        Provider.Get.Heat.Extra.Add(extraObj);
                    }
                    else
                    {
                        extraObj = Provider.Get.Heat.Extra[extra];
                    }

                    extraObj.Current = response.temps.extra[extra].temp;
                    extraObj.Name = response.temps.extra[extra].name;
                }
                for (extra = Provider.Get.Heat.Extra.Count; extra > response.temps.extra.Count; extra--)
                {
                    Provider.Get.Heat.Extra.RemoveAt(extra - 1);
                }

                // - Sensors -
                if (response.probe.type != 0)
                {
                    Probe probeObj;
                    if (Provider.Get.Sensors.Probes.Count == 0)
                    {
                        probeObj = new Probe();
                        Provider.Get.Sensors.Probes.Add(probeObj);
                    }
                    else
                    {
                        probeObj = Provider.Get.Sensors.Probes[0];
                    }

                    probeObj.Type = (ProbeType)response.probe.type;
                    probeObj.Value = response.sensors.probeValue;
                    probeObj.Threshold = response.probe.threshold;
                    probeObj.TriggerHeight = response.probe.height;
                    if (response.sensors.probeSecondary != null)
                    {
                        ListHelpers.SetList(probeObj.SecondaryValues, response.sensors.probeSecondary);
                    }
                    else if (probeObj.SecondaryValues.Count > 0)
                    {
                        probeObj.SecondaryValues.Clear();
                    }
                }
                else if (Provider.Get.Sensors.Probes.Count != 0)
                {
                    Provider.Get.Sensors.Probes.Clear();
                }

                // - Output -
                int beepDuration = 0, beepFrequency = 0;
                MessageBoxMode? messageBoxMode = null;
                string displayMessage = string.Empty;
                if (response.output != null)
                {
                    if (response.output.beepFrequency != 0 && response.output.beepDuration != 0)
                    {
                        beepDuration = response.output.beepDuration;
                        beepFrequency = response.output.beepFrequency;
                    }
                    displayMessage = response.output.message;
                    if (response.output.msgBox != null)
                    {
                        messageBoxMode = (MessageBoxMode)response.output.msgBox.mode;
                        Provider.Get.MessageBox.Title = response.output.msgBox.title;
                        Provider.Get.MessageBox.Message = response.output.msgBox.msg;
                        for (int i = 0; i < 9; i++)
                        {
                            if ((response.output.msgBox.controls & (1 << i)) != 0)
                            {
                                if (!Provider.Get.MessageBox.AxisControls.Contains(i))
                                {
                                    Provider.Get.MessageBox.AxisControls.Add(i);
                                }
                            }
                            else if (Provider.Get.MessageBox.AxisControls.Contains(i))
                            {
                                Provider.Get.MessageBox.AxisControls.Remove(i);
                            }
                        }
                        Provider.Get.MessageBox.Seq = response.output.msgBox.seq;
                    }
                }
                Provider.Get.State.Beep.Duration = beepDuration;
                Provider.Get.State.Beep.Frequency = beepFrequency;
                Provider.Get.State.DisplayMessage = displayMessage;
                Provider.Get.MessageBox.Mode = messageBoxMode;

                // - State -
                MachineStatus oldStatus = Provider.Get.State.Status, newStatus = GetStatus(response.status);
                if ((newStatus == MachineStatus.Idle || newStatus == MachineStatus.Off) &&
                    (oldStatus == MachineStatus.Paused || oldStatus == MachineStatus.Pausing || oldStatus == MachineStatus.Processing ||
                     oldStatus == MachineStatus.Resuming || oldStatus == MachineStatus.Simulating))
                {
                    // No longer processing a file...
                    Provider.Get.Job.File.Assign(new DuetAPI.ParsedFileInfo());
                    Provider.Get.Job.FilePosition = null;
                    Provider.Get.Job.TimesLeft.Assign(new DuetAPI.Machine.TimesLeft());
                }
                Provider.Get.State.AtxPower = (response.@params.atxPower == -1) ? null : (bool?)(response.@params.atxPower != 0);
                Provider.Get.State.CurrentTool = response.currentTool;
                Provider.Get.State.Status = newStatus;
                Provider.Get.State.Mode = (MachineMode)Enum.Parse(typeof(MachineMode), response.mode, true);
                Provider.Get.Network.Name = response.name;

                // - Tools -
                int tool;
                for (tool = 0; tool < response.tools.Count; tool++)
                {
                    Tool toolObj;
                    if (tool >= Provider.Get.Tools.Count)
                    {
                        toolObj = new Tool();
                        Provider.Get.Tools.Add(toolObj);
                        addedTools.Add(toolObj);
                    }
                    else
                    {
                        toolObj = Provider.Get.Tools[tool];
                    }

                    // FIXME: The filament drive is not part of the status response / OM yet
                    toolObj.FilamentExtruder = (response.tools[tool].drives.Count > 0) ? response.tools[tool].drives[0] : -1;
                    toolObj.Filament = string.IsNullOrEmpty(response.tools[tool].filament) ? null : response.tools[tool].filament;
                    toolObj.Name = string.IsNullOrEmpty(response.tools[tool].name) ? null : response.tools[tool].name;
                    toolObj.Number = response.tools[tool].number;
                    ListHelpers.SetList(toolObj.Heaters, response.tools[tool].heaters);
                    ListHelpers.SetList(toolObj.Extruders, response.tools[tool].drives);
                    ListHelpers.SetList(toolObj.Active, response.temps.tools.active[tool]);
                    ListHelpers.SetList(toolObj.Standby, response.temps.tools.standby[tool]);

                    List<int> fanIndices = new List<int>();
                    for (int i = 0; i < response.@params.fanPercent.Count; i++)
                    {
                        if ((response.tools[tool].fans & (1 << i)) != 0)
                        {
                            fanIndices.Add(i);
                        }
                    }
                    ListHelpers.SetList(toolObj.Fans, fanIndices);
                    ListHelpers.SetList(toolObj.Offsets, response.tools[tool].offsets);
                }
                for (tool = Provider.Get.Tools.Count; tool > response.tools.Count; tool--)
                {
                    Utility.FilamentManager.ToolRemoved(Provider.Get.Tools[tool - 1]);
                    Provider.Get.Tools.RemoveAt(tool - 1);
                }
            }

            // Notify FilamentManager about added tools. Deal with them here to avoid deadlocks
            foreach (Tool toolObj in addedTools)
            {
                await Utility.FilamentManager.ToolAdded(toolObj);
            }
        }

        /// <summary>
        /// Process a print status response (type 3)
        /// </summary>
        /// <param name="json">JSON response in UTF-8 format</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ProcessPrintResponse(ReadOnlyMemory<byte> json)
        {
            PrintStatusResponse printResponse = (PrintStatusResponse)JsonSerializer.Deserialize(json.Span, typeof(PrintStatusResponse), JsonHelper.DefaultJsonOptions);

            using (await Provider.AccessReadWriteAsync())
            {
                // Check if the last layer is complete
                if (printResponse.currentLayer > Provider.Get.Job.Layers.Count + 1)
                {
                    float lastHeight = 0F, lastDuration = 0F, lastProgress = 0F;
                    float[] lastFilamentUsage = new float[printResponse.extrRaw.Count];
                    foreach (Layer l in Provider.Get.Job.Layers)
                    {
                        lastHeight += l.Height;
                        lastDuration += l.Duration;
                        lastProgress += l.FractionPrinted;
                        for (int i = 0; i < Math.Min(lastFilamentUsage.Length, l.Filament.Count); i++)
                        {
                            lastFilamentUsage[i] += l.Filament[i];
                        }
                    }

                    float[] filamentUsage = new float[printResponse.extrRaw.Count];
                    for (int i = 0; i < filamentUsage.Length; i++)
                    {
                        filamentUsage[i] = printResponse.extrRaw[i] - lastFilamentUsage[i];
                    }

                    float printDuration = printResponse.printDuration - printResponse.warmUpDuration;
                    Layer layer = new Layer
                    {
                        Duration = printDuration - lastDuration,
                        Filament = new List<float>(filamentUsage),
                        FractionPrinted = (printResponse.fractionPrinted / 100F) - lastProgress,
                        Height = (printResponse.currentLayer > 2) ? _currentHeight - lastHeight : printResponse.firstLayerHeight
                    };
                    Provider.Get.Job.Layers.Add(layer);

                    // FIXME: In case Z isn't mapped to the 3rd axis...
                    _currentHeight = printResponse.coords.xyz[2];
                }
                else if (printResponse.currentLayer < Provider.Get.Job.Layers.Count && GetStatus(printResponse.status) == MachineStatus.Processing)
                {
                    // Starting a new print job
                    Provider.Get.Job.Layers.Clear();
                    _currentHeight = 0F;
                }

                Provider.Get.Job.Layer = printResponse.currentLayer;
                Provider.Get.Job.LayerTime = (printResponse.currentLayer == 1) ? printResponse.firstLayerDuration : printResponse.currentLayerTime;
                Provider.Get.Job.FilePosition = printResponse.filePosition;
                ListHelpers.SetList(Provider.Get.Job.ExtrudedRaw, printResponse.extrRaw);
                Provider.Get.Job.Duration = printResponse.printDuration;
                Provider.Get.Job.WarmUpDuration = printResponse.warmUpDuration;
                Provider.Get.Job.TimesLeft.File = (printResponse.timesLeft.file > 0F) ? (float?)printResponse.timesLeft.file : null;
                Provider.Get.Job.TimesLeft.Filament = (printResponse.timesLeft.filament > 0F) ? (float?)printResponse.timesLeft.filament : null;
                Provider.Get.Job.TimesLeft.Layer = (printResponse.timesLeft.layer > 0F) ? (float?)printResponse.timesLeft.layer : null;
            }
        }

        /// <summary>
        /// Process a config response
        /// </summary>
        /// <param name="json">JSON response in UTF-8 format</param>
        /// <returns>Asynchronous task</returns>
        private static async Task ProcessConfigResponse(ReadOnlyMemory<byte> json)
        {
            ConfigResponse configResponse = (ConfigResponse)JsonSerializer.Deserialize(json.Span, typeof(ConfigResponse), JsonHelper.DefaultJsonOptions);

            if (configResponse.axisMins == null)
            {
                _logger.Warn("Config response unsupported. Update your firmware!");
                return;
            }

            using (await Provider.AccessReadWriteAsync())
            {
                // - Axes -
                for (int axis = 0; axis < Math.Min(Provider.Get.Move.Axes.Count, configResponse.axisMins.Count); axis++)
                {
                    Provider.Get.Move.Axes[axis].Min = configResponse.axisMins[axis];
                    Provider.Get.Move.Axes[axis].Max = configResponse.axisMaxes[axis];
                }

                // - Drives -
                int drive;
                for (drive = 0; drive < configResponse.accelerations.Count; drive++)
                {
                    Drive driveObj;
                    if (drive >= Provider.Get.Move.Drives.Count)
                    {
                        driveObj = new Drive();
                        Provider.Get.Move.Drives.Add(driveObj);
                    }
                    else
                    {
                        driveObj = Provider.Get.Move.Drives[drive];
                    }

                    driveObj.Acceleration = configResponse.accelerations[drive];
                    driveObj.Current = configResponse.currents[drive];
                    driveObj.MinSpeed = configResponse.minFeedrates[drive];
                    driveObj.MaxSpeed = configResponse.maxFeedrates[drive];
                }
                for (drive = Provider.Get.Move.Drives.Count; drive > configResponse.accelerations.Count; drive--)
                {
                    Provider.Get.Move.Drives.RemoveAt(drive - 1);
                    Provider.Get.Sensors.Endstops.RemoveAt(drive - 1);
                }

                // - Electronics -
                Provider.Get.Electronics.Name = configResponse.firmwareElectronics;
                Provider.Get.Electronics.ShortName = configResponse.boardName;
                switch (Provider.Get.Electronics.ShortName)
                {
                    case "MBP05":
                        Provider.Get.Electronics.Revision = "0.5";
                        break;
                    case "MB6HC":
                        Provider.Get.Electronics.Revision = "0.6";
                        break;
                }
                Provider.Get.Electronics.Firmware.Name = configResponse.firmwareName;
                Provider.Get.Electronics.Firmware.Version = configResponse.firmwareVersion;
                Provider.Get.Electronics.Firmware.Date = configResponse.firmwareDate;

                // - Move -
                Provider.Get.Move.Idle.Factor = configResponse.idleCurrentFactor / 100F;
                Provider.Get.Move.Idle.Timeout = configResponse.idleTimeout;
            }

            // Check if the firmware is supposed to be updated only. When this finishes, DCS is terminated.
            if (configResponse.boardName != null && Settings.UpdateOnly && !_updatingFirmware)
            {
                _updatingFirmware = true;
                Console.Write("Updating the firmware... ");

                Code updateCode = new Code
                {
                    Type = DuetAPI.Commands.CodeType.MCode,
                    MajorNumber = 997,
                    Flags = DuetAPI.Commands.CodeFlags.IsPrioritized
                };
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await updateCode.Execute();
                        Console.WriteLine("Done!");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error: Failed to send update request");
                        Console.WriteLine(e);
                    }
                });
            }
        }

        /// <summary>
        /// Translate a status character to a MachineStatus enum value
        /// </summary>
        /// <param name="letter">Status character</param>
        /// <returns>Machine status</returns>
        private static MachineStatus GetStatus(char letter)
        {
            return letter switch
            {
                'F' => MachineStatus.Updating,
                'O' => MachineStatus.Off,
                'H' => MachineStatus.Halted,
                'D' => MachineStatus.Pausing,
                'S' => MachineStatus.Paused,
                'R' => MachineStatus.Resuming,
                'P' => MachineStatus.Processing,
                'M' => MachineStatus.Simulating,
                'B' => MachineStatus.Busy,
                'T' => MachineStatus.ChangingTool,
                _ => MachineStatus.Idle,
            };
        }
    }
}
