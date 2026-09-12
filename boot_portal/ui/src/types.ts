export type WindowKey = "6h" | "24h" | "7d";
export type HealthState = "ready" | "degraded" | "unsafe" | "unknown";

export interface DashboardSummary {
  schemaVersion: number;
  revision: number;
  generatedAtUtc: string;
  node: {
    nodeId: string;
    displayName: string;
    region: string;
    role: string;
    publicEndpoint: string;
    networkId: string;
    bitcoinNetwork: string;
    releaseVersion: string;
    consensusVersion: number;
    protocolVersion: number;
    httpApiVersion: number;
    serviceStartedUtc: string;
  };
  health: {
    status: HealthState;
    miningWorkSafe: boolean;
    miningWorkSafetyReason: string;
    peerCount: number;
    peerLoopsHealthy: boolean;
    outboundRelayHealthy: boolean;
    bitcoinNotificationMode: string;
    bitcoinAuthorityClass: string;
    bitcoinRpcReachable: boolean;
    bitcoinRpcSynced: boolean;
    bitcoinInitialBlockDownload: boolean;
    currentTipBlockHash: string;
    currentTipBlockHeight: number | null;
    provisionalTipBlockHash: string;
    lastPeerPollCompletedUtc: string | null;
  };
  snapshot: {
    roundNumber: number;
    currentStateId: string;
    candidateStateId: string;
    activeSnapshotId: string;
    activeSnapshotFamilyId: string;
    lockedPayoutCount: number;
    lockedProofCount: number;
    reserveCount: number;
    reserveLimit: number;
    reserveFloorDifficulty: number | null;
    reserveFloorDifficultyDisplay: string;
    lastRotationUtc: string | null;
    familyMemberCount: number;
    familyUnionProofCount: number;
    reconciliation: Record<string, number>;
  };
  workRate: WorkRate;
  pulse: {
    enabled: boolean;
    acceptedTotal: number;
    acceptedInWindow: number;
    acceptedPerMinute: number;
    lastAcceptedUtc: string | null;
    lastSuccessfulOutboundRelayUtc: string | null;
    outboundRelayHealthy: boolean;
    targetIntervalSeconds: number;
    relayTtl: number;
    interpretation: string;
  };
  mining?: {
    nativeSv2: {
      enabled: boolean;
      publicHost: string;
      publicPort: number;
      scheme: string;
      usernameGuidance: string;
    };
  };
  capabilities: {
    webUiEnabled: boolean;
    legacyUiEnabled: boolean;
    operatorApiAvailable: boolean;
    addressLookupAvailable: boolean;
    workRateTelemetryAvailable: boolean;
    pulseTelemetryAvailable: boolean;
    watchtowerAvailable: boolean;
    modules: string[];
  };
}

export interface WorkRate {
  window: WindowKey;
  windowSeconds: number;
  windowStartUtc: string;
  windowEndUtc: string;
  observationCount: number;
  retainedOrderStatisticCount: number;
  estimateThs: number | null;
  estimateDisplay: string;
  orderStatisticDifficulty: number | null;
  orderStatisticDifficultyDisplay: string;
  effectiveAdmissionFloorDifficulty: number;
  effectiveAdmissionFloorDisplay: string;
  relativeStandardErrorPercent: number | null;
  confidence: "collecting" | "low" | "medium" | "high";
  warmup: boolean;
  completeWindow: boolean;
  method: string;
  note: string;
}

export interface DashboardHistory {
  schemaVersion: number;
  window: WindowKey;
  windowSeconds: number;
  generatedAtUtc: string;
  points: Array<{
    timestampUtc: string;
    workRateThs: number | null;
    workObservationCount: number;
    relativeStandardErrorPercent: number | null;
    pulseCount: number;
  }>;
}

export interface DashboardAddress {
  schemaVersion: number;
  address: string;
  found: boolean;
  lockedSlotCount: number;
  lockedValueSats: number;
  lockedPositions: number[];
  provisionalPositionCount: number;
  provisionalPositions: number[];
  bestProvisionalDifficulty: number | null;
  bestProvisionalDifficultyDisplay: string;
  reserveFloorDifficulty: number | null;
  reserveFloorDifficultyDisplay: string;
  estimatedTop300SurvivalProbability: number | null;
  interpretation: string;
}

export interface LocalMiningSource {
  source: string;
  displayName: string;
  activeMinerCount: number;
  recentAcceptedShareCount: number;
  hashrateSampleCount: number;
  currentHashrateThs: number | null;
  currentHashrateDisplay: string;
  estimationMethod: string;
  lastShareUtc: string | null;
}

export interface PeerStatus {
  endpoint: string;
  nodeId: string;
  status: string;
  transport?: string;
  lastSuccessUtc?: string | null;
  latencyMs?: number | null;
}

export interface DashboardOperator {
  schemaVersion: number;
  generatedAtUtc: string;
  localMiningSources: LocalMiningSource[];
  localMiners: unknown[];
  peers: PeerStatus[];
  bitcoinNotification: Record<string, unknown>;
  datumDiagnostics: Record<string, unknown>;
  coinbaserDiagnostics: Record<string, unknown>;
  peerLoopFaults: Record<string, unknown>;
}

export interface DashboardChanged {
  revision: number;
  timestampUtc: string;
  topics: string[];
}

export interface DiagramSlotZero {
  verified: boolean;
  address: string;
  observedUtc: string | null;
  proofId: string;
}

export interface DiagramPeer {
  visualId: string;
  displayName: string;
  nodeId: string;
  endpoint: string;
  status: string;
  connected: boolean;
  latencyMs: number | null;
  lastActivityUtc: string | null;
  compatibilityStatus: string;
  transport: string;
  stateRelation: string;
  lastInboundUtc: string | null;
  lastOutboundUtc: string | null;
}

export interface DiagramMiner {
  visualId: string;
  address: string;
  username: string;
  source: string;
  hashrateThs: number | null;
  hashrateDisplay: string;
  lastShareUtc: string | null;
  lastRejectedUtc: string | null;
  acceptedCount: number;
  rejectedCount: number;
  lastRejectionCategory: string;
  lastRejectionReason: string;
}

export interface DiagramProof {
  visualId: string;
  proofId: string;
  rank: number;
  address: string;
  difficulty: number | null;
  difficultyDisplay: string;
  firstSeenUtc: string | null;
  locked: boolean;
  blockQuality: boolean;
}

export interface DashboardDiagram {
  schemaVersion: number;
  generatedAtUtc: string;
  redacted: boolean;
  oldestSequence: number;
  latestSequence: number;
  slotZero: DiagramSlotZero;
  grid: {
    hashrateThs: number | null;
    hashrateDisplay: string;
    relativeStandardErrorPercent: number | null;
    confidence: "collecting" | "low" | "medium" | "high";
  };
  bitcoin: {
    reachable: boolean;
    synced: boolean;
    initialBlockDownload: boolean;
    tipHash: string;
    tipHeight: number | null;
    provisionalTipHash: string;
    networkDifficulty: number | null;
    networkDifficultyDisplay: string;
    networkHashrateHs: number | null;
    networkHashrateDisplay: string;
    peerCount: number;
    inboundPeerCount: number;
    outboundPeerCount: number;
    peerTelemetryUtc: string | null;
    zmqHealthy: boolean;
    miningSafe: boolean;
    peers: Array<{
      visualId: string;
      inbound: boolean;
      latencyMs: number | null;
      connectionType: string;
    }>;
  };
  workGenerator: {
    detailAvailable: boolean;
    connected: boolean;
    id: string;
    displayName: string;
    minerCount: number;
    hashrateThs: number | null;
    hashrateDisplay: string;
    lastActivityUtc: string | null;
  };
  snapshot: {
    currentStateId: string;
    candidateStateId: string;
    activeSnapshotId: string;
    activeSnapshotFamilyId: string;
    lockedProofCount: number;
    paidProofRemovalCount: number;
    lastRotationUtc: string | null;
  };
  quality: {
    rejectionCategories: Array<{ reason: string; count: number }>;
  };
  peers: DiagramPeer[];
  miners: DiagramMiner[];
  workSet: DiagramProof[];
}

export type DiagramEventKind =
  | "proof-admitted"
  | "local-miner-activity"
  | "peer-connection"
  | "peer-header"
  | "boundary-validated"
  | "pulse-accepted"
  | "proof-rejected"
  | "peer-transport"
  | "peer-state"
  | "peer-header-rejected"
  | "sibling-merge"
  | "chain-reorganization"
  | "mining-safety"
  | "bitcoin-peer-connection";

export interface DiagramEvent {
  sequence: number;
  timestampUtc: string;
  kind: DiagramEventKind;
  sourceKind: string;
  sourceId: string;
  sourceVisualId: string;
  transport: string;
  visualId: string;
  proofId: string;
  address: string;
  difficulty: number | null;
  blockQuality: boolean;
  receivedUtc: string | null;
  validatedUtc: string | null;
  mutatedUtc: string | null;
  rank: number | null;
  displacedVisualId: string;
  displacedProofId: string;
  connected: boolean | null;
  latencyMs: number | null;
  acceptedShareDelta: number | null;
  hashrateThs: number | null;
  blockHash: string;
  blockHeight: number | null;
  snapshotId: string;
  lockedVisualIds: string[];
  lockedProofIds: string[];
  category?: string;
  reason?: string;
  previousValue?: string;
  currentValue?: string;
  boundaryKind?: string;
  count?: number;
  safe?: boolean | null;
}

export interface DiagramProofObservation {
  proofId: string;
  address: string;
  sourceKind: string;
  source: string;
  username: string;
  proofClass: string;
  difficulty: number;
  difficultyDisplay: string;
  timestampUtc: string;
  enteredWorkSet: boolean;
  blockQuality: boolean;
}

export interface DiagramHistory {
  schemaVersion: number;
  window: "24h" | "7d";
  generatedAtUtc: string;
  redacted: boolean;
  slotZeroAddress: string;
  bestDifficulty: number | null;
  bestDifficultyDisplay: string;
  proofs: DiagramProofObservation[];
}

export interface DiagramEventPage {
  schemaVersion: number;
  generatedAtUtc: string;
  redacted: boolean;
  oldestSequence: number;
  latestSequence: number;
  nextSequence: number;
  hasMore: boolean;
  gap: boolean;
  events: DiagramEvent[];
}

export interface DashboardState {
  summary: DashboardSummary | null;
  history: DashboardHistory | null;
  operator: DashboardOperator | null;
  loading: boolean;
  stale: boolean;
  error: string;
  lastUpdatedUtc: string | null;
}
