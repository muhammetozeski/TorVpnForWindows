# Portable data and settings recovery

`AppPaths.Root` uses `AppContext.BaseDirectory`, so data follows the executable rather than
the current working directory. `UserData` owns settings, language files, exclusions and persistent
Tor state. `AppCache` owns logs and generated session files. `UserCache` owns fetched bridge lists.
See the [configuration table](../README.md#configuration) for the paths.

The embedded transport runtime remains under ProgramData. `TorRcBuilder` hands the executable
path to Tor's `ClientTransportPlugin`, whose whitespace splitting motivated the existing path
restriction documented in `AppPaths.Runtime`. Moving the program's user data does not remove
that runtime constraint.

`PortableDataMigration.Run` imports the former per-user layout only when portable settings are
absent and a legacy `settings.json` exists. It copies languages, setting backups, exclusions,
Tor state, the bridge cache and child process records without overwriting existing destination
files. Settings are committed last. The legacy settings file is retained as `.migrated`; a
failed final copy restores its original name. Retiring the legacy primary prevents a removed
portable profile from being silently recreated from old settings on a later fresh start.

`AppSettings.Save` writes a temporary file, then atomically replaces the primary and retains its
previous contents as `settings.json.previous`. Save failure is logged and reaches the action's
error boundary; it is not reported as success. `Load` retains an unreadable primary as `.broken`
and tries the previous document. A readable backup restores all profiles and the selected profile.
If existing settings cannot be recovered, initialization stops instead of silently replacing them
with defaults. Only an installation without saved settings starts with fresh defaults.

`tests/SettingsProfileTest` verifies profile isolation, bridge preservation, corrupt-file recovery,
failed saves and migration of settings, custom language text, Tor state and bridge cache.
`tests/PortablePathsTest` starts the real path resolver with a different working directory and
from a copied folder containing spaces; both cases resolve settings and logs beside that executable.
`tests/LogFloodTest` verifies full exception details and the independent emergency log while retaining
the existing repeated-message suppression behavior.
