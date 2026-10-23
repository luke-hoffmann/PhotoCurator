// Talking to the local server. Every call carries the per-launch token the window was opened with.

const params = new URLSearchParams(location.search);
export const token = params.get('t') ?? sessionStorage.getItem('photocurator-token') ?? '';
sessionStorage.setItem('photocurator-token', token);

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

export async function api(method, path, body) {
  let response;
  try {
    response = await fetch(path, {
      method,
      headers: body === undefined ? { 'X-Curator-Token': token } : { 'X-Curator-Token': token, 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
  } catch {
    throw new ApiError('PhotoCurator stopped responding. Close and reopen the app.', 0);
  }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new ApiError(data.error || `Something went wrong (${response.status}).`, response.status);
  return data;
}

export const get = path => api('GET', path);
export const post = (path, body = {}) => api('POST', path, body);
export const put = (path, body) => api('PUT', path, body);

export function thumbUrl(photo) {
  return `/api/thumb?id=${encodeURIComponent(photo.id)}&v=${photo.v}&t=${encodeURIComponent(token)}`;
}

export function imageUrl(photo) {
  return `/api/image?id=${encodeURIComponent(photo.id)}&v=${photo.v}&t=${encodeURIComponent(token)}`;
}

/** Server-sent events: "library" when the folder changed, "job" for copy/export progress. Reconnects by itself. */
export function listen(handlers) {
  const source = new EventSource(`/api/events?t=${encodeURIComponent(token)}`);
  for (const [name, handler] of Object.entries(handlers)) {
    source.addEventListener(name, event => handler(JSON.parse(event.data)));
  }
  return source;
}
