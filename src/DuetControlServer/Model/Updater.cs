using DuetAPI;
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
        private static volatile bool _updating;
        private static byte _lastUpdatedModule;
        private static readonly AsyncManualResetEvent[] _moduleUpdateEvents = new AsyncManualResetEvent[SPI.Communication.Consts.NumModules];

        /// <summary>
        /// Initialize this class
        /// </summary>
        public static void Init()
        {
            for (int i = 0; i < SPI.Communication.Consts.NumModules; i++)
            {
                _moduleUpdateEvents[i] = new AsyncManualResetEvent();
            }

            using (Provider.AccessReadWrite())
            {
                foreach (CodeChannel channel in (CodeChannel[])Enum.GetValues(typeof(CodeChannel)))
                {
                    Provider.Get.Channels[channel].PropertyChanged += PropertyHasChanged;
                }
                Provider.Get.Electronics.PropertyChanged += PropertyHasChanged;
                Provider.Get.Electronics.ExpansionBoards.CollectionChanged += CollectionHasChanged;
                Provider.Get.Fans.CollectionChanged += CollectionHasChanged;
                Provider.Get.Heat.Beds.CollectionChanged += CollectionHasChanged;
                Provider.Get.Heat.Chambers.CollectionChanged += CollectionHasChanged;
                Provider.Get.Job.PropertyChanged += PropertyHasChanged;
                Provider.Get.Job.Layers.CollectionChanged += CollectionHasChanged;
                Provider.Get.MessageBox.PropertyChanged += PropertyHasChanged;
                //Provider.Get.Messages.CollectionChanged += CollectionHasChanged;
                Provider.Get.Move.PropertyChanged += PropertyHasChanged;
                Provider.Get.Move.Extruders.CollectionChanged += CollectionHasChanged;
                Provider.Get.Move.Geometry.PropertyChanged += PropertyHasChanged;
                Provider.Get.Move.Geometry.Anchors.CollectionChanged += CollectionHasChanged;
                Provider.Get.Move.Geometry.AngleCorrections.CollectionChanged += CollectionHasChanged;
                Provider.Get.Move.Idle.PropertyChanged += PropertyHasChanged;
                Provider.Get.Move.WorkplaceCoordinates.CollectionChanged += CollectionHasChanged;
                Provider.Get.Network.PropertyChanged += PropertyHasChanged;
                Provider.Get.Network.Interfaces.CollectionChanged += CollectionHasChanged;
                Provider.Get.Scanner.PropertyChanged += PropertyHasChanged;
                Provider.Get.Sensors.Endstops.CollectionChanged += CollectionHasChanged;
                Provider.Get.Sensors.Probes.CollectionChanged += CollectionHasChanged;
                Provider.Get.Spindles.CollectionChanged += CollectionHasChanged;
                Provider.Get.State.PropertyChanged += PropertyHasChanged;
                Provider.Get.Storages.CollectionChanged += CollectionHasChanged;
                Provider.Get.Tools.CollectionChanged += CollectionHasChanged;
                Provider.Get.UserVariables.CollectionChanged += CollectionHasChanged;
            }
        }

        private static void CollectionHasChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                for (int i = 0; i < e.OldItems.Count; i++)
                {
                    if (e.OldItems[i] is INotifyPropertyChanged obj)
                    {
                        // Unsubscribe from existing items in order to prevent a memory leak...
                        obj.PropertyChanged -= PropertyHasChanged;
                    }
                }
            }

            if (e.NewItems != null)
            {
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    if (e.NewItems[i] is INotifyPropertyChanged obj)
                    {
                        // Subscribe for potential updates again.
                        // We do not subscribe to sub-collections/sub-types yet so it is possible that this has to be changed again...
                        obj.PropertyChanged += PropertyHasChanged;
                    }
                }
            }
        }

        private static void PropertyHasChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Notify clients about new data
            IPC.Processors.Subscription.ModelUpdated();
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
        public static async void MergeData(byte module, string json)
        {
            if (_updating)
            {
                return;
            }

            _updating = true;
            await DoMerge(module, json);
            _updating = false;
        }

        private static async Task DoMerge(byte module, string json)
        {
            // Console.WriteLine($"Got object model for module {module}: {json}");

            if (module == 2)
            {
                // Deserialize extended response (temporary)
                var responseDefinition = new
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
                            msg = "",
                            title = "",
                            seq = 0,
                            timeout = 0,
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
                            extruders = new int[0],
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
                    firmwareName = ""
                };

                // FIXME: I bet this call causes a memory leak for some reason:
                var response = JsonConvert.DeserializeAnonymousType(json, responseDefinition);
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
                    Fan[] fans = new Fan[response.Params.fanPercent.Length];
                    for (int fan = 0; fan < response.Params.fanPercent.Length; fan++)
                    {
                        Fan fanObj = new Fan
                        {
                            Name = response.Params.fanNames[fan],
                            Rpm = (response.sensors.fanRPM.Length > fan && response.sensors.fanRPM[fan] > 0) ? (int?)response.sensors.fanRPM[fan] : null,
                            Value = response.Params.fanPercent[fan] / 100
                        };
                        fanObj.Thermostatic.Control = (response.controllableFans & (1 << fan)) == 0;
                        fans[fan] = fanObj;
                    }
                    ListHelpers.AssignList(Provider.Get.Fans, fans);

                    // - Move -
                    Provider.Get.Move.Compensation = response.compensation;
                    Provider.Get.Move.CurrentMove.RequestedSpeed = response.speeds.requested;
                    Provider.Get.Move.CurrentMove.TopSpeed = response.speeds.top;
                    Provider.Get.Move.SpeedFactor = response.Params.speedFactor / 100;
                    Provider.Get.Move.BabystepZ = response.Params.babystep;

                    Drive[] drives = new Drive[response.coords.xyz.Length + response.coords.extr.Length];

                    // Update axes
                    Axis[] axes = new Axis[response.coords.xyz.Length];
                    for (int axis = 0; axis < response.coords.xyz.Length; axis++)
                    {
                        Axis axisObj = new Axis
                        {
                            Letter = GetAxisLetter(axis),
                            Homed = response.coords.axesHomed[axis] != 0,
                            MachinePosition = response.coords.machine[axis]
                        };
                        axisObj.Drives.Add(axis);
                        axes[axis] = axisObj;

                        drives[axis] = new Drive
                        {
                            Position = response.coords.xyz[axis]
                        };
                    }
                    ListHelpers.AssignList(Provider.Get.Move.Axes, axes);

                    // Update extruder drives
                    Extruder[] extruders = new Extruder[response.coords.extr.Length];
                    for (int extruder = 0; extruder < response.coords.extr.Length; extruder++)
                    {
                        Extruder extruderObj = new Extruder
                        {
                            Factor = response.Params.extrFactors[extruder] / 100
                        };
                        extruderObj.Drives.Add(response.coords.xyz.Length + extruder);
                        extruders[extruder] = extruderObj;

                        drives[response.coords.xyz.Length + extruder] = new Drive
                        {
                            Position = response.coords.extr[extruder]
                        };
                    }
                    ListHelpers.AssignList(Provider.Get.Move.Extruders, extruders);

                    ListHelpers.AssignList(Provider.Get.Move.Drives, drives);

                    // - Heat -
                    Provider.Get.Heat.ColdExtrudeTemperature = response.coldExtrudeTemp;
                    Provider.Get.Heat.ColdRetractTemperature = response.coldRetractTemp;

                    // Update beds
                    BedOrChamber[] beds;
                    if (response.temps.bed != null)
                    {
                        beds = new BedOrChamber[1];

                        BedOrChamber bed = new BedOrChamber();
                        bed.Active.Add(response.temps.bed.active);
                        bed.Standby.Add(response.temps.bed.standby);
                        bed.Heaters.Add(response.temps.bed.heater);
                        beds[0] = bed;
                    }
                    else
                    {
                        beds = new BedOrChamber[0];
                    }
                    ListHelpers.AssignList(Provider.Get.Heat.Beds, beds);

                    // Update chambers
                    BedOrChamber[] chambers;
                    if (response.temps.chamber != null)
                    {
                        chambers = new BedOrChamber[1];

                        BedOrChamber chamber = new BedOrChamber();
                        chamber.Active.Add(response.temps.chamber.active);
                        chamber.Standby.Add(response.temps.chamber.standby);
                        chamber.Heaters.Add(response.temps.chamber.heater);
                        chambers[0] = chamber;
                    }
                    else
                    {
                        chambers = new BedOrChamber[0];
                    }
                    ListHelpers.AssignList(Provider.Get.Heat.Chambers, chambers);

                    // Update heaters
                    Heater[] heaters = new Heater[response.temps.current.Length];
                    for (int heater = 0; heater < response.temps.current.Length; heater++)
                    {
                        heaters[heater] = new Heater
                        {
                            Current = response.temps.current[heater],
                            Max = response.tempLimit,
                            Name = response.temps.names[heater],
                            Sensor = heater,
                            State = (HeaterState)response.temps.state[heater]
                        };
                    }
                    ListHelpers.AssignList(Provider.Get.Heat.Heaters, heaters);

                    // Update extra heaters
                    ExtraHeater[] extraHeaters = new ExtraHeater[response.temps.extra.Length];
                    for (int extra = 0; extra < response.temps.extra.Length; extra++)
                    {
                        extraHeaters[extra] = new ExtraHeater
                        {
                            Current = response.temps.extra[extra].temp,
                            Name = response.temps.extra[extra].name
                        };
                    }
                    ListHelpers.AssignList(Provider.Get.Heat.Extra, extraHeaters);

                    // - Sensors -
                    Probe[] probes = new Probe[1];
                    Probe probe = new Probe()
                    {
                        Value = response.sensors.probeValue
                    };
                    if (response.sensors.probeSecondary != null)
                    {
                        foreach (int secondaryValue in response.sensors.probeSecondary)
                        {
                            probe.SecondaryValues.Add(secondaryValue);
                        }
                    }
                    probes[0] = probe;
                    ListHelpers.AssignList(Provider.Get.Sensors.Probes, probes);

                    // - State -
                    Provider.Get.State.AtxPower = (response.Params.atxPower == -1) ? null : (bool?)(response.Params.atxPower != 0);
                    if (response.output != null)
                    {
                        if (response.output.beepFrequency != 0 && response.output.beepDuration != 0)
                        {
                            Provider.Get.State.Beep.Frequency = response.output.beepFrequency;
                            Provider.Get.State.Beep.Duration = response.output.beepDuration;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(response.output.beepDuration);
                                using (await Provider.AccessReadWriteAsync())
                                {
                                    Provider.Get.State.Beep.Duration = 0;
                                    Provider.Get.State.Beep.Frequency = 0;
                                }
                            }, Program.CancelSource.Token);
                        }
                        Provider.Get.State.DisplayMessage = response.output.message;
                    }
                    Provider.Get.State.CurrentTool = response.currentTool;
                    Provider.Get.State.Status = GetStatus(response.status);
                    if (Provider.Get.State.Status == MachineStatus.Idle && FileExecution.MacroFile.DoingMacroFile)
                    {
                        // RRF does not always know whether a macro file is being executed
                        Provider.Get.State.Status = MachineStatus.Busy;
                    }

                    // - Tools -
                    Tool[] tools = new Tool[response.tools.Length];
                    for (int tool = 0; tool < response.tools.Length; tool++)
                    {
                        Tool toolObj = new Tool
                        {
                            Filament = response.tools[tool].filament,
                            Name = (response.tools[tool].name == "") ? null : response.tools[tool].name,
                            Number = response.tools[tool].number
                        };
                        foreach (float active in response.temps.tools.active[tool])
                        {
                            toolObj.Active.Add(active);
                        }
                        foreach (float standby in response.temps.tools.standby[tool])
                        {
                            toolObj.Standby.Add(standby);
                        }
                        if (response.tools[tool].fan >= 0)
                        {
                            toolObj.Fans.Add(response.tools[tool].fan);
                        }
                        foreach (int heater in response.tools[tool].heaters)
                        {
                            toolObj.Heaters.Add(heater);
                        }
                        foreach (float offset in response.tools[tool].offsets)
                        {
                            toolObj.Offsets.Add(offset);
                        }
                        tools[tool] = toolObj;
                    }
                    ListHelpers.AssignList(Provider.Get.Tools, tools);
                }
            }

            // Deserialize print status response
            else if (module == 3)
            {
                var printResponseDefinition = new
                {
                    currentLayer = 0,
                    currentLayerTime = 0F,
                    filePosition = 0L,
                    firstLayerDuration = 0F,
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

                var printResponse = JsonConvert.DeserializeAnonymousType(json, printResponseDefinition);
                using (await Provider.AccessReadWriteAsync())
                {
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

            // Reset everything if the controller is halted
            using (await Provider.AccessReadOnlyAsync())
            {
                if (Provider.Get.State.Status == MachineStatus.Halted)
                {
                    await SPI.Interface.InvalidateData("Firmware halted");
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
