/** Apply SECURITY.md's documented risk acceptances to advisory findings. Any other finding fails. */
export function evaluateAdvisories({ findings, installed, productionPackages, accepted, today }) {
  const failures = [];
  const acceptedFindings = [];
  const usedEntries = new Set();
  for (const entry of accepted) {
    const label = `${entry.package} ${entry.advisory}`;
    if (!entry.reason?.trim() || !entry.reachedThrough?.trim()) {
      failures.push(`Acceptance for ${label} must state a reason and how it is reached.`);
    }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(entry.reviewBy || "")) {
      failures.push(`Acceptance for ${label} must have a reviewBy date (YYYY-MM-DD).`);
    } else if (entry.reviewBy < today) {
      failures.push(`Acceptance for ${label} expired on ${entry.reviewBy}. Review it again.`);
    }
    if (productionPackages.has(entry.package)) {
      failures.push(`${entry.package} is a production dependency, so ${entry.advisory} cannot be accepted.`);
    }
  }
  for (const finding of findings) {
    const line = `${finding.severity || "unknown"}: ${finding.packageName} — ${finding.title} (${finding.url})`;
    const entry = accepted.find(
      (candidate) => candidate.advisory === finding.url && candidate.package === finding.packageName,
    );
    if (!entry) {
      failures.push(line);
      continue;
    }
    usedEntries.add(entry);
    const unreviewed = [...(installed.get(finding.packageName) || [])].filter(
      (version) => !entry.versions?.includes(version),
    );
    if (unreviewed.length) {
      failures.push(`${line} is accepted, but not for version ${unreviewed.join(", ")}.`);
      continue;
    }
    acceptedFindings.push(line);
  }
  for (const entry of accepted) {
    if (!usedEntries.has(entry)) {
      failures.push(`Remove the acceptance for ${entry.package} ${entry.advisory}: the scan no longer reports it.`);
    }
  }
  return { failures, acceptedFindings };
}
