# Supply-chain security policy

## Runtime boundary

The installable Decky archive contains no `node_modules`, package manager, compiled
third-party native executable, or Python dependency. SteamOS receives the plugin's
source backend (Python standard library only) and the compiled JavaScript bundle.
Decky provides React and `@decky/ui` at runtime. The bundle contains this plugin's
code and the official `@decky/api` client; that client's license and corresponding
source are included for inspection and LGPL compliance.

Build dependencies still matter because compromised build tooling can change the
bundle. They are therefore treated as part of the trusted computing base.

The optional Windows Setup.exe is a distinct runtime boundary containing the
first-party self-contained .NET 10 `WakePcBridge.exe` and an Inno Setup 7 installer.
The runtime uses .NET/Windows platform APIs, HTTP.sys, Windows Forms, and the
standard Windows Run key. It has no third-party NuGet dependency, and setup
downloads nothing. Inno owns files, ACLs, upgrades, Add/Remove Programs, and
uninstall; the bridge executable exposes bounded, elevated system-registration
commands for its URL ACL and firewall rule. Its fixed pairing/sleep routes,
authentication protocol, privilege model, threat analysis, signed-in-user runtime
model, Private/Public LocalSubnet firewall scope, and residual risks are documented
in `docs/sleep-bridge.md`.

## Required controls

- Every direct package uses an exact version and every transitive resolution is
  locked in `pnpm-lock.yaml` with npm SHA-512 integrity metadata.
- Only `https://registry.npmjs.org/` is allowed. Git, URL, file, link, and workspace
  package sources are rejected by the policy checker.
- Dependency lifecycle scripts are disabled in `.npmrc` and on the verification
  command line. New `preinstall`, `install`, or `postinstall` hooks fail policy.
- New native or WebAssembly payloads fail policy. Rollup's version-matched,
  platform-specific build binary and its exact `fsevents@2.3.3` macOS watcher are the
  reviewed build-only exceptions; neither is shipped.
- The locked-graph checker submits every exact production and development version to
  npm's supported bulk advisory endpoint and fails for any known severity. A finding
  requires remediation or an explicit, documented risk acceptance; a zero-result scan
  is expected for this project.
- Registry signatures and provenance attestations are checked separately because
  integrity, authenticity, provenance, and absence of known vulnerabilities are
  different properties.
- The packaged Decky ZIP and compiled Windows Setup.exe are inspected so build-only
  packages cannot cross into either runtime boundary.

Run the enforced local gate with:

```sh
pnpm run security:verify
```

The default gate packages Decky only. Add `--with-companion` to also validate a
previously built Windows installer, or use CI to build and validate both platforms.

The gate uses npm's signature verifier in an isolated, scripts-disabled temporary
resolution because npm does not consume `pnpm-lock.yaml`. The pnpm policy checker
separately verifies the authoritative locked graph and registry origins. This gives
coverage from two independent views without adding a second lockfile to the project.

## Adding or updating a package

Before changing a manifest or lockfile:

1. Establish why existing code or platform APIs cannot satisfy the need.
2. Check the exact package name for typosquatting and inspect the registry owner,
   maintainers, repository ownership, release history, and recent ownership changes.
3. Read the exact-version manifest and published tarball. Review lifecycle scripts,
   binaries, native/Wasm content, network behavior, and the complete transitive diff.
4. Check current advisories using the ecosystem audit plus an independent advisory
   source when risk warrants it.
5. Verify registry signatures and available Sigstore/SLSA provenance. Missing
   provenance is a recorded residual risk, never silently treated as verified.
6. Review licenses and ensure redistribution obligations are satisfied.
7. Pin the exact version, regenerate the lockfile with scripts disabled, run the full
   security gate, inspect the compiled bundle and release artifacts, and document
   the decision.

Inno Setup 7.0.2 is a pinned native build tool, not a runtime download. This hobby
project distributes unsigned Windows installers. Authenticode signing is optional;
`-SignToolCommand` can sign Setup.exe and its generated uninstaller if a maintainer
chooses to configure it. Release notes must disclose unsigned downloads and include
SHA-256 sidecars. A checksum verifies file integrity, not publisher identity.

Security tools only establish specific facts. They do not prove a package is benign,
and this project must not make an absolute “safe” or “zero risk” claim.

## Reporting a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/patrickshao/wake-pc/security/advisories/new)
when enabled. If that option is unavailable, open an issue asking the maintainer
for a private contact channel, without posting exploit details, pairing codes,
keys, or diagnostic reports. Include affected versions and reproduction steps in
the private report. This hobby project has no guaranteed response time.
