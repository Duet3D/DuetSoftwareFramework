// One step on each stack side by side: its replies, the CAN frames and pin changes lined up, and the object model at its end

import { api } from '../api.js';
import { PERIODIC_CAN_TYPES, countEvents, describe, destination, json, splitSteps } from '../capture.js';
import { debounce, h, icon, replace, stored, store } from '../dom.js';
import { on, stacks, state } from '../store.js';
import { loadRun } from './run.js';

/** The comparison options as the API's query: what the page's toggles leave in */
export function optionsQuery(options) {
  const query = new URLSearchParams();
  const hidden = options.hiddenTypes ?? [...PERIODIC_CAN_TYPES];
  if ([...PERIODIC_CAN_TYPES].some(type => !hidden.includes(type))) {
    // A periodic type is shown, so the server lines them up too; those still hidden are hidden here
    query.set('periodic', '1');
  }
  if (!options.ignoreFields) query.set('fields', '1');
  if (options.tolerance) query.set('tolerance', options.tolerance);
  return query.toString();
}

export function mountStep(page, { source, number, id }) {
  const options = { ignoreFields: true, onlyDifferences: false, tolerance: '', hiddenTypes: [...PERIODIC_CAN_TYPES], ...stored('compareOptions', {}) };
  const open = new Set();
  let data = null;
  let error = null;
  let offs = [];

  const load = async () => {
    try {
      if (source === 'run') {
        const run = await loadRun(id, optionsQuery(options));
        const names = Object.keys(run.captures);
        data = {
          names,
          captures: Object.fromEntries(names.map(s => [s, run.captures[s].events])),
          alignment: run.alignment ?? null,
          title: run.name,
          back: `#/runs/${id}`,
          href: n => `#/runs/${id}/step/${n}`
        };
      } else {
        const names = stacks();
        data = {
          names,
          captures: state.captures,
          alignment: names.length === 2 ? await api.get(`/alignment?${optionsQuery(options)}`) : null,
          title: state.status.session?.name ?? '',
          back: '#/',
          href: n => `#/step/${n}`
        };
      }
      error = null;
    } catch (e) {
      error = e.message;
    }
    render();
  };

  const render = () => {
    if (error) return replace(page, h('div', { class: 'compare' }, h('a', { class: 'button link', href: source === 'run' ? `#/runs/${id}` : '#/' }, icon('back'), 'Back'),
      h('div', { class: 'notice err', role: 'alert' }, error)));
    if (!data) return replace(page, h('div', { class: 'empty-state' }, h('span', { class: 'spinner', style: 'display: inline-block' }), ' Lining the captures up'));
    replace(page, compare(data, number, options, open, render, () => { store('compareOptions', options); load(); }));
  };

  if (source === 'session') {
    const reload = debounce(load, 200);
    offs = [on('steps', reload), on('capture', reload)];
  }
  render();
  load();
  return { unmount: () => offs.forEach(off => off()) };
}

// The comparison of capture step <number> + 1: the session numbers its steps from 1, after the start
function compare(data, number, options, open, render, reload) {
  const { names, captures, alignment } = data;
  const captureStep = number + 1;
  const split = Object.fromEntries(names.map(s => [s, splitSteps(captures[s] ?? [])]));
  const last = Math.max(...names.map(s => split[s].length)) - 1;
  const events = Object.fromEntries(names.map(s => [s, split[s][captureStep - 1]?.events ?? []]));
  const aligned = alignment?.steps[captureStep - 1];
  const action = aligned?.action ?? split[names[0]][captureStep - 1]?.action ?? '';
  const differences = alignment ? alignment.differences.filter(d => d.step === captureStep) : [];
  const hidden = new Set(options.hiddenTypes);
  const isIn = e => e.firstStep === captureStep || e.secondStep === captureStep;
  const shown = e => isIn(e) && !(options.onlyDifferences && e.matched);
  const canShown = e => shown(e) && !hidden.has((e.first ?? e.second).type);
  const can = alignment ? alignment.can.filter(canShown) : [];
  const pins = alignment ? alignment.pins.filter(shown) : [];
  const model = aligned ? aligned.objectModel.filter(p => !(options.onlyDifferences && p.same)) : [];
  const visible = e => e.kind !== 'can' || !hidden.has(e.type);

  // The CAN message types the step has, each a chip that shows or hides them
  const types = new Map();
  for (const stack of names) {
    for (const e of events[stack]) {
      if (e.kind === 'can') types.set(e.type, (types.get(e.type) ?? 0) + 1);
    }
  }
  const typeChips = types.size === 0 ? null : h('div', { class: 'options' },
    h('span', { class: 'muted', style: 'font-size: 13px' }, 'CAN messages'),
    [...types].sort((a, b) => a[0].localeCompare(b[0])).map(([type, count]) => h('button', {
      class: 'chip', type: 'button', 'aria-pressed': !hidden.has(type), title: hidden.has(type) ? `Show ${type}` : `Hide ${type}`,
      onclick: () => {
        options.hiddenTypes = hidden.has(type) ? options.hiddenTypes.filter(t => t !== type) : [...options.hiddenTypes, type];
        store('compareOptions', options);
        render();
      }
    }, `${type} ${count}`)),
    h('button', { class: 'button link', type: 'button', style: 'font-size: 12px', onclick: () => {
      options.hiddenTypes = [...PERIODIC_CAN_TYPES];
      store('compareOptions', options);
      render();
    } }, 'Hide only the periodic ones'));

  const option = (key, label) => h('label', { class: 'option' },
    h('input', { type: 'checkbox', checked: options[key], onchange: e => {
      options[key] = e.target.checked;
      if (key === 'onlyDifferences') {
        store('compareOptions', options);
        render();
      } else {
        reload();
      }
    } }), label);

  return h('div', { class: 'compare' },
    h('div', { class: 'compare-head' },
      h('a', { class: 'button link', href: data.back }, icon('back'), data.back === '#/' ? 'Steps' : data.title),
      h('span', { class: 'mono faint' }, number),
      h('h1', {}, number === 0 ? 'start: boot and config.g' : action),
      alignment ? h('span', { class: `badge ${differences.length === 0 ? 'ok' : 'diff'}` }, differences.length === 0 ? 'the same' : `${differences.length} ≠`) : null,
      h('div', { class: 'nav' },
        h('a', { class: 'button', href: data.href(number - 1), 'aria-disabled': number <= 0, style: number <= 0 ? 'pointer-events: none; opacity: 0.5' : '' }, icon('back'), `Step ${number - 1}`),
        h('a', { class: 'button', href: data.href(number + 1), 'aria-disabled': number >= last, style: number >= last ? 'pointer-events: none; opacity: 0.5' : '' }, `Step ${number + 1}`, icon('next')))),
    h('div', { class: 'side-cards' }, names.map(stack => sideCard(stack, events[stack], stack === names[0] ? aligned?.firstReply : aligned?.secondReply))),
    typeChips,
    alignment ? h('div', { class: 'options' },
      option('ignoreFields', 'Ignore RequestId, WhenToExecute, When'),
      option('onlyDifferences', 'Only differences'),
      h('label', { class: 'option' }, 'Time tolerance',
        h('input', { class: 'input mono', type: 'text', value: options.tolerance, placeholder: 'off', style: 'width: 64px; padding: 2px 6px', onchange: e => {
          options.tolerance = e.target.value.trim();
          reload();
        } }), 's')) : null,
    differences.length > 0 ? h('section', { class: 'card card-body', style: 'display: flex; flex-direction: column; gap: 6px' },
      differences.map(d => h('div', { class: 'difference' }, h('span', { class: 'aspect' }, d.aspect), h('span', { class: 'mono' }, d.description)))) : null,
    alignment && aligned && !aligned.compared ? h('div', { class: 'notice warn' }, 'The stacks did different steps by here, so this step is shown but not compared') : null,
    alignment ? h('section', { class: 'card' },
      h('h2', { class: 'card-head', style: 'font-size: 13px' }, 'CAN frames, lined up', h('span', { class: 'faint', style: 'font-weight: 400' }, `${can.length}`)),
      h('div', { class: 'scroll-x' }, h('div', { style: 'min-width: 860px' },
        alignedHead(names),
        can.length === 0 ? h('div', { class: 'empty-state' }, options.onlyDifferences ? 'No differences' : 'No frames') : null,
        can.map((pair, i) => alignedRow(pair, names, open, `can${i}`, render, frame)))) ) : null,
    alignment && pins.length > 0 ? h('section', { class: 'card' },
      h('h2', { class: 'card-head', style: 'font-size: 13px' }, 'Pin changes, lined up'),
      h('div', { class: 'scroll-x' }, h('div', { style: 'min-width: 860px' }, alignedHead(names), pins.map((pair, i) => alignedRow(pair, names, open, `pin${i}`, render, pin))))) : null,
    alignment ? h('section', { class: 'card' },
      h('h2', { class: 'card-head', style: 'font-size: 13px' }, 'Object model at the end of the step'),
      h('div', { class: 'scroll-x model-rows' }, h('div', { style: 'min-width: 600px' },
        h('div', { class: 'row head' }, h('span', {}, 'Path'), names.map(s => h('span', { class: `stack stack-${s}` }, s)), h('span', {})),
        model.length === 0 ? h('div', { class: 'empty-state' }, options.onlyDifferences ? 'No differences' : 'Nothing changed') : null,
        model.map(p => h('div', { class: `row ${p.same ? '' : 'differs'}` },
          h('span', {}, p.path),
          h('span', { class: 'value' }, change(p.firstBefore, p.first)),
          h('span', { class: 'value' }, change(p.secondBefore, p.second)),
          h('span', { class: 'mark' }, p.same ? '✓' : '≠')))))) : null,
    alignment ? null : names.map(stack => single(stack, events[stack], hidden, open, render)),
    h('div', { class: 'side-cards' }, names.map(stack => h('details', { class: `card stack-${stack}` },
      h('summary', { class: 'card-head', style: 'cursor: pointer; font-size: 13px; font-weight: 600' }, h('span', { class: 'dot' }), `Everything ${stack} recorded in the step`),
      h('div', { class: 'event-list' }, events[stack].filter(visible).length === 0 ? h('span', { class: 'faint' }, 'Nothing') : events[stack].filter(visible).map(e =>
        h('div', {}, h('span', { class: 't' }, e.t.toFixed(6)), describe(e))))))));
}

// One stack's step on its own: its CAN frames, each opening to its fields, its object model changes and its pins
function single(stack, events, hidden, open, render) {
  const frames = events.filter(e => e.kind === 'can' && !hidden.has(e.type));
  const changes = events.filter(e => e.kind === 'om');
  const pinChanges = events.filter(e => e.kind === 'pin' || e.kind === 'input' || e.kind === 'input-change');
  const toggle = key => () => {
    if (open.has(key)) open.delete(key);
    else open.add(key);
    render();
  };
  return [
    h('section', { class: `card stack-${stack}` },
      h('h2', { class: 'card-head', style: 'font-size: 13px' }, h('span', { class: 'dot' }), `CAN frames on ${stack}`, h('span', { class: 'faint', style: 'font-weight: 400' }, frames.length)),
      h('div', { class: 'scroll-x' }, h('div', { style: 'min-width: 640px' },
        h('div', { class: 'single-head' }, h('span', {}, 'Time'), h('span', {}, 'From → to'), h('span', {}, 'Message'), h('span', {}, 'Fields')),
        frames.length === 0 ? h('div', { class: 'empty-state' }, 'No frames') : null,
        frames.map((frame, i) => {
          const key = `${stack}-can${i}`;
          return h('div', { class: 'aligned' },
            h('div', { class: 'single-row', role: 'button', tabindex: 0, 'aria-expanded': open.has(key), onclick: toggle(key),
                       onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(key)(); } } },
              h('span', { class: 'time' }, frame.t.toFixed(6)),
              h('span', {}, `${frame.source} → ${destination(frame.destination)}`),
              h('span', { class: 'frame' }, frame.type, frame.response ? h('span', { class: 'faint' }, ' response') : null),
              h('span', { class: 'faint', style: 'overflow: hidden; text-overflow: ellipsis; white-space: nowrap' }, frame.message ? JSON.stringify(frame.message) : frame.data)),
            open.has(key) ? h('div', { class: 'fields', style: 'grid-template-columns: 1fr; margin-left: 14px' }, h('pre', {}, fields(frame))) : null);
        })))),
    h('section', { class: `card stack-${stack}` },
      h('h2', { class: 'card-head', style: 'font-size: 13px' }, h('span', { class: 'dot' }), `Object model changes on ${stack}`, h('span', { class: 'faint', style: 'font-weight: 400' }, changes.length)),
      h('div', { class: 'scroll-x model-rows' }, h('div', { style: 'min-width: 600px' },
        h('div', { class: 'row head single' }, h('span', {}, 'Path'), h('span', {}, 'Before'), h('span', {}, 'After')),
        changes.length === 0 ? h('div', { class: 'empty-state' }, 'Nothing changed') : null,
        changes.map(c => h('div', { class: 'row single' }, h('span', {}, c.path), h('span', { class: 'faint' }, json(c.old ?? null)), h('span', {}, json(c.new ?? null))))))),
    pinChanges.length > 0 ? h('section', { class: `card stack-${stack}` },
      h('h2', { class: 'card-head', style: 'font-size: 13px' }, h('span', { class: 'dot' }), `Pins and inputs on ${stack}`),
      h('div', { class: 'event-list' }, pinChanges.map(e => h('div', {}, h('span', { class: 't' }, e.t.toFixed(6)), describe(e))))) : null
  ];
}

function sideCard(stack, events, reply) {
  const counts = countEvents(events);
  const replyEvent = reply ?? events.find(e => e.kind === 'reply');
  const first = events[0]?.t;
  const lastT = events[events.length - 1]?.t;
  return h('div', { class: `card side-card stack-${stack}` },
    h('span', { class: 'title' }, `${stack}${first !== undefined ? ` · ${first.toFixed(3)} s to ${lastT.toFixed(3)} s` : ''}`),
    h('span', { class: `reply ${replyEvent ? replyEvent.severity : 'none'} ${replyEvent?.text === '' ? 'empty' : ''}` },
      replyEvent ? `${replyEvent.severity === 'success' ? '' : `${replyEvent.severity}: `}${replyEvent.text === '' ? '(empty)' : replyEvent.text}` : 'no reply'),
    h('span', { class: 'muted', style: 'font-size: 12px' }, `${counts.can} CAN frames · ${counts.om} model changes · ${counts.pin} pin changes`));
}

function alignedHead(names) {
  return h('div', { class: 'aligned-head' },
    h('span', { class: `stack stack-${names[0]}` }, `${names[0]} time`), h('span', { class: `stack stack-${names[0]}` }, names[0]), h('span', {}),
    h('span', { class: `stack stack-${names[1]}` }, names[1]), h('span', { class: `stack stack-${names[1]}` }, `${names[1]} time`));
}

function alignedRow(pair, names, open, key, render, label) {
  const isOpen = open.has(key);
  const toggle = () => {
    if (isOpen) open.delete(key);
    else open.add(key);
    render();
  };
  const side = (event, stack) => event ? h('span', { class: 'frame' }, label(event)) : h('span', { class: 'frame absent' }, `not sent by ${stack}`);
  return h('div', { class: `aligned ${pair.matched ? '' : 'missing'}` },
    h('div', { class: 'aligned-row', role: 'button', tabindex: 0, 'aria-expanded': isOpen, onclick: toggle,
               onkeydown: e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } } },
      h('span', { class: 'time' }, pair.first ? pair.first.t.toFixed(6) : '—'),
      side(pair.first, names[0]),
      h('span', { class: 'mark' }, pair.matched ? '=' : '≠'),
      side(pair.second, names[1]),
      h('span', { class: 'time' }, pair.second ? pair.second.t.toFixed(6) : '—')),
    isOpen ? h('div', { class: 'fields' },
      pair.first ? h('pre', {}, fields(pair.first)) : h('div', { class: 'absent' }, `${names[0]} sent nothing in its place`),
      h('span', {}),
      pair.second ? h('pre', {}, fields(pair.second)) : h('div', { class: 'absent' }, `${names[1]} sent nothing in its place`)) : null);
}

function frame(event) {
  return `${event.type}  ${event.source}→${destination(event.destination)}${event.response ? ' (response)' : ''}`;
}

function pin(event) {
  return `${event.board}.${event.ports.join('/')} ${event.output}${event.duty != null ? ` ${(event.duty * 100).toFixed(1)}% ${event.frequency} Hz` : ''}`;
}

function fields(event) {
  if (event.kind === 'can') return event.message ? JSON.stringify(event.message, null, 2) : `raw ${event.data}`;
  return JSON.stringify(event, null, 2);
}

function value(v) {
  if (!v.present) return 'absent';
  return json(v.value ?? null);
}

function change(before, after) {
  const a = value(before), b = value(after);
  return a === b ? b : `${a} → ${b}`;
}
