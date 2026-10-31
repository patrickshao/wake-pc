import { mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { root, runNode, runPnpm, runPython, testEnvironment } from "./tooling.mjs";

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
    case "verify":
      runPnpm(["install", "--frozen-lockfile", "--ignore-scripts"]);
      runNode(["scripts/check_dependency_policy.mjs"]);
      runNode(["scripts/audit_locked_dependencies.mjs"]);
      runNode([tsc, "--noEmit"]);
      pythonTests();
      frontendTests();
      packageArtifacts();
      console.log("Supply-chain policy, vulnerability scan, tests, and artifact checks passed.");
      break;
    default: throw new Error("Choose test, test:python, test:frontend, package, serve, or verify");
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = error.status || 1;
}
