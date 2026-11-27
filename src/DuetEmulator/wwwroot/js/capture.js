// Captures as the page reads them: events split into steps, and each event described in a line

/** The kinds of event that start a step: the same as CaptureComparison.StepAction on the server */
export const STEP_KINDS = new Set(['code', 'await', 'wait', 'config', 'input', 'note']);

/** What an event does to start a step, or null if it does not start one */
export function stepAction(event) {
  switch (event.kind) {
    case 'code': return event.code;
    case 'await': return `await ${event.code}`;
    case 'wait': return `wait ${event.duration}`;
    case 'config': return 'config.g';
    case 'input': return `input ${event.board}.${event.port} ${event.value}`;
    case 'note': return `# ${event.text}`;
    default: return null;
  }
}

/**
 * A capture's events split into steps: step 1 is what came before the first action, and each later
 * step starts with the event that starts it
 */
export function splitSteps(events) {
  const steps = [{ number: 1, action: 'start', events: [] }];
  for (const event of events) {
    const action = stepAction(event);
    if (action !== null) {
      steps.push({ number: steps.length + 1, action, events: [event] });
    } else {
      steps[steps.length - 1].events.push(event);
    }
  }
  return steps;
}

/** How many of each kind of event a step recorded, as the steps list shows them */
export function countEvents(events, ignoredCanTypes = PERIODIC_CAN_TYPES) {
  const counts = { can: 0, om: 0, pin: 0 };
  for (const event of events) {
    if (event.kind === 'can' && !ignoredCanTypes.has(event.type)) counts.can++;
    else if (event.kind === 'om') counts.om++;
    else if (event.kind === 'pin') counts.pin++;
  }
  return counts;
}

/**
 * The CAN message types sent periodically, which the comparison leaves out by default. The server's
 * list (ComparisonOptions.PeriodicCanTypes) replaces these once the page has heard from it
 */
export const PERIODIC_CAN_TYPES = new Set(['TimeSync', 'BoardStatusReportV0', 'BoardStatusReportV1', 'DriversStatusReport', 'FansReport',
                                           'HeatersStatusReport', 'SensorTemperaturesReport']);

/** Take the server's list of periodic CAN message types */
export function setPeriodicCanTypes(types) {
  if (!Array.isArray(types) || types.length === 0) return;
  PERIODIC_CAN_TYPES.clear();
  for (const type of types) PERIODIC_CAN_TYPES.add(type);
}

/** A JSON value as one line of text */
export function json(value) {
  return value === undefined ? 'absent' : JSON.stringify(value);
}

/** The CAN address a frame went to, as the bench writes it */
export function destination(address) {
  return address === 63 ? 'all' : String(address);
}

/** An event as one line of text, without its time: the same as CaptureEvent.Describe on the server */
export function describe(event) {
  switch (event.kind) {
    case 'code': return `> ${event.code}`;
    case 'reply': return `< ${event.severity === 'success' ? '' : `${event.severity}: `}${event.text === '' ? '(empty)' : event.text}`;
    case 'await': return `await ${event.code}`;
    case 'wait': return `wait ${event.duration}`;
    case 'config': return 'run config.g';
    case 'can': return `CAN ${event.source}->${destination(event.destination)} ${event.type}${event.response ? ' (response)' : ''} ${event.message ? JSON.stringify(event.message) : event.data}`;
    case 'om': return `OM ${event.path}: ${json(event.old ?? null)} -> ${json(event.new ?? null)}`;
    case 'om-snapshot': return `OM snapshot of ${Object.keys(event.model ?? {}).length} keys`;
    case 'pin': return `PIN ${event.board}.${event.ports.join('/')} ${event.output}${event.duty != null ? ` ${(event.duty * 100).toFixed(1)}% at ${event.frequency}Hz` : ''}`;
    case 'input': return `INPUT ${event.board}.${event.port} ${event.value}`;
    case 'input-change': return `INPUT ${event.board}.${event.ports.join('/')} ${event.value} (scripted)`;
    case 'note': return `# ${event.text}`;
    default: return JSON.stringify(event);
  }
}

/** A time in seconds of emulated time, as the page shows it */
export function seconds(t) {
  return t == null ? '—' : `${t.toFixed(3)} s`;
}

/**
 * What drives each input of each board, by the inputs a capture recorded: "board.port" to the value
 * the script last drove it with, or that it last changed to by itself
 */
export function inputStates(events) {
  const states = new Map();
  for (const event of events) {
    if (event.kind === 'input') {
      states.set(`${event.board}.${event.port}`, { value: event.value, scripted: false, t: event.t });
    } else if (event.kind === 'input-change') {
      for (const port of event.ports) {
        states.set(`${event.board}.${port}`, { value: event.value, scripted: true, t: event.t });
      }
    }
  }
  return states;
}

/**
 * Apply a capture message from the server to a list of events, replacing them from its index on.
 * Returns false, changing nothing, if the message starts past the events the list has, which means
 * some were missed and the list must be read again
 */
export function applyCaptureChange(events, from, added) {
  if (from > events.length) return false;
  events.length = from;
  events.push(...added);
  return true;
}
