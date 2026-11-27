// The emulation bench's page: the helpers its views use to drop replies and calls they no longer want
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { debounce, latestRequest } from '../../src/DuetEmulator/wwwroot/js/dom.js';

test('only the newest of overlapping requests is current', () => {
  const requests = latestRequest();
  const first = requests.start();
  const second = requests.start();
  assert.equal(requests.isCurrent(first), false, 'a request a newer one superseded');
  assert.equal(requests.isCurrent(second), true);
});

test('cancelling supersedes every request so far, and later ones count again', () => {
  const requests = latestRequest();
  const before = requests.start();
  requests.cancel();
  assert.equal(requests.isCurrent(before), false);
  assert.equal(requests.isCurrent(requests.start()), true);
});

test('a debounced call runs once, after the last time it was asked for', async () => {
  let calls = 0;
  const debounced = debounce(() => calls++, 10);
  debounced();
  debounced();
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(calls, 1);
});

test('a cancelled debounced call does not run', async () => {
  let calls = 0;
  const debounced = debounce(() => calls++, 10);
  debounced();
  debounced.cancel();
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(calls, 0);
});
