/* MatRegistry – small helpers of the scripts of the grid, the viewer and the import: texts, DOM, ids, requests, toasts.
   Everything a person typed goes into the page as text (textContent) or through the sanitizer of the app; never as HTML of the server. */

let texts = null;

/** A text of the catalog (the JSON block the layout writes, keys "Js.*"); {0}, {1} ... are replaced by the arguments. */
export function t(key, ...args) {
  if (!texts) {
    const block = document.getElementById('md-texts');
    try { texts = block ? JSON.parse(block.textContent) : {}; } catch { texts = {}; }
  }
  const text = texts['Js.' + key] ?? key;
  return args.length ? text.replace(/\{(\d+)\}/g, (_, i) => String(args[Number(i)] ?? '')) : text;
}

/** The language of the page (en or de). */
export const lang = () => (document.documentElement.lang || 'en').slice(0, 2);

/** An element: el('button', { class: 'x', type: 'button', onclick: fn }, 'text', child). */
export function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  if (attrs) {
    for (const [name, value] of Object.entries(attrs)) {
      if (value === undefined || value === null || value === false) continue;
      if (name.startsWith('on') && typeof value === 'function') node.addEventListener(name.slice(2), value);
      else if (name === 'dataset') Object.assign(node.dataset, value);
      else if (name === 'text') node.textContent = value;
      else if (name === 'style' && typeof value === 'object') Object.assign(node.style, value);
      else node.setAttribute(name, value === true ? '' : String(value));
    }
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

/** An icon of the sprite (the toolkit's "mat-i-*" or the app's own, wwwroot/img/app-icons.svg). */
export function icon(name, cls) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('class', cls ? 'mat-icon ' + cls : 'mat-icon');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const use = document.createElementNS(ns, 'use');
  use.setAttribute('href', '#mat-i-' + name);
  svg.append(use);
  return svg;
}

/** The anti-forgery token of the page (the layout carries it). */
export function token() {
  return document.querySelector('[data-page-token] input[name="__RequestVerificationToken"]')?.value || '';
}

/** A JSON request; throws an error with .status (0 = no network) and .body for an answer that is not 2xx. */
export async function request(method, url, body, options = {}) {
  const headers = { Accept: 'application/json' };
  if (method !== 'GET') headers.RequestVerificationToken = token();
  let payload;
  if (body instanceof Blob || body instanceof ArrayBuffer) { payload = body; headers['Content-Type'] = body.type || 'application/octet-stream'; }
  else if (body !== undefined) { payload = JSON.stringify(body); headers['Content-Type'] = 'application/json'; }
  let response;
  try {
    response = await fetch(url, { method, headers, body: payload, credentials: 'same-origin', cache: 'no-store', signal: options.signal });
  } catch (e) {
    const error = new Error('network');
    error.status = 0;
    throw error;
  }
  const type = response.headers.get('Content-Type') || '';
  const data = type.includes('json') ? await response.json().catch(() => null) : null;
  if (!response.ok) {
    const error = new Error('HTTP ' + response.status);
    error.status = response.status;
    error.body = data;
    throw error;
  }
  return data;
}

export function debounce(fn, ms) {
  let timer = 0;
  const run = (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
  run.cancel = () => clearTimeout(timer);
  run.flush = (...args) => { clearTimeout(timer); fn(...args); };
  return run;
}

/** A toast of the toolkit. */
export function toast(message, tone = 'info') {
  if (window.MatUi?.toast) window.MatUi.toast(message, { tone });
}
