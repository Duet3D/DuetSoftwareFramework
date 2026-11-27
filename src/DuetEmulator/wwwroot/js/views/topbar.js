// The bar along the top: the session, each stack's emulated time, what the server is doing, and the session's actions

import { api } from '../api.js';
import { h, icon, replace } from '../dom.js';
import { differenceCount, hasSession, on, stacks, state } from '../store.js';

export function mountTopbar(bar) {
  // A choice being confirmed: 'end' or 'restart', which stays until one of its buttons is pressed
  let confirming = null;
  let notice = null;
  let busy = false;

  // Runs a request, showing the label meanwhile, then what the request returns or why it failed
  const act = async (label, request) => {
    confirming = null;
    busy = true;
    notice = label;
    render();
    try {
      notice = (await request()) ?? null;
    } catch (e) {
      notice = e.message;
    } finally {
      busy = false;
      render();
    }
  };

  const save = () => act('Saving', async () => {
    const saved = await api.post('/save');
    const text = `Saved in ${Object.values(saved)[0].replace(/\/[^/]+\/[^/]+$/, '')}`;
    setTimeout(() => {
      if (notice === text) {
        notice = null;
        render();
      }
    }, 2500);
    return text;
  });

  const end = () => act('Ending the session', async () => {
    await api.delete('/session');
    location.hash = '#/';
  });

  const restart = replay => act('Restarting', async () => {
    location.hash = '#/';
    await api.post(`/session/restart${replay ? '?replay=true' : ''}`);
  });

  const render = () => {
    const status = state.status;
    const session = status.session;
    const children = [h('a', { class: 'brand', href: '#/' }, icon('logo'), 'Emulation bench')];
    if (session) {
      const boards = Object.entries(session.boards).map(([address, type]) => `${type} at ${address}`);
      children.push(h('div', { class: 'session-name' },
        h('strong', {}, session.name),
        h('span', { class: 'muted' }, ['MB6HC', ...boards].join(' · '))));
      children.push(h('div', { class: 'clocks' }, status.stacks.map(s =>
        h('span', { class: `clock stack-${s.stack}`, title: `${s.stack}'s emulated time and the events it has recorded` },
          h('span', { class: 'dot' }), h('b', {}, s.stack.toUpperCase()), h('span', { class: 'mono' }, `${s.time.toFixed(3)} s`),
          h('span', { class: 'muted' }, `${s.events} events`)))));
    }
    children.push(phase(status));
    if (session) {
      const differences = differenceCount();
      const firstDifferent = state.steps.find(s => s.differences.length > 0);
      const steps = state.steps.length;
      const actions = confirming === 'end'
        ? [h('span', { class: 'muted', style: 'font-size: 13px' }, 'End the session? Its captures are saved.'),
           h('button', { class: 'button primary', type: 'button', onclick: end }, 'End session'),
           h('button', { class: 'button', type: 'button', onclick: () => { confirming = null; render(); } }, 'Keep it')]
        : confirming === 'restart'
          ? [h('span', { class: 'muted', style: 'font-size: 13px' }, 'Boot the boards afresh with the same config.g?'),
             h('button', { class: 'button', type: 'button', onclick: () => restart(false) }, 'Restart'),
             steps > 0 ? h('button', { class: 'button primary', type: 'button', onclick: () => restart(true) }, `Restart and replay ${steps} step${steps === 1 ? '' : 's'}`) : null,
             h('button', { class: 'button', type: 'button', onclick: () => { confirming = null; render(); } }, 'Cancel')]
          : [stacks().length === 2
               ? h('a', { class: `badge ${differences === 0 ? 'ok' : 'diff'}`, href: firstDifferent ? `#/step/${firstDifferent.number}` : '#/' },
                   differences === 0 ? 'No differences' : `${differences} difference${differences === 1 ? '' : 's'}`)
               : null,
             h('button', { class: 'button', type: 'button', onclick: save, disabled: busy }, icon('save'), 'Save captures'),
             h('a', { class: 'button', href: '#/export' }, 'Save as scenario'),
             h('button', { class: 'button', type: 'button', disabled: busy, onclick: () => { confirming = 'restart'; render(); } }, 'Restart'),
             h('button', { class: 'button primary', type: 'button', disabled: busy, onclick: () => { confirming = 'end'; render(); } }, 'End session')];
      children.push(h('div', { class: 'actions' }, notice ? h('span', { class: 'muted', role: 'status', style: 'font-size: 13px' }, notice) : null, actions));
    }
    replace(bar, children);
  };

  for (const topic of ['status', 'steps', 'connection', 'session']) on(topic, render);
  render();
}

function phase(status) {
  if (!state.connected) return h('span', { class: 'phase offline', role: 'status' }, 'Server not answering');
  switch (status.phase) {
    case 'booting': return h('span', { class: 'phase', role: 'status' }, h('span', { class: 'spinner' }), status.detail ?? 'Booting');
    case 'running': return h('span', { class: 'phase', role: 'status' }, h('span', { class: 'spinner' }), 'Running');
    case 'ready': return h('span', { class: 'phase ready', role: 'status' }, 'Ready');
    case 'failed': return h('span', { class: 'phase offline', role: 'status' }, 'Starting failed');
    default: return hasSession() ? null : h('span', { class: 'phase muted', role: 'status' }, 'No session');
  }
}
