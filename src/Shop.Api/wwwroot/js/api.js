// Transport only: fetch is injected, the token and request are explicit arguments, and nothing here touches store state.
// Every call resolves to { ok: true, status, data } or { ok: false, status, error: { title, detail, errors } }.

function failure(status, title, detail = '', errors = {}) {
  return { ok: false, status, error: { title, detail, errors } };
}

async function readJson(response) {
  let text = '';
  try {
    text = await response.text();
  } catch {
    return null;
  }
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null; // non-JSON bodies (proxies, HTML error pages) are tolerated
  }
}

export function createApi(fetchImpl, { baseUrl = '' } = {}) {
  async function request(method, path, { token, body } = {}) {
    const headers = { Accept: 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    const init = { method, headers };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    let response;
    try {
      response = await fetchImpl(`${baseUrl}${path}`, init);
    } catch (error) {
      return failure(0, 'Network error', error instanceof Error ? error.message : String(error));
    }

    if (response.status === 204) return { ok: true, status: 204, data: null };
    const data = await readJson(response);
    if (response.ok) return { ok: true, status: response.status, data };
    const problem = data && typeof data === 'object' ? data : {};
    return failure(
      response.status,
      typeof problem.title === 'string' && problem.title ? problem.title : `Request failed (${response.status})`,
      typeof problem.detail === 'string' ? problem.detail : '',
      problem.errors && typeof problem.errors === 'object' && !Array.isArray(problem.errors) ? problem.errors : {});
  }

  return {
    login: (email, password) => request('POST', '/auth/login', { body: { email, password } }),
    listProducts: ({ page = 1, pageSize = 12 } = {}) =>
      request('GET', `/products?page=${encodeURIComponent(page)}&pageSize=${encodeURIComponent(pageSize)}`),
    createOrder: (token, order) => request('POST', '/orders', { token, body: order }),
    getOrder: (token, id) => request('GET', `/orders/${encodeURIComponent(id)}`, { token }),
    deleteOrder: (token, id) => request('DELETE', `/orders/${encodeURIComponent(id)}`, { token })
  };
}
