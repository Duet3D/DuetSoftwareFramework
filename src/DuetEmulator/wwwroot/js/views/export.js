// Saving the session as a scenario: the steps to keep, codes to move to config.g, and where it goes

import { api, ApiError } from '../api.js';
import { debounce, h, icon, replace } from '../dom.js';
import { state } from '../store.js';
import { cliPrefix, quote } from './cli.js';

// Queries that look at the machine without changing it, which a scenario does not usually need
const QUERY = /^M(40[89]|11[45]|122)\b/i;

/** The file name a scenario of a name is saved as */
export function fileName(name) {
  return `${name.trim().toLowerCase().replace(/[^a-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'scenario'}.yaml`;
}

export function mountExport(host) {
  const session = state.status.session;
  const steps = state.steps.filter(s => s.state !== 'running');
  const form = {
    name: session.name,
    description: session.description ?? '',
    keep: new Set(steps.filter(s => s.state === 'done' && !(s.action.code && QUERY.test(s.action.code))).map(s => s.number)),
    toConfig: new Set(),
    destination: 'repository',
    path: `src/DuetEmulation/Scenarios/${fileName(session.name)}`,
    pathEdited: false
  };
  let yaml = '';
  let message = null;
  let exists = false;

  const close = () => { location.hash = '#/'; };
  const scrim = h('div', { class: 'scrim', onclick: close });
  const wrap = h('div', { class: 'dialog-wrap', onclick: e => { if (e.target === wrap) close(); } });
  const dialog = h('div', { class: 'dialog', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'export-title' });
  wrap.append(dialog);
  host.append(scrim, wrap);
  const onKey = e => { if (e.key === 'Escape') close(); };
  document.addEventListener('keydown', onKey);

  const request = () => ({
    name: form.name,
    description: form.description,
    steps: steps.filter(s => form.keep.has(s.number) || form.toConfig.has(s.number)).map(s => s.number),
    toConfig: [...form.toConfig]
  });

  const preview = h('pre', {}, '');
  const command = h('code', {});
  const refresh = debounce(async () => {
    try {
      yaml = (await api.post('/scenario', request())).yaml;
      preview.textContent = yaml;
    } catch (e) {
      preview.textContent = e.message;
    }
  }, 150);

  const updateCommand = () => {
    const skipped = steps.filter(s => !form.keep.has(s.number) && !form.toConfig.has(s.number)).map(s => s.number);
    const parts = [`${cliPrefix()} export`];
    if (form.destination === 'repository') parts.push(quote(form.path));
    if (form.name !== session.name) parts.push(`--name ${quote(form.name)}`);
    if (form.description) parts.push(`--description ${quote(form.description)}`);
    if (skipped.length > 0) parts.push(`--skip ${skipped.join(',')}`);
    if (form.toConfig.size > 0) parts.push(`--to-config ${[...form.toConfig].sort((a, b) => a - b).join(',')}`);
    command.textContent = parts.join(' ');
  };

  const changed = () => {
    exists = false;
    message = null;
    refresh();
    updateCommand();
  };

  const save = async (overwrite = false) => {
    message = null;
    if (form.destination === 'download') {
      const link = h('a', { href: URL.createObjectURL(new Blob([yaml], { type: 'application/yaml' })), download: fileName(form.name) });
      link.click();
      URL.revokeObjectURL(link.href);
      close();
      return;
    }
    try {
      const answer = await api.post('/scenario', { ...request(), path: form.path, overwrite });
      message = { ok: true, text: `Saved ${answer.path}` };
      exists = false;
    } catch (e) {
      exists = e instanceof ApiError && e.status === 409;
      message = { ok: false, text: exists ? `${form.path} exists already` : e.message };
    }
    render();
  };

  const render = () => {
    const kept = steps.filter(s => form.keep.has(s.number) || form.toConfig.has(s.number)).length;
    replace(dialog,
      h('div', { class: 'dialog-head' },
        h('div', { style: 'display: flex; flex-direction: column; gap: 2px' },
          h('h2', { id: 'export-title', style: 'font-size: 18px' }, 'Save as scenario'),
          h('span', { class: 'muted', style: 'font-size: 13px' }, 'The session\'s boards, config.g, files and the steps you pick, as a scenario that scripts/emulate.py run replays')),
        h('button', { class: 'button icon-only', type: 'button', 'aria-label': 'Close', onclick: close, style: 'margin-left: auto' }, icon('close'))),
      h('div', { class: 'dialog-body' },
        h('div', { class: 'dialog-form' },
          h('label', { class: 'field' }, 'Name',
            h('input', { class: 'input', type: 'text', value: form.name, oninput: e => {
              form.name = e.target.value;
              if (!form.pathEdited) {
                form.path = `src/DuetEmulation/Scenarios/${fileName(form.name)}`;
                dialog.querySelector('#export-path').value = form.path;
              }
              changed();
            } })),
          h('label', { class: 'field' }, 'Description',
            h('span', { class: 'hint' }, 'Becomes the comment at the top of the file'),
            h('textarea', { class: 'input', rows: 2, oninput: e => { form.description = e.target.value; changed(); } }, form.description)),
          h('div', { class: 'field' },
            h('span', { style: 'display: flex; gap: 10px; align-items: baseline' }, 'Steps',
              h('span', { class: 'hint' }, `${kept} of ${steps.length} · queries and failed steps start unticked`),
              h('button', { class: 'button link', type: 'button', style: 'margin-left: auto; font-size: 12px', onclick: () => {
                steps.forEach(s => form.keep.add(s.number));
                changed();
                render();
              } }, 'Tick all')),
            h('div', { class: 'pick-list' },
              steps.length === 0 ? h('div', { class: 'empty-state' }, 'No steps yet: the scenario is the boards and config.g') : null,
              steps.map(step => {
                const on = form.keep.has(step.number) || form.toConfig.has(step.number);
                const inConfig = form.toConfig.has(step.number);
                return h('div', { class: `pick ${on ? '' : 'off'}` },
                  h('input', { type: 'checkbox', checked: on, 'aria-label': `Keep step ${step.number}, ${step.step}`, onchange: e => {
                    if (e.target.checked) form.keep.add(step.number);
                    else {
                      form.keep.delete(step.number);
                      form.toConfig.delete(step.number);
                    }
                    changed();
                    render();
                  } }),
                  h('span', { class: 'mono faint', style: 'width: 22px; font-size: 11px' }, step.number),
                  h('span', { class: 'text' }, inConfig ? `config.g: ${step.action.code}` : step.step),
                  step.state === 'failed' ? h('span', { class: 'badge err' }, 'failed') : null,
                  step.action.code !== undefined && on ? h('button', { class: 'button small to-config', type: 'button', 'aria-pressed': inConfig, onclick: () => {
                    if (inConfig) {
                      form.toConfig.delete(step.number);
                      form.keep.add(step.number);
                    } else {
                      form.toConfig.add(step.number);
                      form.keep.delete(step.number);
                    }
                    changed();
                    render();
                  } }, inConfig ? 'Back to the steps' : 'Move to config.g') : null);
              }))),
          h('fieldset', { class: 'destination', style: 'border: 0; margin: 0; padding: 0' },
            h('legend', { class: 'field', style: 'padding: 0; margin-bottom: 8px' }, 'Save to'),
            h('label', {},
              h('input', { type: 'radio', name: 'destination', checked: form.destination === 'repository', onchange: () => { form.destination = 'repository'; changed(); render(); } }),
              h('span', { style: 'display: flex; flex-direction: column; gap: 6px; flex: 1' },
                h('span', { style: 'font-weight: 600; font-size: 13px' }, 'A file in the repository'),
                h('input', { id: 'export-path', class: 'input mono', type: 'text', value: form.path, 'aria-label': 'Path from the repository\'s root',
                             oninput: e => { form.path = e.target.value; form.pathEdited = true; changed(); } }),
                h('span', { class: 'faint', style: 'font-size: 12px' }, 'Written by the session server, which asks before replacing a file'))),
            h('label', {},
              h('input', { type: 'radio', name: 'destination', checked: form.destination === 'download', onchange: () => { form.destination = 'download'; changed(); render(); } }),
              h('span', { style: 'font-weight: 600; font-size: 13px' }, 'Download it')))),
        h('div', { class: 'dialog-preview' },
          h('div', { class: 'bar-head' },
            h('span', { class: 'mono', style: 'color: #fff' }, form.destination === 'repository' ? form.path.split('/').pop() : fileName(form.name)),
            h('span', {}, 'as it will be saved'),
            h('button', { class: 'button small', type: 'button', style: 'margin-left: auto', onclick: e => navigator.clipboard.writeText(yaml).then(() => {
              e.target.textContent = 'Copied';
            }) }, 'Copy')),
          preview)),
      h('div', { class: 'dialog-foot' },
        command,
        message ? h('span', { role: 'status', style: `font-size: 13px; color: var(${message.ok ? '--ok' : '--err'})` }, message.text) : null,
        exists ? h('button', { class: 'button danger', type: 'button', onclick: () => save(true) }, 'Replace it') : null,
        h('button', { class: 'button', type: 'button', onclick: close }, message?.ok ? 'Close' : 'Cancel'),
        h('button', { class: 'button primary', type: 'button', onclick: () => save() }, form.destination === 'download' ? 'Download' : 'Save scenario')));
    updateCommand();
  };

  render();
  refresh();
  dialog.querySelector('input')?.focus();

  return {
    unmount() {
      document.removeEventListener('keydown', onKey);
      scrim.remove();
      wrap.remove();
    }
  };
}
