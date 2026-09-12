using System.Numerics;
using boot_portal.Models;
using boot_portal.Utils;

namespace boot_portal.Services;

public sealed class DashboardReadModelService
{
    private static readonly BigInteger DifficultyOneTarget = DecodeCompactTarget(0x1d00ffff);
    private readonly PoolConfig _poolConfig;
    private readonly BootProtocolStateService _stateService;
    private readonly DashboardTelemetryService _telemetry;
    private readonly DashboardRevisionService _revision;
    private readonly DashboardVisualizationJournalService _visualization;

    public DashboardReadModelService(
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        DashboardTelemetryService telemetry,
        DashboardRevisionService revision,
        DashboardVisualizationJournalService visualization)
    {
        _poolConfig = poolConfig;
        _stateService = stateService;
        _telemetry = telemetry;
        _revision = revision;
        _visualization = visualization;
    }

    public DashboardSummaryDto BuildSummary(string? windowKey)
    {
        string window = DashboardWindows.Normalize(windowKey);
        BootNetworkStatusDto status = _stateService.GetPublicNetworkStatus();
        MiningShareAdviceDto advice = _stateService.GetShareAdviceResponse();
        DashboardWorkRateEstimateDto estimate = _telemetry.GetEstimate(window);
        string healthStatus = !status.MiningWorkSafe
            ? "unsafe"
            : !status.PeerLoopsHealthy ||
              !status.OutboundRelayHealthy ||
              !string.IsNullOrWhiteSpace(status.BitcoinNotification.DegradedReason)
                ? "degraded"
                : "ready";

        return new DashboardSummaryDto
        {
            Revision = _revision.CurrentRevision,
            GeneratedAtUtc = DateTime.UtcNow,
            Node = new DashboardNodeDto
            {
                NodeId = status.NodeId,
                DisplayName = status.PublicNodeDisplayName,
                Region = status.PublicNodeRegion,
                Role = status.PublicNodeRole,
                PublicEndpoint = status.SelfEndpoint,
                NetworkId = status.NetworkId,
                BitcoinNetwork = status.BitcoinNetwork,
                ReleaseVersion = status.ReleaseVersion,
                ConsensusVersion = status.ConsensusVersion,
                ProtocolVersion = status.ProtocolVersion,
                HttpApiVersion = status.HttpApiVersion,
                ServiceStartedUtc = status.ServiceStartedUtc
            },
            Health = new DashboardHealthDto
            {
                Status = healthStatus,
                MiningWorkSafe = status.MiningWorkSafe,
                MiningWorkSafetyReason = status.MiningWorkSafetyReason,
                PeerCount = status.PeerCount,
                PeerLoopsHealthy = status.PeerLoopsHealthy,
                OutboundRelayHealthy = status.OutboundRelayHealthy,
                BitcoinNotificationMode = status.BitcoinNotification.Mode,
                BitcoinAuthorityClass = status.BitcoinNotification.AuthorityClass,
                BitcoinRpcReachable = status.BitcoinNotification.Rpc.Reachable,
                BitcoinRpcSynced = status.BitcoinNotification.Rpc.Synced,
                BitcoinInitialBlockDownload = status.BitcoinNotification.Rpc.InitialBlockDownload,
                CurrentTipBlockHash = status.CurrentTipBlockHash ?? string.Empty,
                CurrentTipBlockHeight = status.CurrentTipBlockHeight,
                ProvisionalTipBlockHash = status.ProvisionalTipBlockHash ?? string.Empty,
                LastPeerPollCompletedUtc = status.LastPeerPollCompletedUtc
            },
            Snapshot = new DashboardSnapshotDto
            {
                RoundNumber = status.CurrentRoundNumber,
                CurrentStateId = status.CurrentStateId,
                CandidateStateId = status.CandidateStateId,
                ActiveSnapshotId = status.ActiveSnapshotId,
                ActiveSnapshotFamilyId = status.ActiveSnapshotFamilyId,
                LockedPayoutCount = status.WinnersCount,
                LockedProofCount = status.ActiveSnapshotProofCount,
                ReserveCount = status.WorkSetCount,
                ReserveLimit = status.WorkSetReserveLimit,
                ReserveFloorDifficulty = advice.CurrentWorkSetFloorDifficulty,
                ReserveFloorDifficultyDisplay = advice.CurrentWorkSetFloorDifficultyDisplay,
                LastRotationUtc = status.LastRotationUtc,
                FamilyMemberCount = status.SnapshotFamilyMemberCount,
                FamilyUnionProofCount = status.SnapshotFamilyUnionProofCount,
                Reconciliation = status.ReconciliationCounters
            },
            WorkRate = estimate,
            Pulse = new DashboardPulseDto
            {
                Enabled = status.PulseProofsEnabled,
                AcceptedTotal = status.LocalPulseAcceptedCount,
                AcceptedInWindow = _telemetry.GetPulseCount(window),
                AcceptedPerMinute = status.LocalPulseAcceptRatePerMinute,
                LastAcceptedUtc = status.LastLocalPulseUtc,
                LastSuccessfulOutboundRelayUtc = status.LastSuccessfulOutboundRelayUtc,
                OutboundRelayHealthy = status.OutboundRelayHealthy,
                TargetIntervalSeconds = status.PulseTargetIntervalSeconds,
                RelayTtl = status.PulseRelayTtl
            },
            Mining = new DashboardMiningDto
            {
                NativeSv2 = new DashboardNativeSv2Dto
                {
                    Enabled = _poolConfig.NativeSv2Enabled,
                    PublicHost = _poolConfig.NativeSv2PublicHost,
                    PublicPort = _poolConfig.NativeSv2PublicPort
                }
            },
            Capabilities = new DashboardCapabilitiesDto
            {
                WebUiEnabled = _poolConfig.EnableWebUi,
                LegacyUiEnabled = _poolConfig.EnableLegacyUi,
                OperatorApiAvailable =
                    _poolConfig.EnableAdminApi &&
                    !string.IsNullOrWhiteSpace(_poolConfig.AdminApiKey),
                WatchtowerAvailable = false
            }
        };
    }

    public DashboardHistoryDto BuildHistory(string? windowKey) =>
        _telemetry.GetHistory(windowKey);

    public DashboardAddressDto BuildAddress(string address)
    {
        string normalized = BitcoinScript.NormalizeAddress(address);
        _ = BitcoinScript.AddressToScriptPubKey(normalized, _poolConfig.BitcoinNetwork);
        List<PayoutInfo> locked = _stateService.GetWinnersList();
        List<PayoutInfo> reserve = _stateService.GetOnDeckList();
        MiningShareAdviceDto advice = _stateService.GetShareAdviceResponse();
        BootNetworkStatusDto status = _stateService.GetPublicNetworkStatus();

        List<int> lockedPositions = locked
            .Select((payout, index) => new { payout, position = index })
            .Where(item => string.Equals(
                BitcoinScript.NormalizeAddress(item.payout.Address),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.position)
            .ToList();
        List<(PayoutInfo payout, int position)> provisional = reserve
            .Select((payout, index) => (payout, position: index + 1))
            .Where(item => string.Equals(
                BitcoinScript.NormalizeAddress(item.payout.Address),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        double? bestDifficulty = provisional.Count == 0
            ? null
            : provisional.Max(item => item.payout.Difficulty);
        double? survival = bestDifficulty.HasValue && status.CurrentTipCompactTarget.HasValue
            ? CalculateSurvivalProbability(bestDifficulty.Value, status.CurrentTipCompactTarget.Value, 300)
            : null;

        return new DashboardAddressDto
        {
            Address = normalized,
            Found = lockedPositions.Count > 0 || provisional.Count > 0,
            LockedSlotCount = lockedPositions.Count,
            LockedValueSats = locked
                .Where(item => string.Equals(
                    BitcoinScript.NormalizeAddress(item.Address),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .Aggregate(0UL, (sum, item) => checked(sum + item.Value)),
            LockedPositions = lockedPositions,
            ProvisionalPositionCount = provisional.Count,
            ProvisionalPositions = provisional.Select(item => item.position).ToList(),
            BestProvisionalDifficulty = bestDifficulty,
            BestProvisionalDifficultyDisplay = bestDifficulty.HasValue
                ? ClientHandler.FormatDifficulty(bestDifficulty.Value)
                : "--",
            ReserveFloorDifficulty = advice.CurrentWorkSetFloorDifficulty,
            ReserveFloorDifficultyDisplay = advice.CurrentWorkSetFloorDifficultyDisplay,
            EstimatedTop300SurvivalProbability = survival,
            Interpretation = lockedPositions.Count > 0
                ? "This address is present in the active payout snapshot used by current block templates."
                : provisional.Count > 0
                    ? "This address is currently ranked in the unpaid reserve. Its position is provisional until a snapshot boundary."
                    : "This address is not present in the active payout snapshot or current unpaid reserve."
        };
    }

    public DashboardOperatorDto BuildOperator()
    {
        BootNetworkStatusDto status = _stateService.GetNetworkStatus();
        return new DashboardOperatorDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            LocalMiningSources = status.LocalMiningSources,
            LocalMiners = status.LocalDatumMiners,
            Peers = status.Peers,
            BitcoinNotification = status.BitcoinNotification,
            DatumDiagnostics = status.LocalDatumDiagnostics,
            CoinbaserDiagnostics = status.CoinbaserDiagnostics,
            PeerLoopFaults = status.PeerLoopFaults
        };
    }

    public DashboardDiagramDto BuildDiagram(bool includeOperatorDetails)
    {
        BootNetworkStatusDto fullStatus = _stateService.GetNetworkStatus();
        BootNetworkStatusDto publicStatus = _stateService.GetPublicNetworkStatus();
        DashboardDiagramStateProjection projection = _stateService.GetDashboardDiagramState();
        (long oldest, long latest) = _visualization.Bounds();
        DashboardDiagramSlotZeroDto observedSlotZero = _visualization.SlotZero();
        DashboardWorkRateEstimateDto teamEstimate = _telemetry.GetEstimate("24h");
        double? remoteHashrateThs = teamEstimate.EstimateThs.HasValue
            ? Math.Max(0, teamEstimate.EstimateThs.Value - (fullStatus.LocalMiningHashrateThs ?? 0))
            : null;
        double? remoteRelativeError = remoteHashrateThs is > 0 &&
            teamEstimate.RelativeStandardErrorPercent.HasValue
                ? teamEstimate.RelativeStandardErrorPercent.Value *
                    teamEstimate.EstimateThs!.Value / remoteHashrateThs.Value
                : null;
        double? networkDifficulty = CalculateNetworkDifficulty(publicStatus.CurrentTipCompactTarget);
        BootShareDiagnosticsSeriesDto rejected = _stateService.GetShareDiagnostics(
            "24h", source: null, accepted: false, limit: 1000);
        List<BootShareDiagnosticTelemetry> localRejected = rejected.Events
            .Where(item => !BootPeerSource.TryParsePeerSource(item.Source, out _, out _, out _))
            .ToList();
        BootBitcoinNetworkHealthDto bitcoinNetwork = publicStatus.BitcoinNotification.Network;
        var result = new DashboardDiagramDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Redacted = !includeOperatorDetails,
            OldestSequence = oldest,
            LatestSequence = latest,
            SlotZero = new DashboardDiagramSlotZeroDto
            {
                Verified = observedSlotZero.Verified,
                Address = observedSlotZero.Address,
                ObservedUtc = observedSlotZero.ObservedUtc,
                ProofId = observedSlotZero.ProofId
            },
            Grid = new DashboardDiagramGridDto
            {
                HashrateThs = remoteHashrateThs,
                HashrateDisplay = FormatHashrate(remoteHashrateThs),
                RelativeStandardErrorPercent = remoteRelativeError,
                Confidence = remoteHashrateThs is > 0 ? teamEstimate.Confidence : "collecting"
            },
            Bitcoin = new DashboardDiagramBitcoinDto
            {
                Reachable = publicStatus.BitcoinNotification.Rpc.Reachable,
                Synced = publicStatus.BitcoinNotification.Rpc.Synced,
                InitialBlockDownload = publicStatus.BitcoinNotification.Rpc.InitialBlockDownload,
                TipHash = publicStatus.CurrentTipBlockHash ?? string.Empty,
                TipHeight = publicStatus.CurrentTipBlockHeight,
                ProvisionalTipHash = publicStatus.ProvisionalTipBlockHash ?? string.Empty,
                NetworkDifficulty = networkDifficulty,
                NetworkDifficultyDisplay = FormatNetworkDifficulty(networkDifficulty),
                NetworkHashrateHs = bitcoinNetwork.NetworkHashrateHs,
                NetworkHashrateDisplay = FormatHashesPerSecond(bitcoinNetwork.NetworkHashrateHs),
                PeerCount = bitcoinNetwork.TotalPeerCount,
                InboundPeerCount = bitcoinNetwork.InboundPeerCount,
                OutboundPeerCount = bitcoinNetwork.OutboundPeerCount,
                PeerTelemetryUtc = bitcoinNetwork.LastPeerSuccessUtc,
                MiningSafe = publicStatus.MiningWorkSafe,
                ZmqHealthy = publicStatus.BitcoinNotification.ZmqTopics.All(topic =>
                    !topic.Configured || topic.SubscriberRunning),
                Peers = bitcoinNetwork.Peers
                    .OrderBy(peer => peer.Id)
                    .Take(24)
                    .Select(peer => new DashboardDiagramBitcoinPeerDto
                    {
                        VisualId = _visualization.VisualId("bitcoin-peer", peer.Id.ToString()),
                        Inbound = peer.Inbound,
                        LatencyMs = peer.LatencyMs,
                        ConnectionType = peer.ConnectionType
                    }).ToList()
            },
            WorkGenerator = BuildWorkGenerator(fullStatus, includeOperatorDetails),
            Snapshot = new DashboardDiagramSnapshotDto
            {
                CurrentStateId = publicStatus.CurrentStateId,
                CandidateStateId = publicStatus.CandidateStateId,
                ActiveSnapshotId = publicStatus.ActiveSnapshotId,
                ActiveSnapshotFamilyId = publicStatus.ActiveSnapshotFamilyId,
                LockedProofCount = publicStatus.ActiveSnapshotProofCount,
                PaidProofRemovalCount = projection.LastPaidProofCount,
                LastRotationUtc = publicStatus.LastRotationUtc
            },
            Quality = new DashboardDiagramQualityDto
            {
                RejectionCategories = localRejected
                    .GroupBy(
                        item => DashboardVisualizationJournalService.RejectionCategory(
                            item.RejectionCategory,
                            item.RejectionReason),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => new BootReasonCountDto { Reason = group.Key, Count = group.Count() })
                    .OrderByDescending(item => item.Count)
                    .ThenBy(item => item.Reason, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList()
            },
            WorkSet = projection.WorkSet.Select(proof => new DashboardDiagramProofDto
            {
                VisualId = _visualization.VisualId("proof", proof.ProofId),
                ProofId = proof.ProofId,
                Rank = proof.Rank,
                Address = proof.Address,
                Difficulty = proof.Difficulty,
                DifficultyDisplay = proof.DifficultyDisplay,
                FirstSeenUtc = proof.FirstSeenUtc,
                Locked = proof.Locked,
                BlockQuality = networkDifficulty.HasValue && proof.Difficulty >= networkDifficulty
            }).ToList()
        };

        if (includeOperatorDetails)
        {
            result.Peers = fullStatus.Peers.Select(peer =>
            {
                string identity = !string.IsNullOrWhiteSpace(peer.NodeId) ? peer.NodeId : peer.Endpoint;
                bool connected = peer.SessionConnected ||
                    string.Equals(peer.Status, "connected", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(peer.Status, "ok", StringComparison.OrdinalIgnoreCase);
                return new DashboardDiagramPeerDto
                {
                    VisualId = _visualization.VisualId("peer", identity),
                    DisplayName = PublicPeerDisplayName(peer.Endpoint) ?? peer.NodeId,
                    NodeId = peer.NodeId,
                    Endpoint = peer.Endpoint,
                    Status = peer.Status,
                    Connected = connected,
                    LatencyMs = peer.LatencyMs,
                    LastActivityUtc = peer.LastSuccessUtc ?? peer.LastSeenUtc,
                    CompatibilityStatus = peer.CompatibilityStatus,
                    Transport = peer.ConnectionMode,
                    StateRelation = StateRelation(peer, fullStatus),
                    LastInboundUtc = peer.LastSuccessUtc ?? peer.LastSeenUtc,
                    LastOutboundUtc = peer.LastAttemptUtc
                };
            }).ToList();
            result.Miners = fullStatus.LocalDatumMiners.Select(miner =>
            {
                string identity = $"{miner.Source}:{miner.Address}:{miner.Username}";
                List<BootShareDiagnosticTelemetry> minerRejected = localRejected
                    .Where(item => string.Equals(item.MinerAddress, miner.Address, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.TimestampUtc)
                    .ToList();
                BootShareDiagnosticTelemetry? lastRejected = minerRejected.LastOrDefault();
                return new DashboardDiagramMinerDto
                {
                    VisualId = _visualization.VisualId("miner", identity),
                    Address = miner.Address,
                    Username = miner.Username,
                    Source = miner.Source,
                    HashrateThs = miner.CurrentHashrateThs,
                    HashrateDisplay = miner.CurrentHashrateDisplay,
                    LastShareUtc = miner.LastShareUtc,
                    AcceptedCount = miner.TotalAcceptedShareCount,
                    RejectedCount = minerRejected.Count,
                    LastRejectedUtc = lastRejected?.TimestampUtc,
                    LastRejectionCategory = lastRejected?.RejectionCategory ?? string.Empty,
                    LastRejectionReason = lastRejected?.RejectionReason ?? string.Empty
                };
            }).ToList();
        }
        else
        {
            result.Peers = fullStatus.Peers.Select(peer =>
            {
                string identity = !string.IsNullOrWhiteSpace(peer.NodeId) ? peer.NodeId : peer.Endpoint;
                bool connected = peer.SessionConnected ||
                    string.Equals(peer.Status, "connected", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(peer.Status, "ok", StringComparison.OrdinalIgnoreCase);
                return new DashboardDiagramPeerDto
                {
                    VisualId = _visualization.VisualId("peer", identity),
                    DisplayName = PublicPeerDisplayName(peer.Endpoint) ?? peer.NodeId,
                    NodeId = peer.NodeId,
                    Status = connected ? "connected" : "disconnected",
                    Connected = connected,
                    LatencyMs = peer.LatencyMs,
                    Transport = peer.ConnectionMode,
                    StateRelation = StateRelation(peer, fullStatus),
                    LastInboundUtc = peer.LastSuccessUtc ?? peer.LastSeenUtc,
                    LastOutboundUtc = peer.LastAttemptUtc
                };
            }).ToList();
            result.Miners = fullStatus.LocalDatumMiners.Select(miner =>
            {
                string identity = $"{miner.Source}:{miner.Address}:{miner.Username}";
                List<BootShareDiagnosticTelemetry> minerRejected = localRejected
                    .Where(item => string.Equals(item.MinerAddress, miner.Address, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new DashboardDiagramMinerDto
                {
                    VisualId = _visualization.VisualId("miner", identity),
                    HashrateThs = miner.CurrentHashrateThs,
                    HashrateDisplay = miner.CurrentHashrateDisplay,
                    LastShareUtc = miner.LastShareUtc,
                    AcceptedCount = miner.TotalAcceptedShareCount,
                    RejectedCount = minerRejected.Count,
                    LastRejectedUtc = minerRejected.OrderBy(item => item.TimestampUtc).LastOrDefault()?.TimestampUtc,
                    LastRejectionCategory = minerRejected.OrderBy(item => item.TimestampUtc).LastOrDefault()?.RejectionCategory ?? string.Empty
                };
            }).ToList();
        }

        return result;
    }

    public DashboardDiagramHistoryDto BuildDiagramHistory(
        string? windowKey,
        int limit,
        bool includeOperatorDetails)
    {
        DashboardDiagramSlotZeroDto slotZero = _visualization.SlotZero();
        return _telemetry.GetDiagramHistory(
            slotZero.Verified ? slotZero.Address : string.Empty,
            windowKey,
            limit,
            includeOperatorDetails);
    }

    private static string StateRelation(BootPeerStatus peer, BootNetworkStatusDto local)
    {
        if (string.Equals(peer.CompatibilityStatus, "incompatible", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(peer.CompatibilityStatus, "mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "incompatible";
        }
        if (string.Equals(peer.LastCurrentStateId, local.CurrentStateId, StringComparison.OrdinalIgnoreCase))
        {
            return "current";
        }
        if (string.Equals(peer.LastCandidateStateId, local.CandidateStateId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(peer.LastCurrentStateId, local.CandidateStateId, StringComparison.OrdinalIgnoreCase))
        {
            return "candidate";
        }
        return string.IsNullOrWhiteSpace(peer.LastCurrentStateId) ? "unknown" : "divergent";
    }

    private static DashboardDiagramWorkGeneratorDto BuildWorkGenerator(
        BootNetworkStatusDto status,
        bool includeOperatorDetails)
    {
        if (!includeOperatorDetails)
        {
            List<BootLocalMiningSourceSummaryDto> publicSources = status.LocalMiningSources;
            return new DashboardDiagramWorkGeneratorDto
            {
                DetailAvailable = false,
                Connected = status.MiningWorkSafe,
                MinerCount = status.LocalDatumMiners.Count,
                HashrateThs = status.LocalMiningHashrateThs,
                HashrateDisplay = status.LocalMiningHashrateDisplay,
                DisplayName = publicSources.Count == 1
                    ? publicSources[0].DisplayName
                    : "Local work generator"
            };
        }
        List<BootLocalMiningSourceSummaryDto> sources = status.LocalMiningSources;
        return new DashboardDiagramWorkGeneratorDto
        {
            DetailAvailable = true,
            Connected = sources.Any(source => source.ActiveMinerCount > 0),
            DisplayName = sources.Count == 1
                ? sources[0].DisplayName
                : "Local work generator",
            MinerCount = sources.Sum(source => source.ActiveMinerCount),
            HashrateThs = status.LocalMiningHashrateThs,
            HashrateDisplay = status.LocalMiningHashrateDisplay,
            LastActivityUtc = sources
                .Where(source => source.LastShareUtc.HasValue)
                .Select(source => source.LastShareUtc)
                .DefaultIfEmpty(null)
                .Max()
        };
    }

    internal static string? PublicPeerDisplayName(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }
        return uri.Host.ToLowerInvariant() switch
        {
            "dallas.gridpool.net" => "Dallas",
            "detroit.gridpool.net" => "Detroit",
            "oregon.gridpool.net" => "Oregon",
            "evomining.farted.net" => "evomining.farted.net",
            _ => null
        };
    }

    private static double? CalculateNetworkDifficulty(uint? compactTarget)
    {
        if (!compactTarget.HasValue)
        {
            return null;
        }
        BigInteger target = DecodeCompactTarget(compactTarget.Value);
        if (target <= 0)
        {
            return null;
        }
        double difficulty = (double)DifficultyOneTarget / (double)target;
        return double.IsFinite(difficulty) && difficulty > 0 ? difficulty : null;
    }

    private static string FormatNetworkDifficulty(double? difficulty) => difficulty switch
    {
        >= 1e15 => $"{difficulty / 1e15:0.##} Q",
        >= 1e12 => $"{difficulty / 1e12:0.##} T",
        >= 1e9 => $"{difficulty / 1e9:0.##} G",
        > 0 => difficulty.Value.ToString("0.##"),
        _ => "--"
    };

    private static string FormatHashrate(double? hashrateThs)
    {
        if (!hashrateThs.HasValue || !double.IsFinite(hashrateThs.Value))
        {
            return "--";
        }

        double hashesPerSecond = hashrateThs.Value * 1_000_000_000_000d;
        string[] units = ["H/s", "kH/s", "MH/s", "GH/s", "TH/s", "PH/s", "EH/s"];
        int unit = 0;
        while (hashesPerSecond >= 1000d && unit < units.Length - 1)
        {
            hashesPerSecond /= 1000d;
            unit++;
        }
        return $"{hashesPerSecond:0.##} {units[unit]}";
    }

    private static string FormatHashesPerSecond(double? hashesPerSecond)
    {
        if (!hashesPerSecond.HasValue || !double.IsFinite(hashesPerSecond.Value) || hashesPerSecond < 0)
        {
            return "--";
        }
        double value = hashesPerSecond.Value;
        string[] units = ["H/s", "kH/s", "MH/s", "GH/s", "TH/s", "PH/s", "EH/s", "ZH/s"];
        int unit = 0;
        while (value >= 1000d && unit < units.Length - 1)
        {
            value /= 1000d;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static double? CalculateSurvivalProbability(double shareDifficulty, uint compactTarget, int slots)
    {
        if (shareDifficulty <= 0 || slots <= 0)
        {
            return null;
        }

        BigInteger target = DecodeCompactTarget(compactTarget);
        if (target <= 0)
        {
            return null;
        }

        double networkDifficulty = (double)DifficultyOneTarget / (double)target;
        if (!double.IsFinite(networkDifficulty) || networkDifficulty <= 0)
        {
            return null;
        }

        double missOne = networkDifficulty / (shareDifficulty + networkDifficulty);
        return 1d - Math.Pow(missOne, slots);
    }

    private static BigInteger DecodeCompactTarget(uint compact)
    {
        int exponent = (int)(compact >> 24);
        uint mantissa = compact & 0x007fffff;
        if ((compact & 0x00800000) != 0 || mantissa == 0)
        {
            return BigInteger.Zero;
        }

        BigInteger target = mantissa;
        return exponent <= 3
            ? target >> (8 * (3 - exponent))
            : target << (8 * (exponent - 3));
    }
}
