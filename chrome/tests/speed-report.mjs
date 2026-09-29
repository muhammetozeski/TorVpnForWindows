import fs from 'node:fs/promises';

const labels = ['direct-browser-1m', 'gateway-probe', 'gateway-repeat-1', 'gateway-repeat-2',
  'native-direct-tor', 'native-snowflake-primary', 'native-snowflake-secondary',
  'native-snowflake-secondary-warm', 'native-snowflake-parallel-3', 'native-obfs4-primary',
  'native-obfs4-secondary', 'native-meek', 'native-meek-warm', 'snowflake-ws-primary',
  'snowflake-ws-primary-retry', 'snowflake-ws-secondary', 'snowflake-webrtc-1',
  'snowflake-webrtc-2', 'snowflake-webrtc-3', 'webtunnel-capability'];
const runs = await Promise.all(labels.map(async label => JSON.parse(await fs.readFile(`build/speed/${label}.json`, 'utf8'))));
const expectedHash = '7293c75b10fa19fef6495c1127b419d62ce717d954eb1351d2c3222f606412a0';
for (const run of runs.filter(run => run.ok)) {
  for (const sample of run.samples) {
    if (sample.bytes !== 1048576 || sample.sha256 !== expectedHash) throw new Error('Invalid sample: ' + run.label);
  }
}

function stats(selected) {
  const samples = selected.flatMap(run => run.samples);
  const speeds = samples.map(sample => sample.bytes / sample.seconds / 1000).sort((a, b) => a - b);
  return { samples: samples.length,
    meanKBps: samples.reduce((sum, sample) => sum + sample.bytes, 0) / samples.reduce((sum, sample) => sum + sample.seconds, 0) / 1000,
    minKBps: speeds[0], maxKBps: speeds.at(-1),
    medianKBps: (speeds[Math.floor((speeds.length - 1) / 2)] + speeds[Math.ceil((speeds.length - 1) / 2)]) / 2 };
}
const summary = {
  gatewayAll: stats(runs.filter(run => run.label.startsWith('gateway-'))),
  gatewayRepeats: stats(runs.filter(run => run.label.startsWith('gateway-repeat-')))
};
for (const label of ['native-direct-tor', 'native-snowflake-primary', 'native-snowflake-secondary-warm', 'native-meek-warm']) {
  summary[label] = stats(runs.filter(run => run.label === label));
}
const parallel = runs.find(run => run.label === 'native-snowflake-parallel-3');
const parallelBytes = parallel.samples.reduce((sum, sample) => sum + sample.bytes, 0);
summary.parallel = { connections: parallel.activeConnections, bytes: parallelBytes, seconds: parallel.seconds,
  kBps: parallelBytes / parallel.seconds / 1000 };
const report = { measuredAt: new Date().toISOString(),
  file: 'https://github.com/szalony9szymek/large/releases/download/free/large', fileBytes: 2093168814,
  sampleBytes: 1048576, sampleSha256: expectedHash,
  units: 'kB/s = 1000 bytes/second; request timings include redirects and request connection setup', summary, runs };
await fs.writeFile('build/speed/report.json', JSON.stringify(report, null, 2));
const rows = [
  ['Gateway, all 7 samples / 3 sessions', 'Headless Chrome + tor-js 0.4.2', summary.gatewayAll],
  ['Gateway, final 6 samples / 2 sessions', 'Headless Chrome + tor-js 0.4.2', summary.gatewayRepeats],
  ['Direct Tor', 'Local tor.exe', summary['native-direct-tor']],
  ['Snowflake bridge 1', 'Local tor.exe + lyrebird', summary['native-snowflake-primary']],
  ['Snowflake bridge 2, directory cache ready', 'Local tor.exe + lyrebird', summary['native-snowflake-secondary-warm']],
  ['meek, directory cache ready', 'Local tor.exe + lyrebird', summary['native-meek-warm']]
];
const table = rows.map(([name, runtime, value]) => `| ${name} | ${runtime} | ${value.samples} | ${value.meanKBps.toFixed(1)} | ${value.minKBps.toFixed(1)}–${value.maxKBps.toFixed(1)} |`).join('\n');
await fs.writeFile('build/speed/report.md', `# Tor transport download measurements — 2026-09-29

All completed samples requested bytes 0–1048575 of the supplied GitHub release asset.
Every successful response contained exactly 1,048,576 bytes with SHA-256 ${expectedHash}.
The asset's complete size was 2,093,168,814 bytes. It was not downloaded in full.

Speeds use decimal kilobytes per second. The mean is total completed bytes divided by total
request time. Time includes redirects and opening the request's connections; Tor bootstrap is
recorded separately. Failed requests have no fabricated speed. Individual tests ran sequentially;
only the explicitly parallel experiment used concurrent downloads.

| Method | Runtime | Samples | Mean kB/s | Range kB/s |
|---|---|---:|---:|---:|
${table}

Three independent native Snowflake sessions all connected, with distinct Tor exit addresses.
They used two published bridge identities: two sessions on bridge 1 and one on bridge 2.
The three concurrent 1 MiB requests transferred ${parallelBytes.toLocaleString('en-US')} bytes in
${parallel.seconds.toFixed(3)} seconds: **${summary.parallel.kBps.toFixed(1)} kB/s aggregate**.
This is total bytes divided by the time until the last request completed, not the sum of
the three individual speeds. The requests downloaded the same range; no file assembly was tested.

The gateway was 170.64.236.147:12298. Its first sample took 33.476 seconds; the six follow-up
samples took 9.022–11.775 seconds. Results are end-to-end observations with changing Tor circuits,
not an isolated measurement of gateway CPU or link capacity.

## Connection results and scope

- Browser Snowflake WebRTC: three attempts, no completed circuit/download.
- Browser WebSocket: wss://snowflake.torproject.net/ was tried twice; wss://snowflake.bamsoftware.com/
  was tried once. Neither produced a usable circuit with webtor-rs 0.5.7.
- WebTunnel: a runtime capability probe failed before a usable channel; the upstream WASM code
  explicitly rejects WebTunnel. No real WebTunnel server speed was measured.
- Native obfs4: 51.222.13.177:80 and 209.148.46.65:443 failed to bootstrap within 120 seconds,
  stopping at 2% and 10% respectively.
- Native Snowflake bridge 1: cold bootstrap 113.539 seconds. Bridge 2 stopped at 50% after
  120 seconds cold, but bootstrapped in 9.490 seconds with a verified directory cache present.
- Native meek: cold bootstrap stopped at 50% after 120 seconds; with the directory cache present,
  bootstrap completed in 14.022 seconds and the 1 MiB download took 36.055 seconds.
- Parallel native Snowflake: directory cache was ready; bootstrap times were 8.287, 11.525,
  and 8.301 seconds. Guard state and identities were not copied between clients.

Browser tests used separate headless Chrome profiles and did not change the personal browser.
Native tests used project-local Tor data and loopback SOCKS listeners; no system tunnel, routing,
or Windows firewall settings were installed. The extension's production files were unchanged.

The browser test fixture redirected only the published webtor-rs WASM's retired directory URLs
from igor53627.github.io to ethereum.github.io. The downloaded runtime was not edited.
That removed the initial HTTP 404, but did not make its Snowflake circuits complete.

Complete measurements and error messages are in [report.json](report.json).
`);
console.log(JSON.stringify(summary, null, 2));
