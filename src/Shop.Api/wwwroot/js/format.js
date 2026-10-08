// Pure display helpers. Money arrives already computed by the server; this only formats it.

export function formatMoney(value, currency = 'USD') {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(Number(value));
}

/**
 * Turns a Problem Details `errors` map into readable lines, replacing `items[2].quantity` with the name of the
 * third order line so the message points at something the user can see.
 */
export function describeErrors(errors, lines = []) {
  const messages = [];
  for (const [field, list] of Object.entries(errors ?? {})) {
    const match = /^items\[(\d+)\]\.(\w+)$/.exec(field);
    const line = match ? lines[Number(match[1])] : undefined;
    const label = line ? line.name : field;
    for (const text of Array.isArray(list) ? list : [String(list)]) messages.push(`${label}: ${text}`);
  }
  return messages;
}
