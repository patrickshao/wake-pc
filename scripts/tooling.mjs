import { execFileSync, spawnSync } from "node:child_process";
import { basename, delimiter, join } from "node:path";
import { fileURLToPath } from "node:url";

export const root = fileURLToPath(new URL("../", import.meta.url));
export const runNode = (args, options = {}) =>
  execFileSync(process.execPath, args, { cwd: root, stdio: "inherit", ...options });

// On Windows package-manager launchers are .cmd files. All fallback arguments are
// fixed tooling options, never user input or paths interpolated into shell code.
function packageManager(name, args, options = {}) {
  const script = name === "pnpm"
    ? process.env.PNPM_SCRIPT || (
      /pnpm(?:\.c?js)?$/.test(basename(process.env.npm_execpath || ""))
        ? process.env.npm_execpath : undefined
    )
    : undefined;
  if (script) return runNode([script, ...args], options);
  const settings = { cwd: root, stdio: "inherit", ...options };
  if (process.platform !== "win32") return execFileSync(name, args, settings);
  if (args.some((arg) => !/^[a-zA-Z0-9_@./:=+-]+$/.test(arg))) {
    throw new Error("Unsupported package-manager argument");
  }
  return execFileSync(process.env.ComSpec || "cmd.exe",
    ["/d", "/s", "/c", `${name}.cmd ${args.join(" ")}`], settings);
}

let pnpmVerified = false;
export function runPnpm(args, options = {}) {
  if (!pnpmVerified) {
    const version = packageManager("pnpm", ["--version"], { stdio: "pipe", encoding: "utf8" }).trim();
    if (version !== "9.15.9") {
      throw new Error(`Use pnpm 9.15.9 from package.json; found ${version}. Activate it with Corepack or set PNPM_SCRIPT to its pnpm.cjs.`);
    }
    pnpmVerified = true;
  }
  return packageManager("pnpm", args, options);
}
export const runNpm = (args, options = {}) => packageManager("npm", args, options);

let python;
export function runPython(args, options = {}) {
  if (!python) {
    const candidates = process.env.PYTHON
      ? [[process.env.PYTHON]]
      : process.platform === "win32"
        ? [["py", "-3"], ["python"], ["python3"]]
        : [["python3"], ["python"]];
    python = candidates.find(([command, ...prefix]) =>
      spawnSync(command, [...prefix, "-c", "import sys; sys.exit(sys.version_info < (3, 9))"],
        { stdio: "ignore", timeout: 10000 }).status === 0);
    if (!python) throw new Error("Python 3 was not found. Install Python or set PYTHON to the interpreter path.");
  }
  const [command, ...prefix] = python;
  return execFileSync(command, [...prefix, ...args], { cwd: root, stdio: "inherit", ...options });
}

export const testEnvironment = () => ({
  ...process.env,
  PYTHONPATH: [join(root, "py_modules"), process.env.PYTHONPATH].filter(Boolean).join(delimiter),
});
