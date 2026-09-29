# TorVPN for Chrome

An experimental Chrome extension with a Tor client bundled as WebAssembly. No `tor.exe`,
Node.js, .NET, administrator access, native messaging host, or installed companion is needed
to **use** the extension. JavaScript controls the interface and Chrome integration;
[tor-js 0.4.2](https://github.com/ethereum/tor-js) runs Arti inside an offscreen extension document.

This is a separate JavaScript subproject of TorVpnForWindows. It ports the browser connection
workflow; it does not run the Windows application in the browser.

## Install

1. Open `chrome://extensions` in Chrome 125 or later and enable **Developer mode**.
2. Choose **Load unpacked** and select this subproject's `extension` directory,
   or `publish/TorVpnChrome` after building. For the ZIP distribution, extract it first
   and select the extracted `TorVpnChrome` directory containing `manifest.json`.
3. Open TorVPN from the extensions menu and choose **Connect to Tor**.

Chrome displays its debugging banner while the extension handles requests. Connect and
disconnect reload affected web tabs so existing documents do not keep their previous transport
settings. Disconnect before disabling or removing the extension if you want a clean transition.

The interface supports Turkish and English. It shows the connection state, a Tor exit address,
completed request count, downloaded bytes, and number of attached tabs. **New Tor connection**
rebuilds the client and reloads attached tabs; a different exit address is not guaranteed.
The displayed address comes from the connection check; Tor can use other exits for other streams.

## What is included

- The Tor client and its JavaScript loader are packaged locally; no code is loaded from a remote server.
- Regular HTTP and HTTPS requests in Chrome tabs are intercepted and sent through that client.
- While connected, requests that escape interception hit a loopback proxy with no direct fallback.
- Network prediction is disabled. Page WebRTC and WebTransport constructors are disabled;
  WebSocket requests are blocked. The Tor engine keeps its own WebRTC connection to the gateway.
- Disconnect releases the extension's Chrome settings, so Chrome resumes its underlying settings.

## Gateway and scope

The browser connects to Tor relays through a gateway. The default is the **public demonstration
gateway published by tor-js**; it has limited capacity and can disappear. The Tor circuits and
layered encryption run inside the extension. A gateway address can be changed under Settings.
Its certificate fingerprint is part of the address.

This first version is for ordinary web browsing. It does not carry voice/video calls,
WebSockets, or WebTransport. A single request or response is limited to 16 MiB; file uploads
whose complete bytes are unavailable from Chrome are blocked. Incognito, other browser profiles,
other extensions' requests, and other Windows applications are outside the supported scope.
Requests from unsupported browser components can fail while the profile proxy is active.

The Windows program's exit-country selector, bridge selection, system tunnel, and program lists
are not included. This extension does not provide Tor Browser's fingerprinting defenses.

## Build and verify

Run these commands with **this `chrome` directory as the working directory**. Node.js 22 or
later is used only by development tools. There are no package installation steps.

```powershell
node tools/build.mjs
node --test tests/config.test.mjs
node tests/browser.mjs
```

The build verifies the bundled runtime against `third-party.json` and copies the extension into
`publish/TorVpnChrome`. The integration test starts its own **headless Chrome** with a separate
profile below `build`. It does not attach to an existing personal Chrome session. Set
`TORVPN_TEST_CHROME` to a Chrome executable path if Chrome is installed elsewhere.

The live integration test checks a real Tor exit in an ordinary tab, the page transport policy,
request failure after stopping the Tor engine, restoration of proxy settings, and cancellation
during bootstrap. Its report and popup image are written below `build` and excluded from Git.
It requires a working network and the configured gateway.

See [the documentation index](docs/Index.md) for the architecture and development records.

## Third-party software

`extension/tor` contains unmodified tor-js runtime files, licensed MIT OR Apache-2.0; both license
texts are included. Version, source archive, integrity, and runtime file hashes are recorded in
`third-party.json`. The icon is reused from the parent TorVpnForWindows project.
