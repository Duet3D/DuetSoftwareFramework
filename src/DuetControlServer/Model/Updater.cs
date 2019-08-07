using DuetAPI.Machine;
using DuetAPI.Utility;
using Newtonsoft.Json;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace DuetControlServer.Model
{
    /// <summary>
    /// Static helper class to merge the RepRapFirmware object model with ours
    /// </summary>
    public static class Updater
    {
        private static byte _lastUpdatedModule;
        private static readonly AsyncManualResetEvent[] _moduleUpdateEvents = new AsyncManualResetEvent[SPI.Communication.Consts.NumModules];

        /// <summary>
        /// Indicates if this class is ready to accept JSON data
        /// </summary>
        public static bool IsReady
        {
            get => _isReady;
            set => _isReady = value;
        }
        private static volatile bool _isReady;

        /// <summary>
        /// Initialize this class
        /// </summary>
        public static void Init()
        {
            for (int i = 0; i < SPI.Communication.Consts.NumModules; i++)
            {
                _moduleUpdateEvents[i] = new AsyncManualResetEvent();
            }

            IsReady = true;
        }

        /// <summary>
        /// Wait for the model to be fully updated from RepRapFirmware
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForFullUpdate()
        {
            byte module;
            lock (_moduleUpdateEvents)
            {
                module = _lastUpdatedModule;
            }

            await _moduleUpdateEvents[module].WaitAsync();
        }

        /// <summary>
        /// Merge received data into the object model.
        /// This code is temporary and will be replaced once the new object is finished
        /// </summary>
        /// <param name="module">Module that is supposed to be merged</param>
        /// <param name="json">JSON data</param>
        /// <returns>Asynchronous task</returns>
        public static void MergeData(byte module, string json)
        {
            if (!IsReady)
            {
                Console.WriteLine("[warn] Attempting to merge JSON data but not ready");
                return;
            }

            IsReady = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    await DoMerge(module, json);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[err] Caught exception while updating object model: {e}");
                }
                IsReady = true;
            });
        }

        private static float _currentHeight;

        private static async Task DoMerge(byte module, string json)
        {
            if (module == 2)
            {
                // Deserialize extended response (temporary)
                var response = new
                {
                    status = 'I',
                    coords = new
                    {
                        axesHomed = new byte[0],
                        xyz = new float[0],
                        machine = new float[0],
                        extr = new float[0]
                    },
                    speeds = new
                    {
                        requested = 0.0F,
                        top = 0.0F
                    },
                    currentTool = -1,
                    output = new
                    {
                        beepDuration = 0,
                        beepFrequency = 0,
                        message = "",
                        msgBox = new
                        {
                            mode = 0,
                            msg = "",
                            title = "",
                            seq = 0,
                            timeout = 0.0F,
                            controls = 0
                        }
                    },
                    Params = new
                    {
                        atxPower = 0,
                        fanPercent = new float[0],
                        fanNames = new string[0],
                        speedFactor = 0.0F,
                        extrFactors = new float[0],
                        babystep = 0.0F
                    },
                    sensors = new
                    {
                        probeValue = 0,
                        probeSecondary = new int[0],
                        fanRPM = new int[0]
                    },
                    temps = new
                    {
                        bed = new
                        {
                            active = 0.0F,
                            standby = 0.0F,
                            state = 0,
                            heater = 0
                        },
                        chamber = new
                        {
                            active = 0.0F,
                            standby = 0.0F,
                            state = 0,
                            heater = 0
                        },
                        current = new float[0],
                        state = new byte[0],
                        names = new string[0],
                        tools = new
                        {
                            active = new[] { new float[0] },
                            standby = new[] { new float[0] }
                        },
                        extra = new[]
                        {
                            new
                            {
                                name = "",
                                temp = 0.0F
                            }
                        }
                    },
                    coldExtrudeTemp = 160.0F,
                    coldRetractTemp = 90.0F,
                    compensation = "",
                    controllableFans = 0,
                    tempLimit = 0.0F,
                    tools = new[]
                    {
                        new
                        {
                            number = 0,
                            name = "",
                            heaters = new int[0],
                            drives = new int[0],
                            fan = 0,
                            filament = "",
                            offsets = new float[0]
                        }
                    },
                    mcutemp = new
                    {
                        min = 0.0F,
                        cur = 0.0F,
                        max = 0.0F
                    },
                    vin = new
                    {
                        min = 0.0F,
                        cur = 0.0F,
                        max = 0.0F
                    },
                    firmwareName = "",
                    mode = "FFF",
                    name = Network.DefaultHostname
                };
                response = JsonConvert.DeserializeAnonymousType(json, response);
                List<Tool> addedTools = new List<Tool>();

                using (await Provider.AccessReadWriteAsync())
                {
                    // - Electronics -
                    Provider.Get.Electronics.Firmware.Name = response.firmwareName;
                    Provider.Get.Electronics.McuTemp.Current = response.mcutemp.cur;
                    Provider.Get.Electronics.McuTemp.Min = response.mcutemp.min;
                    Provider.Get.Electronics.McuTemp.Max = response.mcutemp.max;
                    Provider.Get.Electronics.VIn.Current = response.vin.cur;
                    Provider.Get.Electronics.VIn.Min = response.vin.min;
                    Provider.Get.Electronics.VIn.Max = response.vin.max;

                    // - Fans -
                    for (int fan = 0; fan < response.Params.fanPercent.Length; fan++)
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

                        fanObj.Name = response.Params.fanNames[fan];
                        fanObj.Rpm = (response.sensors.fanRPM.Length > fan && response.sensors.fanRPM[fan] > 0) ? (int?)response.sensors.fanRPM[fan] : null;
                        fanObj.Value = response.Params.fanPercent[fan] / 100;
                    }
                    for (int fan = Provider.Get.Fans.Count; fan > response.Params.fanPercent.Length; fan--)
                    {
                        Provider.Get.Fans.RemoveAt(fan - 1);
                    }

                    // - Move -
                    Provider.Get.Move.Compensation = response.compensation;
                    Provider.Get.Move.CurrentMove.RequestedSpeed = response.speeds.requested;
                    Provider.Get.Move.CurrentMove.TopSpeed = response.speeds.top;
                    Provider.Get.Move.SpeedFactor = response.Params.speedFactor / 100;
                    Provider.Get.Move.BabystepZ = response.Params.babystep;

                    // Update drives
                    int numDrives = response.coords.xyz.Length + response.coords.extr.Length;
                    for (int drive = 0; drive < numDrives; drive++)
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

                        driveObj.Position = (drive < response.coords.xyz.Length) ? response.coords.xyz[drive] : response.coords.extr[drive - response.coords.xyz.Length];
                    }
                    for (int drive = Provider.Get.Move.Drives.Count; drive > numDrives; drive--)
                    {
                        Provider.Get.Move.Drives.RemoveAt(drive - 1);
                    }

                    // Update axes
                    for (int axis = 0; axis < response.coords.xyz.Length; axis++)
                    {
                        Axis axisObj;
                        if (axis >= Provider.Get.Move.Axes.Count)
                        {
                            axisObj = new Axis();
                            Provider.Get.Move.Axes.Add(axisObj);
                        }
                        else
                        {
                            axisObj = Provider.Get.Move.Axes[axis];
                        }

                        axisObj.Letter = GetAxisLetter(axis);
                        axisObj.Homed = response.coords.axesHomed[axis] != 0;
                        axisObj.MachinePosition = response.coords.machine[axis];
                    }
                    for (int axis = Provider.Get.Move.Axes.Count; axis > response.coords.xyz.Length; axis--)
                    {
                        Provider.Get.Move.Axes.RemoveAt(axis - 1);
                    }

                    // Update extruder drives
                    Extruder extruderObj;
                    for (int extruder = 0; extruder < response.coords.extr.Length; extruder++)
                    {
                        if (extruder >= Provider.Get.Move.Extruders.Count)
                        {
                            extruderObj = new Extruder();
                            Provider.Get.Move.Extruders.Add(extruderObj);
                        }
                        else
                        {
                            extruderObj = Provider.Get.Move.Extruders[extruder];
                        }

                        extruderObj.Factor = response.Params.extrFactors[extruder] / 100;
                        if (extruderObj.Drives.Count == 1)
                        {
                            extruderObj.Drives[0] = response.coords.xyz.Length + extruder;
                        }
                        else
                        {
                            extruderObj.Drives.Add(response.coords.xyz.Length + extruder);
                        }
                    }
                    for (int extruder = Provider.Get.Move.Extruders.Count; extruder > response.coords.extr.Length; extruder--)
                    {
                        Provider.Get.Move.Extruders.RemoveAt(extruder - 1);
                    }

                    // - Heat -
                    Provider.Get.Heat.ColdExtrudeTemperature = response.coldExtrudeTemp;
                    Provider.Get.Heat.ColdRetractTemperature = response.coldRetractTemp;

                    // Update heaters
                    for (int heater = 0; heater < response.temps.current.Length; heater++)
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
                    for (int heater = Provider.Get.Heat.Heaters.Count; heater > response.temps.current.Length; heater--)
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
                            bedObj.Active[0] = response.temps.bed.active;
                            bedObj.Standby[0] = response.temps.bed.standby;
                            bedObj.Heaters[0] = response.temps.bed.heater;
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
                            chamberObj.Active[0] = response.temps.chamber.active;
                            chamberObj.Standby[0] = response.temps.chamber.standby;
                            chamberObj.Heaters[0] = response.temps.chamber.heater;
                        }
                    }
                    else if (Provider.Get.Heat.Chambers.Count > 0)
                    {
                        Provider.Get.Heat.Chambers.Clear();
                    }

                    // Update extra heaters
                    for (int extra = 0; extra < response.temps.extra.Length; extra++)
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
                    for (int extra = Provider.Get.Heat.Extra.Count; extra > response.temps.extra.Length; extra--)
                    {
                        Provider.Get.Heat.Extra.RemoveAt(extra - 1);
                    }

                    // - Sensors -
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

                    probeObj.Value = response.sensors.probeValue;
                    if (response.sensors.probeSecondary != null)
                    {
                        ListHelpers.SetList(probeObj.SecondaryValues, response.sensors.probeSecondary);
                    }

                    // - Output -
                    int beepDuration = 0, beepFrequency = 0;
                    MessageBoxMode? messageBoxMode = null;
                    string displayMessage = null;
                    if (response.output != null)
                    {
                        if (response.output.beepFrequency != 0 && response.output.beepDuration != 0)
                        {
                            beepDuration = response.output.beepFrequency;
                            beepFrequency = response.output.beepDuration;
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
                    Provider.Get.State.AtxPower = (response.Params.atxPower == -1) ? null : (bool?)(response.Params.atxPower != 0);
                    Provider.Get.State.CurrentTool = response.currentTool;
                    Provider.Get.State.Status = GetStatus(response.status);
                    if (Provider.Get.State.Status == MachineStatus.Idle && FileExecution.MacroFile.DoingMacroFile)
                    {
                        // RRF does not always know whether a macro file is being executed
                        Provider.Get.State.Status = MachineStatus.Busy;
                    }
                    Provider.Get.State.Mode = (MachineMode)Enum.Parse(typeof(MachineMode), response.mode, true);
                    Provider.Get.Network.Name = response.name;

                    // - Tools -
                    Tool toolObj;
                    for (int tool = 0; tool < response.tools.Length; tool++)
                    {
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
                        toolObj.FilamentExtruder = (response.tools[tool].drives.Length > 0) ? response.tools[tool].drives[0] : -1;
                        toolObj.Filament = (response.tools[tool].filament != "") ?  response.tools[tool].filament : null;
                        toolObj.Name = (response.tools[tool].name != "") ? response.tools[tool].name : null;
                        toolObj.Number = response.tools[tool].number;
                        ListHelpers.SetList(toolObj.Heaters, response.tools[tool].heaters);
                        ListHelpers.SetList(toolObj.Extruders, response.tools[tool].drives);
                        ListHelpers.SetList(toolObj.Active, response.temps.tools.active[tool]);
                        ListHelpers.SetList(toolObj.Standby, response.temps.tools.standby[tool]);
                        if (toolObj.Fans.Count == 0)
                        {
                            toolObj.Fans.Add(response.tools[tool].fan);
                        }
                        else
                        {
                            toolObj.Fans[0] = response.tools[tool].fan;
                        }
                        ListHelpers.SetList(toolObj.Offsets, response.tools[tool].offsets);
                    }
                    for (int tool = Provider.Get.Tools.Count; tool > response.tools.Length; tool--)
                    {
                        Utility.FilamentManager.ToolRemoved(Provider.Get.Tools[tool - 1]);
                        Provider.Get.Tools.RemoveAt(tool - 1);
                    }
                }

                // Keep track of filaments. Deal with added tools here to avoid deadlocks
                foreach (Tool toolObj in addedTools)
                {
                    await Utility.FilamentManager.ToolAdded(toolObj);
                }
            }

            // Deserialize print status response
            else if (module == 3)
            {
                var printResponse = new
                {
                    status = 'I',
                    coords = new
                    {
                        xyz = new float[3]
                    },
                    currentLayer = 0,
                    currentLayerTime = 0F,
                    filePosition = 0L,
                    firstLayerDuration = 0F,
                    firstLayerHeight = 0F,
                    fractionPrinted = 0F,
                    extrRaw = new float[0],
                    printDuration = 0F,
					warmUpDuration = 0F,
					timesLeft = new
                    {
                        file = 0F,
						filament = 0F,
						layer = 0F
                    }
                };
                printResponse = JsonConvert.DeserializeAnonymousType(json, printResponse);

                using (await Provider.AccessReadWriteAsync())
                {
                    if (printResponse.currentLayer > Provider.Get.Job.Layers.Count + 1)
                    {
                        // Layer complete
                        Layer lastLayer = (Provider.Get.Job.Layers.Count > 0)
                            ? Provider.Get.Job.Layers[Provider.Get.Job.Layers.Count - 1]
                                : new Layer() { Filament = new float[printResponse.extrRaw.Length] };

                        float lastHeight = 0F, lastDuration = 0F, lastProgress = 0F;
                        float[] lastFilamentUsage = new float[printResponse.extrRaw.Length];
                        foreach (Layer l in Provider.Get.Job.Layers)
                        {
                            lastHeight += l.Height;
                            lastDuration += l.Duration;
                            lastProgress += l.FractionPrinted;
                            for (int i = 0; i < Math.Min(lastFilamentUsage.Length, l.Filament.Length); i++)
                            {
                                lastFilamentUsage[i] += l.Filament[i];
                            }
                        }

                        float[] filamentUsage = new float[printResponse.extrRaw.Length];
                        for (int i = 0; i < filamentUsage.Length; i++)
                        {
                            filamentUsage[i] = printResponse.extrRaw[i] - lastFilamentUsage[i];
                        }

                        float printDuration = printResponse.printDuration - printResponse.warmUpDuration;
                        Layer layer = new Layer
                        {
                            Duration = printDuration - lastDuration,
                            Filament = filamentUsage,
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
                    Provider.Get.Job.Duration =  printResponse.printDuration;
                    Provider.Get.Job.WarmUpDuration = printResponse.warmUpDuration;
                    Provider.Get.Job.TimesLeft.File = (printResponse.timesLeft.file > 0F) ? (float?)printResponse.timesLeft.file : null;
                    Provider.Get.Job.TimesLeft.Filament = (printResponse.timesLeft.filament > 0F) ? (float?)printResponse.timesLeft.filament : null;
                    Provider.Get.Job.TimesLeft.Layer = (printResponse.timesLeft.layer > 0F) ? (float?)printResponse.timesLeft.layer : null;
                }
            }

            // Notify waiting threads about the last module updated
            _moduleUpdateEvents[module].Set();
            _moduleUpdateEvents[module].Reset();
            lock (_moduleUpdateEvents)
            {
                _lastUpdatedModule = module;
            }
        }

        private static MachineStatus GetStatus(char letter)
        {
            switch (letter)
            {
                case 'F': return MachineStatus.Updating;
                case 'O': return MachineStatus.Off;
                case 'H': return MachineStatus.Halted;
                case 'D': return MachineStatus.Pausing;
                case 'S': return MachineStatus.Paused;
                case 'R': return MachineStatus.Resuming;
                case 'P': return MachineStatus.Processing;
                case 'M': return MachineStatus.Simulating;
                case 'B': return MachineStatus.Busy;
                case 'T': return MachineStatus.ChangingTool;
            }
            return MachineStatus.Idle;
        }
        
        // temporary - to be replaced with actual axis letter
        private static char GetAxisLetter(int axis)
        {
            switch (axis)
            {
                case 0: return 'X';
                case 1: return 'Y';
                case 2: return 'Z';
                case 3: return 'U';
                case 4: return 'V';
                case 5: return 'W';
                case 6: return 'A';
                case 7: return 'B';
                case 8: return 'C';
            }
            return '?';
        }
    }
}
