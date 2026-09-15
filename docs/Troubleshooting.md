# Troubleshooting

## It says connected, but nothing loads

Almost always another VPN client's kill switch. ProtonVPN, Mullvad, Windscribe and similar products
install Windows Filtering Platform rules that block every connection not going through their own
adapter, with an exemption for their own executable. Those rules sit below routing, so this tunnel
can own the default route, hand packets to its adapter, and still have them dropped before they
leave the machine.

The status screen names the product when it detects this. Turn that product's kill switch off, or
disconnect it, then connect again.

To see the filters yourself, from an elevated prompt:

```powershell
netsh wfp show filters file=filters.xml
```

Then search `filters.xml` for `FWP_ACTION_BLOCK`. A block filter at
`FWPM_LAYER_ALE_AUTH_CONNECT_V4` belonging to another product is the thing to turn off.

## Connecting never gets past a low percentage

Tor cannot reach the network. Check the log tab: the lines from Tor say what it is trying.

If the network filters or blocks Tor, turn bridges on in the settings. obfs4 is the usual first
choice. The built-in lists are public, so a network that specifically blocks Tor may already block
them; individual bridges from <https://bridges.torproject.org> can be pasted into the custom list.

If a copy of `tor.exe` on `PATH` is being used, the log says which one. A broken or unrelated
executable with that name is skipped automatically, but a genuine Tor that cannot start for its own
reasons is not. Rename or remove it to fall back to the bundled copy.

## The internet is off and the program is not running

The kill switch filters live in a dynamic Windows Filtering Platform session, which the operating
system removes when the process exits. If the machine has no internet and this program is not
running, the cause is elsewhere.

Should you ever need to confirm nothing of ours is left, from an elevated prompt:

```powershell
netsh wfp show filters file=filters.xml
```

and search for `Tor VPN for Windows`. There should be no matches while the program is closed.

## Something on the local network stopped working

Turn on "Allow local network" in the settings. Private address ranges are then kept out of the
tunnel entirely, so printers, network storage and the router stay reachable.

If a specific program needs to bypass the tunnel altogether, add it to the tunnel black list
(Settings, Tunnel, Black list, Edit) and turn the black list on. Its traffic then leaves through
the normal connection, and it is permitted through the kill switch whether or not it is running
when you connect. A running session starts over with the change straight away.

## A program stopped reaching the internet

Check the internet lists (Settings, Internet access). With the white list on, only the listed
programs can reach the internet, whether Tor is connected or not; with the black list on, the
listed programs cannot. Entries match the exact executable path, so a program that was moved or
reinstalled somewhere else is a different program to the lists and has to be added again.

## It stays at a low percentage after the Wi-Fi comes up

The connection starts over three seconds after the network settles, and again whenever Tor has
made no progress and read no data for 30 seconds (45 for meek, 90 for snowflake). The log tab
shows "The network changed" and "Starting the connection over" when that happens. If it keeps
starting over without getting further, the bridges themselves are not reachable from this network.

## VirtualBox, or something similar, breaks while connected

Turn off "Strict routing". It blocks DNS on every adapter other than the tunnel, which is what
stops Windows quietly resolving names through another adapter, but some virtualisation software
does not cope with it.

## A program that needs UDP does not work

Tor carries TCP only. UDP is refused with an ICMP unreachable rather than allowed out around the
tunnel, so applications fall back to TCP immediately instead of waiting for a timeout. Anything
that genuinely requires UDP, such as most voice and video calling, will not work while connected.
That is a property of Tor, not of this program.

## Where the logs are

`%LOCALAPPDATA%\TorVpnForWindows\logs\app.log` holds this application's own log plus everything Tor
and sing-box print. The generated configuration for the current session is in
`%LOCALAPPDATA%\TorVpnForWindows\session`, which is the first place to look when the tunnel behaves
in a way the settings do not explain.
