// The session server's API, and following its session as it changes

/** A refusal from the server, with its status */
export class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function request(method, path, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.body = typeof body === 'string' ? body : JSON.stringify(body);
    options.headers['Content-Type'] = 'application/json';
  }
  let response;
  try {
    response = await fetch(`/api${path}`, options);
  } catch {
    throw new ApiError(0, 'The session server is not answering: is scripts/emulate.py serve running?');
  }
  const text = await response.text();
  let answer = null;
  try {
    answer = text.length > 0 ? JSON.parse(text) : null;
  } catch {
    throw new ApiError(response.status, text || response.statusText);
  }
  if (!response.ok) throw new ApiError(response.status, answer?.error ?? response.statusText);
  return answer;
}

export const api = {
  get: path => request('GET', path),
  post: (path, body) => request('POST', path, body ?? ''),
  delete: path => request('DELETE', path)
};

/**
 * Follow the session: handlers.hello gets everything there is whenever the stream (re)connects, then
 * handlers.status, .step, .capture and .boards get each change. handlers.connection(bool) says whether
 * the stream is up. Returns a function that stops following
 */
export function follow(handlers) {
  let source = null;
  let closed = false;
  let retry = null;
  const open = () => {
    source = new EventSource('/api/events');
    source.onopen = () => handlers.connection?.(true);
    source.onerror = () => {
      handlers.connection?.(false);
      if (source.readyState === EventSource.CLOSED && !closed) {
        retry = setTimeout(open, 1000);
      }
    };
    for (const name of ['hello', 'status', 'step', 'capture', 'boards']) {
      source.addEventListener(name, e => handlers[name]?.(JSON.parse(e.data)));
    }
  };
  open();
  return {
    close() {
      closed = true;
      clearTimeout(retry);
      source?.close();
    },
    // Reconnect, which starts again with hello
    restart() {
      source?.close();
      open();
    }
  };
}
