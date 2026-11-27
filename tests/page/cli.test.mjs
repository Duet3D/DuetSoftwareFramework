// The emulation bench's page: steps written as YAML and as the commands that send them
import assert from 'node:assert/strict';
import { test } from 'node:test';

globalThis.location = { port: '8770' };
const { quote, stepCommand, stepYaml } = await import('../../src/DuetEmulator/wwwroot/js/views/cli.js');

test('a step is written on one line, as the server writes it', () => {
  assert.equal(stepYaml({ drive: { board: 1, port: 'io0.in', level: 'high', afterSteps: { driver: 0, steps: 2000 } } }),
    'drive: {board: 1, port: io0.in, level: high, afterSteps: {driver: 0, steps: 2000}}');
  assert.equal(stepYaml({ code: 'M574 X1 S1 P"1.io0.in"' }), 'code: M574 X1 S1 P"1.io0.in"');
});

test('text YAML would read as something else is quoted', () => {
  assert.equal(stepYaml({ note: 'on' }), 'note: "on"');
  assert.equal(stepYaml({ note: '12' }), 'note: "12"');
  assert.equal(stepYaml({ note: '- a list?' }), 'note: "- a list?"');
  assert.equal(stepYaml({ code: 'G1 X10 # comment' }), 'code: "G1 X10 # comment"');
  assert.equal(stepYaml({ wait: '500ms' }), 'wait: 500ms');
});

test('a step is sent by the CLI command that does the same', () => {
  assert.equal(stepCommand({ code: 'G28' }, ''), 'scripts/emulate.py send G28');
  assert.equal(stepCommand({ code: 'M409 K"move"', timeout: '20s' }, ''), `scripts/emulate.py send 'M409 K"move"' --timeout 20s`);
  assert.equal(stepCommand({ drive: { board: 1, port: 'io0.in', level: 'high' } }, ''), 'scripts/emulate.py drive 1 io0.in high');
  const trigger = { drive: { board: 1, port: 'io0.in', level: 'high', afterSteps: { driver: 0, steps: 2000 } } };
  assert.equal(stepCommand(trigger, stepYaml(trigger)), `scripts/emulate.py step 'drive: {board: 1, port: io0.in, level: high, afterSteps: {driver: 0, steps: 2000}}'`);
});

test('words are quoted for the shell only when they need it', () => {
  assert.equal(quote('io0.in'), 'io0.in');
  assert.equal(quote("it's"), `'it'\\''s'`);
});
