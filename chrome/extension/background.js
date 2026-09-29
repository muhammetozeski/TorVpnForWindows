import { DefaultGateway, BlockProxy, MaxBodyBytes, validateGateway } from './config.js';

let state = { phase: 'disconnected', ip: '', requests: 0, downloaded: 0, failed: 0, error: '' };
let desired = false;
let epoch = 0;
let engineCreation;
let operation = Promise.resolve();
let cancelConnection;
const attached = new Set();
const attaching = new Map();
const events = [];
const waiting = [];
let activeRequests = 0;

// These transports bypass HTTP interception. Install before page scripts run, in all frames.
// The offscreen Tor engine has its own extension origin and retains its WebRTC transport.
const PageTransportPolicy = `(() => {
  for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'WebTransport', 'TCPSocket', 'UDPSocket']) {
    if (name in globalThis) Object.defineProperty(globalThis, name, { value: undefined, configurable: false, writable: false });
  }
})();`;

function log(action, detail = '') {
  const entry = { time: new Date().toISOString(), action, detail: String(detail).slice(0, 400) };
  events.push(entry);
  if (events.length > 60) events.shift();
  console.info('[TorVPN]', action, detail);
}

async function publish(patch = {}) {
  Object.assign(state, patch);
  await chrome.storage.session.set({ state, managedTabs: [...attached] });
  await chrome.action.setBadgeText({ text: ({ disconnected: '', connecting: '…', connected: 'TOR', error: '!' })[state.phase] });
  await chrome.action.setBadgeBackgroundColor({ color: state.phase === 'error' ? '#c74758' : '#7954bd' });
}

async function ensureEngine() {
  if (await chrome.offscreen.hasDocument()) return;
  engineCreation ??= chrome.offscreen.createDocument({ url: 'engine.html', reasons: ['WEB_RTC'],
    justification: 'Run the bundled Tor WebAssembly client and its WebRTC gateway transport.' });
  try { await engineCreation; } finally { engineCreation = undefined; }
}

async function engine(command, values = {}) {
  const response = await chrome.runtime.sendMessage({ target: 'engine', command, ...values });
  if (!response?.ok) throw new Error(response?.error || 'engineUnavailable');
  return response.result;
}

async function assertControl(setting, value) {
  const actual = await setting.get({ incognito: false });
  if (actual.levelOfControl !== 'controlled_by_this_extension' || JSON.stringify(actual.value) !== JSON.stringify(value)) {
    throw new Error('settingsConflict');
  }
}

async function blockDirectTraffic() {
  const proxy = await chrome.proxy.settings.get({ incognito: false });
  if (!['controllable_by_this_extension', 'controlled_by_this_extension'].includes(proxy.levelOfControl)) throw new Error('settingsConflict');
  await chrome.proxy.settings.set({ value: BlockProxy, scope: 'regular' });
  const effective = await chrome.proxy.settings.get({ incognito: false });
  if (effective.levelOfControl !== 'controlled_by_this_extension' || effective.value.mode !== 'fixed_servers' ||
      effective.value.rules?.singleProxy?.host !== '127.0.0.1' || effective.value.rules?.singleProxy?.port !== 1) throw new Error('settingsConflict');
  await chrome.privacy.network.networkPredictionEnabled.set({ value: false, scope: 'regular' });
  await assertControl(chrome.privacy.network.networkPredictionEnabled, false);
}

async function configureTarget(source, isPage = true) {
  await chrome.debugger.sendCommand(source, 'Runtime.enable');
  if (isPage) await chrome.debugger.sendCommand(source, 'Page.enable');
  await chrome.debugger.sendCommand(source, 'Network.enable');
  await chrome.debugger.sendCommand(source, 'Network.setBypassServiceWorker', { bypass: true });
  await chrome.debugger.sendCommand(source, 'Network.setCacheDisabled', { cacheDisabled: true });
  await chrome.debugger.sendCommand(source, 'Network.setBlockedURLs', { urls: ['ws://*', 'wss://*'] });
  await chrome.debugger.sendCommand(source, 'Fetch.enable', { patterns: [{ urlPattern: '*', requestStage: 'Request' }] });
  if (isPage) await chrome.debugger.sendCommand(source, 'Page.addScriptToEvaluateOnNewDocument', { source: PageTransportPolicy });
  const applied = await chrome.debugger.sendCommand(source, 'Runtime.evaluate', { expression: PageTransportPolicy });
  if (applied?.exceptionDetails) throw new Error('pagePolicyFailed');
  await chrome.debugger.sendCommand(source, 'Target.setAutoAttach', {
    autoAttach: true, waitForDebuggerOnStart: true, flatten: true,
    filter: [{ type: 'iframe' }, { type: 'worker' }, { type: 'shared_worker' }]
  });
}

async function attach(tabId) {
  if (attached.has(tabId)) return;
  if (attaching.has(tabId)) return attaching.get(tabId);
  const promise = (async () => {
    await chrome.debugger.attach({ tabId }, '1.3');
    attached.add(tabId);
    try { await configureTarget({ tabId }); }
    catch (error) { await chrome.debugger.detach({ tabId }); attached.delete(tabId); throw error; }
  })();
  attaching.set(tabId, promise);
  try { await promise; } finally { attaching.delete(tabId); }
}

async function start({ gateway = DefaultGateway } = {}) {
  gateway = validateGateway(gateway);
  const current = ++epoch;
  const controller = new AbortController();
  const cancel = () => { ++epoch; controller.abort(new Error('canceled')); };
  cancelConnection = cancel;
  desired = true;
  await chrome.storage.local.set({ desired, gateway });
  await publish({ phase: 'connecting', ip: '', requests: 0, downloaded: 0, failed: 0, error: '' });
  log('connect');
  try {
    await blockDirectTraffic();
    controller.signal.throwIfAborted();
    await ensureEngine();
    controller.signal.throwIfAborted();
    const connection = await Promise.race([engine('start', { gateway }), new Promise((_, reject) => {
      controller.signal.addEventListener('abort', () => reject(controller.signal.reason), { once: true });
    })]);
    if (epoch !== current) return;
    const tabs = await chrome.tabs.query({});
    for (const tab of tabs) {
      if (tab.incognito || !/^https?:/.test(tab.url || '')) continue;
      await attach(tab.id);
    }
    await publish({ phase: 'connected', ip: connection.ip });
    // Start fresh requests after interception and transport restrictions are in place.
    for (const tabId of attached) await chrome.tabs.reload(tabId, { bypassCache: true });
    log('connected', connection.ip);
  } catch (error) {
    log('connection failed', error.message);
    if (epoch === current) await publish({ phase: 'error', error: error.message });
    throw error;
  } finally {
    if (cancelConnection === cancel) cancelConnection = undefined;
  }
}

async function stop() {
  ++epoch;
  desired = false;
  await chrome.storage.local.set({ desired: false });
  const reloadTabs = [...attached];
  // Keep direct requests blocked until Tor and all interception sessions have stopped.
  if (await chrome.offscreen.hasDocument()) {
    try { await engine('stop'); } catch (error) { log('engine shutdown', error.message); }
    try { await chrome.offscreen.closeDocument(); } catch (error) { log('offscreen shutdown', error.message); }
  }
  for (const tabId of [...attached]) {
    try { await chrome.debugger.detach({ tabId }); }
    catch (error) { log('detach', error.message); }
    attached.delete(tabId);
  }
  await chrome.proxy.settings.clear({ scope: 'regular' });
  await chrome.privacy.network.networkPredictionEnabled.clear({ scope: 'regular' });
  await publish({ phase: 'disconnected', ip: '', error: '' });
  for (const tabId of reloadTabs) {
    try { await chrome.tabs.reload(tabId); } catch (error) { log('closed tab', error.message); }
  }
  log('disconnect');
}

async function forward(source, params) {
  const current = epoch;
  const requestId = params.requestId;
  const fail = () => chrome.debugger.sendCommand(source, 'Fetch.failRequest', { requestId, errorReason: 'BlockedByClient' });
  if (!desired || state.phase !== 'connected' || !/^https?:/.test(params.request.url)) return fail();
  if (activeRequests >= 6) await new Promise(resolve => waiting.push(resolve));
  activeRequests++;
  try {
    if (current !== epoch || state.phase !== 'connected') return await fail();
    const { request } = params;
    // Binary uploads are forwarded only when Chrome supplies the complete byte entries.
    let body;
    if (request.hasPostData) {
      if (request.postDataEntries?.length && request.postDataEntries.every(entry => typeof entry.bytes === 'string')) {
        const raw = request.postDataEntries.map(entry => atob(entry.bytes)).join('');
        if (raw.length > MaxBodyBytes) throw new Error('bodyTooLarge');
        body = btoa(raw);
      } else if (typeof request.postData === 'string' && !/multipart\/form-data/i.test(JSON.stringify(request.headers))) {
        const bytes = new TextEncoder().encode(request.postData);
        if (bytes.length > MaxBodyBytes) throw new Error('bodyTooLarge');
        let raw = '';
        for (const byte of bytes) raw += String.fromCharCode(byte);
        body = btoa(raw);
      } else throw new Error('unsupportedUpload');
    }
    const response = await engine('fetch', { request: { url: request.url, method: request.method, headers: request.headers, body } });
    if (current !== epoch || !desired) return await fail();
    await chrome.debugger.sendCommand(source, 'Fetch.fulfillRequest', {
      requestId, responseCode: response.status, responseHeaders: response.headers, body: response.body
    });
    await publish({ requests: state.requests + 1, downloaded: state.downloaded + response.bytes });
  } catch (error) {
    log('request blocked', error.message);
    try { await fail(); } catch (failure) { log('request ended', failure.message); }
    await publish({ failed: state.failed + 1 });
  } finally {
    activeRequests--;
    waiting.shift()?.();
  }
}

chrome.debugger.onEvent.addListener((source, method, params) => {
  if (method === 'Fetch.requestPaused') {
    void forward(source, params).catch(error => log('interception failed', error.message));
  } else if (method === 'Target.attachedToTarget') {
    const child = { tabId: source.tabId, sessionId: params.sessionId };
    void (async () => {
      await configureTarget(child, params.targetInfo.type === 'iframe');
      await chrome.debugger.sendCommand(child, 'Runtime.runIfWaitingForDebugger');
    })().catch(error => { log('child target blocked', error.message); });
  }
});

chrome.debugger.onDetach.addListener(source => {
  attached.delete(source.tabId);
  if (desired) log('tab detached; direct traffic remains blocked', source.tabId);
});
chrome.tabs.onRemoved.addListener(tabId => attached.delete(tabId));
chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
  if (!desired || state.phase !== 'connected' || tab.incognito || attached.has(tabId)) return;
  if (!/^https?:/.test(change.url || tab.url || '')) return;
  void attach(tabId).then(() => chrome.tabs.reload(tabId, { bypassCache: true }))
    .catch(error => { log('tab blocked', error.message); });
});

chrome.proxy.settings.onChange.addListener(value => {
  if (desired && value.levelOfControl !== 'controlled_by_this_extension') {
    void publish({ phase: 'error', error: 'settingsConflict' });
    log('proxy control lost');
  }
});

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || message?.target !== 'background') return;
  if (message.command === 'state') { respond({ ok: true, result: { ...state, tabs: attached.size, events } }); return; }
  if (message.command === 'disconnect') cancelConnection?.();
  operation = operation.then(async () => {
    switch (message.command) {
      case 'connect': await start(message.settings); break;
      case 'disconnect': await stop(); break;
      case 'newCircuit': await start(message.settings); break;
      default: throw new Error('unknownCommand');
    }
    return { ...state };
  });
  operation.then(result => respond({ ok: true, result }), error => respond({ ok: false, error: error.message }));
  operation = operation.catch(() => {});
  return true;
});

async function recover() {
  const saved = await chrome.storage.local.get(['desired', 'gateway']);
  if (!saved.desired) return publish();
  if (await chrome.offscreen.hasDocument()) {
    const running = await engine('state');
    if (running.phase === 'connected') {
      desired = true;
      await blockDirectTraffic();
      const session = await chrome.storage.session.get(['state', 'managedTabs']);
      const targets = await chrome.debugger.getTargets();
      for (const tabId of session.managedTabs || []) {
        if (targets.some(target => target.tabId === tabId && target.attached)) attached.add(tabId);
      }
      return publish({ ...session.state, phase: 'connected', ip: running.ip });
    }
  }
  await start({ gateway: saved.gateway });
}
// Runs when Chrome restarts or revives the worker. Persistent proxy settings keep requests blocked
// until a new, verified Tor session is ready.
operation = recover().catch(error => log('recovery failed', error.message));
