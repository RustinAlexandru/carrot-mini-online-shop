import { h } from '../dom.js';
import { createApi } from '../api.js';
import { createStore } from '../store.js';
import './login-form.js';
import './product-list.js';
import './order-editor.js';
import './order-detail.js';

/**
 * Coordinator. It owns the store and the API wiring: child components only receive properties and emit
 * bubbling events, which are handled here and turned into store calls. The store is subscribed on connect
 * and unsubscribed on disconnect, as are the event listeners.
 */
export class ShopApp extends HTMLElement {
  #store = null;
  #unsubscribe = null;
  #built = false;
  #session;
  #notice;
  #login;
  #products;
  #editor;
  #detail;

  /** Tests or alternative hosts may inject a ready store; otherwise one is created on top of window.fetch. */
  set store(value) { this.#store = value; }
  get store() { return this.#store; }

  connectedCallback() {
    if (!this.#built) this.#build();
    this.#store ??= createStore({ api: createApi((...args) => window.fetch(...args)) });
    this.#unsubscribe = this.#store.subscribe((state) => this.#render(state));
    for (const [name, handler] of this.#handlers) this.addEventListener(name, handler);
    this.#render(this.#store.getState());
    this.#store.loadProducts(1);
  }

  disconnectedCallback() {
    this.#unsubscribe?.();
    this.#unsubscribe = null;
    for (const [name, handler] of this.#handlers) this.removeEventListener(name, handler);
  }

  #build() {
    this.#built = true;
    this.#session = h('div', { class: 'session' });
    this.#notice = h('div', { class: 'notice', role: 'status' });
    this.#login = document.createElement('login-form');
    this.#products = document.createElement('product-list');
    this.#editor = document.createElement('order-editor');
    this.#detail = document.createElement('order-detail');
    this.append(
      h('header', {}, h('h1', { text: 'Mini Online Shop' }), this.#session),
      this.#notice,
      h('main', {}, this.#login, this.#products, this.#editor, this.#detail));
  }

  #render(state) {
    this.#session.replaceChildren(...(state.loggedIn
      ? [h('span', { text: `Logged in as ${state.email} ` }), h('button', { type: 'button', 'data-logout': true, text: 'Log out' })]
      : [h('span', { class: 'muted', text: 'Not logged in' })]));
    this.#notice.replaceChildren(...(state.notice
      ? [h('span', { class: state.notice.kind, text: state.notice.text }), h('button', { type: 'button', 'data-dismiss': true, 'aria-label': 'Dismiss message', text: '×' })]
      : []));
    this.#notice.hidden = !state.notice;

    this.#login.hidden = state.loggedIn;
    this.#login.busy = state.login.status === 'loading';
    this.#login.error = state.login.error;

    this.#products.products = state.products;
    this.#products.selected = state.selected;

    const createError = state.order.error?.source === 'create' ? state.order.error : null;
    const deleteError = state.order.error?.source === 'delete' ? state.order.error : null;
    this.#editor.lines = state.selected;
    this.#editor.couponCode = state.couponCode;
    this.#editor.loggedIn = state.loggedIn;
    this.#editor.busy = state.order.status === 'saving';
    this.#editor.error = createError;
    this.#detail.order = state.order.data;
    this.#detail.busy = state.order.status === 'deleting';
    this.#detail.error = deleteError;
  }

  #handlers = new Map([
    ['login-submit', async (event) => {
      await this.#store.login(event.detail.email, event.detail.password);
      this.#store.loadProducts(this.#store.getState().products.page); // a login invalidates in-flight loads; refresh the visible page
    }],
    ['page-change', (event) => this.#store.loadProducts(event.detail.page)],
    ['product-select', (event) => this.#select(event.detail.productId, event.detail.quantity)],
    ['line-quantity', (event) => this.#select(event.detail.productId, event.detail.quantity)],
    ['line-remove', (event) => this.#store.remove(event.detail.productId)],
    ['coupon-change', (event) => this.#store.setCoupon(event.detail.couponCode)],
    ['order-submit', () => this.#store.createOrder()],
    ['order-delete', () => this.#store.deleteOrder()],
    ['click', (event) => {
      const button = event.target instanceof Element ? event.target.closest('button') : null;
      if (button?.hasAttribute('data-logout')) {
        this.#store.logout();
        this.#store.loadProducts(this.#store.getState().products.page);
      } else if (button?.hasAttribute('data-dismiss')) {
        this.#store.dismissNotice();
      }
    }]
  ]);

  #select(productId, quantity) {
    try {
      this.#store.select(productId, quantity);
    } catch (error) {
      if (!(error instanceof RangeError)) throw error;
      this.#store.notify('error', error.message);
    }
  }
}

customElements.define('shop-app', ShopApp);
