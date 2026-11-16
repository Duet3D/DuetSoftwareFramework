using System;
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
        return bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.Heat.Heaters[heater]?.Current is float t && MathF.Abs(t - temperature) < 0.01f,
                                              what: $"heater {heater} reporting {temperature}C");
    }

    /// <summary>Report that a board supports closed loop control, as an EXP1HCL's status reports say, and wait until <c>boards[].closedLoop</c> shows it</summary>
    public static Task ReportClosedLoopAsync(this Bench bench, byte board)
    {
        bench.CanMaster.InjectBoardStatus(board, hasClosedLoop: true);
        return bench.CanMaster.WaitUntilAsync(() => bench.Host.Model.FindBoard(board)?.ClosedLoop is not null,
                                              what: $"board {board} reporting closed loop support");
    }
}
