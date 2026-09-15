# Lessons

Problems that took real digging, and what solved them.

## A retry during bootstrap briefly lifted the kill switch

**Symptom:** the log showed "Kill switch disarmed; normal networking restored" and, 120 ms later,
"Kill switch armed", every time retry was pressed while Tor was still bootstrapping.

**Cause:** the connect path's own cancellation handler tore the session down with the block removed,
before the retry that cancelled it could re-arm.

**Fix:** the code that is cancelled never decides what happens to the block. One supervisor owns the
session; a restart only cancels the attempt, and only an explicit disconnect removes the block.

## Tor stayed stuck after the Wi-Fi came up

**Symptom:** connect pressed with the Wi-Fi off, the Wi-Fi joined later, and Tor sat at a low
percentage with no further attempts until the application was restarted.

**Cause:** a bridge that fails is retried on Tor's own schedule, and bridge names resolved over HTTPS
are only written to the hosts file before Tor starts. With no network at that moment, nothing made
Tor try again soon.

**Fix:** start the session over when the network arrives or loses an address or gateway, and when
Tor has made no bootstrap progress and read no data for longer than its transport needs.

## SIGNAL HALT always failed with ObjectDisposedException

**Cause:** the control connection's reader was tied to the session's cancellation token. Ending the
session stopped the reader, whose StreamReader closed the socket before the halt was written.

**Fix:** the reader lives as long as the control client and stops only when the client is disposed.

## Tor control replies went out of step after circuit-status

**Cause:** any line whose first three characters parsed as a number was taken as a status line.
Inside a `250+` data block, circuit lines start with the circuit number, so `123 BUILT ...` ended
the reply early.

**Fix:** inside a data block every line is data until a single `.`; a leading `..` is unescaped.

## Case-insensitive regex does not match "FILES" with "Files" on a Turkish machine

**Cause:** .NET's `(?i)` follows the current culture, and in tr-TR `i` and `I` are different
letters. Go's regexp, which sing-box uses, ignores culture.

**Fix:** use `RegexOptions.CultureInvariant` where .NET stands in for Go, and
`StringComparison.OrdinalIgnoreCase` for paths.

## WFP application identifiers and 8.3 short paths

A block on an executable's long path also held when the same executable was started through its
8.3 short path (checked with tests/FirewallTest). The lists still store both spellings for sing-box,
which matches the path string the process reports.

## Rendering WPF screens in a test tool

- Creating the application's own `App` class runs its `OnStartup` as soon as the dispatcher
  processes anything, even without `Run`. Without administrator rights that put up the
  "needs administrator" message box on every run. Create a plain `Application` and load the styles
  from App.xaml instead.
- A window at `Left = -32000` is treated as minimised and is never laid out; use another off-screen
  position.
