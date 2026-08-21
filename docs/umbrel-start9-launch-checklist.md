# Umbrel And Start9 Launch Checklist

Status: primary working checklist before packaging GridPool for broad one-click installs.

This checklist is intentionally stricter than "public beta works on my machine." Umbrel and Start9 users will expect upgrade stability, clear failure modes, and a node that can run unattended without needing frequent manual git pulls.

The immediate execution order is maintained in
[next-week-development-roadmap.md](next-week-development-roadmap.md). That plan
does not weaken the gates below.

## Launch Gate Summary

- [x] Consensus selection rule audited and frozen for the first packaged beta. V2.2 Monotonic Snapshot Reconciliation is implemented and height-gated for coordinated activation at Bitcoin height `959500`.
- [x] Protocol/API/state/peer version fields and compatibility checks are implemented.
- [ ] A pinned multi-region beta runs stably for at least 7 consecutive days.
  Main and Oregon are the required soak anchors; other operator nodes add
  evidence when available but do not control the clock.
- [ ] Hidden/outbound-only node behavior is clear, observable, and safe.
- [ ] Umbrel and StartOS sideload packages complete install, upgrade, restart,
  backup, and recovery canaries on real appliances.
- [ ] Public docs and UI match V2.2 snapshot-family reconciliation and current operational reality. Core technical docs are updated; the full UI audit remains open.
- [x] Initial miner-transport support policy is explicit: native SV2 is the
  only promised production path; Stratum V1 firmware, rental services, and
  DATUM remain experimental/unsupported unless a specific version is listed as
  tested.
- [x] Monitoring catches the failure classes already seen in public beta.
- [ ] Security/privacy review is complete: no secret logging, private nodes do
  not leak endpoints/IPs, and unauthenticated UI/API fields are intentionally
  classified and redacted.
- [x] Repo and cross-project handbook are organized enough for outside
  contributors to find current specifications, runtime code, adapters, and
  archived research.

## G1: Consensus Selection And State Convergence

Goal: activate and validate V2.2 snapshot-family reconciliation without expanding
the first packaged beta into the full V3 branch-market research problem.

Current rollout posture:

- Work Set and active snapshot proofs must remain fully validated before import.
- Incompatible consensus or state-bundle schema versions must fail visibly and safely.
- Before activation, established nodes retain V2.1 compatible-state merge rules.
- At and above height `959500`, fully validated sibling snapshots in one exact Bitcoin-boundary family reconcile by deterministic bounded proof-set union. Post-boundary hashrate, peer count, and first arrival do not vote for a sibling.
- A node must not rewrite an already-observed Bitcoin-block payout snapshot using proofs that first arrive after that node's local snapshot boundary. Late previous-parent proofs should be rejected or quarantined from the canonical future reserve. Proofs committing to genuinely different active payout states cannot be cross-credited.
- "Heaviest state" adoption is limited to bootstrap/proofless recovery and future explicit consensus-version changes, not ordinary same-round active snapshot replacement.
- Family isolation, paid-once lineage, deterministic ranking, and bounded member/context retention are consensus requirements. Any deeper multi-branch market remains a future version.

Deferred research is already tracked in:

- [consensus-selection-audit.md](consensus-selection-audit.md)
- [v3-branch-market-examples.md](v3-branch-market-examples.md)
- [simulation-findings-2026-06.md](simulation-findings-2026-06.md)

Short-term consensus checklist:

- [x] Document the current boundary/merge state-convergence rule in code-level detail.
- [x] Implement explicit consensus, state-bundle schema, HTTP API, peer transport, UDP relay, and release version fields.
- [x] Reject incompatible consensus/state-bundle schema versions before import.
- [x] Preserve HTTP fallback when only peer transport version differs.
- [x] Add visible UI/API/monitor visibility for version mismatch.
- [x] Add compact V2.1 state-convergence regression coverage for the two important cases: reject late previous-parent proofs and merge valid current-parent divergent proofs.
- [x] Add a delayed-snapshot regression fixture: after a local node observes a Bitcoin block and activates a snapshot, a peer bundle with extra stale-parent proofs from the previous parent must not replace the active snapshot or enter the canonical future reserve.
- [x] Add a same-active-state merge regression fixture: two nodes on the same current Bitcoin parent and compatible active payout state should merge fully valid current-parent proofs into the unpaid reserve.
- [x] Specify and test recovery from a genuine sibling active-snapshot split through V2.2 Monotonic Snapshot Reconciliation.
- [ ] Run a multi-node public beta soak and confirm current/candidate state IDs converge without manual state wipes.
- [x] Decide whether V2.1 state selection plus non-retroactive snapshot boundaries is "good enough for beta" or whether package launch waits for a coordinated V3 rule.
- [x] Coordinate V2.1 rollout: heal current public-node state first, then deploy code and config with `boot_protocol_version: 21` together on all participating nodes.
- [x] Deploy V2.2-capable binaries with height-gated activation and pre-activation V2.1 compatibility.
- [x] Verify coordinated V2.2 activation at height `959500`, schema transition
  2 to 3, and post-activation reconnection on participating public nodes.
- [ ] Complete seven post-activation days without unexplained same-tip divergence, state wipe, or manual branch selection.
- [x] Resolve the Grid Labs support-fee construction for the reference network:
  support-on only with one canonical `1/300` output; support-off and custom fee
  addresses are not interoperable reference-network dialects. Any future change
  requires a coordinated consensus/version decision.
- [ ] Model and evaluate delayed snapshot activation: build the payout snapshot at a Bitcoin boundary but make it applicable one or more Bitcoin blocks later. Determine whether the added propagation window materially reduces latency-driven snapshot disagreement, and quantify the costs in payout freshness, state retention, work attribution, reorg handling, and implementation complexity before considering a consensus change.
- [ ] Specify and regression-test GridPool behavior across ordinary one- and two-block Bitcoin reorgs. Define how active snapshots, retained contexts, unpaid Work Set proofs, paid-proof lineage, candidate/current state IDs, and any observed GridPool payment transition roll back or reconcile without double-paying or losing valid unpaid work.
- [ ] Analyze a sustained Bitcoin ruleset split, including a BIP110-driven chain split scenario. Define how GridPool network IDs, parent validation, peer discovery, state bundles, payout snapshots, and operator-visible warnings prevent proofs from incompatible Bitcoin branches from being merged silently; decide whether branch choice is strictly inherited from each node's attached Bitcoin node or needs explicit GridPool configuration/version separation.

Completion criteria:

- A packaged node rejects incompatible consensus/schema versions instead of silently importing them.
- Live V2.2 nodes reconcile fully validated sibling snapshots and converge without manual state wipes.
- A late stale-parent branch cannot pull an established node back to a different active snapshot for a Bitcoin block it already observed.
- One- and two-block Bitcoin reorg behavior is deterministic, tested, and preserves paid-once lineage and valid unpaid work.
- Nodes attached to incompatible Bitcoin consensus branches fail visibly and do not merge proofs or state bundles across the split.
- Public nodes advertise software capability 22 before activation and active consensus/schema 22/3 after height `959500`.
- No V3 or experimental fork-choice rule is required for the short-term development-mode beta.
- Any later fork-choice redesign is documented as a future consensus-version bump, not as an implicit beta behavior change.

## G2: Protocol And Release Versioning

Goal: stop relying on "everyone pull latest" before packaged installs exist.

- [x] Define separate versions for consensus rules, state bundle schema, peer transport, HTTP API, UDP relay, and node release.
- [x] Add those version fields to `/api/network/summary`.
- [x] Add those version fields to state bundles, peer share announcements, and peer session hellos.
- [x] Define compatibility behavior for each version class.
- [x] Define hard-fork style behavior for consensus/schema version changes.
- [x] Define transport fallback behavior when WebSocket/UDP versions differ but HTTP/state consensus remains compatible.
- [x] Add a visible UI warning when peers are unreachable because of version mismatch.
- [x] Add health-monitor alerts for version mismatch or missing version visibility.
- [x] Add a release-note template that explicitly labels coordinated-upgrade releases.

Completion criteria:

- A node on incompatible consensus version refuses sync and says why.
- A node on older transport version can still use canonical HTTP fallback when consensus-compatible.
- Release notes have an explicit "requires coordinated upgrade" marker when needed.

Current evidence:

- Version DTOs and compatibility evaluation live in `boot_portal/Models/BootProtocolVersions.cs` and `BootProtocolStateService`.
- `/api/network/summary`, state bundles, peer share announcements, and peer session hellos carry version fields.
- Node UI surfaces consensus/schema/API/transport/release metadata and peer compatibility.
- Health monitor flags version mismatch or missing version visibility.
- Branch/tag policy lives in [release-process.md](release-process.md), and the release-note template lives in [release-notes-template.md](release-notes-template.md).

## G3: External Beta Stability

Goal: make the current beta boring before inviting one-click installers.

- [x] Monitor at least 2 mainnet beta nodes in independent geographic and
  infrastructure fault domains.
- [ ] Complete 7 consecutive days of stable external multi-node beta runtime.
- [x] Monitor at least 1 testnet4 node for real GridPool-block trigger testing.
- [x] Track DATUM acceptance rate by node and by rejection reason.
- [x] Track peer relay success/failure by transport where exposed by node summaries.
- [x] Track state ID convergence across consensus groups.
- [x] Track Work Set count, active snapshot ID, and candidate state ID drift.
- [x] Track DATUM session churn through existing diagnostics and rejection-rate alerts.
- [x] Track real quickdiff submissions after the quickdiff reconstruction fix through local DATUM diagnostics.
- [x] Write compact monitor logs for later Codex/human review.
- [x] Add `dallas.gridpool.net` to the production monitor config once its deployed version is compatible.
- [x] Add `detroit.gridpool.net` and incident-start diagnostic capture to the production monitor.
- [ ] Put Main and Oregon on one pinned, provenanced commit and start the
  canonical seven-day protocol clock once both are healthy, V2.2-aligned, and
  converged, with the same consensus-adjacent safety configuration including
  `enable_peer_tip_stale_protection`. Dallas may participate in explicit
  `external-fallback` mode.
  Evomining and Detroit contribute opportunistic evidence when reachable but
  are not required soak anchors.
- [ ] Record operator experiments and planned restarts so they are not misclassified as protocol failures.
- [x] Add at least one remote StratumRace vantage with an attached Bitcoin node
  and verified clock health. Oregon is the controlled remote vantage.
- [ ] Generate a multi-vantage StratumRace report with sample counts, median/p95 timing, missing-event rates, and topology caveats.
- [ ] Run an overlapping package canary on the Detroit Umbrel and local DC
  Start9. Package-only changes reset the package canary; they reset the
  protocol soak only if shared runtime behavior or state compatibility changes.

Completion criteria:

- No unexplained payout mismatch bursts for 7 days.
- No unexplained state divergence lasting more than 10 minutes.
- Native SV2 job delivery, slot-0 attribution, accepted-share flow, durable
  retry, restart recovery, and local block submission are exercised without
  unexplained failure.
- Any experimental DATUM/SV1/CKPool observations are labeled by exact adapter
  and version and are not treated as launch support guarantees.
- External tester can upgrade from a previous beta release without wiping state.
- No Stratum V1 firmware or rental provider is recommended publicly without an
  exact full-coinbase test result; lack of matrix coverage does not block an
  SV2-only package launch.

Current evidence:

- `scripts/gridpool-health-monitor.mjs` compares `mainnet-beta` nodes separately from `testnet4-beta`.
- Live config monitors `main.gridpool.net`, `oregon.gridpool.net`,
  `test.gridpool.net`, `evomining.farted.net`, `dallas.gridpool.net`, and
  `detroit.gridpool.net`.
- DATUM TCP endpoint checks cover `datum.main.gridpool.net:3008`, `datum.test.gridpool.net:3009`, and `datum.dallas.gridpool.net:3008`.
- Monitor logs are written to `~/.local/state/gridpool-monitor/latest-summary.json`, `latest-consensus.json`, incident bundles, and dated JSONL files.
- Main and Oregon are the required mainnet soak pair. Evomining, Dallas, and
  Detroit remain in the same comparison group when reachable. The monitor
  distinguishes different-height propagation lag from same-boundary state
  divergence.
- StratumRace records Main and Oregon as controlled vantages, including local
  mining endpoints and GridPool chain-tip events. Cross-vantage clock quality
  and sample completeness remain part of the final report.

## G4: Networking And NAT Readiness

Goal: home miners should not need to understand router internals to participate safely.

Short-term beta posture:

- Public nodes with manually configured DNS and ports are acceptable for early beta.
- Private/Umbrel/Start9 nodes may participate as outbound-only peers as long as this is visible, safe, and well documented.
- The launch-critical goal is not perfect peer-to-peer censorship resistance on day one; it is a low-friction setup where users can mine, verify state, and relay shares without router knowledge.
- This is sufficient for the near-term value proposition of lower fees, sovereign template construction, and open participation, provided there are several independent public nodes.

Long-term censorship-resistance posture:

- Public seed nodes should become bootstrap/rendezvous helpers, not central relay dependencies.
- Outbound-only home nodes should eventually attempt automatic reachability and direct encrypted peer paths.
- UDP hole punching, Tor/I2P transports, and more diverse peer discovery should be evaluated as censorship-resistance upgrades even if short-term latency data looks acceptable.
- Route-dependency metrics should make centralization visible: direct public peers, outbound-only session peers, UDP-direct peers, and relay-fallback paths should be counted separately.

- [x] Make outbound-only peers visible in UI/API as live sessions instead of fake dialable endpoints.
- [x] Relay accepted shares to live outbound-only WebSocket sessions directly connected to a public node.
- [x] Add encrypted V2 session state-bundle sync so outbound-only peers can serve stronger current/candidate state without public HTTP reachability.
- [x] Add optional peer-only listener port so publicly mapped home nodes do not expose the UI/admin surface by default.
- [ ] Validate outbound-only state-bundle sync across at least two real nodes after deployment.
- [x] Add initial seed-assisted reachability self-test for peer HTTP routes and peer-session route visibility.
- [x] Add UDP reachability challenge/ack for router/NAT diagnostics.
- [x] Add admin-triggered PCP/NAT-PMP port mapping for the peer-only TCP port and UDP relay port.
- [ ] Validate PCP/NAT-PMP mapping against at least one real home router and one expected-failure/CGNAT case.
- [ ] Research optional UPnP IGD port mapping after PCP/NAT-PMP testing.
- [x] Add measurement-only full-header relay telemetry over encrypted V2 sessions and compact encrypted UDP, paired against receiver-local Bitcoin rawblock ZMQ arrival.
- [ ] Run chain-tip latency reports during the 7-day soak and compare against local Bitcoin tip detection.
- [x] Track Bitcoin ZMQ topic sequence numbers instead of discarding the third message frame. Expose per-topic last sequence, gaps, duplicates, reconnects, and reset/wrap handling in API/monitor telemetry so delayed or missing notifications can be distinguished from a lagging Bitcoin node.
- [x] Choose ZMQ plus authenticated five-second RPC reconciliation as the attached-node baseline. RPC is authoritative; ZMQ loss degrades latency without changing correctness.
- [x] Implement opt-in peer-header stale-work protection: validate PoW/parent/freshness/expected mainnet target, freeze a provisional Work Set boundary, pause fresh work after a grace period, quarantine late old-parent proofs, and require local full-node confirmation before activation.
- [x] Serialize Bitcoin ZMQ notifications and deduplicate paired `hashblock`/`rawblock` delivery so one block cannot create duplicate snapshots or phantom heights.
- [x] Add an immediate authenticated RPC reconciliation request when a verified peer header indicates `local-bitcoin-lagging`.
- [ ] Validate the attached-node coordinator on Main and Oregon for at least
  three blocks each on the exact soak commit. Add Evomining and Detroit samples
  when those operators restore stable service.
- [ ] Keep Dallas in explicit `external-fallback` mode and confirm it emits no missing-local-ZMQ warning.
- [ ] Pass a 24-hour no-intervention Main/Oregon canary on one provenanced
  commit, then reset and begin the seven-day protocol soak.
- [ ] Coordinate enabling `enable_peer_tip_stale_protection` on every active mainnet-beta node; mixed boundary behavior can produce a short-lived snapshot split.
- [ ] Design any future peer-header snapshot activation as a post-V2.2 consensus
  change with deterministic vectors for competing headers, reorganizations,
  withheld block bodies, rollback, missed headers, retarget boundaries, and the
  exact old-parent cutoff. V2.2 MSR does not activate from a peer header.
- [ ] Evaluate optional 1-3 second header-only empty-block mining as a separate, disabled-by-default experiment. Account for invalid-parent risk, lost fees, the 2015 BIP66/SPV-mining failure, and whether FIBRE makes the feature unnecessary.
- [ ] Add advanced, disabled-by-default optimistic peer-header mining for GridPool-controlled SV2/direct-template adapters.
- [ ] Decide whether UDP hole punching is necessary for beta performance after 7-day latency data review.
- [ ] Separately decide the censorship-resistance roadmap priority for UDP hole punching even if beta performance is acceptable.
- [ ] Add NAT traversal status fields: none/manual/pcp/nat-pmp/upnp/failed, observed external UDP endpoint, and mapping stability.
- [ ] Add clear docs for direct public peer, outbound-only-safe peer, and relay-fallback peer modes.
- [x] Ensure hidden peers are never advertised as dialable endpoints.
- [ ] Add metrics for relay dependency: number of peers reached directly, by outbound-only session sync, and through any relay fallback.
- [ ] Keep seed-mediated relay as fallback only; do not treat it as the preferred launch topology.
- [ ] Document Tor/I2P as future optional privacy/reachability transports for censorship-resistant mode.

Completion criteria:

- A fresh home node behind NAT appears in the peer list as outbound-only within 30 seconds.
- That node receives shares from public peers without manual port forwarding.
- A public node can fetch and validate current/candidate state bundles from an outbound-only peer over the encrypted V2 session.
- If `peer_listener_port` is configured and mapped, only peer protocol endpoints are exposed on that listener.
- If automatic port mapping succeeds, the node verifies its own public reachability.
- If automatic port mapping fails, the UI says the node is still participating outbound-only.
- `node scripts/peer-reachability-test.mjs <seed> <target> [udp-port]` returns peer HTTP/session reachability and UDP challenge/ack status.
- `GRIDPOOL_ADMIN_KEY=... node scripts/peer-port-map.mjs --url <private-node-url> --tcp-port <peer-port> --udp-port <udp-port>` returns per-gateway PCP/NAT-PMP mapping results without exposing the UI/admin surface publicly.
- `node scripts/chain-tip-latency-report.mjs --url <node> --window 7d` summarizes peer chain-tip relay latency.
- A 7-day soak report includes share propagation latency, chain-tip observation latency, direct-vs-session reachability, and UDP first-arrival rates.

Current evidence:

- Implementation status is tracked in [robust-networking-architecture-plan.md](robust-networking-architecture-plan.md).
- Hidden session accounting and direct live WebSocket share relay are implemented.
- Encrypted V2 session bundle sync and optional peer-only listener support are implemented but need real-node validation.
- Reachability self-test, UDP diagnostics, chain-tip latency instrumentation, and admin-triggered PCP/NAT-PMP mapping are implemented.
- The attached-node coordinator now records ZMQ sequences and publisher
  configuration, polls authenticated RPC, recovers missed blocks in height
  order, and requests immediate reconciliation after a verified peer lead.
- Installer and runtime contract implementation is complete. Oregon now
  provides a controlled remote attached-node fault domain; it must be rebuilt
  with commit provenance and pinned to the same release as Main before the
  three-block verification and canary begin.
- Peer-relayed headers remain non-final: stale-work protection may freeze
  provisionally and pause stale work, but only the locally attached Bitcoin node
  can activate a snapshot. Peer headers do not authorize payment transitions or
  mining on an unvalidated parent.
- Real-router PCP/NAT-PMP validation, UPnP decision, direct-vs-session relay dependency metrics, and the 7-day latency report remain open.
- Current production-like topology is acceptable for public beta if enough independent public nodes stay reachable, but it is not the final censorship-resistance topology.

## G5: Packaging And Installer Readiness

Goal: make installation boring and reversible.

- [x] Decide package architecture for Umbrel: a thin platform wrapper pins the
  reference-node image, persists `/data`, uses the Umbrel Bitcoin dependency
  for RPC/ZMQ, keeps the UI private behind the app proxy, and defaults to safe
  outbound-only peer participation.
- [x] Decide package architecture for StartOS: a thin platform wrapper pins the
  same reference-node release, consumes dependency-provided Bitcoin RPC/ZMQ,
  persists identity/state separately from the image, and exposes private
  operator actions for backup, restore, and diagnostics.
- [x] Keep Umbrel and StartOS package definitions outside this runtime repo so
  platform manifests, review history, and release cadence do not clutter or
  implicitly version the consensus implementation. Each wrapper must pin an
  immutable reference-node image digest.
- [x] Create the Umbrel package wrapper in `gridpool-umbrel`; sideload and soak
  it on Detroit remains open.
- [x] Create the StartOS package wrapper in `gridpool-startos`; sideload and
  soak it on the local DC node remains open.
- [x] Add a standard Bitcoin JSON-RPC template provider to the SRI-derived
  native SV2 pool. Core 31 operators may prefer IPC, while Bitcoin Knots, older
  Core, Docker, Umbrel, and StartOS use `getblocktemplate`/`submitblock` without
  bundling a second Bitcoin node.
- [x] Package native SV2 as the default and only promised miner-facing
  transport. DATUM and raw SV1 remain disabled in both appliance wrappers.
- [x] Provide Docker image tags for stable beta releases.
- [x] Provide sample config for mainnet beta Docker/manual installs.
- [x] Provide separate sample config for testnet4 beta installs.
- [x] Provide safe default ports for UI, peer HTTP/WebSocket, UDP relay, and the
  optional experimental DATUM listener.
- [ ] Provide migration scripts for state files.
- [x] Provide backup and restore docs for node identity keys and pool state.
- [x] Test the Umbrel wrapper's fresh-install and UI-driven payout flow in the
  isolated Umbrel-dev VM/container environment.
- [ ] Test fresh install on Raspberry Pi 5 or equivalent ARM64 host.
- [x] Test StartOS upgrade from `0.1.0:13` through `0.1.0:17` without wiping
  state; the candidate harness retains node, payout, token, and SV2 authority
  fingerprints for the final release run.
- [ ] Test uninstall leaves keys/state backed up or clearly prompts before deletion.
- [ ] Run the Umbrel and StartOS package builds for seven days overlapping the
  protocol soak, including at least one planned restart and one upgrade.

Completion criteria:

- Fresh install reaches the mainnet beta seed and syncs state.
- Fresh install accepts a payout address, uses the platform Bitcoin node, keeps
  private diagnostics behind platform authentication, and clearly reports
  outbound-only versus publicly reachable peer status.
- A mining-enabled package shows correct native SV2 connection information and
  fails visibly if its Bitcoin template-provider dependency is unavailable.
- Upgrade preserves node identity and state.
- Package logs are visible in the platform UI or documented shell path.

Current evidence:

- A private acceptance orchestrator now provides StartOS, Umbrel-dev, regtest,
  and operator-assisted Umbrel backends with smoke, lifecycle, destructive, and
  release profiles plus sanitized JSON/Markdown evidence.
- StartOS `0.1.0:19` upgraded from `0.1.0:13`; a package-generation bug that
  replaced node identity keys was found and fixed. Identity then survived a
  package restart and full StartOS reboot. The operator explicitly replaced
  the earlier RDTS Knots dependency with synchronized main-chain Core 31. RPC,
  both ZMQ topics, native SV2, per-channel slot-0 attribution, named Bitaxe
  mining, interfaces, and restart persistence now pass. Encrypted
  uninstall/restore remains blocked until the operator supplies the existing
  server backup password.
- Umbrel-dev `0.1.0-beta.7` upgraded through Umbrel's package RPC with state
  persistence and correct private-port exposure. A copied-data recovery
  preserved node identity, payout, adapter-token, and SV2-authority
  fingerprints. Its disposable Bitcoin dependency remains in IBD, so the
  synchronized-SV2 gate cannot pass yet.
- Candidate workflows build both package architectures, produce checksums,
  SPDX SBOMs and provenance, and verify digest-locked image inputs. A real
  ARM64 install remains a separate gate from a successful ARM64 build.

- Docker sample config exists at `docker/boot_portal_config.sample.json`.
- Testnet4 sample config exists at `docker/boot_portal_config.testnet4.sample.json`.
- GitHub Actions publishes branch, tag, SHA, and `latest` images to GHCR; `develop` is available for staging once that branch exists.
- Main documented node defaults are `5000` WebUI/API and `5001/udp` peer fast
  relay. Port `3008` remains available for experimental DATUM deployments but
  is not part of the initial appliance support promise.
- Raspberry Pi/full-stack installer docs and both appliance wrapper sources now
  exist; neither appliance package is release-ready until its sideload canary
  and upgrade/backup tests pass.
- `gridpool-sv2-pool` RPC mode passed unit tests, the wider SRI miner-workspace
  compile, and a live Core 31 mainnet template/tip-transition smoke test.
- The StartOS package passes TypeScript checking, bundling, x86_64/aarch64
  packing, upgrade, restart, and reboot checks. Final digest-pinned artifacts
  must be rebuilt after security integration.
- The Umbrel wrapper passes structural/template checks, uses an in-app one-time
  payout-address setup flow, and passed a copied-data recovery in Umbrel-dev. A
  real-operator candidate run remains open.

## G5.5: Miner Firmware, Rental, And Stratum V2 Compatibility

Goal: launch with one narrow miner path that avoids the 300-output coinbase
limit, while preserving honest experimental data about other transports.

Launch support decision:

- Native SV2 firmware connected to `gridpool-sv2-pool` is the only explicitly
  supported production miner path for the initial appliance beta.
- Stratum V1 firmware and hashrate rentals are untested and not guaranteed.
  The compatibility matrix remains a community research project, not a launch
  gate and not a claim of broad support.
- DATUM support is deprecated for the initial appliance beta. The server and
  lab tooling remain available for research, but DATUM is not enabled or
  advertised by default until upstream offers deterministic forced coinbase
  selection and the complete path passes sustained testing.
- GridPool consensus still requires the full payout set. No adapter may
  truncate, reorder, or silently replace outputs to accommodate firmware.

- [x] Build a repeatable community firmware compatibility matrix shell for the 300-slot beta team.
- [ ] Continue community testing of uncondensed 300-output coinbases, but do
  not block the native-SV2 launch on matrix breadth.
- [ ] Test specific SV1 firmware and rental paths before recommending that
  exact version/provider; all untested rows remain explicitly unsupported.
- [x] Publish a public compatibility table with `works`, `fails`, `untested`, `suspected works`, `suspected fails`, and `requires alternate firmware` states.
- [ ] Add a UI/API warning when firmware truncation rejects are observed
  repeatedly from an explicitly enabled experimental DATUM session.
- [x] Investigate whether DATUM coinbase-size selection can be made GridPool-safe with existing config. See [datum-gridpool-coinbase-compatibility.md](datum-gridpool-coinbase-compatibility.md).
- [x] Propose or track a DATUM operating mode that can force or require a large coinbase class for GridPool-compatible templates.
- [x] Stand up the testnet full-coinbase compatibility endpoint with `coinbase_uncondensed_outputs_enabled: true`, separate state or network ID, and public `/compat` telemetry.
- [x] Expose DATUM Stratum V1 on `stratum.test.gridpool.net:3334` for first-pass firmware and rental-provider testing.
- [x] Complete the Stratum V2/GridPool integration review in [stratum-v2-gridpool-evaluation.md](stratum-v2-gridpool-evaluation.md).
- [x] Decide whether Stratum V2 standard-channel/header-only mining is the preferred long-term path for avoiding ASIC coinbase-size constraints.
- [x] Add GridPool node-side SV2 work-selection API and smoke test. See [stratum-v2-gridpool-integration-plan.md](stratum-v2-gridpool-integration-plan.md) and `GET /api/mining/sv2-work-selection`.
- [x] Prove a native SV2 path can submit accepted shares into GridPool on
  mainnet beta with a Bitaxe-class miner.
- [x] Replace the overbuilt JDC/JDS experiment with a maintained SRI Pool fork that talks directly to Bitcoin Core and the local GridPool node.
- [x] Support per-channel slot-0 attribution, a global fallback payout address, batched vardiff telemetry, pulse/reserve proofs, and durable proof retry in the fork.
- [ ] Run a sustained native-SV2 miner soak against the new `gridpool-sv2-pool` fork and verify slot-0 attribution plus block submission end to end.
- [ ] Replace temporary SV2 beta keys/config with production-managed keys before broad public advertising.
- [ ] Document public SV2 endpoint operation, monitoring, restart behavior, and upgrade process.
- [ ] Validate at least one named native-SV2 ASIC firmware/version through job
  delivery, accepted shares, restart, snapshot transition, and slot-0
  attribution; support claims apply only to tested firmware versions.
- [ ] Remove DATUM connection instructions from the default Umbrel/StartOS
  onboarding flow and label manual DATUM/SV1 docs experimental.
- [ ] Update `gridpool.net` connection guidance so native SV2 is the supported
  appliance path and DATUM/SV1/rentals are clearly experimental.

Completion criteria:

- The package and website identify native SV2 as the supported path and do not
  imply that ordinary SV1, rentals, or DATUM are guaranteed.
- A miner can check the community matrix before experimenting and distinguish
  exact tested evidence from unsupported or suspected behavior.
- A native-SV2 ASIC can mine through a packaged node without receiving the
  large coinbase transaction, while slot-0 attribution and local block
  submission remain correct.

Current evidence:

- Strict firmware truncation detection is implemented and tested.
- `coinbase_uncondensed_outputs_enabled` exists for non-production firmware/rental stress testing.
- Current support docs warn that some firmware cannot handle large coinbase templates, and the community matrix shell lives in [firmware-coinbase-compatibility-matrix.md](firmware-coinbase-compatibility-matrix.md).
- Existing DATUM `stratum.fingerprint_miners` does not solve this. Unknown miners default to a smaller Antminer-compatible coinbase class, and disabling fingerprinting makes that worse. A full 300-unique-address GridPool list likely requires DATUM's 16 KB `YUGE` class or an equivalent Stratum V2/header-only path.
- A DATUM PR now exists for `coinbase_selection_mode = "force"` plus known-incompatible client disconnects before oversized work is served.
- The first recommended lab endpoint shape is `test.gridpool.net/compat`, `datum.test.gridpool.net:3009` for DATUM gateways, and `stratum.test.gridpool.net:3334` for raw Stratum V1 ASICs, backed by testnet uncondensed output mode.
- Native SV2 is now the preferred long-term path for firmware that cannot safely parse large SV1/DATUM coinbases. The maintained implementation is the `gridpool-sv2-pool` fork: SV2 miners connect directly to the fork, which uses Bitcoin Core IPC and authenticated local GridPool APIs without JDC/JDS.
- The DATUM PR has not been adopted upstream, so current DATUM behavior cannot
  be made deterministic from stock configuration. This is the reason for
  deprecating, rather than deleting, the integration for the initial package.

## G5.6: Mining Gateway Integrations

Goal: support multiple sovereign mining stacks without adding adapter-specific
identity or accounting rules to GridPool consensus.

- [x] Publish a generic work-plan API, authenticated local alias, atomic SSE
  updates, full-proof ingress, and low-difficulty telemetry ingress.
- [x] Build an early CKPool fork plus Rust sidecar using the generic contract.
- [x] Preserve ordinary CKPool behavior for non-GridPool users and make
  GridPool mode explicit per connection.
- [x] Implement deterministic Atlas operator fee buckets through actual slot-0
  attribution rather than metadata or whole non-GridPool blocks.
- [ ] Complete a bounded CKPool/AtlasPool canary with plan refresh, exact issued
  job retention, vardiff telemetry, accepted network proofs, durable retry, and
  local Bitcoin block submission verified.
- [ ] Publish repeatable install, upgrade, monitoring, and rollback instructions
  for the CKPool fork and adapter.
- [ ] Complete a PublicPool architecture spike against current upstream before
  writing a fork: map job construction, per-user attribution, vardiff, block
  submission, and template refresh to the generic GridPool gateway contract.
- [ ] Decide whether PublicPool should use a narrow upstreamable interface,
  optional in-process module, or sidecar. Prefer the smallest design that keeps
  ordinary solo mode unchanged.
- [ ] Add regtest fixtures shared across gateway integrations for wrong network,
  stale plan, snapshot transition, exact coinbase retention, slot-0 attribution,
  and found-block local submission.

Completion criteria:

- Adapter failure cannot make a gateway silently serve stale or non-GridPool
  work while claiming GridPool mode.
- The exact coinbase issued with each job is retained for proof reconstruction.
- A found block is submitted through the gateway's local Bitcoin node before
  GridPool telemetry or proof relay is relied upon.
- New integrations reuse generic APIs and fixtures rather than adding a new
  consensus dialect.

Current evidence:

- The reference contract is documented in
  [ckpool-atlaspool-integration.md](ckpool-atlaspool-integration.md).
- `gridpool-ckpool` and `gridpool-ckpool-adapter` are separate repositories and
  remain labeled early public beta.
- PublicPool upstream is a NestJS/TypeScript Stratum server with ordinary solo
  behavior that must remain intact; no GridPool integration is implemented yet.

## G6: Repo And Project Architecture

Goal: avoid turning the reference node into a junk drawer for every future adapter.

- [x] Write a target repo map for the GridPool ecosystem.
- [x] Keep `gridpool-web` separate from node code.
- [x] Keep `gridpool-simulations` separate from node code.
- [x] Decide whether to create `gridpool-spec` for protocol docs and test vectors.
- [x] Decide whether adapters belong in this repo or separate repos.
- [x] Move obvious old soak logs and historical test artifacts into `docs/archive/` or out of the repo.
- [x] Archive stale V1-era planning docs rather than deleting useful history.
- [x] Update README to point to the current docs and hide obsolete rabbit holes.
- [x] Ensure public docs use "GridPool" language consistently.

Completion criteria:

- A new contributor can read README and know which docs are current.
- Old V1 plans are clearly marked archived.
- Generated logs/state files are not tracked.
- Spec, node, web, and simulation responsibilities are clear.

Current evidence:

- Local sibling repos exist at `../gridpool-web`, `../gridpool-simulations`, and `../gridpool-spec`.
- Target repo responsibilities are documented in [project-architecture-map.md](project-architecture-map.md).
- Adapter decision: modules that reuse this node's consensus/networking core can live in this repo for now; independently useful gateways, firmware forks, alternate pool backends, and cross-implementation fixtures should move to separate repos when their release lifecycle diverges.
- First archive pass moved completed session/debug investigations and the superseded broad launch checklist into `docs/archive/`.
- Active docs now have an index at [README.md](README.md), and the only remaining public-facing `Boot Protocol` references are explicit old-name notes.

## G7: Public Docs, Website, And UI

Goal: public-facing language must match V2.2 consensus and current operational reality.

- [x] Replace or remove the V1-era intro video.
- [x] Update website FAQ for pool hopping, payout snapshots, block withholding, firmware coinbase limits, and outbound-only nodes.
- [x] Update node UI terminology:
  `active payout snapshot`, `unpaid Work Set`, `current shared payout slots`,
  and source-aware `local mining hashrate`.
- [x] Remove or rename V1 leftovers in Nerd Mode.
- [x] Add a clear testnet/mainnet visual banner.
- [x] Add current consensus version and network ID to Nerd Mode.
- [x] Add a concise "What happens if we find a block?" explanation in README/operator-facing docs.
- [x] Keep "The pool for cypherpunks" but make the technical explanation precise.
- [ ] Add V2.2 split/reconciliation states and explanations to the node UI without implying that peer count or post-boundary hashrate elects a branch.
- [ ] Audit every displayed cadence, probability, acceptance, and payout estimate against its API source and statistical meaning.
- [ ] Design a fixture-driven full UI refresh on a staging branch; deploy it only after the V2.2 soak unless it fixes a correctness defect.
- [ ] Add public/private/operator UI disclosure modes. Public mode must not show
  raw IP literals, observed/LAN/socket addresses, miner session identities, or
  NAT diagnostics; intentionally public nodes may show advertised DNS names.
- [ ] Ensure outbound-only peers remain endpoint-free everywhere the public UI
  consumes data, including tooltips and downloadable diagnostics.

Completion criteria:

- A user can tell whether they are looking at mainnet or testnet in under 3 seconds.
- The UI does not imply that every Bitcoin block is a paid GridPool round.
- Public docs explain why raw Stratum V1 is not native unless using a gateway.
- Website and README describe V2.2 snapshot-family reconciliation before broad package launch.

Current evidence:

- README includes the DATUM block-submission safety note and V2.2 snapshot-family explanation.
- Node UI now exposes protocol/network version fields in Nerd Mode.
- Public website includes FAQ entries for pool hopping, payout snapshots, block withholding/attack resistance, firmware coinbase limits, and outbound-only nodes. Its explainer and detailed consensus language still need a V2.2 reconciliation audit.
- Node UI labels now prefer `active payout snapshot`, `unpaid Work Set`, `snapshot`, and `payment transition` in user-facing text while preserving internal IDs/events for compatibility.

## G8: Security, Abuse, And Operational Monitoring

Goal: keep failures visible and bounded without exposing node operators, miners,
or cryptographic secrets.

- [x] Keep duplicate share suppression tested.
- [x] Keep firmware coinbase truncation detection tested.
- [x] Keep DATUM quickdiff reconstruction observable through diagnostics.
- [x] Add malformed and proofless peer bundle tests.
- [ ] Add rate limits for low-difficulty peer spam.
- [x] Add per-peer rate limits for outbound state bundle fetches.
- [x] Add health-monitor checks for GridPool block found, service down, peer divergence, version mismatch, coinbase stress mode, and DATUM acceptance drops.
- [x] Add alert suppression so ordinary snapshot transitions and brief
  different-height propagation lag do not page the operator as consensus forks.
- [x] Add monitor logs and summaries for top rejection categories and node acceptance rates.
- [x] Add lab-only uncondensed coinbase output mode for firmware/rental compatibility testing.
- [x] Remove startup logging of long-term Ed25519 and X25519 private keys.
- [ ] Search all startup, DATUM, peer, UDP, adapter, and exception paths for
  secret/session-key/token/password logging and add a regression check.
- [ ] Inventory unauthenticated API/UI fields and classify each as public,
  peer-protocol, or local-operator-only.
- [ ] Redact raw IP addresses and private-node endpoints from public UI/API,
  peer gossip, telemetry exports, and incident bundles.
- [ ] Put sensitive peer/session/NAT diagnostics behind local/admin access or a
  redacted public DTO.
- [x] Enforce owner-only permissions for the config file holding node identity
  keys on Unix; token and package-specific file modes remain part of packaging review.
- [ ] Document and test identity-key backup, restore, deliberate rotation, and
  response to keys retained in third-party VPS/container logs.
- [ ] Review CORS, trusted forwarded headers, WebSocket authentication/origins,
  admin APIs, reverse-proxy assumptions, and public endpoint rate limits.
- [ ] Review Docker/Umbrel/Start9 service users, mounts, capabilities, port
  bindings, log retention, and support-bundle sanitization.
- [ ] Obtain a second-developer review of the threat inventory and high-risk
  findings before one-click package launch.
- [ ] Promote the RT-2026-041/042 security release only after independent
  regtest confirms that declared-target candidates cannot rotate payout state
  and proofless bundles cannot alter winners or paid lineage. See
  [security-release-rt-2026-041-042.md](security-release-rt-2026-041-042.md).
- [ ] Review and integrate the separate RT-2026-043 through RT-2026-049
  hardening candidate after the exact `400fc6e` critical retest is recorded.
  The candidate adds DATUM bounds, fail-closed payout and parent authority,
  SSRF/UDP protections, canonical payout validation, shallow-reorg coverage,
  locked dependencies, and package supply-chain gates.
- [ ] Design an independently verifiable paid-lineage bootstrap format before
  treating node-less `external-fallback` peers as payout-state authorities.

Completion criteria:

- Health monitor pages only on actual action-worthy events.
- Known invalid input classes are categorized, not generic "payout mismatch."
- A peer cannot force unbounded CPU or storage by sending low-value proofs.
- Normal and debug logs contain no private key, derived session key, token,
  password, or secret-bearing URL.
- A default private package reveals no public/LAN/observed IP or miner identity
  through unauthenticated UI/API responses.

Current evidence:

- Duplicate and firmware-truncation regression tests live in `boot.tests/ShareAttributionTests.cs`.
- Multi-node monitoring lives in `scripts/gridpool-health-monitor.mjs` and writes compact JSONL logs for review.
- `coinbase_uncondensed_outputs_enabled` is available for non-production firmware stress testing and rejected in production configs.
- Low-difficulty peer submissions are bounded by proof validation and the
  peer-write limiter; a dedicated middleware integration test remains open.
- Outbound state-bundle fetch abuse now has a deterministic per-peer limiter
  test. Proofless and malformed bundle rejection is covered in the state tests.
- The active audit and disclosure model are tracked in
  [security-privacy-review.md](security-privacy-review.md).

## G9: Release Decision

Before Umbrel/Start9 launch, answer yes to all:

- [x] Do we know the current consensus rule is good enough for packaged beta?
- [x] Can incompatible nodes fail safely?
- [ ] Can an outbound-only home node participate usefully?
- [ ] Can a fresh install sync without handholding?
- [ ] Can a nontechnical user recover from restart/power loss?
- [ ] Are public docs accurate enough that users will not connect unsupported firmware and blame the protocol?
- [ ] Does at least one explicitly named native-SV2 ASIC firmware/version pass
  the package soak end to end?
- [ ] Are SV1 firmware, rentals, and DATUM clearly labeled
  experimental/unsupported rather than implied to be launch-compatible?
- [ ] Has the security/privacy review closed secret logging and private-node
  disclosure issues?
- [x] Have at least two external operators run nodes successfully?

If any answer is no, keep the project in public beta and avoid one-click platform launch.
