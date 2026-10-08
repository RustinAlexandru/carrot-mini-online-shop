import { h, emit } from '../dom.js';

/** Collects credentials and emits `login-submit` { email, password }. Knows nothing about the store or the API. */
export class LoginForm extends HTMLElement {
  #busy = false;
  #error = '';
  #built = false;
  #form;
  #email;
  #password;
  #submit;
  #message;

  set busy(value) { this.#busy = Boolean(value); this.#render(); }
  get busy() { return this.#busy; }
  set error(value) { this.#error = value || ''; this.#render(); }
  get error() { return this.#error; }

  connectedCallback() {
    if (!this.#built) this.#build();
    this.#form.addEventListener('submit', this.#onSubmit);
    this.#render();
  }

  disconnectedCallback() {
    this.#form?.removeEventListener('submit', this.#onSubmit);
  }

  #build() {
    this.#built = true;
    this.#email = h('input', { id: 'login-email', name: 'email', type: 'email', autocomplete: 'username', required: true });
    this.#password = h('input', { id: 'login-password', name: 'password', type: 'password', autocomplete: 'current-password', required: true });
    this.#submit = h('button', { type: 'submit', text: 'Log in' });
    this.#message = h('p', { class: 'message', role: 'alert' });
    this.#form = h('form', { 'aria-labelledby': 'login-heading' },
      h('h2', { id: 'login-heading', text: 'Log in' }),
      h('label', { for: 'login-email', text: 'Email' }), this.#email,
      h('label', { for: 'login-password', text: 'Password' }), this.#password,
      this.#submit, this.#message,
      h('p', { class: 'hint', text: 'Demo account: demo@shop.test / DemoShop123!' }));
    this.append(this.#form);
  }

  #render() {
    if (!this.#built) return;
    this.#submit.disabled = this.#busy;
    this.#submit.textContent = this.#busy ? 'Logging in…' : 'Log in';
    this.#email.disabled = this.#busy;
    this.#password.disabled = this.#busy;
    this.#message.textContent = this.#error;
    this.#message.hidden = this.#error === '';
  }

  #onSubmit = (event) => {
    event.preventDefault();
    if (this.#busy) return;
    emit(this, 'login-submit', { email: this.#email.value, password: this.#password.value });
  };
}

customElements.define('login-form', LoginForm);
