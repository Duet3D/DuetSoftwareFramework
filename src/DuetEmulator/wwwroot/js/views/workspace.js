// The session's workspace: its boards, its steps with a box to send more, and the object model side by side

import { api } from '../api.js';
import { PERIODIC_CAN_TYPES, countEvents, inputStates, seconds } from '../capture.js';
import { copy, h, icon, replace, store, stored } from '../dom.js';
import { summary, treeRows } from '../model.js';
import { captureSteps, changedPaths, on, pendingStarts, run, stacks, state, stepEvents } from '../store.js';
import { mountBoard } from './board.js';
import { cliPrefix, quote, stepCommand, stepYaml } from './cli.js';
import { mountExport } from './export.js';

export function mountWorkspace(page, target) {
  const boardsColumn = h('aside', { class: 'column-boards', 'aria-label': 'Boards' });
  const stepsColumn = h('section', { class: 'column-steps', 'aria-label': 'Steps' });
  const modelColumn = h('aside', { class: 'column-model', 'aria-label': 'Object model' });
  replace(page, h('div', { class: 'columns' }, boardsColumn, stepsColumn, modelColumn));

  const offs = [
    mountBoards(boardsColumn),
    mountSteps(stepsColumn),
    mountModel(modelColumn)
  ];

  let overlay = null;
  const update = next => {
    overlay?.unmount();
    overlay = null;
    if (next.board !== undefined) overlay = mountBoard(document.body, next.board);
    else if (next.dialog === 'export') overlay = mountExport(document.body);
  };
  update(target);

  return {
    update,
    unmount() {
      overlay?.unmount();
      offs.forEach(off => off());
    }
  };
}

// The boards: each one's drivers, inputs and outputs, as each stack has them
function mountBoards(column) {
  const render = () => {
    const names = stacks();
    const inputs = Object.fromEntries(names.map(s => [s, inputStates(state.captures[s] ?? [])]));
    replace(column,
      h('div', { class: 'section-head' }, h('h2', { class: 'label' }, 'Boards'), h('span', { class: 'faint', style: 'font-size: 12px' }, 'each stack\'s own copy')),
      h('section', { class: 'card board-card' },
        h('div', { class: 'card-head' }, h('strong', {}, 'MB6HC'), h('span', { class: 'address' }, '0')),
        h('div', { class: 'board-rows' }, names.map(stack => h('div', { class: `board-row stack-${stack}` },
          h('span', {}, stack), h('span', { class: 'mono' }, stack === 'rrf' ? 'RepRapFirmware' : 'DuetCANMaster'), h('span', {}))))),
      state.boards.map(board => {
        const states = Object.fromEntries(names.map(s => [s, (state.boardStates[s] ?? []).find(b => b.address === board.address)]));
        const rows = [];
        for (let driver = 0; driver < board.drivers; driver++) {
          rows.push(row('Driver', `${board.address}.${driver}`, names, s => states[s]?.stepCounts[driver], v => `${v} steps`));
        }
        for (const port of board.ports.filter(p => p.kind === 'digitalInput' || p.kind === 'tachoInput' || p.kind === 'analogInput')) {
          const key = `${board.address}.${port.name}`;
          if (!names.some(s => inputs[s].has(key))) continue;
          rows.push(row(port.kind === 'analogInput' ? 'Temp' : 'Input', port.name, names, s => inputs[s].get(key)?.value));
        }
        for (const port of board.ports.filter(p => p.kind === 'output')) {
          // An output the firmware has left as an input or driven low is doing nothing to show
          if (!names.some(s => !['Input', 'Low'].includes(states[s]?.outputs[port.name]?.mode ?? 'Input'))) continue;
          rows.push(row('Output', port.name, names, s => states[s]?.outputs[port.name], output));
        }
        return h('section', { class: 'card board-card' },
          h('div', { class: 'card-head' }, h('strong', {}, board.type), h('span', { class: 'address' }, board.address),
            h('a', { class: 'button small', href: `#/board/${board.address}`, style: 'margin-left: auto' }, icon('board'), 'Inputs and outputs')),
          h('div', { class: 'board-rows' }, rows.length > 0 ? rows : h('span', { class: 'faint', style: 'font-size: 12px; padding: 4px 0' }, 'Nothing driven or driving yet')));
      }));
  };
  const offs = ['boards', 'capture', 'session'].map(topic => on(topic, render));
  render();
  return () => offs.forEach(off => off());
}

/** A row of a board's card: a value on each stack, shown once when they are the same */
function row(kind, port, names, value, format = v => String(v)) {
  const values = names.map(s => value(s));
  const shown = values.map(v => v === undefined || v === null ? '—' : format(v));
  const alike = shown.every(v => v === shown[0]);
  return h('div', { class: 'board-row' }, h('span', {}, kind), h('span', { class: 'mono' }, port),
    h('span', { class: `pair ${alike ? 'same' : ''}` }, alike ? h('span', {}, shown[0]) : names.map((s, i) => h('span', { class: `stack-${s}`, title: s }, shown[i]))));
}

/** What an output drives, as the boards column and drawer show it */
export function output(state) {
  if (!state) return '—';
  return state.mode === 'Pwm' ? `PWM ${(state.duty * 100).toFixed(1)}% ${state.frequency.toFixed(0)} Hz` : state.mode.toLowerCase();
}

// The steps run so far, with the box that sends more
function mountSteps(column) {
  const open = new Set();
  // Why a step's button was refused, by step number, until the step is next run from it
  const refusals = new Map();
  let filter = 'all';
  const table = h('div', { class: 'card scroll-x' });
  const filters = h('div', { class: 'chips', role: 'group', 'aria-label': 'Which steps' });
  const box = composer();
  const held = h('div', { class: 'notice warn held', role: 'status' });
  let configRunning = false;
  let configError = null;
  column.append(
    h('div', { class: 'section-head' }, h('h2', { class: 'label' }, 'Steps'), filters),
    table,
    held,
    box.element);

  const renderFilters = () => replace(filters, [['all', 'All'], ['different', 'With differences'], ['codes', 'Codes only']].map(([value, label]) =>
    h('button', { class: 'chip', type: 'button', 'aria-pressed': filter === value, onclick: () => { filter = value; render(); } }, label)));

  const runFrom = (number, body) => {
    refusals.delete(number);
    render();
    run(body).catch(e => {
      refusals.set(number, e.message);
      render();
    });
  };

  const render = () => {
    renderFilters();
    const names = stacks();
    const pending = pendingStarts();
    const steps = state.steps.filter(step => filter === 'all' || (filter === 'different' ? step.differences.length > 0 : step.action.code !== undefined));
    const start = names.length === 0 ? null : h('div', { class: 'step' }, h('div', { class: 'step-row', style: 'cursor: default' },
      h('span', { class: 'step-number' }, '0'),
      h('div', { class: 'step-what' }, h('div', {}, h('span', { class: 'kind' }, 'Start'), h('span', { class: 'text' }, 'boot'))),
      names.map(s => h('span', { class: 'reply none' }, '—')),
      h('div', { class: 'step-counts' }, counts(names, s => captureSteps(s)[0]?.events ?? []), compareLink(0))));
    replace(table, h('div', { class: 'steps-table', style: `--stacks: ${Math.max(1, names.length)}` },
      h('div', { class: 'steps-head' }, h('span', {}, '#'), h('span', {}, 'Step'),
        names.map(s => h('span', { class: `stack stack-${s}` }, h('span', { class: 'dot' }), `${s} reply`)), h('span', { style: 'text-align: right' }, 'Recorded')),
      filter === 'all' ? start : null,
      steps.map(step => stepRow(step, names, open.has(step.number), pending, () => {
        if (open.has(step.number)) open.delete(step.number);
        else open.add(step.number);
        render();
      }, body => runFrom(step.number, body), refusals.get(step.number))),
      state.steps.length === 0 ? h('div', { class: 'empty-state' }, 'No steps yet: send a code below, or drive an input from a board\'s panel') : null));
    renderHeld();
  };

  // While config.g is held, what is sent runs before it, until it is let run
  const renderHeld = () => {
    if (!state.status.session?.configHeld) {
      replace(held);
      held.hidden = true;
      return;
    }
    held.hidden = false;
    replace(held,
      h('span', {}, 'config.g is waiting: the steps sent now run before it, such as setting the thermistors its heaters will read.'),
      h('button', { class: 'button primary', type: 'button', disabled: configRunning || state.status.phase !== 'ready', onclick: async () => {
        configRunning = true;
        configError = null;
        renderHeld();
        try {
          await api.post('/config');
        } catch (e) {
          configError = e.message;
        } finally {
          configRunning = false;
          renderHeld();
        }
      } }, configRunning ? 'Running config.g' : 'Run config.g'),
      configError ? h('div', { class: 'error', role: 'alert', style: 'flex-basis: 100%' }, configError) : null);
  };

  const offs = [...['steps', 'capture', 'session', 'status'].map(topic => on(topic, render)), box.off];
  render();
  return () => offs.forEach(off => off());
}

const INPUT_ACTIONS = ['drive', 'release', 'analog', 'thermistor', 'pulses', 'tacho'];

function kindOf(action) {
  if (action.config) return ['config', 'Config'];
  if (action.code !== undefined || action.start !== undefined || action.await !== undefined) return ['code', 'Code'];
  if (INPUT_ACTIONS.some(a => action[a])) return ['input', 'Input'];
  if (action.wait !== undefined) return ['', 'Wait'];
  return ['', 'Note'];
}

function stepRow(step, names, isOpen, pending, toggle, runAgain, refusal) {
  const [kindClass, kindLabel] = kindOf(step.action);
  const replyDiffers = step.differences.some(d => d.startsWith('reply:'));
  const finished = names.map(s => step.stacks[s]?.finished).filter(t => t != null);
  const awaitable = step.action.start !== undefined && pending.includes(step.action.start);
  return h('div', { class: `step ${isOpen ? 'open' : ''} ${step.differences.length === 0 ? 'same' : ''} ${step.state}` },
    h('div', { class: 'step-row', role: 'button', tabindex: 0, 'aria-expanded': isOpen, onclick: toggle,
               onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } } },
      h('span', { class: 'step-number' }, step.number),
      h('div', { class: 'step-what' },
        h('div', {}, h('span', { class: `kind ${kindClass}` }, kindLabel), h('span', { class: 'text' }, step.step)),
        h('span', { class: 'when' }, [...finished.map(t => seconds(t)), ...(step.kind === 'beforeConfig' ? ['before config.g'] : [])].join(' · '))),
      names.map(s => replyCell(step, s, replyDiffers)),
      h('div', { class: 'step-counts' }, counts(names, s => stepEvents(s, step.number)),
        step.differences.length > 0 ? h('span', { class: 'badge diff' }, `${step.differences.length} ≠`) : null,
        compareLink(step.number))),
    isOpen ? h('div', { class: 'step-detail' },
      step.differences.map(d => {
        const [aspect, ...rest] = d.split(': ');
        return h('div', { class: 'difference' }, h('span', { class: 'aspect' }, aspect), h('span', { class: 'mono' }, rest.join(': ')));
      }),
      names.length === 2 && step.differences.length === 0 && step.state === 'done' ? h('span', { class: 'muted', style: 'font-size: 13px' }, 'The stacks did the same') : null,
      names.length === 1 ? recorded(stepEvents(names[0], step.number)) : null,
      // config.g runs once, when it is let go, and is no step a scenario lists
      step.action.config ? null : h('div', { style: 'display: flex; gap: 8px; flex-wrap: wrap; margin-top: 4px' },
        awaitable ? h('button', { class: 'button small', type: 'button', onclick: () => runAgain({ await: step.action.start }) }, 'Await it') : null,
        h('button', { class: 'button small', type: 'button', onclick: () => runAgain(step.action) }, 'Run again'),
        h('button', { class: 'button small', type: 'button', onclick: e => copy(step.yaml, e.currentTarget) }, icon('copy'), 'Copy as YAML')),
      refusal ? h('div', { class: 'error', role: 'alert' }, refusal) : null) : null);
}

// The step's CAN frames, pins and object model in full, a click away from its row
function compareLink(number) {
  return h('a', { class: 'compare-link', href: `#/step/${number}`, title: 'Compare frame by frame', onclick: e => e.stopPropagation(),
                  onkeydown: e => e.stopPropagation() }, 'Compare', icon('next'));
}

// What one stack recorded in a step, for a session on one stack, where there are no differences to show
function recorded(events) {
  const frames = events.filter(e => e.kind === 'can' && !PERIODIC_CAN_TYPES.has(e.type));
  const changes = events.filter(e => e.kind === 'om');
  const line = (aspect, text) => h('div', { class: 'difference' }, h('span', { class: 'aspect', style: 'color: var(--muted)' }, aspect), h('span', { class: 'mono' }, text));
  return [
    frames.map(f => line('CAN', `${f.source}→${f.destination === 63 ? 'all' : f.destination} ${f.type}${f.response ? ' (response)' : ''} ${f.message ? JSON.stringify(f.message) : f.data}`)),
    changes.map(c => line('OM', `${c.path}: ${JSON.stringify(c.old ?? null)} → ${JSON.stringify(c.new ?? null)}`)),
    frames.length === 0 && changes.length === 0 ? h('span', { class: 'muted', style: 'font-size: 13px' }, 'No CAN frames or object model changes') : null
  ];
}

function replyCell(step, stack, differs) {
  const result = step.stacks[stack];
  if (!result) return h('span', { class: 'reply none' }, '—');
  if (result.error) return h('span', { class: 'reply failed' }, result.error);
  if (result.finished == null) return h('span', { class: 'reply none', style: 'display: flex; gap: 6px; align-items: center' }, h('span', { class: 'spinner' }), 'running');
  if (!result.reply) return h('span', { class: 'reply none' }, '—');
  const text = result.reply.text === '' ? '(empty)' : result.reply.text;
  return h('span', { class: `reply ${result.reply.text === '' ? 'empty' : ''} ${result.reply.severity} ${differs ? 'differs' : ''}` },
    result.reply.severity === 'success' ? text : `${result.reply.severity}: ${text}`);
}

function counts(names, events) {
  const each = names.map(s => countEvents(events(s)));
  return ['can', 'om', 'pin'].filter(kind => each.some(c => c[kind] > 0))
    .map(kind => h('span', {}, `${{ can: 'CAN', om: 'OM', pin: 'Pins' }[kind]} ${each.map(c => c[kind]).join(' · ')}`));
}

// The box that sends a step: a code, a code not waited for, an await, a wait, a note or any step as YAML
function composer() {
  const modes = [['code', 'Code'], ['start', 'Start (don\'t wait)'], ['await', 'Await'], ['wait', 'Wait'], ['note', 'Note'], ['yaml', 'Step as YAML']];
  let mode = 'code';
  let history = stored('history', []);
  let back = -1;
  let error = null;
  let sending = 0;

  const root = h('div', { class: 'composer' });
  const field = h('input', { class: 'input mono', type: 'text', 'aria-label': 'What to send', autocomplete: 'off', spellcheck: false });
  const yaml = h('textarea', { class: 'textarea', rows: 3, 'aria-label': 'Steps as YAML', spellcheck: false });
  const awaitPick = h('select', { class: 'select', 'aria-label': 'The code to await' });
  const timeout = h('input', { class: 'input mono', type: 'text', placeholder: '10s', style: 'width: 70px; flex: none', 'aria-label': 'Timeout' });
  const hint = h('code', { class: 'mono' });
  const send = h('button', { class: 'button primary', type: 'button', style: 'min-height: 40px' }, 'Send', icon('send'));

  const step = () => {
    const text = field.value.trim();
    const withTimeout = s => timeout.value.trim() ? { ...s, timeout: timeout.value.trim() } : s;
    switch (mode) {
      case 'code': return text ? withTimeout({ code: text }) : null;
      case 'start': return text ? { start: text } : null;
      case 'await': return awaitPick.value ? withTimeout({ await: awaitPick.value }) : null;
      case 'wait': return { wait: text || '1s' };
      case 'note': return text ? { note: text } : null;
      default: return null;
    }
  };

  const updateHint = () => {
    if (mode === 'yaml') {
      hint.textContent = yaml.value.trim() ? `${cliPrefix()} step ${quote(yaml.value.trim())}` : '';
      return;
    }
    const s = step();
    hint.textContent = s ? stepCommand(s, stepYaml(s)) : '';
  };

  const submit = async () => {
    const body = mode === 'yaml' ? yaml.value.trim() : step();
    if (!body) return;
    if (mode === 'code' || mode === 'start') {
      history = [field.value.trim(), ...history.filter(h => h !== field.value.trim())].slice(0, 50);
      store('history', history);
    }
    back = -1;
    error = null;
    if (mode !== 'yaml' && mode !== 'wait') field.value = '';
    sending++;
    render();
    try {
      await run(body);
    } catch (e) {
      error = e.message;
    } finally {
      sending--;
      render();
    }
  };

  const quick = duration => async () => {
    sending++;
    render();
    try {
      await run({ wait: duration });
    } catch (e) {
      error = e.message;
    } finally {
      sending--;
      render();
    }
  };

  field.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
      e.preventDefault();
      submit();
    } else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && (mode === 'code' || mode === 'start') && history.length > 0) {
      e.preventDefault();
      back = Math.max(-1, Math.min(history.length - 1, back + (e.key === 'ArrowUp' ? 1 : -1)));
      field.value = back < 0 ? '' : history[back];
      updateHint();
    }
  });
  yaml.addEventListener('keydown', e => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      submit();
    }
  });
  for (const element of [field, yaml, timeout]) element.addEventListener('input', updateHint);
  awaitPick.addEventListener('change', updateHint);
  send.addEventListener('click', submit);

  const render = () => {
    // The inputs are kept from one drawing to the next; the one being typed in keeps the focus
    const focused = root.contains(document.activeElement) && [field, yaml, timeout, awaitPick].includes(document.activeElement) ? document.activeElement : null;
    const pending = pendingStarts();
    field.placeholder = { code: 'G-code, such as G28 or M409 K"move.axes"', start: 'A code to send without waiting, such as G1 X100 F600',
                          wait: 'How long, such as 500ms or 2s', note: 'A remark, which starts a step when captures are compared' }[mode] ?? '';
    replace(awaitPick, pending.length === 0 ? h('option', { value: '' }, 'No code is waiting') : pending.map(code => h('option', { value: code }, code)));
    replace(root,
      h('div', { class: 'composer-row', role: 'group', 'aria-label': 'What to send' },
        h('div', { class: 'segmented' }, modes.map(([value, label]) => h('button', { type: 'button', 'aria-pressed': mode === value, onclick: () => {
          mode = value;
          render();
          (mode === 'yaml' ? yaml : mode === 'await' ? awaitPick : field).focus();
        } }, label))),
        h('span', { style: 'margin-left: auto; display: flex; gap: 4px; flex-wrap: wrap' },
          ['100ms', '1s', '10s'].map(d => h('button', { class: 'button small', type: 'button', onclick: quick(d), title: `Let ${d} of emulated time pass` }, `+${d}`)))),
      h('div', { class: 'composer-row' },
        mode === 'yaml' ? yaml : mode === 'await' ? awaitPick : field,
        mode === 'code' || mode === 'await' ? timeout : null,
        send),
      error ? h('div', { class: 'error', role: 'alert' }, error) : null,
      h('div', { class: 'composer-hint' },
        h('span', {}, sending > 0 ? `Running ${sending} request${sending === 1 ? '' : 's'}… ` : '',
          mode === 'yaml' ? 'Ctrl+Enter sends' : 'Enter sends to every stack · ↑ recalls', ' · steps sent from the CLI appear here too'),
        hint));
    updateHint();
    focused?.focus();
  };
  const off = on('steps', () => { if (mode === 'await') render(); });
  render();
  return { element: root, off };
}

// The object model on each stack side by side, and the keys being watched
function mountModel(column) {
  let tab = 'tree';
  let root = stored('modelRoot', '');
  let onlyDifferences = stored('modelOnlyDifferences', false);
  const expanded = new Set(stored('modelExpanded', []));
  const collapsed = new Set(stored('modelCollapsed', []));
  let watched = stored('watched', []);
  const body = h('div', { class: 'card' });
  const tabs = h('div', { class: 'tabs', role: 'tablist', 'aria-label': 'Object model' });
  column.append(tabs, body);

  const lastStep = () => [...state.steps].reverse().find(s => s.state !== 'running')?.number;

  const toggle = row => {
    if (row.open) {
      expanded.delete(row.key);
      collapsed.add(row.key);
    } else {
      collapsed.delete(row.key);
      expanded.add(row.key);
    }
    store('modelExpanded', [...expanded]);
    store('modelCollapsed', [...collapsed]);
    render();
  };

  const watch = key => {
    watched = watched.includes(key) ? watched.filter(k => k !== key) : [...watched, key];
    store('watched', watched);
    render();
  };

  const rowElement = (r, names, isWatchedRoot) => h('div', { class: `tree-row ${r.differs ? 'differs' : ''} ${r.changed ? 'changed' : ''} ${r.ignored ? 'ignored' : ''}`,
                                                             title: r.ignored ? 'Not compared: it describes the computer, or changes with time' : undefined },
    h('span', { class: 'key', style: `padding-left: ${r.depth * 14}px` },
      r.container
        ? h('button', { type: 'button', 'aria-expanded': r.open, onclick: () => toggle(r) }, h('span', { class: 'toggle' }, icon(r.open ? 'down' : 'open')), r.name)
        : h('button', { type: 'button', title: `Copy ${r.key}`, onclick: e => copy(r.key, e.currentTarget) }, r.name)),
    names.map(s => {
      const v = r.values[s];
      return h('span', { class: `value ${v !== null && typeof v === 'object' ? 'container' : ''}` }, v === undefined ? (isWatchedRoot ? 'absent' : '') : summary(v));
    }),
    h('span', { class: 'marks' }, r.differs ? '≠' : r.changed ? '•' : '',
      isWatchedRoot
        ? h('button', { class: 'watch', type: 'button', 'aria-pressed': true, 'aria-label': `Stop watching ${r.key}`, onclick: () => watch(r.key) }, icon('close'))
        : h('button', { class: 'watch', type: 'button', 'aria-pressed': watched.includes(r.key), 'aria-label': `Watch ${r.key}`, onclick: () => watch(r.key) }, icon('eye'))));

  const render = () => {
    // Drawing again keeps where the tree was scrolled to
    const scrolled = body.querySelector('.model-body')?.scrollTop ?? 0;
    const names = Object.keys(state.models);
    replace(tabs, [['tree', 'Object model'], ['watched', `Watched (${watched.length})`]].map(([value, label]) =>
      h('button', { type: 'button', role: 'tab', 'aria-selected': tab === value, onclick: () => { tab = value; render(); } }, label)));
    if (names.length === 0) {
      replace(body, h('div', { class: 'empty-state' }, 'Waiting for the object model'));
      return;
    }
    const number = lastStep();
    const options = { expanded, collapsed, changed: number === undefined ? new Set() : changedPaths(number), ignored: state.status.ignoredPaths ?? [], onlyDifferences };
    const head = h('div', { class: 'tree-head' }, h('span', {}, 'Key'), names.map(s => h('span', { class: `stack stack-${s}` }, s)), h('span', {}));
    const controls = h('div', { style: 'padding: 10px; border-bottom: 1px solid var(--line-soft); display: flex; flex-direction: column; gap: 8px' },
      tab === 'tree' ? h('input', { class: 'input mono', type: 'text', value: root, placeholder: 'A key, such as move.axes[0]; empty for all', 'aria-label': 'Object model key',
                                    onchange: e => { root = e.target.value.trim(); store('modelRoot', root); if (root) { expanded.add(root); collapsed.delete(root); } render(); } }) : null,
      h('div', { class: 'legend', style: 'align-items: center' },
        names.length > 1 ? h('label', { class: 'option', style: 'padding: 2px 10px' },
          h('input', { type: 'checkbox', checked: onlyDifferences, onchange: e => { onlyDifferences = e.target.checked; store('modelOnlyDifferences', onlyDifferences); render(); } }),
          'Only differences') : null,
        h('span', {}, '• changed in the last step'), names.length > 1 ? h('span', { style: 'color: var(--diff)' }, '≠ stacks differ') : null,
        h('span', { class: 'faint' }, 'grey: not compared')));

    let content;
    if (tab === 'watched') {
      content = watched.length === 0
        ? h('div', { class: 'empty-state' }, 'Watch a key with its eye in the object model, to keep it in view')
        : watched.map(key => {
          let rows;
          try {
            rows = treeRows(state.models, { ...options, root: key, onlyDifferences: false });
          } catch (e) {
            return h('div', { class: 'tree-row' }, h('span', { class: 'key' }, key), h('span', {}, e.message));
          }
          const shown = onlyDifferences ? rows.filter((r, i) => i === 0 || r.differs) : rows;
          return shown.map((r, i) => rowElement(r, names, i === 0));
        });
    } else {
      let rows = [];
      let problem = null;
      try {
        rows = treeRows(state.models, { ...options, root });
      } catch (e) {
        problem = e.message;
      }
      content = [problem ? h('div', { class: 'empty-state' }, problem) : null,
                 rows.length === 0 && !problem ? h('div', { class: 'empty-state' }, onlyDifferences ? 'The stacks\' object models are the same' : 'Nothing here') : null,
                 rows.map(r => rowElement(r, names, false))];
    }
    replace(body, controls, h('div', { class: 'model-body', style: `--stacks: ${names.length}` }, head, content));
    body.querySelector('.model-body').scrollTop = scrolled;
  };

  const offs = ['models', 'steps', 'session'].map(topic => on(topic, render));
  render();
  return () => offs.forEach(off => off());
}
