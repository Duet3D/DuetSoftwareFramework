// The emulation bench's page: splitting captures into steps and describing their events
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { applyCaptureChange, countEvents, describe, inputStates, splitSteps, stepAction } from '../../src/DuetEmulator/wwwroot/js/capture.js';

const events = [
  { kind: 'om-snapshot', t: 0, model: { move: {} } },
  { kind: 'config', t: 0.5 },
  { kind: 'code', t: 1, code: 'G91' },
  { kind: 'reply', t: 1.1, code: 'G91', severity: 'success', text: '' },
  { kind: 'input', t: 2, board: 1, port: 'io0.in', value: 'high after 2000 steps of driver 1.0' },
  { kind: 'start', t: 2.5 },
  { kind: 'wait', t: 3, duration: '500ms' },
  { kind: 'input-change', t: 3.2, board: 1, ports: ['io0.in'], value: 'high' },
  { kind: 'can', t: 3.3, source: 0, destination: 63, type: 'TimeSync', response: false, data: '00' },
  { kind: 'can', t: 3.4, source: 0, destination: 1, type: 'StopMovement', response: false, data: '01', message: { WhichDrives: 1 } },
  { kind: 'await', t: 4, code: 'G1 X10' },
  { kind: 'note', t: 5, text: 'done' }
];

test('every action starts a step, as the server splits captures', () => {
  const steps = splitSteps(events);
  assert.deepEqual(steps.map(s => s.action), ['start', 'config.g', 'G91', 'input 1.io0.in high after 2000 steps of driver 1.0', 'wait 500ms', 'await G1 X10', '# done']);
  assert.deepEqual(steps.map(s => s.number), [1, 2, 3, 4, 5, 6, 7]);
  assert.equal(steps[0].events.length, 1, 'the start holds what came before the first action');
  assert.equal(steps[2].events[0].kind, 'code', 'a step starts with the event that starts it');
  assert.equal(describe(events[1]), 'run config.g');
});

test('an event that is not an action starts no step', () => {
  assert.equal(stepAction({ kind: 'reply', code: 'G91' }), null);
  assert.equal(stepAction({ kind: 'input-change', ports: ['io0.in'] }), null);
});

test('counts leave out the periodic frames', () => {
  assert.deepEqual(countEvents(splitSteps(events)[4].events), { can: 1, om: 0, pin: 0 });
});

test('events are described as the capture reports describe them', () => {
  assert.equal(describe(events[3]), '< (empty)');
  assert.equal(describe({ kind: 'reply', severity: 'error', text: 'Bad' }), '< error: Bad');
  assert.equal(describe(events[8]), 'CAN 0->all TimeSync 00');
  assert.equal(describe(events[9]), 'CAN 0->1 StopMovement {"WhichDrives":1}');
  assert.equal(describe({ kind: 'pin', board: 1, ports: ['out3'], output: 'Pwm', duty: 0.5, frequency: 250 }), 'PIN 1.out3 Pwm 50.0% at 250Hz');
  assert.equal(describe({ kind: 'om', path: 'fans[0].rpm', old: 0, new: 3000 }), 'OM fans[0].rpm: 0 -> 3000');
});

test('an input is driven by what last drove it, or by what it last changed to by itself', () => {
  const states = inputStates(events);
  assert.deepEqual(states.get('1.io0.in'), { value: 'high', scripted: true, t: 3.2 });
});

test('a capture message replaces the events from its index on', () => {
  const list = [1, 2, 3];
  assert.equal(applyCaptureChange(list, 3, [4, 5]), true);
  assert.deepEqual(list, [1, 2, 3, 4, 5]);
  assert.equal(applyCaptureChange(list, 1, [9]), true);
  assert.deepEqual(list, [1, 9]);
});

test('a capture message past the end says events were missed, and changes nothing', () => {
  const list = [1, 2];
  assert.equal(applyCaptureChange(list, 3, [4]), false);
  assert.deepEqual(list, [1, 2]);
});

test('the server\'s periodic CAN types replace the page\'s own', async () => {
  const { PERIODIC_CAN_TYPES, setPeriodicCanTypes } = await import('../../src/DuetEmulator/wwwroot/js/capture.js');
  const before = [...PERIODIC_CAN_TYPES];
  assert.ok(PERIODIC_CAN_TYPES.has('HeatersStatusReport') && PERIODIC_CAN_TYPES.has('SensorTemperaturesReport'));
  setPeriodicCanTypes(['TimeSync']);
  assert.deepEqual([...PERIODIC_CAN_TYPES], ['TimeSync']);
  setPeriodicCanTypes([]);
  assert.deepEqual([...PERIODIC_CAN_TYPES], ['TimeSync'], 'an empty list changes nothing');
  setPeriodicCanTypes(before);
});
