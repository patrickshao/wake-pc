# Wake PC Bridge native Windows runtime

`WakePcBridge.exe` is the optional Windows companion for Wake PC. Waking works
without it; the companion adds authenticated on-demand sleep plus setup/status
tools. It is a .NET 10 `net10.0-windows` self-contained, single-file
`win-x64` Windows-subsystem application. It has no console or main window. A custom
notification-area icon shows **Wake PC Companion 1.0.0 — Running** and provides
**Setup & status…**, one **Advanced diagnostics…** action, and **Exit until next
sign-in**. Pairing lives only in Setup & status.
Startup is silent: the icon does
not display a toast or balloon when the user signs in. Its menu reports only the
paired-device count and bridge version; it does not display device IDs, keys, or
key-derived identifiers.

The companion version is stored in `windows-native/version.txt` and is independent
from the Decky plugin version in `package.json`.

The circular power/wake icon is generated from deterministic drawing primitives at
every Windows tray size. Regenerate both source assets with:

```powershell
python windows-native\scripts\build-icon.py
```

**Advanced diagnostics…** refreshes and opens one secret-free JSON support report.
It records active network data and saved/current address health for investigating a
router reset, but never includes the expected or live MAC or pairing material.

**Setup & status…** uses bounded **Status** and **How to set up** tabs so text cannot
resize the window. Status shows only the Ethernet ID, PC address, and Sleep PC state.
The help tab has five short steps plus optional Sleep pairing. Per-monitor DPI mode
and a final working-area fit keep the window visible when monitor scaling changes.
The guide is also installed as a Start Menu shortcut and never opens automatically.

The standard startup command is `supervise`. It launches the same executable in
`run` mode and restarts that child after an unexpected exit. User-requested exit
writes a volatile current-session pause; both runtime and supervisor then exit.
`start` clears that pause before entering supervisor mode and is used by Setup and
the Start Menu shortcut.

An in-place update stops the supervisor and runtime pair before Inno checks for
in-use files. This avoids presenting two identical `WakePcBridge` entries, while
Restart Manager remains enabled as a fallback if either process could not be stopped.

## Runtime modes

```text
WakePcBridge.exe start [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe supervise [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe run [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe setup-guide [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe diagnose [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe configure-system [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe unconfigure-system [--config C:\ProgramData\WakePcSleepBridge\config.json]
WakePcBridge.exe self-test
```

With no arguments the app behaves as `start` using the ProgramData config path.
`configure-system` and `unconfigure-system` require elevation and are called only by
the Inno installer. They own the bounded HTTP.sys URL reservation, Windows Firewall
rule, and cleanup of the superseded development scheduled task. Runtime modes refuse
to run elevated.

## Build and test

From Windows PowerShell:

```powershell
.\windows-native\scripts\bootstrap-inno.ps1
.\windows-native\scripts\integration-test.ps1 -DotNet .\.dotnet-sdk\dotnet.exe
.\windows-native\scripts\build.ps1 `
  -DotNet .\.dotnet-sdk\dotnet.exe `
  -InnoCompiler .\.inno-setup\ISCC.exe
```

The build requires the pinned Inno Setup 7.0.2 compiler. It publishes the native
single-file runtime, runs its side-effect-free self-test, compiles the standard
Setup.exe, and writes SHA-256 sidecars under the ignored
`windows-native\artifacts` directory.

Hobby releases are unsigned. Optionally pass `-SignToolCommand` with an Authenticode signing command.
The build passes it into Inno so both Setup.exe and the generated uninstaller are
signed. Unsigned output is supported for public releases and must be labeled as such.

## Security boundary

The runtime exposes only `POST /v1/pair`, authenticated `POST /v1/status`, and
authenticated `POST /v1/sleep`. It rejects request bodies, query strings, unknown
routes, stale timestamps, malformed nonces, invalid HMACs, and replays. Status
returns only the nonsecret build version and never invokes power control. Inno owns
files, ACLs, upgrades, the machine Run entry, and uninstall. The bridge owns only two
explicit elevated registration commands; they invoke fixed `netsh.exe` operations
without a shell or user-controlled command strings.

The per-device key registry, active one-time pairing code, its hash, and expiry are
serialized together and encrypted with Windows DPAPI using `CurrentUser`. A fresh
installation creates the empty registry directly in that protected payload and each
pairing adds an independent credential. On the first launch after upgrading an older
installation, its sole key becomes the `primary` device credential without rotation;
legacy plaintext fields are atomically removed. The ProgramData JSON contains only
DPAPI ciphertext and nonsecret runtime settings. Another local account can still
delete the shared file and cause a denial of service, but cannot decrypt or silently
rewrite its credential contents.
