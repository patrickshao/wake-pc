# Release runbook

Windows installers are intentionally unsigned hobby releases. A signing certificate
is optional and is not a release gate.

## First public release: 1.0.0

Both components start their public release history at 1.0.0. The former 1.6.x
versions were private development builds. Keep the existing installer AppId and
configuration locations so local testing does not erase saved setup or pairing.
Test an in-place transition from the development installer before publishing.

Prepare a snapshot from the tested, merged commit with `git archive`, preserving
the license and third-party notices. Initialize a new Git repository in that
snapshot with one initial commit; do not copy `.git`, ignored build output, local
configuration, or device logs. Validate the snapshot again.

At publication, rename the existing private GitHub repository to
`decky-wake-pc-development-archive`, keep it private, and archive it. Create the
fresh `wake-pc` repository using the snapshot. Do not
force-push away the development history or make the archive public. Repository
renaming, archival, creation, and publication are separate from preparing and
validating the snapshot. The initial release tag is `v1.0.0`.

## Build and verify

1. Update `package.json` and `PLUGIN_VERSION` in `src/index.tsx` together when the
   Decky plugin changes. The companion has its own `windows-native/version.txt`;
   update that only when releasing companion changes. Never replace a published
   version with different bytes.
2. Run `pnpm run security:verify`. For local companion builds, install the exact
   SDK in `global.json`, run `windows-native/scripts/bootstrap-inno.ps1` followed by
   `windows-native/scripts/build.ps1` on Windows, then run
   `pnpm run security:verify --with-companion`.
3. Merge the PR after **Release checks** passes. Download `wake-pc-release-assets`
   from the successful run on the exact main commit being released. This includes
   the versioned and stable Decky ZIPs, unsigned Windows installers, and SHA-256
   sidecars. The workflow validates both artifacts and publishes nothing.
4. Verify the downloaded checksums (`sha256sum --check *.sha256` on Linux, or
   `shasum -a 256 -c *.sha256` on macOS). On Windows, compare
   `Get-FileHash <download> -Algorithm SHA256` with its sidecar.

## Device acceptance

Record the tested commit, SteamOS/Decky versions, Windows version, and results in
the release notes. These checks require real hardware and are not replaced by CI:

- Install the ZIP in Decky and verify first-run settings and controller input.
- Install the Mac command from the source snapshot. Verify setup, wake, status,
  pairing alongside Decky, and an explicitly confirmed sleep request.
- Check the Windows companion's tray, setup and pairing dialogs at 100%, 150%,
  and 200% scaling, including keyboard navigation and long network details.
- Install the companion; pair, reject a wrong code, and reject reuse of a code.
- Complete five Wake → Remote Play → confirmed Sleep → Wake cycles. Confirm the
  lock/sign-in screen after waking and that Wake works with the companion stopped.
- Upgrade while the companion is running; confirm settings and pairing survive. From
  1.0.0, confirm the finish screen asks for a new pairing and that pairing works.
- Verify sign-in startup, crash recovery, and **Exit until next sign-in**.
- Uninstall; confirm files, startup entry, URL reservation, and firewall rule are
  removed. See [Windows acceptance](windows-installer.md#validation-gates).

## Publish when acceptance passes

Before publishing, review every commit in the fresh repository for credentials or
private configuration, and enable GitHub private vulnerability reporting when
available. Keep the development archive private. Removing data only from the
latest commit does not remove it from history.

Create a draft GitHub Release targeting the tested main commit. Use `v1.0.0` for
the first public release. For subsequent releases, use the plugin and companion
versions in the tag (for example, `plugin-v1.1.0-bridge-v1.0.0`) so either component
can release independently. Attach all eight files from
`wake-pc-release-assets`. For an unchanged, already-published component, reuse its
original download and checksums instead of replacing it with a fresh build; verify
the assembled set again before publishing.

Use these release-note sections:

- Changes and supported platforms (SteamOS/Decky, Mac CLI, and Windows x64).
- Install: Decky ZIP, Mac command setup, and optional Windows companion download.
- **Windows installers are unsigned.** Include checksum verification instructions.
- Device acceptance results and any known limitations. Pair only on a trusted LAN;
  the one-time pairing response transfers its key over local HTTP.

When ready, make the repository public and publish the draft as the latest stable
release. Confirm these direct links download successfully without authentication:

- `https://github.com/patrickshao/wake-pc/releases/latest/download/wake-pc-latest.zip`
- `https://github.com/patrickshao/wake-pc/releases/latest/download/wake-pc-sleep-bridge-latest-setup.exe`

Attach both stable filenames to every release marked **Latest**, even when only
one component changes. Otherwise the plugin's **Copy update URL** can break.
