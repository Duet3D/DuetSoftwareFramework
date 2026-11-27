// The bar along the top: the session, each stack's emulated time, what the server is doing, and the session's actions

import { api } from '../api.js';
import { h, icon, replace } from '../dom.js';
import { differenceCount, hasSession, on, stacks, state } from '../store.js';

export function mountTopbar(bar) {
  let confirmEnd = false;
  let saved = null;

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
      children.push(h('div', { class: 'actions' },
        stacks().length === 2
          ? h('a', { class: `badge ${differences === 0 ? 'ok' : 'diff'}`, href: firstDifferent ? `#/step/${firstDifferent.number}` : '#/' },
              differences === 0 ? 'No differences' : `${differences} difference${differences === 1 ? '' : 's'}`)
          : null,
        h('button', { class: 'button', type: 'button', onclick: save, dataset: saved ? { flash: saved } : undefined }, icon('save'), 'Save captures'),
        h('a', { class: 'button', href: '#/export' }, 'Save as scenario'),
        h('button', { class: `button ${confirmEnd ? 'danger' : 'primary'}`, type: 'button', onclick: end,
                      onblur: () => { confirmEnd = false; render(); } },
          confirmEnd ? 'End it: captures are saved' : 'End session')));
    }
    replace(bar, children);
  };

  const save = async () => {
    try {
      await api.post('/save');
      saved = 'Saved';
    } catch (e) {
      saved = e.message;
    }
    render();
    setTimeout(() => { saved = null; render(); }, 1500);
  };

  const end = async () => {
    if (!confirmEnd) {
      confirmEnd = true;
      render();
      bar.querySelector('.button.danger')?.focus();
      return;
    }
    confirmEnd = false;
    await api.delete('/session');
    location.hash = '#/';
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
