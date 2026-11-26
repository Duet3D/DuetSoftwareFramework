using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Captures;

namespace DuetEmulation.Scenarios;

/// <summary>Runs scenarios and their steps on a bench, recording what they do</summary>
public static class ScenarioRunner
{
    /// <summary>
    /// Boot the scenario's boards on a stack, run its steps, and return the capture, which is also
    /// saved in <paramref name="workDirectory"/> beside the run's logs and traces
    /// </summary>
    public static async Task<MachineCapture> RunAsync(Scenario scenario, Stack stack, string workDirectory, CancellationToken cancellationToken = default)
    {
        await using Bench bench = await Bench.StartAsync(stack, scenario.BoardTypes(), scenario.Config, workDirectory, scenario.Files, cancellationToken);
        Recorder recorder = await Recorder.StartAsync(bench, Recorder.Header(scenario.Name, stack, scenario.BoardTypes(), scenario.Config),
                                                      scenario.CaptureBoot, cancellationToken: cancellationToken);
        StepRunner steps = new(recorder);
        foreach (ScenarioStep step in scenario.Steps)
        {
            await steps.RunAsync(step, cancellationToken);
        }
        await recorder.CatchUpAsync(cancellationToken);
        recorder.Capture.Save(workDirectory);
        return recorder.Capture;
    }
}

/// <summary>
/// Runs steps one at a time on a recorder, keeping the codes started without waiting until a step awaits them
/// </summary>
public sealed class StepRunner(Recorder recorder)
{
    private readonly List<Recorder.RecordedCode> _started = [];

    /// <summary>The recorder the steps run on</summary>
    public Recorder Recorder => recorder;

    /// <summary>Run a step, and return the reply for one that finishes a code</summary>
    /// <exception cref="InvalidOperationException">An await names a code that was not started</exception>
    public async Task<CodeReply?> RunAsync(ScenarioStep step, CancellationToken cancellationToken = default)
    {
        step.Validate();
        if (step.Code is string code)
        {
            return await recorder.SendCodeAsync(code, step.TimeoutSpan, cancellationToken);
        }
        if (step.Start is string start)
        {
            _started.Add(await recorder.StartCodeAsync(start, cancellationToken));
            return null;
        }
        if (step.Await is string awaited)
        {
            Recorder.RecordedCode pending = _started.Find(c => c.Code == awaited)
                ?? throw new InvalidOperationException($"\"{awaited}\" was not started");
            _started.Remove(pending);
            return await pending.WaitAsync(step.TimeoutSpan, cancellationToken);
        }
        if (step.Wait is string wait)
        {
            await recorder.WaitAsync(Scenario.ParseDuration(wait), cancellationToken);
            return null;
        }
        if (step.Note is string note)
        {
            recorder.Note(note);
            return null;
        }
        if (step.Drive is PinInput { AfterSteps: StepCount after } trigger)
        {
            await recorder.DriveAfterStepsAsync(trigger.Board, after.Driver, after.Steps, trigger.Port, trigger.LevelValue, cancellationToken);
        }
        else if (step.Drive is PinInput drive)
        {
            await recorder.DriveAsync(drive.Board, drive.Port, drive.LevelValue, cancellationToken);
        }
        else if (step.Pulses is PinInput pulses)
        {
            await recorder.PulsesAsync(pulses.Board, pulses.Port, pulses.Frequency!.Value, cancellationToken);
        }
        else if (step.Tacho is PinInput tacho)
        {
            await recorder.TachoFollowsAsync(tacho.Board, tacho.Fan!, tacho.Port, tacho.MaxRpm, tacho.PulsesPerRev, tacho.Inverted, cancellationToken);
        }
        else if (step.Release is PinInput release)
        {
            await recorder.ReleaseAsync(release.Board, release.Port, cancellationToken);
        }
        else if (step.Analog is PinInput analog)
        {
            await recorder.SetAnalogAsync(analog.Board, analog.Port, analog.Value!.Value, cancellationToken);
        }
        else if (step.Thermistor is PinInput thermistor)
        {
            await recorder.SetThermistorAsync(thermistor.Board, thermistor.Port, thermistor.Celsius!.Value, cancellationToken);
        }
        await recorder.CatchUpAsync(cancellationToken);
        return null;
    }
}
