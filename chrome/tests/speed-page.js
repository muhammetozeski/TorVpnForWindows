const FileUrl = 'https://github.com/szalony9szymek/large/releases/download/free/large';
const Gateway = '170.64.236.147:12298:uEiBHwUMNRTetrbqScahm81Di57Xv2OphNrx-CurJGOq3ww';
const NativeFetch = globalThis.fetch.bind(globalThis);
// The upstream project moved organizations; its published WASM still requests the retired URL.
// Keep the runtime untouched and map only the two directory snapshots in this test fixture.
globalThis.fetch = (input, options) => {
  const url = typeof input === 'string' ? input : input?.url;
  const oldBase = 'https://igor53627.github.io/webtor-rs/';
  if (url === oldBase + 'consensus.txt.br' || url === oldBase + 'microdescriptors.txt.br') {
    return NativeFetch(url.replace(oldBase, 'https://ethereum.github.io/webtor-rs/'), options);
  }
  return NativeFetch(input, options);
};
const peers = [];
const NativePeer = globalThis.RTCPeerConnection;
globalThis.RTCPeerConnection = class extends NativePeer {
  constructor(...args) { super(...args); peers.push(this); }
};
globalThis.progress = { phase: 'idle' };
globalThis.outcome = null;
const logs = [];
const log = (...values) => {
  logs.push(values.map(value => typeof value === 'string' ? value : JSON.stringify(value)).join(' ').slice(0, 1500));
  if (logs.length > 160) logs.shift();
};
for (const level of ['log', 'info', 'warn', 'error', 'debug']) console[level] = (...values) => log(level, ...values);

function bounded(promise, milliseconds, label) {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(label + ' timed out')), milliseconds);
  })]).finally(() => clearTimeout(timer));
}

function getHeader(headers, key) {
  const entries = typeof headers?.entries === 'function' ? [...headers.entries()] : Object.entries(headers || {});
  return entries.find(([name]) => name.toLowerCase() === key.toLowerCase())?.[1] ?? null;
}

async function readBytes(response, maximum) {
  if (response.body instanceof Uint8Array) {
    if (response.body.length > maximum) throw new Error('Server ignored the requested byte range.');
    return response.body;
  }
  const reader = response.body.getReader();
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.length;
      if (size > maximum) { await reader.cancel(); throw new Error('Server ignored the requested byte range.'); }
      chunks.push(value);
      progress.received = size;
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return bytes;
}

async function requestFollowingRedirects(request, url, headers = {}) {
  for (let redirect = 0; redirect < 6; redirect++) {
    const response = await request(url, headers);
    const location = getHeader(response.headers, 'location');
    if (![301, 302, 303, 307, 308].includes(response.status) || !location) return response;
    if (response.body?.cancel) await response.body.cancel();
    const next = new URL(location, url);
    if (next.protocol !== 'https:') throw new Error('Unexpected non-HTTPS redirect.');
    url = next.href;
  }
  throw new Error('Too many download redirects.');
}

async function peerAddresses() {
  const result = [];
  for (const peer of peers) {
    try {
      const stats = await peer.getStats();
      for (const item of stats.values()) {
        if (item.type !== 'candidate-pair' || item.state !== 'succeeded' || !item.nominated) continue;
        const remote = stats.get(item.remoteCandidateId);
        result.push({ address: remote?.address, port: remote?.port, protocol: remote?.protocol });
      }
    } catch (error) { log(error.message); }
  }
  return result;
}

async function runCase(config) {
  const begin = performance.now();
  let client;
  let request;
  const result = { label: config.label, kind: config.kind,
    endpoint: config.endpoint || (config.kind === 'gateway' ? Gateway : config.kind === 'direct' ? 'direct' : 'Snowflake broker'), samples: [] };
  try {
    progress = { phase: 'bootstrap', label: config.label };
    if (config.kind === 'direct') {
      request = (url, headers) => fetch(url, { headers, cache: 'no-store', signal: AbortSignal.timeout(60000) });
    } else if (config.kind === 'gateway') {
      const module = await import('./tor/entryPoints/wasm-file/index.js');
      client = new module.TorClient({ gateway: config.endpoint || Gateway, logLevel: 'info',
        log: new module.Log({ rawLog: (level, ...values) => log(level, ...values) }) });
      await bounded(client.ready(), 90000, 'Tor bootstrap');
      request = (url, headers) => client.fetch(url, { headers, signal: AbortSignal.timeout(60000) });
    } else {
      const module = await import('./webtor/webtor_wasm.js');
      await module.default();
      module.setLogCallback(log);
      let options;
      if (config.kind === 'snowflake-rtc') options = module.TorClientOptions.snowflakeWebRtc();
      else if (config.kind === 'webtunnel') options = module.TorClientOptions.webtunnel(config.endpoint, config.fingerprint);
      else options = new module.TorClientOptions(config.endpoint);
      options = options.withConnectionTimeout(30000).withCircuitTimeout(60000);
      client = await bounded(new module.TorClient(options), 90000, 'Tor bootstrap');
      await bounded(client.waitForCircuit(), 90000, 'Tor circuit');
      result.relays = await client.getCircuitRelays();
      request = (url, headers) => client.request('GET', url, headers, undefined, 60000);
    }
    result.bootstrapSeconds = (performance.now() - begin) / 1000;
    if (config.kind !== 'direct') {
      progress.phase = 'verify';
      const response = await bounded(requestFollowingRedirects(request, 'https://check.torproject.org/api/ip'), 60000, 'Tor verification');
      const ip = typeof response.json === 'function' ? await response.json() : JSON.parse(new TextDecoder().decode(response.body));
      if (ip.IsTor !== true) throw new Error('Tor exit was not verified: ' + JSON.stringify(ip));
      result.exitIp = ip.IP;
    }
    result.remotePeers = await peerAddresses();
    for (let sample = 0; sample < (config.samples || 1); sample++) {
      progress = { phase: 'download', label: config.label, sample, received: 0 };
      const size = config.bytes || 1048576;
      const started = performance.now();
      const response = await bounded(requestFollowingRedirects(request, FileUrl,
        { Range: `bytes=0-${size - 1}`, 'Accept-Encoding': 'identity' }), 65000, 'Download headers');
      if (response.status !== 206) throw new Error(`Expected partial content, received ${response.status}`);
      const range = getHeader(response.headers, 'content-range');
      if (range !== `bytes 0-${size - 1}/2093168814`) throw new Error('Unexpected Content-Range: ' + range);
      const bytes = await bounded(readBytes(response, size), 65000, 'Download body');
      const seconds = (performance.now() - started) / 1000;
      if (bytes.length !== size) throw new Error(`Short download: ${bytes.length}/${size}`);
      const hash = await crypto.subtle.digest('SHA-256', bytes);
      result.samples.push({ bytes: size, seconds, mibPerSecond: size / 1048576 / seconds,
        sha256: [...new Uint8Array(hash)].map(value => value.toString(16).padStart(2, '0')).join('') });
    }
    result.ok = true;
  } catch (error) {
    result.ok = false;
    result.error = error?.message || (typeof error === 'string' ? error : JSON.stringify(error));
    result.failedPhase = progress.phase;
  } finally {
    result.elapsedSeconds = (performance.now() - begin) / 1000;
    result.remotePeers = await peerAddresses();
    result.logs = logs;
    outcome = result;
    progress.phase = 'done';
    try { await client?.close(); } catch (error) { log(error.message); }
  }
}

globalThis.startCase = config => { void runCase(config); return true; };
