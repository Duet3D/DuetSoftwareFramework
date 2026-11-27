// The start page: a session's stacks, boards, config.g and files, or a scenario file to start from; and the saved runs

import { api } from '../api.js';
import { h, icon, replace, stored, store } from '../dom.js';
import { on, state } from '../store.js';
import { cliPrefix, quote } from './cli.js';

const DEFAULT_FORM = {
  name: 'session',
  stacks: ['rrf', 'dsf'],
  boards: [{ address: 1, type: 'EXP3HC' }],
  config: 'M564 H0 S0\n',
  files: [],
  scenario: null,
  runSteps: true,
  hold: false
};

export function mountStart(page) {
  const form = { ...DEFAULT_FORM, ...stored('startForm', {}) };
  let boardTypes = [];
  let scenarios = [];
  let runs = [];
  let error = null;
  let starting = false;

  const formCard = h('section', { class: 'card start-form', 'aria-labelledby': 'start-title' });
  const side = h('aside', { class: 'start-side' });
  replace(page, h('div', { class: 'start' }, formCard, side));

  const remember = () => store('startForm', { ...form, scenario: null });

  const scenarioBody = () => {
    const body = {
      name: form.name.trim() || 'session',
      stacks: form.stacks,
      boards: Object.fromEntries(form.boards.map(b => [String(b.address), b.type])),
      config: form.config
    };
    if (form.files.length > 0) body.files = Object.fromEntries(form.files.map(f => [f.path, f.contents]));
    if (form.scenario?.beforeConfig?.length > 0) body.beforeConfig = form.scenario.beforeConfig;
    if (form.scenario && form.runSteps && form.scenario.steps.length > 0) body.steps = form.scenario.steps;
    return body;
  };

  const command = () => {
    const hold = form.hold ? ' --hold' : '';
    if (form.scenario && form.runSteps && form.scenario.steps.length > 0) {
      return `${cliPrefix()} start ${form.scenario.path}${hold}`;
    }
    const parts = [`${cliPrefix()} start${hold}`];
    if (form.name.trim() && form.name.trim() !== 'session') parts.push(`--name ${quote(form.name.trim())}`);
    if (form.stacks.length === 1) parts.push(`--stack ${form.stacks[0]}`);
    for (const board of form.boards) parts.push(`--board ${board.address}=${board.type}`);
    for (const line of form.config.split('\n').filter(l => l.trim().length > 0)) parts.push(`--config ${quote(line)}`);
    return parts.join(' ');
  };

  const boot = async () => {
    error = null;
    starting = true;
    render();
    try {
      await api.post(form.hold ? '/session?hold=true' : '/session', scenarioBody());
      location.hash = '#/';
    } catch (e) {
      error = e.message;
    } finally {
      starting = false;
      render();
    }
  };

  const useScenario = async path => {
    try {
      const file = await api.get(`/scenarios/file?path=${encodeURIComponent(path)}`);
      const scenario = file.scenario;
      Object.assign(form, {
        name: scenario.name,
        stacks: scenario.stacks,
        boards: Object.entries(scenario.boards).map(([address, type]) => ({ address: Number(address), type })),
        config: scenario.config,
        files: Object.entries(scenario.files).map(([path, contents]) => ({ path, contents })),
        scenario: { path: file.path, steps: scenario.steps, beforeConfig: scenario.beforeConfig ?? [], description: scenario.description }
      });
      remember();
      render();
    } catch (e) {
      error = e.message;
      render();
    }
  };

  const typeOf = name => boardTypes.find(t => t.name === name);

  const addBoard = type => {
    const used = new Set(form.boards.map(b => b.address));
    let address = type.defaultAddress;
    if (address === null || address === undefined) {
      address = type.addresses[0];
      while (used.has(address) && address < type.addresses[1]) address++;
    }
    form.boards.push({ address, type: type.name });
    remember();
    render();
  };

  const renderForm = () => {
    const booting = state.status.phase === 'booting' || starting;
    if (booting) {
      return replace(formCard,
        h('div', { class: 'booting' },
          h('h1', { id: 'start-title' }, 'Booting'),
          h('div', { class: 'status', role: 'status' }, h('span', { class: 'spinner' }), state.status.detail ?? 'Starting the session'),
          h('p', { class: 'muted' }, 'Each stack takes about a minute: the boards boot through their bootloaders, then config.g runs.')));
    }
    const failed = state.status.phase === 'failed' ? state.status.detail : null;
    replace(formCard,
      h('div', {},
        h('h1', { id: 'start-title' }, 'New session'),
        h('p', { class: 'muted', style: 'margin: 4px 0 0' }, 'Boot the boards once, then send codes and drive inputs a step at a time on each stack.')),
      error || failed ? h('div', { class: 'notice err', role: 'alert' }, error ?? `Starting the last session failed: ${failed}`) : null,
      form.scenario ? h('div', { class: 'notice warn' },
        `From ${form.scenario.path}`, form.scenario.description ? `: ${form.scenario.description}` : '',
        ' ', h('button', { class: 'button link', type: 'button', onclick: () => { form.scenario = null; render(); } }, 'Forget it')) : null,
      h('label', { class: 'field' }, 'Name',
        h('input', { class: 'input mono', type: 'text', value: form.name, oninput: e => { form.name = e.target.value; remember(); updateCli(); } }),
        h('span', { class: 'hint' }, 'Its files go in build/emulation/sessions under this name')),
      h('fieldset', { style: 'border: 0; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 10px' },
        h('legend', { class: 'field', style: 'margin-bottom: 10px; padding: 0' }, 'Stacks'),
        h('div', { class: 'stack-choice' },
          stackOption('rrf', 'RRF', 'RepRapFirmware standalone on the MB6HC', 'Duet3Firmware_MB6HC'),
          stackOption('dsf', 'DSF', 'DuetControlServer driving DuetCANMaster', 'DuetCANMaster_MB6HC'))),
      h('fieldset', { style: 'border: 0; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 10px' },
        h('legend', { class: 'field', style: 'margin-bottom: 10px; padding: 0' }, 'Expansion boards'),
        h('div', { class: 'board-list' },
          h('div', { class: 'board-list-row head' }, h('span', {}, 'Address'), h('span', {}, 'Board'), h('span', { class: 'note' }, 'Notes'), h('span', {})),
          form.boards.length === 0 ? h('div', { class: 'empty-state' }, 'The main board only') : null,
          form.boards.map((board, index) => {
            const type = typeOf(board.type);
            const fixed = type?.defaultAddress !== null && type?.defaultAddress !== undefined;
            const clash = form.boards.some((b, i) => i !== index && b.address === board.address);
            return h('div', { class: 'board-list-row' },
              h('input', { class: 'input mono', type: 'number', 'aria-label': `Address of the ${board.type}`, value: board.address, disabled: fixed,
                           min: type?.addresses[0], max: type?.addresses[1],
                           oninput: e => { board.address = Number(e.target.value); remember(); updateCli(); } }),
              h('select', { class: 'select', 'aria-label': 'Board type', onchange: e => {
                board.type = e.target.value;
                const chosen = typeOf(board.type);
                if (chosen?.defaultAddress != null) board.address = chosen.defaultAddress;
                remember();
                render();
              } }, boardTypes.map(t => h('option', { value: t.name, selected: t.name === board.type }, t.name))),
              h('span', { class: 'note muted', style: 'font-size: 13px' },
                clash ? h('span', { style: 'color: var(--err)' }, 'Two boards at this address') : type ? note(type) : ''),
              h('button', { class: 'button icon-only', type: 'button', 'aria-label': `Remove the ${board.type}`,
                            onclick: () => { form.boards.splice(index, 1); remember(); render(); } }, icon('close')));
          })),
        h('div', { class: 'chips', style: 'align-items: center' }, h('span', { class: 'muted', style: 'font-size: 13px' }, 'Add'),
          boardTypes.map(t => h('button', { class: 'button small', type: 'button', onclick: () => addBoard(t) },
            icon('plus'), t.defaultAddress != null ? `${t.name} at ${t.defaultAddress}` : t.name)))),
      h('label', { class: 'field' }, 'config.g',
        h('span', { class: 'hint' }, 'Runs after each stack\'s own first line'),
        h('textarea', { class: 'textarea', rows: 7, spellcheck: false, oninput: e => { form.config = e.target.value; remember(); updateCli(); } }, form.config)),
      h('div', { class: 'field' }, 'Files on the SD card',
        form.files.map((file, index) => h('div', { class: 'file-row' },
          h('div', {},
            h('input', { class: 'input mono', type: 'text', 'aria-label': 'Path on the SD card', value: file.path, style: 'flex: 1',
                         oninput: e => { file.path = e.target.value; remember(); } }),
            h('button', { class: 'button icon-only', type: 'button', 'aria-label': `Remove ${file.path}`,
                          onclick: () => { form.files.splice(index, 1); remember(); render(); } }, icon('trash'))),
          h('textarea', { class: 'textarea', rows: 3, spellcheck: false, 'aria-label': `Contents of ${file.path}`,
                          oninput: e => { file.contents = e.target.value; remember(); } }, file.contents))),
        h('button', { class: 'button small', type: 'button', style: 'align-self: flex-start',
                      onclick: () => { form.files.push({ path: 'sys/homex.g', contents: '' }); remember(); render(); } }, icon('plus'), 'Add a file')),
      form.scenario && form.scenario.steps.length > 0 ? h('label', { class: 'option', style: 'align-self: flex-start' },
        h('input', { type: 'checkbox', checked: form.runSteps, onchange: e => { form.runSteps = e.target.checked; updateCli(); } }),
        `Run its ${form.scenario.steps.length} steps once the boards are up`) : null,
      form.scenario?.beforeConfig?.length > 0 ? h('span', { class: 'muted', style: 'font-size: 13px' },
        `Its ${form.scenario.beforeConfig.length} step${form.scenario.beforeConfig.length === 1 ? '' : 's'} before config.g run${form.scenario.beforeConfig.length === 1 ? 's' : ''} first`) : null,
      h('label', { class: 'option', style: 'align-self: flex-start' },
        h('input', { type: 'checkbox', checked: form.hold, onchange: e => { form.hold = e.target.checked; remember(); updateCli(); } }),
        'Stop before config.g, so that inputs such as thermistors can be set first'),
      h('div', { style: 'border-top: 1px solid var(--line-soft); padding-top: 18px; display: flex; flex-direction: column; gap: 12px' },
        h('div', { class: 'cli' }, h('span', { class: 'label' }, 'The same from a terminal'), h('code', { id: 'start-cli' }, command())),
        h('div', { style: 'display: flex; align-items: center; gap: 14px; flex-wrap: wrap' },
          h('button', { class: 'button primary', type: 'button', style: 'min-height: 44px; padding: 0 22px; font-size: 15px',
                        disabled: form.stacks.length === 0, onclick: boot }, 'Boot session'),
          h('span', { class: 'muted', style: 'font-size: 13px' }, 'About a minute for each stack'))));
  };

  const updateCli = () => {
    const cli = formCard.querySelector('#start-cli');
    if (cli) cli.textContent = command();
  };

  const stackOption = (stack, title, what, firmware) =>
    h('label', { class: `stack-option stack-${stack}` },
      h('input', { type: 'checkbox', checked: form.stacks.includes(stack), onchange: e => {
        form.stacks = ['rrf', 'dsf'].filter(s => s === stack ? e.target.checked : form.stacks.includes(s));
        remember();
        render();
      } }),
      h('span', { style: 'display: flex; flex-direction: column; gap: 2px' },
        h('b', {}, title), h('span', { style: 'font-size: 13px' }, what), h('span', { class: 'mono faint', style: 'font-size: 11px' }, firmware)));

  const renderSide = () => replace(side,
    h('section', { class: 'card', 'aria-labelledby': 'scenarios-title' },
      h('h2', { id: 'scenarios-title' }, 'Or start from a scenario'),
      h('p', { class: 'faint', style: 'margin: 0; font-size: 12px' }, 'Picking one fills in the form, so it can be changed before booting'),
      scenarios.length === 0 ? h('p', { class: 'muted' }, 'No scenario files') : null,
      scenarios.map(file => h('button', { class: 'list-button', type: 'button', 'aria-pressed': form.scenario?.path === file.path,
                                          onclick: () => useScenario(file.path), disabled: file.error !== null },
        h('span', { class: 'mono' }, file.path.replace(/^src\/DuetEmulation\/Scenarios\//, '')),
        file.scenario?.description ? h('span', { class: 'what' }, file.scenario.description) : null,
        h('span', { class: 'faint', style: 'font-size: 12px' }, file.error ?? describeBoards(file)))) ),
    h('section', { class: 'card', 'aria-labelledby': 'runs-title' },
      h('h2', { id: 'runs-title' }, 'Saved runs'),
      h('p', { class: 'faint', style: 'margin: 0; font-size: 12px' }, 'Open a run\'s captures without booting anything'),
      runs.length === 0 ? h('p', { class: 'muted' }, 'None yet') : null,
      runs.slice(0, 12).map(run => h('div', { class: 'run-row' },
        h('span', { style: 'display: flex; flex-direction: column; min-width: 0; flex: 1' },
          h('span', { class: 'mono', style: 'overflow-wrap: anywhere' }, run.id),
          h('span', { class: 'faint', style: 'font-size: 12px' }, `${run.stacks.join(' and ')} · ${new Date(run.modified).toLocaleString()}`)),
        h('a', { class: 'button small', href: `#/runs/${run.id}` }, run.stacks.length === 2 ? 'Compare' : 'View')))));

  const render = () => {
    renderForm();
    renderSide();
  };

  const offStatus = on('status', renderForm);
  render();
  Promise.all([api.get('/board-types'), api.get('/scenarios'), api.get('/runs')]).then(([types, files, saved]) => {
    boardTypes = types;
    scenarios = files;
    runs = saved;
    render();
  }).catch(e => {
    error = e.message;
    render();
  });

  return { unmount: offStatus };
}

function note(type) {
  const drivers = `${type.drivers} driver${type.drivers === 1 ? '' : 's'}`;
  return type.defaultAddress != null ? `Its default address · ${drivers}` : `Address ${type.addresses[0]} to ${type.addresses[1]} · ${drivers}`;
}

function describeBoards(file) {
  const boards = Object.entries(file.scenario?.boards ?? {}).map(([address, type]) => `${type} at ${address}`);
  return `${boards.length === 0 ? 'Main board only' : boards.join(', ')} · ${file.stepCount} step${file.stepCount === 1 ? '' : 's'}`;
}
