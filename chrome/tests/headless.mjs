import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import fs from 'node:fs/promises';
import path from 'node:path';

/** Control only a dedicated headless Chrome process through its private debugging pipes. */
export class HeadlessChrome extends EventEmitter {
  nextId = 1;
  pending = new Map();
  buffer = '';

  static async launch(profileDir) {
    await fs.mkdir(profileDir, { recursive: true });
    const browser = new HeadlessChrome();
    const executable = process.env.TORVPN_TEST_CHROME || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
    browser.process = spawn(executable, [
      '--headless=new', '--remote-debugging-pipe', '--enable-unsafe-extension-debugging',
      `--user-data-dir=${path.resolve(profileDir)}`, '--no-first-run', '--no-default-browser-check',
      '--disable-background-networking', '--disable-component-update', 'about:blank'
    ], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'] });
    browser.process.stderr.on('data', chunk => browser.emit('log', chunk.toString()));
    browser.process.stdio[4].on('data', chunk => browser.receive(chunk));
    browser.process.on('error', error => browser.fail(error));
    browser.process.on('exit', code => browser.fail(new Error(`Headless Chrome exited: ${code}`)));
    await browser.send('Browser.getVersion');
    return browser;
  }

  receive(chunk) {
    this.buffer += chunk.toString();
    let end;
    while ((end = this.buffer.indexOf('\0')) >= 0) {
      const message = JSON.parse(this.buffer.slice(0, end));
      this.buffer = this.buffer.slice(end + 1);
      if (message.id) {
        const pending = this.pending.get(message.id);
        if (!pending) continue;
        this.pending.delete(message.id);
        clearTimeout(pending.timer);
        if (message.error) pending.reject(new Error(JSON.stringify(message.error)));
        else pending.resolve(message.result);
      } else this.emit('event', message);
    }
  }

  send(method, params = {}, sessionId) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Chrome command timed out: ${method}`));
      }, 30000);
      this.pending.set(id, { resolve, reject, timer });
      this.process.stdio[3].write(JSON.stringify({ id, method, params, sessionId }) + '\0');
    });
  }

  fail(error) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.pending.clear();
  }

  async page(url) {
    const { targetId } = await this.send('Target.createTarget', { url });
    const { sessionId } = await this.send('Target.attachToTarget', { targetId, flatten: true });
    return { targetId, sessionId };
  }

  async evaluate(sessionId, expression) {
    const response = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId);
    if (response.exceptionDetails) throw new Error(JSON.stringify(response.exceptionDetails));
    return response.result.value;
  }

  async close() {
    if (this.process.exitCode !== null) return;
    const exit = new Promise(resolve => this.process.once('exit', resolve));
    try { await this.send('Browser.close'); } catch { /* The pipe can close before its response. */ }
    const timer = setTimeout(() => this.process.kill(), 5000);
    try { await exit; } finally { clearTimeout(timer); }
  }
}
