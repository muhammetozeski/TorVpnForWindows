export const DefaultGateway = '170.64.236.147:12298:uEiBHwUMNRTetrbqScahm81Di57Xv2OphNrx-CurJGOq3ww';
export const MaxBodyBytes = 16 * 1024 * 1024;
export const BlockProxy = {
  mode: 'fixed_servers',
  rules: { singleProxy: { scheme: 'socks5', host: '127.0.0.1', port: 1 }, bypassList: ['<-loopback>'] }
};

/** Permit only literal gateway addresses; the fingerprint pins the gateway certificate. */
export function validateGateway(value) {
  const gateway = String(value || DefaultGateway).trim();
  if (!/^(?:\d{1,3}(?:\.\d{1,3}){3}|\[[\da-f:]+\]):\d{1,5}:u[A-Za-z0-9_-]{40,100}$/i.test(gateway)) {
    throw new Error('invalidGateway');
  }
  const port = Number(gateway.slice(gateway.lastIndexOf(']') + 1).split(':').filter(Boolean).at(-2));
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('invalidGateway');
  return gateway;
}

/** Preserve response metadata and individual cookies while removing HTTP transfer framing. */
export function responseHeaders(headers) {
  const cookies = headers.getSetCookie?.() || [];
  const excluded = ['transfer-encoding', 'connection', 'keep-alive', 'content-length'];
  if (cookies.length) excluded.push('set-cookie');
  return [...headers].filter(([name]) => !excluded.includes(name.toLowerCase()))
    .map(([name, value]) => ({ name, value }))
    .concat(cookies.map(value => ({ name: 'Set-Cookie', value })));
}

export function toBase64(bytes) {
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return btoa(binary);
}

export function fromBase64(text) {
  return Uint8Array.from(atob(text), character => character.charCodeAt(0));
}
