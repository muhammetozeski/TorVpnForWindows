import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import { HeadlessChrome } from './headless.mjs';

const browser = await HeadlessChrome.launch(await fs.mkdtemp('build/integration-profile-'));
let popup;
const report = { started: new Date().toISOString(), checks: [] };
try {
  const extension = await browser.send('Extensions.loadUnpacked', { path: path.resolve('publish/TorVpnChrome') });
  popup = await browser.page(`chrome-extension://${extension.id}/popup.html`);
  await browser.send('Runtime.enable', {}, popup.sessionId);
  await delay(300);
  const request = command => browser.evaluate(popup.sessionId,
    `chrome.runtime.sendMessage({target:'background',command:${JSON.stringify(command)}})`);
  await browser.evaluate(popup.sessionId, `globalThis.connectionResult = null;
    chrome.runtime.sendMessage({target:'background',command:'connect'}).then(result => globalThis.connectionResult = result); true;`);
  let state;
  let phase;
  const deadline = Date.now() + 100000;
  while (Date.now() < deadline) {
    await delay(1500);
    state = (await request('state')).result;
    if (phase !== state.phase) { phase = state.phase; console.log('state', JSON.stringify(state)); }
    if (['connected', 'error'].includes(state.phase)) break;
  }
  report.connection = state;
  assert.equal(state.phase, 'connected');
  report.checks.push('Tor client bootstraps with direct HTTP traffic blocked');

  const page = await browser.page('https://check.torproject.org/api/ip');
  let body = '';
  const pageDeadline = Date.now() + 60000;
  while (Date.now() < pageDeadline) {
    await delay(1500);
    body = await browser.evaluate(page.sessionId, 'document.body?.innerText || ""');
    if (body.includes('IsTor')) break;
    state = (await request('state')).result;
    console.log('page', JSON.stringify({body:body.slice(0,100),state}));
  }
  const check = JSON.parse(body);
  assert.equal(check.IsTor, true);
  report.page = check;
  report.checks.push('A normal Chrome HTTPS tab reports IsTor=true');
  console.log('Tor tab', check);

  await browser.send('Page.navigate', { url: 'https://example.com' }, page.sessionId);
  const htmlDeadline = Date.now() + 45000;
  while (Date.now() < htmlDeadline) {
    await delay(1000);
    if (await browser.evaluate(page.sessionId, 'document.title === "Example Domain"')) break;
  }
  const transports = await browser.evaluate(page.sessionId,
    `({title:document.title,peer:typeof RTCPeerConnection,transport:typeof WebTransport,socket:typeof WebSocket})`);
  console.log('Page transport policy', transports);
  assert.equal(transports.peer, 'undefined');
  assert.equal(transports.transport, 'undefined');
  report.checks.push('Page WebRTC and WebTransport entry points are disabled');

  await browser.send('Emulation.setDeviceMetricsOverride', { width: 360, height: 540, deviceScaleFactor: 1, mobile: false }, popup.sessionId);
  const screenshot = await browser.send('Page.captureScreenshot', { format: 'png' }, popup.sessionId);
  await fs.writeFile('build/popup.png', Buffer.from(screenshot.data, 'base64'));

  const settingsBefore = await browser.evaluate(popup.sessionId, 'chrome.proxy.settings.get({incognito:false})');
  assert.equal(settingsBefore.levelOfControl, 'controlled_by_this_extension');
  await browser.evaluate(popup.sessionId, `chrome.runtime.sendMessage({target:'engine',command:'stop'})`);
  const failed = await browser.evaluate(page.sessionId,
    `fetch('https://check.torproject.org/api/ip?closed=' + Date.now()).then(()=>'unexpected success',()=>'blocked')`);
  assert.equal(failed, 'blocked');
  report.checks.push('Stopping the WASM engine fails web requests without direct fallback');

  const disconnected = await request('disconnect');
  assert.equal(disconnected.ok, true);
  const settingsAfter = await browser.evaluate(popup.sessionId, 'chrome.proxy.settings.get({incognito:false})');
  assert.notEqual(settingsAfter.levelOfControl, 'controlled_by_this_extension');
  report.checks.push('Disconnect releases this extension’s proxy setting');
  await browser.evaluate(popup.sessionId, `chrome.runtime.sendMessage({target:'background',command:'connect'}); true`);
  while ((await request('state')).result.phase !== 'connecting') await delay(50);
  const cancelStart = Date.now();
  const canceled = await request('disconnect');
  assert.equal(canceled.ok, true);
  assert.ok(Date.now() - cancelStart < 5000, 'Cancel must not wait for Tor bootstrap');
  assert.equal((await request('state')).result.phase, 'disconnected');
  report.checks.push('Disconnect cancels bootstrap without waiting for its deadline');
  report.state = (await request('state')).result;
  console.log(JSON.stringify(report, null, 2));
} catch (error) {
  report.error = error.stack;
  console.error(error);
  if (popup) {
    report.lastState = await browser.evaluate(popup.sessionId,
      `chrome.runtime.sendMessage({target:'background',command:'state'})`).catch(() => null);
    console.log('last state', JSON.stringify(report.lastState));
  }
  process.exitCode = 1;
} finally {
  await fs.writeFile('build/browser-report.json', JSON.stringify(report, null, 2));
  await browser.close();
}
