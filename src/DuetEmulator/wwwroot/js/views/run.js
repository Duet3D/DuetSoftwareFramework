// A saved run: its steps on each stack and how they differ, without booting anything

import { api } from '../api.js';
import { splitSteps } from '../capture.js';
import { h, icon, replace, stored } from '../dom.js';
import { optionsQuery } from './step.js';

const cache = new Map();

/** A saved run with its captures, lined up as the options say; kept while the page is open */
export async function loadRun(id, query) {
  const key = `${id}?${query}`;
  if (!cache.has(key)) cache.set(key, api.get(`/runs/${id}?${query}`).catch(e => { cache.delete(key); throw e; }));
  return cache.get(key);
}

export function mountRun(page, id) {
  replace(page, h('div', { class: 'empty-state' }, h('span', { class: 'spinner', style: 'display: inline-block' }), ' Reading the run'));
  const options = { hidePeriodic: true, ignoreFields: true, tolerance: '', ...stored('compareOptions', {}) };
  loadRun(id, optionsQuery(options)).then(run => render(page, id, run)).catch(e =>
    replace(page, h('div', { class: 'compare' }, h('a', { class: 'button link', href: '#/' }, icon('back'), 'Start'), h('div', { class: 'notice err', role: 'alert' }, e.message))));
  return { unmount: () => {} };
}

function render(page, id, run) {
  const names = Object.keys(run.captures);
  const split = Object.fromEntries(names.map(s => [s, splitSteps(run.captures[s].events)]));
  const count = Math.max(...names.map(s => split[s].length));
  const header = run.captures[names[0]].header;
  const differences = run.alignment?.differences ?? [];
  const boards = Object.entries(header.boards).map(([address, type]) => `${type} at ${address}`);

  const reply = (stack, number) => {
    const event = split[stack][number]?.events.find(e => e.kind === 'reply');
    if (!event) return h('span', { class: 'reply none' }, split[stack][number] ? '—' : 'no such step');
    return h('span', { class: `reply ${event.severity} ${event.text === '' ? 'empty' : ''}` },
      `${event.severity === 'success' ? '' : `${event.severity}: `}${event.text === '' ? '(empty)' : event.text}`);
  };

  replace(page, h('div', { class: 'compare' },
    h('div', { class: 'compare-head' },
      h('a', { class: 'button link', href: '#/' }, icon('back'), 'Start'),
      h('h1', {}, run.name),
      h('span', { class: 'muted' }, `${id} · ${['MB6HC', ...boards].join(' · ')}`),
      run.alignment ? h('span', { class: `badge ${differences.length === 0 ? 'ok' : 'diff'}` },
        differences.length === 0 ? 'No differences' : `${differences.length} difference${differences.length === 1 ? '' : 's'}`) : null),
    header.config ? h('details', { class: 'card' }, h('summary', { class: 'card-head', style: 'cursor: pointer; font-size: 13px; font-weight: 600' }, 'config.g'),
      h('pre', { class: 'event-list', style: 'margin: 0' }, header.config)) : null,
    h('section', { class: 'card scroll-x' }, h('div', { class: 'steps-table', style: `--stacks: ${names.length}` },
      h('div', { class: 'steps-head' }, h('span', {}, '#'), h('span', {}, 'Step'), names.map(s => h('span', { class: `stack stack-${s}` }, h('span', { class: 'dot' }), `${s} reply`)),
        h('span', { style: 'text-align: right' }, 'Differences')),
      Array.from({ length: count }, (_, i) => {
        const step = differences.filter(d => d.step === i + 1);
        const action = split[names[0]][i]?.action ?? split[names[1]]?.[i]?.action;
        return h('a', { class: 'step', href: `#/runs/${id}/step/${i}`, style: 'display: block; color: inherit; text-decoration: none' },
          h('div', { class: 'step-row' },
            h('span', { class: 'step-number' }, i),
            h('div', { class: 'step-what' }, h('div', {}, h('span', { class: 'text' }, i === 0 ? 'start' : action))),
            names.map(s => reply(s, i)),
            h('div', { class: 'step-counts' }, step.length > 0 ? h('span', { class: 'badge diff' }, `${step.length} ≠`) : null)));
      })))));
}
