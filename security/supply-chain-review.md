# Supply-chain review — 2026-07-19

## Local preview addendum — 2026-09-21

The development-only browser preview adds exact-pinned `react` and `react-dom`
19.1.1, plus their locked `scheduler` dependency. Both direct packages identify
the official facebook/react repository. None of these packages has an install
lifecycle hook. Installation uses `--ignore-scripts`. The preview reuses the
existing Rollup adapters; production continues to use Decky's external React.
No preview files or bundled React runtime are included in the plugin ZIP.

## Release readiness addendum — 2026-09-19

This addendum supersedes the historical signing requirement and advisory results
below. Public hobby releases are unsigned; Authenticode remains optional.

- Updated only `brace-expansion` from 1.1.16 to 1.1.18 and 2.1.2 to 2.1.4,
  pinned by major-specific pnpm overrides. These fix
  [GHSA-mh99-v99m-4gvg](https://github.com/advisories/GHSA-mh99-v99m-4gvg) and
  [GHSA-rgw5-rvv9-x895](https://github.com/advisories/GHSA-rgw5-rvv9-x895).
- Reviewed both exact npm manifests and complete executable JavaScript payloads.
  Repository attribution remains `juliangruber/brace-expansion`; registry maintainers
  are juliangruber and isaacs. No install/lifecycle hooks, native payloads, network
  calls, new transitive packages, or license changes (MIT) were introduced.
- Both patched tarballs carry registry signatures and SHA-512 integrity metadata;
  neither publishes provenance attestations. The npm verification resolution checks
  130 registry signatures and 6 attestations. The authoritative pnpm graph contains
  148 locked versions.
- Added `global.json` with SDK 10.0.302 and roll-forward disabled. Installing an SDK
  in CI alone did not select it: newer runner SDKs caused locked ILLink restore drift.
  The NuGet lockfiles and runtime dependency versions are unchanged.
- CI runs the plugin security gate and Windows native build, then validates the
  independently versioned artifact pair and checksums. The complete frontend rebuild
  source, manifests, lockfile, and scripts-disabled npm configuration ship in the ZIP.
- Real-device acceptance remains separate from automated verification; see
  [the release runbook](../docs/releasing.md).

## Native Windows installer addendum — 2026-07-28

Wake PC Bridge replaces the PowerShell/CMD distribution path with a versioned Inno
Setup 7 Setup.exe containing first-party C# source targeting `net10.0-windows`. The
published runtime is self-contained and single-file. It uses only the .NET and
Windows platform libraries; no third-party NuGet package was added.

Restore is constrained by `NuGet.Config` to `Microsoft.*` packages from
`https://api.nuget.org/v3/index.json`. Restore inputs are locked. The only explicit
resolved build package is `Microsoft.NET.ILLink.Tasks@10.0.10`, pulled by the .NET SDK
for single-file publishing even though trimming is disabled. The application is built
with Microsoft .NET SDK 10.0.302 / runtime 10.0.10, warnings as errors, trimming and
Native AOT disabled, and an exact publish inventory of one executable.

Inno Setup 7.0.2 is pinned as a native build tool. The bootstrap verifies both the
official installer SHA-256 and its Pyrsys B.V. Authenticode signature before portable
repo-local extraction. The Inno license permits binary redistribution; the separate
commercial compiler-license request is recorded in `THIRD_PARTY_NOTICES.md`.

The package validator requires a plausible `MZ` executable containing the Inno Setup
marker and a version matching `windows-native/version.txt`. The compiled installer
downloads nothing and contains no PDB, source tree, config, key, log, or test output.
Hobby releases are unsigned. Authenticode signing is optional; public release notes
disclose this and include SHA-256 sidecars.

## Decision

Approved with the controls in `SECURITY.md`. The Decky artifact has no
package installation step and includes no third-party native binary or Python module.
The separate Windows companion contains the first-party native runtime inside an Inno
installer and uses Windows components already present on the target.

The frontend icon dependency was removed and replaced by four small local SVGs.
Unused direct `tslib`, React DOM types, and the platform-specific Rollup binary were
also removed. All remaining direct versions are exact.

## Direct trust set

| Package | Purpose | Registry integrity/signature | Published provenance |
| --- | --- | --- | --- |
| `@decky/api@1.1.3` | Official backend-callable client; bundled | Present | Not published |
| `@decky/ui@4.11.6` | Official Decky UI types/runtime external | Present | SLSA provenance present |
| `@decky/rollup@1.0.2` | Official template build preset | Present | Not published |
| `rollup@4.62.2` | Bundler | Present | SLSA provenance present |
| `typescript@5.9.3` | Compiler | Present | Not published |
| `@types/react@19.1.1` | Compile-time types | Present | Not published |
| `@types/react-dom@19.1.1` | Required Decky UI compile-time types | Present | Not published |
| `@types/react-router@5.1.20` | Required by `@decky/api` declarations | Present | Not published |

`@decky/rollup` has a small seven-file npm payload. Its executable source was manually
reviewed: it reads the local Decky manifest and composes standard Rollup plugins. It
has no lifecycle script or runtime role. Its lack of SLSA provenance remains a gap,
mitigated by exact pinning, npm registry signature verification, lockfile integrity,
scripts-disabled installation, and output inspection.

## Scan results

- Final graph: 8 exact-pinned direct packages and 148 locked dependency versions.
- Advisory scan before and after minimization: 0 info, low, moderate, high, or
  critical issues.
- Signature verification: all 130 packages installed in the independent current-
  platform npm resolution have verified registry signatures; 6 have verified
  provenance attestations. Optional binaries for other platforms remain protected by
  lockfile integrity and are verified when installed on their target platform.
- All dependency sources: npm registry; no Git or arbitrary URL resolutions.
- All 148 locked package payloads: SHA-512 integrity metadata present.
- Install-time lifecycle execution: disabled. The graph policy rejects packages with
  executable install hooks.
- Decky native runtime payload: none. Rollup's platform binary and `fsevents@2.3.3` macOS
  watcher are build-only, exact-version reviewed exceptions and are excluded from the
  ZIP. The published `fsevents` tarball contains a prebuilt macOS module, has npm
  integrity and a registry signature, and no provenance attestation. Lifecycle scripts
  remain disabled. Any different native/Wasm package fails the repository policy.
- Observed licenses: 0BSD, Apache-2.0, BSD-3-Clause, BlueOak-1.0.0, ISC, LGPL-2.1,
  and MIT. The bundled Decky API client's exact LGPL license, corresponding source,
  declarations, manifest, and notices are included in the ZIP. Decky UI and React are
  runtime externals and are not redistributed.
- Artifact packaging is deterministic under the recorded source tree, emits a SHA-256
  sidecar, verifies ZIP integrity, and fails if `node_modules` or native/Wasm libraries
  cross into the archive.
- The Windows Setup.exe has a versioned allowlisted name and verified Inno marker.
  Source policy rejects a PowerShell/CMD end-user distribution path. Native self-tests verify fixed
  pairing/sleep routes, pairing expiration/attempt lockout, HMAC compatibility,
  bounded replay prevention, redaction, and loopback behavior. Source tests verify the
  Private/Public LocalSubnet firewall restriction, trusted-network confirmation,
  standard Run-key startup, same-executable supervisor, and elevated/runtime split.

## Residual risk

Registry signatures and integrity hashes detect tampering relative to npm metadata;
they do not establish that maintainer-published code is non-malicious. SLSA provenance
links a package to a build source but likewise does not prove code safety. The two
official Decky packages without published provenance are the main provenance gap.
Future package changes must rerun the policy and receive a fresh human review.

The native runtime, protocol, fake-power path, loopback listener, and Inno compilation
were executed on Windows. Real Setup/upgrade/uninstall, HTTP.sys URL ACL, firewall,
actual suspend, sign-in recovery, and sign-in-after-wake behavior remain the explicit
manual acceptance boundary recorded in `docs/windows-installer.md`.

The official Decky Rollup preset currently brings four deprecated, build-only
transitives: `glob@10.5.0`, `glob@7.2.3`, `rimraf@3.0.2`, and `inflight@1.0.6`.
`glob@10.5.0` is outside the corrected vulnerable range for CVE-2025-64756. Advisories
later published against `glob`, `minimatch` and `deepmerge` in this preset are recorded with
their reasoning in `security/accepted-advisories.json`.
`inflight` is deprecated for a memory leak. These packages only operate on local
build files with lifecycle execution disabled and never enter the ZIP. Replacing the
official Decky preset solely to remove them would increase compatibility risk, so the
MVP records and monitors this maintenance debt.

The project remains on pnpm 9 for Decky's documented build compatibility. npm retired
the legacy audit endpoint used by pnpm's audit command in 2026, so the security gate
queries npm's supported bulk advisory endpoint directly using the exact pnpm graph.
The independent npm resolution still runs npm's native vulnerability and signature
verification as a second view.
