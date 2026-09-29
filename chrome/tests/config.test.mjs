import { test } from 'node:test';
import assert from 'node:assert/strict';
import { responseHeaders, toBase64, fromBase64, validateGateway, DefaultGateway, BlockProxy } from '../extension/config.js';

test('gateway input rejects configuration injection and invalid ports', () => {
  assert.equal(validateGateway(DefaultGateway), DefaultGateway);
  for (const value of ['https://example.com', DefaultGateway + '\nDIRECT', DefaultGateway.replace(':12298:', ':65536:')]) {
    assert.throws(() => validateGateway(value));
  }
});

test('binary responses survive native Chrome string messaging', () => {
  const input = new Uint8Array(131073);
  for (let index = 0; index < input.length; index++) input[index] = index % 256;
  assert.deepEqual(fromBase64(toBase64(input)), input);
});

test('response headers preserve separate cookies and remove transport framing', () => {
  const headers = new Headers({ 'Content-Type': 'text/plain', 'Content-Length': '500', 'Transfer-Encoding': 'chunked' });
  headers.append('Set-Cookie', 'a=1; Path=/; HttpOnly');
  headers.append('Set-Cookie', 'b=2; Expires=Thu, 01 Jan 2027 00:00:00 GMT');
  const result = responseHeaders(headers);
  assert.equal(result.filter(header => header.name === 'Set-Cookie').length, 2);
  assert.ok(!result.some(header => ['content-length', 'transfer-encoding'].includes(header.name)));
  assert.ok(result.some(header => header.name === 'content-type'));
});

test('fallback configuration includes loopback requests and has no direct alternative', () => {
  assert.equal(BlockProxy.rules.singleProxy.scheme, 'socks5');
  assert.deepEqual(BlockProxy.rules.bypassList, ['<-loopback>']);
  assert.equal(BlockProxy.mode, 'fixed_servers');
});
