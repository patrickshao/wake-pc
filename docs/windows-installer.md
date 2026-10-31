# Windows Sleep Bridge installer specification

## Product decision

Wake PC's primary feature—waking the PC—does not require a Windows application. It
distributes one optional companion as a versioned Inno Setup 7 Setup.exe, not a ZIP
containing PowerShell and CMD launchers. The companion adds on-demand paired sleep,
guided setup, and network health checks; Windows may also sleep automatically under
its own power policy.

This is a consumer companion for a home gaming PC. The installer must therefore
prioritize a familiar setup/uninstall experience, one elevation prompt, reliable
in-place upgrades, no reboot, and a visible signed-in-user tray app. MSI-specific
enterprise deployment and repair are not current requirements.

Inno Setup 7.0.2 is pinned. It creates a single EXE, supports administrative
installation, Restart Manager, stable-AppId upgrades, Add/Remove Programs, silent
deployment, ACLs, and integrated Authenticode signing. Inno itself does not model
HTTP.sys URL reservations or Windows Firewall rules, so the first-party bridge
executable owns those two bounded operations.

## Installed state

| Resource | Owner and lifecycle |
| --- | --- |
| `C:\Program Files\Wake PC Sleep Bridge\WakePcBridge.exe` | Inno `[Files]`; replaced on upgrade and removed on uninstall |
| `%LOCALAPPDATA%\WakePcSleepBridge` | Created per user by the non-elevated app; contains nonsecret settings, current-user DPAPI ciphertext, and logs; removed on uninstall |
| Add/Remove Programs entry | Inno, keyed by stable AppId `{EE345E29-5C07-44F9-A41B-5826C96C6356}` |
| HKLM Run value `WakePcSleepBridge` | Inno `[Registry]`; launches `supervise`; removed on uninstall |
| HTTP URL ACL `http://+:47653/` | `configure-system`; grants execute to built-in Users; removed by `unconfigure-system` |
| Firewall rule `Wake PC Sleep Bridge` | `configure-system`; TCP 47653, LocalSubnet, Private/Public; removed by `unconfigure-system` |
| Start Menu shortcuts | Inno `[Icons]`; one launches `start`, one opens `setup-guide`; removed on uninstall |

The installer may clean up the previously tested development scheduled task, but it
does not ship or retain a legacy installer. Elevated commands read nothing from a
user-writable location.

Version 1.0.0 kept data in a `ProgramData` folder every local user could edit. Newer
versions never read that folder, so upgrading from 1.0.0 needs one new pairing, which
the installer's finish screen explains. The non-elevated app removes the old folder at
first launch, skipping it if it is a link.

## Runtime model

The machine Run entry launches:

```text
WakePcBridge.exe supervise
```

`supervise` runs as the signed-in, non-elevated Windows user. It starts the same
executable in `run` mode and restarts that child after an unexpected exit. The tray
command **Exit until next sign-in** writes a volatile current-session registry value;
runtime and supervisor then exit without fighting the user. The marker disappears at
sign-out or reboot.

`start` explicitly clears the current-session pause and is used by the Start Menu and
Setup's finish-page launch action. Runtime modes reject an elevated token.

No Windows service, scheduled task, shell, PowerShell process, or reboot is required.

## Installation flow

1. The user opens the versioned Setup.exe.
2. Setup requests administrator access once.
3. Setup explains that wake is the main feature and that the companion adds optional
   paired sleep control plus network tools on the local subnet. There is no
   additional checkbox; continuing advances the installation. Silent setup must pass
   `/TRUSTEDNETWORK=1`.
4. On an update, Setup stops the existing supervisor and runtime pair when the user
   commits from the Ready page, before Restart Manager scans in-use files. Silent
   setup performs the same stop during preparation. Restart Manager remains a
   fallback, and the finish action starts the updated pair again.
5. Setup uses the standard Program Files location without asking for an install
   directory, then installs the native executable and data-directory ACL.
6. `configure-system` runs elevated, performs pure self-tests, creates or preserves
   nonsecret configuration, replaces its fixed URL ACL and firewall rule, and
   verifies both. A fresh install does not create plaintext credentials.
7. Inno writes the startup value, bridge and setup-guide Start Menu shortcuts, and
   uninstall metadata.
8. The finish page points to **Setup & status…** in the tray, then offers to launch
   `start` as the original
   non-elevated user. It does not open the guide automatically.
9. The bridge starts quietly without a toast, creates an empty per-device registry
    directly under user-scoped DPAPI or migrates the existing client as `primary`,
    and reports whether Sleep PC is enabled. Pairing actions live only in **Setup &
    status…**.

The tray has one **Advanced diagnostics…** action rather than separate network, log,
and report choices. It refreshes and opens a secret-free JSON support report with
active network data and saved/current address health, but never includes a MAC,
device ID, or credential material.

The primary **Setup & status…** action uses separate **Status** and **How to set up**
tabs. Status exposes only the MAC address, PC address, and optional Sleep PC state.
The second tab provides the short wired-network, Wake on LAN, address reservation,
Decky, and test workflow. A bounded scrolling text control prevents its contents from
changing the window size. Per-monitor DPI mode and a final working-area fit keep the
window visible when monitor scale or placement changes.
After explicit confirmation, the expected MAC and IPv4 address are stored as
nonsecret baseline fields in `config.json`. The tray and guide compare them with the
live wired adapter, flag changes, and offer to accept a changed IP while explaining
that Decky and the router reservation must also be updated. The MAC is not serialized
to the secret-free diagnostic report or written to logs.

The system-registration command uses absolute System32 paths and `ProcessStartInfo`
argument lists. It does not invoke a command shell or accept network-rule arguments
from the user. If registration fails after removing old state, it removes partial
new state before returning failure.

## Upgrade and uninstall

The AppId, architecture mode, and installation directory stay stable across
versions. Before the in-use-file scan, Setup stops both processes that share the
single installed executable: the crash-recovery supervisor and its runtime child.
This prevents two identical entries from appearing during a normal update. Inno
still uses Restart Manager as a fallback. Setup preserves the encrypted device
registry and only replaces the application and registration needed for the new
version. Users update by running the newer Setup.exe over the installed version.

Uninstall runs `unconfigure-system` before files are removed, then deletes the Run
value, shortcut, application directory, and Add/Remove Programs entry. Each user's
`%LOCALAPPDATA%\WakePcSleepBridge` stays, because an elevated uninstaller should not
reach into user profiles; delete it to erase that user's paired devices. The uninstaller is idempotent if a firewall rule, URL ACL,
or development scheduled task is already absent.

## Security and release requirements

- Setup requires elevation; runtime and supervisor reject elevation.
- The firewall rule is local-subnet only, but covers both Private and Public
  profiles so Windows 11's default classification does not silently break the app.
- Interactive installation always shows the trusted-network disclosure before it
  modifies the firewall. Unattended installation requires explicit confirmation.
- Pairing and power-action controls remain as defined in
  [`sleep-bridge.md`](sleep-bridge.md).
- Per-device keys and all pairing material are authenticated and encrypted together
  with DPAPI `CurrentUser`, in a folder other standard local accounts cannot write.
- Hobby releases are unsigned; Authenticode signing is optional.
  The build's `-SignToolCommand` connects the signing command to Inno's `SignTool`.
- If configured, the code-signing certificate and timestamp service are credentials and are
  not stored in the repository.

Inno's source license permits commercial use and binary redistribution. Its
maintainers separately request a paid compiler license from for-profit users above
their published revenue threshold, including CI use. That operational requirement
is documented in `THIRD_PARTY_NOTICES.md`; it does not add a runtime dependency or
license check to the installer.

## Reproducible build

The repository-local bootstrap:

```powershell
.\windows-native\scripts\bootstrap-inno.ps1
```

downloads only the pinned official 7.0.2 installer, verifies the pinned SHA-256 and
the valid Pyrsys B.V. Authenticode signature, and extracts a portable compiler under
the ignored `.inno-setup` directory. It does not install Inno system-wide.

Build:

```powershell
.\windows-native\scripts\build.ps1 `
  -DotNet .\.dotnet-sdk\dotnet.exe `
  -InnoCompiler .\.inno-setup\ISCC.exe
```

Output:

```text
windows-native\artifacts\wake-pc-sleep-bridge-v<VERSION>-setup.exe
windows-native\artifacts\wake-pc-sleep-bridge-v<VERSION>-setup.exe.sha256
windows-native\artifacts\WakePcBridge.exe.sha256
```

## Validation gates

- C# warnings are errors; restore is locked.
- Pure runtime self-tests cover protocol vectors, plaintext and protected credential
  migration, independent per-device pairing, first-run generation, redaction, pause
  matching, and the embedded icon.
- Loopback integration uses a fake power controller and covers independent clients,
  one-use codes, signed sleep, replays, malformed requests, duplicate listeners,
  shutdown, and log secrecy.
- Python source tests assert the fixed installer and runtime security boundaries and
  ensure the PowerShell/CMD distribution path is absent.
- ISCC must compile without warnings or errors.
- The build verifies a plausible PE artifact, version metadata, exact versioned
  filename, and SHA-256 sidecars.
- Package validation identifies the Windows artifact as Inno Setup and requires its
  version to match `windows-native/version.txt`, independently of the Decky plugin.
- Before release, perform real install, running-tray, crash-recovery, in-place
  upgrade, uninstall, and five Sleep → Wake cycle tests on the target Windows PC.
