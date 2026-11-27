// Building the page's elements

/**
 * An element: h('button', { class: 'primary', onclick: send }, 'Send'). Properties starting with "on"
 * are listeners, "class" and "style" set those attributes, a boolean sets or removes an attribute
 * (except an aria- one, which is written "true" or "false"),
 * "value" and "checked" set the element's state, and children are elements, text or arrays of them
 */
export function h(tag, props = {}, ...children) {
  const element = document.createElement(tag);
  for (const [name, value] of Object.entries(props ?? {})) {
    if (name.startsWith('aria-') && typeof value === 'boolean') {
      // aria-expanded="false" and the like say something, where a boolean attribute's absence does
      element.setAttribute(name, String(value));
      continue;
    }
    if (value === undefined || value === null || value === false) continue;
    if (name.startsWith('on') && typeof value === 'function') {
      element.addEventListener(name.slice(2), value);
    } else if (name === 'value' || name === 'checked' || name === 'indeterminate') {
      element[name] = value;
    } else if (name === 'dataset') {
      Object.assign(element.dataset, value);
    } else {
      element.setAttribute(name, value === true ? '' : String(value));
    }
  }
  append(element, children);
  return element;
}

/** Add children to an element, skipping null, undefined and false */
export function append(element, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    element.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return element;
}

/** Replace an element's children */
export function replace(element, ...children) {
  element.replaceChildren();
  return append(element, children);
}

/** An icon from the page's sprite, for a button that also has text or an aria-label */
export function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', `#i-${name}`);
  svg.append(use);
  return svg;
}

/** Call a function after it has stopped being asked for for a while */
export function debounce(fn, ms) {
  let timer = null;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

/** Text copied to the clipboard, saying so briefly on the element that copied it */
export async function copy(text, element) {
  try {
    await navigator.clipboard.writeText(text);
    if (element) flash(element, 'Copied');
  } catch {
    if (element) flash(element, 'Copy failed');
  }
}

function flash(element, message) {
  const previous = element.dataset.flash;
  element.dataset.flash = message;
  setTimeout(() => {
    if (element.dataset.flash === message) {
      if (previous === undefined) delete element.dataset.flash;
      else element.dataset.flash = previous;
    }
  }, 1200);
}

/** The value of something stored in the browser for this page, or a default if there is none */
export function stored(key, fallback) {
  try {
    const text = localStorage.getItem(`emulation.${key}`);
    return text === null ? fallback : JSON.parse(text);
  } catch {
    return fallback;
  }
}

/** Store something in the browser for this page */
export function store(key, value) {
  try {
    localStorage.setItem(`emulation.${key}`, JSON.stringify(value));
  } catch {
    // Storage is a convenience: without it the page still works
  }
}
