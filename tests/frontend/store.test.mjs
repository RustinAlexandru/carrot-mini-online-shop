import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, copyFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
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

// Exercise the real gate's orchestration in an isolated directory with a fake Docker CLI.
// This keeps nested checks independent of the outer gate's lock and SQL service.
for (const failingSuite of ['backend', 'js']) {
  test(`gate runs both suites and preserves the ${failingSuite} failure`, () => {
    const root = mkdtempSync(join(tmpdir(), 'shop-gate-regression-'));
    try {
      mkdirSync(join(root, 'scripts'));
      mkdirSync(join(root, 'bin'));
      copyFileSync(new URL('../../scripts/test.sh', import.meta.url), join(root, 'scripts/test.sh'));
      writeFileSync(join(root, 'bin/docker'), `#!/bin/sh
case "$*" in
  *"run --build --rm api-tests"*)
    echo 'Backend suite executed'
    [ "$GATE_FAIL_SUITE" != backend ] || exit 37
    ;;
  *"run --rm ui-tests"*)
    echo 'JS suite executed'
    [ "$GATE_FAIL_SUITE" != js ] || exit 42
    ;;
esac
`, { mode: 0o755 });
      const result = spawnSync('sh', [join(root, 'scripts/test.sh')], {
        env: { ...process.env, PATH: `${join(root, 'bin')}:${process.env.PATH}`, TMPDIR: root, GATE_FAIL_SUITE: failingSuite },
        encoding: 'utf8', timeout: 10000
      });
      assert.equal(result.error, undefined);
      assert.equal(result.status, failingSuite === 'backend' ? 37 : 42, result.stderr);
      assert.match(result.stdout, /Backend suite executed/);
      assert.match(result.stdout, /JS suite executed/);
      assert.doesNotMatch(result.stdout, /Gate passed:/);
      assert.equal(existsSync(join(root, '.test-gate.lock')), false);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
}

test('a stale gate lock reports its path and safe recovery', () => {
  const root = mkdtempSync(join(tmpdir(), 'shop-gate-lock-'));
  try {
    mkdirSync(join(root, 'scripts'));
    mkdirSync(join(root, '.test-gate.lock'));
    copyFileSync(new URL('../../scripts/test.sh', import.meta.url), join(root, 'scripts/test.sh'));
    const result = spawnSync('sh', [join(root, 'scripts/test.sh')], {
      encoding: 'utf8', timeout: 10000
    });
    assert.equal(result.error, undefined);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /If no gate is running/);
    assert.match(result.stderr, /rmdir \.test-gate\.lock/);
    assert.equal(existsSync(join(root, '.test-gate.lock')), true);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
