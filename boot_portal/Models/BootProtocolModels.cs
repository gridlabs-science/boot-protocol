using boot_portal.Services;

namespace boot_portal.Models;

public class RecordedShareSubmission
{
    public string MinerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string HeaderHex { get; set; } = string.Empty;
    public string CoinbaseHex { get; set; } = string.Empty;
    public List<string> MerklePath { get; set; } = [];
    public string? PayoutSnapshotId { get; set; }
    public string? PrevBlockHash { get; set; }
    public double Difficulty { get; set; }
    public string Source { get; set; } = "unknown";
    public int PayloadBytes { get; set; }
    public DateTime? TransportReceivedUtc { get; set; }
    public string ProofClass { get; set; } = BootProofClasses.Work;
    public string RelayStage { get; set; } = BootRelayStages.Validated;
    public int RelayTtl { get; set; }
}

public class BootShareProof
{
    public string ShareId { get; set; } = string.Empty;
    public string MinerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ScriptPubKeyHex { get; set; } = string.Empty;
    public string HeaderHex { get; set; } = string.Empty;
    public string CoinbaseHex { get; set; } = string.Empty;
    public List<string> MerklePath { get; set; } = [];
    public string? PayoutSnapshotId { get; set; }
    public string? PrevBlockHash { get; set; }
    public double Difficulty { get; set; }
    public string DiffString { get; set; } = "0";
    public string Source { get; set; } = "unknown";
    public DateTime Timestamp { get; set; }
    public string ProofClass { get; set; } = BootProofClasses.Work;
    public string RelayStage { get; set; } = BootRelayStages.Validated;
    public int RelayTtl { get; set; }
    public DateTime? TransportReceivedUtc { get; set; }
    public DateTime? StateServiceReceivedUtc { get; set; }
    public DateTime? DifficultyCheckedUtc { get; set; }
    public DateTime? ValidationCompletedUtc { get; set; }
    public DateTime? StateMutationCompletedUtc { get; set; }
}

public static class BootProofClasses
{
    public const string Work = "work";
    public const string Pulse = "pulse";
}

public static class BootRelayStages
{
    public const string Optimistic = "optimistic";
    public const string Validated = "validated";
}

public class BootCommitmentInfo
{
    public int ProtocolVersion { get; set; }
    public string NetworkId { get; set; } = string.Empty;
    public string NextStateId { get; set; } = string.Empty;
    public bool OnChainSupported { get; set; }
    public string TagPreview { get; set; } = string.Empty;
    public string SupportNote { get; set; } = string.Empty;
}

public class BootPeerStatus
{
    public string Endpoint { get; set; } = string.Empty;
    public string Status { get; set; } = "unimplemented";
    public string Source { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string ConnectionMode { get; set; } = "unknown";
    public bool SessionConnected { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public bool IsConfiguredSeed { get; set; }
    public DateTime? DiscoveredUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastSessionUtc { get; set; }
    public double? LatencyMs { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public DateTime? SuppressedUntilUtc { get; set; }
    public DateTime? TombstonedUntilUtc { get; set; }
    public int FailureCount { get; set; }
    public int RelaySuccessCount { get; set; }
    public int RelayFailureCount { get; set; }
    public int SessionSuccessCount { get; set; }
    public int SessionFailureCount { get; set; }
    public int UdpRelaySuccessCount { get; set; }
    public int UdpRelayFailureCount { get; set; }
    public string LastCurrentStateId { get; set; } = string.Empty;
    public string LastCandidateStateId { get; set; } = string.Empty;
    public string LastTipBlockHash { get; set; } = string.Empty;
    public BootNodeVersionInfo RemoteVersion { get; set; } = new();
    public string CompatibilityStatus { get; set; } = "unknown";
    public string CompatibilityReason { get; set; } = string.Empty;
    public List<string> CompatibilityWarnings { get; set; } = [];
    public double Score { get; set; }
}

public class BootPeerAddressBookDto
{
    public string SelfEndpoint { get; set; } = string.Empty;
    public int TotalKnownPeers { get; set; }
    public int ReturnedCount { get; set; }
    public List<BootPeerAddressDto> Peers { get; set; } = [];
}

public class BootPeerAddressDto
{
    public string Endpoint { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double Score { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? LastSessionUtc { get; set; }
    public int SessionSuccessCount { get; set; }
    public int SessionFailureCount { get; set; }
    public int RelaySuccessCount { get; set; }
    public int RelayFailureCount { get; set; }
    public int UdpRelaySuccessCount { get; set; }
    public int UdpRelayFailureCount { get; set; }
    public BootNodeVersionInfo RemoteVersion { get; set; } = new();
    public string CompatibilityStatus { get; set; } = "unknown";
    public string CompatibilityReason { get; set; } = string.Empty;
}

public class BootPeerTombstoneRequest
{
    public string Endpoint { get; set; } = string.Empty;
}

public class BootReachabilityProbeRequest
{
    public string TargetBaseUrl { get; set; } = string.Empty;
    public string UdpHost { get; set; } = string.Empty;
    public int? UdpPort { get; set; }
    public bool IncludeUdpProbe { get; set; }
    public string UdpChallengeNonce { get; set; } = string.Empty;
}

public class BootReachabilityProbeResult
{
    public string TargetBaseUrl { get; set; } = string.Empty;
    public DateTime TestedAtUtc { get; set; }
    public bool HttpReachable { get; set; }
    public int? HttpStatusCode { get; set; }
    public double? HttpLatencyMs { get; set; }
    public bool NetworkSummaryReachable { get; set; }
    public int? NetworkSummaryStatusCode { get; set; }
    public double? NetworkSummaryLatencyMs { get; set; }
    public bool PeerSessionRouteReachable { get; set; }
    public int? PeerSessionRouteStatusCode { get; set; }
    public double? PeerSessionRouteLatencyMs { get; set; }
    public bool UdpProbeAttempted { get; set; }
    public bool UdpProbeSent { get; set; }
    public bool UdpChallengeAcknowledged { get; set; }
    public string UdpHost { get; set; } = string.Empty;
    public int? UdpPort { get; set; }
    public string UdpChallengeNonce { get; set; } = string.Empty;
    public string ObservedRequesterIp { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
}

public class BootUdpReachabilityAckRequest
{
    public string Nonce { get; set; } = string.Empty;
    public string TargetBaseUrl { get; set; } = string.Empty;
}

public class BootPortMappingRequest
{
    public int? PeerTcpPort { get; set; }
    public int? PeerUdpPort { get; set; }
    public int LifetimeSeconds { get; set; } = 3600;
    public List<string> Protocols { get; set; } = ["pcp", "nat-pmp"];
}

public class BootPortMappingResponse
{
    public DateTime AttemptedAtUtc { get; set; } = DateTime.UtcNow;
    public int LifetimeSeconds { get; set; }
    public int PeerTcpPort { get; set; }
    public int PeerUdpPort { get; set; }
    public int GatewayCount { get; set; }
    public bool TcpMapped => Results.Any(result => result.Success && result.Transport == "tcp");
    public bool UdpMapped => Results.Any(result => result.Success && result.Transport == "udp");
    public string Summary { get; set; } = string.Empty;
    public List<BootPortMappingResult> Results { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class BootPortMappingResult
{
    public string Protocol { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public string LocalAddress { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public int RequestedExternalPort { get; set; }
    public int? MappedExternalPort { get; set; }
    public string ExternalAddress { get; set; } = string.Empty;
    public int LifetimeSeconds { get; set; }
    public bool Success { get; set; }
    public int? ResultCode { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BootLaunchReadinessDto
{
    public bool ReadyForProductionRoundMode { get; set; }
    public bool ProductionRoundModeActive { get; set; }
    public bool OperatorProductionHardeningReady { get; set; }
    public bool MainnetPayoutsReal { get; set; }
    public string RoundTriggerMode { get; set; } = string.Empty;
    public bool TestingRoundResetEnabled { get; set; }
    public string NodeMode { get; set; } = string.Empty;
    public string StatusSummary { get; set; } = string.Empty;
    public int WarningCount => Warnings.Count;
    public int InfoCount => Info.Count;
    public List<string> Warnings { get; set; } = [];
    public List<string> Info { get; set; } = [];
}

public class BootReasonCountDto
{
    public string Reason { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class BootDatumDiagnosticsDto
{
    public int WindowSeconds { get; set; }
    public int TotalSubmissions { get; set; }
    public int AcceptedCount { get; set; }
    public int AcceptedOnDeckCount { get; set; }
    public int RejectedCount { get; set; }
    public DateTime? LastAcceptedUtc { get; set; }
    public DateTime? LastRejectedUtc { get; set; }
    public string LastRejectionReason { get; set; } = string.Empty;
    public List<BootReasonCountDto> RejectionReasons { get; set; } = [];
}

public class BootCoinbaserDiagnosticsSummaryDto
{
    public int WindowSeconds { get; set; }
    public int TotalFetches { get; set; }
    public DateTime? LastFetchUtc { get; set; }
    public double? AverageDurationMs { get; set; }
    public double? P95DurationMs { get; set; }
    public double? AverageParseDurationMs { get; set; }
    public double? AverageStateReadDurationMs { get; set; }
    public double? AverageBuildDurationMs { get; set; }
    public double? AverageSerializeDurationMs { get; set; }
    public double? AverageSendDurationMs { get; set; }
    public double? P95SendDurationMs { get; set; }
    public int TemporarySlotZeroCount { get; set; }
    public int SlowFetchCount { get; set; }
    public int SlowStateReadCount { get; set; }
    public int SlowBuildCount { get; set; }
    public int SlowSerializeCount { get; set; }
    public int SlowSendCount { get; set; }
}

public class BootLocalDatumMinerSummaryDto
{
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public long TotalAcceptedShareCount { get; set; }
    public int RecentAcceptedShareCount { get; set; }
    public int HashrateSampleCount { get; set; }
    public int CurrentRoundAcceptedShareCount { get; set; }
    public double? CurrentHashrateThs { get; set; }
    public string CurrentHashrateDisplay { get; set; } = "--";
    public double CurrentRoundBestDifficulty { get; set; }
    public string CurrentRoundBestDifficultyDisplay { get; set; } = "0";
    public DateTime? LastShareUtc { get; set; }
}

public class BootLocalMiningSourceSummaryDto
{
    public string Source { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int ActiveMinerCount { get; set; }
    public long RecentAcceptedShareCount { get; set; }
    public int HashrateSampleCount { get; set; }
    public double? CurrentHashrateThs { get; set; }
    public string CurrentHashrateDisplay { get; set; } = "--";
    public string EstimationMethod { get; set; } = "insufficient-data";
    public DateTime? LastShareUtc { get; set; }
}

public class BootLocalDatumMinerSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalTrackedMiners { get; set; }
    public int ReturnedCount { get; set; }
    public List<BootLocalDatumMinerSummaryDto> Miners { get; set; } = [];
    public List<BootLocalDatumMinerHashratePointDto> Points { get; set; } = [];
}

public class BootLocalDatumMinerHashratePointDto
{
    public DateTime TimestampUtc { get; set; }
    public double? HashrateThs { get; set; }
    public string HashrateDisplay { get; set; } = "--";
    public int SampleCount { get; set; }
}

public class BootLocalDatumMinerHashrateRollupPoint
{
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public int CurrentRoundNumber { get; set; }
    public double? HashrateThs { get; set; }
    public string HashrateDisplay { get; set; } = "--";
    public int SampleCount { get; set; }
}

public class BootNetworkStatusDto
{
    public string NodeId { get; set; } = string.Empty;
    public bool IdentityChanged { get; set; }
    public string SelfEndpoint { get; set; } = string.Empty;
    public string DatumPublicHost { get; set; } = string.Empty;
    public int DatumPublicPort { get; set; }
    public int DatumListenPort { get; set; }
    public List<string> ConfigWarnings { get; set; } = [];
    public DateTime ServiceStartedUtc { get; set; }
    public int ActiveDatumSessionCount { get; set; }
    public DateTime? LastDatumSessionOpenedUtc { get; set; }
    public DateTime? LastDatumHelloReceivedUtc { get; set; }
    public DateTime? LastDatumCoinbaserRequestUtc { get; set; }
    public DateTime? LastPeerPollCompletedUtc { get; set; }
    public DateTime? LastShareRelayDequeuedUtc { get; set; }
    public DateTime? LastShareRelayQueuedUtc { get; set; }
    public DateTime? LastSuccessfulOutboundRelayUtc { get; set; }
    public DateTime? LastUdpShareRelayUtc { get; set; }
    public DateTime? LastWebSocketShareRelayUtc { get; set; }
    public DateTime? LastHttpShareRelayUtc { get; set; }
    public DateTime? LastChainTipRelayUtc { get; set; }
    public DateTime? LastValidLocalDatumShareUtc { get; set; }
    public DateTime? LastSuccessfulDatumCoinbaserResponseUtc { get; set; }
    public DateTime? LastDatumSessionClosedUtc { get; set; }
    public string LastDatumSessionCloseReason { get; set; } = string.Empty;
    public int ShareRelayQueueDepth { get; set; }
    public Dictionary<string, BootPeerLoopFault> PeerLoopFaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PeerLoopsHealthy { get; set; } = true;
    public bool OutboundRelayHealthy { get; set; } = true;
    public string OutboundRelayHealthReason { get; set; } = string.Empty;
    public DateTime? LastLocalPulseUtc { get; set; }
    public long LocalPulseAcceptedCount { get; set; }
    public double LocalPulseAcceptRatePerMinute { get; set; }
    public int SoftwareConsensusVersion { get; set; }
    public int ProtocolVersion { get; set; }
    public int ConsensusVersion { get; set; }
    public long V22ActivationBlockHeight { get; set; }
    public long? V22ActivationTipBlockHeight { get; set; }
    public long? BlocksToV22Activation { get; set; }
    public int StateBundleSchemaVersion { get; set; }
    public int HttpApiVersion { get; set; }
    public int PeerTransportVersion { get; set; }
    public int UdpRelayVersion { get; set; }
    public bool EnablePeerPersistentSessions { get; set; }
    public bool EnablePeerUdpFastRelay { get; set; }
    public string PeerUdpPublicHost { get; set; } = string.Empty;
    public int PeerUdpPort { get; set; }
    public int PeerUdpMaxDatagramBytes { get; set; }
    public bool PeerRelayLatencyProbeAllTransports { get; set; }
    public bool PulseProofsEnabled { get; set; }
    public double MinimumPulseDifficulty { get; set; }
    public int PulseTargetIntervalSeconds { get; set; }
    public int PulseRelayTtl { get; set; }
    public bool OptimisticShareRelayEnabled { get; set; }
    public double MinimumOptimisticRelayDifficulty { get; set; }
    public bool PublicTelemetryOptIn { get; set; }
    public string PublicNodeDisplayName { get; set; } = string.Empty;
    public string PublicNodeRegion { get; set; } = string.Empty;
    public string PublicNodeRole { get; set; } = string.Empty;
    public double? PublicNodeApproxLatitude { get; set; }
    public double? PublicNodeApproxLongitude { get; set; }
    public string ReleaseVersion { get; set; } = string.Empty;
    public BootNodeVersionInfo VersionInfo { get; set; } = new();
    public string NetworkId { get; set; } = string.Empty;
    public string BitcoinNetwork { get; set; } = "mainnet";
    public int CurrentRoundNumber { get; set; }
    public int SharedWinnerSlotCount { get; set; }
    public int TotalPayoutSlotCount { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string ActiveSnapshotId { get; set; } = string.Empty;
    public string LastPaidSnapshotId { get; set; } = string.Empty;
    public int ActiveSnapshotProofCount { get; set; }
    public int WorkSetCount { get; set; }
    public int WorkSetReserveLimit { get; set; }
    public bool SupportFeeEnabled { get; set; }
    public string PayoutVariant { get; set; } = string.Empty;
    public string CoinbaseOutputMode { get; set; } = "condensed";
    public int CoinbaseOutputCount { get; set; }
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public uint? CurrentTipCompactTarget { get; set; }
    public bool PeerTipStaleProtectionEnabled { get; set; }
    public bool MiningWorkSafe { get; set; } = true;
    public bool LocalBitcoinLagging { get; set; }
    public string MiningWorkSafetyReason { get; set; } = string.Empty;
    public BootBitcoinNotificationDto BitcoinNotification { get; set; } = new();
    public string? ProvisionalTipBlockHash { get; set; }
    public string? ProvisionalTipParentBlockHash { get; set; }
    public string? ProvisionalSnapshotId { get; set; }
    public int ProvisionalSnapshotProofCount { get; set; }
    public DateTime? ProvisionalTipObservedUtc { get; set; }
    public DateTime? ProvisionalTipGraceDeadlineUtc { get; set; }
    public bool ProvisionalExpectedDifficultyValidated { get; set; }
    public DateTime? LastRotationUtc { get; set; }
    public int WinnersCount { get; set; }
    public int CurrentStateProofCount { get; set; }
    public double CurrentStateTotalDifficulty { get; set; }
    public int OnDeckCount { get; set; }
    public double OnDeckTotalDifficulty { get; set; }
    public long? CurrentRoundElapsedSeconds { get; set; }
    public double? CurrentRoundObservedHashrateThs { get; set; }
    public string CurrentRoundObservedHashrateDisplay { get; set; } = "--";
    public double? LocalDatumHashrateThs { get; set; }
    public string LocalDatumHashrateDisplay { get; set; } = "--";
    public double? LocalMiningHashrateThs { get; set; }
    public string LocalMiningHashrateDisplay { get; set; } = "--";
    public int LocalMiningSourceCount { get; set; }
    public List<BootLocalMiningSourceSummaryDto> LocalMiningSources { get; set; } = [];
    public int PeerCount { get; set; }
    public bool AdminApiEnabled { get; set; }
    public bool TestingRoundResetEnabled { get; set; }
    public string RoundTriggerMode { get; set; } = "gridpool-block-found";
    public string TestingRoundResetMode { get; set; } = "none";
    public int TestingRoundResetLowNibbleThreshold { get; set; }
    public string TestingRoundResetDescription { get; set; } = string.Empty;
    public string? LastTestingTriggerBlockHash { get; set; }
    public long? LastTestingTriggerBlockHeight { get; set; }
    public string? LastGridPoolBlockHash { get; set; }
    public long? LastGridPoolBlockHeight { get; set; }
    public DateTime? LastGridPoolBlockUtc { get; set; }
    public string? LastGridPoolBlockMinerAddress { get; set; }
    public double? LastGridPoolBlockDifficulty { get; set; }
    public BootLaunchReadinessDto LaunchReadiness { get; set; } = new();
    public BootDatumDiagnosticsDto LocalDatumDiagnostics { get; set; } = new();
    public int LocalDatumMinerCount { get; set; }
    public List<BootLocalDatumMinerSummaryDto> LocalDatumMiners { get; set; } = [];
    public BootCoinbaserDiagnosticsSummaryDto CoinbaserDiagnostics { get; set; } = new();
    public List<BootPeerStatus> Peers { get; set; } = [];
    public BootCommitmentInfo Commitment { get; set; } = new();
    public string ActiveSnapshotFamilyId { get; set; } = string.Empty;
    public int SnapshotFamilyMemberCount { get; set; }
    public int SnapshotFamilyUnionProofCount { get; set; }
    public BootSnapshotReconciliationCounters ReconciliationCounters { get; set; } = new();
}

public class BootAcceptedShareTelemetry
{
    public string MinerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double Difficulty { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class BootShareDiagnosticTelemetry
{
    public string Source { get; set; } = string.Empty;
    public string MinerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool AffectedOnDeck { get; set; }
    public string? RejectionReason { get; set; }
    public string? RejectionCategory { get; set; }
    public double Difficulty { get; set; }
    public int CurrentRoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class BootPeerRelayObservation
{
    public string ShareId { get; set; } = string.Empty;
    public string ProofClass { get; set; } = BootProofClasses.Work;
    public string RelayStage { get; set; } = BootRelayStages.Validated;
    public string Transport { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string MinerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public double Difficulty { get; set; }
    public bool Accepted { get; set; }
    public bool AffectedOnDeck { get; set; }
    public string? RejectionReason { get; set; }
    public bool IsFirstArrival { get; set; }
    public string FirstTransport { get; set; } = string.Empty;
    public double DeltaFromFirstMs { get; set; }
    public int PayloadBytes { get; set; }
    public double ValidationDurationMs { get; set; }
    public DateTime? TransportReceivedUtc { get; set; }
    public DateTime? StateServiceReceivedUtc { get; set; }
    public DateTime? DifficultyCheckedUtc { get; set; }
    public DateTime? ValidationCompletedUtc { get; set; }
    public DateTime? StateMutationCompletedUtc { get; set; }
    public double? TransportToStateServiceMs { get; set; }
    public double? StateServiceToDifficultyMs { get; set; }
    public double? DifficultyToValidationMs { get; set; }
    public double? ValidationToMutationMs { get; set; }
    public int CurrentRoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class BootPeerRelayTransportSummaryDto
{
    public string Transport { get; set; } = string.Empty;
    public string ProofClass { get; set; } = string.Empty;
    public string RelayStage { get; set; } = string.Empty;
    public int ArrivalCount { get; set; }
    public int FirstArrivalCount { get; set; }
    public int AcceptedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int RejectedCount { get; set; }
    public double? AverageDeltaFromFirstMs { get; set; }
    public double? MedianDeltaFromFirstMs { get; set; }
    public double? P95DeltaFromFirstMs { get; set; }
    public double? AveragePayloadBytes { get; set; }
    public int? MinPayloadBytes { get; set; }
    public int? MaxPayloadBytes { get; set; }
}

public class BootCoinbaserFetchTelemetry
{
    public string Source { get; set; } = "datum";
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string ClientIdentityPreview { get; set; } = string.Empty;
    public long RequestSequence { get; set; }
    public ulong RewardValue { get; set; }
    public ulong TeamPayoutTotal { get; set; }
    public ulong SlotZeroValue { get; set; }
    public string SlotZeroAddress { get; set; } = string.Empty;
    public bool UsingTemporarySlotZero { get; set; }
    public int WinnersCount { get; set; }
    public int CoinbaseOutputCount { get; set; }
    public int ResponsePayloadBytes { get; set; }
    public double DurationMs { get; set; }
    public double ParseDurationMs { get; set; }
    public double StateReadDurationMs { get; set; }
    public double BuildDurationMs { get; set; }
    public double SerializeDurationMs { get; set; }
    public double SendDurationMs { get; set; }
    public int CurrentRoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class BootDatumShareResponseTelemetry
{
    public string SessionId { get; set; } = string.Empty;
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string MinerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public bool AffectedOnDeck { get; set; }
    public string? RejectionReason { get; set; }
    public double Difficulty { get; set; }
    public string? PrevBlockHash { get; set; }
    public byte JobId { get; set; }
    public byte CoinbaseId { get; set; }
    public byte? CoinbaserId { get; set; }
    public string? PayoutSnapshotId { get; set; }
    public uint Nonce { get; set; }
    public bool IsBlock { get; set; }
    public bool SubsidyOnly { get; set; }
    public bool QuickDiff { get; set; }
    public bool NonceOnlySubmit { get; set; }
    public bool UsedCachedJob { get; set; }
    public double? CachedJobAgeMs { get; set; }
    public byte TargetByte { get; set; }
    public ushort? TargetByteIndex { get; set; }
    public int PayloadBytes { get; set; }
    public int CoinbaseBytes { get; set; }
    public int Coinb1Bytes { get; set; }
    public int Coinb2Bytes { get; set; }
    public int MerkleBranchCount { get; set; }
    public double ParseDurationMs { get; set; }
    public double BuildDurationMs { get; set; }
    public double ValidationDurationMs { get; set; }
    public double SnapshotReadDurationMs { get; set; }
    public double SnapshotReadLockWaitDurationMs { get; set; }
    public double SnapshotReadLockBodyDurationMs { get; set; }
    public double ShareCoreValidationDurationMs { get; set; }
    public double StateMutationDurationMs { get; set; }
    public double StateMutationLockWaitDurationMs { get; set; }
    public double StateMutationLockBodyDurationMs { get; set; }
    public double StaleHandlingDurationMs { get; set; }
    public double ResponseSendDurationMs { get; set; }
    public double TotalDurationMs { get; set; }
    public int CurrentRoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class DatumCoinbaseTemplate
{
    public List<PayoutInfo> WinnersList { get; set; } = [];
    public List<PayoutInfo> CoinbaseOutputs { get; set; } = [];
    public string ActiveSnapshotId { get; set; } = string.Empty;
}

public class BootDatumSessionTelemetry
{
    public string SessionId { get; set; } = string.Empty;
    public string Protocol { get; set; } = "unknown";
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string ClientIdentityKey { get; set; } = string.Empty;
    public string ClientEncryptIdentityKey { get; set; } = string.Empty;
    public string LockedPayoutAddress { get; set; } = string.Empty;
    public bool HandshakeCompleted { get; set; }
    public bool ServerInitiatedClose { get; set; }
    public string? ServerCloseEventType { get; set; }
    public string CloseDisposition { get; set; } = "open";
    public string? CloseReason { get; set; }
    public int HelloCount { get; set; }
    public int CoinbaserFetchCount { get; set; }
    public int RefreshRequestCount { get; set; }
    public int ShareResponseCount { get; set; }
    public int AcceptedShareCount { get; set; }
    public int RejectedShareCount { get; set; }
    public int AffectedOnDeckCount { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? HelloReceivedUtc { get; set; }
    public DateTime? PayoutLockedUtc { get; set; }
    public DateTime? LastCoinbaserFetchUtc { get; set; }
    public DateTime? LastShareResponseUtc { get; set; }
    public DateTime? LastRefreshRequestUtc { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public string LastActivityType { get; set; } = string.Empty;
    public DateTime? ClosedUtc { get; set; }
    public double? DurationMs { get; set; }
    public double? HandshakeMs { get; set; }
    public double? IdleBeforeCloseMs { get; set; }
}

public class BootDatumProtocolEvent
{
    public string SessionId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string Protocol { get; set; } = "unknown";
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string Direction { get; set; } = "internal";
    public string EventType { get; set; } = string.Empty;
    public string MessageLabel { get; set; } = string.Empty;
    public byte? ProtoCmd { get; set; }
    public byte? MiningSubcommand { get; set; }
    public bool? IsSigned { get; set; }
    public bool? IsEncryptedPubKey { get; set; }
    public bool? IsEncryptedChannel { get; set; }
    public uint? CmdLen { get; set; }
    public int? BytesRead { get; set; }
    public int? ExpectedBytes { get; set; }
    public int? DecryptedBytes { get; set; }
    public string? RawHeaderHex { get; set; }
    public string? DecodedHeaderHex { get; set; }
    public uint? HeaderKeyBefore { get; set; }
    public uint? HeaderKeyAfter { get; set; }
    public bool? Accepted { get; set; }
    public bool? AffectedOnDeck { get; set; }
    public string? RejectionReason { get; set; }
    public double? Difficulty { get; set; }
    public string? PrevBlockHash { get; set; }
    public int? JobId { get; set; }
    public int? CoinbaseId { get; set; }
    public bool? NonceOnlySubmit { get; set; }
    public bool? UsedCachedJob { get; set; }
    public double? CachedJobAgeMs { get; set; }
    public string? Username { get; set; }
    public string? CloseDisposition { get; set; }
    public string? CloseReason { get; set; }
    public string? Detail { get; set; }
    public double? DurationMs { get; set; }
    public int CurrentRoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class BootNetworkEvent
{
    public string EventType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string Transport { get; set; } = string.Empty;
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string RemoteNodeId { get; set; } = string.Empty;
    public DateTime? AnnouncedAtUtc { get; set; }
    public double? RelayLatencyMs { get; set; }
    public int PayloadBytes { get; set; }
    public string? BlockHash { get; set; }
    public long? BlockHeight { get; set; }
    public int CurrentRoundNumber { get; set; }
    public string CurrentStateId { get; set; } = string.Empty;
    public string CandidateStateId { get; set; } = string.Empty;
    public string? CurrentTipBlockHash { get; set; }
    public long? CurrentTipBlockHeight { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class BootCoinbaserDiagnosticsSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootCoinbaserFetchTelemetry> Events { get; set; } = [];
}

public class BootShareDiagnosticsSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootShareDiagnosticTelemetry> Events { get; set; } = [];
}

public class BootDatumShareResponseSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootDatumShareResponseTelemetry> Events { get; set; } = [];
}

public class BootDatumSessionSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootDatumSessionTelemetry> Events { get; set; } = [];
}

public class BootDatumProtocolEventSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootDatumProtocolEvent> Events { get; set; } = [];
}

public class BootNetworkEventSeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootNetworkEvent> Events { get; set; } = [];
}

public class BootPeerRelayLatencySeriesDto
{
    public int WindowSeconds { get; set; }
    public int TotalEvents { get; set; }
    public List<BootPeerRelayTransportSummaryDto> Transports { get; set; } = [];
    public List<BootPeerRelayObservation> Observations { get; set; } = [];
}

public class BootHashratePoint
{
    public DateTime TimestampUtc { get; set; }
    public int CurrentRoundNumber { get; set; }
    public double? TeamEstimatedHashrateThs { get; set; }
    public string TeamEstimatedHashrateDisplay { get; set; } = "--";
    public double? LocalDatumHashrateThs { get; set; }
    public string LocalDatumHashrateDisplay { get; set; } = "--";
}

public class BootHashrateSeriesDto
{
    public int SampleIntervalSeconds { get; set; }
    public int LocalWindowSeconds { get; set; }
    public List<BootHashratePoint> Points { get; set; } = [];
}

public class BootRoundPayoutAggregate
{
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int SlotCount { get; set; }
    public ulong TotalValue { get; set; }
    public double TotalDifficulty { get; set; }
    public string TotalDifficultyDisplay { get; set; } = "0";
}

public class BootRoundHistoryEntry
{
    public int RoundNumber { get; set; }
    public string StateId { get; set; } = string.Empty;
    public string? PreviousStateId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public bool IsCanonical { get; set; }
    public bool IsOrphaned { get; set; }
    public string? TriggerBlockHash { get; set; }
    public long? TriggerBlockHeight { get; set; }
    public string? ParentBlockHash { get; set; }
    public long? ParentBlockHeight { get; set; }
    public DateTime LockedAtUtc { get; set; }
    public long? RoundElapsedSeconds { get; set; }
    public int WinningShareCount { get; set; }
    public double WinningTotalDifficulty { get; set; }
    public string WinningTotalDifficultyDisplay { get; set; } = "0";
    public double? ObservedHashrateThs { get; set; }
    public string ObservedHashrateDisplay { get; set; } = "--";
    public int PaidSlotCount { get; set; }
    public int PaidRecipientCount { get; set; }
    public ulong PaidTotalValue { get; set; }
    public int NextWinnerSlotCount { get; set; }
    public int NextWinnerRecipientCount { get; set; }
    public ulong NextWinnerTotalValue { get; set; }
    public List<BootRoundPayoutAggregate> PaidRecipients { get; set; } = [];
    public List<BootRoundPayoutAggregate> NextRecipients { get; set; } = [];
}

public class PeerShareAnnouncement
{
    public string SenderEndpoint { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; }
    public int ConsensusVersion { get; set; }
    public int StateBundleSchemaVersion { get; set; }
    public int HttpApiVersion { get; set; }
    public int PeerTransportVersion { get; set; }
    public int UdpRelayVersion { get; set; }
    public string ReleaseVersion { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    public string ProofClass { get; set; } = BootProofClasses.Work;
    public string RelayStage { get; set; } = BootRelayStages.Validated;
    public int RelayTtl { get; set; }
    public BootShareProof Share { get; set; } = new();
}

public class BootPeerSessionHello
{
    public string Type { get; set; } = "hello";
    public int ProtocolVersion { get; set; }
    public int ConsensusVersion { get; set; }
    public int StateBundleSchemaVersion { get; set; }
    public int HttpApiVersion { get; set; }
    public int PeerTransportVersion { get; set; }
    public int UdpRelayVersion { get; set; }
    public string ReleaseVersion { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string UdpHost { get; set; } = string.Empty;
    public int UdpPort { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string X25519PublicKey { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string TimestampUtc { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

public class BootPeerSessionEncryptedFrame
{
    public string Type { get; set; } = "encrypted";
    public ulong Sequence { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
}

public class BootPeerSessionPayload
{
    public string Type { get; set; } = string.Empty;
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
    public PeerShareAnnouncement? Share { get; set; }
    public BootPeerAddressBookDto? AddressBook { get; set; }
    public BootNetworkStatusDto? NetworkStatus { get; set; }
    public BootChainTipAnnouncement? ChainTip { get; set; }
    public string StateId { get; set; } = string.Empty;
    public string StateKind { get; set; } = string.Empty;
    public string BundleEncoding { get; set; } = string.Empty;
    public string BundleData { get; set; } = string.Empty;
    public int BundleUncompressedBytes { get; set; }
    public string? Text { get; set; }
}

public class BootChainTipAnnouncement
{
    public string SenderEndpoint { get; set; } = string.Empty;
    public string SenderNodeId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string HeaderHex { get; set; } = string.Empty;
    public string BlockHash { get; set; } = string.Empty;
    public long? BlockHeight { get; set; }
    public DateTime ObservedUtc { get; set; } = DateTime.UtcNow;
    public DateTime RelayQueuedUtc { get; set; }
    public int ProtocolVersion { get; set; }
    public int ConsensusVersion { get; set; }
    public int PeerTransportVersion { get; set; }
    public string NetworkId { get; set; } = string.Empty;
}

public class BootStateBundle
{
    public string StateId { get; set; } = string.Empty;
    public string? PreviousStateId { get; set; }
    public string Kind { get; set; } = "current";
    public int CurrentRoundNumber { get; set; }
    public int ProtocolVersion { get; set; }
    public int ConsensusVersion { get; set; }
    public int StateBundleSchemaVersion { get; set; }
    public int HttpApiVersion { get; set; }
    public int PeerTransportVersion { get; set; }
    public int UdpRelayVersion { get; set; }
    public string ReleaseVersion { get; set; } = string.Empty;
    public BootNodeVersionInfo VersionInfo { get; set; } = new();
    public string NetworkId { get; set; } = string.Empty;
    public string? LockedByBlockHash { get; set; }
    public long? LockedByBlockHeight { get; set; }
    public string? ParentBlockHash { get; set; }
    public long? ParentBlockHeight { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public double TotalDifficulty { get; set; }
    public string ActiveSnapshotId { get; set; } = string.Empty;
    public string PaidSnapshotId { get; set; } = string.Empty;
    public List<string> ActiveSnapshotProofIds { get; set; } = [];
    public List<string> PaidSnapshotProofIds { get; set; } = [];
    public bool SupportFeeEnabled { get; set; }
    public string PayoutVariant { get; set; } = string.Empty;
    public List<string> ValidParentBlockHashes { get; set; } = [];
    public List<PayoutInfo> WinnersList { get; set; } = [];
    public List<PayoutInfo> ProofWinnersList { get; set; } = [];
    public List<BootShareProof> ShareProofs { get; set; } = [];
    public List<BootShareProof> WorkSetProofs { get; set; } = [];
    public List<BootPayoutSnapshotContext> SnapshotContexts { get; set; } = [];
    public BootCommitmentInfo Commitment { get; set; } = new();
    public BootSnapshotFamilyMember? SnapshotFamilyMember { get; set; }
}

public class BootPayoutSnapshotContext
{
    public string SnapshotId { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public string PreviousSnapshotId { get; set; } = string.Empty;
    public int CurrentRoundNumber { get; set; }
    public string? LockedByBlockHash { get; set; }
    public long? LockedByBlockHeight { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool SupportFeeEnabled { get; set; }
    public string PayoutVariant { get; set; } = string.Empty;
    public List<string> ProofIds { get; set; } = [];
    public List<PayoutInfo> WinnersList { get; set; } = [];
    public List<PayoutInfo> FeeFreeWinnersList { get; set; } = [];
}

public class ShareRecordingResult
{
    public bool Accepted { get; set; }
    public string? RejectionReason { get; set; }
    public string ProofClass { get; set; } = BootProofClasses.Work;
    public string RelayStage { get; set; } = BootRelayStages.Validated;
    public bool PulseAccepted { get; set; }
    public bool AffectedConsensusState { get; set; }
    public bool NewRecord { get; set; }
    public bool AffectedOnDeck { get; set; }
    public double ComputedDifficulty { get; set; }
    public bool BlockCandidate { get; set; }
    public bool IsBlock { get; set; }
    public string BlockHash { get; set; } = string.Empty;
    public BootShareProof? AcceptedProof { get; set; }
    public List<PayoutInfo> OnDeckList { get; set; } = [];
    public BestShareRecord BestShare { get; set; } = new();
    public BootNetworkStatusDto NetworkStatus { get; set; } = new();
    public RoundRotationResult? Rotation { get; set; }
    public double SnapshotReadDurationMs { get; set; }
    public double SnapshotReadLockWaitDurationMs { get; set; }
    public double SnapshotReadLockBodyDurationMs { get; set; }
    public double ShareCoreValidationDurationMs { get; set; }
    public double StateMutationDurationMs { get; set; }
    public double StateMutationLockWaitDurationMs { get; set; }
    public double StateMutationLockBodyDurationMs { get; set; }
    public DateTime? TransportReceivedUtc { get; set; }
    public DateTime? StateServiceReceivedUtc { get; set; }
    public DateTime? DifficultyCheckedUtc { get; set; }
    public DateTime? ValidationCompletedUtc { get; set; }
    public DateTime? StateMutationCompletedUtc { get; set; }
}

public class RoundRotationResult
{
    public bool Rotated { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? BlockHash { get; set; }
    public List<PayoutInfo> WinnersList { get; set; } = [];
    public List<PayoutInfo> OnDeckList { get; set; } = [];
    public BootNetworkStatusDto NetworkStatus { get; set; } = new();
    public BootStateBundle? LockedStateBundle { get; set; }
}
