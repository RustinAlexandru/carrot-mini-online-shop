import test from 'node:test';
import assert from 'node:assert/strict';
import { createStore } from '../../src/Shop.Api/wwwroot/js/store.js';

test('selection replaces quantity and exposes a detached snapshot', () => {
  const store = createStore();
  store.select('coffee', 2);
  store.select('coffee', 3);
  const snapshot = store.items;
  snapshot[0].quantity = 99;
  assert.deepEqual(store.items, [{ productId: 'coffee', quantity: 3 }]);
});
test('invalid quantities leave selection unchanged', () => {
  const store = createStore();
  store.select('tea', 1);
  for (const quantity of [0, -1, 1.5, 10001, NaN]) {
    assert.throws(() => store.select('tea', quantity), RangeError);
  }
  assert.deepEqual(store.items, [{ productId: 'tea', quantity: 1 }]);
});
test('remove and clear reset selected lines', () => {
  const store = createStore();
  store.select('tea', 1);
  store.select('coffee', 2);
  store.remove('tea');
  assert.deepEqual(store.items, [{ productId: 'coffee', quantity: 2 }]);
  store.clear();
  assert.deepEqual(store.items, []);
});
