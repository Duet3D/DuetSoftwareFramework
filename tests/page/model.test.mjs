// The emulation bench's page: the object model tree with two stacks side by side
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { childKey, found, matches, modelQuery, parseKey, same, sameIgnoring, summary, touches, treeRows, valueAt } from '../../src/DuetEmulator/wwwroot/js/model.js';

const rrf = { move: { axes: [{ letter: 'X', machinePosition: 0, homed: true }], kinematics: { name: 'cartesian' } }, network: { name: 'rrf' } };
const dsf = { move: { axes: [{ letter: 'X', machinePosition: -10, homed: true }], kinematics: { name: 'cartesian' } }, network: { name: 'dsf' } };

test('keys are read as M409 keys', () => {
  assert.deepEqual(parseKey('move.axes[0].letter'), ['move', 'axes', 0, 'letter']);
  assert.deepEqual(parseKey('boards[1].drivers[2]'), ['boards', 1, 'drivers', 2]);
  assert.throws(() => parseKey('boards[x]'));
  assert.equal(childKey('', 'move'), 'move');
  assert.equal(childKey('move.axes', 0), 'move.axes[0]');
  assert.equal(valueAt(rrf, 'move.axes[0].letter'), 'X');
  assert.equal(valueAt(rrf, 'move.axes[3]'), undefined);
});

test('numbers are the same within the tolerance the comparison uses', () => {
  assert.equal(same(10, 9.9995), true);
  assert.equal(same(0, 0.01), false);
  assert.equal(same({ a: [1, 2] }, { a: [1, 2] }), true);
  assert.equal(same({ a: 1 }, { a: 1, b: 2 }), false);
});

test('ignored keys match as the comparison matches them, any index for [*]', () => {
  assert.equal(matches('inputs[3].lineNumber', ['inputs[*].lineNumber']), true);
  assert.equal(matches('boards[0].firmwareVersion', ['boards[0]']), true);
  assert.equal(matches('boards[1]', ['boards[0]']), false);
  assert.equal(matches('networkInterfaces', ['network']), false, 'a pattern is a whole key, not a prefix of a name');
  assert.equal(sameIgnoring(rrf, dsf, '', ['network', 'move.axes[*].machinePosition']), true);
  assert.equal(sameIgnoring(rrf, dsf, '', ['network']), false);
});

test('the tree shows each key once with each stack\'s value, and what differs under it', () => {
  const rows = treeRows({ rrf, dsf }, { expanded: new Set(['move', 'move.axes', 'move.axes[0]']), ignored: ['network'] });
  assert.deepEqual(rows.map(r => r.key), ['move', 'move.axes', 'move.axes[0]', 'move.axes[0].homed', 'move.axes[0].letter', 'move.axes[0].machinePosition',
    'move.kinematics', 'network']);
  const byKey = Object.fromEntries(rows.map(r => [r.key, r]));
  assert.equal(byKey.move.differs, true);
  assert.equal(byKey['move.kinematics'].differs, false);
  assert.equal(byKey['move.axes[0].machinePosition'].differs, true);
  assert.deepEqual(byKey['move.axes[0].machinePosition'].values, { rrf: 0, dsf: -10 });
  assert.equal(byKey.network.differs, false, 'an ignored key never differs');
  assert.equal(byKey.network.ignored, true);
  assert.equal(byKey['move.kinematics'].open, false);
});

test('the tree can start at a key', () => {
  const rows = treeRows({ rrf, dsf }, { root: 'move.axes[0]', expanded: new Set(['move.axes[0]']) });
  assert.deepEqual(rows.map(r => r.name), ['move.axes[0]', 'homed', 'letter', 'machinePosition']);
});

test('a key the last step changed is marked, as are those above and below it', () => {
  const changed = new Set(['move.axes[0].homed']);
  assert.equal(touches('move', changed), true);
  assert.equal(touches('move.axes[0].homed', changed), true);
  assert.equal(touches('move.axes[0].letter', changed), false);
  assert.equal(touches('move.axes[0].homed.x', changed), true);
});

test('containers are summarised by their size', () => {
  assert.equal(summary([1, 2]), '2 items');
  assert.equal(summary({ a: 1 }), '1 key');
  assert.equal(summary('X'), '"X"');
  assert.equal(summary(undefined), '');
});

test('with only differences, the keys that agree are left out and those that differ open', () => {
  const rows = treeRows({ rrf, dsf }, { onlyDifferences: true, ignored: ['network'] });
  assert.deepEqual(rows.map(r => r.key), ['move', 'move.axes', 'move.axes[0]', 'move.axes[0].machinePosition']);
});

test('a key closed by hand stays closed, even where the stacks differ', () => {
  const rows = treeRows({ rrf, dsf }, { onlyDifferences: true, ignored: ['network'], collapsed: new Set(['move.axes']) });
  assert.deepEqual(rows.map(r => [r.key, r.open]), [['move', true], ['move.axes', false]]);
});

test('a search finds values as well as names, and opens the way to them', () => {
  const rows = treeRows({ rrf, dsf }, { search: 'cartes' });
  assert.deepEqual(rows.map(r => [r.key, r.open]), [['move', true], ['move.kinematics', true], ['move.kinematics.name', false]]);
  assert.deepEqual(treeRows({ rrf, dsf }, { search: '-10' }).map(r => r.key),
                   ['move', 'move.axes', 'move.axes[0]', 'move.axes[0].machinePosition'], 'a value on one stack is enough');
  assert.deepEqual(treeRows({ rrf, dsf }, { search: 'KINEMATICS' }).map(r => [r.key, r.open]), [['move', true], ['move.kinematics', false]],
                   'a key found by its own name is shown closed');
  assert.deepEqual(treeRows({ rrf, dsf }, { search: 'nothing like it' }), []);
});

test('a search reads strings without their quotes, and a closed key stays closed', () => {
  assert.equal(found('X', 'letter', 'x'), true);
  assert.equal(found('X', 'letter', '"x"'), false);
  assert.equal(found({ a: { b: 285 } }, 'heat', '28'), true);
  const rows = treeRows({ rrf, dsf }, { search: 'cartes', collapsed: new Set(['move']) });
  assert.deepEqual(rows.map(r => [r.key, r.open]), [['move', false]]);
});

test('the one box shows a key that is in a model and searches for anything else', () => {
  assert.deepEqual(modelQuery({ rrf, dsf }, ' move.axes[0] '), { root: 'move.axes[0]', search: '' });
  assert.deepEqual(modelQuery({ rrf, dsf }, 'network.name'), { root: 'network.name', search: '' }, 'a key in either model');
  assert.deepEqual(modelQuery({ rrf, dsf }, 'move.axes[3]'), { root: '', search: 'move.axes[3]' }, 'a key in neither is searched for');
  assert.deepEqual(modelQuery({ rrf, dsf }, 'cartes'), { root: '', search: 'cartes' });
  assert.deepEqual(modelQuery({ rrf, dsf }, '[x'), { root: '', search: '[x' }, 'text that is not written as a key');
  assert.deepEqual(modelQuery({ rrf, dsf }, '  '), { root: '', search: '' });
});
