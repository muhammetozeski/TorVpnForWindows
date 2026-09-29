# Browser integration

## Request path

`background.js` installs a loopback SOCKS proxy without a direct alternative and disables network
prediction. This proxy is a blocking fallback, not the Tor transport. The extension attaches
Chrome's debugger to ordinary web tabs and enables request-stage interception. `forward` passes
each paused request to `engine.js` in an offscreen extension document. The bundled Arti client
fetches through a Tor circuit, and `Fetch.fulfillRequest` returns the response to Chrome.

The offscreen document uses the `WEB_RTC` reason because tor-js reaches its gateway over WebRTC.
Turning off non-proxied WebRTC globally would also break this transport. Instead, `configureTarget`
installs a document-start policy disabling the page's WebRTC and WebTransport entry points and
blocks WebSockets at the target network layer. Related frames and workers are attached before
their scripts resume. Unsupported or failed requests are failed, never retried directly.

This is an experimental HTTP browsing integration, not Tor Browser's complete privacy model.
The supported boundaries and gateway dependency are stated in the [user guide](../README.md).

## Lifecycle and ownership

The service worker owns Chrome settings and debugger sessions. The offscreen document owns the
Tor client and active request cancellation. `epoch` prevents old request results from being
fulfilled after reconnect or disconnect; `generation` provides the same protection inside the
engine. Cancellation can interrupt a pending bootstrap instead of waiting for its deadline.

Chrome persists extension proxy settings across browser restarts. Recovery reconnects when the
saved desired state is enabled; a revived worker can reuse an already connected offscreen engine.
Disconnect stops the engine, detaches debugger sessions, clears this extension's settings, and
reloads affected tabs to restore ordinary page APIs. Settings are cleared rather than replaced
with guessed previous values, so Chrome's underlying configuration takes effect again.

Response bodies cross Chrome's extension message channel as base64. Limit request and response
bodies to 16 MiB and bound active requests to six. Preserve response headers, including separate
Set-Cookie fields, while removing hop-by-hop transfer framing. Uploads without complete byte
information are refused. The engine requests identity content encoding.

## Tested Chrome behavior

The live integration test succeeded on Chrome 153.0.8010.54 on Windows using a separate headless
profile. A normal HTTPS tab returned `IsTor: true` from Tor Project's check endpoint. An HTML page
loaded through the same engine, page WebRTC and WebTransport constructors were unavailable,
requests failed after the engine stopped, disconnect released proxy control, and cancellation
returned without waiting for the bootstrap deadline.

The page transport policy must be exercised **after navigation**, not just in the document that
existed when attaching. Initial testing found that immediate evaluation alone was insufficient.
`configureTarget` enables both Runtime and Page, registers the document-start script, and applies
the policy to the current context. The integration test checks the resulting constructors after
navigating to an HTML page.

`tests/headless.mjs` uses the installed Chrome executable with a private debugging pipe,
`--headless=new`, a project-local profile, and `--enable-unsafe-extension-debugging`.
`Extensions.loadUnpacked` loads the real extension. It does not automate or alter an existing
Chrome window. Test artifacts stay under `build`; build output stays under `publish`.

Runtime files are vendored unmodified and hashed in `third-party.json`. The Git attribute for
`extension/tor` disables newline conversion so a Windows checkout preserves those hashes.

## Upstream contracts

- [tor-js](https://github.com/ethereum/tor-js): local Arti execution, browser gateway transport, fetch interface.
- [Chrome debugger](https://developer.chrome.com/docs/extensions/reference/api/debugger): request interception and related targets.
- [Chrome proxy](https://developer.chrome.com/docs/extensions/reference/api/proxy): extension-owned proxy settings.
