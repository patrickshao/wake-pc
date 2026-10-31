# Releasing

Wake PC is a hobby project. Windows installers are intentionally unsigned.

1. **Bump only what changed.** The Decky plugin version is in `package.json` and
   `PLUGIN_VERSION` in `src/index.tsx`, which must match. The companion version is in
   `windows-native/version.txt`.
2. **Merge once Release checks passes**, then download `wake-pc-release-assets` from
   the run on that main commit:

   ```sh
   gh run download <run-id> -n wake-pc-release-assets -D assets
   ```

3. **Try it on real hardware.** Install what changed, then do one Sleep and one Wake.
4. **Publish.** Tag the release with both versions, such as
   `plugin-v1.0.0-bridge-v1.1.0`, and attach all eight files. Say briefly what changed
   and what you tested.

   ```sh
   gh release create <tag> assets/* --target <commit> --latest --notes-file notes.md
   ```

## Rules

- Never replace a published version with different bytes. CI rebuilds an unchanged
  component, so replace those files in `assets` with the ones from its last release:

  ```sh
  gh release download <previous-tag> -p 'wake-pc-v*.zip*' -p 'wake-pc-latest.zip*' -D assets --clobber
  ```

- Every Latest release needs both stable filenames, even if only one component
  changed. The plugin's **Copy update URL** depends on them:
  - `https://github.com/patrickshao/wake-pc/releases/latest/download/wake-pc-latest.zip`
  - `https://github.com/patrickshao/wake-pc/releases/latest/download/wake-pc-sleep-bridge-latest-setup.exe`
