# Wake PC

Wake and sleep a Windows PC from a SteamOS handheld, a Mac, or an agent running
on your Mac. Wake uses your local network; optional sleep control uses the
Windows companion. No cloud account or remote shell is needed.

- **Decky plugin:** power controls in SteamOS Gaming Mode.
- **Mac command:** `wake-pc wake`, `wake-pc status`, and `wake-pc sleep --yes`.
- **Windows companion:** pairs clients and accepts authenticated sleep requests.

Wake needs wired Ethernet and Wake-on-LAN enabled on the Windows PC. Clients
must be on the same trusted LAN. Wake works without the companion; sleep needs it.

## Install

For Mac, follow the [command setup](docs/mac-wake.md). For SteamOS, use Decky:

Requires Decky Loader on a SteamOS handheld and a Windows PC with wired Ethernet
and Wake-on-LAN enabled. Both devices must be on the same trusted local network.
The optional companion targets Windows x64.

1. Open the [latest GitHub release](https://github.com/patrickshao/wake-pc/releases/latest).
2. In Gaming Mode, open **Decky → Settings → Install Plugin from URL** and paste:

   ```text
   https://github.com/patrickshao/wake-pc/releases/latest/download/wake-pc-latest.zip
   ```

3. Open **Decky → Wake PC → Settings**, enter your PC's wired MAC and reserved IP
   address, and select **Save changes**. Test **Wake PC**.
4. For optional sleep control, download `wake-pc-sleep-bridge-latest-setup.exe`
   from the same release and follow [Windows setup](windows/README-WINDOWS.md).

This is a hobby project. **Windows downloads are unsigned**; Windows may display
an unknown-publisher or reputation warning. Download only from this repository's
releases and compare the file's SHA-256 with its accompanying `.sha256` file.
Checksums detect file changes; they are not publisher signatures.

The update URL becomes available when the first public release is published.
Use **Copy update URL** in the plugin for later updates. Installation is manual;
this project is not listed in the Decky store.

## Behavior and defaults

- Sends the standard 102-byte magic packet (`FF` six times, then the Ethernet MAC
  repeated 16 times).
- Accepts colon, hyphen, Cisco dotted, and unseparated MAC formats. Saved MACs are
  normalized to uppercase colon notation.
- Sends 3 UDP packets, 200 ms apart, to port 9.
- Uses `255.255.255.255` when the optional broadcast address is blank.
- Checks TCP port 27036 up to 30 times after Wake, pausing 2 seconds between checks.
- Performs no continuous or background monitoring.
- Stores one host only.
- Shows **Sleep PC** only after the optional companion is paired, and requires a
  second confirmation press within eight seconds.

TCP 27036 is the default because Steam documents it as a local Remote Play port.
It normally requires no additional host service when Steam is running.

## Build

The commands below work in PowerShell, macOS, and Linux; Bash is not required.
Use the pinned pnpm 9.15.9, since newer major versions can ignore this project's
dependency-policy settings. If Python is not on PATH, set `PYTHON` to the full
interpreter path (`$env:PYTHON = 'C:\path\to\python.exe'` in PowerShell).

The scaffold follows the maintained
[Decky plugin template](https://github.com/SteamDeckHomebrew/decky-plugin-template)
and uses its current `@decky/api` callable backend pattern.

Requirements:

- Node.js 22 or newer (CI uses 24.16.0)
- pnpm 9 (the version recommended by the Decky template)
- Python 3.10 or newer for backend tests
- On Windows, .NET SDK 10.0.302 (pinned by `global.json`) and Inno Setup 7.0.2
  for Sleep Bridge releases

```sh
corepack enable
corepack prepare pnpm@9.15.9 --activate
pnpm install --frozen-lockfile
pnpm test
pnpm run build
pnpm run package
```

The frontend is written to `dist/index.js`. `pnpm run package` builds the Decky ZIP
without requiring Windows.
`pnpm run package:release` additionally packages a previously built companion:

- `out/wake-pc-v1.0.0.zip` for Decky on SteamOS handhelds.
- `out/wake-pc-sleep-bridge-v1.1.0-setup.exe` for the Windows gaming PC.

The first public release is 1.0.0 for both components; earlier 1.x builds were
private development versions. The two products version independently:
`package.json` is the Decky plugin version,
while `windows-native/version.txt` is the Windows companion version. A companion-only
release does not require reinstalling the Decky plugin.

No Docker is required. The Decky backend uses only Python standard-library
capabilities. The optional Windows companion is a self-contained .NET 10 Windows
executable built from this repository.
Build the Windows installer first with
`windows-native\scripts\build.ps1`; `pnpm run package:release` then copies that compiled
Setup.exe alongside the Decky ZIP.

Run the dependency-policy, vulnerability, typecheck, test, build, and Decky packaging
gate with:

```sh
pnpm run security:verify
```

To also validate a locally built Windows installer, run
`pnpm run security:verify --with-companion`. The **Release checks** workflow builds
and validates both platforms and uploads `wake-pc-release-assets`. See the
[release runbook](docs/releasing.md) for the remaining device checks and publication.

See [SECURITY.md](SECURITY.md) for the enforced package policy. [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) records the
bundled Decky API client and its included LGPL source. Packaging also writes
SHA-256 sidecars for the versioned Decky ZIP and Windows Setup.exe, plus stable
`latest` copies.

## Local UI preview

Run `pnpm preview` and open `http://127.0.0.1:4173`. The preview renders the actual
Decky component with simulated first-run, offline, paired, and error states.
All device actions are mocked; it cannot wake or sleep a real PC. Restart the
command after edits. `pnpm preview:check` type-checks and builds the preview.

Styling approximates Decky at panel width. Verify controller focus, Steam's actual
controls, and Windows display scaling on-device before release.

## Local development install

Serve the packaged ZIP to a SteamOS device on the same LAN with:

```sh
pnpm run serve
```

Then open **Decky → Settings → Install Plugin from URL** and enter:

```text
http://<build-computer-LAN-IP>:8000/wake-pc-latest.zip
```

Stop the temporary server with `Ctrl+C` after Decky finishes downloading the ZIP.
Replace `<build-computer-LAN-IP>` with your build computer's address. The plugin's
**Copy update URL** button always uses GitHub Releases, not this temporary server.

## Optional Windows companion

Wake PC itself does not require a Windows app. Install the companion only if you want
the optional **Sleep PC** action or the guided setup/status view.

1. Open `wake-pc-sleep-bridge-latest-setup.exe` from the release on the PC you want to wake.
2. Approve the Windows administrator prompt and finish Setup.
3. Open **Wake PC Setup & Status** from Start, or choose **Setup & status…** from the
   companion's notification-area icon.
4. On **Status**, copy the MAC address and PC address into **Wake PC → Settings**
   in Decky. On **How to set up**, follow the short wired-network steps.
5. Choose **Save changes** and test Wake PC.

To add **Sleep PC**, choose **Pair for Sleep…** in Windows and enter the displayed
six-digit code in Decky. Repeat **Pair another for Sleep…** for each additional
device. Updating in place keeps saved setup and paired devices.

The companion starts quietly. If a router reset changes the PC address, open **Setup
& status…**; it will show the change and what to update. **Advanced diagnostics…**
creates and opens one support report without adding more menu choices.

See [docs/sleep-bridge.md](docs/sleep-bridge.md) for protocol and security details.

## Pre-UI device smoke test

The automated tests verify the exact packet bytes, three-packet UDP delivery,
MAC validation, TCP online/offline behavior, and persistent configuration. Actual
NIC wake behavior can only be verified on the target LAN. Before UI testing, copy
the source tree to the SteamOS device and run:

```sh
python3 scripts/device_smoke_test.py \
  --mac 00:11:22:33:44:55 \
  --ip 192.168.1.50 \
  --broadcast 192.168.1.255
```

Run it once while the PC is asleep to verify Wake-on-LAN, then with
`--check-only` while the PC and Steam are awake to verify TCP 27036. If TCP 27036
is filtered, choose another known listening TCP port or disable status checking;
waking does not depend on status detection.

## Manual installation

Decky's supported ZIP layout is used: the archive contains one `wake-pc/` folder
with `dist/index.js`, `main.py`, `py_modules/wakepc.py`, and the manifests.

### Install from a hosted ZIP

1. Put `out/wake-pc-v1.0.0.zip` at a direct HTTPS URL you control.
2. In Gaming Mode, open **Decky → Settings → Install Plugin from URL**.
3. Enter the direct ZIP URL and install it.
4. Open **Decky → Wake PC**, enter the host settings, and select **Save changes**.

Decky's own documentation warns against installing plugins from untrusted URLs.

### Install over SSH for development

With SSH enabled on the SteamOS device, copy and extract the ZIP:

```sh
scp out/wake-pc-v1.0.0.zip deck@steamdeck.local:/tmp/
ssh deck@steamdeck.local
sudo rm -rf /home/deck/homebrew/plugins/wake-pc
sudo bsdtar -xf /tmp/wake-pc-v1.0.0.zip -C /home/deck/homebrew/plugins
sudo chown -R deck:deck /home/deck/homebrew/plugins/wake-pc
sudo systemctl restart plugin_loader
```

Return to Gaming Mode if necessary. After initial setup, normal Wake and Refresh
use requires no Desktop Mode.

## Configuration storage

Configuration is stored as `config.json` in Decky's per-plugin settings directory,
provided to the backend as `DECKY_PLUGIN_SETTINGS_DIR`. On a standard Decky install
this is managed under Decky's homebrew data tree, outside the plugin package, so it
survives plugin reloads and SteamOS reboots. The file is written atomically with
user-only (`0600`) permissions. The plugin's uninstall hook removes it.

## Uninstall

Open **Decky → Settings → Plugins**, select **Wake PC**, and choose **Uninstall**.
The plugin removes its Decky configuration in the uninstall hook. If the Windows
companion was installed, remove **Wake PC Companion** from Windows **Settings → Apps →
Installed apps**. Its uninstaller removes the startup entry, firewall rule, URL
reservation, key, files, and log.

For a development copy installed over SSH:

```sh
sudo rm -rf /home/deck/homebrew/plugins/wake-pc
sudo systemctl restart plugin_loader
```

If the folder is deleted manually, Decky does not run the uninstall hook; remove the
plugin's settings directory manually only if you also want to erase its saved host.

## Logs and troubleshooting

View recent Decky service logs over SSH or in Desktop Mode:

```sh
journalctl -u plugin_loader -n 200 --no-pager
journalctl -u plugin_loader -f
```

Filter for this plugin's backend messages:

```sh
journalctl -u plugin_loader --no-pager | rg "Wake PC|Wake-on-LAN"
```

If `rg` is unavailable, use `grep -E "Wake PC|Wake-on-LAN"`.

Common checks:

- **Invalid MAC address:** use `00:11:22:33:44:55`, `00-11-22-33-44-55`,
  `0011.2233.4455`, or 12 hex digits.
- **Network send failed:** verify the SteamOS device is on the same LAN and try the
  subnet broadcast (for example `192.168.1.255`) instead of leaving it blank.
- **Packet sent, PC did not wake:** confirm the wired NIC still has link lights while
  asleep and that the saved MAC belongs to that Ethernet adapter. Some routers or
  Wi-Fi client-isolation modes block broadcasts.
- **PC is awake but status says Offline:** confirm Steam is running, Windows Firewall
  allows Steam Remote Play, and TCP 27036 is listening. Try TCP 27037 or another
  known listening service if appropriate.
- **Router was reset or the saved PC IP changed:** open **Setup & status…** from the
  Windows companion tray, update the IP address in Wake PC Settings, and
  recreate the DHCP reservation. A network change does not erase pairing.
- **Plugin does not appear:** confirm the archive extracted to exactly
  `/home/deck/homebrew/plugins/wake-pc/dist/index.js`, then restart `plugin_loader`.
- **Sleep PC does not appear:** open **Setup & status…** from the Windows notification-
  area icon, choose **Pair for Sleep…**, then enter its six-digit code in the
  **Sleep PC** section and select **Pair for Sleep**.
- **Pairing code rejected:** use the Windows notification-area icon to request a
  fresh 30-minute code. Ten incorrect attempts lock the current code.

## TCP status limitation

`Online` means a TCP connection to the configured address and port succeeded.
`Offline` means only that the connection did not succeed before its short timeout.
A firewall, a stopped Steam client, a changed DHCP address, or a different listening
port can all make an awake PC appear Offline. For that reason the UI never describes
an Offline result as proof that the Wake-on-LAN packet failed.

## License and references

BSD-3-Clause. The implementation was authored for this plugin; no code was copied
from third-party Wake-on-LAN plugins. Architecture and packaging were checked against
the official Decky template, and the status-port default against
[Steam's required-ports documentation](https://help.steampowered.com/en/faqs/view/2EA8-4D75-DA21-31EB).
