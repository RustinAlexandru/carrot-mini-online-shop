import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, copyFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { createStore } from '../../src/Shop.Api/wwwroot/js/store.js';
import { createApi } from '../../src/Shop.Api/wwwroot/js/api.js';
import { describeErrors, formatMoney } from '../../src/Shop.Api/wwwroot/js/format.js';

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

// ---- transport (the real api.js with a fake fetch) and the store flows built on it ----

const settle = () => new Promise((resolve) => setImmediate(resolve)); // lets pending store promises finish; not a timer

const reply = (status, body) => ({
  status,
  ok: status >= 200 && status < 300,
  text: async () => (body === undefined ? '' : typeof body === 'string' ? body : JSON.stringify(body))
});

function deferred() {
  let resolve;
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
}

/** routes: { 'METHOD /path': response | (call) => response | Promise<response> } */
function fakeFetch(routes) {
  const calls = [];
  const fetchImpl = async (url, init = {}) => {
    const call = { url, method: init.method, headers: init.headers ?? {}, body: init.body === undefined ? undefined : JSON.parse(init.body) };
    calls.push(call);
    const route = routes[`${init.method} ${new URL(url, 'http://shop.test').pathname}`];
    if (route === undefined) throw new Error(`unexpected request ${init.method} ${url}`);
    return typeof route === 'function' ? route(call) : route;
  };
  fetchImpl.calls = calls;
  return fetchImpl;
}

const ORDER = {
  id: 'order-1', status: 'Draft', createdAt: '2026-03-01T12:00:00Z', updatedAt: '2026-03-01T12:00:00Z',
  items: [{ productId: 'p1', sku: 'COF-1', name: 'Coffee', unitPrice: 14.5, quantity: 2, lineTotal: 29 }],
  coupon: { code: 'SAVE5', amount: 5 }, subtotal: 29, discount: 5, total: 24, currency: 'USD'
};
const PAGE = (page, names) => ({
  items: names.map((name, i) => ({ id: `${name}-${i}`, sku: name.toUpperCase(), name, price: 1, stockQuantity: 5 })),
  page, pageSize: 6, totalCount: 12, totalPages: 2
});

async function loggedIn(routes, email = 'demo@shop.test') {
  const fetchImpl = fakeFetch({ 'POST /auth/login': reply(200, { token: 'tok-A', expiresAt: '2030-01-01T00:00:00Z' }), ...routes });
  const store = createStore({ api: createApi(fetchImpl) });
  await store.login(email, 'pw');
  return { store, fetchImpl };
}

test('api: login sends a JSON body without credentials in the header and returns the DTO', async () => {
  const fetchImpl = fakeFetch({ 'POST /auth/login': reply(200, { token: 't', expiresAt: 'x' }) });
  const result = await createApi(fetchImpl).login('a@b.c', 'pw');
  assert.deepEqual(result, { ok: true, status: 200, data: { token: 't', expiresAt: 'x' } });
  const [call] = fetchImpl.calls;
  assert.deepEqual(call.body, { email: 'a@b.c', password: 'pw' });
  assert.equal(call.headers['Content-Type'], 'application/json');
  assert.equal(call.headers.Authorization, undefined);
});

test('api: authenticated calls send the bearer token and the right verb and path', async () => {
  const fetchImpl = fakeFetch({
    'POST /orders': reply(201, ORDER), 'GET /orders/o%2F1': reply(200, ORDER), 'DELETE /orders/o%2F1': reply(204)
  });
  const api = createApi(fetchImpl);
  await api.createOrder('tok', { items: [] });
  await api.getOrder('tok', 'o/1');
  await api.deleteOrder('tok', 'o/1');
  assert.deepEqual(fetchImpl.calls.map((c) => `${c.method} ${c.url}`), ['POST /orders', 'GET /orders/o%2F1', 'DELETE /orders/o%2F1']);
  for (const call of fetchImpl.calls) assert.equal(call.headers.Authorization, 'Bearer tok');
});

test('api: the catalog request is anonymous and carries paging', async () => {
  const fetchImpl = fakeFetch({ 'GET /products': reply(200, PAGE(2, ['a'])) });
  await createApi(fetchImpl).listProducts({ page: 2, pageSize: 6 });
  assert.equal(fetchImpl.calls[0].url, '/products?page=2&pageSize=6');
  assert.equal(fetchImpl.calls[0].headers.Authorization, undefined);
});

test('api: 204 succeeds without reading or parsing a body', async () => {
  const noBody = { status: 204, ok: true, text: async () => { throw new Error('a 204 body must not be read'); } };
  const result = await createApi(fakeFetch({ 'DELETE /orders/o1': noBody })).deleteOrder('tok', 'o1');
  assert.deepEqual(result, { ok: true, status: 204, data: null });
});

test('api: problem details become a structured error with field errors', async () => {
  const body = { title: 'One or more validation errors occurred.', status: 400, detail: 'bad', errors: { 'items[0].quantity': ['Only 3 in stock.'] } };
  const result = await createApi(fakeFetch({ 'POST /orders': reply(400, body) })).createOrder('t', {});
  assert.deepEqual(result, {
    ok: false, status: 400,
    error: { title: body.title, detail: 'bad', errors: { 'items[0].quantity': ['Only 3 in stock.'] } }
  });
});

test('api: empty, non-JSON and malformed error bodies are tolerated', async () => {
  const api = (response) => createApi(fakeFetch({ 'GET /orders/o1': response })).getOrder('t', 'o1');
  assert.deepEqual((await api(reply(500))).error, { title: 'Request failed (500)', detail: '', errors: {} });
  assert.deepEqual((await api(reply(502, '<html>Bad gateway</html>'))).error, { title: 'Request failed (502)', detail: '', errors: {} });
  assert.deepEqual((await api(reply(404, '{"title":42,"errors":[1]}'))).error, { title: 'Request failed (404)', detail: '', errors: {} });
  assert.equal((await api(reply(200, 'not json'))).data, null); // a success with an unreadable body is still a success
});

test('api: a network failure is a structured status-0 error, not an exception', async () => {
  const result = await createApi(async () => { throw new TypeError('Failed to fetch'); }).listProducts();
  assert.deepEqual(result, { ok: false, status: 0, error: { title: 'Network error', detail: 'Failed to fetch', errors: {} } });
});

test('api: a 401 is only reported; transport never changes any state', async () => {
  const result = await createApi(fakeFetch({ 'GET /orders/o1': reply(401) })).getOrder('t', 'o1');
  assert.equal(result.ok, false);
  assert.equal(result.status, 401);
});

test('store: login holds the token in memory and a bad login reports without logging in', async () => {
  const bad = createStore({ api: createApi(fakeFetch({ 'POST /auth/login': reply(401, { title: 'Unauthorized', detail: 'Invalid email or password.' }) })) });
  await bad.login('demo@shop.test', 'nope');
  assert.equal(bad.isLoggedIn, false);
  assert.deepEqual(bad.getState().login, { status: 'error', error: 'Invalid email or password.' });

  const { store } = await loggedIn({});
  assert.equal(store.isLoggedIn, true);
  assert.equal(store.getState().email, 'demo@shop.test');
  assert.equal(JSON.stringify(store.getState()).includes('tok-A'), false); // the token is never part of observable state
});

test('store: create posts the selected lines and coupon, then shows the order fetched by GET', async () => {
  const stored = { ...ORDER, status: 'Draft', total: 24 };
  const { store, fetchImpl } = await loggedIn({
    'POST /orders': reply(201, { ...ORDER, total: 999 }), // the create response is not what is displayed
    'GET /orders/order-1': reply(200, stored)
  });
  store.select('p1', 2);
  store.setCoupon('  SAVE5 ');
  await store.createOrder();

  const [, post, get] = fetchImpl.calls;
  assert.deepEqual(post.body, { items: [{ productId: 'p1', quantity: 2 }], couponCode: 'SAVE5' });
  assert.equal(post.headers.Authorization, 'Bearer tok-A');
  assert.equal(`${get.method} ${get.url}`, 'GET /orders/order-1');
  const { order, selected, couponCode, notice } = store.getState();
  assert.deepEqual(order, { status: 'loaded', data: stored, error: null });
  assert.deepEqual([selected, couponCode], [[], '']);
  assert.deepEqual(notice, { kind: 'success', text: 'Order created.' });
});

test('store: an empty coupon is sent as null', async () => {
  const { store, fetchImpl } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(200, ORDER) });
  store.select('p1', 1);
  await store.createOrder();
  assert.equal(fetchImpl.calls[1].body.couponCode, null);
});

test('store: create needs a login and at least one line, and sends nothing otherwise', async () => {
  const fetchImpl = fakeFetch({});
  const store = createStore({ api: createApi(fetchImpl) });
  store.select('p1', 1);
  await store.createOrder();
  assert.equal(store.getState().order.error.title, 'Log in to place an order.');

  const { store: loggedInStore, fetchImpl: second } = await loggedIn({});
  await loggedInStore.createOrder();
  assert.equal(loggedInStore.getState().order.error.title, 'Add at least one product first.');
  assert.equal(fetchImpl.calls.length + second.calls.length - 1, 0); // only the login request was ever sent
});

test('store: a second submit while one is in flight is ignored', async () => {
  const gate = deferred();
  const { store, fetchImpl } = await loggedIn({ 'POST /orders': () => gate.promise, 'GET /orders/order-1': reply(200, ORDER) });
  store.select('p1', 1);
  const first = store.createOrder();
  assert.equal(store.getState().order.status, 'saving');
  await store.createOrder();
  gate.resolve(reply(201, ORDER));
  await first;
  assert.equal(fetchImpl.calls.filter((c) => c.method === 'POST' && c.url === '/orders').length, 1);
});

/** Everything the draft could be changed with while a submit is in flight. */
function tryToEditDraft(store) {
  store.select('p2', 3);
  store.select('p1', 2);
  store.remove('p1');
  store.setCoupon('SAVE10');
  store.clear();
}

test('store: the draft is frozen while the POST is in flight, so nothing accepted can be erased', async () => {
  const post = deferred();
  const { store, fetchImpl } = await loggedIn({ 'POST /orders': () => post.promise, 'GET /orders/order-1': reply(200, ORDER) });
  store.select('p1', 1);
  store.setCoupon('SAVE5');
  const pending = store.createOrder();
  assert.equal(store.getState().order.status, 'saving');

  tryToEditDraft(store);

  assert.deepEqual(store.getState().selected.map((l) => [l.productId, l.quantity]), [['p1', 1]]);
  assert.equal(store.getState().couponCode, 'SAVE5');
  post.resolve(reply(201, ORDER));
  await pending;
  const posted = fetchImpl.calls.find((c) => c.method === 'POST' && c.url === '/orders').body;
  assert.deepEqual(posted, { items: [{ productId: 'p1', quantity: 1 }], couponCode: 'SAVE5' });
  assert.deepEqual([store.getState().selected, store.getState().order.status], [[], 'loaded']);
  store.select('p2', 3); // editing works again once the order is shown
  assert.deepEqual(store.getState().selected.map((l) => l.productId), ['p2']);
});

test('store: the draft stays frozen during the follow-up GET too', async () => {
  const detail = deferred();
  const { store } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': () => detail.promise });
  store.select('p1', 1);
  const pending = store.createOrder();
  await settle(); // the POST has completed and the GET is now pending
  assert.equal(store.getState().order.status, 'saving');

  tryToEditDraft(store);

  assert.deepEqual(store.getState().selected.map((l) => [l.productId, l.quantity]), [['p1', 1]]);
  detail.resolve(reply(200, ORDER));
  await pending;
  assert.deepEqual([store.getState().selected, store.getState().order.data?.id], [[], 'order-1']);
});

test('store: when the detail GET fails only lines that were in the created order are cleared', async () => {
  const detail = deferred();
  const { store } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': () => detail.promise });
  store.select('p1', 1);
  const pending = store.createOrder();
  await settle();
  store.select('p2', 3); // refused, so it can never be accepted and then lost
  assert.deepEqual(store.getState().selected.map((l) => l.productId), ['p1']);
  detail.resolve(reply(500, { title: 'Server error' }));
  await pending;
  const { order, selected } = store.getState();
  assert.deepEqual([order.data, order.error.source, selected], [null, 'create', []]);
  assert.match(order.error.title, /Order order-1 was created but could not be loaded/);
  assert.equal(store.items.some((i) => i.productId === 'p2'), false);
});

test('store: a failed create unlocks the draft with the lines untouched', async () => {
  const { store } = await loggedIn({ 'POST /orders': reply(400, { title: 'bad', errors: {} }) });
  store.select('p1', 1);
  await store.createOrder();
  store.select('p2', 2);
  assert.deepEqual(store.getState().selected.map((l) => [l.productId, l.quantity]), [['p1', 1], ['p2', 2]]);
});

test('store: a validation error keeps the selection and exposes the field errors', async () => {
  const errors = { 'items[0].quantity': ['Only 3 in stock.'] };
  const { store } = await loggedIn({ 'POST /orders': reply(400, { title: 'One or more validation errors occurred.', errors }) });
  store.select('p1', 9);
  await store.createOrder();
  const { order, selected } = store.getState();
  assert.equal(order.status, 'idle');
  assert.deepEqual(order.error.errors, errors);
  assert.deepEqual(selected.map((l) => [l.productId, l.quantity]), [['p1', 9]]);
});

test('store: a 401 in the current session clears token, draft and current order', async () => {
  const { store } = await loggedIn({ 'POST /orders': reply(401) });
  store.select('p1', 1);
  store.setCoupon('SAVE5');
  await store.createOrder();
  const state = store.getState();
  assert.equal(store.isLoggedIn, false);
  assert.deepEqual([state.loggedIn, state.selected, state.couponCode, state.order], [false, [], '', { status: 'idle', data: null, error: null }]);
  assert.deepEqual(state.notice, { kind: 'error', text: 'Your session has expired. Please log in again.' });
});

test('store: a 401 from the detail fetch after a successful create also ends the session', async () => {
  const { store } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(401) });
  store.select('p1', 1);
  await store.createOrder();
  assert.equal(store.isLoggedIn, false);
  assert.equal(store.getState().order.data, null);
});

test('store: a 401 that belongs to an older session is ignored by the newer one', async () => {
  const gate = deferred();
  let logins = 0;
  const fetchImpl = fakeFetch({
    'POST /auth/login': () => reply(200, { token: `tok-${++logins}` }),
    'POST /orders': () => gate.promise
  });
  const store = createStore({ api: createApi(fetchImpl) });
  await store.login('a@b.c', 'pw');
  store.select('p1', 1);
  const pending = store.createOrder(); // session 1
  store.logout();
  await store.login('a@b.c', 'pw'); // session 2
  store.select('p2', 3);

  gate.resolve(reply(401)); // the old request fails after the new session began
  await pending;

  assert.equal(store.isLoggedIn, true);
  assert.deepEqual(store.getState().selected.map((l) => l.productId), ['p2']);
  assert.equal(store.getState().notice?.kind === 'error', false);
});

test('store: a success from an older session is not committed after logout', async () => {
  const gate = deferred();
  const { store, fetchImpl } = await loggedIn({ 'POST /orders': () => gate.promise, 'GET /orders/order-1': reply(200, ORDER) });
  store.select('p1', 1);
  const pending = store.createOrder();
  store.logout();
  gate.resolve(reply(201, ORDER));
  await pending;
  assert.equal(fetchImpl.calls.some((c) => c.method === 'GET'), false); // no follow-up GET for a dead session
  assert.deepEqual(store.getState().order, { status: 'idle', data: null, error: null });
  assert.equal(store.isLoggedIn, false);
});

test('store: the newest requested product page wins whichever response arrives last', async () => {
  for (const order of [['second', 'first'], ['first', 'second']]) {
    const gates = { 1: deferred(), 2: deferred() };
    const fetchImpl = fakeFetch({ 'GET /products': (call) => gates[new URL(call.url, 'http://x').searchParams.get('page')].promise });
    const store = createStore({ api: createApi(fetchImpl) });
    const first = store.loadProducts(1);
    const second = store.loadProducts(2);
    const settle = { first: () => gates[1].resolve(reply(200, PAGE(1, ['old']))), second: () => gates[2].resolve(reply(200, PAGE(2, ['new']))) };
    for (const which of order) { settle[which](); await Promise.resolve(); }
    await Promise.all([first, second]);
    const { products } = store.getState();
    assert.deepEqual([products.page, products.items.map((p) => p.name), products.status], [2, ['new'], 'ready'], order.join(' then '));
  }
});

test('store: catalog loading, empty and error states, and a response from before a login is dropped', async () => {
  const empty = createStore({ api: createApi(fakeFetch({ 'GET /products': reply(200, { items: [], page: 1, pageSize: 6, totalCount: 0, totalPages: 0 }) })) });
  await empty.loadProducts(1);
  assert.equal(empty.getState().products.status, 'empty');

  const failing = createStore({ api: createApi(fakeFetch({ 'GET /products': reply(500, { title: 'Server error', detail: 'db down' }) })) });
  await failing.loadProducts(1);
  assert.deepEqual([failing.getState().products.status, failing.getState().products.error], ['error', 'db down']);

  const gate = deferred();
  const store = createStore({ api: createApi(fakeFetch({ 'GET /products': () => gate.promise, 'POST /auth/login': reply(200, { token: 't' }) })) });
  const loading = store.loadProducts(1);
  assert.equal(store.getState().products.status, 'loading');
  await store.login('a@b.c', 'pw');
  gate.resolve(reply(200, PAGE(1, ['stale'])));
  await loading;
  assert.notEqual(store.getState().products.items[0]?.name, 'stale');
});

test('store: selected lines carry the names of loaded products and survive paging', async () => {
  const fetchImpl = fakeFetch({ 'GET /products': (call) => reply(200, PAGE(Number(new URL(call.url, 'http://x').searchParams.get('page')), call.url.includes('page=1') ? ['coffee'] : ['tea'])) });
  const store = createStore({ api: createApi(fetchImpl) });
  await store.loadProducts(1);
  store.select('coffee-0', 2);
  await store.loadProducts(2);
  assert.deepEqual(store.getState().selected, [{ productId: 'coffee-0', quantity: 2, name: 'coffee', sku: 'COFFEE' }]);
});

test('store: delete clears the order on 204, treats 404 as already gone and keeps it on other errors', async () => {
  const outcomes = [[reply(204), null, 'Order deleted.'], [reply(404, { title: 'Not Found' }), null, 'The order was already gone.'], [reply(500, { title: 'Server error' }), ORDER, undefined]];
  for (const [response, expectedOrder, text] of outcomes) {
    const { store, fetchImpl } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(200, ORDER), 'DELETE /orders/order-1': response });
    store.select('p1', 1);
    await store.createOrder();
    await store.deleteOrder();
    const { order, notice } = store.getState();
    assert.deepEqual(order.data, expectedOrder);
    if (text) assert.equal(notice.text, text);
    else assert.equal(order.error.title, 'Server error');
    assert.equal(fetchImpl.calls.at(-1).headers.Authorization, 'Bearer tok-A');
  }
});

test('store: delete in a dead session or a 401 on delete does not resurrect or leave the session', async () => {
  const gate = deferred();
  const { store } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(200, ORDER), 'DELETE /orders/order-1': () => gate.promise });
  store.select('p1', 1);
  await store.createOrder();
  const pending = store.deleteOrder();
  store.logout();
  gate.resolve(reply(204));
  await pending;
  assert.equal(store.getState().notice.text, 'Logged out.'); // the stale delete result committed nothing

  const second = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(200, ORDER), 'DELETE /orders/order-1': reply(401) });
  second.store.select('p1', 1);
  await second.store.createOrder();
  await second.store.deleteOrder();
  assert.equal(second.store.isLoggedIn, false);
});

test('store: logout drops token, selection, coupon and order; subscribers hear changes until they unsubscribe', async () => {
  const { store } = await loggedIn({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(200, ORDER) });
  const heard = [];
  const unsubscribe = store.subscribe((state) => heard.push(state.loggedIn));
  store.select('p1', 1);
  store.setCoupon('SAVE5');
  store.logout();
  assert.equal(store.isLoggedIn, false);
  assert.deepEqual([store.getState().selected, store.getState().couponCode], [[], '']);
  assert.ok(heard.length >= 3);
  unsubscribe();
  const count = heard.length;
  store.select('p1', 1);
  assert.equal(heard.length, count);
});

test('format: server errors are described with line names and money is only formatted', () => {
  const lines = [{ name: 'Coffee' }, { name: 'Tea' }];
  assert.deepEqual(
    describeErrors({ 'items[1].quantity': ['Only 3 in stock.'], couponCode: ['Unknown coupon code.'], 'items[7].quantity': ['x'] }, lines),
    ['Tea: Only 3 in stock.', 'couponCode: Unknown coupon code.', 'items[7].quantity: x']);
  assert.deepEqual(describeErrors(undefined), []);
  assert.equal(formatMoney(24), '$24.00');
  assert.equal(formatMoney('1234.5'), '$1,234.50');
});

// ---- a small dependency-free DOM host, so the real components run inside the Node gate ----
// It implements only what the components use: elements with attributes/dataset/properties, tree mutation with
// connectedCallback/disconnectedCallback, a tiny selector matcher, bubbling event dispatch and customElements.

class TextNode {
  constructor(data) { this.data = String(data); this.parentNode = null; }
  get textContent() { return this.data; }
}

class Element {
  constructor() {
    this.attributes = new Map();
    this.childNodes = [];
    this.parentNode = null;
    this.listeners = new Map();
  }
  removeAttribute(name) { this.attributes.delete(name); }
  get hidden() { return this.hasAttribute('hidden'); }
  set hidden(on) { if (on) this.setAttribute('hidden', ''); else this.removeAttribute('hidden'); }
  get disabled() { return this.hasAttribute('disabled'); }
  set disabled(on) { if (on) this.setAttribute('disabled', ''); else this.removeAttribute('disabled'); }
  get tagName() { return this.localName.toUpperCase(); }
  get isConnected() { return this === document.body || (this.parentNode?.isConnected ?? false); }
  get children() { return this.childNodes.filter((n) => n instanceof Element); }
  setAttribute(name, value) { this.attributes.set(name, String(value)); if (name === 'id') this.id = String(value); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  hasAttribute(name) { return this.attributes.has(name); }
  set className(value) { this.attributes.set('class', value); }
  get className() { return this.attributes.get('class') ?? ''; }
  get dataset() {
    const data = {};
    for (const [name, value] of this.attributes) {
      if (name.startsWith('data-')) data[name.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = value;
    }
    return data;
  }
  get textContent() { return this.childNodes.map((n) => n.textContent).join(''); }
  set textContent(value) { this.replaceChildren(String(value)); }
  append(...nodes) { for (const node of nodes) this.#adopt(typeof node === 'string' ? new TextNode(node) : node); }
  replaceChildren(...nodes) {
    for (const old of [...this.childNodes]) this.#drop(old);
    this.append(...nodes);
  }
  remove() { this.parentNode?.removeChild(this); }
  removeChild(node) { this.#drop(node); }
  #adopt(node) {
    node.parentNode?.removeChild(node);
    node.parentNode = this;
    this.childNodes.push(node);
    if (this.isConnected && node instanceof Element) node.#connect();
  }
  #drop(node) {
    this.childNodes.splice(this.childNodes.indexOf(node), 1);
    node.parentNode = null;
    if (node instanceof Element) node.#disconnect();
  }
  #connect() {
    this.connectedCallback?.();
    for (const child of [...this.children]) child.#connect();
  }
  #disconnect() {
    for (const child of [...this.children]) child.#disconnect();
    this.disconnectedCallback?.();
  }
  matches(selector) {
    const m = /^([a-z0-9-]+)?(?:\.([\w-]+))?(?:\[([\w-]+)(?:="((?:[^"\\]|\\.)*)")?\])?$/.exec(selector);
    if (!m) throw new Error(`unsupported selector ${selector}`);
    const [, tag, cls, attribute, value] = m;
    if (tag && this.localName !== tag) return false;
    if (cls && !this.className.split(' ').includes(cls)) return false;
    if (attribute && !this.hasAttribute(attribute)) return false;
    return value === undefined || this.getAttribute(attribute) === value.replace(/\\(.)/g, '$1');
  }
  closest(selector) { for (let node = this; node instanceof Element; node = node.parentNode) if (node.matches(selector)) return node; return null; }
  querySelectorAll(selector) {
    const found = [];
    const walk = (node) => { for (const child of node.children) { if (child.matches(selector)) found.push(child); walk(child); } };
    walk(this);
    return found;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  addEventListener(type, handler) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type).add(handler);
  }
  removeEventListener(type, handler) { this.listeners.get(type)?.delete(handler); }
  listenerCount(type) { return this.listeners.get(type)?.size ?? 0; }
  /** Bubbling dispatch along the parent chain; the event keeps the flags it was created with. */
  dispatchEvent(event) {
    const path = [];
    for (let node = this; node instanceof Element; node = node.parentNode) { path.push(node); if (!event.bubbles) break; }
    let prevented = false;
    for (const node of path) {
      const view = { type: event.type, detail: event.detail, bubbles: event.bubbles, composed: event.composed, target: this, currentTarget: node, preventDefault() { prevented = true; } };
      for (const handler of [...(node.listeners.get(event.type) ?? [])]) handler(view);
    }
    return !prevented;
  }
  click() { return this.dispatchEvent(new Event('click', { bubbles: true, composed: true })); }
}

class HTMLElement extends Element {}
class HTMLInputElement extends HTMLElement {
  #value = null;
  get value() { return this.#value ?? this.getAttribute('value') ?? ''; }
  set value(v) { this.#value = String(v); }
  reportValidity() { return true; }
}
class HTMLButtonElement extends HTMLElement {}

const registry = new Map();
const classFor = { input: HTMLInputElement, button: HTMLButtonElement };
globalThis.Element = Element;
globalThis.HTMLElement = HTMLElement;
globalThis.HTMLInputElement = HTMLInputElement;
globalThis.CSS = { escape: (text) => String(text).replace(/["\\]/g, '\\$&') };
globalThis.customElements = { define: (name, constructor) => registry.set(name, constructor) };
globalThis.window = { fetch: () => { throw new Error('the DOM host never reaches the real network'); } };
globalThis.document = {
  createElement(tag) {
    const element = new (registry.get(tag) ?? classFor[tag] ?? HTMLElement)();
    element.localName = tag;
    return element;
  },
  body: null
};
document.body = document.createElement('body');

await import('../../src/Shop.Api/wwwroot/js/app.js'); // registers the five real components
const { ShopApp } = await import('../../src/Shop.Api/wwwroot/js/components/shop-app.js');

const buttonByText = (root, text) => root.querySelectorAll('button').find((b) => b.textContent === text);

const PRODUCTS = PAGE(1, ['coffee', 'tea']);
const mountRoutes = (extra = {}) => ({
  'POST /auth/login': reply(200, { token: 'tok-A', expiresAt: '2030-01-01T00:00:00Z' }),
  'GET /products': reply(200, PRODUCTS),
  ...extra
});

async function mountApp(extra) {
  const fetchImpl = fakeFetch(mountRoutes(extra));
  const store = createStore({ api: createApi(fetchImpl) });
  const app = document.createElement('shop-app');
  app.store = store;
  document.body.append(app);
  await settle();
  return { app, store, fetchImpl };
}

async function logIn(app) {
  const form = app.querySelector('login-form').querySelector('form');
  const [email, password] = form.querySelectorAll('input');
  email.value = 'demo@shop.test';
  password.value = 'pw';
  form.dispatchEvent(new Event('submit', { bubbles: true, composed: true }));
  await settle();
}

test('component: Log out and Dismiss clicks reach the store (empty data-* flags are tested by presence)', async () => {
  const { app, store } = await mountApp({ 'POST /orders': reply(201, ORDER), 'GET /orders/order-1': reply(200, ORDER) });
  await logIn(app);
  assert.equal(store.isLoggedIn, true);
  assert.equal(store.getState().notice?.text, 'Logged in.');

  buttonByText(app, '×').click(); // Dismiss
  assert.equal(store.getState().notice, null);
  assert.equal(app.querySelectorAll('.notice')[0].children.length, 0);

  // a draft and a current order exist before logging out
  app.querySelector('product-list').querySelectorAll('button').find((b) => b.hasAttribute('data-add')).click();
  app.querySelector('order-editor').querySelector('form').dispatchEvent(new Event('submit', { bubbles: true, composed: true }));
  await settle();
  assert.equal(store.getState().order.data.id, 'order-1');
  app.querySelector('product-list').querySelectorAll('button').find((b) => b.hasAttribute('data-add')).click();
  assert.equal(store.getState().selected.length, 1);

  buttonByText(app, 'Log out').click();
  const state = store.getState();
  assert.equal(store.isLoggedIn, false);
  assert.deepEqual([state.selected, state.order.data, state.loggedIn], [[], null, false]);
  assert.equal(app.querySelector('header').textContent.includes('Not logged in'), true);
});

test('component: while an order is saving the catalog cannot change the draft', async () => {
  const post = deferred();
  const { app, store } = await mountApp({ 'POST /orders': () => post.promise, 'GET /orders/order-1': reply(200, ORDER) });
  await logIn(app);
  const list = app.querySelector('product-list');
  const addButtons = () => list.querySelectorAll('button').filter((b) => b.hasAttribute('data-add'));
  const quantityInputs = () => list.querySelectorAll('input');
  addButtons()[0].click();
  assert.equal(addButtons()[0].disabled, false);

  app.querySelector('order-editor').querySelector('form').dispatchEvent(new Event('submit', { bubbles: true, composed: true }));
  assert.equal(store.getState().order.status, 'saving');
  assert.deepEqual([addButtons().every((b) => b.disabled), quantityInputs().every((i) => i.disabled)], [true, true]);
  assert.equal(app.querySelector('order-editor').querySelectorAll('input').every((i) => i.disabled), true);
  addButtons()[1].click(); // even if an event slipped through, the store refuses it
  assert.equal(store.getState().selected.length, 1);

  post.resolve(reply(201, ORDER));
  await settle();
  assert.deepEqual([addButtons().some((b) => b.disabled), store.getState().order.status], [false, 'loaded']);
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
