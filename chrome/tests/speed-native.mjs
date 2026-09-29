import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';

const execute = promisify(execFile);
const work = path.resolve('build/speed');
const torDir = path.resolve('../payload/tor');
const fileUrl = 'https://github.com/szalony9szymek/large/releases/download/free/large';
const expectedHash = '7293c75b10fa19fef6495c1127b419d62ce717d954eb1351d2c3222f606412a0';
const source = await fs.readFile('../src/TorVpnForWindows/Config/BuiltInBridges.cs', 'utf8');
const bridgeLines = [...source.matchAll(/^\s*"((?:snowflake|obfs4|meek_lite) .+)"[,]?\s*$/gm)].map(match => match[1]);

async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}

async function boot(config) {
  const directory = await fs.mkdtemp(path.join(work, 'native-'));
  if (config.seedDirectory) {
    for (const name of ['cached-certs', 'cached-microdesc-consensus', 'cached-microdescs', 'cached-microdescs.new']) {
      try { await fs.copyFile(path.join(config.seedDirectory, name), path.join(directory, name)); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
  }
  const socksPort = await freePort();
  const lines = [
    'ClientOnly 1', 'AvoidDiskWrites 1', 'Log notice stdout', 'GeoIPFile ' + JSON.stringify(path.join(torDir, 'data/geoip').replaceAll('\\', '/')),
    'GeoIPv6File ' + JSON.stringify(path.join(torDir, 'data/geoip6').replaceAll('\\', '/')),
    'DataDirectory ' + JSON.stringify(directory.replaceAll('\\', '/')),
    `SocksPort 127.0.0.1:${socksPort}`, 'ControlPort 0', 'DNSPort 0', `__OwningControllerProcess ${process.pid}`
  ];
  if (config.bridge) {
    const bridge = bridgeLines.filter(line => line.startsWith(config.bridge + ' '))[config.bridgeIndex || 0];
    if (!bridge) throw new Error('Unknown bridge selection.');
    lines.push('UseBridges 1', 'ClientTransportPlugin obfs4,snowflake,meek_lite exec ./pluggable_transports/lyrebird.exe', 'Bridge ' + bridge);
  }
  const configFile = path.join(directory, 'torrc');
  await fs.writeFile(configFile, lines.join('\n') + '\n');
  const child = spawn(path.join(torDir, 'tor.exe'), ['-f', configFile], {
    cwd: torDir, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  const logs = [];
  const began = performance.now();
  let bootstrap = 0;
  const ready = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`Bootstrap deadline exceeded at ${bootstrap}%`)), 120000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => { clearTimeout(timer); reject(new Error(`Tor exited: ${code}`)); });
    for (const stream of [child.stdout, child.stderr]) {
      let buffer = '';
      stream.on('data', chunk => {
        buffer += chunk.toString();
        let end;
        while ((end = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, end).trim();
          buffer = buffer.slice(end + 1);
          logs.push(line);
          const match = /Bootstrapped (\d+)%/.exec(line);
          if (!match) continue;
          bootstrap = Number(match[1]);
          console.log(config.label, 'bootstrap', bootstrap);
          if (bootstrap === 100) { clearTimeout(timer); resolve(); }
        }
      });
    }
  });
  const stop = async () => {
    if (child.exitCode === null) {
      try { await execute('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true }); }
      catch (error) { if (child.exitCode === null) throw error; }
    }
    await fs.writeFile(path.join(directory, 'tor.log'), logs.join('\n'));
  };
  try { await ready; }
  catch (error) { await stop(); throw error; }
  return { socksPort, directory, bootstrapSeconds: (performance.now() - began) / 1000, stop };
}

async function measure(session, name) {
  const target = path.join(session.directory, name + '.bin');
  const args = ['--location', '--silent', '--show-error', '--socks5-hostname', `127.0.0.1:${session.socksPort}`,
    '--range', '0-1048575', '--max-time', '60', '--output', target,
    '--write-out', '{"http":%{http_code},"bytes":%{size_download},"firstByteSeconds":%{time_starttransfer},"seconds":%{time_total},"bytesPerSecond":%{speed_download}}', fileUrl];
  const { stdout } = await execute('curl.exe', args, { windowsHide: true, timeout: 65000 });
  const result = JSON.parse(stdout);
  const data = await fs.readFile(target);
  result.sha256 = createHash('sha256').update(data).digest('hex');
  if (result.http !== 206 || data.length !== 1048576 || result.sha256 !== expectedHash) throw new Error('Download verification failed.');
  result.mibPerSecond = data.length / 1048576 / result.seconds;
  return result;
}

async function checkIp(session) {
  const { stdout } = await execute('curl.exe', ['--silent', '--show-error', '--max-time', '30', '--socks5-hostname',
    `127.0.0.1:${session.socksPort}`, 'https://check.torproject.org/api/ip'], { windowsHide: true, timeout: 35000 });
  const ip = JSON.parse(stdout);
  if (!ip.IsTor) throw new Error('Tor exit verification failed.');
  return ip.IP;
}

const cases = JSON.parse(await fs.readFile(process.argv[2], 'utf8'));
for (const config of cases) {
  const result = { ...config, runtime: 'native Tor + lyrebird', samples: [] };
  const sessions = [];
  try {
    if (config.parallel) {
      const starts = await Promise.allSettled(config.parallel.map(boot));
      for (const value of starts) if (value.status === 'fulfilled') sessions.push(value.value);
      result.failedConnections = starts.flatMap((value, index) => value.status === 'rejected'
        ? [{ label: config.parallel[index].label, error: value.reason.message }] : []);
      result.activeConnections = sessions.length;
      if (!sessions.length) throw new Error('None of the parallel connections bootstrapped.');
      result.bootstrapSeconds = sessions.map(session => session.bootstrapSeconds);
      result.exitIps = await Promise.all(sessions.map(checkIp));
      const started = performance.now();
      result.samples = await Promise.all(sessions.map((session, index) => measure(session, 'parallel-' + index)));
      result.seconds = (performance.now() - started) / 1000;
      result.aggregateMibPerSecond = sessions.length / result.seconds;
    } else {
      const session = await boot(config);
      sessions.push(session);
      result.bootstrapSeconds = session.bootstrapSeconds;
      result.exitIp = await checkIp(session);
      for (let index = 0; index < (config.samples || 1); index++) result.samples.push(await measure(session, 'sample-' + index));
    }
    result.ok = true;
  } catch (error) { result.ok = false; result.error = error.message; }
  finally { for (const session of sessions) await session.stop(); }
  await fs.writeFile(path.join(work, config.label + '.json'), JSON.stringify(result, null, 2));
  console.log('RESULT', JSON.stringify(result));
}
