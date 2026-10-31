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

- Every direct package uses an exact version, and `pnpm-lock.yaml` locks every
  transitive package to npm SHA-512 integrity.
- Only `https://registry.npmjs.org/` is allowed. Git, URL, file, link, and workspace
  package sources fail policy.
- Dependency lifecycle scripts are disabled in `.npmrc` and in the verification command.
- New native or WebAssembly packages fail policy. Rollup's platform binary and the exact
  `fsevents@2.3.3` macOS watcher are build-only exceptions; neither is shipped.
- The advisory scan covers the locked graph. A finding in a production package fails at
  any severity. A finding in build tooling is printed and does not fail, because it
  never reaches users. Read those lines when updating dependencies.
- The Decky ZIP fails validation if it contains `node_modules` or native/Wasm libraries,
  enables Decky flags, or if its source map shows anything bundled beyond `src/` and
  `@decky/api`. The source map is a guard against accidental bundling, not against a
  malicious build tool.
- The job that assembles release assets does not run packaged code, so a compromised
  plugin build cannot replace Setup.exe there. Setup.exe is checked for its name,
  version, and Inno Setup marker, which does not prove its contents.
- Inno Setup 7.0.2 is a pinned build tool, verified by SHA-256 and Authenticode before
  use. The compiled installer downloads nothing.

A compromised build package could still alter the Decky bundle. The controls above make
that harder but cannot rule it out, and no tool here proves a package is benign.

The official `@decky/rollup` preset brings in deprecated build-only packages such as
`glob` and `rimraf`. Keep the preset anyway: replacing it adds more compatibility risk
than they do. pnpm stays on version 9 for Decky's documented build compatibility, and
because npm retired the audit endpoint it uses, the scan calls npm's bulk advisory
endpoint directly.

Run the gate with:

```sh
pnpm run security:verify
```

Add `--with-companion` to also validate a locally built Windows installer. CI builds and
validates both platforms.

## Adding or updating a package

1. Avoid it if existing code or platform APIs can do the job.
2. Check the exact package name for typosquatting, and look over what it brings in.
3. Pin the exact version, regenerate the lockfile with scripts disabled, and run the gate.

## Releases

This hobby project distributes unsigned Windows installers. Authenticode signing is
optional; `-SignToolCommand` can sign Setup.exe and its uninstaller. Release notes must
disclose unsigned downloads and include SHA-256 sidecars. A checksum detects changed
files; it does not identify the publisher.

## Reporting a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/patrickshao/wake-pc/security/advisories/new)
when enabled. If that option is unavailable, open an issue asking the maintainer
for a private contact channel, without posting exploit details, pairing codes,
keys, or diagnostic reports. Include affected versions and reproduction steps in
the private report. This hobby project has no guaranteed response time.
