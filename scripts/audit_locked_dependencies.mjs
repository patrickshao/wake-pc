import { runPnpm } from "./tooling.mjs";
import { request } from "node:https";
import { readFileSync } from "node:fs";
import { evaluateAdvisories } from "./advisory_policy.mjs";

const listPnpmGraph = (options) => runPnpm(["list", "--depth", "Infinity", "--json"], { stdio: "pipe", ...options });
const graph = JSON.parse(
  listPnpmGraph({
    encoding: "utf8",
  }),
);

const packages = new Map();
const visit = (node, nameHint) => {
  const name = node?.name || node?.from || nameHint;
  if (name && node?.version && node?.path) {
    if (!packages.has(name)) packages.set(name, new Set());
    packages.get(name).add(node.version);
  }
  for (const group of [node?.dependencies, node?.devDependencies, node?.optionalDependencies]) {
    for (const [dependencyName, dependency] of Object.entries(group || {})) {
      visit(dependency, dependencyName);
    }
  }
};

for (const root of graph) {
  for (const group of [root.dependencies, root.devDependencies, root.optionalDependencies]) {
    for (const [dependencyName, dependency] of Object.entries(group || {})) {
      visit(dependency, dependencyName);
    }
  }
}

const payload = JSON.stringify(
  Object.fromEntries(
    [...packages.entries()]
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([name, versions]) => [name, [...versions].sort()]),
  ),
);

const response = await new Promise((resolve, reject) => {
  const auditRequest = request(
    "https://registry.npmjs.org/-/npm/v1/security/advisories/bulk",
    {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "Content-Length": Buffer.byteLength(payload),
        "User-Agent": "decky-wake-pc-locked-audit/1.0",
      },
    },
    (auditResponse) => {
      let body = "";
      auditResponse.setEncoding("utf8");
      auditResponse.on("data", (chunk) => {
        body += chunk;
      });
      auditResponse.on("end", () => {
        if (!auditResponse.statusCode || auditResponse.statusCode >= 300) {
          reject(
            new Error(
              `npm bulk advisory endpoint returned ${auditResponse.statusCode}: ${body}`,
            ),
          );
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (error) {
          reject(new Error(`npm bulk advisory endpoint returned invalid JSON: ${error}`));
        }
      });
    },
  );
  auditRequest.on("error", reject);
  auditRequest.end(payload);
});

const findings = Object.entries(response).flatMap(([packageName, advisories]) =>
  advisories.map((advisory) => ({ packageName, ...advisory })),
);
const versionCount = [...packages.values()].reduce((total, versions) => total + versions.size, 0);

const productionPackages = new Set();
const visitProduction = (dependencies) => {
  for (const [name, dependency] of Object.entries(dependencies || {})) {
    productionPackages.add(name);
    visitProduction(dependency?.dependencies);
  }
};
for (const root of JSON.parse(runPnpm(["list", "--prod", "--depth", "Infinity", "--json"], { stdio: "pipe", encoding: "utf8" }))) {
  visitProduction(root.dependencies);
}

const { accepted } = JSON.parse(readFileSync(new URL("../security/accepted-advisories.json", import.meta.url), "utf8"));
const { failures, acceptedFindings } = evaluateAdvisories({
  findings,
  installed: packages,
  productionPackages,
  accepted,
  today: new Date().toISOString().slice(0, 10),
});

for (const line of acceptedFindings) console.log(`Accepted build-only risk: ${line}`);
if (failures.length) {
  for (const failure of failures) console.error(failure);
  process.exit(1);
}

console.log(
  `No unaccepted vulnerabilities found for ${versionCount} locked package versions via npm's bulk advisory endpoint.`,
);
