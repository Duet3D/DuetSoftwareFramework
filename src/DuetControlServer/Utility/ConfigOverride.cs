using DuetAPI.Machine;
using DuetControlServer.Commands;
using DuetControlServer.FileExecution;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Static class holding values for config-override.g
    /// </summary>
    public static class ConfigOverride
    {
        /// <summary>
        /// Save the current non-volatile parameters to config-override.g
        /// </summary>
        /// <param name="code">Code that invoked this method</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Save(Code code)
        {
            string file = await FilePath.ToPhysical(MacroFile.ConfigOverrideFile, "sys");
            using (FileStream stream = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    await writer.WriteLineAsync($"; config-override.g file generated in response to M500 at {DateTime.Now:yyyy-MM-dd HH:MM}");
                    await writer.WriteLineAsync();
                    await WriteCalibrationParameters(writer);
                    await WriteModelParameters(writer);
                    await WriteAxisLimits(writer);
                    if (code.Parameter('P') == 31)
                    {
                        await WriteProbeValues(writer);
                    }
                    await WriteToolParameters(writer);
                    await WriteWorkplaceCoordinates(writer);
                }
            }
        }

        private static async Task WriteCalibrationParameters(StreamWriter writer)
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                Geometry geo = Model.Provider.Get.Move.Geometry;
                if (geo.Type == GeometryType.Hangprinter)
                {
                    await writer.WriteLineAsync("; Hangprinter parameters");
                    await writer.WriteLineAsync("M669 K6 " +
                        $"A{geo.Anchors[0]:F3}:{geo.Anchors[1]:F3}:{geo.Anchors[2]:F3} " +
                        $"B{geo.Anchors[3]:F3}:{geo.Anchors[4]:F3}:{geo.Anchors[5]:F3} " +
                        $"C{geo.Anchors[6]:F3}:{geo.Anchors[7]:F3}:{geo.Anchors[8]:F3} " +
                        $"D{geo.Anchors[9]:F3} P{geo.PrintRadius:F2}");
                }
                else if (geo.Type == GeometryType.Delta)
                {
                    await writer.WriteLineAsync("; Delta parameters");
                    await writer.WriteLineAsync("M665 " +
                        $"L{string.Join(':', geo.Diagonals.Select((val) => val.ToString("F3")))} " +
                        $"R{geo.Radius:F3} H{geo.HomedHeight:F3} B{geo.PrintRadius:F1} " +
                        $"X{geo.AngleCorrections[0]:F3} Y{geo.AngleCorrections[1]:F3} Z{geo.AngleCorrections:F3}");
                    await writer.WriteLineAsync("M666 " +
                        $"X{geo.EndstopAdjustments[0]:F3} Y{geo.EndstopAdjustments[1]:F3} Z{geo.EndstopAdjustments:F3} " +
                        $"A{geo.Tilt[0] * 100:F2} B{geo.Tilt[1] * 100:F2}");
                }
            }
        }

        private static async Task WriteModelParameters(StreamWriter writer)
        {
            await writer.WriteLineAsync("; Heater model parameters");
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                for (int heater = 0; heater < Model.Provider.Get.Heat.Heaters.Count; heater++)
                {
                    Heater item = Model.Provider.Get.Heat.Heaters[heater];

                    // Heater model
                    await writer.WriteLineAsync("M307 " +
                        $"H{item.Model.Gain} A{item.Model.TimeConstant:F1} C{item.Model.DeadTime:F1} D{item.Model.MaxPwm:F1} " +
                        $"S{item.Model.StandardVoltage:F1} B{(item.Model.UsePID ? 0 : 1)}");

                    // Custom PID parameters
                    if (item.Model.CustomPID)
                    {
                        await writer.WriteLineAsync($"M301 H{heater} P{item.Model.P:F1} I{item.Model.I} D{item.Model.D:F1}");
                    }
                }
            }
        }

        private static async Task WriteAxisLimits(StreamWriter writer)
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Move.Axes.Any(axis => axis.MinProbed || axis.MaxProbed))
                {
                    await writer.WriteLineAsync("; Probed axis limits");

                    // Axis minima
                    if (Model.Provider.Get.Move.Axes.Any(axis => axis.MinProbed))
                    {
                        string[] axisMins = (from axis in Model.Provider.Get.Move.Axes
                                             where axis.MinProbed
                                             select $"{axis.Letter}{axis.Min:F2}").ToArray();
                        await writer.WriteLineAsync($"M208 S1 {string.Join(' ', axisMins)}");
                    }

                    // Axis maxima
                    if (Model.Provider.Get.Move.Axes.Any(axis => axis.MaxProbed))
                    {
                        string[] axisMins = (from axis in Model.Provider.Get.Move.Axes
                                             where axis.MaxProbed
                                             select $"{axis.Letter}{axis.Max:F2}").ToArray();
                        await writer.WriteLineAsync($"M208 S0 {string.Join(' ', axisMins)}");
                    }
                }
            }
        }

        private static async Task WriteProbeValues(StreamWriter writer)
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Sensors.Probes.Count > 0)
                {
                    // At the moment only the parameters of the first Z probe can be saved. We may need to add an 'I' parameter to G31 in the future
                    Probe probe = Model.Provider.Get.Sensors.Probes[0];

                    await writer.WriteLineAsync("; Z probe parameters");
                    await writer.WriteLineAsync($"G31 T{probe.Type} P{probe.Threshold} X{probe.Offsets[0]:F1} Y{probe.Offsets[2]:F1} Z{probe.TriggerHeight:F2}");
                }
            }
        }

        private static async Task WriteToolParameters(StreamWriter writer)
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (Model.Provider.Get.Tools.Any(tool => tool.OffsetsProbed != 0))
                {
                    await writer.WriteLineAsync("; Probed tool offsets");
                    foreach (Tool tool in Model.Provider.Get.Tools)
                    {
                        if (tool.OffsetsProbed != 0)
                        {
                            List<string> values = new List<string>();
                            for (int axis = 0; axis < Model.Provider.Get.Move.Axes.Count; axis++)
                            {
                                if ((tool.OffsetsProbed & (1 << axis)) != 0)
                                {
                                    char axisLetter = Model.Provider.Get.Move.Axes[axis].Letter;
                                    values.Add($"{axisLetter}{tool.Offsets[axis]:F2}");
                                }
                            }
                            await writer.WriteLineAsync($"G10 P{tool.Number} {string.Join(' ', values)}");
                        }
                    }
                }
            }
        }

        private static async Task WriteWorkplaceCoordinates(StreamWriter writer)
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                await writer.WriteLineAsync("; Workplace coordinates");
                for (int i = 0; i < Model.Provider.Get.Move.WorkplaceCoordinates.Count; i++)
                {
                    List<string> values = new List<string>();
                    for (int axisIndex = 0; axisIndex < Model.Provider.Get.Move.Axes.Count; axisIndex++)
                    {
                        Axis axis = Model.Provider.Get.Move.Axes[axisIndex];
                        if (axis.Visible)
                        {
                            values.Add($"{axis.Letter}{Model.Provider.Get.Move.WorkplaceCoordinates[i][axisIndex]:F2}");
                        }
                    }
                    await writer.WriteLineAsync($"G10 L2 P{i} {string.Join(' ', values)}");
                }
            }
        }
    }
}