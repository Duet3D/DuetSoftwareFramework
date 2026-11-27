// The session as the page knows it, kept up to date from the server's events

import { api, follow } from './api.js';
import { applyCaptureChange, splitSteps } from './capture.js';

const listeners = new Map();
let stream = null;
let modelTimer = null;
let captureFrame = null;

/** What the page knows */
export const state = {
  connected: false,
  status: { phase: 'none', session: null, stacks: [] },
  steps: [],
  captures: {},
  boardStates: {},
  boards: [],
  models: {},
  // The capture's steps, by stack, worked out when asked for after the capture changes
  splits: {}
};

/** Be told when something changes: 'connection', 'status', 'session', 'steps', 'capture', 'boards', 'models' */
export function on(topic, listener) {
  if (!listeners.has(topic)) listeners.set(topic, new Set());
  listeners.get(topic).add(listener);
  return () => listeners.get(topic).delete(listener);
}

function emit(topic) {
  for (const listener of listeners.get(topic) ?? []) listener(state);
}

/** The session's stacks, in order */
export function stacks() {
  return state.status.session?.stacks ?? Object.keys(state.captures);
}

/** Whether a session has booted */
export function hasSession() {
  return state.status.session !== null && state.status.session !== undefined;
}

/** A stack's capture split into steps; step n of the session is capture step n + 1 */
export function captureSteps(stack) {
  const events = state.captures[stack] ?? [];
  const cached = state.splits[stack];
  if (cached?.events === events && cached.length === events.length) return cached.steps;
  const steps = splitSteps(events);
  state.splits[stack] = { events, length: events.length, steps };
  return steps;
}

/** The events a stack recorded in a step of the session */
export function stepEvents(stack, number) {
  return captureSteps(stack)[number]?.events ?? [];
}

/** The object model paths any stack changed in a step of the session */
export function changedPaths(number) {
  const paths = new Set();
  for (const stack of stacks()) {
    for (const event of stepEvents(stack, number)) {
      if (event.kind === 'om') paths.add(event.path);
    }
  }
  return paths;
}

/** The codes started without waiting that no step has awaited yet */
export function pendingStarts() {
  const pending = [];
  for (const step of state.steps) {
    if (step.action.start !== undefined && step.action.start !== null) pending.push(step.action.start);
    if (step.action.await !== undefined && step.action.await !== null) {
      const index = pending.indexOf(step.action.await);
      if (index >= 0) pending.splice(index, 1);
    }
  }
  return pending;
}

/** The number of differences the steps have found */
export function differenceCount() {
  return state.steps.reduce((sum, step) => sum + step.differences.length, 0);
}

function sessionKey(status) {
  return status.session ? `${status.directory}` : null;
}

async function loadBoards() {
  if (!hasSession()) {
    state.boards = [];
    emit('boards');
    return;
  }
  try {
    const answer = await api.get('/boards');
    state.boards = answer.boards;
    state.boardStates = answer.states;
    emit('boards');
  } catch {
    // The session ended meanwhile; the next hello says so
  }
}

async function loadModels() {
  if (!hasSession()) {
    state.models = {};
    emit('models');
    return;
  }
  try {
    state.models = await api.get('/model');
    emit('models');
  } catch {
    // As for the boards
  }
}

function loadModelsSoon() {
  clearTimeout(modelTimer);
  modelTimer = setTimeout(loadModels, 150);
}

// Many capture messages arrive together; the page redraws for them once a frame
function captureChanged() {
  if (captureFrame !== null) return;
  captureFrame = requestAnimationFrame(() => {
    captureFrame = null;
    emit('capture');
  });
}

/** Start following the server's session */
export function connect() {
  stream = follow({
    connection(up) {
      state.connected = up;
      emit('connection');
    },
    hello(hello) {
      const changed = sessionKey(hello.status) !== sessionKey(state.status);
      state.status = hello.status;
      state.steps = hello.steps;
      state.captures = hello.captures;
      state.boardStates = hello.boards;
      state.splits = {};
      emit('status');
      if (changed) {
        state.models = {};
        emit('session');
        loadBoards();
      }
      emit('steps');
      emit('capture');
      emit('boards');
      loadModels();
    },
    status(status) {
      const changed = sessionKey(status) !== sessionKey(state.status);
      state.status = status;
      emit('status');
      if (changed) emit('session');
    },
    step(step) {
      const index = state.steps.findIndex(s => s.number === step.number);
      if (index >= 0) state.steps[index] = step;
      else state.steps.push(step);
      emit('steps');
      if (step.state !== 'running') loadModelsSoon();
    },
    capture({ stack, from, events }) {
      const list = state.captures[stack] ?? (state.captures[stack] = []);
      if (!applyCaptureChange(list, from, events)) {
        // Some were missed: start again from everything there is
        stream.restart();
        return;
      }
      captureChanged();
    },
    boards({ stack, boards }) {
      state.boardStates[stack] = boards;
      emit('boards');
    }
  });
}

/** Run steps on the session; what they do arrives through the events */
export function run(steps) {
  return api.post('/steps', steps);
}
