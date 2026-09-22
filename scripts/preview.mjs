import { createServer } from "node:http";
import { readFileSync, mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { resolve } from "node:path";
import { rollup } from "rollup";
import ts from "typescript";

// Reuse the bundler adapters already supplied by the Decky build preset.
const require = createRequire(import.meta.resolve("@decky/rollup"));
const { nodeResolve } = require("@rollup/plugin-node-resolve");
const commonjs = require("@rollup/plugin-commonjs");
const root = resolve(import.meta.dirname, "..");
const output = resolve(root, "out/preview");
mkdirSync(output, { recursive: true });
const bundle = await rollup({
  input: resolve(root, "preview/main.tsx"),
  plugins: [
    {
      name: "preview-adapters",
      resolveId(id) {
        if (id === "@decky/ui") return resolve(root, "preview/ui.tsx");
        if (id === "@decky/api") return resolve(root, "preview/api.ts");
      },
      transform(code, id) {
        if (/\.tsx?$/.test(id)) return {
          code: ts.transpileModule(code, { fileName: id, compilerOptions: {
            jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.ESNext,
            target: ts.ScriptTarget.ES2022,
          }}).outputText,
          map: null,
        };
        if (id.includes("node_modules")) return {
          code: code.replaceAll("process.env.NODE_ENV", '"development"'), map: null,
        };
      },
    },
    nodeResolve({ browser: true, extensions: [".js", ".ts", ".tsx"] }),
    commonjs(),
  ],
});
await bundle.write({ file: resolve(output, "app.js"), format: "iife" });
await bundle.close();
if (!process.argv.includes("--build")) {
  const files = {
    "/": [resolve(root, "preview/index.html"), "text/html"],
    "/app.js": [resolve(output, "app.js"), "text/javascript"],
    "/style.css": [resolve(root, "preview/style.css"), "text/css"],
  };
  createServer((request, response) => {
    const file = files[new URL(request.url, "http://localhost").pathname];
    if (!file) { response.writeHead(404); response.end(); return; }
    response.writeHead(200, { "Content-Type": file[1], "Cache-Control": "no-store" });
    response.end(readFileSync(file[0]));
  }).listen(4173, "127.0.0.1", () => console.log("Preview: http://127.0.0.1:4173 (mock devices; restart after edits)"));
}
