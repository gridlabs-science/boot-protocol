# Follow-on Security Hardening Candidate

This branch is intentionally separate from
`codex/security-rt-041-042@400fc6e`. Commit `400fc6e` remains immutable for the
independent retest of RT-2026-041 and RT-2026-042. The controls below are a
follow-on candidate and must not be described as independently verified until
that review is recorded and the branches are deliberately integrated.

## Implemented Controls

- DATUM connections, declared message length, and read time are bounded before
  allocating or waiting for a message body.
- Miner labels are bounded and normalized before storage; legacy dynamic HTML
  output escapes miner-controlled labels and responses carry a restrictive CSP.
- Reachability tests require authenticated administration, reject non-public
  destinations, disable redirects, validate the actual connection address, and
  no longer implement unauthenticated UDP callbacks.
- A fresh mining proof cannot teach the node a Bitcoin parent. Parent authority
  comes from the attached Bitcoin source; retained proof recovery remains in the
  validating state reconciliation path.
- Payout configuration fails closed. DATUM work is unavailable without a valid,
  explicitly configured payout identity.
- Snapshot payout validation accepts the canonical configured variant only.
- Candidate-state fetch decisions do not trust peer-claimed total difficulty,
  and outbound state-bundle fetches have a per-peer budget.
- One- and two-block RPC reconciliation use the reorganization path and unwind
  removed snapshot families before applying a replacement boundary.
- Repeated failed RPC polls cannot renew the attached-node safety grace period;
  mining becomes unsafe after the configured grace until synchronized RPC
  authority returns.
- NuGet restores are locked, container bases are digest-pinned, mutable-branch
  live updates are refused, and CI performs dependency, secret, and filesystem
  vulnerability checks.

The repository history contains retired prototype node identities from early
tracked configuration files, plus audited test/command false positives. Their
exact Gitleaks fingerprints are baselined in `.gitleaksignore`; those historical
identities are considered compromised and must never be used by an active
node. Any finding outside that immutable baseline fails CI.

## Verification

Run:

```bash
dotnet restore boot_portal.slnx --locked-mode
dotnet test boot_portal.slnx --no-restore
dotnet list boot_portal/boot_portal.csproj package --vulnerable --include-transitive
dotnet list boot.tests/boot.tests.csproj package --vulnerable --include-transitive
```

Package deployment and the public soak remain blocked on the exact `400fc6e`
retest, review of this follow-on branch, deliberate integration into the current
runtime line, and appliance acceptance against digest-locked images.
