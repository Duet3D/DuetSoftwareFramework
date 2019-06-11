using DuetAPI.Machine;
using Newtonsoft.Json;
using Nito.AsyncEx;
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
                    Provider.Get.Fans.Clear();
                    for (int fan = 0; fan < response.Params.fanPercent.Length; fan++)
                    {
                        Provider.Get.Fans.Add(new Fan
                        {
                            Name = response.Params.fanNames[fan],
                            Rpm = (response.sensors.fanRPM.Length > fan && response.sensors.fanRPM[fan] > 0) ? (int?)response.sensors.fanRPM[fan] : null,
                            Value = response.Params.fanPercent[fan] / 100,
                            Thermostatic = new Thermostatic
                            {
                                Control = (response.controllableFans & (1 << fan)) == 0
                            }
                        });
                    }

                    // - Move -
                    Provider.Get.Move.Compensation = response.compensation;
                    Provider.Get.Move.CurrentMove.RequestedSpeed = response.speeds.requested;
                    Provider.Get.Move.CurrentMove.TopSpeed = response.speeds.top;
                    Provider.Get.Move.SpeedFactor = response.Params.speedFactor / 100;
                    Provider.Get.Move.BabystepZ = response.Params.babystep;
                    Provider.Get.Move.Drives.Clear();

                    // Rewrite axes
                    Provider.Get.Move.Axes.Clear();
                    for (int axis = 0; axis < response.coords.xyz.Length; axis++)
                    {
                        Provider.Get.Move.Axes.Add(new Axis
                        {
                            Letter = GetAxisLetter(axis),
                            Drives = new int[] { axis },
                            Homed = response.coords.axesHomed[axis] != 0,
                            MachinePosition = response.coords.machine[axis]
                        });

                        Provider.Get.Move.Drives.Add(new Drive
                        {
                            Position = response.coords.xyz[axis]
                        });
                    }

                    // Rewrite extruder drives
                    Provider.Get.Move.Extruders.Clear();
                    for (int extruder = 0; extruder < response.coords.extr.Length; extruder++)
                    {
                        Provider.Get.Move.Extruders.Add(new Extruder
                        {
                            Drives = new int[] { response.coords.xyz.Length + extruder },
                            Factor = response.Params.extrFactors[extruder] / 100
                        });

                        Provider.Get.Move.Drives.Add(new Drive
                        {
                            Position = response.coords.extr[extruder]
                        });
                    }

                    // - Heat -
                    Provider.Get.Heat.ColdExtrudeTemperature = response.coldExtrudeTemp;
                    Provider.Get.Heat.ColdRetractTemperature = response.coldRetractTemp;

                    // Rewrite beds
                    Provider.Get.Heat.Beds.Clear();
                    if (response.temps.bed != null)
                    {
                        Provider.Get.Heat.Beds.Add(new BedOrChamber
                        {
                            Active = new float[] { response.temps.bed.active },
                            Standby = new float[] { response.temps.bed.standby },
                            Heaters = new int[] { response.temps.bed.heater }
                        });
                    }

                    // Rewrite chambers
                    Provider.Get.Heat.Chambers.Clear();
                    if (response.temps.chamber != null)
                    {
                        Provider.Get.Heat.Chambers.Add(new BedOrChamber
                        {
                            Active = new float[] { response.temps.chamber.active },
                            Standby = new float[] { response.temps.chamber.standby },
                            Heaters = new int[] { response.temps.chamber.heater }
                        });
                    }

                    // Rewrite heaters
                    Provider.Get.Heat.Heaters.Clear();
                    for (int heater = 0; heater < response.temps.current.Length; heater++)
                    {
                        Provider.Get.Heat.Heaters.Add(new Heater
                        {
                            Current = response.temps.current[heater],
                            Max = response.tempLimit,
                            Name = response.temps.names[heater],
                            Sensor = heater,
                            State = (HeaterState)response.temps.state[heater]
                        });
                    }

                    // Rewrite extra heaters
                    Provider.Get.Heat.Extra.Clear();
                    foreach (var extra in response.temps.extra)
                    {
                        Provider.Get.Heat.Extra.Add(new ExtraHeater
                        {
                            Current = extra.temp,
                            Name = extra.name
                        });
                    }

                    // - Sensors -
                    Provider.Get.Sensors.Probes.Clear();
                    Provider.Get.Sensors.Probes.Add(new Probe
                    {
                        Value = response.sensors.probeValue,
                        SecondaryValues = response.sensors.probeSecondary
                    });

                    // - State -
                    Provider.Get.State.AtxPower = response.Params.atxPower != 0;
                    Provider.Get.State.CurrentTool = response.currentTool;
                    Provider.Get.State.Status = GetStatus(response.status);
                    if (Provider.Get.State.Status == MachineStatus.Idle && FileExecution.MacroFile.DoingMacroFile)
                    {
                        // RRF does not always know whether a macro file is being executed
                        Provider.Get.State.Status = MachineStatus.Busy;
                    }

                    // - Tools -
                    Provider.Get.Tools.Clear();
                    for (int tool = 0; tool < response.tools.Length; tool++)
                    {
                        Provider.Get.Tools.Add(new Tool
                        {
                            Active = response.temps.tools.active[tool],
                            Standby = response.temps.tools.standby[tool],
                            Fans = new int[] { response.tools[tool].fan },
                            Filament = response.tools[tool].filament,
                            Heaters = response.tools[tool].heaters,
                            Name = (response.tools[tool].name == "") ? null : response.tools[tool].name,
                            Number = response.tools[tool].number,
                            Offsets = response.tools[tool].offsets
                        });
                    }
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
                    Provider.Get.Job.ExtrudedRaw = printResponse.extrRaw;
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
                    await SPI.Interface.InvalidateData("Code cancelled because the firmware is halted");
                }
            }

            // Notify IPC subscribers
            await IPC.Processors.Subscription.ModelUpdated();

            // Notify waiting threads
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
