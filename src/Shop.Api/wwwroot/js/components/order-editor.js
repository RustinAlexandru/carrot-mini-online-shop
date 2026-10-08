import { h, emit } from '../dom.js';
import { describeErrors } from '../format.js';

/**
 * The order being built: selected lines, a coupon code and the submit button.
 * Emits `line-quantity` { productId, quantity }, `line-remove` { productId }, `coupon-change` { couponCode } and `order-submit`.
 * Properties: `lines`, `couponCode`, `loggedIn`, `busy`, `error` ({ title, detail, errors }).
 * Totals are never computed here: the server prices the order and the result is shown by <order-detail>.
 */
export class OrderEditor extends HTMLElement {
  #lines = [];
  #couponCode = '';
  #loggedIn = false;
  #busy = false;
  #error = null;
  #built = false;
  #list;
  #empty;
  #coupon;
  #submit;
  #messages;
  #hint;
  #form;

  set lines(value) { this.#lines = value ?? []; this.#render(); }
  get lines() { return this.#lines; }
  set couponCode(value) { this.#couponCode = value ?? ''; this.#render(); }
  get couponCode() { return this.#couponCode; }
  set loggedIn(value) { this.#loggedIn = Boolean(value); this.#render(); }
  set busy(value) { this.#busy = Boolean(value); this.#render(); }
  get busy() { return this.#busy; }
  set error(value) { this.#error = value ?? null; this.#render(); }

  connectedCallback() {
    if (!this.#built) this.#build();
    this.#form.addEventListener('submit', this.#onSubmit);
    this.#form.addEventListener('click', this.#onClick);
    this.#form.addEventListener('change', this.#onChange);
    this.#coupon.addEventListener('input', this.#onCoupon);
    this.#render();
  }

  disconnectedCallback() {
    this.#form?.removeEventListener('submit', this.#onSubmit);
    this.#form?.removeEventListener('click', this.#onClick);
    this.#form?.removeEventListener('change', this.#onChange);
    this.#coupon?.removeEventListener('input', this.#onCoupon);
  }

  #build() {
    this.#built = true;
    this.#empty = h('p', { class: 'message', text: 'No products selected yet. Add some from the list.' });
    this.#list = h('ul', { class: 'lines' });
    this.#coupon = h('input', { id: 'coupon-code', name: 'couponCode', type: 'text', autocomplete: 'off', maxlength: 32, placeholder: 'e.g. SAVE5' });
    this.#submit = h('button', { type: 'submit', text: 'Place order' });
    this.#hint = h('p', { class: 'hint' });
    this.#messages = h('div', { class: 'message', role: 'alert' });
    this.#form = h('form', { 'aria-labelledby': 'editor-heading' },
      h('h2', { id: 'editor-heading', text: 'Your order' }), this.#empty, this.#list,
      h('label', { for: 'coupon-code', text: 'Coupon code (optional)' }), this.#coupon,
      this.#submit, this.#hint, this.#messages);
    this.append(this.#form);
  }

  #render() {
    if (!this.#built) return;
    this.#empty.hidden = this.#lines.length > 0;
    this.#list.replaceChildren(...this.#lines.map((line) => {
      const id = `line-${line.productId}`;
      return h('li', {},
        h('strong', { text: line.name }),
        h('label', { for: id, text: `Quantity of ${line.name}` }),
        h('input', { id, type: 'number', min: 1, max: 10000, step: 1, value: line.quantity, 'data-line': line.productId, disabled: this.#busy }),
        h('button', { type: 'button', 'data-remove': line.productId, disabled: this.#busy, 'aria-label': `Remove ${line.name}`, text: 'Remove' }));
    }));
    if (this.#coupon.value !== this.#couponCode) this.#coupon.value = this.#couponCode;
    this.#coupon.disabled = this.#busy;
    this.#submit.disabled = this.#busy || this.#lines.length === 0 || !this.#loggedIn;
    this.#submit.textContent = this.#busy ? 'Placing order…' : 'Place order';
    this.#hint.textContent = this.#loggedIn ? '' : 'Log in to place an order.';
    this.#hint.hidden = this.#loggedIn;

    const messages = [];
    if (this.#error) {
      messages.push(h('p', { text: this.#error.title }));
      if (this.#error.detail) messages.push(h('p', { text: this.#error.detail }));
      const fields = describeErrors(this.#error.errors, this.#lines);
      if (fields.length) messages.push(h('ul', {}, ...fields.map((text) => h('li', { text }))));
    }
    this.#messages.replaceChildren(...messages);
    this.#messages.hidden = messages.length === 0;
  }

  #onSubmit = (event) => {
    event.preventDefault();
    if (this.#busy) return;
    emit(this, 'order-submit', { couponCode: this.#coupon.value });
  };

  #onClick = (event) => {
    const button = event.target instanceof Element ? event.target.closest('button[data-remove]') : null;
    if (button) emit(this, 'line-remove', { productId: button.dataset.remove });
  };

  #onChange = (event) => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || !input.dataset.line) return;
    if (input.reportValidity()) emit(this, 'line-quantity', { productId: input.dataset.line, quantity: Number(input.value) });
  };

  #onCoupon = () => emit(this, 'coupon-change', { couponCode: this.#coupon.value });
}

customElements.define('order-editor', OrderEditor);
