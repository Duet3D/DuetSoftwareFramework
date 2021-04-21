using DuetAPI.ObjectModel;
using DuetControlServer.Commands;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DuetControlServer.Files
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
            int[] pParam = code.Parameter('P', Array.Empty<int>());

            string file = await FilePath.ToPhysicalAsync(FilePath.ConfigOverrideFile, FileDirectory.System);
            using FileStream fs = new(file, FileMode.Create, FileAccess.Write);
            using StreamWriter writer = new(fs);

            using (await Model.Provider.AccessReadOnlyAsync())
            {
                await writer.WriteLineAsync($"; config-override.g file generated in response to M500 at {DateTime.Now:yyyy-MM-dd HH:mm}");
                await writer.WriteLineAsync();
                await WriteCalibrationParameters(writer);
                await WriteModelParameters(writer);
                await WriteAxisLimits(writer);
                if (pParam.Contains(31))
                {
                    await WriteProbeValues(writer);
                }
                await WriteToolParameters(writer, pParam.Contains(10));
                await WriteWorkplaceCoordinates(writer);
            }
        }

        private static async Task WriteCalibrationParameters(StreamWriter writer)
        {
            if (Model.Provider.Get.Move.Kinematics is HangprinterKinematics hangprinterKinematics)
            {
                await writer.WriteLineAsync("; Hangprinter parameters");
                await writer.WriteLineAsync("M669 K6 " +
                    $"A{hangprinterKinematics.AnchorA[0]:F3}:{hangprinterKinematics.AnchorA[1]:F3}:{hangprinterKinematics.AnchorA[2]:F3} " +
                    $"B{hangprinterKinematics.AnchorB[0]:F3}:{hangprinterKinematics.AnchorB[1]:F3}:{hangprinterKinematics.AnchorB[2]:F3} " +
                    $"C{hangprinterKinematics.AnchorC[0]:F3}:{hangprinterKinematics.AnchorC[1]:F3}:{hangprinterKinematics.AnchorC[2]:F3} " +
                    $"D{hangprinterKinematics.AnchorDz:F3} P{hangprinterKinematics.PrintRadius:F2}");
            }
            else if (Model.Provider.Get.Move.Kinematics is DeltaKinematics deltaKinematics)
            {
                await writer.WriteLineAsync("; Delta parameters");
                await writer.WriteLineAsync("M665 " +
                    $"L{string.Join(':', deltaKinematics.Towers.Select(tower => tower.Diagonal.ToString("F3")))} " +
                    $"R{deltaKinematics.DeltaRadius:F3} H{deltaKinematics.HomedHeight:F3} B{deltaKinematics.PrintRadius:F1} " +
                    $"X{deltaKinematics.Towers[0].AngleCorrection:F3} Y{deltaKinematics.Towers[1].AngleCorrection:F3} Z{deltaKinematics.Towers[2].AngleCorrection:F3}");
                await writer.WriteLineAsync("M666 " +
                    $"X{deltaKinematics.Towers[0].EndstopAdjustment:F3} Y{deltaKinematics.Towers[1].EndstopAdjustment:F3} Z{deltaKinematics.Towers[2].EndstopAdjustment:F3} " +
                    $"A{deltaKinematics.XTilt * 100F:F2} B{deltaKinematics.YTilt * 100F:F2}");
            }
        }

        private static async Task WriteModelParameters(StreamWriter writer)
        {
            await writer.WriteLineAsync("; Heater model parameters");
            for (int heater = 0; heater < Model.Provider.Get.Heat.Heaters.Count; heater++)
            {
                Heater item = Model.Provider.Get.Heat.Heaters[heater];
                if (item != null)
                {
                    // Heater model
                    await writer.WriteLineAsync("M307 " +
                        $"H{heater} R{item.Model.HeatingRate:F3} C{item.Model.TimeConstant:F3}:{item.Model.TimeConstantFansOn:F3} " +
                        $"D{item.Model.DeadTime:F2} S{item.Model.MaxPwm:F2} V{item.Model.StandardVoltage:F1} B{(item.Model.PID.Used ? 0 : 1)}");

                    // Custom PID parameters
                    if (item.Model.PID.Overridden)
                    {
                        await writer.WriteLineAsync($"M301 H{heater} P{item.Model.PID.P:F1} I{item.Model.PID.I} D{item.Model.PID.D:F1}");
                    }
                }
            }
        }

        private static async Task WriteAxisLimits(StreamWriter writer)
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

        private static async Task WriteProbeValues(StreamWriter writer)
        {
            if (Model.Provider.Get.Sensors.Probes.Count > 0)
            {
                // At the moment only the parameters of the first Z probe can be saved. We may need to add an 'I' parameter to G31 in the future
                Probe probe = Model.Provider.Get.Sensors.Probes[0];

                await writer.WriteLineAsync("; Z probe parameters");
                await writer.WriteLineAsync($"G31 P{probe.Threshold} X{probe.Offsets[0]:F1} Y{probe.Offsets[1]:F1} Z{probe.TriggerHeight:F2}");
            }
        }

        private static async Task WriteToolParameters(StreamWriter writer, bool forceWrite)
        {
            if (forceWrite || Model.Provider.Get.Tools.Any(tool => tool != null && tool.OffsetsProbed != 0))
            {
                await writer.WriteLineAsync("; Probed tool offsets");
                foreach (Tool tool in Model.Provider.Get.Tools)
                {
                    if (tool != null && (tool.OffsetsProbed != 0 || forceWrite))
                    {
                        List<string> values = new();
                        for (int i = 0; i < Model.Provider.Get.Move.Axes.Count; i++)
                        {
                            Axis axis = Model.Provider.Get.Move.Axes[i];
                            if (axis.Visible && ((tool.OffsetsProbed & (1 << i)) != 0 || forceWrite))
                            {
                                char axisLetter = Model.Provider.Get.Move.Axes[i].Letter;
                                values.Add($"{axisLetter}{tool.Offsets[i]:F2}");
                            }
                        }
                        await writer.WriteLineAsync($"G10 P{tool.Number} {string.Join(' ', values)}");
                    }
                }
            }
        }

        private static async Task WriteWorkplaceCoordinates(StreamWriter writer)
        {
            await writer.WriteLineAsync("; Workplace coordinates");
            for (int i = 0; i < Model.Provider.Get.Limits.Workplaces; i++)
            {
                List<string> values = new();
                for (int axisIndex = 0; axisIndex < Model.Provider.Get.Move.Axes.Count; axisIndex++)
                {
                    Axis axis = Model.Provider.Get.Move.Axes[axisIndex];
                    if (axis.Visible)
                    {
                        values.Add($"{axis.Letter}{Model.Provider.Get.Move.Axes[axisIndex].WorkplaceOffsets[i]:F2}");
                    }
                }
                await writer.WriteLineAsync($"G10 L2 P{i + 1} {string.Join(' ', values)}");
            }
        }
    }
}