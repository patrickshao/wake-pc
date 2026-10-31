import json
import subprocess
import unittest

FINDING = {
    "packageName": "glob",
    "severity": "moderate",
    "title": "Example",
    "url": "https://github.com/advisories/GHSA-example",
}
ENTRY = {
    "advisory": FINDING["url"],
    "package": "glob",
    "versions": ["10.5.0"],
    "reachedThrough": "@decky/rollup",
    "reason": "Build-only.",
    "reviewBy": "2027-01-31",
}
SCRIPT = """
import { evaluateAdvisories } from "./scripts/advisory_policy.mjs";
const input = JSON.parse(process.argv[1]);
console.log(JSON.stringify(evaluateAdvisories({
  findings: input.findings,
  installed: new Map(Object.entries(input.installed)),
  productionPackages: new Set(input.production),
  accepted: input.accepted,
  today: input.today,
})));
"""


def evaluate(findings=(FINDING,), accepted=(ENTRY,), installed=None, production=(), today="2026-11-01"):
    payload = json.dumps(
        {
            "findings": list(findings),
            "accepted": list(accepted),
            "installed": installed or {"glob": ["10.5.0"]},
            "production": list(production),
            "today": today,
        }
    )
    result = subprocess.run(
        ["node", "--input-type=module", "-e", SCRIPT, payload],
        check=True,
        capture_output=True,
        text=True,
    )
    return json.loads(result.stdout)


class AdvisoryPolicyTests(unittest.TestCase):
    def test_exact_acceptance_passes_and_is_reported(self) -> None:
        result = evaluate()
        self.assertEqual([], result["failures"])
        self.assertEqual(1, len(result["acceptedFindings"]))

    def test_new_advisory_of_any_severity_fails(self) -> None:
        low = {**FINDING, "severity": "low", "url": "https://github.com/advisories/GHSA-new"}
        self.assertIn("GHSA-new", evaluate(findings=(FINDING, low))["failures"][0])

    def test_another_package_cannot_use_the_acceptance(self) -> None:
        other = {**FINDING, "packageName": "minimatch"}
        result = evaluate(findings=(FINDING, other), installed={"glob": ["10.5.0"], "minimatch": ["3.1.5"]})
        self.assertEqual(1, len(result["failures"]))
        self.assertIn("minimatch", result["failures"][0])

    def test_unreviewed_version_fails(self) -> None:
        result = evaluate(installed={"glob": ["10.5.0", "11.0.0"]})
        self.assertIn("11.0.0", result["failures"][0])
        self.assertEqual([], result["acceptedFindings"])

    def test_expired_acceptance_fails(self) -> None:
        self.assertIn("expired", evaluate(today="2027-02-01")["failures"][0])

    def test_acceptance_is_valid_through_its_review_date(self) -> None:
        self.assertEqual([], evaluate(today="2027-01-31")["failures"])

    def test_production_dependency_cannot_be_accepted(self) -> None:
        self.assertIn("production", evaluate(production=("glob",))["failures"][0])

    def test_stale_acceptance_fails(self) -> None:
        self.assertIn("no longer reports", evaluate(findings=())["failures"][0])

    def test_acceptance_needs_a_reason(self) -> None:
        self.assertIn("reason", evaluate(accepted=({**ENTRY, "reason": " "},))["failures"][0])


if __name__ == "__main__":
    unittest.main()
