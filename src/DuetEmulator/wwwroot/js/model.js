// The object model as the page shows it: keys addressed as M409 keys, and two stacks' models side by side

/** How far apart numbers may be and still be the same: as the comparison on the server compares them */
export const NUMBER_TOLERANCE = 0.001;

/** An M409 key as the names and indices it walks, such as move.axes[0] → ['move', 'axes', 0] */
export function parseKey(key) {
  const parts = [];
  for (const part of key.split('.').filter(p => p.length > 0)) {
    const match = /^([^[\]]+)((?:\[\d+\])*)$/.exec(part);
    if (!match) throw new Error(`"${key}" is not an object model key such as boards[1].drivers[0]`);
    parts.push(match[1]);
    for (const index of match[2].matchAll(/\[(\d+)\]/g)) parts.push(Number(index[1]));
  }
  return parts;
}

/** The key of a child: a name after a dot, an index in brackets */
export function childKey(parent, child) {
  if (typeof child === 'number') return `${parent}[${child}]`;
  return parent === '' ? child : `${parent}.${child}`;
}

/** The part of a model a key names, or undefined if it has nothing there */
export function valueAt(model, key) {
  let node = model;
  for (const part of parseKey(key)) {
    if (node === null || typeof node !== 'object') return undefined;
    node = node[part];
    if (node === undefined) return undefined;
  }
  return node;
}

/** Whether two JSON values are the same, numbers within the tolerance */
export function same(a, b) {
  if (typeof a === 'number' && typeof b === 'number') {
    return Math.abs(a - b) <= NUMBER_TOLERANCE * Math.max(1, Math.abs(a), Math.abs(b));
  }
  if (Array.isArray(a) && Array.isArray(b)) return a.length === b.length && a.every((v, i) => same(v, b[i]));
  if (isObject(a) && isObject(b)) {
    const keys = Object.keys(a);
    return keys.length === Object.keys(b).length && keys.every(k => k in b && same(a[k], b[k]));
  }
  return a === b;
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/**
 * Whether a key is one of the patterns or under one, [*] in a pattern matching any index: as
 * ObjectModelDiff.Matches on the server, which the comparison leaves such keys out with
 */
export function matches(key, patterns) {
  return patterns.some(pattern => {
    const expression = pattern.replace(/[.+?^${}()|\\]/g, '\\$&').replace(/\[\*\]/g, '\\[\\d+\\]').replace(/\[(\d+)\]/g, '\\[$1\\]');
    return new RegExp(`^${expression}($|[.[])`).test(key);
  });
}

/** Whether two values are the same, leaving out the keys under <key> that match the patterns */
export function sameIgnoring(a, b, key, patterns) {
  if (patterns.length > 0 && key !== '' && matches(key, patterns)) return true;
  const containers = [a, b].filter(v => v !== null && typeof v === 'object');
  if (containers.length < 2 || Array.isArray(a) !== Array.isArray(b)) return same(a, b);
  const names = Array.isArray(a) ? [...Array(Math.max(a.length, b.length)).keys()] : [...new Set([...Object.keys(a), ...Object.keys(b)])];
  return names.every(name => sameIgnoring(a[name], b[name], childKey(key, name), patterns));
}

/** A value as one cell of the tree: a leaf as JSON, an object or array by its size */
export function summary(value) {
  if (value === undefined) return '';
  if (Array.isArray(value)) return `${value.length} item${value.length === 1 ? '' : 's'}`;
  if (isObject(value)) return `${Object.keys(value).length} key${Object.keys(value).length === 1 ? '' : 's'}`;
  return JSON.stringify(value);
}

/** Whether a key is, contains or is inside one of the keys that changed */
export function touches(key, changed) {
  for (const path of changed) {
    if (path === key || path.startsWith(`${key}.`) || path.startsWith(`${key}[`) || key.startsWith(`${path}.`) || key.startsWith(`${path}[`)) {
      return true;
    }
  }
  return false;
}

/**
 * What the object model's one box asks for: a key that is in any stack's model shows that key, and any
 * other text is searched for in names and values
 */
export function modelQuery(models, text) {
  const query = text.trim();
  if (query === '') return { root: '', search: '' };
  let isKey = false;
  try {
    isKey = Object.values(models).some(model => valueAt(model, query) !== undefined);
  } catch {
    // Not written as a key, so it is searched for
  }
  return isKey ? { root: query, search: '' } : { root: '', search: query };
}

/**
 * Whether a value, or one under it, is or holds the text, or has a name that does: what a search of the
 * tree finds, case aside. A string is searched without its quotes
 */
export function found(value, name, text) {
  const needle = text.toLowerCase();
  const search = (v, n) => {
    if (String(n).toLowerCase().includes(needle)) return true;
    if (v !== null && typeof v === 'object') return Object.entries(v).some(([k, child]) => search(child, k));
    return (typeof v === 'string' ? v : JSON.stringify(v) ?? '').toLowerCase().includes(needle);
  };
  return search(value, name);
}

/**
 * The rows of a tree of several stacks' models side by side, from a key down: each row a key, its
 * depth, the value on each stack, whether it can open, whether the stacks differ under it (leaving out
 * the keys the comparison ignores) and whether the last step changed it. A key opens when it is in
 * <expanded>, with <onlyDifferences> when the stacks differ under it, or with <search> when something
 * under it is found, unless it is in <collapsed>; with <onlyDifferences> the keys under which the
 * stacks agree are left out, and with <search> those whose name, value and children do not have the text
 */
export function treeRows(models, { root = '', expanded = new Set(), collapsed = new Set(), changed = new Set(), ignored = [], onlyDifferences = false,
                                   search = '' } = {}) {
  const stacks = Object.keys(models);
  const rows = [];
  const text = search.trim();
  const visit = (key, name, depth) => {
    const values = stacks.map(stack => key === '' ? models[stack] : valueAt(models[stack], key));
    const container = values.some(v => v !== null && typeof v === 'object');
    const differs = stacks.length > 1 && !values.every(v => sameIgnoring(v, values[0], key, ignored));
    if (onlyDifferences && !differs) return;
    if (text && !values.some(v => found(v, name, text))) return;
    // A container whose own name is what was searched for stays closed, unless something under it is found too
    const foundUnder = text !== '' && container && values.some(v => v !== null && typeof v === 'object' && Object.entries(v).some(([k, child]) => found(child, k, text)));
    const open = container && (expanded.has(key) || (onlyDifferences && differs) || foundUnder) && !collapsed.has(key);
    rows.push({
      key,
      name,
      depth,
      values: Object.fromEntries(stacks.map((stack, i) => [stack, values[i]])),
      container,
      open,
      ignored: matches(key, ignored),
      differs,
      changed: touches(key, changed)
    });
    if (open) {
      for (const child of childNames(values)) visit(childKey(key, child), typeof child === 'number' ? `[${child}]` : child, depth + 1);
    }
  };
  if (root === '') {
    for (const child of childNames(stacks.map(s => models[s]))) visit(child, child, 0);
  } else {
    visit(root, root, 0);
  }
  return rows;
}

// The names or indices of the children any of the values has, in order
function childNames(values) {
  const names = [];
  const seen = new Set();
  for (const value of values) {
    if (value === null || typeof value !== 'object') continue;
    const keys = Array.isArray(value) ? value.map((_, i) => i) : Object.keys(value).sort();
    for (const key of keys) {
      if (!seen.has(key)) {
        seen.add(key);
        names.push(key);
      }
    }
  }
  return Array.isArray(values.find(v => v !== null && typeof v === 'object')) ? names.sort((a, b) => a - b) : names.sort();
}
