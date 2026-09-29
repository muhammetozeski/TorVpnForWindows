# Comparing browser and native Tor transports

Use `tests/speed.mjs` for browser clients and `tests/speed-native.mjs` for the existing Tor and
lyrebird binaries. These are measurement consumers; they do not change production engine code.
Run download measurements sequentially except for a deliberately labelled parallel experiment.
Keep profiles, Tor data, downloaded samples, dependencies, and raw results under `build/speed`.

## Method and interpretation

The 2026-09-29 comparison used the GitHub release asset supplied by the user:
`https://github.com/szalony9szymek/large/releases/download/free/large`.
Its size was 2,093,168,814 bytes and it supported byte ranges. The measured unit was the first
1,048,576 bytes. Verify response status 206, length, and SHA-256 before accepting a sample.
The verified sample digest is encoded in `tests/speed-report.mjs`.

Report startup time separately from request time. Request time includes the GitHub redirect and
connection setup for that request. Mean speed is the sum of completed bytes divided by the sum
of request times. Parallel speed is total bytes divided by wall time until the last concurrent
request finishes; adding individual speeds would overstate this experiment's result.

Directory downloads can dominate a cold Tor start. The native comparison first attempted cold
starts. Later cases copied only `cached-certs`, `cached-microdesc-consensus`, and microdescriptor
cache files from a successful test session. Tor still validates these documents. No guard state,
identity, locks, or settings were shared. Label these cases as having a ready directory cache;
their startup times cannot be compared as cold starts.

The parallel experiment used three independent native Tor sessions, two using Snowflake bridge
identity `2B280B23E1107BB62ABFC40DDCC8824814F80A72` and one using
`8838024498816A039FCBBAB14E6F40A0843051FA`. All three had different exit addresses. This establishes
three separate Tor sessions and two configured bridges, not three proven distinct volunteer proxies.
Each downloaded the same 1 MiB range; file assembly or adaptive multi-source downloading was not tested.

## Observed results on 2026-09-29

The gateway was `170.64.236.147:12298`, tested with bundled tor-js 0.4.2 in headless Chrome.
Native measurements used the parent's existing Tor and lyrebird payloads, with loopback listeners
and isolated data folders. No Windows tunnel or firewall was activated.

| Method | Successful samples | Mean kilobytes/second |
|---|---:|---:|
| Browser gateway, all three sessions | 7 | 76.9 |
| Browser gateway, final two sessions | 6 | 101.4 |
| Native direct Tor | 2 | 181.3 |
| Native Snowflake bridge 1 | 2 | 88.7 |
| Native Snowflake bridge 2, directory ready | 2 | 167.2 |
| Three concurrent native Snowflake sessions, directory ready | 3 concurrent | 345.0 aggregate |
| Native meek, directory ready | 1 | 29.1 |

The gateway's first sample was slower than its follow-up samples. Tor exits and circuits varied;
these are complete-path measurements, not a diagnosis of the gateway's own capacity.

The two tested obfs4 addresses, `51.222.13.177:80` and `209.148.46.65:443`, did not finish bootstrap
within 120 seconds. Cold Snowflake bridge 2 and meek stopped at 50% within that deadline; both
worked in later sessions with the directory cache ready. These are distinct test conditions.

## Published browser package pitfalls

The webtor-rs 0.5.7 release requested directory snapshots from
`https://igor53627.github.io/webtor-rs`, which returned 404. The current files were available under
`https://ethereum.github.io/webtor-rs`. The browser test fixture remaps only the two snapshot
requests; it does not modify the runtime or change any download target.

After that correction, three Snowflake WebRTC attempts still failed before downloading. The
WebSocket endpoints `wss://snowflake.torproject.net/` (two attempts) and
`wss://snowflake.bamsoftware.com/` (one attempt) also failed to provide a usable circuit.
Record this as a failure of those tested browser paths, not as a speed of zero or a conclusion
that the Snowflake network is unavailable.

The upstream README advertises WebTunnel for WASM, but its
[`BridgeType::WebTunnel` branch](https://github.com/ethereum/webtor-rs/blob/main/webtor/src/client.rs)
explicitly rejects it when compiled for `wasm32`. The runtime capability probe did not establish
a channel. A real WebTunnel server was not speed-tested; the probe's placeholder address must
not be reported as a failed public server.

Native Snowflake measurements demonstrate bridge performance through the existing local client.
They do not demonstrate a working standalone Snowflake Chrome extension. Production extension
files remained unchanged throughout this comparison.
