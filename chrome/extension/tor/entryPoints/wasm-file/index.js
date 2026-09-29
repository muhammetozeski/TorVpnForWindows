var __defProp = Object.defineProperty;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __esm = (fn, res) => function __init() {
  return fn && (res = (0, fn[__getOwnPropNames(fn)[0]])(fn = 0)), res;
};
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: true });
};

// node_modules/@kpstreams/core/dist/address.js
function parseAddress2(s) {
  const malformed = () => new Error(`address: malformed (expected <ip>:<port>:<certhash> or [ipv6]:<port>:<certhash>): ${s}`);
  let ip;
  let rest;
  if (s.startsWith("[")) {
    const end = s.indexOf("]");
    if (end < 0 || s[end + 1] !== ":")
      throw malformed();
    ip = s.slice(1, end);
    rest = s.slice(end + 2);
  } else {
    const i = s.indexOf(":");
    if (i < 0)
      throw malformed();
    ip = s.slice(0, i);
    rest = s.slice(i + 1);
  }
  const j = rest.indexOf(":");
  if (j < 0)
    throw malformed();
  const portStr = rest.slice(0, j);
  const certhash = rest.slice(j + 1);
  if (!/^\d+$/.test(portStr))
    throw malformed();
  const port = Number(portStr);
  if (port < 1 || port > 65535)
    throw new Error("address: port out of range");
  if (!ip || !certhash)
    throw malformed();
  return { ip, port, certhash };
}
function formatAddress2(addr) {
  const host = addr.ip.includes(":") ? `[${addr.ip}]` : addr.ip;
  return `${host}:${addr.port}:${addr.certhash}`;
}
var init_address = __esm({
  "node_modules/@kpstreams/core/dist/address.js"() {
    "use strict";
  }
});

// node_modules/@kpstreams/core/dist/certhash.js
function decodeCerthash(s) {
  if (!s.startsWith(MULTIBASE_BASE64URL_NOPAD)) {
    throw new Error(`certhash: expected multibase prefix '${MULTIBASE_BASE64URL_NOPAD}', got '${s[0] ?? ""}'`);
  }
  const bytes = base64urlDecode(s.slice(1));
  if (bytes.length !== 2 + MULTIHASH_SHA256_LEN) {
    throw new Error(`certhash: expected ${2 + MULTIHASH_SHA256_LEN} bytes, got ${bytes.length}`);
  }
  if (bytes[0] !== MULTIHASH_SHA256_CODE || bytes[1] !== MULTIHASH_SHA256_LEN) {
    throw new Error(`certhash: not a sha2-256 multihash (prefix ${bytes[0].toString(16)} ${bytes[1].toString(16)})`);
  }
  return bytes.slice(2);
}
function digestToSdpFingerprint(digest) {
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0").toUpperCase()).join(":");
}
function base64urlDecode(s) {
  const pad = (4 - s.length % 4) % 4;
  const b64 = s.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat(pad);
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++)
    out[i] = bin.charCodeAt(i);
  return out;
}
var MULTIBASE_BASE64URL_NOPAD, MULTIHASH_SHA256_CODE, MULTIHASH_SHA256_LEN;
var init_certhash = __esm({
  "node_modules/@kpstreams/core/dist/certhash.js"() {
    "use strict";
    MULTIBASE_BASE64URL_NOPAD = "u";
    MULTIHASH_SHA256_CODE = 18;
    MULTIHASH_SHA256_LEN = 32;
  }
});

// node_modules/@kpstreams/core/dist/errors.js
function streamError(reason) {
  const e = new Error(reason.message ?? `kps: stream ${reason.code ?? "reset"}`);
  e.code = reason.code;
  return e;
}
function reasonFrom(x) {
  if (x == null)
    return void 0;
  if (typeof x === "object" && ("code" in x || "message" in x))
    return x;
  return { message: String(x?.message ?? x) };
}
var init_errors = __esm({
  "node_modules/@kpstreams/core/dist/errors.js"() {
    "use strict";
  }
});

// node_modules/@kpstreams/core/dist/index.js
var init_dist = __esm({
  "node_modules/@kpstreams/core/dist/index.js"() {
    "use strict";
    init_address();
    init_certhash();
  }
});

// node_modules/@kpstreams/core/dist/framing.js
function codeToNum(code) {
  return code ? CODE_TO_NUM[code] ?? 0 : 0;
}
function numToCode(n) {
  return n === 0 ? void 0 : NUM_TO_CODE[n] ?? "internal-error";
}
function encodeData(payload) {
  const out = new Uint8Array(1 + payload.length);
  out[0] = FRAME_DATA;
  out.set(payload, 1);
  return out;
}
function encodeFin() {
  return new Uint8Array([FRAME_FIN]);
}
function encodeCode(type, code) {
  const out = new Uint8Array(5);
  out[0] = type;
  new DataView(out.buffer).setUint32(1, code >>> 0, false);
  return out;
}
function encodeMaxStreamData(value) {
  const out = new Uint8Array(9);
  out[0] = FRAME_MAX_STREAM_DATA;
  new DataView(out.buffer).setBigUint64(1, value, false);
  return out;
}
function parseFrame(data) {
  if (data.length === 0)
    throw new ProtocolViolation("empty data-channel message");
  if (data.length > MAX_WEBRTC_FRAME_SIZE) {
    throw new ProtocolViolation(`frame exceeds ${MAX_WEBRTC_FRAME_SIZE} bytes (${data.length})`);
  }
  const type = data[0];
  const payload = data.subarray(1);
  const view = () => new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  switch (type) {
    case FRAME_DATA:
      if (payload.length === 0)
        throw new ProtocolViolation("empty DATA frame");
      return { type: "data", payload };
    case FRAME_FIN:
      if (payload.length !== 0)
        throw new ProtocolViolation("FIN with payload");
      return { type: "fin" };
    case FRAME_RESET:
      if (payload.length !== 4)
        throw new ProtocolViolation("RESET payload must be 4 bytes");
      return { type: "reset", code: view().getUint32(0, false) };
    case FRAME_STOP_SENDING:
      if (payload.length !== 4)
        throw new ProtocolViolation("STOP_SENDING payload must be 4 bytes");
      return { type: "stop-sending", code: view().getUint32(0, false) };
    case FRAME_MAX_STREAM_DATA: {
      if (payload.length !== 8)
        throw new ProtocolViolation("MAX_STREAM_DATA payload must be 8 bytes");
      const value = view().getBigUint64(0, false);
      if (value > MAX_OFFSET)
        throw new ProtocolViolation("MAX_STREAM_DATA above MAX_OFFSET");
      return { type: "max-stream-data", value };
    }
    default:
      throw new ProtocolViolation(`unknown frame type 0x${type.toString(16)}`);
  }
}
var FRAME_DATA, FRAME_FIN, FRAME_RESET, FRAME_STOP_SENDING, FRAME_MAX_STREAM_DATA, MAX_WEBRTC_FRAME_SIZE, MAX_FRAME_PAYLOAD, MAX_OFFSET, ProtocolViolation, CODE_TO_NUM, NUM_TO_CODE;
var init_framing = __esm({
  "node_modules/@kpstreams/core/dist/framing.js"() {
    "use strict";
    FRAME_DATA = 0;
    FRAME_FIN = 1;
    FRAME_RESET = 2;
    FRAME_STOP_SENDING = 3;
    FRAME_MAX_STREAM_DATA = 4;
    MAX_WEBRTC_FRAME_SIZE = 16384;
    MAX_FRAME_PAYLOAD = MAX_WEBRTC_FRAME_SIZE - 1;
    MAX_OFFSET = (1n << 62n) - 1n;
    ProtocolViolation = class extends Error {
    };
    CODE_TO_NUM = {
      cancelled: 1,
      closed: 2,
      reset: 3,
      timeout: 4,
      "network-error": 5,
      "protocol-error": 6,
      unsupported: 7,
      "too-large": 8,
      "queue-full": 9,
      "permission-denied": 10,
      "internal-error": 11
    };
    NUM_TO_CODE = Object.fromEntries(Object.entries(CODE_TO_NUM).map(([k, v]) => [v, k]));
  }
});

// node_modules/@kpstreams/core/dist/control.js
function encodeConnClose(code) {
  const out = new Uint8Array(5);
  out[0] = CTRL_CONNECTION_CLOSE;
  new DataView(out.buffer).setUint32(1, codeToNum(code), false);
  return out;
}
function encodeHello(limits, version = WIRE_VERSION) {
  const out = new Uint8Array(26);
  const v = new DataView(out.buffer);
  out[0] = CTRL_HELLO;
  out[1] = version;
  v.setBigUint64(2, limits.initialMaxStreamData, false);
  v.setBigUint64(10, limits.initialMaxData, false);
  v.setBigUint64(18, limits.initialMaxStreams, false);
  return out;
}
function encodeMaxData(value) {
  const out = new Uint8Array(9);
  out[0] = CTRL_MAX_DATA;
  new DataView(out.buffer).setBigUint64(1, value, false);
  return out;
}
function encodeMaxStreams(value) {
  const out = new Uint8Array(9);
  out[0] = CTRL_MAX_STREAMS;
  new DataView(out.buffer).setBigUint64(1, value, false);
  return out;
}
function decodeControl(data) {
  if (data.length === 0)
    throw new ProtocolViolation("empty control message");
  const type = data[0];
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  switch (type) {
    case CTRL_CONNECTION_CLOSE:
      if (data.length !== 5)
        throw new ProtocolViolation("CONNECTION_CLOSE must be 5 bytes");
      return { t: "close", code: view.getUint32(1, false) };
    case CTRL_HELLO: {
      if (data.length !== 26)
        throw new ProtocolViolation("HELLO must be 26 bytes");
      const limits = {
        initialMaxStreamData: view.getBigUint64(2, false),
        initialMaxData: view.getBigUint64(10, false),
        initialMaxStreams: view.getBigUint64(18, false)
      };
      for (const v of Object.values(limits)) {
        if (v > MAX_OFFSET)
          throw new ProtocolViolation("HELLO credit above MAX_OFFSET");
      }
      return { t: "hello", version: data[1], limits };
    }
    case CTRL_MAX_DATA: {
      if (data.length !== 9)
        throw new ProtocolViolation("MAX_DATA must be 9 bytes");
      const value = view.getBigUint64(1, false);
      if (value > MAX_OFFSET)
        throw new ProtocolViolation("MAX_DATA above MAX_OFFSET");
      return { t: "max-data", value };
    }
    case CTRL_MAX_STREAMS: {
      if (data.length !== 9)
        throw new ProtocolViolation("MAX_STREAMS must be 9 bytes");
      const value = view.getBigUint64(1, false);
      if (value > MAX_OFFSET)
        throw new ProtocolViolation("MAX_STREAMS above MAX_OFFSET");
      return { t: "max-streams", value };
    }
    default:
      throw new ProtocolViolation(`unknown control message type 0x${type.toString(16)}`);
  }
}
var CTRL_CONNECTION_CLOSE, CTRL_HELLO, CTRL_MAX_DATA, CTRL_MAX_STREAMS, WIRE_VERSION;
var init_control = __esm({
  "node_modules/@kpstreams/core/dist/control.js"() {
    "use strict";
    init_framing();
    CTRL_CONNECTION_CLOSE = 0;
    CTRL_HELLO = 1;
    CTRL_MAX_DATA = 2;
    CTRL_MAX_STREAMS = 3;
    WIRE_VERSION = 1;
  }
});

// node_modules/@kpstreams/core/dist/flow.js
function resolveLimits(partial) {
  return {
    initialMaxStreamData: partial?.initialMaxStreamData ?? DEFAULT_INITIAL_MAX_STREAM_DATA,
    initialMaxData: partial?.initialMaxData ?? DEFAULT_INITIAL_MAX_DATA,
    initialMaxStreams: partial?.initialMaxStreams ?? DEFAULT_INITIAL_MAX_STREAMS
  };
}
function saturate(v) {
  return v > MAX_OFFSET ? MAX_OFFSET : v;
}
var DEFAULT_INITIAL_MAX_STREAM_DATA, DEFAULT_INITIAL_MAX_DATA, DEFAULT_INITIAL_MAX_STREAMS, Wakeable, ConnFlow, StreamFlow;
var init_flow = __esm({
  "node_modules/@kpstreams/core/dist/flow.js"() {
    "use strict";
    init_framing();
    DEFAULT_INITIAL_MAX_STREAM_DATA = 1n << 20n;
    DEFAULT_INITIAL_MAX_DATA = 8n << 20n;
    DEFAULT_INITIAL_MAX_STREAMS = 100n;
    Wakeable = class {
      #resolvers = [];
      wake() {
        const rs = this.#resolvers;
        this.#resolvers = [];
        for (const r of rs)
          r();
      }
      wait(signal) {
        return new Promise((resolve, reject) => {
          if (signal) {
            const onAbort = () => reject(new Error("kps: aborted"));
            signal.addEventListener("abort", onAbort, { once: true });
            this.#resolvers.push(() => {
              signal.removeEventListener("abort", onAbort);
              resolve();
            });
          } else {
            this.#resolvers.push(resolve);
          }
        });
      }
    };
    ConnFlow = class {
      // ---- our receive policy (what we grant the peer) ----
      local;
      // ---- sender side (peer-granted, all zero until the peer's HELLO) ----
      #peerMaxStreamDataInitial = 0n;
      // seeds each new StreamFlow's send window
      #peerMaxData = 0n;
      #peerMaxStreams = 0n;
      #connSent = 0n;
      #connReserved = 0n;
      #streamsOpened = 0n;
      #streamsReserved = 0n;
      // ---- receiver side ----
      #localMaxData;
      // enforcement limit (advances at commit-to-send)
      #connReceived = 0n;
      #connConsumed = 0n;
      #connAdvertisedAt = 0n;
      // connConsumed value at the last advertisement
      #peerOpenedStreams = 0n;
      #peerRetiredStreams = 0n;
      #advertisedMaxStreams;
      #credit = new Wakeable();
      #failure = null;
      #sink;
      constructor(local, sink) {
        this.local = local;
        this.#localMaxData = local.initialMaxData;
        this.#advertisedMaxStreams = local.initialMaxStreams;
        this.#sink = sink;
      }
      /** The peer's HELLO: seed every send-side limit. */
      onPeerHello(limits) {
        this.#peerMaxStreamDataInitial = limits.initialMaxStreamData;
        this.#peerMaxData = limits.initialMaxData;
        this.#peerMaxStreams = limits.initialMaxStreams;
        this.#credit.wake();
      }
      /** Peer raised the connection data limit (MAX_DATA). Decreases are ignored. */
      onPeerMaxData(value) {
        if (value > this.#peerMaxData) {
          this.#peerMaxData = value;
          this.#credit.wake();
        }
      }
      /** Peer raised the stream-count limit (MAX_STREAMS). Decreases are ignored. */
      onPeerMaxStreams(value) {
        if (value > this.#peerMaxStreams) {
          this.#peerMaxStreams = value;
          this.#credit.wake();
        }
      }
      /** Fail every pending and future credit wait (connection teardown). */
      fail(err) {
        if (this.#failure)
          return;
        this.#failure = err;
        this.#credit.wake();
      }
      /** Wake blocked reservations so they re-check (stream credit or failure). */
      wake() {
        this.#credit.wake();
      }
      get failed() {
        return this.#failure;
      }
      /**
       * The peer's per-stream initial window. A getter (not copied into
       * StreamFlow) so streams staged before the peer's HELLO see the window the
       * moment it arrives.
       */
      get peerInitialMaxStreamData() {
        return this.#peerMaxStreamDataInitial;
      }
      newStream(sendMaxStreamData) {
        return new StreamFlow(this, this.local.initialMaxStreamData, sendMaxStreamData);
      }
      // ---- sender: byte credit (called via StreamFlow) ----
      /**
       * Reserve up to `n` DATA payload bytes at both levels, waiting until at
       * least one byte of credit is available (a writer larger than the whole
       * window must split at the window boundary, like a QUIC sender — an
       * all-or-nothing reservation would deadlock). Returns the granted amount
       * (1..n). Rejects if the stream's write half fails (STOP_SENDING, reset,
       * close), the connection fails, or `signal` aborts.
       */
      async reserveData(sf, n, signal) {
        for (; ; ) {
          if (this.#failure)
            throw this.#failure;
          const sfErr = sf.sendFailed;
          if (sfErr)
            throw sfErr;
          if (signal?.aborted)
            throw new Error("kps: aborted");
          const streamAvail = sf.peerMaxStreamData - sf.sendSent - sf.sendReserved;
          const connAvail = this.#peerMaxData - this.#connSent - this.#connReserved;
          let grant = streamAvail < connAvail ? streamAvail : connAvail;
          if (grant > n)
            grant = n;
          if (grant >= 1n) {
            sf.sendReserved += grant;
            this.#connReserved += grant;
            return grant;
          }
          await this.#credit.wait(signal);
        }
      }
      /** Bytes passed to the transport: reserved → sent, both levels. */
      commitData(sf, n) {
        sf.sendReserved -= n;
        sf.sendSent += n;
        this.#connReserved -= n;
        this.#connSent += n;
      }
      /** A reserved-but-unsent frame was discarded: release its reservation. */
      releaseData(sf, n) {
        sf.sendReserved -= n;
        this.#connReserved -= n;
        this.#credit.wake();
      }
      // ---- sender: stream slots ----
      /** Reserve a slot to open one stream, waiting at the limit. */
      async reserveStreamSlot(signal) {
        for (; ; ) {
          if (this.#failure)
            throw this.#failure;
          if (signal?.aborted)
            throw new Error("kps: aborted");
          if (this.#streamsOpened + this.#streamsReserved < this.#peerMaxStreams) {
            this.#streamsReserved += 1n;
            return;
          }
          await this.#credit.wait(signal);
        }
      }
      /** Channel creation succeeded: the cumulative count never decreases. */
      commitStreamSlot() {
        this.#streamsReserved -= 1n;
        this.#streamsOpened += 1n;
      }
      /** Channel creation failed synchronously: release the slot. */
      releaseStreamSlot() {
        this.#streamsReserved -= 1n;
        this.#credit.wake();
      }
      // ---- receiver: byte credit (called via StreamFlow) ----
      /** @throws ProtocolViolation when the peer exceeds the connection window. */
      connDataReceived(n) {
        if (this.#connReceived + n > this.#localMaxData) {
          throw new ProtocolViolation("peer exceeded MAX_DATA");
        }
        this.#connReceived += n;
      }
      connDataConsumed(n) {
        this.#connConsumed += n;
        const window2 = this.local.initialMaxData;
        if (this.#connConsumed - this.#connAdvertisedAt >= window2 / 2n) {
          this.#connAdvertisedAt = this.#connConsumed;
          this.#localMaxData = saturate(this.#connConsumed + window2);
          this.#sink.sendMaxData(this.#localMaxData);
        }
      }
      // ---- receiver: stream count ----
      /**
       * A peer-initiated stream was observed (it consumes a slot immediately, even
       * unaccepted or pre-HELLO).
       * @throws ProtocolViolation when the peer exceeds MAX_STREAMS.
       */
      peerStreamOpened() {
        if (this.#peerOpenedStreams >= this.#advertisedMaxStreams) {
          throw new ProtocolViolation("peer exceeded MAX_STREAMS");
        }
        this.#peerOpenedStreams += 1n;
      }
      /** A peer-initiated stream retired: grant a replacement slot. */
      peerStreamRetired() {
        this.#peerRetiredStreams += 1n;
        this.#advertisedMaxStreams = saturate(this.local.initialMaxStreams + this.#peerRetiredStreams);
        this.#sink.sendMaxStreams(this.#advertisedMaxStreams);
      }
    };
    StreamFlow = class {
      // sender side
      sendSent = 0n;
      sendReserved = 0n;
      #peerMaxExplicit = 0n;
      // largest MAX_STREAM_DATA received on this stream
      #sendFailure = null;
      // receiver side
      #localMaxStreamData;
      // enforcement limit
      #received = 0n;
      #consumed = 0n;
      #advertisedAt = 0n;
      #cancelled = false;
      // local cancelRead: no further stream credit
      #conn;
      #sendMaxStreamData;
      constructor(conn, localMaxStreamData, sendMaxStreamData) {
        this.#conn = conn;
        this.#localMaxStreamData = localMaxStreamData;
        this.#sendMaxStreamData = sendMaxStreamData;
      }
      /** Effective peer window: explicit updates never lower it below the HELLO initial. */
      get peerMaxStreamData() {
        const initial = this.#conn.peerInitialMaxStreamData;
        return this.#peerMaxExplicit > initial ? this.#peerMaxExplicit : initial;
      }
      // ---- sender ----
      /** Reserve up to `n` bytes; resolves with the granted amount (1..n). */
      async reserve(n, signal) {
        return Number(await this.#conn.reserveData(this, BigInt(n), signal));
      }
      commit(n) {
        this.#conn.commitData(this, BigInt(n));
      }
      release(n) {
        this.#conn.releaseData(this, BigInt(n));
      }
      /** Fail pending and future reservations (STOP_SENDING, reset, close). */
      failSend(err) {
        if (this.#sendFailure)
          return;
        this.#sendFailure = err;
        this.#conn.wake();
      }
      get sendFailed() {
        return this.#sendFailure;
      }
      /** MAX_STREAM_DATA from the peer. Decreases are ignored. */
      onPeerMaxStreamData(value) {
        if (value > this.#peerMaxExplicit) {
          this.#peerMaxExplicit = value;
          this.#conn.wake();
        }
      }
      // ---- receiver ----
      /**
       * `n` inbound DATA payload bytes arrived; enforce both windows atomically
       * (single-threaded: check both, then count both).
       * @throws ProtocolViolation when the peer exceeds either window.
       */
      onDataReceived(n) {
        const bn = BigInt(n);
        if (this.#received + bn > this.#localMaxStreamData) {
          throw new ProtocolViolation("peer exceeded MAX_STREAM_DATA");
        }
        this.#conn.connDataReceived(bn);
        this.#received += bn;
      }
      /**
       * `n` bytes were consumed — read-fulfilled to the application or explicitly
       * discarded. Advertises replacement credit past the half-window threshold
       * (stream credit is withheld after cancelRead; connection credit always
       * flows so a discarded stream cannot starve unrelated streams).
       */
      onConsumed(n) {
        const bn = BigInt(n);
        this.#consumed += bn;
        const window2 = this.#conn.local.initialMaxStreamData;
        if (!this.#cancelled && this.#consumed - this.#advertisedAt >= window2 / 2n) {
          this.#advertisedAt = this.#consumed;
          this.#localMaxStreamData = saturate(this.#consumed + window2);
          this.#sendMaxStreamData(this.#localMaxStreamData);
        }
        this.#conn.connDataConsumed(bn);
      }
      /** Local cancelRead: stop granting stream credit; discards still free MAX_DATA. */
      markCancelled() {
        this.#cancelled = true;
      }
    };
  }
});

// node_modules/@kpstreams/core/dist/stream-core.js
var LOCAL_SEND_BUFFER_LOW, Wakeable2, KpsStream;
var init_stream_core = __esm({
  "node_modules/@kpstreams/core/dist/stream-core.js"() {
    "use strict";
    init_framing();
    init_errors();
    LOCAL_SEND_BUFFER_LOW = 1 << 20;
    Wakeable2 = class {
      #resolvers = [];
      wake() {
        const rs = this.#resolvers;
        this.#resolvers = [];
        for (const r of rs)
          r();
      }
      wait() {
        return new Promise((res) => this.#resolvers.push(res));
      }
    };
    KpsStream = class {
      readable;
      writable;
      closed;
      /** Resolves when the channel opens; rejects if it dies first. */
      opened;
      #ch;
      #sf;
      #hooks;
      #inbuf = [];
      #peerFin = false;
      #peerReset = null;
      #peerStop = null;
      #localTerminal = null;
      #readCancelled = false;
      // The reason a locally-terminated read half surfaces to a pending/subsequent
      // read. Per SPEC §9.2, EOF is reserved for the peer's FIN; a local
      // cancelRead/close or a connection teardown must make the read *error*.
      #readError = null;
      #channelClosed = false;
      #retiredFired = false;
      #readWake = new Wakeable2();
      #drainWake = new Wakeable2();
      #closeResolve;
      #closeSettled = false;
      #openResolve;
      #openReject;
      #openSettled = false;
      constructor(ch, connFlow, hooks) {
        this.#ch = ch;
        this.#hooks = hooks;
        this.#sf = connFlow.newStream((v) => {
          if (this.#ch.isOpen())
            this.#ch.send(encodeMaxStreamData(v));
        });
        this.closed = new Promise((res) => {
          this.#closeResolve = res;
        });
        this.opened = new Promise((res, rej) => {
          this.#openResolve = res;
          this.#openReject = rej;
        });
        this.opened.catch(() => {
        });
        ch.setBufferedAmountLowThreshold(LOCAL_SEND_BUFFER_LOW);
        ch.onBufferedAmountLow(() => this.#drainWake.wake());
        ch.onOpen(() => this.#settleOpen(null));
        if (ch.isOpen())
          this.#settleOpen(null);
        ch.onMessage((d) => this.#onFrame(d));
        ch.onClose(() => this.#onChannelClose());
        ch.onError((msg) => {
          this.#settle({ ok: false, reason: { code: "network-error", message: msg } });
          this.#settleOpen(new Error(`kps: stream failed: ${msg}`));
          this.#readWake.wake();
          this.#drainWake.wake();
        });
        this.readable = new ReadableStream({
          pull: async (controller) => {
            for (; ; ) {
              if (this.#readCancelled) {
                controller.error(streamError(this.#readError ?? { code: "cancelled" }));
                return;
              }
              const chunk = this.#inbuf.shift();
              if (chunk) {
                controller.enqueue(chunk);
                this.#sf.onConsumed(chunk.length);
                this.#maybeRetire();
                return;
              }
              if (this.#peerReset) {
                controller.error(streamError(this.#peerReset));
                return;
              }
              if (this.#peerFin) {
                controller.close();
                return;
              }
              if (this.#channelClosed) {
                controller.error(streamError({ code: "network-error", message: "kps: stream closed" }));
                return;
              }
              await this.#readWake.wait();
            }
          },
          cancel: (reason) => {
            void this.cancelRead(reasonFrom(reason) ?? { code: "cancelled" });
          }
        }, { highWaterMark: 0 });
        this.writable = new WritableStream({
          write: (chunk) => this.#writeChunk(chunk),
          close: () => this.closeWrite(),
          abort: (reason) => this.resetWrite(reasonFrom(reason) ?? { code: "reset" })
        });
      }
      // ---- inbound ----
      #onFrame(data) {
        let f;
        try {
          f = parseFrame(data);
        } catch (e) {
          this.#hooks.fatal({ code: "protocol-error", message: e.message });
          return;
        }
        switch (f.type) {
          case "data": {
            if (this.#peerFin || this.#peerReset) {
              this.#hooks.fatal({ code: "protocol-error", message: "DATA after terminal frame" });
              return;
            }
            try {
              this.#sf.onDataReceived(f.payload.length);
            } catch (e) {
              this.#hooks.fatal({ code: "protocol-error", message: e.message });
              return;
            }
            if (this.#readCancelled) {
              this.#sf.onConsumed(f.payload.length);
              return;
            }
            this.#inbuf.push(f.payload.slice());
            this.#readWake.wake();
            return;
          }
          case "fin": {
            if (this.#peerFin || this.#peerReset) {
              this.#hooks.fatal({ code: "protocol-error", message: "second terminal frame" });
              return;
            }
            this.#peerFin = true;
            this.#readWake.wake();
            this.#maybeRetire();
            return;
          }
          case "reset": {
            if (this.#peerFin || this.#peerReset) {
              this.#hooks.fatal({ code: "protocol-error", message: "second terminal frame" });
              return;
            }
            this.#peerReset = { code: numToCode(f.code) ?? "reset" };
            this.#discardInbuf();
            this.#readWake.wake();
            this.#maybeRetire();
            return;
          }
          case "stop-sending": {
            if (this.#peerStop)
              return;
            this.#peerStop = { code: numToCode(f.code) ?? "cancelled" };
            this.#sf.failSend(streamError(this.#peerStop));
            if (!this.#localTerminal) {
              this.#localTerminal = "reset";
              if (this.#ch.isOpen())
                this.#ch.send(encodeCode(FRAME_RESET, f.code));
              this.#maybeRetire();
            }
            return;
          }
          case "max-stream-data":
            this.#sf.onPeerMaxStreamData(f.value);
            return;
        }
      }
      #onChannelClose() {
        this.#channelClosed = true;
        const wireComplete = this.#localTerminal !== null && (this.#peerFin || this.#peerReset !== null);
        if (!wireComplete && !this.#hooks.isTeardown()) {
          this.#hooks.fatal({ code: "protocol-error", message: "data channel closed mid-stream" });
        }
        this.#sf.failSend(streamError({ code: "network-error", message: "kps: stream closed" }));
        this.#settleOpen(new Error("kps: stream closed before opening"));
        this.#settle({ ok: !this.#peerReset, reason: this.#peerReset ?? void 0 });
        this.#readWake.wake();
        this.#drainWake.wake();
        this.#maybeRetire();
      }
      // ---- outbound ----
      async #writeChunk(chunk) {
        let off = 0;
        while (off < chunk.length) {
          this.#checkWritable();
          const want = Math.min(chunk.length - off, MAX_FRAME_PAYLOAD);
          const granted = await this.#sf.reserve(want);
          const slice = chunk.subarray(off, off + granted);
          try {
            await this.#drainLocal();
            this.#checkWritable();
            this.#ch.send(encodeData(slice));
          } catch (e) {
            this.#sf.release(granted);
            throw e;
          }
          this.#sf.commit(granted);
          off += granted;
        }
      }
      #checkWritable() {
        if (this.#peerStop)
          throw streamError(this.#peerStop);
        if (this.#localTerminal)
          throw streamError({ code: "closed", message: "kps: write half closed" });
        if (this.#channelClosed || !this.#ch.isOpen())
          throw new Error("kps: stream is closed");
      }
      // Local send-queue bound only — flow control is the credit reservation above.
      async #drainLocal() {
        while (this.#ch.isOpen() && this.#ch.bufferedAmount() >= LOCAL_SEND_BUFFER_LOW) {
          await this.#drainWake.wait();
        }
      }
      // ---- public stream operations ----
      /** Gracefully finish the local write half; the peer observes EOF after all written bytes. */
      async closeWrite() {
        if (this.#localTerminal)
          return;
        this.#localTerminal = "fin";
        this.#sf.failSend(streamError({ code: "closed", message: "kps: write half closed" }));
        if (this.#ch.isOpen())
          this.#ch.send(encodeFin());
        this.#maybeRetire();
      }
      /** Stop wanting inbound bytes (not EOF); the peer is told to stop sending. */
      async cancelRead(reason) {
        if (this.#readCancelled)
          return;
        this.#readCancelled = true;
        this.#readError = reason ?? { code: "cancelled" };
        this.#sf.markCancelled();
        this.#discardInbuf();
        if (!this.#peerFin && !this.#peerReset && this.#ch.isOpen()) {
          this.#ch.send(encodeCode(FRAME_STOP_SENDING, codeToNum(reason?.code ?? "cancelled")));
        }
        this.#readWake.wake();
        this.#maybeRetire();
      }
      /** Abort the local write half; the peer observes a stream error rather than EOF. */
      async resetWrite(reason) {
        if (this.#localTerminal)
          return;
        this.#localTerminal = "reset";
        this.#sf.failSend(streamError(reason ?? { code: "reset" }));
        if (this.#ch.isOpen())
          this.#ch.send(encodeCode(FRAME_RESET, codeToNum(reason?.code ?? "reset")));
        this.#maybeRetire();
      }
      /**
       * Tear down both halves. The channel itself closes at retirement — once the
       * peer's terminal frame (a conforming peer answers STOP_SENDING with RESET)
       * has arrived — because closing it earlier is a §6.5 protocol violation.
       */
      async close(reason) {
        try {
          await this.closeWrite();
        } catch {
        }
        try {
          await this.cancelRead(reason ?? { code: "closed" });
        } catch {
        }
      }
      /** Connection teardown: discard state, fail waiters, no wire activity. */
      destroy(reason) {
        this.#discardInbuf();
        this.#readCancelled = true;
        this.#readError = reason ?? { code: "closed", message: "kps: connection closed" };
        this.#sf.failSend(streamError(reason ?? { code: "closed", message: "kps: connection closed" }));
        this.#settleOpen(new Error("kps: connection closed"));
        this.#settle(reason ? { ok: false, reason } : { ok: true });
        this.#readWake.wake();
        this.#drainWake.wake();
      }
      // ---- lifecycle ----
      #discardInbuf() {
        if (this.#inbuf.length === 0)
          return;
        let n = 0;
        for (const c of this.#inbuf)
          n += c.length;
        this.#inbuf = [];
        this.#sf.onConsumed(n);
      }
      #maybeRetire() {
        const wireComplete = this.#localTerminal !== null && (this.#peerFin || this.#peerReset !== null);
        if (!wireComplete)
          return;
        const drained = this.#inbuf.length === 0;
        if (!drained)
          return;
        if (!this.#channelClosed) {
          this.#ch.close();
          return;
        }
        if (!this.#retiredFired) {
          this.#retiredFired = true;
          this.#hooks.retired();
        }
      }
      #settle(info) {
        if (this.#closeSettled)
          return;
        this.#closeSettled = true;
        this.#closeResolve(info);
      }
      #settleOpen(err) {
        if (this.#openSettled)
          return;
        this.#openSettled = true;
        if (err)
          this.#openReject(err);
        else
          this.#openResolve();
      }
    };
  }
});

// node_modules/@kpstreams/core/dist/conn-core.js
function raceAbort(p, signal, message) {
  if (!signal)
    return p;
  if (signal.aborted)
    return Promise.reject(new Error(message));
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(new Error(message));
    signal.addEventListener("abort", onAbort, { once: true });
    p.then((v) => {
      signal.removeEventListener("abort", onAbort);
      resolve(v);
    }, (e) => {
      signal.removeEventListener("abort", onAbort);
      reject(e);
    });
  });
}
var WEBRTC_MAX_DATAGRAM, CONTROL_LABEL, CONTROL_ID, DATAGRAM_LABEL, DATAGRAM_ID, DEFAULT_HELLO_TIMEOUT_MS, MAX_DATAGRAM_QUEUE, ConnCore;
var init_conn_core = __esm({
  "node_modules/@kpstreams/core/dist/conn-core.js"() {
    "use strict";
    init_framing();
    init_control();
    init_flow();
    init_stream_core();
    init_errors();
    WEBRTC_MAX_DATAGRAM = 1200;
    CONTROL_LABEL = "_kps_control";
    CONTROL_ID = 0;
    DATAGRAM_LABEL = "_kps_datagrams";
    DATAGRAM_ID = 1;
    DEFAULT_HELLO_TIMEOUT_MS = 15e3;
    MAX_DATAGRAM_QUEUE = 256;
    ConnCore = class {
      closed;
      /** Resolves at mutual HELLO; rejects if the connection dies first. */
      established;
      flow;
      #host;
      #state = "connecting";
      #tearingDown = false;
      #helloSent = false;
      #peerHello = null;
      #establishedDone = false;
      #establishResolve;
      #establishReject;
      #helloTimer;
      #seq = 0;
      #streams = /* @__PURE__ */ new Set();
      #staged = [];
      #incoming = [];
      #acceptWaiters = [];
      #dgQueue = [];
      #dgWaiters = [];
      #closeResolve;
      #closeFired = false;
      constructor(host) {
        this.#host = host;
        this.closed = new Promise((res) => {
          this.#closeResolve = res;
        });
        this.established = new Promise((res, rej) => {
          this.#establishResolve = res;
          this.#establishReject = rej;
        });
        this.established.catch(() => {
        });
        this.flow = new ConnFlow(resolveLimits(host.limits), {
          sendMaxData: (v) => this.#trySendControl(encodeMaxData(v)),
          sendMaxStreams: (v) => this.#trySendControl(encodeMaxStreams(v))
        });
        host.control.onOpen(() => this.#sendHello());
        host.control.onMessage((d) => this.#onControl(d));
        host.control.onClose(() => this.#reservedChannelLost("control"));
        host.control.onError(() => this.#reservedChannelLost("control"));
        if (host.control.isOpen())
          this.#sendHello();
        host.datagram.onMessage((d) => this.#onDatagram(d));
        host.datagram.onClose(() => this.#reservedChannelLost("datagram"));
        this.#helloTimer = setTimeout(() => this.fatal({ code: "timeout", message: "kps: HELLO timeout" }), host.helloTimeoutMs ?? DEFAULT_HELLO_TIMEOUT_MS);
        this.#helloTimer.unref?.();
      }
      get state() {
        return this.#state;
      }
      // ---- control channel ----
      #sendHello() {
        if (this.#helloSent || this.#closeFired)
          return;
        this.#helloSent = true;
        this.#trySendControl(encodeHello(this.flow.local));
        this.#checkEstablished();
      }
      #onControl(data) {
        let m;
        try {
          m = decodeControl(data);
        } catch (e) {
          this.fatal({ code: "protocol-error", message: e.message });
          return;
        }
        switch (m.t) {
          case "hello": {
            if (this.#peerHello) {
              this.fatal({ code: "protocol-error", message: "duplicate HELLO" });
              return;
            }
            if (m.version !== WIRE_VERSION) {
              this.#trySendControl(encodeConnClose("unsupported"));
              this.#teardown({
                ok: false,
                reason: { code: "unsupported", message: `kps: peer wire version ${m.version} (want ${WIRE_VERSION})` }
              });
              return;
            }
            this.#peerHello = m.limits;
            this.flow.onPeerHello(m.limits);
            this.#checkEstablished();
            return;
          }
          case "close": {
            const reason = m.code === 0 ? void 0 : { code: numToCode(m.code) ?? "internal-error" };
            this.#teardown({ ok: m.code === 0, reason });
            return;
          }
          case "max-data":
            if (!this.#peerHello) {
              this.fatal({ code: "protocol-error", message: "control message before HELLO" });
              return;
            }
            this.flow.onPeerMaxData(m.value);
            return;
          case "max-streams":
            if (!this.#peerHello) {
              this.fatal({ code: "protocol-error", message: "control message before HELLO" });
              return;
            }
            this.flow.onPeerMaxStreams(m.value);
            return;
        }
      }
      #checkEstablished() {
        if (this.#establishedDone || this.#closeFired)
          return;
        if (!this.#helloSent || !this.#peerHello)
          return;
        this.#establishedDone = true;
        this.#state = "open";
        clearTimeout(this.#helloTimer);
        this.#establishResolve();
        const staged = this.#staged;
        this.#staged = [];
        for (const s of staged)
          this.#enqueueIncoming(s);
      }
      #trySendControl(msg) {
        try {
          if (this.#host.control.isOpen())
            this.#host.control.send(msg);
        } catch {
        }
      }
      #reservedChannelLost(which) {
        if (this.#tearingDown || this.#closeFired)
          return;
        this.fatal({ code: "protocol-error", message: `kps: reserved ${which} channel lost` });
      }
      // ---- streams ----
      /** The wrapper calls this for every incoming (DCEP) application channel. */
      handleIncomingChannel(ch) {
        try {
          this.flow.peerStreamOpened();
        } catch (e) {
          this.fatal({ code: "protocol-error", message: e.message });
          return;
        }
        const stream = this.#makeStream(ch, true);
        if (this.#establishedDone)
          this.#enqueueIncoming(stream);
        else
          this.#staged.push(stream);
      }
      #makeStream(ch, peerInitiated) {
        const stream = new KpsStream(ch, this.flow, {
          fatal: (r) => this.fatal(r),
          retired: () => {
            this.#streams.delete(stream);
            if (peerInitiated)
              this.flow.peerStreamRetired();
          },
          isTeardown: () => this.#tearingDown
        });
        this.#streams.add(stream);
        return stream;
      }
      #enqueueIncoming(stream) {
        const w = this.#acceptWaiters.shift();
        if (w)
          w.resolve(stream);
        else
          this.#incoming.push(stream);
      }
      async openStream(opts = {}) {
        if (opts.signal?.aborted)
          throw new Error("kps: openStream aborted");
        if (this.#state !== "open")
          throw new Error(`kps: connection is ${this.#state}`);
        await this.flow.reserveStreamSlot(opts.signal);
        let ch;
        try {
          ch = this.#host.openChannel(`kps-${++this.#seq}`);
        } catch (e) {
          this.flow.releaseStreamSlot();
          throw e;
        }
        this.flow.commitStreamSlot();
        const stream = this.#makeStream(ch, false);
        try {
          await raceAbort(stream.opened, opts.signal, "kps: openStream aborted");
        } catch (e) {
          stream.opened.then(() => {
            void stream.resetWrite({ code: "cancelled" });
            void stream.cancelRead({ code: "cancelled" });
          }).catch(() => {
          });
          throw e;
        }
        return stream;
      }
      acceptStream(opts = {}) {
        const ready = this.#incoming.shift();
        if (ready)
          return Promise.resolve(ready);
        if (opts.signal?.aborted)
          return Promise.reject(new Error("kps: acceptStream aborted"));
        if (this.#state === "closed")
          return Promise.reject(new Error("kps: connection is closed"));
        const signal = opts.signal;
        return new Promise((resolve, reject) => {
          const waiter = {
            resolve: (s) => {
              signal?.removeEventListener("abort", onAbort);
              resolve(s);
            },
            reject: (e) => {
              signal?.removeEventListener("abort", onAbort);
              reject(e);
            }
          };
          const onAbort = () => {
            const i = this.#acceptWaiters.indexOf(waiter);
            if (i >= 0)
              this.#acceptWaiters.splice(i, 1);
            reject(new Error("kps: acceptStream aborted"));
          };
          this.#acceptWaiters.push(waiter);
          signal?.addEventListener("abort", onAbort, { once: true });
        });
      }
      // ---- datagrams (SPEC §7) ----
      #onDatagram(data) {
        const w = this.#dgWaiters.shift();
        if (w) {
          w.resolve(data);
          return;
        }
        this.#dgQueue.push(data);
        if (this.#dgQueue.length > MAX_DATAGRAM_QUEUE)
          this.#dgQueue.shift();
      }
      async sendDatagram(data, opts) {
        if (opts?.signal?.aborted)
          throw new Error("kps: sendDatagram aborted");
        if (data.length > WEBRTC_MAX_DATAGRAM) {
          const e = new Error(`kps: datagram exceeds limit (max ${WEBRTC_MAX_DATAGRAM} bytes)`);
          Object.assign(e, { code: "too-large", maxDatagramPayloadSize: WEBRTC_MAX_DATAGRAM });
          throw e;
        }
        if (!this.#host.datagram.isOpen())
          throw new Error("kps: datagram channel not open");
        this.#host.datagram.send(data);
      }
      receiveDatagram(opts) {
        const next = this.#dgQueue.shift();
        if (next)
          return Promise.resolve(next);
        if (this.#state === "closed")
          return Promise.reject(new Error("kps: connection closed"));
        if (opts?.signal?.aborted)
          return Promise.reject(new Error("kps: receiveDatagram aborted"));
        const signal = opts?.signal;
        return new Promise((resolve, reject) => {
          const waiter = {
            resolve: (v) => {
              signal?.removeEventListener("abort", onAbort);
              resolve(v);
            },
            reject: (e) => {
              signal?.removeEventListener("abort", onAbort);
              reject(e);
            }
          };
          const onAbort = () => {
            const i = this.#dgWaiters.indexOf(waiter);
            if (i >= 0)
              this.#dgWaiters.splice(i, 1);
            reject(new Error("kps: receiveDatagram aborted"));
          };
          this.#dgWaiters.push(waiter);
          signal?.addEventListener("abort", onAbort, { once: true });
        });
      }
      // ---- close paths ----
      /** Graceful local close: best-effort CONNECTION_CLOSE, then teardown. */
      close(reason) {
        if (this.#closeFired)
          return;
        this.#tearingDown = true;
        this.#trySendControl(encodeConnClose(reason?.code));
        this.#teardown({ ok: true, reason });
      }
      /** A peer wire violation or local fatal condition: convey a code, tear down. */
      fatal(reason) {
        if (this.#closeFired)
          return;
        this.#tearingDown = true;
        this.#trySendControl(encodeConnClose(reason.code ?? "protocol-error"));
        this.#teardown({ ok: false, reason });
      }
      /** Transport-layer state changes, forwarded by the wrapper. */
      onTransportFailed(message = "peer connection failed") {
        this.#teardown({ ok: false, reason: { code: "network-error", message } });
      }
      onTransportClosed() {
        this.#teardown({ ok: this.#state !== "connecting" });
      }
      #teardown(info) {
        if (this.#closeFired)
          return;
        this.#closeFired = true;
        this.#tearingDown = true;
        this.#state = "closed";
        clearTimeout(this.#helloTimer);
        const err = info.reason ? streamError(info.reason) : new Error("kps: connection closed");
        if (!this.#establishedDone)
          this.#establishReject(err);
        this.flow.fail(err);
        for (const s of [...this.#streams, ...this.#staged])
          s.destroy(info.reason);
        this.#streams.clear();
        this.#staged = [];
        for (const w of this.#acceptWaiters)
          w.reject(new Error("kps: connection closed"));
        this.#acceptWaiters = [];
        for (const w of this.#dgWaiters)
          w.reject(new Error("kps: connection closed"));
        this.#dgWaiters = [];
        try {
          this.#host.closeTransport();
        } catch {
        }
        this.#closeResolve(info);
      }
    };
  }
});

// node_modules/@kpstreams/core/dist/sdp.js
function generateUfrag() {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}
async function deriveICEPwd(certhashDigest, ufrag) {
  const key = await crypto.subtle.importKey("raw", toArrayBuffer(certhashDigest), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const msg = new TextEncoder().encode("kps-ice-pwd-v1:" + ufrag);
  const sig = new Uint8Array(await crypto.subtle.sign("HMAC", key, toArrayBuffer(msg)));
  let bin = "";
  for (const b of sig)
    bin += String.fromCharCode(b);
  return btoa(bin).replace(/=+$/, "");
}
function toArrayBuffer(u8) {
  return u8.buffer.slice(u8.byteOffset, u8.byteOffset + u8.byteLength);
}
function rewriteOfferUfrag(sdp, ufrag, pwd) {
  const lines = sdp.split(/\r\n|\n/).map((line) => {
    if (line.startsWith("a=ice-ufrag:"))
      return `a=ice-ufrag:${ufrag}`;
    if (line.startsWith("a=ice-pwd:"))
      return `a=ice-pwd:${pwd}`;
    return line;
  });
  return lines.join("\r\n");
}
function synthesizeAnswer(addr, ufrag, pwd) {
  const fingerprint = digestToSdpFingerprint(decodeCerthash(addr.certhash));
  const ip6 = addr.ip.includes(":");
  const fam = ip6 ? "IP6" : "IP4";
  const lines = [
    "v=0",
    `o=- 0 0 IN ${fam} ${ip6 ? "::" : "0.0.0.0"}`,
    "s=-",
    "t=0 0",
    "a=ice-lite",
    `m=application ${addr.port} UDP/DTLS/SCTP webrtc-datachannel`,
    `c=IN ${fam} ${addr.ip}`,
    "a=mid:0",
    `a=ice-ufrag:${ufrag}`,
    `a=ice-pwd:${pwd}`,
    `a=fingerprint:sha-256 ${fingerprint}`,
    "a=setup:passive",
    "a=sctp-port:5000",
    "a=max-message-size:1048576",
    `a=candidate:1 1 UDP 1 ${addr.ip} ${addr.port} typ host`
  ];
  return lines.join("\r\n") + "\r\n";
}
var init_sdp = __esm({
  "node_modules/@kpstreams/core/dist/sdp.js"() {
    "use strict";
    init_certhash();
  }
});

// node_modules/@kpstreams/core/dist/webrtc.js
var init_webrtc = __esm({
  "node_modules/@kpstreams/core/dist/webrtc.js"() {
    "use strict";
    init_conn_core();
    init_sdp();
  }
});

// node_modules/@kpstreams/webrtc-client/dist/connection.js
function toArrayBuffer2(u8) {
  return u8.buffer.slice(u8.byteOffset, u8.byteOffset + u8.byteLength);
}
function dialAbortError(signal) {
  const reason = signal.reason;
  return new Error(reason?.name === "TimeoutError" ? "kps: dial timed out" : "kps: dial aborted");
}
function dial(addr, opts) {
  return Connection.dial(addr, opts);
}
var DEFAULT_TIMEOUT, RTCChannelAdapter, Connection;
var init_connection = __esm({
  "node_modules/@kpstreams/webrtc-client/dist/connection.js"() {
    "use strict";
    init_dist();
    init_webrtc();
    DEFAULT_TIMEOUT = 15e3;
    RTCChannelAdapter = class {
      #dc;
      #message = null;
      #open = null;
      #close = null;
      #error = null;
      #bal = null;
      constructor(dc) {
        this.#dc = dc;
        dc.binaryType = "arraybuffer";
        dc.addEventListener("message", (e) => {
          const raw = e.data;
          const data = typeof raw === "string" ? new TextEncoder().encode(raw) : new Uint8Array(raw);
          this.#message?.(data);
        });
        dc.addEventListener("open", () => this.#open?.());
        dc.addEventListener("close", () => this.#close?.());
        dc.addEventListener("error", (e) => {
          this.#error?.(e.error?.message ?? "data channel error");
        });
        dc.addEventListener("bufferedamountlow", () => this.#bal?.());
      }
      isOpen() {
        return this.#dc.readyState === "open";
      }
      send(data) {
        this.#dc.send(toArrayBuffer2(data));
      }
      bufferedAmount() {
        return this.#dc.bufferedAmount;
      }
      setBufferedAmountLowThreshold(bytes) {
        this.#dc.bufferedAmountLowThreshold = bytes;
      }
      onBufferedAmountLow(cb) {
        this.#bal = cb;
      }
      onMessage(cb) {
        this.#message = cb;
      }
      onOpen(cb) {
        this.#open = cb;
      }
      onClose(cb) {
        this.#close = cb;
      }
      onError(cb) {
        this.#error = cb;
      }
      close() {
        try {
          this.#dc.close();
        } catch {
        }
      }
    };
    Connection = class _Connection {
      // The dialed endpoint (see the core Connection.remoteAddress doc).
      remoteAddress;
      #pc;
      #core;
      // `control` is the reserved reliable channel (ID 0) dial() created before the
      // offer (to force the SCTP m-line).
      constructor(pc, control, remote) {
        this.#pc = pc;
        this.remoteAddress = remote;
        const dg = pc.createDataChannel(DATAGRAM_LABEL, {
          negotiated: true,
          id: DATAGRAM_ID,
          ordered: false,
          maxRetransmits: 0
        });
        this.#core = new ConnCore({
          control: new RTCChannelAdapter(control),
          datagram: new RTCChannelAdapter(dg),
          openChannel: (label) => new RTCChannelAdapter(pc.createDataChannel(label)),
          closeTransport: () => {
            try {
              pc.close();
            } catch {
            }
          }
        });
        pc.addEventListener("connectionstatechange", () => {
          const s = pc.connectionState;
          if (s === "failed")
            this.#core.onTransportFailed();
          else if (s === "closed")
            this.#core.onTransportClosed();
        });
        pc.addEventListener("datachannel", (e) => {
          const channel = e.channel;
          if (channel.label === CONTROL_LABEL || channel.label === DATAGRAM_LABEL)
            return;
          this.#core.handleIncomingChannel(new RTCChannelAdapter(channel));
        });
      }
      get closed() {
        return this.#core.closed;
      }
      static async dial(addrStr, opts = {}) {
        const signal = opts.signal ?? AbortSignal.timeout(DEFAULT_TIMEOUT);
        if (signal.aborted)
          throw dialAbortError(signal);
        const addr = parseAddress2(addrStr);
        const digest = decodeCerthash(addr.certhash);
        const pc = new RTCPeerConnection({});
        const control = pc.createDataChannel(CONTROL_LABEL, { negotiated: true, id: CONTROL_ID });
        const offer = await pc.createOffer();
        const ufrag = generateUfrag();
        const pwd = await deriveICEPwd(digest, ufrag);
        await pc.setLocalDescription({ type: offer.type, sdp: rewriteOfferUfrag(offer.sdp ?? "", ufrag, pwd) });
        await pc.setRemoteDescription({ type: "answer", sdp: synthesizeAnswer(addr, ufrag, pwd) });
        const conn = new _Connection(pc, control, { ip: addr.ip, port: addr.port });
        await conn.#waitEstablished(signal);
        return conn;
      }
      #waitEstablished(signal) {
        return new Promise((resolve, reject) => {
          const onAbort = () => {
            try {
              this.#pc.close();
            } catch {
            }
            reject(dialAbortError(signal));
          };
          signal.addEventListener("abort", onAbort, { once: true });
          this.#core.established.then(() => {
            signal.removeEventListener("abort", onAbort);
            resolve();
          }, (e) => {
            signal.removeEventListener("abort", onAbort);
            reject(e);
          });
        });
      }
      openStream(opts = {}) {
        return this.#core.openStream(opts);
      }
      acceptStream(opts = {}) {
        return this.#core.acceptStream(opts);
      }
      async close(reason) {
        this.#core.close(reason);
      }
      // Datagrams (SPEC §7) — unreliable, unordered, best-effort.
      sendDatagram(data, opts) {
        return this.#core.sendDatagram(data, opts);
      }
      receiveDatagram(opts) {
        return this.#core.receiveDatagram(opts);
      }
    };
  }
});

// node_modules/@kpstreams/webrtc-client/dist/open-stream.js
async function openStream(addr, opts) {
  const conn = await dial(addr, opts);
  try {
    const stream = await conn.openStream({ signal: opts?.signal });
    void stream.closed.finally(() => {
      void conn.close();
    });
    return stream;
  } catch (err) {
    await conn.close();
    throw err;
  }
}
var init_open_stream = __esm({
  "node_modules/@kpstreams/webrtc-client/dist/open-stream.js"() {
    "use strict";
    init_connection();
  }
});

// node_modules/@kpstreams/webrtc-client/dist/index.js
var dist_exports = {};
__export(dist_exports, {
  dial: () => dial,
  formatAddress: () => formatAddress2,
  openStream: () => openStream,
  parseAddress: () => parseAddress2
});
var init_dist2 = __esm({
  "node_modules/@kpstreams/webrtc-client/dist/index.js"() {
    "use strict";
    init_connection();
    init_open_stream();
    init_dist();
  }
});

// src/kpsDial.ts
var kpsDial_exports = {};
__export(kpsDial_exports, {
  kpsDial: () => kpsDial
});
var kpsDial;
var init_kpsDial = __esm({
  "src/kpsDial.ts"() {
    "use strict";
    kpsDial = async (address) => {
      if (typeof globalThis.RTCPeerConnection !== "undefined") {
        const { dial: dial2 } = await Promise.resolve().then(() => (init_dist2(), dist_exports));
        return dial2(address);
      }
      const quicClientPkg = "@kpstreams/quic-client";
      let mod;
      try {
        mod = await import(
          /* @vite-ignore */
          quicClientPkg
        );
      } catch {
        throw new Error(
          "kps: no transport available. Browsers need RTCPeerConnection; in Node, install the optional '@kpstreams/quic-client' package to reach a gateway over QUIC."
        );
      }
      return mod.dial(address);
    };
  }
});

// src/polyfills.ts
Symbol.dispose ??= /* @__PURE__ */ Symbol("Symbol.dispose");

// crates/tor-js-wasm/pkg/tor_js.js
var IntoUnderlyingByteSource = class {
  __destroy_into_raw() {
    const ptr = this.__wbg_ptr;
    this.__wbg_ptr = 0;
    IntoUnderlyingByteSourceFinalization.unregister(this);
    return ptr;
  }
  free() {
    const ptr = this.__destroy_into_raw();
    wasm.__wbg_intounderlyingbytesource_free(ptr, 0);
  }
  /**
   * @returns {number}
   */
  get autoAllocateChunkSize() {
    const ret = wasm.intounderlyingbytesource_autoAllocateChunkSize(this.__wbg_ptr);
    return ret >>> 0;
  }
  cancel() {
    const ptr = this.__destroy_into_raw();
    wasm.intounderlyingbytesource_cancel(ptr);
  }
  /**
   * @param {ReadableByteStreamController} controller
   * @returns {Promise<any>}
   */
  pull(controller) {
    const ret = wasm.intounderlyingbytesource_pull(this.__wbg_ptr, controller);
    return ret;
  }
  /**
   * @param {ReadableByteStreamController} controller
   */
  start(controller) {
    wasm.intounderlyingbytesource_start(this.__wbg_ptr, controller);
  }
  /**
   * @returns {ReadableStreamType}
   */
  get type() {
    const ret = wasm.intounderlyingbytesource_type(this.__wbg_ptr);
    return __wbindgen_enum_ReadableStreamType[ret];
  }
};
if (Symbol.dispose) IntoUnderlyingByteSource.prototype[Symbol.dispose] = IntoUnderlyingByteSource.prototype.free;
var IntoUnderlyingSink = class {
  __destroy_into_raw() {
    const ptr = this.__wbg_ptr;
    this.__wbg_ptr = 0;
    IntoUnderlyingSinkFinalization.unregister(this);
    return ptr;
  }
  free() {
    const ptr = this.__destroy_into_raw();
    wasm.__wbg_intounderlyingsink_free(ptr, 0);
  }
  /**
   * @param {any} reason
   * @returns {Promise<any>}
   */
  abort(reason) {
    const ptr = this.__destroy_into_raw();
    const ret = wasm.intounderlyingsink_abort(ptr, reason);
    return ret;
  }
  /**
   * @returns {Promise<any>}
   */
  close() {
    const ptr = this.__destroy_into_raw();
    const ret = wasm.intounderlyingsink_close(ptr);
    return ret;
  }
  /**
   * @param {any} chunk
   * @returns {Promise<any>}
   */
  write(chunk) {
    const ret = wasm.intounderlyingsink_write(this.__wbg_ptr, chunk);
    return ret;
  }
};
if (Symbol.dispose) IntoUnderlyingSink.prototype[Symbol.dispose] = IntoUnderlyingSink.prototype.free;
var IntoUnderlyingSource = class _IntoUnderlyingSource {
  static __wrap(ptr) {
    const obj = Object.create(_IntoUnderlyingSource.prototype);
    obj.__wbg_ptr = ptr;
    IntoUnderlyingSourceFinalization.register(obj, obj.__wbg_ptr, obj);
    return obj;
  }
  __destroy_into_raw() {
    const ptr = this.__wbg_ptr;
    this.__wbg_ptr = 0;
    IntoUnderlyingSourceFinalization.unregister(this);
    return ptr;
  }
  free() {
    const ptr = this.__destroy_into_raw();
    wasm.__wbg_intounderlyingsource_free(ptr, 0);
  }
  cancel() {
    const ptr = this.__destroy_into_raw();
    wasm.intounderlyingsource_cancel(ptr);
  }
  /**
   * @param {ReadableStreamDefaultController} controller
   * @returns {Promise<any>}
   */
  pull(controller) {
    const ret = wasm.intounderlyingsource_pull(this.__wbg_ptr, controller);
    return ret;
  }
};
if (Symbol.dispose) IntoUnderlyingSource.prototype[Symbol.dispose] = IntoUnderlyingSource.prototype.free;
var TorClient = class _TorClient {
  static __wrap(ptr) {
    const obj = Object.create(_TorClient.prototype);
    obj.__wbg_ptr = ptr;
    TorClientFinalization.register(obj, obj.__wbg_ptr, obj);
    return obj;
  }
  __destroy_into_raw() {
    const ptr = this.__wbg_ptr;
    this.__wbg_ptr = 0;
    TorClientFinalization.unregister(this);
    return ptr;
  }
  free() {
    const ptr = this.__destroy_into_raw();
    wasm.__wbg_torclient_free(ptr, 0);
  }
  /**
   * Close the TorClient and release resources
   * @returns {Promise<any>}
   */
  close() {
    const ret = wasm.torclient_close(this.__wbg_ptr);
    return ret;
  }
  /**
   * Create a new TorClient with the given options.
   *
   * This is an async operation that returns a Promise.
   * The client will bootstrap and establish a connection to the Tor network.
   *
   * Usage from JS: `const client = await TorClient.create(options);`
   * @param {TorClientOptions} options
   * @returns {Promise<any>}
   */
  static create(options) {
    _assertClass(options, TorClientOptions);
    var ptr0 = options.__destroy_into_raw();
    const ret = wasm.torclient_create(ptr0);
    return ret;
  }
  /**
   * Make an HTTP fetch request through Tor
   *
   * Returns a Promise that resolves to a standard browser `Response` object
   * as soon as response headers are received. The body is a `ReadableStream`
   * that reads from the Tor circuit on demand.
   * @param {string} url
   * @param {any} init
   * @returns {Promise<any>}
   */
  fetch(url, init2) {
    const ptr0 = passStringToWasm0(url, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
    const len0 = WASM_VECTOR_LEN;
    const ret = wasm.torclient_fetch(this.__wbg_ptr, ptr0, len0, init2);
    return ret;
  }
  /**
   * Wait until the client is ready for traffic (connection usable + valid directory).
   * @returns {Promise<any>}
   */
  ready() {
    const ret = wasm.torclient_ready(this.__wbg_ptr);
    return ret;
  }
};
if (Symbol.dispose) TorClient.prototype[Symbol.dispose] = TorClient.prototype.free;
var TorClientOptions = class _TorClientOptions {
  static __wrap(ptr) {
    const obj = Object.create(_TorClientOptions.prototype);
    obj.__wbg_ptr = ptr;
    TorClientOptionsFinalization.register(obj, obj.__wbg_ptr, obj);
    return obj;
  }
  __destroy_into_raw() {
    const ptr = this.__wbg_ptr;
    this.__wbg_ptr = 0;
    TorClientOptionsFinalization.unregister(this);
    return ptr;
  }
  free() {
    const ptr = this.__destroy_into_raw();
    wasm.__wbg_torclientoptions_free(ptr, 0);
  }
  /**
   * Create options with a connect function.
   *
   * The connect function receives a target address string (e.g. "198.51.100.1:9001")
   * and must return a Promise resolving to a socket object with:
   * - `send(data: Uint8Array)` — send binary data
   * - `onmessage: ((data: Uint8Array) => void) | null` — receive callback
   * - `onclose: (() => void) | null` — close notification
   * - `close()` — close the socket
   *
   * The TS wrapper provides this automatically via the Gateway class.
   * @param {Function} connect
   */
  constructor(connect) {
    const ret = wasm.torclientoptions_new(connect);
    this.__wbg_ptr = ret;
    TorClientOptionsFinalization.register(this, this.__wbg_ptr, this);
    return this;
  }
  /**
   * Set a callback that provides bootstrap.zip bytes for fast directory pre-population.
   *
   * The callback should be `() => Promise<Uint8Array>` returning the
   * bootstrap archive from a tor-js-gateway server — either raw zip bytes
   * or zstd-compressed (`bootstrap.zip.zst`); compression is auto-detected.
   *
   * When set and storage has no cached consensus, the zip is parsed and the
   * directory cache is pre-populated before bootstrap begins.
   * @param {Function} callback
   * @returns {TorClientOptions}
   */
  withFastBootstrap(callback) {
    const ptr = this.__destroy_into_raw();
    const ret = wasm.torclientoptions_withFastBootstrap(ptr, callback);
    return _TorClientOptions.__wrap(ret);
  }
  /**
   * Set a custom storage implementation for persistent state.
   *
   * When set, the Tor client will persist guard selection and other state
   * to this storage, allowing faster reconnection across page reloads.
   *
   * If not set, in-memory storage is used (state lost on page reload).
   *
   * # Arguments
   * * `storage` - A JavaScript object implementing the TorStorage interface
   * @param {TorStorage} storage
   * @returns {TorClientOptions}
   */
  withStorage(storage) {
    const ptr = this.__destroy_into_raw();
    const ret = wasm.torclientoptions_withStorage(ptr, storage);
    return _TorClientOptions.__wrap(ret);
  }
};
if (Symbol.dispose) TorClientOptions.prototype[Symbol.dispose] = TorClientOptions.prototype.free;
function init(log_level) {
  var ptr0 = isLikeNone(log_level) ? 0 : passStringToWasm0(log_level, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
  var len0 = WASM_VECTOR_LEN;
  const ret = wasm.init(ptr0, len0);
  if (ret[1]) {
    throw takeFromExternrefTable0(ret[0]);
  }
}
function setLogCallback(callback) {
  wasm.setLogCallback(callback);
}
function setLogLevel(level) {
  const ptr0 = passStringToWasm0(level, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
  const len0 = WASM_VECTOR_LEN;
  const ret = wasm.setLogLevel(ptr0, len0);
  if (ret[1]) {
    throw takeFromExternrefTable0(ret[0]);
  }
}
function __wbg_get_imports() {
  const import0 = {
    __proto__: null,
    __wbg_Error_67e7344beaa85059: function(arg0, arg1) {
      const ret = Error(getStringFromWasm0(arg0, arg1));
      return ret;
    },
    __wbg_String_8564e559799eccda: function(arg0, arg1) {
      const ret = String(arg1);
      const ptr1 = passStringToWasm0(ret, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
      const len1 = WASM_VECTOR_LEN;
      getDataViewMemory0().setInt32(arg0 + 4 * 1, len1, true);
      getDataViewMemory0().setInt32(arg0 + 4 * 0, ptr1, true);
    },
    __wbg___wbindgen_boolean_get_7a12af2b3f899c5a: function(arg0) {
      const v = arg0;
      const ret = typeof v === "boolean" ? v : void 0;
      return isLikeNone(ret) ? 16777215 : ret ? 1 : 0;
    },
    __wbg___wbindgen_debug_string_0e68cf47c9cbd9b0: function(arg0, arg1) {
      const ret = debugString(arg1);
      const ptr1 = passStringToWasm0(ret, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
      const len1 = WASM_VECTOR_LEN;
      getDataViewMemory0().setInt32(arg0 + 4 * 1, len1, true);
      getDataViewMemory0().setInt32(arg0 + 4 * 0, ptr1, true);
    },
    __wbg___wbindgen_in_50072d4d6e45c193: function(arg0, arg1) {
      const ret = arg0 in arg1;
      return ret;
    },
    __wbg___wbindgen_is_function_fcda5e3902d732fe: function(arg0) {
      const ret = typeof arg0 === "function";
      return ret;
    },
    __wbg___wbindgen_is_null_5160b3e381865372: function(arg0) {
      const ret = arg0 === null;
      return ret;
    },
    __wbg___wbindgen_is_object_edb6b15aa3afe12e: function(arg0) {
      const val = arg0;
      const ret = typeof val === "object" && val !== null;
      return ret;
    },
    __wbg___wbindgen_is_string_c4f7cb494a2a21f1: function(arg0) {
      const ret = typeof arg0 === "string";
      return ret;
    },
    __wbg___wbindgen_is_undefined_8c687d0b90d5b524: function(arg0) {
      const ret = arg0 === void 0;
      return ret;
    },
    __wbg___wbindgen_jsval_loose_eq_3c30021c243b64cd: function(arg0, arg1) {
      const ret = arg0 == arg1;
      return ret;
    },
    __wbg___wbindgen_number_get_1dc732b810cb937c: function(arg0, arg1) {
      const obj = arg1;
      const ret = typeof obj === "number" ? obj : void 0;
      getDataViewMemory0().setFloat64(arg0 + 8 * 1, isLikeNone(ret) ? 0 : ret, true);
      getDataViewMemory0().setInt32(arg0 + 4 * 0, !isLikeNone(ret), true);
    },
    __wbg___wbindgen_string_get_92ab86bb19cbc12f: function(arg0, arg1) {
      const obj = arg1;
      const ret = typeof obj === "string" ? obj : void 0;
      var ptr1 = isLikeNone(ret) ? 0 : passStringToWasm0(ret, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
      var len1 = WASM_VECTOR_LEN;
      getDataViewMemory0().setInt32(arg0 + 4 * 1, len1, true);
      getDataViewMemory0().setInt32(arg0 + 4 * 0, ptr1, true);
    },
    __wbg___wbindgen_throw_5d9e815e6fdf150f: function(arg0, arg1) {
      throw new Error(getStringFromWasm0(arg0, arg1));
    },
    __wbg__wbg_cb_unref_997e73d32238e655: function(arg0) {
      arg0._wbg_cb_unref();
    },
    __wbg_aborted_523817079f8cb2c0: function(arg0) {
      const ret = arg0.aborted;
      return ret;
    },
    __wbg_all_ec3dacfec8d1eb8f: function(arg0) {
      const ret = Promise.all(arg0);
      return ret;
    },
    __wbg_append_8e87c13f0f08f8cd: function() {
      return handleError(function(arg0, arg1, arg2, arg3, arg4) {
        arg0.append(getStringFromWasm0(arg1, arg2), getStringFromWasm0(arg3, arg4));
      }, arguments);
    },
    __wbg_buffer_4a989bded7035f57: function(arg0) {
      const ret = arg0.buffer;
      return ret;
    },
    __wbg_byobRequest_f161fc37241dd3d4: function(arg0) {
      const ret = arg0.byobRequest;
      return isLikeNone(ret) ? 0 : addToExternrefTable0(ret);
    },
    __wbg_byteLength_0ddb1795e2c7e689: function(arg0) {
      const ret = arg0.byteLength;
      return ret;
    },
    __wbg_byteOffset_46eb015f52b6ad7d: function(arg0) {
      const ret = arg0.byteOffset;
      return ret;
    },
    __wbg_call_269c5566fbede3eb: function() {
      return handleError(function(arg0, arg1) {
        const ret = arg0.call(arg1);
        return ret;
      }, arguments);
    },
    __wbg_call_6bcf8d3e20937e46: function() {
      return handleError(function(arg0, arg1, arg2) {
        const ret = arg0.call(arg1, arg2);
        return ret;
      }, arguments);
    },
    __wbg_call_c1ad1cb1b78e8130: function() {
      return handleError(function(arg0, arg1, arg2, arg3, arg4) {
        const ret = arg0.call(arg1, arg2, arg3, arg4);
        return ret;
      }, arguments);
    },
    __wbg_cancel_c076350a7e8954be: function(arg0) {
      const ret = arg0.cancel();
      return ret;
    },
    __wbg_catch_e2134ef6dcdb69ed: function(arg0, arg1) {
      const ret = arg0.catch(arg1);
      return ret;
    },
    __wbg_clearTimeout_113b1cde814ec762: function(arg0) {
      const ret = clearTimeout(arg0);
      return ret;
    },
    __wbg_close_22882088c136df25: function() {
      return handleError(function(arg0) {
        arg0.close();
      }, arguments);
    },
    __wbg_close_5c3f1f028070f2f7: function(arg0) {
      const ret = arg0.close();
      return ret;
    },
    __wbg_close_8b609460dd26e367: function() {
      return handleError(function(arg0) {
        arg0.close();
      }, arguments);
    },
    __wbg_crypto_38df2bab126b63dc: function(arg0) {
      const ret = arg0.crypto;
      return ret;
    },
    __wbg_delete_39424fe5dfd0d536: function() {
      return handleError(function(arg0, arg1, arg2) {
        const ret = arg0.delete(getStringFromWasm0(arg1, arg2));
        return ret;
      }, arguments);
    },
    __wbg_digest_1b9b654016330d0f: function() {
      return handleError(function(arg0, arg1, arg2, arg3) {
        const ret = arg0.digest(getStringFromWasm0(arg1, arg2), arg3);
        return ret;
      }, arguments);
    },
    __wbg_done_cffed884d87aa22e: function(arg0) {
      const ret = arg0.done;
      return ret;
    },
    __wbg_enqueue_6cb545d22db14f33: function() {
      return handleError(function(arg0, arg1) {
        arg0.enqueue(arg1);
      }, arguments);
    },
    __wbg_entries_972a87586902cf87: function(arg0) {
      const ret = Object.entries(arg0);
      return ret;
    },
    __wbg_error_757e9472f8410341: function(arg0, arg1) {
      let deferred0_0;
      let deferred0_1;
      try {
        deferred0_0 = arg0;
        deferred0_1 = arg1;
        console.error(getStringFromWasm0(arg0, arg1));
      } finally {
        wasm.__wbindgen_free_command_export(deferred0_0, deferred0_1, 1);
      }
    },
    __wbg_from_a39669ce566077da: function(arg0) {
      const ret = Array.from(arg0);
      return ret;
    },
    __wbg_getAll_5064dec960ad3673: function() {
      return handleError(function(arg0, arg1, arg2) {
        const ret = arg0.getAll(getStringFromWasm0(arg1, arg2));
        return ret;
      }, arguments);
    },
    __wbg_getRandomValues_436a51d0629d84e1: function() {
      return handleError(function(arg0, arg1) {
        globalThis.crypto.getRandomValues(getArrayU8FromWasm0(arg0, arg1));
      }, arguments);
    },
    __wbg_getRandomValues_c44a50d8cfdaebeb: function() {
      return handleError(function(arg0, arg1) {
        arg0.getRandomValues(arg1);
      }, arguments);
    },
    __wbg_getReader_38a152de4ceaae00: function(arg0) {
      const ret = arg0.getReader();
      return ret;
    },
    __wbg_getReader_b4b1868fbca77dbe: function() {
      return handleError(function(arg0) {
        const ret = arg0.getReader();
        return ret;
      }, arguments);
    },
    __wbg_getWriter_5a3d7baaf32780c4: function() {
      return handleError(function(arg0) {
        const ret = arg0.getWriter();
        return ret;
      }, arguments);
    },
    __wbg_get_6cf5a4d4d8ad3c5a: function() {
      return handleError(function(arg0, arg1) {
        const ret = Reflect.get(arg0, arg1);
        return ret;
      }, arguments);
    },
    __wbg_get_989d0a1309644f2b: function() {
      return handleError(function(arg0, arg1) {
        const ret = Reflect.get(arg0, arg1);
        return ret;
      }, arguments);
    },
    __wbg_get_b1f0ab13c737f856: function(arg0, arg1) {
      const ret = arg0[arg1 >>> 0];
      return ret;
    },
    __wbg_get_done_a668aa62d81fad70: function(arg0) {
      const ret = arg0.done;
      return isLikeNone(ret) ? 16777215 : ret ? 1 : 0;
    },
    __wbg_get_unchecked_363572bdd397d473: function(arg0, arg1) {
      const ret = arg0[arg1 >>> 0];
      return ret;
    },
    __wbg_get_value_6c62a77c168d825a: function(arg0) {
      const ret = arg0.value;
      return ret;
    },
    __wbg_get_with_ref_key_6412cf3094599694: function(arg0, arg1) {
      const ret = arg0[arg1];
      return ret;
    },
    __wbg_instanceof_AbortSignal_0ab04fb6f375e739: function(arg0) {
      let result;
      try {
        result = arg0 instanceof AbortSignal;
      } catch (_) {
        result = false;
      }
      const ret = result;
      return ret;
    },
    __wbg_instanceof_ArrayBuffer_d4ff01f8247925ae: function(arg0) {
      let result;
      try {
        result = arg0 instanceof ArrayBuffer;
      } catch (_) {
        result = false;
      }
      const ret = result;
      return ret;
    },
    __wbg_instanceof_Crypto_05bc6194f86e23c0: function(arg0) {
      let result;
      try {
        result = arg0 instanceof Crypto;
      } catch (_) {
        result = false;
      }
      const ret = result;
      return ret;
    },
    __wbg_instanceof_ReadableStream_07dda5955244a495: function(arg0) {
      let result;
      try {
        result = arg0 instanceof ReadableStream;
      } catch (_) {
        result = false;
      }
      const ret = result;
      return ret;
    },
    __wbg_instanceof_Uint8Array_598adc0fef426aa8: function(arg0) {
      let result;
      try {
        result = arg0 instanceof Uint8Array;
      } catch (_) {
        result = false;
      }
      const ret = result;
      return ret;
    },
    __wbg_instanceof_WritableStream_47d1925f730c0ae9: function(arg0) {
      let result;
      try {
        result = arg0 instanceof WritableStream;
      } catch (_) {
        result = false;
      }
      const ret = result;
      return ret;
    },
    __wbg_iterator_22ddeb808cf55a6f: function() {
      const ret = Symbol.iterator;
      return ret;
    },
    __wbg_length_31bdaf014f5fbde2: function(arg0) {
      const ret = arg0.length;
      return ret;
    },
    __wbg_length_4e1adc0d42e23620: function(arg0) {
      const ret = arg0.length;
      return ret;
    },
    __wbg_log_363d83b9114c8831: function(arg0) {
      console.log(arg0);
    },
    __wbg_msCrypto_bd5a034af96bcba6: function(arg0) {
      const ret = arg0.msCrypto;
      return ret;
    },
    __wbg_new_0afe64b4dc16ab74: function() {
      return handleError(function() {
        const ret = new Headers();
        return ret;
      }, arguments);
    },
    __wbg_new_1da3429bc3c4541c: function(arg0) {
      const ret = new Uint8Array(arg0);
      return ret;
    },
    __wbg_new_227d7c05414eb861: function() {
      const ret = new Error();
      return ret;
    },
    __wbg_new_a32a1ab6c6655abe: function(arg0, arg1) {
      const ret = new Error(getStringFromWasm0(arg0, arg1));
      return ret;
    },
    __wbg_new_bebc3f4757acf305: function() {
      const ret = new Object();
      return ret;
    },
    __wbg_new_from_slice_4ee02165f9de919e: function(arg0, arg1) {
      const ret = new Uint8Array(getArrayU8FromWasm0(arg0, arg1));
      return ret;
    },
    __wbg_new_typed_6f8b0d724fe26c07: function(arg0, arg1) {
      try {
        var state0 = { a: arg0, b: arg1 };
        var cb0 = (arg02, arg12) => {
          const a = state0.a;
          state0.a = 0;
          try {
            return wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___js_sys_4841e98648b61fa6___Function_fn_wasm_bindgen_2e541aa8012e5493___JsValue_____wasm_bindgen_2e541aa8012e5493___sys__Undefined___js_sys_4841e98648b61fa6___Function_fn_wasm_bindgen_2e541aa8012e5493___JsValue_____wasm_bindgen_2e541aa8012e5493___sys__Undefined_______true_(a, state0.b, arg02, arg12);
          } finally {
            state0.a = a;
          }
        };
        const ret = new Promise(cb0);
        return ret;
      } finally {
        state0.a = 0;
      }
    },
    __wbg_new_with_byte_offset_and_length_492c969e8b5da8a4: function(arg0, arg1, arg2) {
      const ret = new Uint8Array(arg0, arg1 >>> 0, arg2 >>> 0);
      return ret;
    },
    __wbg_new_with_into_underlying_source_b45133df5ff75afa: function(arg0, arg1) {
      const ret = new ReadableStream(IntoUnderlyingSource.__wrap(arg0), arg1);
      return ret;
    },
    __wbg_new_with_length_5ffeddb9d9fbb96f: function(arg0) {
      const ret = new Uint8Array(arg0 >>> 0);
      return ret;
    },
    __wbg_new_with_length_6a9fc3631737ef8c: function(arg0) {
      const ret = new Array(arg0 >>> 0);
      return ret;
    },
    __wbg_new_with_opt_readable_stream_and_init_652b1d60607d75aa: function() {
      return handleError(function(arg0, arg1) {
        const ret = new Response(arg0, arg1);
        return ret;
      }, arguments);
    },
    __wbg_next_95053e306b1c3aed: function(arg0) {
      const ret = arg0.next;
      return ret;
    },
    __wbg_next_f31ecb8646d2c605: function() {
      return handleError(function(arg0) {
        const ret = arg0.next();
        return ret;
      }, arguments);
    },
    __wbg_node_84ea875411254db1: function(arg0) {
      const ret = arg0.node;
      return ret;
    },
    __wbg_now_d1fb6650485d7f3e: function() {
      const ret = Date.now();
      return ret;
    },
    __wbg_now_e7c6795a7f81e10f: function(arg0) {
      const ret = arg0.now();
      return ret;
    },
    __wbg_performance_3fcf6e32a7e1ed0a: function(arg0) {
      const ret = arg0.performance;
      return ret;
    },
    __wbg_process_44c7a14e11e9f69e: function(arg0) {
      const ret = arg0.process;
      return ret;
    },
    __wbg_prototypesetcall_ae9f5e7459250748: function(arg0, arg1, arg2) {
      Uint8Array.prototype.set.call(getArrayU8FromWasm0(arg0, arg1), arg2);
    },
    __wbg_queueMicrotask_85c90f6987555d65: function(arg0) {
      const ret = arg0.queueMicrotask;
      return ret;
    },
    __wbg_queueMicrotask_f6a1fa10b81d1fc0: function(arg0) {
      queueMicrotask(arg0);
    },
    __wbg_randomFillSync_6c25eac9869eb53c: function() {
      return handleError(function(arg0, arg1) {
        arg0.randomFillSync(arg1);
      }, arguments);
    },
    __wbg_read_31091533ffadf971: function(arg0) {
      const ret = arg0.read();
      return ret;
    },
    __wbg_releaseLock_1538945f5f183d9f: function(arg0) {
      arg0.releaseLock();
    },
    __wbg_require_b4edbdcf3e2a1ef0: function() {
      return handleError(function() {
        const ret = module.require;
        return ret;
      }, arguments);
    },
    __wbg_resolve_35ec7e0c6af4c82c: function(arg0) {
      const ret = Promise.resolve(arg0);
      return ret;
    },
    __wbg_respond_83a71686e927ca32: function() {
      return handleError(function(arg0, arg1) {
        arg0.respond(arg1 >>> 0);
      }, arguments);
    },
    __wbg_setTimeout_ef24d2fc3ad97385: function() {
      return handleError(function(arg0, arg1) {
        const ret = setTimeout(arg0, arg1);
        return ret;
      }, arguments);
    },
    __wbg_set_13d25b81ab403f5e: function(arg0, arg1, arg2) {
      arg0[arg1 >>> 0] = arg2;
    },
    __wbg_set_330c5a0dc45586f5: function() {
      return handleError(function(arg0, arg1, arg2, arg3, arg4) {
        const ret = arg0.set(getStringFromWasm0(arg1, arg2), getStringFromWasm0(arg3, arg4));
        return ret;
      }, arguments);
    },
    __wbg_set_5f2ad37e5e02dc7b: function(arg0, arg1, arg2) {
      arg0.set(getArrayU8FromWasm0(arg1, arg2));
    },
    __wbg_set_a377297433dfea63: function() {
      return handleError(function(arg0, arg1, arg2) {
        const ret = Reflect.set(arg0, arg1, arg2);
        return ret;
      }, arguments);
    },
    __wbg_set_headers_dfe6a763facd3dcc: function(arg0, arg1) {
      arg0.headers = arg1;
    },
    __wbg_set_high_water_mark_089e3476522036ad: function(arg0, arg1) {
      arg0.highWaterMark = arg1;
    },
    __wbg_set_status_74351f5228412c33: function(arg0, arg1) {
      arg0.status = arg1;
    },
    __wbg_stack_3b0d974bbf31e44f: function(arg0, arg1) {
      const ret = arg1.stack;
      const ptr1 = passStringToWasm0(ret, wasm.__wbindgen_malloc_command_export, wasm.__wbindgen_realloc_command_export);
      const len1 = WASM_VECTOR_LEN;
      getDataViewMemory0().setInt32(arg0 + 4 * 1, len1, true);
      getDataViewMemory0().setInt32(arg0 + 4 * 0, ptr1, true);
    },
    __wbg_static_accessor_GLOBAL_8eb4cd83130a11a0: function() {
      const ret = typeof global === "undefined" ? null : global;
      return isLikeNone(ret) ? 0 : addToExternrefTable0(ret);
    },
    __wbg_static_accessor_GLOBAL_THIS_1e7044f654e934db: function() {
      const ret = typeof globalThis === "undefined" ? null : globalThis;
      return isLikeNone(ret) ? 0 : addToExternrefTable0(ret);
    },
    __wbg_static_accessor_SELF_d8b50611246a6d92: function() {
      const ret = typeof self === "undefined" ? null : self;
      return isLikeNone(ret) ? 0 : addToExternrefTable0(ret);
    },
    __wbg_static_accessor_WINDOW_fd0bc376bf0f8b42: function() {
      const ret = typeof window === "undefined" ? null : window;
      return isLikeNone(ret) ? 0 : addToExternrefTable0(ret);
    },
    __wbg_subarray_1daff70dde20c145: function(arg0, arg1, arg2) {
      const ret = arg0.subarray(arg1 >>> 0, arg2 >>> 0);
      return ret;
    },
    __wbg_subtle_0459789f1437758b: function(arg0) {
      const ret = arg0.subtle;
      return ret;
    },
    __wbg_then_7a850dae4493f353: function(arg0, arg1, arg2) {
      const ret = arg0.then(arg1, arg2);
      return ret;
    },
    __wbg_then_b830475380919203: function(arg0, arg1) {
      const ret = arg0.then(arg1);
      return ret;
    },
    __wbg_torclient_new: function(arg0) {
      const ret = TorClient.__wrap(arg0);
      return ret;
    },
    __wbg_tryLock_1ee4b60fa559e640: function() {
      return handleError(function(arg0) {
        const ret = arg0.tryLock();
        return ret;
      }, arguments);
    },
    __wbg_unlock_fd23e835b8b45411: function() {
      return handleError(function(arg0) {
        const ret = arg0.unlock();
        return ret;
      }, arguments);
    },
    __wbg_value_c227f843d21da141: function(arg0) {
      const ret = arg0.value;
      return ret;
    },
    __wbg_versions_276b2795b1c6a219: function(arg0) {
      const ret = arg0.versions;
      return ret;
    },
    __wbg_view_d8c7b26e4d4650f1: function(arg0) {
      const ret = arg0.view;
      return isLikeNone(ret) ? 0 : addToExternrefTable0(ret);
    },
    __wbg_write_d32d19927af3f942: function(arg0, arg1) {
      const ret = arg0.write(arg1);
      return ret;
    },
    __wbindgen_generic_0000000000000001: function(arg0, arg1) {
      const ret = makeMutClosure(arg0, arg1, wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___wasm_bindgen_2e541aa8012e5493___JsValue______true_);
      return ret;
    },
    __wbindgen_generic_0000000000000002: function(arg0, arg1) {
      const ret = makeMutClosure(arg0, arg1, wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___wasm_bindgen_2e541aa8012e5493___JsValue__core_9b3796e30d99ddb7___result__Result_____wasm_bindgen_2e541aa8012e5493___JsError___true_);
      return ret;
    },
    __wbindgen_generic_0000000000000003: function(arg0, arg1) {
      const ret = makeMutClosure(arg0, arg1, wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke_______true_);
      return ret;
    },
    __wbindgen_generic_0000000000000004: function(arg0, arg1) {
      const ret = getArrayU8FromWasm0(arg0, arg1);
      return ret;
    },
    __wbindgen_generic_0000000000000005: function(arg0, arg1) {
      const ret = getStringFromWasm0(arg0, arg1);
      return ret;
    },
    __wbindgen_init_externref_table: function() {
      const table = wasm.__wbindgen_externrefs;
      const offset = table.grow(4);
      table.set(0, void 0);
      table.set(offset + 0, void 0);
      table.set(offset + 1, null);
      table.set(offset + 2, true);
      table.set(offset + 3, false);
    }
  };
  return {
    __proto__: null,
    "./tor_js_bg.js": import0
  };
}
function wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke_______true_(arg0, arg1) {
  wasm.wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke_______true_(arg0, arg1);
}
function wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___wasm_bindgen_2e541aa8012e5493___JsValue______true_(arg0, arg1, arg2) {
  wasm.wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___wasm_bindgen_2e541aa8012e5493___JsValue______true_(arg0, arg1, arg2);
}
function wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___wasm_bindgen_2e541aa8012e5493___JsValue__core_9b3796e30d99ddb7___result__Result_____wasm_bindgen_2e541aa8012e5493___JsError___true_(arg0, arg1, arg2) {
  const ret = wasm.wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___wasm_bindgen_2e541aa8012e5493___JsValue__core_9b3796e30d99ddb7___result__Result_____wasm_bindgen_2e541aa8012e5493___JsError___true_(arg0, arg1, arg2);
  if (ret[1]) {
    throw takeFromExternrefTable0(ret[0]);
  }
}
function wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___js_sys_4841e98648b61fa6___Function_fn_wasm_bindgen_2e541aa8012e5493___JsValue_____wasm_bindgen_2e541aa8012e5493___sys__Undefined___js_sys_4841e98648b61fa6___Function_fn_wasm_bindgen_2e541aa8012e5493___JsValue_____wasm_bindgen_2e541aa8012e5493___sys__Undefined_______true_(arg0, arg1, arg2, arg3) {
  wasm.wasm_bindgen_2e541aa8012e5493___convert__closures_____invoke___js_sys_4841e98648b61fa6___Function_fn_wasm_bindgen_2e541aa8012e5493___JsValue_____wasm_bindgen_2e541aa8012e5493___sys__Undefined___js_sys_4841e98648b61fa6___Function_fn_wasm_bindgen_2e541aa8012e5493___JsValue_____wasm_bindgen_2e541aa8012e5493___sys__Undefined_______true_(arg0, arg1, arg2, arg3);
}
var __wbindgen_enum_ReadableStreamType = ["bytes"];
var IntoUnderlyingByteSourceFinalization = typeof FinalizationRegistry === "undefined" ? { register: () => {
}, unregister: () => {
} } : new FinalizationRegistry((ptr) => wasm.__wbg_intounderlyingbytesource_free(ptr, 1));
var IntoUnderlyingSinkFinalization = typeof FinalizationRegistry === "undefined" ? { register: () => {
}, unregister: () => {
} } : new FinalizationRegistry((ptr) => wasm.__wbg_intounderlyingsink_free(ptr, 1));
var IntoUnderlyingSourceFinalization = typeof FinalizationRegistry === "undefined" ? { register: () => {
}, unregister: () => {
} } : new FinalizationRegistry((ptr) => wasm.__wbg_intounderlyingsource_free(ptr, 1));
var TorClientFinalization = typeof FinalizationRegistry === "undefined" ? { register: () => {
}, unregister: () => {
} } : new FinalizationRegistry((ptr) => wasm.__wbg_torclient_free(ptr, 1));
var TorClientOptionsFinalization = typeof FinalizationRegistry === "undefined" ? { register: () => {
}, unregister: () => {
} } : new FinalizationRegistry((ptr) => wasm.__wbg_torclientoptions_free(ptr, 1));
function addToExternrefTable0(obj) {
  const idx = wasm.__externref_table_alloc_command_export();
  wasm.__wbindgen_externrefs.set(idx, obj);
  return idx;
}
function _assertClass(instance, klass) {
  if (!(instance instanceof klass)) {
    throw new Error(`expected instance of ${klass.name}`);
  }
}
var CLOSURE_DTORS = typeof FinalizationRegistry === "undefined" ? { register: () => {
}, unregister: () => {
} } : new FinalizationRegistry((state) => wasm.__wbindgen_destroy_closure_command_export(state.a, state.b));
function debugString(val) {
  const type = typeof val;
  if (type == "number" || type == "boolean" || val == null) {
    return `${val}`;
  }
  if (type == "string") {
    return `"${val}"`;
  }
  if (type == "symbol") {
    const description = val.description;
    if (description == null) {
      return "Symbol";
    } else {
      return `Symbol(${description})`;
    }
  }
  if (type == "function") {
    const name = val.name;
    if (typeof name == "string" && name.length > 0) {
      return `Function(${name})`;
    } else {
      return "Function";
    }
  }
  if (Array.isArray(val)) {
    const length = val.length;
    let debug = "[";
    if (length > 0) {
      debug += debugString(val[0]);
    }
    for (let i = 1; i < length; i++) {
      debug += ", " + debugString(val[i]);
    }
    debug += "]";
    return debug;
  }
  const builtInMatches = /\[object ([^\]]+)\]/.exec(toString.call(val));
  let className;
  if (builtInMatches && builtInMatches.length > 1) {
    className = builtInMatches[1];
  } else {
    return toString.call(val);
  }
  if (className == "Object") {
    try {
      return "Object(" + JSON.stringify(val) + ")";
    } catch (_) {
      return "Object";
    }
  }
  if (val instanceof Error) {
    return `${val.name}: ${val.message}
${val.stack}`;
  }
  return className;
}
function getArrayU8FromWasm0(ptr, len) {
  ptr = ptr >>> 0;
  return getUint8ArrayMemory0().subarray(ptr / 1, ptr / 1 + len);
}
var cachedDataViewMemory0 = null;
function getDataViewMemory0() {
  if (cachedDataViewMemory0 === null || cachedDataViewMemory0.buffer.detached === true || cachedDataViewMemory0.buffer.detached === void 0 && cachedDataViewMemory0.buffer !== wasm.memory.buffer) {
    cachedDataViewMemory0 = new DataView(wasm.memory.buffer);
  }
  return cachedDataViewMemory0;
}
function getStringFromWasm0(ptr, len) {
  return decodeText(ptr >>> 0, len);
}
var cachedUint8ArrayMemory0 = null;
function getUint8ArrayMemory0() {
  if (cachedUint8ArrayMemory0 === null || cachedUint8ArrayMemory0.byteLength === 0) {
    cachedUint8ArrayMemory0 = new Uint8Array(wasm.memory.buffer);
  }
  return cachedUint8ArrayMemory0;
}
function handleError(f, args) {
  try {
    return f.apply(this, args);
  } catch (e) {
    const idx = addToExternrefTable0(e);
    wasm.__wbindgen_exn_store_command_export(idx);
  }
}
function isLikeNone(x) {
  return x === void 0 || x === null;
}
function makeMutClosure(arg0, arg1, f) {
  const state = { a: arg0, b: arg1, cnt: 1 };
  const real = (...args) => {
    state.cnt++;
    const a = state.a;
    state.a = 0;
    try {
      return f(a, state.b, ...args);
    } finally {
      state.a = a;
      real._wbg_cb_unref();
    }
  };
  real._wbg_cb_unref = () => {
    if (--state.cnt === 0) {
      wasm.__wbindgen_destroy_closure_command_export(state.a, state.b);
      state.a = 0;
      CLOSURE_DTORS.unregister(state);
    }
  };
  CLOSURE_DTORS.register(real, state, state);
  return real;
}
function passStringToWasm0(arg, malloc, realloc) {
  if (realloc === void 0) {
    const buf = cachedTextEncoder.encode(arg);
    const ptr2 = malloc(buf.length, 1) >>> 0;
    getUint8ArrayMemory0().subarray(ptr2, ptr2 + buf.length).set(buf);
    WASM_VECTOR_LEN = buf.length;
    return ptr2;
  }
  let len = arg.length;
  let ptr = malloc(len, 1) >>> 0;
  const mem = getUint8ArrayMemory0();
  let offset = 0;
  for (; offset < len; offset++) {
    const code = arg.charCodeAt(offset);
    if (code > 127) break;
    mem[ptr + offset] = code;
  }
  if (offset !== len) {
    if (offset !== 0) {
      arg = arg.slice(offset);
    }
    ptr = realloc(ptr, len, len = offset + arg.length * 3, 1) >>> 0;
    const view = getUint8ArrayMemory0().subarray(ptr + offset, ptr + len);
    const ret = cachedTextEncoder.encodeInto(arg, view);
    offset += ret.written;
    ptr = realloc(ptr, len, offset, 1) >>> 0;
  }
  WASM_VECTOR_LEN = offset;
  return ptr;
}
function takeFromExternrefTable0(idx) {
  const value = wasm.__wbindgen_externrefs.get(idx);
  wasm.__externref_table_dealloc_command_export(idx);
  return value;
}
var cachedTextDecoder = new TextDecoder("utf-8", { ignoreBOM: true, fatal: true });
cachedTextDecoder.decode();
var MAX_SAFARI_DECODE_BYTES = 2146435072;
var numBytesDecoded = 0;
function decodeText(ptr, len) {
  numBytesDecoded += len;
  if (numBytesDecoded >= MAX_SAFARI_DECODE_BYTES) {
    cachedTextDecoder = new TextDecoder("utf-8", { ignoreBOM: true, fatal: true });
    cachedTextDecoder.decode();
    numBytesDecoded = len;
  }
  return cachedTextDecoder.decode(getUint8ArrayMemory0().subarray(ptr, ptr + len));
}
var cachedTextEncoder = new TextEncoder();
if (!("encodeInto" in cachedTextEncoder)) {
  cachedTextEncoder.encodeInto = function(arg, view) {
    const buf = cachedTextEncoder.encode(arg);
    view.set(buf);
    return {
      read: arg.length,
      written: buf.length
    };
  };
}
var WASM_VECTOR_LEN = 0;
var wasmModule;
var wasmInstance;
var wasm;
function __wbg_finalize_init(instance, module2) {
  wasmInstance = instance;
  wasm = instance.exports;
  wasmModule = module2;
  cachedDataViewMemory0 = null;
  cachedUint8ArrayMemory0 = null;
  wasm.__wbindgen_start();
  return wasm;
}
async function __wbg_load(module2, imports) {
  if (typeof Response === "function" && module2 instanceof Response) {
    if (!module2.ok) {
      throw new Error(`failed to fetch Wasm: ${module2.status} ${module2.statusText} fetching '${module2.url}'`);
    }
    if (typeof WebAssembly.instantiateStreaming === "function") {
      try {
        return await WebAssembly.instantiateStreaming(module2, imports);
      } catch (e) {
        const validResponse = expectedResponseType(module2.type);
        if (validResponse && module2.headers.get("Content-Type") !== "application/wasm") {
          console.warn("`WebAssembly.instantiateStreaming` failed because your server does not serve Wasm with `application/wasm` MIME type. Falling back to `WebAssembly.instantiate` which is slower. Original error:\n", e);
        } else {
          throw e;
        }
      }
    }
    const bytes = await module2.arrayBuffer();
    return await WebAssembly.instantiate(bytes, imports);
  } else {
    const instance = await WebAssembly.instantiate(module2, imports);
    if (instance instanceof WebAssembly.Instance) {
      return { instance, module: module2 };
    } else {
      return instance;
    }
  }
  function expectedResponseType(type) {
    switch (type) {
      case "basic":
      case "cors":
      case "default":
        return true;
    }
    return false;
  }
}
async function __wbg_init(module_or_path) {
  if (wasm !== void 0) return wasm;
  if (module_or_path !== void 0) {
    if (Object.getPrototypeOf(module_or_path) === Object.prototype) {
      ({ module_or_path } = module_or_path);
    } else {
      console.warn("using deprecated parameters for the initialization function; pass a single object instead");
    }
  }
  if (module_or_path === void 0) {
    module_or_path = new URL("tor_js_bg.wasm", import.meta.url);
  }
  const imports = __wbg_get_imports();
  if (typeof module_or_path === "string" || typeof Request === "function" && module_or_path instanceof Request || typeof URL === "function" && module_or_path instanceof URL) {
    module_or_path = fetch(module_or_path);
  }
  const { instance, module: module2 } = await __wbg_load(await module_or_path, imports);
  return __wbg_finalize_init(instance, module2);
}

// src/wasm.ts
var LEVEL_ORDER = ["trace", "debug", "info", "warn", "error"];
function levelIndex(level) {
  const idx = LEVEL_ORDER.indexOf(level);
  return idx === -1 ? 1 : idx;
}
var logListeners = /* @__PURE__ */ new Map();
function syncWasmLogLevel() {
  let broadestIdx = LEVEL_ORDER.length - 1;
  for (const listener of logListeners.values()) {
    if (listener.levelIdx < broadestIdx) {
      broadestIdx = listener.levelIdx;
    }
  }
  setLogLevel(LEVEL_ORDER[broadestIdx]);
}
function addLogListener(cb, level = "debug") {
  logListeners.set(cb, { callback: cb, levelIdx: levelIndex(level) });
  syncWasmLogLevel();
  return () => {
    logListeners.delete(cb);
    if (logListeners.size > 0) {
      syncWasmLogLevel();
    }
  };
}
function setListenerLevel(cb, level) {
  const listener = logListeners.get(cb);
  if (listener) {
    listener.levelIdx = levelIndex(level);
    syncWasmLogLevel();
  }
}
var initPromise = null;
var customWasmUrl;
var wasmSourceProvider;
function setWasmUrl(url) {
  if (initPromise) {
    throw new Error("setWasmUrl() must be called before any TorClient is created");
  }
  customWasmUrl = url;
}
function setWasmSourceProvider(provider) {
  if (initPromise) {
    throw new Error("setWasmSourceProvider() must be called before any TorClient is created");
  }
  wasmSourceProvider = provider;
}
async function ensureWasmInitialized() {
  if (initPromise) return initPromise;
  initPromise = doInit();
  return initPromise;
}
async function doInit() {
  if (customWasmUrl) {
    await __wbg_init({ module_or_path: customWasmUrl });
  } else if (wasmSourceProvider) {
    await __wbg_init({ module_or_path: await wasmSourceProvider() });
  } else {
    throw new Error(
      "No WASM source configured. Import from a specific entry point (tor-js/wasm-base64, tor-js/wasm-cdn, or tor-js/wasm-file) or call setWasmUrl() before creating a TorClient."
    );
  }
  init();
  setLogCallback((level, target, message) => {
    const lvl = levelIndex(level);
    for (const listener of logListeners.values()) {
      if (lvl >= listener.levelIdx) {
        listener.callback(level, target, message);
      }
    }
  });
}

// src/Log.ts
var LEVEL_ORDER2 = ["trace", "debug", "info", "warn", "error"];
function levelEnabled(level, min) {
  return LEVEL_ORDER2.indexOf(level) >= LEVEL_ORDER2.indexOf(min);
}
var warnedOnce = /* @__PURE__ */ new Set();
var Log = class _Log {
  rawLog;
  rawLogOnce;
  parentStartTime;
  namePrefix;
  constructor(params = {}) {
    this.parentStartTime = params.parentStartTime ?? Date.now();
    this.namePrefix = params.namePrefix ?? "";
    this.rawLog = params.rawLog ?? this.defaultRawLog.bind(this);
    this.rawLogOnce = params.rawLogOnce ?? this.rawLog;
  }
  child(name) {
    const newPrefix = this.namePrefix ? `${this.namePrefix}.${name}` : name;
    return new _Log({
      rawLog: this.rawLog,
      rawLogOnce: this.rawLogOnce,
      parentStartTime: this.parentStartTime,
      namePrefix: newPrefix
    });
  }
  trace(...args) {
    this.log("trace", ...args);
  }
  debug(...args) {
    this.log("debug", ...args);
  }
  info(...args) {
    this.log("info", ...args);
  }
  warn(...args) {
    this.log("warn", ...args);
  }
  error(...args) {
    this.log("error", ...args);
  }
  /**
   * Warn once per process for `key`; later calls with the same key are no-ops.
   * For warnings a person needs to see, such as a demo gateway, which is why
   * TorClient's otherwise-silent default log still delivers these.
   */
  warnOnce(key, ...args) {
    if (warnedOnce.has(key)) return;
    warnedOnce.add(key);
    this.emit(this.rawLogOnce, "warn", args);
  }
  /** @internal Create a callback for WASM setLogCallback */
  _makeWasmCallback() {
    const levels = /* @__PURE__ */ new Set(["trace", "debug", "info", "warn", "error"]);
    return (level, target, message) => {
      if (!levels.has(level)) {
        this.log("error", `unexpected log level from WASM: ${JSON.stringify(level)}`);
        level = "debug";
      }
      this.child(target).log(level, message);
    };
  }
  log(level, ...args) {
    this.emit(this.rawLog, level, args);
  }
  emit(raw, level, args) {
    const elapsed = Date.now() - this.parentStartTime;
    const timestamp = formatTimestamp(elapsed);
    if (this.namePrefix) {
      raw(level, `[${timestamp}]`, `[${this.namePrefix}]`, ...args);
    } else {
      raw(level, `[${timestamp}]`, ...args);
    }
  }
  defaultRawLog(level, ...args) {
    console[level](...args);
  }
};
function formatTimestamp(elapsedMs) {
  const totalSeconds = Math.floor(elapsedMs / 1e3);
  const milliseconds = elapsedMs % 1e3;
  const ms = String(milliseconds).padStart(3, "0");
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor(totalSeconds % 86400 / 3600);
  const minutes = Math.floor(totalSeconds % 3600 / 60);
  const seconds = totalSeconds % 60;
  if (days > 0) {
    return `${days}d ${p2(hours)}:${p2(minutes)}:${p2(seconds)}.${ms}`;
  }
  if (hours > 0) {
    return `${p2(hours)}:${p2(minutes)}:${p2(seconds)}.${ms}`;
  }
  if (minutes > 0) {
    return `${p2(minutes)}:${p2(seconds)}.${ms}`;
  }
  return `${p2(seconds)}.${ms}`;
}
function p2(n) {
  return String(n).padStart(2, "0");
}

// src/storage/index.ts
var storage_exports = {};
__export(storage_exports, {
  FilesystemStorage: () => FilesystemStorage,
  IndexedDBStorage: () => IndexedDBStorage,
  MemoryStorage: () => MemoryStorage,
  addLocking: () => addLocking,
  createAutoStorage: () => createAutoStorage
});

// src/storage/memory.ts
var MemoryStorage = class {
  data = /* @__PURE__ */ new Map();
  locked = false;
  async get(key) {
    return this.data.get(key) ?? null;
  }
  async set(key, value) {
    this.data.set(key, value);
  }
  async delete(key) {
    this.data.delete(key);
  }
  async keys(prefix) {
    return [...this.data.keys()].filter((k) => k.startsWith(prefix)).sort();
  }
  async getAll(prefix) {
    const result = [];
    for (const [key, value] of this.data) {
      if (key.startsWith(prefix)) {
        result.push([key, value]);
      }
    }
    return result;
  }
  async tryLock() {
    if (this.locked) return false;
    this.locked = true;
    return true;
  }
  async unlock() {
    this.locked = false;
  }
};

// src/storage/indexeddb.ts
var IndexedDBStorage = class {
  dbName;
  storeName = "keyvalue";
  dbPromise = null;
  constructor(name = "tor-js") {
    this.dbName = name;
  }
  getDB() {
    if (!this.dbPromise) {
      this.dbPromise = new Promise((resolve, reject) => {
        const request = indexedDB.open(this.dbName, 1);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result);
        request.onupgradeneeded = (event) => {
          const db = event.target.result;
          if (!db.objectStoreNames.contains(this.storeName)) {
            db.createObjectStore(this.storeName);
          }
        };
      });
    }
    return this.dbPromise;
  }
  async get(key) {
    const db = await this.getDB();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(this.storeName, "readonly");
      const store = tx.objectStore(this.storeName);
      const request = store.get(key);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        resolve(request.result === void 0 ? null : request.result);
      };
    });
  }
  async set(key, value) {
    const db = await this.getDB();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(this.storeName, "readwrite");
      const store = tx.objectStore(this.storeName);
      const request = store.put(value, key);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => resolve();
    });
  }
  async delete(key) {
    const db = await this.getDB();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(this.storeName, "readwrite");
      const store = tx.objectStore(this.storeName);
      const request = store.delete(key);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => resolve();
    });
  }
  async keys(prefix) {
    const db = await this.getDB();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(this.storeName, "readonly");
      const store = tx.objectStore(this.storeName);
      const request = store.getAllKeys();
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const allKeys = request.result;
        resolve(allKeys.filter((k) => k.startsWith(prefix)).sort());
      };
    });
  }
  async getAll(prefix) {
    const db = await this.getDB();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(this.storeName, "readonly");
      const store = tx.objectStore(this.storeName);
      const keysReq = store.getAllKeys();
      const valsReq = store.getAll();
      tx.onerror = () => reject(tx.error);
      tx.oncomplete = () => {
        const keys = keysReq.result;
        const vals = valsReq.result;
        const result = [];
        for (let i = 0; i < keys.length; i++) {
          if (keys[i].startsWith(prefix)) {
            result.push([keys[i], vals[i]]);
          }
        }
        resolve(result);
      };
    });
  }
};

// src/storage/node-deps.ts
var promise;
function getNodeDeps() {
  if (!promise) {
    promise = (async () => {
      const [fs, fsSync, os, path] = await Promise.all([
        import("fs/promises").then((m) => m.default ?? m),
        import("fs").then((m) => m.default ?? m),
        import("os").then((m) => m.default ?? m),
        import("path").then((m) => m.default ?? m)
      ]);
      return { fs, fsSync, os, path };
    })();
  }
  return promise;
}

// src/storage/filesystem.ts
function isNodeError(err) {
  return err instanceof Error && "code" in err;
}
function mangleKey(key) {
  let result = "";
  for (let i = 0; i < key.length; i++) {
    const code = key.charCodeAt(i);
    if (code >= 97 && code <= 122 || // a-z
    code >= 65 && code <= 90 || // A-Z
    code >= 48 && code <= 57) {
      result += key[i];
    } else if (code <= 255) {
      result += "_" + code.toString(16).padStart(2, "0") + "_";
    } else {
      result += "_" + code.toString(16).padStart(4, "0") + "_";
    }
  }
  return result;
}
function unmangleKey(filename) {
  let result = "";
  let i = 0;
  while (i < filename.length) {
    if (filename[i] === "_") {
      if (i + 5 < filename.length && filename[i + 5] === "_") {
        const hex = filename.slice(i + 1, i + 5);
        if (/^[0-9a-f]{4}$/i.test(hex)) {
          result += String.fromCharCode(parseInt(hex, 16));
          i += 6;
          continue;
        }
      }
      if (i + 3 < filename.length && filename[i + 3] === "_") {
        const hex = filename.slice(i + 1, i + 3);
        if (/^[0-9a-f]{2}$/i.test(hex)) {
          result += String.fromCharCode(parseInt(hex, 16));
          i += 4;
          continue;
        }
      }
      result += "_";
      i++;
    } else {
      result += filename[i];
      i++;
    }
  }
  return result;
}
var FilesystemStorage = class _FilesystemStorage {
  dirPath;
  name;
  resolvedDirPath = null;
  initialized = false;
  constructor(dirPath) {
    this.dirPath = dirPath;
    this.name = null;
  }
  static localShare(name) {
    const s = new _FilesystemStorage("");
    s.dirPath = null;
    s.name = name;
    return s;
  }
  async resolvedDir() {
    if (!this.resolvedDirPath) {
      if (this.dirPath) {
        this.resolvedDirPath = this.dirPath;
      } else {
        const { os, path } = await getNodeDeps();
        this.resolvedDirPath = path.join(os.homedir(), ".local", "share", this.name);
      }
    }
    return this.resolvedDirPath;
  }
  async ensureDir() {
    if (!this.initialized) {
      const { fs } = await getNodeDeps();
      await fs.mkdir(await this.resolvedDir(), { recursive: true });
      this.initialized = true;
    }
  }
  async filePath(key) {
    const { path } = await getNodeDeps();
    return path.join(await this.resolvedDir(), mangleKey(key));
  }
  async get(key) {
    const { fs } = await getNodeDeps();
    await this.ensureDir();
    try {
      return await fs.readFile(await this.filePath(key), "utf-8");
    } catch (err) {
      if (isNodeError(err) && err.code === "ENOENT") return null;
      throw err;
    }
  }
  async set(key, value) {
    const { fs } = await getNodeDeps();
    await this.ensureDir();
    await fs.writeFile(await this.filePath(key), value, "utf-8");
  }
  async delete(key) {
    const { fs } = await getNodeDeps();
    await this.ensureDir();
    try {
      await fs.unlink(await this.filePath(key));
    } catch (err) {
      if (isNodeError(err) && err.code === "ENOENT") return;
      throw err;
    }
  }
  async keys(prefix) {
    const { fs } = await getNodeDeps();
    await this.ensureDir();
    try {
      const files = await fs.readdir(await this.resolvedDir());
      return files.map(unmangleKey).filter((k) => k.startsWith(prefix)).sort();
    } catch (err) {
      if (isNodeError(err) && err.code === "ENOENT") return [];
      throw err;
    }
  }
  async getAll(prefix) {
    const { fs } = await getNodeDeps();
    await this.ensureDir();
    try {
      const files = await fs.readdir(await this.resolvedDir());
      const keys = files.map(unmangleKey).filter((k) => k.startsWith(prefix));
      const entries = await Promise.all(
        keys.map(async (key) => {
          const value = await this.get(key);
          return value !== null ? [key, value] : null;
        })
      );
      return entries.filter((e) => e !== null);
    } catch (err) {
      if (isNodeError(err) && err.code === "ENOENT") return [];
      throw err;
    }
  }
};

// src/storage/locking.ts
function isNodeError2(err) {
  return err instanceof Error && "code" in err;
}
function addLocking(inner, name) {
  let hasRealLock = false;
  let overlay = null;
  let releaseLock;
  let lockRequestDone;
  let lockPath = null;
  let exitHandler = null;
  let heartbeatTimer = null;
  const STALE_MS = 3e4;
  const HEARTBEAT_MS = 1e4;
  async function tryAcquireReal() {
    if (typeof navigator !== "undefined" && navigator.locks) {
      let resolveAcquired;
      const acquired = new Promise((r) => {
        resolveAcquired = r;
      });
      lockRequestDone = navigator.locks.request(
        `tor-js:${name}`,
        { ifAvailable: true },
        (lock) => {
          if (lock) {
            resolveAcquired(true);
            return new Promise((r) => {
              releaseLock = r;
            });
          }
          resolveAcquired(false);
        }
      );
      return acquired;
    }
    if (typeof process !== "undefined" && process.versions?.node) {
      try {
        const { fs, fsSync, path, os } = await getNodeDeps();
        const dir = path.join(os.homedir(), ".local", "share", name);
        await fs.mkdir(dir, { recursive: true });
        const lp = path.join(dir, ".lock");
        try {
          await fs.writeFile(lp, `${process.pid}`, { flag: "wx" });
        } catch (err) {
          if (!isNodeError2(err) || err.code !== "EEXIST") throw err;
          const stat = await fs.stat(lp);
          if (Date.now() - stat.mtimeMs < STALE_MS) return false;
          await fs.writeFile(lp, `${process.pid}`);
        }
        lockPath = lp;
        heartbeatTimer = setInterval(async () => {
          try {
            const now = /* @__PURE__ */ new Date();
            await fs.utimes(lp, now, now);
          } catch {
          }
        }, HEARTBEAT_MS);
        if (heartbeatTimer.unref) heartbeatTimer.unref();
        exitHandler = () => {
          try {
            fsSync.unlinkSync(lp);
          } catch {
          }
        };
        process.on("exit", exitHandler);
        return true;
      } catch (err) {
        return false;
      }
    }
    throw new Error("Failed to detect suitable locking mechanism");
  }
  async function releaseReal() {
    if (releaseLock) {
      releaseLock();
      releaseLock = void 0;
      await lockRequestDone;
      lockRequestDone = void 0;
    }
    if (heartbeatTimer) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
    if (lockPath) {
      const { fs } = await getNodeDeps();
      try {
        await fs.unlink(lockPath);
      } catch (err) {
        if (!isNodeError2(err) || err.code !== "ENOENT") throw err;
      }
      lockPath = null;
    }
    if (exitHandler) {
      process.removeListener("exit", exitHandler);
      exitHandler = null;
    }
  }
  return {
    async get(key) {
      if (overlay?.has(key)) return overlay.get(key);
      return inner.get(key);
    },
    async set(key, value) {
      if (overlay) {
        overlay.set(key, value);
        return;
      }
      return inner.set(key, value);
    },
    async delete(key) {
      if (overlay) {
        overlay.set(key, null);
        return;
      }
      return inner.delete(key);
    },
    async keys(prefix) {
      const base = await inner.keys(prefix);
      if (!overlay) return base;
      const result = new Set(base);
      for (const [k, v] of overlay) {
        if (!k.startsWith(prefix)) continue;
        if (v !== null) result.add(k);
        else result.delete(k);
      }
      return [...result].sort();
    },
    async getAll(prefix) {
      const base = await inner.getAll(prefix);
      if (!overlay) return base;
      const merged = new Map(base);
      for (const [k, v] of overlay) {
        if (!k.startsWith(prefix)) continue;
        if (v !== null) merged.set(k, v);
        else merged.delete(k);
      }
      return [...merged.entries()];
    },
    async tryLock() {
      if (hasRealLock) return false;
      const acquired = await tryAcquireReal();
      hasRealLock = acquired;
      overlay = acquired ? null : overlay ?? /* @__PURE__ */ new Map();
      return true;
    },
    async unlock() {
      await releaseReal();
      hasRealLock = false;
      overlay = null;
    }
  };
}

// src/storage/index.ts
function createAutoStorage(name = "tor-js") {
  if (typeof globalThis !== "undefined" && typeof globalThis.indexedDB !== "undefined") {
    return addLocking(new IndexedDBStorage(name), name);
  }
  if (typeof process !== "undefined" && process.versions?.node) {
    return addLocking(FilesystemStorage.localShare(name), name);
  }
  throw new Error(
    "No persistent storage available: need IndexedDB (browser) or filesystem (Node.js)"
  );
}

// src/kpsAddress.ts
function parseAddress(s) {
  const malformed = () => new Error(
    `address: malformed (expected <ip>:<port>:<certhash> or [ipv6]:<port>:<certhash>): ${s}`
  );
  let ip;
  let rest;
  if (s.startsWith("[")) {
    const end = s.indexOf("]");
    if (end < 0 || s[end + 1] !== ":") throw malformed();
    ip = s.slice(1, end);
    rest = s.slice(end + 2);
  } else {
    const i = s.indexOf(":");
    if (i < 0) throw malformed();
    ip = s.slice(0, i);
    rest = s.slice(i + 1);
  }
  const j = rest.indexOf(":");
  if (j < 0) throw malformed();
  const portStr = rest.slice(0, j);
  const certhash = rest.slice(j + 1);
  if (!/^\d+$/.test(portStr)) throw malformed();
  const port = Number(portStr);
  if (port < 1 || port > 65535) throw new Error("address: port out of range");
  if (!ip || !certhash) throw malformed();
  return { ip, port, certhash };
}
function formatAddress(addr) {
  const host = addr.ip.includes(":") ? `[${addr.ip}]` : addr.ip;
  return `${host}:${addr.port}:${addr.certhash}`;
}

// src/kpsGateway.ts
var enc = new TextEncoder();
var dec = new TextDecoder();
var OPEN_STREAM_TIMEOUT_MS = 2e4;
function abortRace(p, signal, what) {
  if (!signal) return p;
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(new Error(`${what}: timed out`));
    if (signal.aborted) return onAbort();
    signal.addEventListener("abort", onAbort, { once: true });
    p.then(resolve, reject).finally(() => signal.removeEventListener("abort", onAbort));
  });
}
async function readHead(reader) {
  let buf = new Uint8Array(0);
  for (; ; ) {
    const sep = findHeadEnd(buf);
    if (sep !== -1) {
      const head = dec.decode(buf.subarray(0, sep));
      const lines = head.split("\r\n");
      const m = lines[0].match(/^HTTP\/1\.1 (\d{3})\s*(.*)$/);
      if (!m) throw new Error(`malformed status line: ${lines[0]}`);
      const headers = {};
      for (const line of lines.slice(1)) {
        const i = line.indexOf(":");
        if (i === -1) continue;
        headers[line.slice(0, i).toLowerCase()] = line.slice(i + 1).trim();
      }
      return {
        status: parseInt(m[1], 10),
        statusText: m[2],
        headers,
        extra: buf.subarray(sep + 4)
      };
    }
    const { done, value } = await reader.read();
    if (done) throw new Error("stream ended before response head");
    const next = new Uint8Array(buf.length + value.length);
    next.set(buf, 0);
    next.set(value, buf.length);
    buf = next;
  }
}
function findHeadEnd(buf) {
  for (let i = 0; i + 3 < buf.length; i++) {
    if (buf[i] === 13 && buf[i + 1] === 10 && buf[i + 2] === 13 && buf[i + 3] === 10) {
      return i;
    }
  }
  return -1;
}
function concat(chunks, length) {
  const out = new Uint8Array(length);
  let off = 0;
  for (const c of chunks) {
    out.set(c, off);
    off += c.length;
  }
  return out;
}
var KpsGateway = class {
  #address;
  #certhash;
  #connPromise = null;
  // Per-connection teardown callbacks. The JS kps client does not reliably
  // settle streams when the connection dies (kps ISSUES #4) — a reader
  // blocked on stream.readable can hang forever — so every socket/exchange
  // registers a teardown that conn.closed triggers.
  #teardowns = /* @__PURE__ */ new Map();
  #closed = false;
  #dial;
  #log;
  // The demo check runs at most once per gateway, whatever the outcome: it is
  // a courtesy notice, not something to retry or repeat per request.
  #demoChecked = false;
  /**
   * @param address KPS address (`ip:port:certhash`).
   * @param options Optional {@link KpsGatewayOptions} (e.g. a custom `dial`).
   */
  constructor(address, options = {}) {
    this.#address = address.trim();
    this.#certhash = parseAddress(this.#address).certhash;
    this.#dial = options.dial;
    this.#log = options.log;
  }
  /**
   * Warn once if the gateway declares itself a demonstration instance
   * (`"demo": true` in `/metadata.json`, PROTOCOL.md §5).
   *
   * Deliberately fire-and-forget: it costs one small exchange on a connection
   * that is already up, and nothing about it may delay or fail real work — a
   * gateway that never answers this is still a working gateway. Errors are
   * swallowed for the same reason, and the absent field means `false`, because
   * an older gateway cannot say.
   */
  #checkDemo() {
    if (this.#demoChecked || !this.#log) return;
    this.#demoChecked = true;
    void (async () => {
      const res = await this.fetch("/metadata.json", {
        signal: AbortSignal.timeout(1e4)
      });
      if (res.status !== 200) return;
      const meta = JSON.parse(new TextDecoder().decode(res.body));
      if (meta.demo !== true) return;
      this.#log?.warnOnce(
        `demo-gateway:${this.#address}`,
        `Gateway ${this.#address} reports itself as a demo instance: limited capacity, and it may disappear at any time. Run your own gateway for anything real: https://github.com/ethereum/tor-js/tree/main/crates/tor-js-gateway`
      );
    })().catch(() => {
    });
  }
  get address() {
    return this.#address;
  }
  /** Dial (or reuse) the KPS connection, optionally bounding this caller's wait. */
  async #connection(signal) {
    if (this.#closed) throw new Error("KpsGateway is closed");
    if (!this.#connPromise) {
      const dialP = this.#dial ? Promise.resolve(this.#dial) : Promise.resolve().then(() => (init_kpsDial(), kpsDial_exports)).then((m) => this.#dial = m.kpsDial);
      const p = dialP.then((dial2) => dial2(this.#address)).then(
        (conn) => {
          this.#teardowns.set(conn, /* @__PURE__ */ new Set());
          const onClosed = () => {
            if (this.#connPromise === p) this.#connPromise = null;
            const teardowns = this.#teardowns.get(conn);
            this.#teardowns.delete(conn);
            for (const fn of teardowns ?? []) fn();
          };
          conn.closed.then(onClosed, onClosed);
          this.#checkDemo();
          return conn;
        },
        (err) => {
          if (this.#connPromise === p) this.#connPromise = null;
          throw err;
        }
      );
      this.#connPromise = p;
    }
    return abortRace(this.#connPromise, signal, `dial ${this.#address}`);
  }
  #addTeardown(conn, fn) {
    const set = this.#teardowns.get(conn);
    if (!set) {
      queueMicrotask(fn);
      return () => {
      };
    }
    set.add(fn);
    return () => set.delete(fn);
  }
  async #openStream(conn, signal) {
    return conn.openStream({ signal: signal ?? AbortSignal.timeout(OPEN_STREAM_TIMEOUT_MS) });
  }
  /**
   * One KPS-HTTP/1 GET exchange (PROTOCOL.md §3): write the request, FIN,
   * then read the response; the body ends at EOF.
   *
   * @param path Absolute request path (e.g. "/bootstrap.zip.zst").
   * @param opts Optional `signal` bounding the whole exchange.
   */
  async fetch(path, opts = {}) {
    const { signal } = opts;
    const conn = await this.#connection(signal);
    const stream = await this.#openStream(conn, signal);
    const reader = stream.readable.getReader();
    const removeTeardown = this.#addTeardown(conn, () => {
      reader.cancel(new Error("kps connection closed")).catch(() => {
      });
      stream.close().catch(() => {
      });
    });
    try {
      const writer = stream.writable.getWriter();
      await writer.write(enc.encode(`GET ${path} HTTP/1.1\r
Host: ${this.#certhash}\r
\r
`));
      await writer.close();
      const { status, statusText, headers, extra } = await abortRace(
        readHead(reader),
        signal,
        `GET ${path}`
      );
      const chunks = [];
      let length = 0;
      if (extra.length) {
        chunks.push(extra);
        length += extra.length;
      }
      for (; ; ) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value?.length) {
          chunks.push(value);
          length += value.length;
        }
      }
      return { status, statusText, headers, body: concat(chunks, length) };
    } finally {
      removeTeardown();
      stream.close().catch(() => {
      });
    }
  }
  /**
   * Open a TCP tunnel to a Tor relay via CONNECT (PROTOCOL.md §4). After
   * the gateway's 200 the stream is the raw byte pipe to the target.
   *
   * @param target Relay address as "ip:port" (consensus relays only).
   * @param opts Optional `signal` bounding setup (dial, stream, CONNECT reply);
   *   it does not affect the tunnel once established.
   */
  async connect(target, opts = {}) {
    const { signal } = opts;
    const conn = await this.#connection(signal);
    const stream = await this.#openStream(conn, signal);
    const reader = stream.readable.getReader();
    const writer = stream.writable.getWriter();
    await writer.write(enc.encode(`CONNECT ${target} HTTP/1.1\r
Host: ${target}\r
\r
`));
    let head;
    try {
      head = await abortRace(readHead(reader), signal, `CONNECT ${target}`);
    } catch (e) {
      stream.close().catch(() => {
      });
      throw e;
    }
    if (head.status !== 200) {
      let text = dec.decode(head.extra);
      try {
        for (; ; ) {
          const { done, value } = await reader.read();
          if (done) break;
          text += dec.decode(value, { stream: true });
        }
      } catch {
      }
      stream.close().catch(() => {
      });
      throw new Error(`CONNECT ${target}: ${head.status} ${text.trim() || head.statusText}`);
    }
    writer.releaseLock();
    const readable = new ReadableStream({
      start(controller) {
        if (head.extra.length) controller.enqueue(new Uint8Array(head.extra));
      },
      async pull(controller) {
        try {
          const { done, value } = await reader.read();
          if (done) controller.close();
          else if (value?.length) controller.enqueue(new Uint8Array(value));
        } catch (e) {
          controller.error(e);
        }
      },
      cancel(reason) {
        reader.cancel(reason).catch(() => {
        });
      }
    });
    const closed = stream.closed.then(
      (info) => ({ ok: info.ok, reason: info.ok ? void 0 : info.reason?.code ?? "error" }),
      (err) => ({ ok: false, reason: err?.code ?? err?.message ?? "closed" })
    );
    const removeTeardown = this.#addTeardown(conn, () => {
      reader.cancel(new Error("kps connection closed")).catch(() => {
      });
      stream.close().catch(() => {
      });
    });
    stream.closed.then(removeTeardown, removeTeardown);
    return new ArtiSocket({
      readable,
      writable: stream.writable,
      closed,
      closeWrite: () => stream.closeWrite(),
      close: () => {
        stream.close().catch(() => {
        });
      }
    });
  }
  /** Close the underlying KPS connection (all streams/tunnels with it). */
  close() {
    this.#closed = true;
    const p = this.#connPromise;
    this.#connPromise = null;
    if (p) {
      p.then((conn) => conn.close()).catch(() => {
      });
    }
  }
};

// src/socketProvider.ts
var HAS_DENO = typeof globalThis.Deno !== "undefined";
var HAS_NODE = typeof globalThis.process?.versions?.node !== "undefined";
function defaultStrategies(hasGateway) {
  const s = [];
  if (HAS_DENO || HAS_NODE) s.push("direct");
  if (hasGateway) s.push("kps");
  return s;
}
var PREFERRED_GATEWAYS = 2;
var DEFAULT_TIMING = {
  attemptTimeoutMs: 15e3,
  cooldownBaseMs: 2e3,
  cooldownMaxMs: 6e4
};
function shuffled(items) {
  const out = items.slice();
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}
var ArtiSocket = class _ArtiSocket {
  /** Inbound bytes. Pull-based: reading drives the transport's network pull. */
  readable;
  /** Outbound bytes. The writer's backpressure reflects the transport buffer. */
  writable;
  /** Resolves when the socket is fully closed. */
  closed;
  #closeWrite;
  #close;
  constructor(parts) {
    this.readable = parts.readable;
    this.writable = parts.writable;
    this.closed = parts.closed;
    this.#closeWrite = parts.closeWrite;
    this.#close = parts.close;
  }
  /** Half-close the write side (the peer sees a TCP FIN); reads continue. */
  closeWrite() {
    return this.#closeWrite();
  }
  /** Tear down both halves of the socket. */
  close() {
    this.#close();
  }
  // -- Transport factories --------------------------------------------------
  /** Wrap a Node.js net.Socket (already connected) as WHATWG streams. */
  static async fromNodeSocket(socket) {
    const { Duplex } = await import("stream");
    const { readable, writable } = Duplex.toWeb(socket);
    return new _ArtiSocket({
      readable,
      writable,
      closed: new Promise((resolve) => {
        socket.once("close", (hadError) => resolve({ ok: !hadError }));
      }),
      closeWrite: () => new Promise((resolve) => socket.end(resolve)),
      close: () => socket.destroy()
    });
  }
  /** Wrap a Deno TCP connection (whose readable/writable are already WHATWG). */
  static fromDenoConn(conn) {
    let onClosed;
    const closed = new Promise((resolve) => {
      onClosed = () => resolve({ ok: true });
    });
    return new _ArtiSocket({
      readable: conn.readable,
      writable: conn.writable,
      closed,
      closeWrite: () => conn.closeWrite ? conn.closeWrite() : Promise.resolve(),
      close: () => {
        try {
          conn.close();
        } catch {
        }
        onClosed();
      }
    });
  }
};
var ArtiSocketProvider = class {
  #states = [];
  #strategies;
  #timing;
  constructor(options = {}) {
    const addresses = options.gateway == null ? [] : Array.isArray(options.gateway) ? options.gateway : [options.gateway];
    for (const address of shuffled(addresses)) {
      if (/^(https?|wss?):\/\//.test(address)) {
        throw new Error(
          `gateway is now a KPS address ("ip:port:certhash"), not a URL \u2014 got "${address}". Gateways expose their address at startup and in /metadata.json.`
        );
      }
      this.#states.push({
        gw: new KpsGateway(address, { dial: options.dial, log: options.log }),
        inFlight: 0,
        failures: 0,
        notBefore: 0
      });
    }
    this.#strategies = options.strategies ?? defaultStrategies(this.#states.length > 0);
    this.#timing = { ...DEFAULT_TIMING, ...options.timing };
  }
  /**
   * The gateway currently preferred for single-gateway work, or null if none is
   * configured. Prefer {@link gatewayFetch} for requests — it falls over.
   */
  get gateway() {
    return this.#candidates()[0]?.gw ?? null;
  }
  /**
   * Gateways in the order to try them for one operation; the first is the pick,
   * the rest are fallbacks.
   *
   * Least-outstanding decides between members of the preferred set, and is the
   * whole latency story here: a slow or stalled gateway accumulates in-flight
   * work and stops being chosen, with no probing or RTT bookkeeping. Because
   * the sort is stable, equal load keeps the construction-time shuffle — so a
   * client whose tunnels don't overlap reuses one gateway (and fast bootstrap
   * contacts exactly one), while concurrent load spreads across the set.
   */
  #candidates() {
    const now = Date.now();
    const ready = this.#states.filter((s) => s.notBefore <= now);
    const preferred = ready.slice(0, PREFERRED_GATEWAYS).sort((a, b) => a.inFlight - b.inFlight);
    const rest = ready.slice(PREFERRED_GATEWAYS);
    const cooling = this.#states.filter((s) => s.notBefore > now).sort((a, b) => a.notBefore - b.notBefore);
    return [...preferred, ...rest, ...cooling];
  }
  #onSuccess(s) {
    s.failures = 0;
    s.notBefore = 0;
  }
  /** Cool a failed gateway off for min(base·2^(n-1), max), 50-100% jittered. */
  #onFailure(s) {
    s.failures += 1;
    const { cooldownBaseMs, cooldownMaxMs } = this.#timing;
    const exp = Math.min(cooldownMaxMs, cooldownBaseMs * 2 ** (s.failures - 1));
    s.notBefore = Date.now() + Math.round(exp * (0.5 + Math.random() * 0.5));
  }
  /**
   * Open a relay socket to the given target (e.g. "198.51.100.1:9001").
   * Tries each configured strategy in order until one succeeds.
   */
  async connect(target) {
    const errors = [];
    for (const strategy of this.#strategies) {
      try {
        switch (strategy) {
          case "direct":
            return await this.#connectDirect(target);
          case "kps":
            return await this.#connectKps(target);
          default:
            throw new Error(`unknown strategy: ${strategy}`);
        }
      } catch (e) {
        errors.push(`${strategy}: ${e.message}`);
      }
    }
    throw new Error(`all strategies failed for ${target}: ${errors.join("; ")}`);
  }
  /** Close all KPS gateway connections and release resources. */
  close() {
    for (const s of this.#states) s.gw.close();
  }
  // -- Direct TCP strategy (Node.js / Deno) ---------------------------------
  async #connectDirect(target) {
    const [host, portStr] = target.split(":");
    const port = parseInt(portStr, 10);
    if (HAS_DENO) {
      const conn = await globalThis.Deno.connect({ hostname: host, port });
      return ArtiSocket.fromDenoConn(conn);
    }
    if (HAS_NODE) {
      const net = await import("net");
      const socket = net.createConnection({ host, port });
      await new Promise((resolve, reject) => {
        socket.once("connect", resolve);
        socket.once("error", reject);
      });
      return ArtiSocket.fromNodeSocket(socket);
    }
    throw new Error("direct TCP not available in this environment");
  }
  // -- KPS gateway strategy -------------------------------------------------
  async #connectKps(target) {
    if (!this.#states.length) throw new Error("kps strategy requires a gateway address");
    const errors = [];
    for (const s of this.#candidates()) {
      s.inFlight += 1;
      try {
        const sock = await s.gw.connect(target, {
          signal: AbortSignal.timeout(this.#timing.attemptTimeoutMs)
        });
        this.#onSuccess(s);
        const release = () => {
          s.inFlight -= 1;
        };
        sock.closed.then(release, release);
        return sock;
      } catch (e) {
        s.inFlight -= 1;
        this.#onFailure(s);
        errors.push(`${s.gw.address}: ${e.message}`);
      }
    }
    throw new Error(`all gateways failed for ${target}: ${errors.join("; ")}`);
  }
  /**
   * One KPS-HTTP/1 GET against a gateway (used for fast bootstrap), falling
   * over to the next candidate on failure. The happy path contacts exactly one
   * gateway — bootstrap is not raced.
   */
  async gatewayFetch(path) {
    if (!this.#states.length) throw new Error("no gateway configured");
    const errors = [];
    for (const s of this.#candidates()) {
      try {
        const res = await s.gw.fetch(path, {
          signal: AbortSignal.timeout(this.#timing.attemptTimeoutMs)
        });
        this.#onSuccess(s);
        return res;
      } catch (e) {
        this.#onFailure(s);
        errors.push(`${s.gw.address}: ${e.message}`);
      }
    }
    throw new Error(`all gateways failed for ${path}: ${errors.join("; ")}`);
  }
};

// src/TorClient.ts
function isBrowser() {
  const g = globalThis;
  const hasNode = typeof g.process?.versions?.node !== "undefined";
  const hasDeno = typeof g.Deno !== "undefined";
  return !hasNode && !hasDeno && typeof g.window !== "undefined";
}
var TorClient2 = class {
  log;
  // This client's level, as given to its log listener (whose default is
  // 'debug'). Also applied to the JS-side console output below.
  logLevel;
  clientPromise;
  removeLogListener = null;
  wasmCallback = null;
  closed = false;
  readyPromise = null;
  socketProvider = null;
  constructor(options = {}) {
    const hasGateway = Array.isArray(options.gateway) ? options.gateway.length > 0 : !!options.gateway;
    if (isBrowser() && !hasGateway && !options.socketProvider) {
      throw new Error(
        `TorClient: in the browser, you must configure a gateway (KPS address "ip:port:certhash") because browsers can't open regular TCP sockets.`
      );
    }
    this.logLevel = options.logLevel ?? "debug";
    const toConsole = (level, ...args) => {
      if (levelEnabled(level, this.logLevel)) console[level](...args);
    };
    this.log = options.log ?? (options.logLevel ? new Log({ rawLog: toConsole }) : new Log({ rawLog: () => {
    }, rawLogOnce: toConsole }));
    this.clientPromise = this.bootstrap(options);
    this.clientPromise.catch(() => {
    });
  }
  async bootstrap(options) {
    await ensureWasmInitialized();
    this.wasmCallback = this.log._makeWasmCallback();
    this.removeLogListener = addLogListener(this.wasmCallback, options.logLevel);
    this.socketProvider = options.socketProvider ?? new ArtiSocketProvider({ gateway: options.gateway, log: this.log });
    const sp = this.socketProvider;
    let wasmOptions = new TorClientOptions(
      (addr) => sp.connect(addr)
    );
    const storage = options.storage ?? createAutoStorage();
    wasmOptions = wasmOptions.withStorage(storage);
    if (sp.gateway) {
      wasmOptions = wasmOptions.withFastBootstrap(async () => {
        this.log.info("Fast bootstrap: fetching bootstrap.zip.zst...");
        const res = await sp.gatewayFetch("/bootstrap.zip.zst");
        if (res.status !== 200) {
          throw new Error(`Fast bootstrap fetch failed: ${res.status} ${res.statusText}`);
        }
        this.log.info(`Fast bootstrap: received ${res.body.byteLength} bytes (compressed)`);
        return res.body;
      });
    }
    this.log.info("Bootstrapping...");
    const client = await TorClient.create(wasmOptions);
    this.log.info("Bootstrap complete");
    return client;
  }
  /**
   * Make an HTTP fetch request through Tor.
   * Returns a standard browser Response object.
   */
  async fetch(url, init2) {
    if (this.closed) throw new Error("TorClient is closed");
    const client = await this.clientPromise;
    await this.ready();
    this.log.info(`Fetching ${url}`);
    return client.fetch(url, init2);
  }
  /**
   * Wait for the Tor client to be ready for traffic
   * (guard connected, usable consensus, and sufficient microdescs).
   *
   * Parallel callers share the same underlying promise — a single WS
   * connection failure rejects all waiters. The cached promise is cleared
   * on settle so the next call creates a fresh attempt.
   */
  async ready() {
    if (this.closed) throw new Error("TorClient is closed");
    if (this.readyPromise) return this.readyPromise;
    const p = (async () => {
      const startTime = Date.now();
      this.log.info("Waiting for client");
      const client = await this.clientPromise;
      this.log.info("Waiting for client to be ready");
      await client.ready();
      this.log.info(`Client ready in ${Date.now() - startTime}ms`);
    })();
    this.readyPromise = p;
    const clear = () => {
      this.readyPromise = null;
    };
    p.then(clear, clear);
    return p;
  }
  /**
   * Change the log level for this client's listener.
   * Also re-syncs the global WASM filter to the broadest level across all clients.
   */
  setLogLevel(level) {
    this.logLevel = level;
    if (this.wasmCallback) {
      setListenerLevel(this.wasmCallback, level);
    }
  }
  /**
   * Close the TorClient and release resources.
   */
  close() {
    if (this.closed) return;
    this.closed = true;
    this.removeLogListener?.();
    this.removeLogListener = null;
    this.wasmCallback = null;
    this.socketProvider?.close();
    this.socketProvider = null;
    this.clientPromise.then((client) => client.close()).catch(() => {
    });
  }
  [Symbol.dispose]() {
    this.close();
  }
};

// src/entryPoints/wasm-file/index.ts
setWasmSourceProvider(async () => {
  if (typeof process !== "undefined" && process.versions?.node) {
    const { readFile } = await import("fs/promises");
    const { fileURLToPath } = await import("url");
    const wasmPath = fileURLToPath(new URL("../../tor_js_bg.wasm", import.meta.url));
    return readFile(wasmPath);
  }
  const resp = await fetch(new URL("../../tor_js_bg.wasm", import.meta.url));
  if (!resp.ok) throw new Error(`Failed to fetch WASM: HTTP ${resp.status}`);
  return new Uint8Array(await resp.arrayBuffer());
});
export {
  ArtiSocket,
  ArtiSocketProvider,
  KpsGateway,
  Log,
  TorClient2 as TorClient,
  formatAddress,
  parseAddress,
  setWasmUrl,
  storage_exports as storage
};
//# sourceMappingURL=index.js.map