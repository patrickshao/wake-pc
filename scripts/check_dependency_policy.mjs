import { runPnpm } from "./tooling.mjs";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";

const root = new URL("../", import.meta.url);
const readRoot = (name) => readFileSync(new URL(name, root), "utf8");
const manifest = JSON.parse(readRoot("package.json"));
const lockfile = readRoot("pnpm-lock.yaml");
const failures = [];
const listPnpmGraph = (options) => runPnpm(["list", "--depth", "Infinity", "--json"], { stdio: "pipe", ...options });

const direct = {
  ...(manifest.dependencies || {}),
  ...(manifest.devDependencies || {}),
  ...(manifest.optionalDependencies || {}),
};

const exactVersion = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;
for (const [name, version] of Object.entries(direct)) {
  if (!exactVersion.test(version)) {
    failures.push(`${name} must use an exact version, found ${version}`);
  }
}

if (/\b(?:git\+|git:|github:|https?:|file:|link:|workspace:)/i.test(
  Object.values(direct).join("\n"),
)) {
  failures.push("Direct dependencies must come from the configured npm registry.");
}

if (/\b(?:git\+|git:|github:|file:|link:)/i.test(lockfile)) {
  failures.push("Lockfile contains a non-registry dependency source.");
}

const integrityCount = (lockfile.match(/integrity: sha512-/g) || []).length;
if (integrityCount === 0) {
  failures.push("Lockfile contains no SHA-512 integrity records.");
}

const graph = JSON.parse(
  listPnpmGraph({
    cwd: new URL(".", root),
    encoding: "utf8",
  }),
);

const packages = new Map();
const visit = (node, nameHint) => {
  const name = node?.name || node?.from || nameHint;
  if (name && node?.version && node?.path) {
    packages.set(`${name}@${node.version}`, { ...node, name });
  }
  for (const group of [node?.dependencies, node?.devDependencies, node?.optionalDependencies]) {
    for (const [dependencyName, dependency] of Object.entries(group || {})) {
      visit(dependency, dependencyName);
    }
  }
};
for (const rootNode of graph) visit(rootNode, rootNode.name);

if (integrityCount < packages.size - 1) {
  failures.push(
    `Lockfile has fewer integrity records (${integrityCount}) than resolved package versions (${packages.size - 1}).`,
  );
}

const executableLifecyclePackages = [];
const nativePackages = [];
const walkForNative = (directory, depth = 0) => {
  if (depth > 5 || !existsSync(directory)) return false;
  for (const entry of readdirSync(directory)) {
    if (entry === "node_modules") continue;
    const path = join(directory, entry);
    const stats = statSync(path);
    if (stats.isFile() && (entry.endsWith(".node") || entry.endsWith(".wasm"))) return true;
    if (stats.isDirectory() && walkForNative(path, depth + 1)) return true;
  }
  return false;
};

for (const [id, node] of packages) {
  if (node.resolved && !node.resolved.startsWith("https://registry.npmjs.org/")) {
    failures.push(`${id} resolves outside registry.npmjs.org: ${node.resolved}`);
  }
  const packagePath = join(node.path, "package.json");
  if (!existsSync(packagePath)) continue;
  const installedManifest = JSON.parse(readFileSync(packagePath, "utf8"));
  const scripts = installedManifest.scripts || {};
  const lifecycle = ["preinstall", "install", "postinstall"].filter((name) => scripts[name]);
  if (lifecycle.length) executableLifecyclePackages.push({ id, lifecycle });
  if (installedManifest.gypfile || walkForNative(node.path)) nativePackages.push(id);
}

if (executableLifecyclePackages.length) {
  failures.push(
    `Executable install hooks require explicit review: ${JSON.stringify(executableLifecyclePackages)}`,
  );
}

const unexpectedNative = nativePackages.filter((id) => {
  if (id === "fsevents@2.3.3") return false;
  if (!id.startsWith("@rollup/rollup-")) return true;
  return !id.endsWith(`@${manifest.devDependencies.rollup}`);
});
if (unexpectedNative.length) {
  failures.push(`Unexpected native/Wasm packages: ${unexpectedNative.join(", ")}`);
}

console.log(
  JSON.stringify(
    {
      directPackageCount: Object.keys(direct).length,
      resolvedPackageVersionCount: packages.size,
      lockfileSha512IntegrityRecords: integrityCount,
      executableInstallHookPackages: executableLifecyclePackages,
      reviewedNativeBuildPackages: nativePackages,
    },
    null,
    2,
  ),
);

if (failures.length) {
  for (const failure of failures) console.error(`POLICY FAILURE: ${failure}`);
  process.exit(1);
}
