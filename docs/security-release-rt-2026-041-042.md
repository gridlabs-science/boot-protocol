# GridPool Critical State-Validation Security Release

Status: release candidate for independent red-team verification.

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

## Verification Gate

Before promotion to `main` or deployment:

1. Run the full `boot.tests` suite.
2. Repeat RT-2026-041 against the exact release-candidate commit and verify that
   the candidate share cannot change current state, round, trusted tip, Winners
   List, or paid lineage.
3. Repeat RT-2026-042 through both HTTP polling and persistent peer gossip and
   verify that every node rejects the proofless bundle without state mutation.
4. Verify one legitimate attached-node GridPool block transition on regtest,
   including both share-first and notification-first event orderings.
5. Verify a valid V2.2 sibling merge still succeeds with complete proofs.

Do not publish a stable tag until the independent retest passes.
