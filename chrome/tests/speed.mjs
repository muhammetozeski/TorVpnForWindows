import fs from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { HeadlessChrome } from './headless.mjs';

const work = path.resolve('build/speed');
const extensionPath = path.join(work, 'extension');
await fs.mkdir(extensionPath, { recursive: true });
await fs.cp('extension/tor', path.join(extensionPath, 'tor'), { recursive: true });
await fs.cp(path.join(work, 'webtor'), path.join(extensionPath, 'webtor'), { recursive: true });
await fs.copyFile('tests/speed-page.js', path.join(extensionPath, 'speed-page.js'));
await fs.writeFile(path.join(extensionPath, 'manifest.json'), JSON.stringify({
  manifest_version: 3, name: 'TorVPN speed measurement', version: '0.0.1',
  host_permissions: ['<all_urls>'],
  content_security_policy: { extension_pages: "script-src 'self' 'wasm-unsafe-eval'; object-src 'none'" }
}));
await fs.writeFile(path.join(extensionPath, 'speed.html'), '<!doctype html><meta charset="utf-8"><script type="module" src="speed-page.js"></script>');

const cases = JSON.parse(await fs.readFile(process.argv[2] || 'build/speed/cases.json', 'utf8'));
for (const config of cases) {
  const browser = await HeadlessChrome.launch(await fs.mkdtemp(path.join(work, 'profile-')));
  const started = Date.now();
  let result;
  try {
    const extension = await browser.send('Extensions.loadUnpacked', { path: extensionPath });
    const page = await browser.page(`chrome-extension://${extension.id}/speed.html`);
    for (let attempt = 0; attempt < 30; attempt++) {
      if (await browser.evaluate(page.sessionId, 'typeof startCase === "function"')) break;
      await delay(100);
    }
    await browser.evaluate(page.sessionId, `startCase(${JSON.stringify(config)})`);
    let previous = '';
    while (Date.now() - started < 220000) {
      await delay(2000);
      const snapshot = await browser.evaluate(page.sessionId, '({progress,outcome})');
      const current = JSON.stringify(snapshot.progress);
      if (current !== previous) { console.log(config.label, current); previous = current; }
      if (snapshot.outcome) { result = snapshot.outcome; break; }
    }
    if (!result) result = { label: config.label, ok: false, error: 'Overall test deadline exceeded.' };
  } catch (error) { result = { label: config.label, ok: false, error: error.message }; }
  finally { await browser.close(); }
  await fs.writeFile(path.join(work, config.label + '.json'), JSON.stringify(result, null, 2));
  const { logs, ...summary } = result;
  console.log('RESULT', JSON.stringify(summary));
}
