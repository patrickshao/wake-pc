import { cpSync, mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { root, runNode, runNpm, runPnpm, runPython, testEnvironment } from "./tooling.mjs";

const [task, ...args] = process.argv.slice(2);
const tsc = join(root, "node_modules/typescript/bin/tsc");
const rollup = join(root, "node_modules/rollup/dist/bin/rollup");

function pythonTests() {
  runPython(["-m", "unittest", "discover", "-s", "tests", "-v"], { env: testEnvironment() });
}
function frontendTests() {
  const output = join(root, "out/test-frontend");
  rmSync(output, { recursive: true, force: true });
  mkdirSync(output, { recursive: true });
  runNode([tsc, "--target", "ES2022", "--module", "ES2022", "--moduleResolution", "node",
    "--strict", "--skipLibCheck", "--outDir", output, "src/runtime.ts", "tests/frontend_runtime.ts"]);
  runNode([join(output, "tests/frontend_runtime.js")]);
}
function packageArtifacts() {
  runNode([rollup, "-c"]);
  runPython(["scripts/package.py", ...args]);
}
function signatures() {
  const directory = mkdtempSync(join(tmpdir(), "wake-pc-signatures-"));
  const env = { ...process.env };
  for (const key of ["npm_config_strict_peer_dependencies", "npm_config_strict_store_pkg_content_check",
    "npm_config_verify_store_integrity"]) delete env[key];
  try {
    cpSync(join(root, "package.json"), join(directory, "package.json"));
    runNpm(["install", "--ignore-scripts", "--no-audit", "--no-fund", "--registry=https://registry.npmjs.org/"], { cwd: directory, env });
    runNpm(["audit", "signatures", "--registry=https://registry.npmjs.org/"], { cwd: directory, env });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

try {
  if (args.length && (!(task === "package" || task === "verify") ||
      args.length !== 1 || args[0] !== "--with-companion")) {
    throw new Error("Only package and verify accept --with-companion");
  }
  switch (task) {
    case "test": pythonTests(); frontendTests(); break;
    case "test:python": pythonTests(); break;
    case "test:frontend": frontendTests(); break;
    case "package": packageArtifacts(); break;
    case "serve": runPython(["-m", "http.server", "8000", "--bind", "0.0.0.0", "--directory", "out"]); break;
    case "signatures": signatures(); break;
    case "verify":
      runPnpm(["install", "--frozen-lockfile", "--ignore-scripts"]);
      runNode(["scripts/check_dependency_policy.mjs"]);
      runNode(["scripts/audit_locked_dependencies.mjs"]);
      signatures();
      runNode([tsc, "--noEmit"]);
      pythonTests();
      frontendTests();
      packageArtifacts();
      console.log("Supply-chain policy, vulnerability scan, signature check, tests, and artifact checks passed.");
      break;
    default: throw new Error("Choose test, test:python, test:frontend, package, serve, signatures, or verify");
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = error.status || 1;
}
