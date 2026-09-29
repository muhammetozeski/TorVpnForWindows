import { DefaultGateway } from './config.js';

const Text = {
  tr: {
    subtitle: 'Chrome için Tor bağlantısı', preview: 'DENEYSEL', disconnected: 'Bağlantı kapalı',
    connecting: 'Tor’a bağlanıyor', connected: 'Tor’a bağlı', error: 'Bağlantı durdu',
    disconnectedDetail: 'Hazır olduğunda bu Chrome profilini bağla.', connectingDetail: 'Tor devresi kurulurken web istekleri bekler.',
    connectedDetail: 'Web istekleri eklentideki Tor motorundan geçiyor.', errorDetail: 'Yeniden bağlan veya bağlantıyı kapat.',
    connect: 'Tor’a bağlan', disconnect: 'Bağlantıyı kes', renew: 'Yeni Tor bağlantısı', requests: 'İstek', traffic: 'İndirilen', tabs: 'Sekme',
    settings: 'Ayarlar ve kapsam', language: 'Dil', gateway: 'Geçiş sunucusu',
    gatewayHint: 'Varsayılan adres tor-js projesinin herkese açık deneme sunucusudur. Tor motoru ve şifreleme bu eklentide çalışır.',
    limits: 'Normal web sayfaları içindir. Chrome hata ayıklama çubuğu gösterir. Sesli/görüntülü aramalar ve WebSocket bağlantıları kapalıdır. Tek istek sınırı 16 MB. Gizli pencere kapsam dışıdır.',
    localEngine: 'JavaScript + WebAssembly · Ek program yok',
    settingsConflict: 'Bağlantı ayarı başka bir eklenti veya yönetici tarafından kontrol ediliyor.',
    bootstrapTimeout: 'Tor bağlantısı süre içinde kurulamadı. Yeniden bağlanmayı dene.',
    torCheckFailed: 'Tor çıkışı doğrulanamadı.', invalidGateway: 'Geçiş sunucusunun adresini kontrol et.',
    engineUnavailable: 'Tor motoruna ulaşılamadı.', canceled: 'Bağlantı iptal edildi.'
  },
  en: {
    subtitle: 'Tor connection for Chrome', preview: 'EXPERIMENTAL', disconnected: 'Disconnected',
    connecting: 'Connecting to Tor', connected: 'Connected to Tor', error: 'Connection stopped',
    disconnectedDetail: 'Connect this Chrome profile when you are ready.', connectingDetail: 'Web requests wait while Tor builds a circuit.',
    connectedDetail: 'Web requests pass through the bundled Tor engine.', errorDetail: 'Reconnect or disconnect to restore browsing.',
    connect: 'Connect to Tor', disconnect: 'Disconnect', renew: 'New Tor connection', requests: 'Requests', traffic: 'Downloaded', tabs: 'Tabs',
    settings: 'Settings and scope', language: 'Language', gateway: 'Gateway',
    gatewayHint: 'The default address is the tor-js public demonstration gateway. The Tor client and encryption run inside this extension.',
    limits: 'For regular web pages. Chrome displays a debugging banner. Voice/video calls and WebSockets are blocked. Each request is limited to 16 MB. Incognito is outside the scope.',
    localEngine: 'JavaScript + WebAssembly · No helper app',
    settingsConflict: 'Another extension or administrator controls a required browser setting.',
    bootstrapTimeout: 'Tor did not connect in time. Try reconnecting.', torCheckFailed: 'Could not verify a Tor exit.',
    invalidGateway: 'Check the gateway address.', engineUnavailable: 'The Tor engine is unavailable.', canceled: 'Connection canceled.'
  }
};
const $ = id => document.getElementById(id);
const saved = await chrome.storage.local.get(['language', 'gateway']);
let language = saved.language || (navigator.language.startsWith('tr') ? 'tr' : 'en');
let state = { phase: 'disconnected' };
let error = '';
$('language').value = language;
$('gateway').value = saved.gateway || DefaultGateway;

function render() {
  const words = Text[language];
  document.documentElement.lang = language;
  for (const element of document.querySelectorAll('[data-i18n]')) element.textContent = words[element.dataset.i18n];
  $('status').textContent = words[state.phase];
  $('description').textContent = words[state.phase + 'Detail'];
  $('dot').className = 'dot ' + state.phase;
  $('ip').textContent = state.ip || '';
  $('toggle').textContent = state.phase === 'disconnected' ? words.connect : words.disconnect;
  $('renew').disabled = !['connected', 'error'].includes(state.phase);
  $('gateway').disabled = state.phase !== 'disconnected';
  $('requests').textContent = String(state.requests || 0);
  $('traffic').textContent = `${((state.downloaded || 0) / 1024 / 1024).toFixed(1)} MB`;
  $('tabs').textContent = String(state.tabs || 0);
  const message = error || state.error;
  $('error').hidden = !message;
  $('error').textContent = words[message] || message || '';
}

async function refresh() {
  try {
    const response = await chrome.runtime.sendMessage({ target: 'background', command: 'state' });
    if (response?.ok) state = response.result;
  } catch (failure) { error = failure.message; }
  render();
}

async function act(command) {
  error = '';
  const settings = { gateway: $('gateway').value.trim() };
  if (command !== 'disconnect') state = { ...state, phase: 'connecting', error: '' };
  render();
  try {
    const response = await chrome.runtime.sendMessage({ target: 'background', command, settings });
    if (!response?.ok && response?.error !== 'canceled') error = response?.error || 'engineUnavailable';
  } catch (failure) { error = failure.message; }
  await refresh();
}
$('toggle').addEventListener('click', () => { void act(state.phase === 'disconnected' ? 'connect' : 'disconnect'); });
$('renew').addEventListener('click', () => { void act('newCircuit'); });
$('language').addEventListener('change', async () => {
  language = $('language').value;
  await chrome.storage.local.set({ language });
  render();
});
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'session' && changes.state) void refresh(); });
await refresh();
