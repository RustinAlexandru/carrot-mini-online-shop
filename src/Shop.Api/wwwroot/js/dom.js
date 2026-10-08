// Tiny element factory. Dynamic text always goes through textContent, never innerHTML.
export function h(tag, props = {}, ...children) {
  const element = document.createElement(tag);
  for (const [name, value] of Object.entries(props)) {
    if (value === undefined || value === null || value === false) continue;
    if (name === 'class') element.className = value;
    else if (name === 'text') element.textContent = value;
    else element.setAttribute(name, value === true ? '' : value);
  }
  for (const child of children) element.append(child);
  return element;
}

export function emit(source, name, detail) {
  source.dispatchEvent(new CustomEvent(name, { bubbles: true, composed: true, detail }));
}
