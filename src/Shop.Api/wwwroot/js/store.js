// The central store. It owns the in-memory session (token), the product page, the selected lines, the current order
// and every async flow. The injected `api` is transport only; the store decides what a response means.
//
// Session rule: every async operation captures the session generation when it starts and re-checks it before
// committing a success OR a 401. Login and logout advance the generation, so a response that belongs to an older
// session can neither fill the new one with data nor log it out.

export const MIN_QUANTITY = 1;
// Usability mirror of the server's QuantityRules (1-10000); the API remains the authority.
export const MAX_QUANTITY = 10000;
export const PRODUCT_PAGE_SIZE = 6;

const SESSION_EXPIRED = 'Your session has expired. Please log in again.';

export function createStore({ api } = {}) {
  const selectedItems = new Map();
  const knownProducts = new Map();
  const listeners = new Set();
  let generation = 0;
  let latestProductRequest = 0;
  let token = null;

  let state = {
    loggedIn: false,
    email: '',
    login: { status: 'idle', error: '' },
    products: { status: 'idle', items: [], page: 1, pageSize: PRODUCT_PAGE_SIZE, totalCount: 0, totalPages: 0, error: '' },
    selected: [],
    couponCode: '',
    order: { status: 'idle', data: null, error: null },
    notice: null
  };

  function selectedLines() {
    return [...selectedItems].map(([productId, quantity]) => {
      const product = knownProducts.get(productId);
      return { productId, quantity, name: product?.name ?? productId, sku: product?.sku ?? '' };
    });
  }

  function update(changes) {
    state = { ...state, selected: selectedLines(), ...changes };
    for (const listener of [...listeners]) listener(state);
  }

  function endSession(notice) {
    token = null;
    selectedItems.clear();
    update({ loggedIn: false, email: '', couponCode: '', order: { status: 'idle', data: null, error: null }, notice });
  }

  /** Commits nothing for a stale session; a current-session 401 ends the session. Returns true when the caller should stop. */
  function superseded(startedIn, result) {
    if (startedIn !== generation) return true;
    if (result.status === 401 && token) {
      endSession({ kind: 'error', text: SESSION_EXPIRED });
      return true;
    }
    return false;
  }

  function problem(result, source) {
    return { source, title: result.error.title, detail: result.error.detail, errors: result.error.errors };
  }

  const store = {
    getState: () => state,
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    get isLoggedIn() { return token !== null; },

    // ---- selected lines (synchronous) ----
    get items() { return [...selectedItems].map(([productId, quantity]) => ({ productId, quantity })); },
    select(productId, quantity) {
      if (!productId || !Number.isInteger(quantity) || quantity < MIN_QUANTITY || quantity > MAX_QUANTITY) {
        throw new RangeError('A product and quantity from 1 to 10000 are required.');
      }
      selectedItems.set(productId, quantity);
      update({});
    },
    remove(productId) {
      selectedItems.delete(productId);
      update({});
    },
    clear() {
      selectedItems.clear();
      update({});
    },
    setCoupon(code) { update({ couponCode: String(code ?? '') }); },
    dismissNotice() { update({ notice: null }); },
    notify(kind, text) { update({ notice: { kind, text } }); },

    // ---- session ----
    async login(email, password) {
      const startedIn = ++generation; // invalidates everything still in flight from the previous session
      token = null;
      update({ loggedIn: false, login: { status: 'loading', error: '' }, notice: null });
      const result = await api.login(email, password);
      if (startedIn !== generation) return;
      if (!result.ok) {
        const message = result.status === 401 ? 'Invalid email or password.' : result.error.detail || result.error.title;
        update({ login: { status: 'error', error: message } });
        return;
      }
      token = result.data.token;
      update({ loggedIn: true, email: String(email).trim(), login: { status: 'idle', error: '' }, notice: { kind: 'success', text: 'Logged in.' } });
    },

    logout() {
      generation += 1;
      token = null;
      selectedItems.clear();
      update({
        loggedIn: false, email: '', couponCode: '', login: { status: 'idle', error: '' },
        order: { status: 'idle', data: null, error: null }, notice: { kind: 'info', text: 'Logged out.' }
      });
    },

    // ---- catalog (public) ----
    async loadProducts(page = 1) {
      const startedIn = generation;
      const request = ++latestProductRequest; // the newest requested page wins, whatever order responses arrive in
      update({ products: { ...state.products, status: 'loading', error: '' } });
      const result = await api.listProducts({ page, pageSize: PRODUCT_PAGE_SIZE });
      if (startedIn !== generation || request !== latestProductRequest) return;
      if (!result.ok) {
        update({ products: { ...state.products, status: 'error', error: result.error.detail || result.error.title } });
        return;
      }
      for (const product of result.data.items) knownProducts.set(product.id, product);
      const { items, page: current, pageSize, totalCount, totalPages } = result.data;
      update({ products: { status: items.length === 0 ? 'empty' : 'ready', items, page: current, pageSize, totalCount, totalPages, error: '' } });
    },

    // ---- orders (authenticated) ----
    async createOrder() {
      if (token === null) {
        update({ order: { ...state.order, error: { source: 'create', title: 'Log in to place an order.', detail: '', errors: {} } } });
        return;
      }
      if (state.order.status === 'saving' || state.order.status === 'deleting') return; // one submit in flight
      if (selectedItems.size === 0) {
        update({ order: { ...state.order, error: { source: 'create', title: 'Add at least one product first.', detail: '', errors: {} } } });
        return;
      }

      const startedIn = generation;
      const body = { items: store.items, couponCode: state.couponCode.trim() === '' ? null : state.couponCode.trim() };
      update({ order: { status: 'saving', data: state.order.data, error: null }, notice: null });

      const created = await api.createOrder(token, body);
      if (superseded(startedIn, created)) return;
      if (!created.ok) {
        update({ order: { status: state.order.data ? 'loaded' : 'idle', data: state.order.data, error: problem(created, 'create') } });
        return;
      }

      const loaded = await api.getOrder(token, created.data.id); // the shown order is what the server stored
      if (superseded(startedIn, loaded)) return;
      if (!loaded.ok) {
        selectedItems.clear();
        update({
          order: { status: 'idle', data: null, error: { source: 'create', title: `Order ${created.data.id} was created but could not be loaded.`, detail: loaded.error.detail, errors: {} } }
        });
        return;
      }
      selectedItems.clear();
      update({ couponCode: '', order: { status: 'loaded', data: loaded.data, error: null }, notice: { kind: 'success', text: 'Order created.' } });
    },

    async deleteOrder() {
      const current = state.order.data;
      if (token === null || current === null || state.order.status === 'saving' || state.order.status === 'deleting') return;
      const startedIn = generation;
      update({ order: { status: 'deleting', data: current, error: null }, notice: null });

      const result = await api.deleteOrder(token, current.id);
      if (superseded(startedIn, result)) return;
      if (!result.ok && result.status !== 404) {
        update({ order: { status: 'loaded', data: current, error: problem(result, 'delete') } });
        return;
      }
      const text = result.ok ? 'Order deleted.' : 'The order was already gone.';
      update({ order: { status: 'idle', data: null, error: null }, notice: { kind: 'success', text } });
    }
  };

  return store;
}
