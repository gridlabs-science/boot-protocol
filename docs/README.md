# GridPool Docs Index

This directory contains implementation, operator, and launch-readiness docs for
the GridPool reference node.

Project-wide concepts, decisions, research interpretation, and repository maps
live in the [GridPool handbook](https://github.com/gridlabs-science/gridpool-handbook).
Draft protocol documents live in
[`gridpool-spec`](https://github.com/gridlabs-science/gridpool-spec). This
directory owns reference-node implementation, operator, and launch-readiness
documentation.

## Start Here

- [Umbrel And Start9 Launch Checklist](umbrel-start9-launch-checklist.md):
  primary launch gate before one-click packages.
- [22-31 July Development Roadmap](next-week-development-roadmap.md): active
  execution order for V2.2 activation, soak, StratumRace, adapters, and UI work.
- [Project Architecture Map](project-architecture-map.md): redirect to the
  handbook-owned cross-project map.
- [Consensus Selection Audit](consensus-selection-audit.md): V2.1 snapshot
  boundary, merge-forward rule, and fork-choice reasoning.
- [V2.2 Monotonic Snapshot Reconciliation Draft](gridpool-v2.2-monotonic-snapshot-reconciliation-draft.md):
  consensus-version-22 design and implementation status for deterministic
  recovery from active-snapshot splits.
- [V2.2 Coordinated Cutover](v2.2-cutover.md): shipped consensus changes,
  compatibility behavior, deferred work, and operator rollout notes.
- [Technical FAQ](critic-faq.md): redirect to the handbook-owned FAQ.
- [Release Process](release-process.md): branch, tag, Docker image, and
  coordinated-upgrade policy.
- [Security And Privacy Review](security-privacy-review.md): active pre-package
  gate for key handling, public UI/API disclosure, logs, and private-node safety.
- [Critical State-Validation Security Release](security-release-rt-2026-041-042.md):
  release-candidate behavior and retest gate for proof-backed state sync and
  locally confirmed GridPool block payments.

## Operators

- [Node Bootstrap And Critical Configuration](node-bootstrap-and-critical-config.md)
- [Bitcoin Node Connectivity](bitcoin-node-connectivity.md)
- [Public Bitcoin Notification Rollout](bitcoin-notification-public-rollout.md)
- [Mainnet Beta Service Runbook](mainnet-beta-service-runbook.md)
- [GridPool Health Monitor](gridpool-health-monitor.md)
- [Testnet Full-Coinbase Compatibility Endpoint](testnet-full-coinbase-compatibility-endpoint.md)
- [Raspberry Pi Sovereign Stack Installer](raspberry-pi-one-shot-installer.md)
- [Testnet4 Real-Trigger Runbook](testnet4-real-trigger-runbook.md)

## Protocol And Architecture

- [256 Foundation Architecture](256-foundation-architecture.md)
- [Scaling Analysis](scaling-analysis.md)
- [Mining Hot Paths](mining-hot-paths.md)
- [Robust Networking Architecture Plan](robust-networking-architecture-plan.md)
- [Hashrate Estimation](hashrate-estimation.md)
- [Hydrapool HTTP Submission](hydrapool-http-submission.md)
- [CKPool And AtlasPool Integration](ckpool-atlaspool-integration.md)
- [DATUM Upstream Server Compatibility Notes](datum-server-compatibility-notes.md)
- [Stratum V2 / GridPool Evaluation](stratum-v2-gridpool-evaluation.md)
- [Firmware Coinbase Compatibility Matrix](firmware-coinbase-compatibility-matrix.md)

## Research And Testing

- [Modeling And Simulation Roadmap](modeling-and-simulation-roadmap.md): redirect
  to the research repository.
- [Simulation Findings](simulation-findings-2026-06.md): redirect to current
  research and the archived June snapshot.
- [Stress-Test Plan](stress-test-plan.md)
- [2500 DATUM Client Stress Architecture](datum-2500-stress-architecture.md)
- [V3 Branch Market Examples](v3-branch-market-examples.md)

## Product And Launch Planning

- [UI Modes Plan](ui-modes-plan.md)
- [Launch Infrastructure Rollout Plan](launch-infra-plan.md)
- [Launch Infra Click-Through Checklist](launch-infra-clickthrough.md)
- [Waiting For Funding And Launch Infrastructure Backlog](waiting-for-funding-backlog.md)
- [256 Foundation Proposal](256-foundation-proposal.md)
- [256 Foundation Status](256-foundation-status.md)

## Archive

Historical debugging notes, old session handoffs, and superseded launch plans
are in [archive/](archive/). These files may contain stale terminology or old
V1 assumptions.
