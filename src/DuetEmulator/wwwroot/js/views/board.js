// A board's drawer: a control for each of its inputs, as the scenario step it sends, and what its drivers and outputs do

import { inputStates } from '../capture.js';
import { h, icon, replace } from '../dom.js';
import { on, run, stacks, state } from '../store.js';
import { stepCommand, stepYaml } from './cli.js';
import { output } from './workspace.js';

export function mountBoard(host, address) {
  let drawer = mountDrawer(host, address);
  // The boards are read after the session arrives, so a drawer opened from the address may come before its board
  const off = on('boards', () => {
    if (drawer.missing && state.boards.some(b => b.address === address)) {
      drawer.unmount();
      drawer = mountDrawer(host, address);
    }
  });
  return {
    unmount: () => {
      off();
      drawer.unmount();
    }
  };
}

function mountDrawer(host, address) {
  const board = state.boards.find(b => b.address === address);
  const close = () => { location.hash = '#/'; };
  const scrim = h('div', { class: 'scrim', onclick: close });
  const drawer = h('div', { class: 'drawer', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': 'drawer-title' });
  host.append(scrim, drawer);
  const onKey = e => { if (e.key === 'Escape') close(); };
  document.addEventListener('keydown', onKey);

  if (!board) {
    replace(drawer, h('div', { class: 'drawer-head' }, h('div', {}, h('h2', { id: 'drawer-title' }, `No board at ${address}`),
      h('button', { class: 'button icon-only', type: 'button', 'aria-label': 'Close', onclick: close, style: 'margin-left: auto' }, icon('close')))));
    return { missing: true, unmount: () => { scrim.remove(); drawer.remove(); document.removeEventListener('keydown', onKey); } };
  }

  // The parts that change as the boards do, refreshed without rebuilding the controls
  const live = [];
  const liveText = (render) => {
    const element = h('span', {});
    live.push(() => replace(element, render()));
    return element;
  };
  const refresh = () => live.forEach(update => update());

  const names = stacks();
  const inputsNow = () => Object.fromEntries(names.map(s => [s, inputStates(state.captures[s] ?? [])]));
  const boardState = stack => (state.boardStates[stack] ?? []).find(b => b.address === address);
  const ports = kind => board.ports.filter(p => p.kind === kind);
  const outputs = ports('output');

  const levelOf = port => liveText(() => {
    const inputs = inputsNow();
    const values = names.map(s => inputs[s].get(`${address}.${port}`)?.value ?? 'not driven');
    const alike = values.every(v => v === values[0]);
    return (alike ? [values[0]] : values).map((v, i) => h('span', {
      class: `level ${/^high/.test(v) ? 'high' : ''} ${alike ? '' : `stack-${names[i]}`}`, title: alike ? 'on every stack' : names[i]
    }, v));
  });

  replace(drawer,
    h('div', { class: 'drawer-head' },
      h('div', {},
        h('h2', { id: 'drawer-title' }, board.type),
        h('span', { class: 'address' }, `at ${address}`),
        h('button', { class: 'button icon-only', type: 'button', 'aria-label': 'Close', onclick: close, style: 'margin-left: auto' }, icon('close'))),
      h('span', { class: 'muted', style: 'font-size: 12px' }, 'Each stack has its own copy of this board; what is set here is set on each')),
    h('div', { class: 'drawer-body' },
      h('section', { class: 'drawer-section' },
        h('h3', { class: 'label' }, 'Drivers'),
        h('div', { class: 'drivers', style: `--stacks: ${names.length}` },
          h('span', { class: 'faint' }, 'driver'), names.map(s => h('span', { class: `stack stack-${s}` }, `${s} steps`)),
          Array.from({ length: board.drivers }, (_, driver) => [
            h('span', {}, `${address}.${driver}`),
            names.map(s => liveText(() => String(boardState(s)?.stepCounts[driver] ?? '—')))]))),
      ports('digitalInput').length > 0 ? h('section', { class: 'drawer-section' },
        h('h3', { class: 'label' }, 'Digital inputs'),
        ports('digitalInput').map(port => digitalInput(address, port.name, board.drivers, levelOf(port.name)))) : null,
      ports('analogInput').length > 0 ? h('section', { class: 'drawer-section' },
        h('h3', { class: 'label' }, 'Analogue inputs'),
        ports('analogInput').map(port => analogInput(address, port.name, levelOf(port.name)))) : null,
      ports('tachoInput').length > 0 ? h('section', { class: 'drawer-section' },
        h('h3', { class: 'label' }, 'Pulses and tachos'),
        ports('tachoInput').map(port => tachoInput(address, port.name, outputs, levelOf(port.name)))) : null,
      h('section', { class: 'drawer-section' },
        h('div', { class: 'section-head' }, h('h3', { class: 'label' }, 'Outputs'), h('span', { class: 'faint', style: 'font-size: 12px' }, 'what the firmware drives')),
        h('div', { style: `display: flex; flex-direction: column; gap: 6px; --stacks: ${names.length}` },
          outputs.map(port => h('div', { class: 'output-row' },
            h('span', { style: 'font-weight: 600' }, port.name),
            liveText(() => {
              const duty = Math.max(0, ...names.map(s => dutyOf(boardState(s)?.outputs[port.name])));
              return h('div', { class: 'bar' }, h('div', { style: `width: ${duty * 100}%` }));
            }),
            names.map(s => liveText(() => h('span', { class: `stack-${s}`, title: s }, output(boardState(s)?.outputs[port.name])))))))),
      ports('other').length > 0 ? h('section', { class: 'drawer-section' },
        h('details', {}, h('summary', { class: 'label', style: 'cursor: pointer' }, `Other pins (${ports('other').length})`),
          h('div', { style: 'display: flex; flex-direction: column; gap: 10px; margin-top: 10px' },
            ports('other').map(port => digitalInput(address, port.name, board.drivers, levelOf(port.name), false))))) : null));

  refresh();
  const offs = ['boards', 'capture'].map(topic => on(topic, refresh));
  drawer.querySelector('button')?.focus();

  return {
    unmount() {
      offs.forEach(off => off());
      document.removeEventListener('keydown', onKey);
      scrim.remove();
      drawer.remove();
    }
  };
}

function dutyOf(output) {
  if (!output) return 0;
  if (output.mode === 'Pwm') return output.duty;
  return output.mode === 'High' ? 1 : 0;
}

// A control's step: what it sends, shown as YAML under it, and sent with its button
function sender(build) {
  const yaml = h('code', { class: 'step-yaml' });
  const error = h('span', { style: 'color: var(--err); font-size: 12px' });
  const update = () => {
    try {
      const step = build();
      yaml.textContent = step ? stepYaml(step) : '';
      yaml.title = step ? stepCommand(step, stepYaml(step)) : '';
    } catch (e) {
      yaml.textContent = e.message;
    }
  };
  const send = async (step = build()) => {
    error.textContent = '';
    try {
      await run(step);
    } catch (e) {
      error.textContent = e.message;
    }
  };
  return { yaml, error, update, send };
}

function digitalInput(board, port, drivers, level, withTrigger = true) {
  const direct = sender(() => null);
  const trigger = {
    level: h('select', { class: 'select', 'aria-label': `Level ${port} goes to` }, h('option', {}, 'high'), h('option', {}, 'low')),
    steps: h('input', { class: 'input number', type: 'number', min: 1, value: 2000, 'aria-label': 'Steps' }),
    driver: h('select', { class: 'select', 'aria-label': 'Driver' }, Array.from({ length: drivers }, (_, d) => h('option', { value: d }, `${board}.${d}`)))
  };
  const armed = sender(() => ({ drive: { board, port, level: trigger.level.value, afterSteps: { driver: Number(trigger.driver.value), steps: Number(trigger.steps.value) } } }));
  for (const control of Object.values(trigger)) control.addEventListener('input', armed.update);
  armed.update();
  const drive = level => () => direct.send(level === 'released' ? { release: { board, port } } : { drive: { board, port, level } });
  return h('div', { class: 'port' },
    h('div', { class: 'port-head' }, h('span', { class: 'name' }, port), h('span', { class: 'state' }, level)),
    h('div', { class: 'segmented', role: 'group', 'aria-label': `Drive ${port}` },
      h('button', { type: 'button', onclick: drive('low'), title: `drive: {board: ${board}, port: ${port}, level: low}` }, 'Low'),
      h('button', { type: 'button', onclick: drive('high'), title: `drive: {board: ${board}, port: ${port}, level: high}` }, 'High'),
      h('button', { type: 'button', onclick: drive('released'), title: `release: {board: ${board}, port: ${port}}` }, 'Released')),
    direct.error,
    withTrigger && drivers > 0 ? h('div', { class: 'trigger' },
      h('div', { class: 'port-row' }, 'Go', trigger.level, 'after', trigger.steps, 'steps of driver', trigger.driver,
        h('button', { class: 'button small', type: 'button', style: 'margin-left: auto', onclick: () => armed.send() }, 'Arm')),
      armed.yaml, armed.error) : null);
}

function analogInput(board, port, level) {
  let mode = 'thermistor';
  const value = h('input', { class: 'input number', type: 'number', step: 'any', value: 25, 'aria-label': `${port}'s value` });
  const unit = h('span', {}, '°C');
  const modeSelect = h('select', { class: 'select', 'aria-label': `${port} reads as` },
    h('option', { value: 'thermistor' }, 'Thermistor'), h('option', { value: 'analog' }, 'Fraction of reference'));
  const control = sender(() => mode === 'thermistor'
    ? { thermistor: { board, port, celsius: Number(value.value) } }
    : { analog: { board, port, value: Number(value.value) } });
  modeSelect.addEventListener('change', () => {
    mode = modeSelect.value;
    unit.textContent = mode === 'thermistor' ? '°C' : 'of 1';
    value.value = mode === 'thermistor' ? 25 : 0.5;
    control.update();
  });
  value.addEventListener('input', control.update);
  value.addEventListener('keydown', e => { if (e.key === 'Enter') control.send(); });
  control.update();
  return h('div', { class: 'port' },
    h('div', { class: 'port-head' }, h('span', { class: 'name' }, port), h('span', { class: 'state' }, level)),
    h('div', { class: 'port-row' }, modeSelect, value, unit,
      h('button', { class: 'button small', type: 'button', style: 'margin-left: auto', onclick: () => control.send() }, 'Set')),
    control.yaml, control.error);
}

function tachoInput(board, port, outputs, level) {
  const fan = port.replace(/\.tach$/, '');
  const frequency = h('input', { class: 'input number', type: 'number', min: 0, step: 'any', value: 100, 'aria-label': 'Frequency in Hz' });
  const follow = {
    fan: h('select', { class: 'select', 'aria-label': 'The fan output' }, outputs.map(o => h('option', { value: o.name, selected: o.name === fan }, o.name))),
    maxRpm: h('input', { class: 'input number', type: 'number', min: 0, value: 6000, 'aria-label': 'RPM at full duty' }),
    pulsesPerRev: h('input', { class: 'input number', type: 'number', min: 0.5, step: 'any', value: 2, 'aria-label': 'Pulses a revolution' })
  };
  const pulses = sender(() => ({ pulses: { board, port, frequency: Number(frequency.value) } }));
  const tacho = sender(() => {
    const step = { tacho: { board, port, fan: follow.fan.value } };
    if (Number(follow.maxRpm.value) !== 6000) step.tacho.maxRpm = Number(follow.maxRpm.value);
    if (Number(follow.pulsesPerRev.value) !== 2) step.tacho.pulsesPerRev = Number(follow.pulsesPerRev.value);
    return step;
  });
  frequency.addEventListener('input', pulses.update);
  for (const control of Object.values(follow)) control.addEventListener('input', tacho.update);
  pulses.update();
  tacho.update();
  return h('div', { class: 'port' },
    h('div', { class: 'port-head' }, h('span', { class: 'name' }, port), h('span', { class: 'state' }, level)),
    h('div', { class: 'port-row' }, 'Follow', follow.fan, 'at', follow.maxRpm, 'RPM full duty,', follow.pulsesPerRev, 'pulses a rev',
      h('button', { class: 'button small', type: 'button', style: 'margin-left: auto', onclick: () => tacho.send() }, 'Follow')),
    tacho.yaml, tacho.error,
    h('div', { class: 'port-row' }, 'Or pulse at', frequency, 'Hz',
      h('button', { class: 'button small', type: 'button', onclick: () => pulses.send() }, 'Pulse'),
      h('button', { class: 'button small', type: 'button', onclick: () => pulses.send({ pulses: { board, port, frequency: 0 } }) }, 'Stop')),
    pulses.yaml, pulses.error);
}
