import { h, emit } from '../dom.js';
import { formatMoney } from '../format.js';

/**
 * Shows the order exactly as the server stored it (fetched by GET) and emits `order-delete`.
 * Properties: `order` (the DTO or null), `busy`, `error`. Every amount shown was computed by the server.
 */
export class OrderDetail extends HTMLElement {
  #order = null;
  #busy = false;
  #error = null;
  #built = false;
  #body;
  #messages;

  set order(value) { this.#order = value ?? null; this.#render(); }
  get order() { return this.#order; }
  set busy(value) { this.#busy = Boolean(value); this.#render(); }
  set error(value) { this.#error = value ?? null; this.#render(); }

  connectedCallback() {
    if (!this.#built) this.#build();
    this.addEventListener('click', this.#onClick);
    this.#render();
  }

  disconnectedCallback() {
    this.removeEventListener('click', this.#onClick);
  }

  #build() {
    this.#built = true;
    this.#body = h('div', {});
    this.#messages = h('div', { class: 'message', role: 'alert' });
    this.append(h('section', { 'aria-labelledby': 'detail-heading' }, h('h2', { id: 'detail-heading', text: 'Current order' }), this.#body, this.#messages));
  }

  #render() {
    if (!this.#built) return;
    const order = this.#order;
    if (!order) {
      this.#body.replaceChildren(h('p', { class: 'message', text: 'No order yet. Place one to see it here.' }));
    } else {
      const coupon = order.coupon ? `${order.coupon.code} (−${formatMoney(order.coupon.amount, order.currency)})` : 'none';
      this.#body.replaceChildren(
        h('p', {}, h('strong', { text: `Order ${order.id}` }), h('span', { class: `badge status-${order.status.toLowerCase()}`, text: order.status })),
        h('p', { class: 'muted', text: `Created ${new Date(order.createdAt).toLocaleString()} · updated ${new Date(order.updatedAt).toLocaleString()}` }),
        h('table', {},
          h('thead', {}, h('tr', {}, ...['Product', 'Qty', 'Unit price', 'Line total'].map((label) => h('th', { scope: 'col', text: label })))),
          h('tbody', {}, ...order.items.map((item) => h('tr', {},
            h('td', { text: `${item.name} (${item.sku})` }), h('td', { text: String(item.quantity) }),
            h('td', { text: formatMoney(item.unitPrice, order.currency) }), h('td', { text: formatMoney(item.lineTotal, order.currency) })))),
          h('tfoot', {},
            this.#total('Subtotal', formatMoney(order.subtotal, order.currency)),
            this.#total('Coupon', coupon),
            this.#total('Discount', formatMoney(order.discount, order.currency)),
            this.#total('Total', formatMoney(order.total, order.currency)))),
        h('button', { type: 'button', 'data-delete': true, disabled: this.#busy, text: this.#busy ? 'Deleting…' : 'Delete order' }));
    }
    const messages = this.#error ? [h('p', { text: this.#error.title }), ...(this.#error.detail ? [h('p', { text: this.#error.detail })] : [])] : [];
    this.#messages.replaceChildren(...messages);
    this.#messages.hidden = messages.length === 0;
  }

  #total(label, value) {
    return h('tr', {}, h('th', { scope: 'row', colspan: 3, text: label }), h('td', { text: value }));
  }

  #onClick = (event) => {
    const button = event.target instanceof Element ? event.target.closest('button[data-delete]') : null;
    if (button && !this.#busy) emit(this, 'order-delete', {});
  };
}

customElements.define('order-detail', OrderDetail);
