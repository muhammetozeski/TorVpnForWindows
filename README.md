# Tor VPN for Windows

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6)](#requirements)
[![Tor](https://img.shields.io/badge/Tor-15.0.21-7D4698)](https://www.torproject.org/)
[![sing-box](https://img.shields.io/badge/sing--box-1.14.0-2C8EBB)](https://sing-box.sagernet.org/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

Routes every TCP connection on a Windows machine through the Tor network, without configuring
anything per application. It creates a network adapter, takes over the default route, and hands
the traffic to Tor.

Tor Browser protects one browser. A SOCKS proxy protects the applications you remember to
configure. This covers the whole machine, including programs that have no proxy setting.

## What it actually does

- Starts `tor.exe` and waits for it to finish bootstrapping.
- Creates a TUN adapter with [Wintun](https://www.wintun.net/) and points the default route at it.
- Hands every TCP connection to Tor's SOCKS port. When the hostname can be read from the first
  packet it is passed to Tor as a name, so Tor resolves it at the exit relay and the name never
  leaves the machine in the clear.
- Sends DNS queries to Tor's own DNS port. Queries an application aims at a hardcoded server such
  as `8.8.8.8` are intercepted as well.
- Refuses UDP. Tor carries TCP only, so UDP is answered with an ICMP unreachable rather than
  allowed out around the tunnel. Applications fall back to TCP immediately.
- Resolves `.onion` addresses. Tor maps them into a routable range that the tunnel carries back to
  it, so onion services work in any program, not just a browser.
- Keeps `tor.exe` and its transports out of the tunnel they are providing, along with any
  application listed in `exclusions.txt`.

## Features

| | |
|---|---|
| **System-wide** | One adapter, one default route. No per-application proxy settings. |
| **Kill switch** | Blocks all traffic at the Windows Filtering Platform, below routing, permitting only the tunnel. If Tor drops, the block stays and the machine stays dark until Tor is back. On by default. |
| **Exit country** | Restrict the exit relay to a chosen country, resolved against Tor's own GeoIP data. |
| **Bridges** | obfs4, Snowflake and meek, or a list you paste in. Built-in lists are fetched from the Tor Project and cached rather than compiled in. |
| **Excluded applications** | A plain text list of executables that bypass the tunnel and keep using the normal connection and DNS. |
| **New circuit** | Ask Tor for fresh circuits and re-check the exit address. |
| **Local network** | Printers, network storage and the router stay reachable while connected. |
| **Two languages** | English and Turkish, following the Windows display language on first start. Adding another is a matter of dropping in one XML file. |

## How the pieces fit

```
  Application
      │  no proxy configured
      ▼
  TorVPN adapter  ── default route, IPv4 and IPv6
      │
      ▼
  sing-box  ── routing, DNS interception, UDP refusal, per-process exclusions
      │                                    │
      │ TCP + hostname                     │ DNS
      ▼                                    ▼
  tor.exe SOCKS port                 tor.exe DNS port
      │
      ▼
  Tor network ──▶ exit relay ──▶ destination
```

`tor.exe` itself is matched by process name and sent straight out of the physical adapter. Without
that, it would route its own traffic into the tunnel it is providing.

### The kill switch

Pressing connect blocks everything before Tor even starts. Only Tor, its transports, this
application and the executables in `exclusions.txt` are permitted. Once the tunnel is up, the
tunnel adapter is permitted too. If Tor or the tunnel drops, that permit is revoked and the machine
stays offline while a retry loop works on getting back; a network that disappears and returns stays
blocked until Tor is connected again. Only an explicit disconnect removes the filters.

They are installed in a dynamic Windows Filtering Platform session, so if this application is
killed or crashes, Windows removes every filter it added. A crash restores networking rather than
leaving the machine cut off.

Routing alone could not do this. A program that binds to a specific adapter, or anything that
installs a more specific route, travels around the tunnel without ever consulting the default
route. These filters sit at the connect layer, below routing.

Note that another VPN client's kill switch works the same way and will block this one; see
[Known conflicts](#known-conflicts).

### Choices worth explaining

**UDP is refused, not dropped.** Tor cannot carry UDP. Silently discarding it leaves applications
waiting for a timeout; an ICMP unreachable makes them fall back to TCP at once, which matters for
QUIC-heavy browsing where the fallback fires constantly.

**DNS answers A records only.** Tor's exit support for IPv6 is uneven, and a half-working AAAA
answer is worse than none, because the connection gets handed to an exit that cannot complete it.
IPv6 is still routed into the tunnel, so a literal IPv6 destination cannot escape around it.

**Helper binaries are taken from `PATH` first.** If the machine already has `tor.exe` or
`sing-box.exe` installed and kept up to date, that copy is used; the bundled ones are the fallback.
The lookup walks the `PATH` entries itself rather than calling `where.exe` or the Win32
`SearchPath`, because both of those start with the current working directory and would happily
return a file sitting next to the program. A candidate found on `PATH` is run once with a version
flag and skipped if it does not identify itself as the right tool.

**Built-in bridges are fetched, not compiled in.** The Tor Project retires and replaces them over
time, so a list frozen at build time ages with the release rather than with the network. The
current list is fetched and cached; the compiled-in copy is only used when that endpoint cannot be
reached, which is exactly when someone needs bridges most.

## Requirements

- Windows 10 or 11, 64-bit
- Administrator rights. Creating the adapter, installing routes and applying the DNS filters all
  need them, and there is no useful subset of the functionality that works without.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the
  framework-dependent build. The portable build needs nothing.

## Install

Download either executable from the [latest release](../../releases/latest) and run it.

| File | Needs .NET installed | Size |
|---|---|---|
| `TorVpnForWindows.exe` | No | larger |
| `TorVpnForWindows-FrameworkDependent-RequiresNET10.exe` | Yes | smaller |

The executables are digitally signed. To let Windows verify the signature, run `Guven-Kur.cmd`
from `SignatureTrust.zip` once. The programs run without it; only the signature stays unverified.

## Known conflicts

**Another VPN client's kill switch will block this one.** ProtonVPN, Mullvad, Windscribe and
similar products install Windows Filtering Platform rules that block every connection not going
through their own adapter, with an exemption for their own executable. Those rules sit below
routing, so this tunnel can own the default route, hand packets to its adapter, and still have them
dropped before they leave the machine. The result is a tunnel that reports connected while nothing
can reach anything.

The application detects this: if Tor is reachable through its SOCKS port but traffic sent through
the tunnel is not getting out, it names the other product on the status screen. Turn that product's
kill switch off, or disconnect it, and connect again.

Running this on top of another VPN is otherwise fine. Tor connects out through whatever the default
route was before the tunnel came up.

## Configuration

Settings live in `%LOCALAPPDATA%\TorVpnForWindows`.

| File | Purpose |
|---|---|
| `settings.json` | Everything the Settings tab writes. |
| `exclusions.txt` | Executables that bypass the tunnel, one name per line. |
| `lang.en.xml`, `lang.tr.xml` | Interface text. Edit to change wording, or copy to `lang.<code>.xml` to add a language. |
| `logs\app.log` | The application's own log plus everything Tor and sing-box print. |
| `session\` | The generated `torrc` and `sing-box.json` for the current session, useful when something does not behave. |

The extracted helper binaries live in `%ProgramData%\TorVpnForWindows\runtime`. They are there
rather than under the user profile because Tor's `ClientTransportPlugin` directive splits its
argument on whitespace, so the path to the bridge transport must not contain a space, and a user
account named "John Smith" would produce one.

## Building

```powershell
git clone https://github.com/muhammetozeski/TorVpnForWindows.git
cd TorVpnForWindows
pwsh tools\Fetch-Payload.ps1
dotnet build src\TorVpnForWindows\TorVpnForWindows.csproj -c Release
```

`Fetch-Payload.ps1` downloads Tor, sing-box and Wintun from their official distribution points and
verifies each archive against a pinned SHA-256 digest before staging it. The build zips that folder
and embeds it, which is what makes the result a single executable. The binaries are not stored in
the repository.

To publish both executables:

```powershell
dotnet publish src\TorVpnForWindows\TorVpnForWindows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
dotnet publish src\TorVpnForWindows\TorVpnForWindows.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-fd
```

## Tests

```powershell
dotnet build tests\TunnelSmokeTest\TunnelSmokeTest.csproj -c Release -o build\smoketest
# then, from an elevated prompt:
build\smoketest\TunnelSmokeTest.exe report.txt 15
```

The smoke test compiles the engine straight from the application sources, brings a real session up
against the live Tor network, and checks that a request which was never told about a proxy comes
back reporting a Tor exit address. It then tears everything down and checks the routes, the
adapter and ordinary connectivity are back. A watchdog forces the session down after five minutes
no matter what, because losing the default route to a tunnel that never came up would otherwise
leave the machine without networking.

`tests\PathResolutionTest` covers the `PATH`-first binary lookup, including the case that motivated
writing it by hand: a decoy executable in the working directory that `where.exe` returns and the
resolver must not. It needs no privileges.

`tests\KillSwitchTest` arms the filters on their own, without Tor or the tunnel, and checks that a
permitted process still reaches the network while an unpermitted copy of itself does not, then that
disarming leaves nothing behind. Keeping it separate from the tunnel test means a mistake in the
filters shows up in fifteen seconds rather than after a bootstrap, and the blocking window stays
short. It needs administrator rights.

## What this does not do

- It does not make you anonymous by itself. Tor protects the transport; a signed-in browser
  session, a leaked hostname in an application protocol, or a fingerprintable browser will identify
  you regardless. For browsing, Tor Browser remains the better answer.
- It does not carry UDP, so anything that needs it will not work while connected. That is a
  property of Tor, not of this program.
- It does not hide from your network that you are using Tor unless bridges are on, and the built-in
  bridges are public enough that a determined censor can block them.

## Third-party components

| Component | Version | Licence |
|---|---|---|
| [Tor](https://www.torproject.org/) (Expert Bundle) | 15.0.21 | BSD-3-Clause |
| [lyrebird](https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/lyrebird) | bundled with the above | BSD-3-Clause |
| [sing-box](https://github.com/SagerNet/sing-box) | 1.14.0 | GPL-3.0 |
| [Wintun](https://www.wintun.net/) | 0.14.1 | GPL-2.0 |

## Licence

MIT. See [LICENSE](LICENSE).
