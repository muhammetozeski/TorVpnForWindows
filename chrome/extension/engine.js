import { TorClient, Log } from './tor/entryPoints/wasm-file/index.js';
import { MaxBodyBytes, validateGateway, responseHeaders, toBase64, fromBase64 } from './config.js';

let client = null;
let generation = 0;
let engineState = { phase: 'disconnected', ip: '' };
const requests = new Set();

function stop() {
  generation++;
  for (const request of requests) request.abort();
  requests.clear();
  client?.close();
  client = null;
  engineState = { phase: 'disconnected', ip: '' };
}

async function start(gateway) {
  stop();
  const current = generation;
  engineState = { phase: 'connecting', ip: '' };
  client = new TorClient({ gateway: validateGateway(gateway), logLevel: 'warn',
    log: new Log({ rawLog: (level, ...values) => {
      if (level === 'error' || level === 'warn') console.warn('[Tor]', ...values);
    } }) });
  const active = client;
  // Closing the client after a failed bootstrap releases its transports and pending requests.
  let timer;
  try {
    await Promise.race([active.ready(), new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('bootstrapTimeout')), 60000);
    })]);
    if (current !== generation) throw new Error('canceled');
    const response = await active.fetch('https://check.torproject.org/api/ip', { signal: AbortSignal.timeout(30000) });
    const result = await response.json();
    if (!response.ok || result.IsTor !== true) throw new Error('torCheckFailed');
    if (current !== generation) throw new Error('canceled');
    engineState = { phase: 'connected', ip: result.IP };
    return { ip: result.IP };
  } catch (error) {
    if (current === generation) stop();
    throw error;
  } finally { clearTimeout(timer); }
}

async function fetchThroughTor(request) {
  if (!client) throw new Error('notConnected');
  const url = new URL(request.url);
  if (!['https:', 'http:'].includes(url.protocol)) throw new Error('unsupportedProtocol');
  const active = client;
  const current = generation;
  const controller = new AbortController();
  requests.add(controller);
  const timeout = setTimeout(() => controller.abort(), 60000);
  try {
    const headers = Object.fromEntries(Object.entries(request.headers || {}).filter(([name]) =>
      !['host', 'connection', 'proxy-connection', 'transfer-encoding', 'content-length', 'accept-encoding'].includes(name.toLowerCase())));
    // The browser decodes the returned body according to its response headers. Request identity
    // encoding so no library/browser pair can decode the same compressed entity twice.
    headers['Accept-Encoding'] = 'identity';
    const body = request.body ? fromBase64(request.body) : undefined;
    if (body && body.length > MaxBodyBytes) throw new Error('bodyTooLarge');
    const response = await active.fetch(url.href, { method: request.method, headers, body, signal: controller.signal });
    const chunks = [];
    let size = 0;
    const reader = response.body?.getReader();
    if (reader) {
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          size += value.length;
          if (size > MaxBodyBytes) { await reader.cancel(); throw new Error('bodyTooLarge'); }
          chunks.push(value);
        }
      } finally { reader.releaseLock(); }
    }
    if (generation !== current) throw new Error('canceled');
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    return { status: response.status, headers: responseHeaders(response.headers), body: toBase64(bytes), bytes: size };
  } finally {
    clearTimeout(timeout);
    requests.delete(controller);
  }
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || message?.target !== 'engine') return;
  (async () => {
    switch (message.command) {
      case 'start': return start(message.gateway);
      case 'state': return { ...engineState };
      case 'stop': stop(); return {};
      case 'fetch': return fetchThroughTor(message.request);
      default: throw new Error('unknownCommand');
    }
  })().then(result => respond({ ok: true, result }), error => {
    console.error('[TorVPN engine]', error);
    respond({ ok: false, error: error.message || String(error) });
  });
  return true;
});
