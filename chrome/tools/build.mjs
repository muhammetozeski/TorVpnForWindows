import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'publish', 'TorVpnChrome');
const dependency = JSON.parse(await fs.readFile(path.join(root, 'third-party.json'), 'utf8'));
for (const file of dependency.files) {
  const data = await fs.readFile(path.join(root, file.path));
  if (createHash('sha256').update(data).digest('hex') !== file.sha256) throw new Error(`Bundled dependency changed: ${file.path}`);
}
await fs.mkdir(output, { recursive: true });
await fs.cp(path.join(root, 'extension'), output, { recursive: true });
const manifest = JSON.parse(await fs.readFile(path.join(output, 'manifest.json'), 'utf8'));
const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
console.log(JSON.stringify({ output, extensionId: [...digest].map(value => String.fromCharCode(97 + Number.parseInt(value, 16))).join('') }));
