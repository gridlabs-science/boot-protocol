using boot_portal.Services;

namespace boot_portal.Models;

public static class DashboardWindows
{
    public static readonly IReadOnlyDictionary<string, TimeSpan> Supported =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["6h"] = TimeSpan.FromHours(6),
            ["24h"] = TimeSpan.FromHours(24),
            ["7d"] = TimeSpan.FromDays(7)
        };

    public static string Normalize(string? value) =>
        value != null && Supported.ContainsKey(value) ? value.ToLowerInvariant() : "24h";
}

public sealed class DashboardSummaryDto
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public DashboardNodeDto Node { get; set; } = new();
    public DashboardHealthDto Health { get; set; } = new();
    public DashboardSnapshotDto Snapshot { get; set; } = new();
    public DashboardWorkRateEstimateDto WorkRate { get; set; } = new();
    public DashboardPulseDto Pulse { get; set; } = new();
    public DashboardMiningDto Mining { get; set; } = new();
    public DashboardCapabilitiesDto Capabilities { get; set; } = new();
}

public sealed class DashboardMiningDto
{
    public DashboardNativeSv2Dto NativeSv2 { get; set; } = new();
}

public sealed class DashboardNativeSv2Dto
{
    public bool Enabled { get; set; }
    public string PublicHost { get; set; } = string.Empty;
    public int PublicPort { get; set; } = 34265;
    public string Scheme { get; set; } = "stratum2+noise";
    public string UsernameGuidance { get; set; } =
        "Use a Bitcoin payout address for per-miner slot-0 attribution, or a worker label to use the node payout address.";
}

public sealed class DashboardNodeDto
{
    public string NodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PublicEndpoint { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    public string BitcoinNetwork { get; set; } = string.Empty;
    public string ReleaseVersion { get; set; } = string.Empty;
    public int ConsensusVersion { get; set; }
    public int ProtocolVersion { get; set; }
    public int HttpApiVersion { get; set; }
    public DateTime ServiceStartedUtc { get; set; }
}

public sealed class DashboardHealthDto
{
    public string Status { get; set; } = "unknown";
    public bool MiningWorkSafe { get; set; }
    public string MiningWorkSafetyReason { get; set; } = string.Empty;
    public int PeerCount { get; set; }
    public bool PeerLoopsHealthy { get; set; }
    public bool OutboundRelayHealthy { get; set; }
    public string BitcoinNotificationMode { get; set; } = string.Empty;
    public string BitcoinAuthorityClass { get; set; } = string.Empty;
    public bool BitcoinRpcReachable { get; set; }
    public bool BitcoinRpcSynced { get; set; }
    public bool BitcoinInitialBlockDownload { get; set; }
    public string CurrentTipBlockHash { get; set; } = string.Empty;
    public long? CurrentTipBlockHeight { get; set; }
    public string ProvisionalTipBlockHash { get; set; } = string.Empty;
    public DateTime? LastPeerPollCompletedUtc { get; set; }
}

public sealed class DashboardSnapshotDto
{
    public int RoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string ActiveSnapshotId { get; set; } = string.Empty;
    public string ActiveSnapshotFamilyId { get; set; } = string.Empty;
    public int LockedPayoutCount { get; set; }
    public int LockedProofCount { get; set; }
    public int ReserveCount { get; set; }
    public int ReserveLimit { get; set; }
    public double? ReserveFloorDifficulty { get; set; }
    public string ReserveFloorDifficultyDisplay { get; set; } = "--";
    public DateTime? LastRotationUtc { get; set; }
    public int FamilyMemberCount { get; set; }
    public int FamilyUnionProofCount { get; set; }
    public BootSnapshotReconciliationCounters Reconciliation { get; set; } = new();
}

public sealed class DashboardWorkRateEstimateDto
{
    public string Window { get; set; } = "24h";
    public int WindowSeconds { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public int ObservationCount { get; set; }
    public int RetainedOrderStatisticCount { get; set; }
    public double? EstimateThs { get; set; }
    public string EstimateDisplay { get; set; } = "--";
    public double? OrderStatisticDifficulty { get; set; }
    public string OrderStatisticDifficultyDisplay { get; set; } = "--";
    public double EffectiveAdmissionFloorDifficulty { get; set; } = 1d;
    public string EffectiveAdmissionFloorDisplay { get; set; } = "1";
    public double? RelativeStandardErrorPercent { get; set; }
    public string Confidence { get; set; } = "collecting";
    public bool Warmup { get; set; } = true;
    public bool CompleteWindow { get; set; }
    public string Method { get; set; } = "difficulty-order-statistic";
    public string Note { get; set; } = string.Empty;
}

public sealed class DashboardPulseDto
{
    public bool Enabled { get; set; }
    public long AcceptedTotal { get; set; }
    public int AcceptedInWindow { get; set; }
    public double AcceptedPerMinute { get; set; }
    public DateTime? LastAcceptedUtc { get; set; }
    public DateTime? LastSuccessfulOutboundRelayUtc { get; set; }
    public bool OutboundRelayHealthy { get; set; }
    public int TargetIntervalSeconds { get; set; }
    public int RelayTtl { get; set; }
    public string Interpretation { get; set; } =
        "Pulse proofs measure liveness and relay health; they are not blended into the team work-rate estimate.";
}

public sealed class DashboardCapabilitiesDto
{
    public bool WebUiEnabled { get; set; }
    public bool LegacyUiEnabled { get; set; }
    public bool OperatorApiAvailable { get; set; }
    public bool AddressLookupAvailable { get; set; } = true;
    public bool WorkRateTelemetryAvailable { get; set; } = true;
    public bool PulseTelemetryAvailable { get; set; } = true;
    public bool WatchtowerAvailable { get; set; }
    public List<string> Modules { get; set; } =
    [
        "status",
        "miner-connection",
        "snapshot",
        "reserve",
        "work-rate",
        "pulse",
        "address",
        "network",
        "history",
        "protocol",
        "console"
    ];
}

public sealed class DashboardHistoryDto
{
    public int SchemaVersion { get; set; } = 1;
    public string Window { get; set; } = "24h";
    public int WindowSeconds { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<DashboardHistoryPointDto> Points { get; set; } = [];
}

public sealed class DashboardHistoryPointDto
{
    public DateTime TimestampUtc { get; set; }
    public double? WorkRateThs { get; set; }
    public int WorkObservationCount { get; set; }
    public double? RelativeStandardErrorPercent { get; set; }
    public int PulseCount { get; set; }
}

public sealed class DashboardAddressDto
{
    public int SchemaVersion { get; set; } = 1;
    public string Address { get; set; } = string.Empty;
    public bool Found { get; set; }
    public int LockedSlotCount { get; set; }
    public ulong LockedValueSats { get; set; }
    public List<int> LockedPositions { get; set; } = [];
    public int ProvisionalPositionCount { get; set; }
    public List<int> ProvisionalPositions { get; set; } = [];
    public double? BestProvisionalDifficulty { get; set; }
    public string BestProvisionalDifficultyDisplay { get; set; } = "--";
    public double? ReserveFloorDifficulty { get; set; }
    public string ReserveFloorDifficultyDisplay { get; set; } = "--";
    public double? EstimatedTop300SurvivalProbability { get; set; }
    public string Interpretation { get; set; } = string.Empty;
}

public sealed class DashboardOperatorDto
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime GeneratedAtUtc { get; set; }
    public List<BootLocalMiningSourceSummaryDto> LocalMiningSources { get; set; } = [];
    public List<BootLocalDatumMinerSummaryDto> LocalMiners { get; set; } = [];
    public List<BootPeerStatus> Peers { get; set; } = [];
    public BootBitcoinNotificationDto BitcoinNotification { get; set; } = new();
    public BootDatumDiagnosticsDto DatumDiagnostics { get; set; } = new();
    public BootCoinbaserDiagnosticsSummaryDto CoinbaserDiagnostics { get; set; } = new();
    public Dictionary<string, BootPeerLoopFault> PeerLoopFaults { get; set; } = new();
}

public sealed class DashboardChangedDto
{
    public long Revision { get; set; }
    public DateTime TimestampUtc { get; set; }
    public List<string> Topics { get; set; } = [];
}

public static class DashboardDiagramEventKinds
{
    public const string ProofAdmitted = "proof-admitted";
    public const string LocalMinerActivity = "local-miner-activity";
    public const string PeerConnection = "peer-connection";
    public const string PeerHeader = "peer-header";
    public const string BoundaryValidated = "boundary-validated";
    public const string PulseAccepted = "pulse-accepted";
    public const string ProofRejected = "proof-rejected";
    public const string PeerTransport = "peer-transport";
    public const string PeerState = "peer-state";
    public const string PeerHeaderRejected = "peer-header-rejected";
    public const string SiblingMerge = "sibling-merge";
    public const string ChainReorganization = "chain-reorganization";
    public const string MiningSafety = "mining-safety";
    public const string BitcoinPeerConnection = "bitcoin-peer-connection";
}

public sealed class DashboardDiagramDto
{
    public int SchemaVersion { get; set; } = 3;
    public DateTime GeneratedAtUtc { get; set; }
    public bool Redacted { get; set; } = true;
    public long OldestSequence { get; set; }
    public long LatestSequence { get; set; }
    public DashboardDiagramSlotZeroDto SlotZero { get; set; } = new();
    public DashboardDiagramGridDto Grid { get; set; } = new();
    public DashboardDiagramBitcoinDto Bitcoin { get; set; } = new();
    public DashboardDiagramWorkGeneratorDto WorkGenerator { get; set; } = new();
    public DashboardDiagramSnapshotDto Snapshot { get; set; } = new();
    public DashboardDiagramQualityDto Quality { get; set; } = new();
    public List<DashboardDiagramPeerDto> Peers { get; set; } = [];
    public List<DashboardDiagramMinerDto> Miners { get; set; } = [];
    public List<DashboardDiagramProofDto> WorkSet { get; set; } = [];
}

public sealed class DashboardDiagramGridDto
{
    public double? HashrateThs { get; set; }
    public string HashrateDisplay { get; set; } = "--";
    public double? RelativeStandardErrorPercent { get; set; }
    public string Confidence { get; set; } = "collecting";
}

public sealed class DashboardDiagramSlotZeroDto
{
    public bool Verified { get; set; }
    public string Address { get; set; } = string.Empty;
    public DateTime? ObservedUtc { get; set; }
    public string ProofId { get; set; } = string.Empty;
}

public sealed class DashboardDiagramBitcoinDto
{
    public bool Reachable { get; set; }
    public bool Synced { get; set; }
    public bool InitialBlockDownload { get; set; }
    public string TipHash { get; set; } = string.Empty;
    public long? TipHeight { get; set; }
    public string ProvisionalTipHash { get; set; } = string.Empty;
    public double? NetworkDifficulty { get; set; }
    public string NetworkDifficultyDisplay { get; set; } = "--";
    public double? NetworkHashrateHs { get; set; }
    public string NetworkHashrateDisplay { get; set; } = "--";
    public int PeerCount { get; set; }
    public int InboundPeerCount { get; set; }
    public int OutboundPeerCount { get; set; }
    public DateTime? PeerTelemetryUtc { get; set; }
    public bool ZmqHealthy { get; set; }
    public bool MiningSafe { get; set; }
    public List<DashboardDiagramBitcoinPeerDto> Peers { get; set; } = [];
}

public sealed class DashboardDiagramBitcoinPeerDto
{
    public string VisualId { get; set; } = string.Empty;
    public bool Inbound { get; set; }
    public double? LatencyMs { get; set; }
    public string ConnectionType { get; set; } = string.Empty;
}

public sealed class DashboardDiagramSnapshotDto
{
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string ActiveSnapshotId { get; set; } = string.Empty;
    public string ActiveSnapshotFamilyId { get; set; } = string.Empty;
    public int LockedProofCount { get; set; }
    public long PaidProofRemovalCount { get; set; }
    public DateTime? LastRotationUtc { get; set; }
}

public sealed class DashboardDiagramQualityDto
{
    public List<BootReasonCountDto> RejectionCategories { get; set; } = [];
}

public sealed class DashboardDiagramWorkGeneratorDto
{
    public bool DetailAvailable { get; set; }
    public bool Connected { get; set; }
    public string Id { get; set; } = "work-generator";
    public string DisplayName { get; set; } = "Work generator";
    public int MinerCount { get; set; }
    public double? HashrateThs { get; set; }
    public string HashrateDisplay { get; set; } = "--";
    public DateTime? LastActivityUtc { get; set; }
}

public sealed class DashboardDiagramPeerDto
{
    public string VisualId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public bool Connected { get; set; }
    public double? LatencyMs { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public string CompatibilityStatus { get; set; } = "unknown";
    public string Transport { get; set; } = string.Empty;
    public string StateRelation { get; set; } = "unknown";
    public DateTime? LastInboundUtc { get; set; }
    public DateTime? LastOutboundUtc { get; set; }
}

public sealed class DashboardDiagramMinerDto
{
    public string VisualId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double? HashrateThs { get; set; }
    public string HashrateDisplay { get; set; } = "--";
    public DateTime? LastShareUtc { get; set; }
    public DateTime? LastRejectedUtc { get; set; }
    public long AcceptedCount { get; set; }
    public long RejectedCount { get; set; }
    public string LastRejectionCategory { get; set; } = string.Empty;
    public string LastRejectionReason { get; set; } = string.Empty;
}

public sealed class DashboardDiagramProofDto
{
    public string VisualId { get; set; } = string.Empty;
    public string ProofId { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Address { get; set; } = string.Empty;
    public double? Difficulty { get; set; }
    public string DifficultyDisplay { get; set; } = "--";
    public DateTime? FirstSeenUtc { get; set; }
    public bool Locked { get; set; }
    public bool BlockQuality { get; set; }
}

public sealed class DashboardDiagramEventDto
{
    public long Sequence { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SourceVisualId { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public string VisualId { get; set; } = string.Empty;
    public string ProofId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public double? Difficulty { get; set; }
    public bool BlockQuality { get; set; }
    public DateTime? ReceivedUtc { get; set; }
    public DateTime? ValidatedUtc { get; set; }
    public DateTime? MutatedUtc { get; set; }
    public int? Rank { get; set; }
    public string DisplacedVisualId { get; set; } = string.Empty;
    public string DisplacedProofId { get; set; } = string.Empty;
    public bool? Connected { get; set; }
    public double? LatencyMs { get; set; }
    public long? AcceptedShareDelta { get; set; }
    public double? HashrateThs { get; set; }
    public string BlockHash { get; set; } = string.Empty;
    public long? BlockHeight { get; set; }
    public string SnapshotId { get; set; } = string.Empty;
    public List<string> LockedVisualIds { get; set; } = [];
    public List<string> LockedProofIds { get; set; } = [];
    public string Category { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string PreviousValue { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public string BoundaryKind { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool? Safe { get; set; }
}

public sealed class DashboardDiagramEventPageDto
{
    public int SchemaVersion { get; set; } = 2;
    public DateTime GeneratedAtUtc { get; set; }
    public bool Redacted { get; set; } = true;
    public long OldestSequence { get; set; }
    public long LatestSequence { get; set; }
    public long NextSequence { get; set; }
    public bool HasMore { get; set; }
    public bool Gap { get; set; }
    public List<DashboardDiagramEventDto> Events { get; set; } = [];
}

public sealed class DashboardDiagramHistoryDto
{
    public int SchemaVersion { get; set; } = 1;
    public string Window { get; set; } = "24h";
    public DateTime GeneratedAtUtc { get; set; }
    public bool Redacted { get; set; } = true;
    public string SlotZeroAddress { get; set; } = string.Empty;
    public double? BestDifficulty { get; set; }
    public string BestDifficultyDisplay { get; set; } = "--";
    public List<DashboardDiagramProofObservationDto> Proofs { get; set; } = [];
}

public sealed class DashboardDiagramProofObservationDto
{
    public string ProofId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ProofClass { get; set; } = string.Empty;
    public double Difficulty { get; set; }
    public string DifficultyDisplay { get; set; } = "--";
    public DateTime TimestampUtc { get; set; }
    public bool EnteredWorkSet { get; set; }
    public bool BlockQuality { get; set; }
}

public sealed class DashboardDiagramStateProjection
{
    public List<DashboardDiagramProofDto> WorkSet { get; set; } = [];
    public List<string> ActiveSnapshotProofIds { get; set; } = [];
    public int LastPaidProofCount { get; set; }
}

internal sealed class DashboardTelemetryDocument
{
    public int SchemaVersion { get; set; } = 2;
    public DateTime TrackingStartedUtc { get; set; }
    public DateTime? WorkDataTruncatedThroughUtc { get; set; }
    public List<DashboardWorkObservation> WorkProofs { get; set; } = [];
    public List<DashboardFloorObservation> AdmissionFloors { get; set; } = [];
    public List<DashboardPulseObservation> Pulses { get; set; } = [];
}

internal sealed class DashboardWorkObservation
{
    public string ShareId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double Difficulty { get; set; }
    public double AdmissionFloorDifficulty { get; set; } = 1d;
    public DateTime ReceivedUtc { get; set; }
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public bool EnteredWorkSet { get; set; }
    public bool BlockQuality { get; set; }
}

internal sealed class DashboardFloorObservation
{
    public double Difficulty { get; set; } = 1d;
    public DateTime ObservedUtc { get; set; }
}

internal sealed class DashboardPulseObservation
{
    public string ShareId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public double Difficulty { get; set; }
    public bool BlockQuality { get; set; }
}
