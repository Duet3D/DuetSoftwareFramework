using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Model;

/// <summary>
/// Singleton hosted service that tracks and evaluates M581.1 expression-based triggers
/// whose expressions reference SBC object model fields (which RRF cannot evaluate)
/// </summary>
/// <param name="codeFactory">Factory to create code instances</param>
/// <param name="expressions">Expression evaluator</param>
/// <param name="model">Object model</param>
/// <param name="observer">Object model observer</param>
/// <param name="logger">Logger</param>
public sealed class SbcTriggerService(
    CodeFactory codeFactory,
    Codes.Meta.Expressions expressions,
    ObjectModel model,
    Observer observer,
    ILogger<SbcTriggerService> logger) : BackgroundService
{
    private sealed class TriggerState
    {
        public required string Expression { get; init; }
        public int Condition { get; init; }
        public bool LastResult { get; set; }
    }

    private readonly Dictionary<int, TriggerState> _triggers = new();
    private readonly object _triggersLock = new();
    private readonly AsyncAutoResetEvent _signal = new(false);

    /// <summary>
    /// Subscribes to OM change notifications when started
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        observer.OnPropertyPathChanged += OnObservedPropertyChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Unsubscribes from OM change notifications when stopped
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        observer.OnPropertyPathChanged -= OnObservedPropertyChanged;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Register, update, or remove an SBC expression trigger from an M581.1 code.
    /// Returns null if the expression contains no SBC fields (so RRF can handle M581.1 itself),
    /// or a <see cref="Message"/> when the request was fully handled by this service.
    /// </summary>
    /// <param name="code">M581.1 code with T, P, and optional R parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result message, or null to pass the code on to RRF</returns>
    public async Task<Message?> ConfigureAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('T', out int triggerNumber) || triggerNumber < 0 || triggerNumber >= 32)
        {
            return new Message(MessageType.Error, "T parameter must be between 0 and 31");
        }

        if (!code.TryGetParameter('P', out CodeParameter? pParam))
        {
            return new Message(MessageType.Error, "P parameter (expression) is required");
        }

        // P-1 (integer) means delete/clear the trigger
        if (pParam.Type == typeof(int) && (int)pParam == -1)
        {
            lock (_triggersLock)
            {
                _triggers.Remove(triggerNumber);
            }
            return new Message();
        }

        if (pParam.Type != typeof(string))
        {
            return new Message(MessageType.Error, "P parameter must be a quoted expression string");
        }
        string triggerExpression = (string)pParam!;

        if (!expressions.ContainsSbcFields(triggerExpression))
        {
            // No SBC fields — let RRF handle M581.1 natively
            return null;
        }

        int condition = code.TryGetInt('R', out int rParam) ? rParam : 0;

        // Clear any existing pin-based RRF trigger for this slot to avoid duplicate firing
        Commands.Code clearCode = codeFactory.Create();
        clearCode.Flags = CodeFlags.IsInternallyProcessed;
        clearCode.Channel = code.Channel;
        clearCode.Type = CodeType.MCode;
        clearCode.MajorNumber = 581;
        clearCode.Parameters = [new('T', triggerNumber), new('P', -1)];
        await clearCode.ExecuteAsync(cancellationToken);

        // Evaluate the expression immediately to seed LastResult, so that a true expression
        // at registration time does not cause a spurious trigger on the first OM change
        bool initialResult = false;
        try
        {
            Commands.Code evalCode = codeFactory.Create();
            evalCode.Channel = CodeChannel.Trigger;
            object? evalResult = await expressions.EvaluateExpressionRaw(evalCode, triggerExpression, false, cancellationToken);
            initialResult = evalResult is bool b && b;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Failed to evaluate initial state of M581.1 expression '{Expression}'", triggerExpression);
        }

        lock (_triggersLock)
        {
            _triggers[triggerNumber] = new TriggerState
            {
                Expression = triggerExpression,
                Condition = condition,
                LastResult = initialResult
            };
        }

        logger.LogInformation("Registered SBC trigger {Number} with expression '{Expression}' (condition {Condition})", triggerNumber, triggerExpression, condition);
        return new Message();
    }

    /// <summary>
    /// Remove a DSF-managed trigger, if one is registered for the given slot.
    /// Called when plain M581 (re)configures or deletes a trigger so RRF takes back ownership.
    /// </summary>
    /// <param name="triggerNumber">Trigger number (0–31)</param>
    public void Remove(int triggerNumber)
    {
        lock (_triggersLock)
        {
            if (_triggers.Remove(triggerNumber))
            {
                logger.LogInformation("Removed SBC trigger {Number} (superseded by M581)", triggerNumber);
            }
        }
    }

    /// <summary>
    /// Called by the OM observer whenever any property changes.
    /// Signals the evaluation loop to run a new cycle.
    /// </summary>
    private void OnObservedPropertyChanged(object[] path, PropertyChangeType changeType, object? value)
    {
        _signal.Set();
    }

    /// <summary>
    /// Background loop that re-evaluates all registered triggers after each batch of OM changes.
    /// Fires the corresponding trigger macro on a false-to-true transition.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for a signal that an observed property has changed, or for cancellation
            try
            {
                await _signal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
                break;
            }

            // Snapshot the current trigger map before evaluating
            (int number, TriggerState state)[] snapshot;
            lock (_triggersLock)
            {
                snapshot = [.. _triggers.Select(kvp => (kvp.Key, kvp.Value))];
            }

            foreach ((int triggerNum, TriggerState state) in snapshot)
            {
                try
                {
                    Commands.Code evalCode = codeFactory.Create();
                    evalCode.Channel = CodeChannel.Trigger;
                    object? result = await expressions.EvaluateExpressionRaw(evalCode, state.Expression, false, stoppingToken);
                    bool newResult = result is bool b && b;

                    bool fire;
                    lock (_triggersLock)
                    {
                        fire = newResult && !state.LastResult;
                        state.LastResult = newResult;
                    }

                    if (fire && await IsTriggerConditionMetAsync(state.Condition, stoppingToken))
                    {
                        logger.LogInformation("SBC trigger {Number} fired (expression: '{Expression}')", triggerNum, state.Expression);
                        Commands.Code triggerCode = codeFactory.Create();
                        triggerCode.Flags = CodeFlags.IsInternallyProcessed | CodeFlags.Asynchronous;
                        triggerCode.Channel = CodeChannel.Trigger;
                        triggerCode.Type = CodeType.MCode;
                        triggerCode.MajorNumber = 98;
                        triggerCode.Parameters = [new('P', $"trigger{triggerNum}.g")];
                        _ = triggerCode.ExecuteAsync();
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // expected on shutdown — just exit the loop without logging a warning
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to evaluate SBC trigger expression '{Expression}'", state.Expression);
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the trigger condition (R parameter) allows firing given the current print status.
    /// </summary>
    /// <param name="condition">0 = always, 1 = only while printing, 2 = only while not printing</param>
    private async Task<bool> IsTriggerConditionMetAsync(int condition, CancellationToken cancellationToken)
    {
        if (condition == 0)
        {
            return true;
        }

        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            bool isPrinting = model.State.Status is MachineStatus.Processing
                or MachineStatus.Pausing
                or MachineStatus.Paused
                or MachineStatus.Resuming;
            return condition == 1 ? isPrinting : !isPrinting;
        }
    }
}
