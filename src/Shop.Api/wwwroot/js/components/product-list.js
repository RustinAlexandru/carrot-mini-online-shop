import { h, emit } from '../dom.js';
import { formatMoney } from '../format.js';

/**
 * Shows one page of the public catalog (default ordering) and emits
 * `product-select` { productId, quantity } and `page-change` { page }.
 * Properties: `products` (page state), `selected` (lines already in the order).
 */
export class ProductList extends HTMLElement {
  #products = { status: 'idle', items: [], page: 1, totalPages: 0, error: '' };
  #selected = [];
  #typed = new Map();
  #built = false;
  #status;
  #rows;
  #pager;
  #prev;
  #next;
  #position;

  set products(value) { this.#products = value; this.#render(); }
  get products() { return this.#products; }
  set selected(value) { this.#selected = value ?? []; this.#render(); }
  get selected() { return this.#selected; }

  connectedCallback() {
    if (!this.#built) this.#build();
    this.addEventListener('click', this.#onClick);
    this.addEventListener('input', this.#onInput);
    this.#render();
  }

  disconnectedCallback() {
    this.removeEventListener('click', this.#onClick);
    this.removeEventListener('input', this.#onInput);
  }

  #build() {
    this.#built = true;
    this.#status = h('p', { class: 'message', role: 'status' });
    this.#rows = h('ul', { class: 'products' });
    this.#prev = h('button', { type: 'button', 'data-page': 'prev', text: 'Previous' });
    this.#next = h('button', { type: 'button', 'data-page': 'next', text: 'Next' });
    this.#position = h('span', { 'aria-live': 'polite' });
    this.#pager = h('nav', { 'aria-label': 'Product pages', class: 'pager' }, this.#prev, this.#position, this.#next);
    this.append(h('section', { 'aria-labelledby': 'products-heading' }, h('h2', { id: 'products-heading', text: 'Products' }), this.#status, this.#rows, this.#pager));
  }

  #render() {
    if (!this.#built) return;
    const { status, items, page, totalPages, error } = this.#products;
    this.#status.textContent = { loading: 'Loading products…', empty: 'No products to show.', error: `Could not load products: ${error}` }[status] ?? '';
    this.#status.hidden = this.#status.textContent === '';
    const inOrder = new Map(this.#selected.map((line) => [line.productId, line.quantity]));
    this.#rows.replaceChildren(...items.map((product) => this.#row(product, inOrder.get(product.id))));
    this.#rows.setAttribute('aria-busy', String(status === 'loading'));
    this.#pager.hidden = totalPages <= 1;
    this.#position.textContent = ` Page ${page} of ${totalPages} `;
    this.#prev.disabled = status === 'loading' || page <= 1;
    this.#next.disabled = status === 'loading' || page >= totalPages;
  }

  #row(product, quantityInOrder) {
    const soldOut = product.stockQuantity <= 0;
    const id = `qty-${product.id}`;
    const quantity = h('input', {
      id, type: 'number', min: 1, max: Math.min(10000, Math.max(1, product.stockQuantity)), step: 1,
      value: this.#typed.get(product.id) ?? '1', 'data-product': product.id, disabled: soldOut
    });
    const add = h('button', { type: 'button', 'data-add': product.id, disabled: soldOut, text: quantityInOrder ? 'Update order line' : 'Add to order' });
    return h('li', {},
      h('strong', { text: product.name }),
      h('span', { class: 'muted', text: ` ${product.sku} · ${product.category}` }),
      h('span', { class: 'price', text: formatMoney(product.price) }),
      h('span', { class: soldOut ? 'badge out' : 'badge', text: soldOut ? 'Out of stock' : `${product.stockQuantity} in stock` }),
      quantityInOrder ? h('span', { class: 'badge in-order', text: `In order: ${quantityInOrder}` }) : '',
      h('label', { for: id, text: `Quantity of ${product.name}` }), quantity, add);
  }

  #onInput = (event) => {
    const input = event.target;
    if (input instanceof HTMLInputElement && input.dataset.product) this.#typed.set(input.dataset.product, input.value);
  };

  #onClick = (event) => {
    const button = event.target instanceof Element ? event.target.closest('button') : null;
    if (!button) return;
    if (button.dataset.page) {
      const { page } = this.#products;
      emit(this, 'page-change', { page: button.dataset.page === 'next' ? page + 1 : page - 1 });
    } else if (button.dataset.add) {
      const input = this.querySelector(`input[data-product="${CSS.escape(button.dataset.add)}"]`);
      if (!input || !input.reportValidity()) return;
      emit(this, 'product-select', { productId: button.dataset.add, quantity: Number(input.value) });
    }
  };
}

customElements.define('product-list', ProductList);
