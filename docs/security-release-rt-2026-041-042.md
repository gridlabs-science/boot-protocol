# GridPool Critical State-Validation Security Release

Status: original critical findings independently verified; follow-up candidate
required for RT-2026-076 and complete regtest transition coverage.

Integration note (2026-08-24): the exact candidate `400fc6e` is preserved at
tag `security-rt-041-042-retest-candidate` and has been merged into `develop`
through `d542af6` so feature work can proceed from the intended defensive
behavior. Independent verification on 2026-08-26 confirmed RT-2026-041 and
RT-2026-042 are closed. The same run surfaced RT-2026-076, a false rejection of
complete sibling bundles containing a proof recorded against an empty
bootstrap plan, plus regtest transition gaps. Development builds must not be
promoted to `main` until the follow-up candidate passes the remaining gates.

This release closes two critical state-transition classes found during the
pre-beta security review. It does not change the V2.2 consensus version or
state-bundle wire schema; it enforces validation rules that the protocol
already intended to require.

## Block Payment Finality

- A submitted header meeting its own compact target is now a `blockCandidate`,
  not a confirmed GridPool block.
- A candidate may be retained and relayed as ordinary proof-of-work, but it
  cannot rotate the round, consume a payout snapshot, update paid lineage, or
  replace the trusted Bitcoin tip.
- `isBlock` becomes true only after the attached Bitcoin node reports the exact
  header hash on its active chain through ZMQ or RPC reconciliation.
- Duplicate submissions cannot trigger another payment transition.
- Internal non-manual rotation calls must state the local active-chain
  confirmation precondition explicitly.

This handles both event orders: share before Bitcoin notification, and Bitcoin
notification before share.

The follow-up candidate additionally preserves a notification-first block
share when the attached node has already validated that exact block onto its
active chain. This is not a general stale-share exception: the block hash must
match the locally retained header, current tip, and trusted local tip.

## State-Bundle Validation

- Current-state and bootstrap bundles with a non-empty Winners List and no
  share proofs are rejected.
- Imported Winners Lists are reconstructed from fully validated proofs and must
  match the claimed payout list and state ID.
- A remote bundle cannot replace locally established paid-snapshot lineage.
- Bootstrap refuses remote paid history that the local node cannot establish
  independently.
- The former proofless fast-forward and context-free bootstrap implementation
  has been removed.
- Unauthenticated read requests no longer add or gossip an endpoint supplied in
  `X-Boot-Peer-Endpoint`; discovery continues through configured peers, fetched
  address books, and authenticated persistent sessions.

## Operational Consequence

An `external-fallback` node has no local Bitcoin authority and therefore cannot
independently finalize a GridPool payment. Such nodes remain useful as beta
relay and observation nodes, but must not be treated as payout-state authorities
after a GridPool block. Production sovereign nodes should use `attached-node`
mode with synchronized RPC plus ZMQ.

A future bootstrap format may restore late-join synchronization after paid
rounds by carrying independently verifiable block/payment lineage. Until then,
the implementation fails closed rather than trusting peer assertions.

## RT-2026-076 Completeness Path

Production GridPool is not expected to have an empty payout plan: mainnet
already has a populated Winners List, and an intentional restart must seed at
least one shared payout address. Empty-plan support is therefore an explicit
lab compatibility path, not a mainnet bootstrap mechanism.

- `allow_empty_snapshot_bootstrap` defaults to `false`.
- It is accepted only with `bitcoin_network: regtest` in a non-production mode.
- The context must have zero proof IDs, empty normal and fee-free Winners Lists,
  and the canonical state ID for that zero-proof plan.
- Arbitrary empty peer contexts remain invalid; production/mainnet nodes remain
  fail-closed.

The runtime also supports the regtest PoW limit, `bcrt1` addresses, and explicit
RPC chain-name matching. Do not use the historical testnet4-over-regtest shim.

## Verification Gate

Before promotion to `main` or deployment:

1. Run the full `boot.tests` suite.
2. Preserve the completed RT-2026-041 verdict and verify on the follow-up that
   the candidate share cannot change current state, round, trusted tip, Winners
   List, or paid lineage.
3. Preserve the completed RT-2026-042 verdict and verify through both HTTP
   polling and persistent peer gossip that
   verify that every node rejects the proofless bundle without state mutation.
4. Verify one legitimate attached-node GridPool block transition on regtest,
   including both share-first and notification-first event orderings.
5. Verify a valid V2.2 sibling merge still succeeds with complete proofs.
6. In the isolated regtest profile only, enable
   `allow_empty_snapshot_bootstrap`, retain a first proof tied to the canonical
   empty context, cross a boundary while a sibling is offline, and verify the
   rejoining sibling converges without deleting state.

Do not publish a stable tag until the independent retest passes.
