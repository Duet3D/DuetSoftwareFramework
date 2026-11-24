using System.Threading.Tasks;
using DuetControlServer.Link.Protocol.Shared;

namespace SystemTests.Host;

/// <summary>Reports an expansion board sends, injected and waited for until the object model has taken them in</summary>
internal static class BoardReports
{
    /// <summary>Report a heater on <see cref="Machines.DriverBoard"/> in the given mode and at the given temperature</summary>
    public static Task ReportHeaterAsync(this Bench bench, int heater, HeaterMode mode, float temperature)
    {
        bench.CanMaster.InjectHeatersStatus(srcAddress: Machines.DriverBoard, heaterNumber: heater, mode, currentTemperature: temperature);
        return bench.Host.WaitForNumberAsync(model => model.Heat.Heaters[heater]?.Current, temperature,
                                             $"heater {heater}'s temperature", tolerance: 0.01);
    }
}
