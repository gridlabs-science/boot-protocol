import type { DashboardDiagram, DashboardSummary } from "../types";

export const summaryFixture: DashboardSummary = {
  schemaVersion: 1,
  revision: 1,
  generatedAtUtc: "2026-07-29T12:00:00Z",
  node: {
    nodeId: "node-id",
    displayName: "Test node",
    region: "Lab",
    role: "test",
    publicEndpoint: "https://test.gridpool.net",
    networkId: "testnet4-beta",
    bitcoinNetwork: "testnet4",
    releaseVersion: "1.1.0-beta",
    consensusVersion: 22,
    protocolVersion: 22,
    httpApiVersion: 1,
    serviceStartedUtc: "2026-07-29T10:00:00Z"
  },
  health: {
    status: "ready",
    miningWorkSafe: true,
    miningWorkSafetyReason: "",
    peerCount: 4,
    peerLoopsHealthy: true,
    outboundRelayHealthy: true,
    bitcoinNotificationMode: "attached-node",
    bitcoinAuthorityClass: "locally-validated",
    bitcoinRpcReachable: true,
    bitcoinRpcSynced: true,
    bitcoinInitialBlockDownload: false,
    currentTipBlockHash: "0000000000000000000000000000000000000000000000000000000000000001",
    currentTipBlockHeight: 123,
    provisionalTipBlockHash: "",
    lastPeerPollCompletedUtc: "2026-07-29T11:59:59Z"
  },
  snapshot: {
    roundNumber: 9,
    currentStateId: "current",
    candidateStateId: "candidate",
    activeSnapshotId: "snapshot",
    activeSnapshotFamilyId: "family",
    lockedPayoutCount: 299,
    lockedProofCount: 299,
    reserveCount: 897,
    reserveLimit: 897,
    reserveFloorDifficulty: 100,
    reserveFloorDifficultyDisplay: "100",
    lastRotationUtc: "2026-07-29T11:00:00Z",
    familyMemberCount: 1,
    familyUnionProofCount: 897,
    reconciliation: {}
  },
  workRate: {
    window: "24h",
    windowSeconds: 86400,
    windowStartUtc: "2026-07-28T12:00:00Z",
    windowEndUtc: "2026-07-29T12:00:00Z",
    observationCount: 897,
    retainedOrderStatisticCount: 897,
    estimateThs: 1000,
    estimateDisplay: "1 PH/s",
    orderStatisticDifficulty: 100,
    orderStatisticDifficultyDisplay: "100",
    effectiveAdmissionFloorDifficulty: 1,
    effectiveAdmissionFloorDisplay: "1",
    relativeStandardErrorPercent: 3.34,
    confidence: "high",
    warmup: false,
    completeWindow: true,
    method: "difficulty-order-statistic",
    note: "Complete window."
  },
  pulse: {
    enabled: true,
    acceptedTotal: 12,
    acceptedInWindow: 4,
    acceptedPerMinute: 0.1,
    lastAcceptedUtc: "2026-07-29T11:59:00Z",
    lastSuccessfulOutboundRelayUtc: "2026-07-29T11:59:01Z",
    outboundRelayHealthy: true,
    targetIntervalSeconds: 60,
    relayTtl: 1,
    interpretation: "Pulse proofs are liveness."
  },
  mining: {
    nativeSv2: {
      enabled: true,
      publicHost: "node.gridpool.test",
      publicPort: 34265,
      scheme: "stratum2+noise",
      usernameGuidance: "Use a payout address or worker label."
    }
  },
  capabilities: {
    webUiEnabled: true,
    legacyUiEnabled: true,
    operatorApiAvailable: true,
    addressLookupAvailable: true,
    workRateTelemetryAvailable: true,
    pulseTelemetryAvailable: true,
    watchtowerAvailable: false,
    modules: []
  }
};

export const diagramFixture: DashboardDiagram = {
  schemaVersion: 3,
  generatedAtUtc: "2026-07-29T12:00:00Z",
  redacted: false,
  oldestSequence: 1,
  latestSequence: 8,
  slotZero: {
    verified: true,
    address: "tb1qhome",
    observedUtc: "2026-07-29T11:59:30Z",
    proofId: "proof-local"
  },
  grid: {
    hashrateThs: 1200,
    hashrateDisplay: "1.2 PH/s",
    relativeStandardErrorPercent: 6.68,
    confidence: "high"
  },
  bitcoin: {
    reachable: true,
    synced: true,
    initialBlockDownload: false,
    tipHash: "00000001",
    tipHeight: 123,
    provisionalTipHash: "",
    networkDifficulty: 129_000_000_000_000,
    networkDifficultyDisplay: "129 T",
    networkHashrateHs: 730_000_000_000_000_000_000,
    networkHashrateDisplay: "730 EH/s",
    peerCount: 2,
    inboundPeerCount: 1,
    outboundPeerCount: 1,
    peerTelemetryUtc: "2026-07-29T11:59:59Z",
    zmqHealthy: true,
    miningSafe: true,
    peers: [
      { visualId: "btc-peer-1", inbound: false, latencyMs: 32, connectionType: "outbound-full-relay" },
      { visualId: "btc-peer-2", inbound: true, latencyMs: 85, connectionType: "inbound" }
    ]
  },
  workGenerator: {
    detailAvailable: true,
    connected: true,
    id: "work-generator",
    displayName: "Native SV2",
    minerCount: 3,
    hashrateThs: 1200,
    hashrateDisplay: "1.2 PH/s",
    lastActivityUtc: "2026-07-29T11:59:30Z"
  },
  peers: [
    {
      visualId: "peer-dallas",
      displayName: "Dallas",
      nodeId: "dallas",
      endpoint: "https://dallas.gridpool.net",
      status: "connected",
      connected: true,
      latencyMs: 47,
      lastActivityUtc: "2026-07-29T11:59:59Z",
      compatibilityStatus: "compatible",
      transport: "websocket",
      stateRelation: "current",
      lastInboundUtc: "2026-07-29T11:59:59Z",
      lastOutboundUtc: "2026-07-29T11:59:58Z"
    },
    {
      visualId: "peer-evomining",
      displayName: "evomining.farted.net",
      nodeId: "evomining",
      endpoint: "",
      status: "connected",
      connected: true,
      latencyMs: 62,
      lastActivityUtc: "2026-07-29T11:59:58Z",
      compatibilityStatus: "compatible",
      transport: "websocket",
      stateRelation: "current",
      lastInboundUtc: "2026-07-29T11:59:58Z",
      lastOutboundUtc: "2026-07-29T11:59:57Z"
    }
  ],
  miners: [
    {
      visualId: "miner-1",
      address: "tb1qhome",
      username: "garage-a",
      source: "sv2",
      hashrateThs: 400,
      hashrateDisplay: "400 TH/s",
      lastShareUtc: "2026-07-29T11:59:30Z",
      lastRejectedUtc: null,
      acceptedCount: 42,
      rejectedCount: 0,
      lastRejectionCategory: "",
      lastRejectionReason: ""
    }
  ],
  snapshot: {
    currentStateId: "current",
    candidateStateId: "candidate",
    activeSnapshotId: "snapshot",
    activeSnapshotFamilyId: "family",
    lockedProofCount: 300,
    paidProofRemovalCount: 0,
    lastRotationUtc: "2026-07-29T11:58:00Z"
  },
  quality: { rejectionCategories: [] },
  workSet: Array.from({ length: 897 }, (_, index) => ({
    visualId: `proof-${index + 1}`,
    proofId: `raw-proof-${index + 1}`,
    rank: index + 1,
    address: index === 0 ? "tb1qhome" : "tb1qpeer",
    difficulty: 10_000 - index,
    difficultyDisplay: `${10_000 - index}`,
    firstSeenUtc: "2026-07-29T11:00:00Z",
    locked: index < 300,
    blockQuality: false
  }))
};
