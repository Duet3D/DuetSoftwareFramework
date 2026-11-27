// The page: a top bar, and the view the address names

import { h, replace } from './dom.js';
import { connect, hasSession, on, state } from './store.js';
import { mountTopbar } from './views/topbar.js';
import { mountStart } from './views/start.js';
import { mountWorkspace } from './views/workspace.js';
import { mountStep } from './views/step.js';
import { mountRun } from './views/run.js';

const app = document.getElementById('app');
const topbar = h('header', { class: 'topbar' });
const page = h('main', { class: 'page' });
replace(app, topbar, page);
mountTopbar(topbar);

let current = { key: null, unmount: null };

/**
 * What the address names: the session's workspace (with a board's drawer or the save dialog over it),
 * one of its steps, a saved run or one of its steps; the start page when there is no session
 */
function route() {
  const parts = location.hash.replace(/^#\/?/, '').split('/').filter(p => p.length > 0).map(decodeURIComponent);
  if (parts[0] === 'runs' && parts.length >= 3) {
    const id = `${parts[1]}/${parts[2]}`;
    return parts[3] === 'step' ? { view: 'run-step', id, number: Number(parts[4]) } : { view: 'run', id };
  }
  if (!hasSession()) return { view: 'start' };
  if (parts[0] === 'step') return { view: 'step', number: Number(parts[1]) };
  if (parts[0] === 'board') return { view: 'workspace', board: Number(parts[1]) };
  if (parts[0] === 'export') return { view: 'workspace', dialog: 'export' };
  return { view: 'workspace' };
}

function show() {
  const target = route();
  // The workspace stays mounted while its drawer or dialog changes, so that what is typed in it stays
  const key = target.view === 'workspace' ? `workspace:${state.status.directory}` : JSON.stringify(target);
  if (key === current.key && current.update) {
    current.update(target);
    return;
  }
  current.unmount?.();
  page.replaceChildren();
  page.dataset.view = target.view;
  const mounted = {
    start: () => mountStart(page),
    workspace: () => mountWorkspace(page, target),
    step: () => mountStep(page, { source: 'session', number: target.number }),
    run: () => mountRun(page, target.id),
    'run-step': () => mountStep(page, { source: 'run', id: target.id, number: target.number })
  }[target.view]();
  current = { key, unmount: mounted.unmount, update: mounted.update };
}

window.addEventListener('hashchange', show);
on('session', show);
connect();
show();
