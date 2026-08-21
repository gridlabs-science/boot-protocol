using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using boot_portal.Models;
using boot_portal.Utils;
using Microsoft.AspNetCore.SignalR;

namespace boot_portal.Services;

public class BootProtocolStateService
{
    public const string GenesisFoundationAddress = "bc1qce93hy5rhg02s6aeu7mfdvxg76x66pqqtrvzs3";
    public const string TestnetGenesisFoundationAddress = "mhK63i2JYNBsZ9aWcq6rhA1eCMFqp5MALL";
    public const string GridLabsSupportAddress = "bc1qrwsx8fs0l6z7ugp5cvzy6lhss7jlyru3kg9s8y";
    public const string TestnetGridLabsSupportAddress = TestnetGenesisFoundationAddress;
    private const ulong MainnetCurrentSubsidySats = 312_500_000;
    private const ulong Testnet4CurrentSubsidySats = 5_000_000_000;

    public static string GetGenesisFoundationAddress(string? bitcoinNetwork)
    {
        return BitcoinScript.NormalizeNetwork(bitcoinNetwork) == BitcoinScript.Testnet4
            ? TestnetGenesisFoundationAddress
            : GenesisFoundationAddress;
    }

    public static ulong GetCurrentBlockSubsidySats(string? bitcoinNetwork)
    {
        return BitcoinScript.NormalizeNetwork(bitcoinNetwork) == BitcoinScript.Testnet4
            ? Testnet4CurrentSubsidySats
            : MainnetCurrentSubsidySats;
    }

    public static string GetGridLabsSupportAddress(string? bitcoinNetwork)
    {
        return BitcoinScript.NormalizeNetwork(bitcoinNetwork) == BitcoinScript.Testnet4
            ? TestnetGridLabsSupportAddress
            : GridLabsSupportAddress;
    }

    private readonly DateTime _serviceStartedUtc = DateTime.UtcNow;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _suppressedPeerEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly PoolConfig _poolConfig;
    private readonly BootShareVerifier _shareVerifier;
    private readonly IHubContext<PoolStatsHub> _hubContext;
    private readonly ILogger<BootProtocolStateService> _logger;
    private readonly BootPeerLoopHealth _peerLoopHealth;
    private readonly BootPeerIdentity? _peerIdentity;
    private readonly BitcoinNotificationHealth? _bitcoinNotificationHealth;
    private bool _identityChanged;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly JsonSerializerOptions _compactJsonOptions = new() { WriteIndented = false };
    private readonly Channel<BootShareProof> _acceptedShares = Channel.CreateUnbounded<BootShareProof>();
    private readonly Channel<BootChainTipAnnouncement> _chainTipAnnouncements = Channel.CreateUnbounded<BootChainTipAnnouncement>();
    private readonly HashSet<string> _seenShareIds = [];
    private readonly Queue<string> _seenShareQueue = new();
    private readonly List<BootShareDiagnosticTelemetry> _recentShareDiagnostics = [];
    private readonly List<BootDatumProtocolEvent> _recentDatumProtocolEvents = [];
    private readonly Dictionary<string, PeerRelayFirstArrival> _peerRelayFirstArrivals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BootDatumSessionTelemetry> _activeDatumSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalDatumAddressHashrateTracker> _localDatumHashrateByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalMiningSourceGauge> _localMiningSourceGauges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastLocalDatumHashrateRollupByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BootStateBundle> _recentCandidateBundles = [];
    private readonly Dictionary<string, Queue<DateTime>> _recentPulseByPeer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTime>> _recentPulseByAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optimisticRelayedShareIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitcoinHeaderEvaluation> _localChainTipHeaders = new(StringComparer.OrdinalIgnoreCase);
    private long _provisionalTipGeneration;
    private Task? _deferredSaveTask;
    private bool _deferredSavePending;
    private Task? _deferredHistorySaveTask;
    private bool _deferredHistorySavePending;
    private static readonly TimeSpan DeferredSaveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeferredHistorySaveInterval = TimeSpan.FromSeconds(60);
    private const double SlowStateLockWaitWarningMs = 250;
    private const double SlowStateSaveWarningMs = 250;
    private const int MaxSeenShareIds = 20000;
    private const int MaxAcceptedParentBlockHashes = 128;
    private const int MaxRecentRejectedShareDiagnostics = 1000;
    private const int MaxRecentCoinbaserDiagnostics = 1000;
    private const int MaxRecentDatumShareResponses = 2000;
    private const int MaxRecentDatumSessions = 5000;
    private const int MaxRecentDatumProtocolEvents = 25000;
    private const int MaxRecentNetworkEvents = 20000;
    private const int MaxRecentPeerRelayObservations = 10000;
    private const int MaxRecentCandidateBundles = 8;
    private const int MinLocalDatumMinerDisplaySamples = 8;
    private const int MinLocalHashrateObservationSeconds = 300;

    private PoolState _state = new();

    private sealed record PeerRelayFirstArrival(DateTime TimestampUtc, string Transport);
    private sealed record SnapshotValidationResult(BootShareValidationResult Validation, string SnapshotId);

    public event Func<string, Task>? WinnersListChanged;
    public event Func<string, Task>? WorkTemplatesInvalidated;

    public BootProtocolStateService(
        PoolConfig poolConfig,
        BootShareVerifier shareVerifier,
        IHubContext<PoolStatsHub> hubContext,
        ILogger<BootProtocolStateService> logger,
        BootPeerLoopHealth? peerLoopHealth = null,
        BootPeerIdentity? peerIdentity = null,
        BitcoinNotificationHealth? bitcoinNotificationHealth = null)
    {
        _poolConfig = poolConfig;
        _shareVerifier = shareVerifier;
        _hubContext = hubContext;
        _logger = logger;
        _peerLoopHealth = peerLoopHealth ?? new BootPeerLoopHealth();
        _peerIdentity = peerIdentity;
        _bitcoinNotificationHealth = bitcoinNotificationHealth;
        LoadState();

        string? restoredProvisionalHash = null;
        long restoredGeneration = 0;
        lock (_sync)
        {
            if (!_poolConfig.EnablePeerTipStaleProtection)
            {
                _state.ProvisionalTip = null;
            }
            else if (_state.ProvisionalTip != null)
            {
                BitcoinHeaderEvaluation restoredHeader = BitcoinHashes.EvaluateHeader(
                    _state.ProvisionalTip.HeaderHex,
                    _state.ProvisionalTip.ObservedUtc);
                if (!restoredHeader.IsValid ||
                    !BitcoinHashes.AreEquivalent(restoredHeader.BlockHash, _state.ProvisionalTip.BlockHash) ||
                    !BitcoinHashes.AreEquivalent(restoredHeader.ParentBlockHash, _state.CurrentTipBlockHash))
                {
                    _logger.LogWarning("Discarded malformed or obsolete provisional peer-tip state during startup.");
                    _state.ProvisionalTip = null;
                    SaveStateNoLock();
                }
                else
                {
                    _provisionalTipGeneration++;
                    restoredGeneration = _provisionalTipGeneration;
                    restoredProvisionalHash = _state.ProvisionalTip.BlockHash;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(restoredProvisionalHash))
        {
            ScheduleProvisionalTipGraceCheck(restoredProvisionalHash, restoredGeneration);
        }
    }

    public ChannelReader<BootShareProof> AcceptedShares => _acceptedShares.Reader;
    public ChannelReader<BootChainTipAnnouncement> ChainTipAnnouncements => _chainTipAnnouncements.Reader;

    public int GetActiveConsensusVersion()
    {
        lock (_sync)
        {
            return GetActiveConsensusVersionNoLock();
        }
    }

    private int GetActiveConsensusVersionNoLock() =>
        BootProtocolVersions.GetActiveConsensusVersion(_poolConfig, _state.TrustedLocalTipBlockHeight);

    private BootNodeVersionInfo GetLocalVersionInfoNoLock() =>
        BootProtocolVersions.Local(_poolConfig, GetActiveConsensusVersionNoLock());

    public List<PayoutInfo> GetWinnersList()
    {
        var waitStopwatch = Stopwatch.StartNew();
        lock (_sync)
        {
            double waitMs = waitStopwatch.Elapsed.TotalMilliseconds;
            if (waitMs >= SlowStateLockWaitWarningMs)
            {
                _logger.LogWarning(
                    "Slow state lock wait in GetWinnersList: {WaitMs:F1} ms (round={Round}, candidate={CandidateStateId}, onDeck={OnDeckCount}).",
                    waitMs,
                    _state.CurrentRoundNumber,
                    _state.CandidateStateId,
                    _state.OnDeckList.Count);
            }

            return ClonePayouts(_state.WinnersList);
        }
    }

    public List<PayoutInfo> GetCoinbaseOutputs()
    {
        var waitStopwatch = Stopwatch.StartNew();
        lock (_sync)
        {
            double waitMs = waitStopwatch.Elapsed.TotalMilliseconds;
            if (waitMs >= SlowStateLockWaitWarningMs)
            {
                _logger.LogWarning(
                    "Slow state lock wait in GetCoinbaseOutputs: {WaitMs:F1} ms (round={Round}, candidate={CandidateStateId}, onDeck={OnDeckCount}).",
                    waitMs,
                    _state.CurrentRoundNumber,
                    _state.CandidateStateId,
                    _state.OnDeckList.Count);
            }

            return BuildCoinbaseOutputsNoLock(_state.WinnersList);
        }
    }

    public DatumCoinbaseTemplate GetDatumCoinbaseTemplate()
    {
        var waitStopwatch = Stopwatch.StartNew();
        lock (_sync)
        {
            if (!IsMiningWorkSafeNoLock(DateTime.UtcNow))
            {
                throw new InvalidOperationException(BuildMiningWorkSafetyReasonNoLock());
            }

            double waitMs = waitStopwatch.Elapsed.TotalMilliseconds;
            if (waitMs >= SlowStateLockWaitWarningMs)
            {
                _logger.LogWarning(
                    "Slow state lock wait in GetDatumCoinbaseTemplate: {WaitMs:F1} ms (round={Round}, candidate={CandidateStateId}, onDeck={OnDeckCount}).",
                    waitMs,
                    _state.CurrentRoundNumber,
                    _state.CandidateStateId,
                    _state.OnDeckList.Count);
            }

            return new DatumCoinbaseTemplate
            {
                WinnersList = ClonePayouts(_state.WinnersList),
                CoinbaseOutputs = BuildCoinbaseOutputsNoLock(_state.WinnersList),
                ActiveSnapshotId = _state.ActiveSnapshotId
            };
        }
    }

    public List<PayoutInfo> GetOnDeckList()
    {
        lock (_sync)
        {
            return ClonePayouts(_state.OnDeckList);
        }
    }

    public BestShareRecord GetBestShare()
    {
        lock (_sync)
        {
            return CloneBestShare(_state.BestShare);
        }
    }

    public BootNetworkStatusDto GetNetworkStatus()
    {
        lock (_sync)
        {
            return BuildNetworkStatusNoLock();
        }
    }

    public bool CanIssueMiningWork(out string reason)
    {
        lock (_sync)
        {
            bool safe = IsMiningWorkSafeNoLock(DateTime.UtcNow);
            reason = safe ? string.Empty : BuildMiningWorkSafetyReasonNoLock();
            return safe;
        }
    }

    public PayoutResponseDto GetPayoutResponse()
    {
        lock (_sync)
        {
            return new PayoutResponseDto
            {
                Sequence = DateTime.UtcNow.Ticks,
                Payouts = ClonePayouts(_state.WinnersList),
                CoinbaseOutputs = BuildCoinbaseOutputsNoLock(_state.WinnersList),
                Network = BuildNetworkStatusNoLock()
            };
        }
    }

    public Sv2WorkSelectionDto GetSv2WorkSelectionResponse()
    {
        lock (_sync)
        {
            List<PayoutInfo> coinbaseOutputs = BuildCoinbaseOutputsNoLock(_state.WinnersList);
            var serializedOutputs = new List<(ulong Value, byte[] ScriptPubKey)>(coinbaseOutputs.Count);
            var outputDtos = new List<Sv2CoinbaseOutputDto>(coinbaseOutputs.Count);

            foreach (PayoutInfo payout in coinbaseOutputs)
            {
                string normalizedAddress = BitcoinScript.NormalizeAddress(payout.Address);
                byte[] scriptPubKey = BitcoinScript.AddressToScriptPubKey(normalizedAddress, _poolConfig.BitcoinNetwork);
                byte[] serializedOutput = BitcoinTransactionSerialization.SerializeTxOutput(payout.Value, scriptPubKey);

                serializedOutputs.Add((payout.Value, scriptPubKey));
                outputDtos.Add(new Sv2CoinbaseOutputDto
                {
                    Value = payout.Value,
                    Address = normalizedAddress,
                    ScriptPubKeyHex = Convert.ToHexString(scriptPubKey).ToLowerInvariant(),
                    OutputHex = Convert.ToHexString(serializedOutput).ToLowerInvariant(),
                    Username = string.IsNullOrWhiteSpace(payout.Username) ? normalizedAddress : payout.Username,
                    Difficulty = payout.Difficulty,
                    DiffString = payout.DiffString
                });
            }

            byte[] coinbaseTxOutputs = BitcoinTransactionSerialization.SerializeTxOutputs(serializedOutputs);
            double minimumDifficultyToEnter = GetWorkSetAdmissionDifficultyNoLock();

            string planMaterial = string.Join('|',
                "gridpool-mining-plan-v1",
                _poolConfig.BootNetworkId,
                _poolConfig.BitcoinNetwork,
                GetActiveConsensusVersionNoLock(),
                _state.ActiveSnapshotId,
                _state.CurrentTipBlockHash ?? string.Empty,
                _state.CurrentTipBlockHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                IsMiningWorkSafeNoLock(DateTime.UtcNow),
                _state.ProvisionalTip?.BlockHash ?? string.Empty,
                _poolConfig.TotalPayoutSlotCount,
                _poolConfig.SharedWinnerSlotCount,
                _poolConfig.GridLabsSupportFeeEnabled,
                Math.Max(1d, _poolConfig.PulseMinDifficulty).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToHexString(coinbaseTxOutputs).ToLowerInvariant());
            string planId = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(planMaterial)))
                .ToLowerInvariant();

            return new Sv2WorkSelectionDto
            {
                SchemaVersion = 1,
                PlanId = planId,
                Sequence = DateTime.UtcNow.Ticks,
                NetworkId = _poolConfig.BootNetworkId,
                BitcoinNetwork = _poolConfig.BitcoinNetwork,
                ProtocolVersion = GetActiveConsensusVersionNoLock(),
                ActiveSnapshotId = _state.ActiveSnapshotId,
                CurrentStateId = _state.CurrentStateId,
                CandidateStateId = _state.CandidateStateId,
                CurrentTipBlockHash = _state.CurrentTipBlockHash,
                CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
                MiningWorkSafe = IsMiningWorkSafeNoLock(DateTime.UtcNow),
                MiningWorkSafetyReason = BuildMiningWorkSafetyReasonNoLock(),
                ProvisionalTipBlockHash = _state.ProvisionalTip?.BlockHash,
                TotalPayoutSlotCount = _poolConfig.TotalPayoutSlotCount,
                SharedWinnerSlotCount = _poolConfig.SharedWinnerSlotCount,
                SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
                CoinbaseOutputCount = coinbaseOutputs.Count,
                CoinbaseTxOutputsBytes = coinbaseTxOutputs.Length,
                CoinbaseTxOutputsHex = Convert.ToHexString(coinbaseTxOutputs).ToLowerInvariant(),
                CoinbaseOutputs = outputDtos,
                MinimumAcceptedDifficulty = 1d,
                MinimumPulseDifficulty = Math.Max(1d, _poolConfig.PulseMinDifficulty),
                MinimumDifficultyToEnterReserve = minimumDifficultyToEnter,
                MinimumDifficultyToEnterReserveDisplay = ClientHandler.FormatDifficulty(minimumDifficultyToEnter),
                UserIdentifierRule = "Use payoutAddress or payoutAddress.worker; GridPool still attributes shares from the slot-0 coinbase output, not this metadata.",
                Mode = "coinbase-only"
            };
        }
    }

    public MiningShareAdviceDto GetShareAdviceResponse()
    {
        lock (_sync)
        {
            List<double> workSetDifficulties = _state.OnDeckProofs
                .Select(proof => proof.Difficulty)
                .Where(difficulty => difficulty > 0)
                .OrderByDescending(difficulty => difficulty)
                .ToList();
            List<double> onDeckDifficulties = workSetDifficulties
                .Take(_poolConfig.SnapshotProofSlotCount)
                .ToList();
            double? workSetFloorDifficulty = workSetDifficulties.Count == 0
                ? null
                : workSetDifficulties[^1];
            double? floorDifficulty = onDeckDifficulties.Count == 0
                ? null
                : onDeckDifficulties[^1];
            double? bestDifficulty = workSetDifficulties.Count == 0
                ? null
                : workSetDifficulties[0];
            int openSlots = Math.Max(0, _poolConfig.WorkSetReserveLimit - _state.OnDeckProofs.Count);
            bool onDeckIsFull = openSlots == 0;
            bool requiresStrictlyGreaterThanFloor = onDeckIsFull && workSetFloorDifficulty.HasValue;
            double minimumDifficultyToEnter = requiresStrictlyGreaterThanFloor
                ? Math.Max(1d, Math.BitIncrement(workSetFloorDifficulty!.Value))
                : 1d;
            double minimumPulseDifficulty = Math.Max(1d, _poolConfig.PulseMinDifficulty);
            double minimumOptimisticRelayDifficulty = Math.Max(minimumDifficultyToEnter, _poolConfig.MinOptimisticRelayDifficulty);
            string submitRule = requiresStrictlyGreaterThanFloor
                ? $"Submit only shares with computed difficulty greater than {ClientHandler.FormatDifficulty(workSetFloorDifficulty!.Value)}."
                : "Submit any share with computed difficulty at least 1; open work-set reserve slots remain.";

            return new MiningShareAdviceDto
            {
                Sequence = DateTime.UtcNow.Ticks,
                CurrentRoundNumber = _state.CurrentRoundNumber,
                CurrentStateId = _state.CurrentStateId,
                CandidateStateId = _state.CandidateStateId,
                ActiveSnapshotId = _state.ActiveSnapshotId,
                CurrentTipBlockHash = _state.CurrentTipBlockHash,
                CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
                MiningWorkSafe = IsMiningWorkSafeNoLock(DateTime.UtcNow),
                MiningWorkSafetyReason = BuildMiningWorkSafetyReasonNoLock(),
                ProvisionalTipBlockHash = _state.ProvisionalTip?.BlockHash,
                SharedWinnerSlotCount = _poolConfig.SharedWinnerSlotCount,
                WorkSetCount = _state.OnDeckProofs.Count,
                WorkSetReserveLimit = _poolConfig.WorkSetReserveLimit,
                OnDeckCount = _state.OnDeckList.Count,
                OpenOnDeckSlots = openSlots,
                OnDeckIsFull = onDeckIsFull,
                MinimumAcceptedDifficulty = 1d,
                CurrentWorkSetFloorDifficulty = workSetFloorDifficulty,
                CurrentWorkSetFloorDifficultyDisplay = workSetFloorDifficulty.HasValue ? ClientHandler.FormatDifficulty(workSetFloorDifficulty.Value) : "--",
                CurrentOnDeckFloorDifficulty = floorDifficulty,
                CurrentOnDeckFloorDifficultyDisplay = floorDifficulty.HasValue ? ClientHandler.FormatDifficulty(floorDifficulty.Value) : "--",
                MinimumDifficultyToEnterOnDeck = minimumDifficultyToEnter,
                MinimumDifficultyToEnterOnDeckDisplay = ClientHandler.FormatDifficulty(minimumDifficultyToEnter),
                RequiresStrictlyGreaterThanFloor = requiresStrictlyGreaterThanFloor,
                PulseProofsEnabled = _poolConfig.EnablePulseProofs,
                MinimumPulseDifficulty = minimumPulseDifficulty,
                MinimumPulseDifficultyDisplay = ClientHandler.FormatDifficulty(minimumPulseDifficulty),
                PulseTargetIntervalSeconds = Math.Max(1, _poolConfig.PulseTargetIntervalSeconds),
                PulseRelayTtl = Math.Max(1, _poolConfig.PulseRelayTtl),
                OptimisticRelayEnabled = _poolConfig.EnableOptimisticShareRelay,
                MinimumOptimisticRelayDifficulty = minimumOptimisticRelayDifficulty,
                MinimumOptimisticRelayDifficultyDisplay = ClientHandler.FormatDifficulty(minimumOptimisticRelayDifficulty),
                BestOnDeckDifficulty = bestDifficulty,
                BestOnDeckDifficultyDisplay = bestDifficulty.HasValue ? ClientHandler.FormatDifficulty(bestDifficulty.Value) : "--",
                SubmitRule = submitRule
            };
        }
    }

    public double GetWorkSetAdmissionDifficulty()
    {
        lock (_sync)
        {
            return GetWorkSetAdmissionDifficultyNoLock();
        }
    }

    public bool IsAcceptedParentBlockHash(string? blockHash)
    {
        lock (_sync)
        {
            return IsAcceptedParentBlockHashNoLock(blockHash);
        }
    }

    private double GetWorkSetAdmissionDifficultyNoLock()
    {
        List<double> workSetDifficulties = _state.OnDeckProofs
            .Select(proof => proof.Difficulty)
            .Where(difficulty => difficulty > 0)
            .OrderByDescending(difficulty => difficulty)
            .ToList();
        if (workSetDifficulties.Count < _poolConfig.WorkSetReserveLimit)
        {
            return 1d;
        }

        double floorDifficulty = workSetDifficulties[^1];
        return Math.Max(1d, Math.BitIncrement(floorDifficulty));
    }

    public BootStateBundle? GetStateBundle(string stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId))
        {
            return null;
        }

        lock (_sync)
        {
            var archived = _state.ArchivedStateBundles.FirstOrDefault(x =>
                string.Equals(x.StateId, stateId, StringComparison.OrdinalIgnoreCase));
            if (archived != null)
            {
                return BuildExportableBundleNoLock(archived);
            }

            var recentCandidate = _recentCandidateBundles.FirstOrDefault(x =>
                string.Equals(x.StateId, stateId, StringComparison.OrdinalIgnoreCase));
            if (recentCandidate != null)
            {
                return BuildExportableBundleNoLock(recentCandidate);
            }

            if (string.Equals(stateId, _state.CandidateStateId, StringComparison.OrdinalIgnoreCase))
            {
                return BuildBundleFromCurrentCandidateNoLock();
            }

            if (string.Equals(stateId, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase))
            {
                return BuildBundleFromCurrentWinnersNoLock();
            }

            return null;
        }
    }

    private BootStateBundle BuildExportableBundleNoLock(BootStateBundle bundle)
    {
        BootStateBundle exportable = CloneBundle(bundle);
        StampBundleVersionNoLock(exportable);
        exportable.SnapshotContexts = BuildSnapshotContextsForBundleNoLock(
            exportable.ShareProofs.Concat(exportable.WorkSetProofs),
            exportable.SnapshotContexts);
        return exportable;
    }

    private void StampBundleVersionNoLock(BootStateBundle bundle)
    {
        BootNodeVersionInfo localVersion = GetLocalVersionInfoNoLock();
        int bundleConsensusVersion = bundle.ConsensusVersion != 0
            ? bundle.ConsensusVersion
            : bundle.ProtocolVersion != 0
                ? bundle.ProtocolVersion
                : localVersion.ConsensusVersion;
        bundle.ProtocolVersion = bundle.ProtocolVersion != 0 ? bundle.ProtocolVersion : bundleConsensusVersion;
        bundle.ConsensusVersion = bundleConsensusVersion;
        bundle.StateBundleSchemaVersion = bundle.StateBundleSchemaVersion != 0
            ? bundle.StateBundleSchemaVersion
            : BootProtocolVersions.GetStateBundleSchemaVersion(bundleConsensusVersion);
        bundle.HttpApiVersion = bundle.HttpApiVersion != 0 ? bundle.HttpApiVersion : localVersion.HttpApiVersion;
        bundle.PeerTransportVersion = bundle.PeerTransportVersion != 0
            ? bundle.PeerTransportVersion
            : localVersion.PeerTransportVersion;
        bundle.UdpRelayVersion = bundle.UdpRelayVersion != 0 ? bundle.UdpRelayVersion : localVersion.UdpRelayVersion;
        bundle.ReleaseVersion = string.IsNullOrWhiteSpace(bundle.ReleaseVersion)
            ? localVersion.ReleaseVersion
            : bundle.ReleaseVersion;
        int bundleSoftwareConsensusVersion = bundle.VersionInfo?.SoftwareConsensusVersion ?? 0;
        bundle.VersionInfo = new BootNodeVersionInfo
        {
            SoftwareConsensusVersion = bundleSoftwareConsensusVersion != 0
                ? bundleSoftwareConsensusVersion
                : localVersion.SoftwareConsensusVersion,
            ConsensusVersion = bundle.ConsensusVersion,
            ProtocolVersion = bundle.ProtocolVersion,
            StateBundleSchemaVersion = bundle.StateBundleSchemaVersion,
            HttpApiVersion = bundle.HttpApiVersion,
            PeerTransportVersion = bundle.PeerTransportVersion,
            UdpRelayVersion = bundle.UdpRelayVersion,
            ReleaseVersion = bundle.ReleaseVersion
        };
        bundle.NetworkId = string.IsNullOrWhiteSpace(bundle.NetworkId)
            ? _poolConfig.BootNetworkId
            : bundle.NetworkId;
    }

    public List<BootRoundHistoryEntry> GetRoundHistory(int limit = 24)
    {
        lock (_sync)
        {
            return BuildRoundHistoryNoLock(limit);
        }
    }

    public BootRoundHistoryEntry? GetRoundHistoryEntry(string stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId))
        {
            return null;
        }

        lock (_sync)
        {
            List<BootStateBundle> completedRounds = _state.ArchivedStateBundles
                .Where(x => x.WinnersList.Count > 0 || x.ProofWinnersList.Count > 0)
                .ToList();
            HashSet<string> canonicalStateIds = BuildCanonicalStateIdSetNoLock();
            int index = completedRounds.FindIndex(x =>
                string.Equals(x.StateId, stateId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            BootStateBundle? priorBundle = index + 1 < completedRounds.Count
                ? completedRounds[index + 1]
                : null;
            return BuildRoundHistoryEntryNoLock(
                completedRounds[index],
                canonicalStateIds.Contains(completedRounds[index].StateId),
                priorBundle);
        }
    }

    public List<BootPeerStatus> GetPeers()
    {
        lock (_sync)
        {
            return CloneExternalPeersNoLock();
        }
    }

    public BootHashrateSeriesDto GetHashrateSeries(string? windowKey = null)
    {
        lock (_sync)
        {
            return BuildHashrateSeriesNoLock(windowKey);
        }
    }

    public BootShareDiagnosticsSeriesDto GetShareDiagnostics(
        string? windowKey = "12h",
        string? source = null,
        bool? accepted = false,
        int limit = 500,
        string? minerAddress = null,
        string? category = null)
    {
        lock (_sync)
        {
            return BuildShareDiagnosticsSeriesNoLock(windowKey, source, accepted, limit, minerAddress, category);
        }
    }

    public BootCoinbaserDiagnosticsSeriesDto GetCoinbaserDiagnostics(
        string? windowKey = "12h",
        int limit = 500,
        string? remoteEndpoint = null,
        bool? temporarySlotZero = null)
    {
        lock (_sync)
        {
            return BuildCoinbaserDiagnosticsSeriesNoLock(windowKey, limit, remoteEndpoint, temporarySlotZero);
        }
    }

    public BootDatumShareResponseSeriesDto GetDatumShareResponses(
        string? windowKey = "12h",
        int limit = 500,
        string? remoteEndpoint = null,
        bool? accepted = null,
        string? reason = null)
    {
        lock (_sync)
        {
            return BuildDatumShareResponseSeriesNoLock(windowKey, limit, remoteEndpoint, accepted, reason);
        }
    }

    public BootDatumSessionSeriesDto GetDatumSessions(
        string? windowKey = "12h",
        int limit = 500,
        string? remoteEndpoint = null,
        bool? active = null,
        string? protocol = null)
    {
        lock (_sync)
        {
            return BuildDatumSessionSeriesNoLock(windowKey, limit, remoteEndpoint, active, protocol);
        }
    }

    public BootDatumProtocolEventSeriesDto GetDatumProtocolEvents(
        string? windowKey = "12h",
        int limit = 500,
        string? sessionId = null,
        string? remoteEndpoint = null,
        string? eventType = null,
        string? direction = null,
        string? messageLabel = null)
    {
        lock (_sync)
        {
            return BuildDatumProtocolEventSeriesNoLock(windowKey, limit, sessionId, remoteEndpoint, eventType, direction, messageLabel);
        }
    }

    public BootLocalDatumMinerSeriesDto GetLocalDatumMinerSummaries(string? address = null, int? limit = null, string? windowKey = "24h")
    {
        lock (_sync)
        {
            DateTime nowUtc = DateTime.UtcNow;
            string normalizedSearch = NormalizeSearchTerm(address);
            int requestedLimit = Math.Clamp(limit ?? GetLocalDatumMinerSummaryLimit(), 1, 5000);
            int buildLimit = GetLocalDatumMaxTrackedAddresses();
            List<BootLocalDatumMinerSummaryDto> allSummaries = BuildLocalDatumMinerSummariesNoLock(nowUtc, buildLimit);
            List<BootLocalDatumMinerSummaryDto> activeSummaries = allSummaries
                .Where(summary => IsActiveLocalDatumMinerSummaryNoLock(summary, nowUtc))
                .ToList();
            List<BootLocalDatumMinerSummaryDto> activeNonTemporarySummaries = activeSummaries
                .Where(summary => !IsTemporaryFoundationLocalDatumSummary(summary, _poolConfig.BitcoinNetwork))
                .ToList();
            List<BootLocalDatumMinerSummaryDto> displayableSummaries = activeNonTemporarySummaries
                .Where(summary => IsDisplayableLocalDatumMinerSummaryNoLock(summary, nowUtc))
                .ToList();
            List<BootLocalDatumMinerSummaryDto> summaries;
            if (!string.IsNullOrWhiteSpace(normalizedSearch))
            {
                List<BootLocalDatumMinerSummaryDto> addressMatches = activeNonTemporarySummaries
                    .Where(miner => NormalizeSearchTerm(miner.Address).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                summaries = (addressMatches.Count > 0
                    ? addressMatches
                    : activeNonTemporarySummaries
                        .Where(miner => NormalizeSearchTerm(miner.Username).Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase))
                        .ToList())
                    .Take(requestedLimit)
                    .ToList();
            }
            else
            {
                summaries = displayableSummaries
                    .Take(requestedLimit)
                    .ToList();
            }

            List<BootLocalDatumMinerHashratePointDto> points = summaries.Count == 1
                ? BuildLocalDatumMinerHashratePointsNoLock(summaries[0].Address, nowUtc, windowKey)
                : [];

            return new BootLocalDatumMinerSeriesDto
            {
                WindowSeconds = GetHashrateLocalWindowSeconds(),
                TotalTrackedMiners = activeNonTemporarySummaries.Count,
                ReturnedCount = summaries.Count,
                Miners = summaries,
                Points = points
            };
        }
    }

    public BootNetworkEventSeriesDto GetNetworkEvents(
        string? windowKey = "12h",
        int limit = 500,
        string? eventType = null,
        string? source = null)
    {
        lock (_sync)
        {
            return BuildNetworkEventSeriesNoLock(windowKey, limit, eventType, source);
        }
    }

    public BootPeerRelayLatencySeriesDto GetPeerRelayLatency(
        string? windowKey = "12h",
        int limit = 500,
        string? remoteEndpoint = null,
        string? transport = null,
        string? proofClass = null,
        string? relayStage = null)
    {
        lock (_sync)
        {
            return BuildPeerRelayLatencySeriesNoLock(windowKey, limit, remoteEndpoint, transport, proofClass, relayStage);
        }
    }

    public void RecordDatumProtocolEvent(BootDatumProtocolEvent telemetry)
    {
        lock (_sync)
        {
            DateTime effectiveTimestampUtc = telemetry.TimestampUtc == default
                ? DateTime.UtcNow
                : telemetry.TimestampUtc;
            telemetry.TimestampUtc = effectiveTimestampUtc;
            telemetry.CurrentRoundNumber = _state.CurrentRoundNumber;
            telemetry.CurrentStateId = _state.CurrentStateId;
            telemetry.CandidateStateId = _state.CandidateStateId;
            telemetry.CurrentTipBlockHash = _state.CurrentTipBlockHash;
            telemetry.CurrentTipBlockHeight = _state.CurrentTipBlockHeight;

            _recentDatumProtocolEvents.Add(CloneDatumProtocolEvent(telemetry));
            TrimDatumProtocolEventsNoLock(effectiveTimestampUtc);
        }
    }

    public void RecordDatumSessionOpened(string sessionId, string remoteEndpoint, DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            var session = FindOrCreateDatumSessionNoLock(sessionId, remoteEndpoint, effectiveTimestampUtc);
            session.RemoteEndpoint = string.IsNullOrWhiteSpace(remoteEndpoint) ? session.RemoteEndpoint : remoteEndpoint;
            session.StartedUtc = effectiveTimestampUtc;
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = "opened";
            _peerLoopHealth.RecordDatumSessionOpened(effectiveTimestampUtc);
            TrimDatumSessionsNoLock(effectiveTimestampUtc);
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordDatumSessionProtocol(string sessionId, string protocol, DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(protocol))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.Protocol = protocol;
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = "protocol";
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordDatumSessionHello(
        string sessionId,
        string? clientIdentityKey,
        string? clientEncryptIdentityKey,
        DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.HandshakeCompleted = true;
            session.HelloCount += 1;
            session.ClientIdentityKey = clientIdentityKey ?? string.Empty;
            session.ClientEncryptIdentityKey = clientEncryptIdentityKey ?? string.Empty;
            session.HelloReceivedUtc ??= effectiveTimestampUtc;
            session.HandshakeMs ??= Math.Max(0, (effectiveTimestampUtc - session.StartedUtc).TotalMilliseconds);
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = "hello";
            _peerLoopHealth.RecordDatumHelloReceived(effectiveTimestampUtc);
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordDatumSessionPayoutLock(string sessionId, string? payoutAddress, DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(payoutAddress))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.LockedPayoutAddress = BitcoinScript.NormalizeAddress(payoutAddress);
            session.PayoutLockedUtc ??= effectiveTimestampUtc;
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = "payout-lock";
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordDatumSessionCoinbaserFetch(string sessionId, DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.CoinbaserFetchCount += 1;
            session.LastCoinbaserFetchUtc = effectiveTimestampUtc;
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = "coinbaser-fetch";
            _peerLoopHealth.RecordDatumCoinbaserRequest(effectiveTimestampUtc);
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordSuccessfulDatumCoinbaserResponse(DateTime? timestampUtc = null) =>
        _peerLoopHealth.RecordSuccessfulDatumCoinbaserResponse(timestampUtc);

    public void RecordDatumSessionRefreshRequest(string sessionId, DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.RefreshRequestCount += 1;
            session.LastRefreshRequestUtc = effectiveTimestampUtc;
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = "refresh-request";
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordDatumSessionShareOutcome(
        string sessionId,
        bool accepted,
        bool affectedOnDeck,
        DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.ShareResponseCount += 1;
            if (accepted)
            {
                session.AcceptedShareCount += 1;
                _peerLoopHealth.RecordValidLocalDatumShare(effectiveTimestampUtc);
            }
            else
            {
                session.RejectedShareCount += 1;
            }

            if (affectedOnDeck)
            {
                session.AffectedOnDeckCount += 1;
            }

            session.LastShareResponseUtc = effectiveTimestampUtc;
            session.LastActivityUtc = effectiveTimestampUtc;
            session.LastActivityType = accepted ? "share-accepted" : "share-rejected";
            RequestDeferredHistorySaveNoLock();
        }
    }

    public ShareRecordingResult RecordDatumTelemetryShare(
        string minerAddress,
        string username,
        double difficulty,
        DateTime? timestampUtc = null)
    {
        DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
        ShareRecordingResult result;
        bool shouldNotifyNetwork = false;
        lock (_sync)
        {
            string normalizedMinerAddress = BitcoinScript.NormalizeAddress(minerAddress);
            string effectiveUsername = string.IsNullOrWhiteSpace(username) ? normalizedMinerAddress : username;
            var proof = new BootShareProof
            {
                MinerAddress = normalizedMinerAddress,
                Username = effectiveUsername,
                Source = "datum",
                Difficulty = difficulty,
                Timestamp = effectiveTimestampUtc
            };

            bool newRecord = false;
            if (difficulty > _state.BestShare.Difficulty)
            {
                _state.BestShare = new BestShareRecord
                {
                    Difficulty = difficulty,
                    MinerAddress = effectiveUsername,
                    Timestamp = effectiveTimestampUtc
                };
                newRecord = true;
            }

            RecordAcceptedShareTelemetryNoLock(proof);
            RecordShareDiagnosticNoLock(
                "datum",
                normalizedMinerAddress,
                effectiveUsername,
                accepted: true,
                affectedOnDeck: false,
                rejectionReason: null,
                difficulty: difficulty,
                timestampUtc: effectiveTimestampUtc);
            bool capturedHashrateSample = MaybeCaptureHashrateSampleNoLock(effectiveTimestampUtc, force: false);
            RequestDeferredHistorySaveNoLock();

            shouldNotifyNetwork = newRecord || capturedHashrateSample;
            result = new ShareRecordingResult
            {
                Accepted = true,
                ProofClass = BootProofClasses.Pulse,
                RelayStage = BootRelayStages.Validated,
                PulseAccepted = true,
                AffectedConsensusState = false,
                AffectedOnDeck = false,
                NewRecord = newRecord,
                ComputedDifficulty = difficulty,
                BestShare = newRecord ? CloneBestShare(_state.BestShare) : new BestShareRecord(),
                NetworkStatus = shouldNotifyNetwork ? BuildNetworkStatusNoLock() : new BootNetworkStatusDto()
            };
        }

        if (result.NewRecord)
        {
            QueueRealtimeSend(_hubContext.Clients.All.SendAsync("UpdateRecord", result.BestShare), "UpdateRecord");
        }

        if (shouldNotifyNetwork)
        {
            QueueRealtimeSend(_hubContext.Clients.All.SendAsync("UpdateNetworkState", result.NetworkStatus), "UpdateNetworkState");
        }

        return result;
    }

    public void CompleteDatumSession(
        string sessionId,
        string closeDisposition,
        string? closeReason = null,
        bool serverInitiated = false,
        string? serverCloseEventType = null,
        DateTime? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            if (!TryGetDatumSessionNoLock(sessionId, out var session))
            {
                return;
            }

            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            session.ServerInitiatedClose = serverInitiated;
            session.ServerCloseEventType = serverCloseEventType;
            session.CloseDisposition = string.IsNullOrWhiteSpace(closeDisposition) ? "closed" : closeDisposition;
            session.CloseReason = closeReason;
            session.ClosedUtc = effectiveTimestampUtc;
            session.DurationMs = Math.Max(0, (effectiveTimestampUtc - session.StartedUtc).TotalMilliseconds);
            session.IdleBeforeCloseMs = session.LastActivityUtc.HasValue
                ? Math.Max(0, (effectiveTimestampUtc - session.LastActivityUtc.Value).TotalMilliseconds)
                : null;
            if (!session.LastActivityUtc.HasValue)
            {
                session.LastActivityUtc = effectiveTimestampUtc;
                session.LastActivityType = "closed-without-activity";
            }

            _activeDatumSessions.Remove(sessionId);
            _peerLoopHealth.RecordDatumSessionClosed(
                !string.IsNullOrWhiteSpace(closeReason) ? closeReason : session.CloseDisposition,
                effectiveTimestampUtc);
            TrimDatumSessionsNoLock(effectiveTimestampUtc);
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordCoinbaserFetch(
        string sessionId,
        string source,
        string remoteEndpoint,
        string? clientIdentityPreview,
        long requestSequence,
        ulong rewardValue,
        ulong teamPayoutTotal,
        ulong slotZeroValue,
        string slotZeroAddress,
        bool usingTemporarySlotZero,
        int winnersCount,
        int coinbaseOutputCount,
        int responsePayloadBytes,
        double durationMs,
        double parseDurationMs,
        double stateReadDurationMs,
        double buildDurationMs,
        double serializeDurationMs,
        double sendDurationMs,
        DateTime? timestampUtc = null)
    {
        lock (_sync)
        {
            DateTime effectiveTimestampUtc = timestampUtc ?? DateTime.UtcNow;
            _state.RecentCoinbaserDiagnostics.Add(new BootCoinbaserFetchTelemetry
            {
                Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
                RemoteEndpoint = remoteEndpoint,
                ClientIdentityPreview = clientIdentityPreview ?? string.Empty,
                RequestSequence = requestSequence,
                RewardValue = rewardValue,
                TeamPayoutTotal = teamPayoutTotal,
                SlotZeroValue = slotZeroValue,
                SlotZeroAddress = slotZeroAddress,
                UsingTemporarySlotZero = usingTemporarySlotZero,
                WinnersCount = winnersCount,
                CoinbaseOutputCount = coinbaseOutputCount,
                ResponsePayloadBytes = responsePayloadBytes,
                DurationMs = durationMs,
                ParseDurationMs = parseDurationMs,
                StateReadDurationMs = stateReadDurationMs,
                BuildDurationMs = buildDurationMs,
                SerializeDurationMs = serializeDurationMs,
                SendDurationMs = sendDurationMs,
                CurrentRoundNumber = _state.CurrentRoundNumber,
                CurrentStateId = _state.CurrentStateId,
                CandidateStateId = _state.CandidateStateId,
                CurrentTipBlockHash = _state.CurrentTipBlockHash,
                CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
                TimestampUtc = effectiveTimestampUtc
            });

            TrimCoinbaserDiagnosticsNoLock(effectiveTimestampUtc);
            RequestDeferredHistorySaveNoLock();
        }
    }

    public void RecordDatumShareResponse(BootDatumShareResponseTelemetry telemetry)
    {
        lock (_sync)
        {
            DateTime effectiveTimestampUtc = telemetry.TimestampUtc == default
                ? DateTime.UtcNow
                : telemetry.TimestampUtc;
            telemetry.TimestampUtc = effectiveTimestampUtc;
            telemetry.CurrentRoundNumber = _state.CurrentRoundNumber;
            telemetry.CurrentStateId = _state.CurrentStateId;
            telemetry.CandidateStateId = _state.CandidateStateId;
            telemetry.CurrentTipBlockHash = _state.CurrentTipBlockHash;
            telemetry.CurrentTipBlockHeight = _state.CurrentTipBlockHeight;

            _state.RecentDatumShareResponses.Add(CloneDatumShareResponse(telemetry));
            TrimDatumShareResponsesNoLock(effectiveTimestampUtc);
            RequestDeferredHistorySaveNoLock();
        }

        double slowThresholdMs = GetDatumShareResponseSlowMs();
        if (telemetry.TotalDurationMs >= slowThresholdMs)
        {
            _logger.LogWarning(
                "Slow DATUM share response to {RemoteEndpoint}: {TotalMs:F1} ms (accepted={Accepted}, reason={Reason}, job={JobId}, coinbase={CoinbaseId}, coinbaser={CoinbaserId}, snapshot={SnapshotId}, nonceOnly={NonceOnly}, cached={Cached}, difficulty={Difficulty}, snapshotRead={SnapshotReadMs:F1} ms, coreValidate={CoreValidateMs:F1} ms, stateMutation={StateMutationMs:F1} ms).",
                telemetry.RemoteEndpoint,
                telemetry.TotalDurationMs,
                telemetry.Accepted,
                telemetry.RejectionReason ?? "none",
                telemetry.JobId,
                telemetry.CoinbaseId,
                telemetry.CoinbaserId,
                telemetry.PayoutSnapshotId,
                telemetry.NonceOnlySubmit,
                telemetry.UsedCachedJob,
                telemetry.Difficulty,
                telemetry.SnapshotReadDurationMs,
                telemetry.ShareCoreValidationDurationMs,
                telemetry.StateMutationDurationMs);
        }
    }

    public void RecordExternalNetworkEvent(
        string eventType,
        string source,
        string? message,
        string? blockHash = null,
        long? blockHeight = null,
        DateTime? timestampUtc = null,
        string transport = "",
        string remoteEndpoint = "",
        string remoteNodeId = "",
        DateTime? announcedAtUtc = null,
        double? relayLatencyMs = null,
        int payloadBytes = 0)
    {
        lock (_sync)
        {
            RecordNetworkEventNoLock(
                eventType,
                source,
                message,
                blockHash,
                blockHeight,
                timestampUtc,
                transport,
                remoteEndpoint,
                remoteNodeId,
                announcedAtUtc,
                relayLatencyMs,
                payloadBytes);
            RequestDeferredHistorySaveNoLock();
        }
    }

    public string? GetKnownDatumPayoutAddress(string? clientIdentity)
    {
        if (string.IsNullOrWhiteSpace(clientIdentity))
        {
            return null;
        }

        lock (_sync)
        {
            return _state.KnownDatumPayoutAddresses.TryGetValue(clientIdentity, out string? address)
                ? address
                : null;
        }
    }

    public void RememberDatumPayoutAddress(string? clientIdentity, string? payoutAddress)
    {
        if (string.IsNullOrWhiteSpace(clientIdentity) || string.IsNullOrWhiteSpace(payoutAddress))
        {
            return;
        }

        string normalizedAddress = BitcoinScript.NormalizeAddress(payoutAddress);
        lock (_sync)
        {
            if (_state.KnownDatumPayoutAddresses.TryGetValue(clientIdentity, out string? existing) &&
                string.Equals(BitcoinScript.NormalizeAddress(existing), normalizedAddress, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _state.KnownDatumPayoutAddresses[clientIdentity] = normalizedAddress;
            RequestDeferredSaveNoLock();
        }
    }

    public List<string> GetPeerEndpoints()
    {
        return GetEligiblePeerEndpoints(GetPeerOutboundTarget(), markAttempt: true, sourceEndpoint: null);
    }

    public List<string> GetPeerEndpointsForShareRelay(string? sourceEndpoint = null)
    {
        return GetEligiblePeerEndpoints(GetPeerShareRelayTarget(), markAttempt: false, sourceEndpoint);
    }

    public List<string> GetPeerEndpointsForPersistentSessions()
    {
        return GetEligiblePeerEndpoints(GetPeerSessionTarget(), markAttempt: false, sourceEndpoint: null);
    }

    public int GetPeerRelayParallelism()
    {
        return Math.Min(GetPeerRelayParallelismLimit(), GetPeerShareRelayTarget());
    }

    public BootPeerAddressBookDto GetPeerAddressBook(int? limit = null)
    {
        lock (_sync)
        {
            DateTime nowUtc = DateTime.UtcNow;
            NormalizePeerAddressBookNoLock(nowUtc);
            RefreshPeerScoresNoLock(nowUtc);
            int requestedLimit = Math.Clamp(limit ?? GetPeerAddressGossipLimit(), 1, GetPeerAddressGossipLimit());
            List<BootPeerAddressDto> peers = _state.Peers
                .Where(peer => IsPeerAdvertisableNoLock(peer, nowUtc))
                .OrderByDescending(peer => peer.Score)
                .ThenByDescending(peer => peer.LastSuccessUtc ?? peer.LastSeenUtc ?? DateTime.MinValue)
                .Take(requestedLimit)
                .Select(peer => new BootPeerAddressDto
                {
                    Endpoint = peer.Endpoint,
                    Status = peer.Status,
                    Score = peer.Score,
                    LastSeenUtc = peer.LastSeenUtc,
                    LastSuccessUtc = peer.LastSuccessUtc,
                    LastSessionUtc = peer.LastSessionUtc,
                    SessionSuccessCount = peer.SessionSuccessCount,
                    SessionFailureCount = peer.SessionFailureCount,
                    RelaySuccessCount = peer.RelaySuccessCount,
                    RelayFailureCount = peer.RelayFailureCount,
                    UdpRelaySuccessCount = peer.UdpRelaySuccessCount,
                    UdpRelayFailureCount = peer.UdpRelayFailureCount,
                    RemoteVersion = CloneVersionInfo(peer.RemoteVersion),
                    CompatibilityStatus = peer.CompatibilityStatus,
                    CompatibilityReason = peer.CompatibilityReason
                })
                .ToList();

            return new BootPeerAddressBookDto
            {
                SelfEndpoint = GetSelfEndpoint(),
                TotalKnownPeers = _state.Peers.Count(peer => IsPeerAdvertisableNoLock(peer, nowUtc)),
                ReturnedCount = peers.Count,
                Peers = peers
            };
        }
    }

    private List<string> GetEligiblePeerEndpoints(int targetCount, bool markAttempt, string? sourceEndpoint)
    {
        lock (_sync)
        {
            DateTime nowUtc = DateTime.UtcNow;
            NormalizePeerAddressBookNoLock(nowUtc);
            RefreshPeerScoresNoLock(nowUtc);
            string normalizedSource = string.IsNullOrWhiteSpace(sourceEndpoint)
                ? string.Empty
                : NormalizePeerEndpoint(sourceEndpoint);
            List<BootPeerStatus> selectedPeers = _state.Peers
                .Where(peer => IsPeerEligibleForAttemptNoLock(peer, nowUtc, normalizedSource))
                .OrderByDescending(peer => peer.Score)
                .ThenBy(peer => peer.LastAttemptUtc ?? DateTime.MinValue)
                .ThenBy(peer => peer.Endpoint, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, targetCount))
                .ToList();

            if (markAttempt)
            {
                foreach (BootPeerStatus peer in selectedPeers)
                {
                    peer.LastAttemptUtc = nowUtc;
                    if (string.Equals(peer.Status, "configured", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(peer.Status, "discovered", StringComparison.OrdinalIgnoreCase))
                    {
                        peer.Status = "dialing";
                    }
                }

                if (selectedPeers.Count > 0)
                {
                    RequestDeferredSaveNoLock();
                }
            }

            return selectedPeers.Select(peer => peer.Endpoint).ToList();
        }
    }

    public bool IsAdminAuthorized(string? suppliedApiKey)
    {
        if (!_poolConfig.EnableAdminApi || string.IsNullOrWhiteSpace(_poolConfig.AdminApiKey))
        {
            return false;
        }

        return string.Equals(_poolConfig.AdminApiKey, suppliedApiKey, StringComparison.Ordinal);
    }

    public string GetSelfEndpoint()
    {
        return NormalizePeerEndpoint(_poolConfig.PublicBaseUrl);
    }

    public string ResolvePeerEndpoint(string dialedEndpoint, string? advertisedEndpoint)
    {
        bool hasNormalizedDialed = TryNormalizePeerEndpoint(dialedEndpoint, allowPrivate: true, out string normalizedDialed, out _);
        if (!string.IsNullOrWhiteSpace(advertisedEndpoint) &&
            TryNormalizePeerEndpoint(advertisedEndpoint, AllowPrivatePeerAdvertisements(), out string normalizedAdvertised, out _))
        {
            if (hasNormalizedDialed && ShouldPreferDialedEndpoint(normalizedDialed, normalizedAdvertised))
            {
                return normalizedDialed;
            }

            return normalizedAdvertised;
        }

        return hasNormalizedDialed
            ? normalizedDialed
            : NormalizePeerEndpoint(dialedEndpoint);
    }

    public bool IsCompatiblePeerNetwork(int protocolVersion, string networkId)
    {
        lock (_sync)
        {
            return protocolVersion == GetActiveConsensusVersionNoLock() &&
                   string.Equals(networkId, _poolConfig.BootNetworkId, StringComparison.OrdinalIgnoreCase);
        }
    }

    public BootNodeVersionInfo GetLocalVersionInfo()
    {
        lock (_sync)
        {
            return GetLocalVersionInfoNoLock();
        }
    }

    public BootVersionCompatibilityDto EvaluatePeerCompatibility(BootNetworkStatusDto remote, bool requireStateBundleSchema = true)
    {
        return EvaluateVersionCompatibility(
            BootProtocolVersions.FromNetworkStatus(remote),
            remote.NetworkId,
            requireStateBundleSchema);
    }

    public BootVersionCompatibilityDto EvaluateStateBundleCompatibility(BootStateBundle bundle)
    {
        return EvaluateVersionCompatibility(
            BootProtocolVersions.FromStateBundle(bundle),
            bundle.NetworkId,
            requireStateBundleSchema: true);
    }

    public BootVersionCompatibilityDto EvaluatePeerShareCompatibility(PeerShareAnnouncement announcement)
    {
        return EvaluateVersionCompatibility(
            BootProtocolVersions.FromPeerShare(announcement),
            announcement.NetworkId,
            requireStateBundleSchema: false);
    }

    private BootVersionCompatibilityDto EvaluateVersionCompatibility(
        BootNodeVersionInfo remoteVersion,
        string? remoteNetworkId,
        bool requireStateBundleSchema)
    {
        lock (_sync)
        {
            return BootProtocolVersions.Evaluate(
                GetLocalVersionInfoNoLock(),
                remoteVersion,
                _poolConfig.BootNetworkId,
                remoteNetworkId,
                requireStateBundleSchema);
        }
    }

    public void SeedPeers(IEnumerable<string> endpoints)
    {
        bool changed = false;
        lock (_sync)
        {
            foreach (string endpoint in endpoints)
            {
                changed |= UpsertPeerNoLock(
                    endpoint,
                    "configured",
                    null,
                    null,
                    persistStatusOnly: false,
                    allowSuppressed: true,
                    source: "configured",
                    isConfiguredSeed: true,
                    allowPrivate: true);
            }

            if (changed)
            {
                TrimPeerAddressBookNoLock(DateTime.UtcNow);
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void MergeDiscoveredPeers(IEnumerable<string> endpoints)
    {
        bool changed = false;
        lock (_sync)
        {
            foreach (string endpoint in endpoints)
            {
                changed |= UpsertPeerNoLock(
                    endpoint,
                    "discovered",
                    null,
                    null,
                    persistStatusOnly: false,
                    allowSuppressed: false,
                    source: "gossip",
                    allowPrivate: AllowPrivatePeerAdvertisements());
            }

            if (changed)
            {
                TrimPeerAddressBookNoLock(DateTime.UtcNow);
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void ReconcilePeerIdentity(string dialedEndpoint, string resolvedEndpoint, string nodeId)
    {
        string normalizedNodeId = NormalizePeerNodeId(nodeId);
        if (string.IsNullOrWhiteSpace(normalizedNodeId))
        {
            return;
        }

        TryNormalizePeerEndpoint(dialedEndpoint, allowPrivate: true, out string normalizedDialed, out _);
        TryNormalizePeerEndpoint(resolvedEndpoint, allowPrivate: true, out string normalizedResolved, out _);

        lock (_sync)
        {
            bool changed = false;
            foreach (BootPeerStatus peer in _state.Peers.Where(peer =>
                         string.Equals(NormalizePeerNodeId(peer.NodeId), normalizedNodeId, StringComparison.Ordinal) ||
                         (!string.IsNullOrWhiteSpace(normalizedDialed) &&
                          string.Equals(NormalizePeerEndpoint(peer.Endpoint), normalizedDialed, StringComparison.OrdinalIgnoreCase)) ||
                         (!string.IsNullOrWhiteSpace(normalizedResolved) &&
                          string.Equals(NormalizePeerEndpoint(peer.Endpoint), normalizedResolved, StringComparison.OrdinalIgnoreCase))))
            {
                if (!string.Equals(peer.NodeId, normalizedNodeId, StringComparison.Ordinal))
                {
                    peer.NodeId = normalizedNodeId;
                    changed = true;
                }
            }

            if (changed)
            {
                NormalizePeerAddressBookNoLock(DateTime.UtcNow);
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void AnnouncePeer(string endpoint)
    {
        if (!TryNormalizePeerEndpoint(endpoint, AllowPrivatePeerAdvertisements(), out string normalized, out _))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(normalized) ||
            (!string.IsNullOrWhiteSpace(GetSelfEndpoint()) &&
             string.Equals(normalized, GetSelfEndpoint(), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        bool changed = false;
        lock (_sync)
        {
            changed = UpsertPeerNoLock(
                normalized,
                "discovered",
                null,
                null,
                persistStatusOnly: false,
                allowSuppressed: false,
                source: "header",
                allowPrivate: AllowPrivatePeerAdvertisements());

            if (changed)
            {
                TrimPeerAddressBookNoLock(DateTime.UtcNow);
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void UpdatePeerHeartbeat(string endpoint, string status, double? latencyMs, DateTime lastSeenUtc)
    {
        lock (_sync)
        {
            bool changed = UpsertPeerNoLock(
                endpoint,
                status,
                latencyMs,
                lastSeenUtc,
                persistStatusOnly: true,
                allowSuppressed: true,
                source: "heartbeat",
                allowPrivate: true);
            BootPeerStatus? peer = FindPeerNoLock(endpoint);
            if (peer != null && !IsPeerFailureStatus(status))
            {
                peer.LastSuccessUtc = lastSeenUtc;
                changed = true;
            }

            if (peer != null && (peer.FailureCount != 0 || peer.LastFailureUtc.HasValue))
            {
                peer.FailureCount = 0;
                peer.LastFailureUtc = null;
                changed = true;
            }

            if (peer != null && string.Equals(status, "relayed", StringComparison.OrdinalIgnoreCase))
            {
                peer.RelaySuccessCount++;
                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void UpdatePeerSessionHeartbeat(string endpoint, string nodeId, string status, DateTime lastSeenUtc, double? latencyMs = null)
    {
        lock (_sync)
        {
            bool changed = UpsertPeerSessionNoLock(
                endpoint,
                nodeId,
                status,
                lastSeenUtc,
                sessionConnected: true,
                latencyMs: latencyMs);
            BootPeerStatus? peer = FindPeerByEndpointOrNodeNoLock(endpoint, nodeId);
            if (peer != null)
            {
                peer.LastSessionUtc = lastSeenUtc;
                peer.LastSuccessUtc = lastSeenUtc;
                peer.FailureCount = 0;
                peer.LastFailureUtc = null;
                peer.SessionSuccessCount++;
                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void UpdatePeerSessionClosed(string endpoint, string nodeId, string status, DateTime closedUtc)
    {
        lock (_sync)
        {
            bool changed = UpsertPeerSessionNoLock(
                endpoint,
                nodeId,
                status,
                closedUtc,
                sessionConnected: false);
            BootPeerStatus? peer = FindPeerByEndpointOrNodeNoLock(endpoint, nodeId);
            if (peer != null)
            {
                peer.LastSessionUtc = closedUtc;
                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void UpdatePeerUdpHeartbeat(string endpoint, string nodeId, string status, bool success, DateTime lastSeenUtc)
    {
        lock (_sync)
        {
            bool changed = UpsertPeerNoLock(
                endpoint,
                status,
                null,
                success ? lastSeenUtc : null,
                persistStatusOnly: true,
                allowSuppressed: true,
                source: "udp",
                allowPrivate: true);
            BootPeerStatus? peer = FindPeerNoLock(endpoint);
            if (peer != null)
            {
                if (!string.IsNullOrWhiteSpace(nodeId) && !string.Equals(peer.NodeId, nodeId, StringComparison.Ordinal))
                {
                    peer.NodeId = nodeId;
                    changed = true;
                }

                if (success)
                {
                    peer.LastSuccessUtc = lastSeenUtc;
                    peer.UdpRelaySuccessCount++;
                    peer.FailureCount = 0;
                    peer.LastFailureUtc = null;
                }
                else
                {
                    peer.UdpRelayFailureCount++;
                    peer.FailureCount++;
                    peer.LastFailureUtc = lastSeenUtc;
                }

                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void MarkPeerSessionFailure(string endpoint, string status)
    {
        MarkPeerSessionFailure(endpoint, string.Empty, status);
    }

    public void MarkPeerSessionFailure(string endpoint, string nodeId, string status)
    {
        lock (_sync)
        {
            bool changed = UpsertPeerSessionNoLock(
                endpoint,
                nodeId,
                status,
                DateTime.UtcNow,
                sessionConnected: false,
                allowSuppressed: false);
            BootPeerStatus? peer = FindPeerByEndpointOrNodeNoLock(endpoint, nodeId);
            if (peer != null)
            {
                peer.SessionFailureCount++;
                peer.FailureCount++;
                peer.LastFailureUtc = DateTime.UtcNow;
                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void MarkPeerFailure(string endpoint, string status)
    {
        lock (_sync)
        {
            bool changed = UpsertPeerNoLock(
                endpoint,
                status,
                null,
                null,
                persistStatusOnly: true,
                allowSuppressed: false,
                source: "failure",
                allowPrivate: true);
            BootPeerStatus? peer = FindPeerNoLock(endpoint);
            if (peer != null)
            {
                peer.FailureCount++;
                peer.LastFailureUtc = DateTime.UtcNow;
                if (status.StartsWith("relay-", StringComparison.OrdinalIgnoreCase))
                {
                    peer.RelayFailureCount++;
                }
                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void UpdatePeerNetworkSnapshot(string endpoint, string? currentStateId, string? candidateStateId, string? tipBlockHash)
    {
        lock (_sync)
        {
            BootPeerStatus? peer = FindPeerNoLock(endpoint);
            if (peer == null)
            {
                return;
            }

            string nextCurrentStateId = currentStateId ?? string.Empty;
            string nextCandidateStateId = candidateStateId ?? string.Empty;
            string nextTipBlockHash = tipBlockHash ?? string.Empty;
            bool changed =
                !string.Equals(peer.LastCurrentStateId, nextCurrentStateId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(peer.LastCandidateStateId, nextCandidateStateId, StringComparison.OrdinalIgnoreCase) ||
                !BitcoinHashes.AreEquivalent(peer.LastTipBlockHash, nextTipBlockHash);
            peer.LastCurrentStateId = nextCurrentStateId;
            peer.LastCandidateStateId = nextCandidateStateId;
            peer.LastTipBlockHash = nextTipBlockHash;
            peer.LastSuccessUtc = DateTime.UtcNow;
            if (changed)
            {
                RequestDeferredSaveNoLock();
            }
        }
    }

    public void UpdatePeerCompatibility(string endpoint, BootVersionCompatibilityDto compatibility, DateTime observedUtc)
    {
        lock (_sync)
        {
            BootPeerStatus? peer = FindPeerNoLock(endpoint);
            if (peer == null)
            {
                return;
            }

            peer.RemoteVersion = CloneVersionInfo(compatibility.RemoteVersion);
            peer.CompatibilityStatus = compatibility.Status;
            peer.CompatibilityReason = compatibility.Reason;
            peer.CompatibilityWarnings = compatibility.Warnings.ToList();
            peer.LastSeenUtc = observedUtc;
            RequestDeferredSaveNoLock();
        }
    }

    public bool TombstonePeer(string endpoint)
    {
        if (!TryNormalizePeerEndpoint(endpoint, allowPrivate: true, out string normalized, out _))
        {
            return false;
        }

        string selfEndpoint = GetSelfEndpoint();
        if (!string.IsNullOrWhiteSpace(selfEndpoint) &&
            string.Equals(normalized, selfEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (_sync)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime tombstonedUntilUtc = nowUtc.AddSeconds(GetPeerTombstoneSeconds());
            BootPeerStatus? peer = FindPeerNoLock(normalized);
            if (peer == null)
            {
                peer = new BootPeerStatus
                {
                    Endpoint = normalized,
                    DiscoveredUtc = nowUtc,
                    Source = "admin"
                };
                _state.Peers.Add(peer);
            }

            peer.Status = "tombstoned";
            peer.TombstonedUntilUtc = tombstonedUntilUtc;
            peer.SuppressedUntilUtc = tombstonedUntilUtc;
            peer.LastFailureUtc = nowUtc;
            SuppressPeerNoLock(normalized, tombstonedUntilUtc);
            RecordNetworkEventNoLock(
                "peer-tombstoned",
                "admin",
                $"Manually removed peer endpoint: {normalized}.",
                _state.CurrentTipBlockHash,
                _state.CurrentTipBlockHeight,
                DateTime.UtcNow);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();
            return true;
        }
    }

    public int PruneStalePeers(
        DateTime nowUtc,
        TimeSpan pruneAfter,
        int minimumFailureCount,
        IReadOnlyCollection<string> protectedEndpoints)
    {
        if (pruneAfter <= TimeSpan.Zero)
        {
            return 0;
        }

        HashSet<string> protectedSet = protectedEndpoints
            .Select(NormalizePeerEndpoint)
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string selfEndpoint = GetSelfEndpoint();
        if (!string.IsNullOrWhiteSpace(selfEndpoint))
        {
            protectedSet.Add(selfEndpoint);
        }

        DateTime cutoffUtc = nowUtc - pruneAfter;
        List<string> removedEndpoints;
        lock (_sync)
        {
            removedEndpoints = _state.Peers
                .Where(peer => ShouldPrunePeerNoLock(peer, cutoffUtc, minimumFailureCount, protectedSet))
                .Select(peer => peer.Endpoint)
                .ToList();
            if (removedEndpoints.Count == 0)
            {
                return 0;
            }

            _state.Peers = _state.Peers
                .Where(peer => !removedEndpoints.Any(removed =>
                    string.Equals(NormalizePeerEndpoint(removed), NormalizePeerEndpoint(peer.Endpoint), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            DateTime suppressUntilUtc = nowUtc + pruneAfter;
            foreach (string removedEndpoint in removedEndpoints)
            {
                SuppressPeerNoLock(removedEndpoint, suppressUntilUtc);
            }

            RecordNetworkEventNoLock(
                "peer-pruned",
                "peer-sync",
                $"Pruned stale peer endpoint(s): {string.Join(", ", removedEndpoints)}.",
                _state.CurrentTipBlockHash,
                _state.CurrentTipBlockHeight,
                nowUtc);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();
        }

        return removedEndpoints.Count;
    }

    public async Task<ShareRecordingResult> RecordShareAsync(RecordedShareSubmission share)
    {
        DateTime arrivalUtc = DateTime.UtcNow;
        DateTime transportReceivedUtc = share.TransportReceivedUtc ?? arrivalUtc;
        var validationStopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();
        double snapshotReadDurationMs;
        double shareCoreValidationDurationMs;
        double stateMutationDurationMs = 0;
        double snapshotReadLockWaitDurationMs = 0;
        double snapshotReadLockBodyDurationMs = 0;
        double stateMutationLockWaitDurationMs = 0;
        double stateMutationLockBodyDurationMs = 0;
        List<PayoutInfo> winnersSnapshot;
        string currentStateSnapshot;
        List<BootPayoutSnapshotContext> snapshotContextsSnapshot;
        List<string> acceptedParentBlockHashesSnapshot;
        var snapshotLockWaitStopwatch = Stopwatch.StartNew();
        lock (_sync)
        {
            snapshotLockWaitStopwatch.Stop();
            snapshotReadLockWaitDurationMs = snapshotLockWaitStopwatch.Elapsed.TotalMilliseconds;
            var snapshotLockBodyStopwatch = Stopwatch.StartNew();
            winnersSnapshot = ClonePayouts(_state.WinnersList);
            currentStateSnapshot = _state.CurrentStateId;
            snapshotContextsSnapshot = ClonePreferredSnapshotContextsNoLock(share.PayoutSnapshotId);
            acceptedParentBlockHashesSnapshot = GetAcceptedParentBlockHashesNoLock();
            snapshotLockBodyStopwatch.Stop();
            snapshotReadLockBodyDurationMs = snapshotLockBodyStopwatch.Elapsed.TotalMilliseconds;
        }
        snapshotReadDurationMs = snapshotReadLockWaitDurationMs + snapshotReadLockBodyDurationMs;

        BootShareHeaderEvaluationResult headerEvaluation = _shareVerifier.EvaluateHeaderDifficulty(share);
        DateTime difficultyCheckedUtc = DateTime.UtcNow;
        if (headerEvaluation.IsValid)
        {
            MaybeRelayOptimisticShare(share, headerEvaluation, transportReceivedUtc, arrivalUtc, difficultyCheckedUtc);
        }

        stageStopwatch.Restart();
        SnapshotValidationResult snapshotValidation = ValidateShareAgainstKnownSnapshots(
            share,
            winnersSnapshot,
            snapshotContextsSnapshot,
            acceptedParentBlockHashesSnapshot);
        if (!snapshotValidation.Validation.IsValid &&
            ShouldRetryShareValidationWithAllSnapshotContexts(share.PayoutSnapshotId, snapshotValidation.Validation.RejectionReason))
        {
            lock (_sync)
            {
                snapshotContextsSnapshot = _state.SnapshotContexts.Select(CloneSnapshotContext).ToList();
            }

            snapshotValidation = ValidateShareAgainstKnownSnapshots(
                share,
                winnersSnapshot,
                snapshotContextsSnapshot,
                acceptedParentBlockHashesSnapshot);
        }

        BootShareValidationResult validation = snapshotValidation.Validation;
        string matchedSnapshotId = snapshotValidation.SnapshotId;
        if (!validation.IsValid && IsWrongParentRejection(validation.RejectionReason))
        {
            if (ShouldRetryShareValidationWithAllSnapshotContexts(share.PayoutSnapshotId, validation.RejectionReason))
            {
                lock (_sync)
                {
                    snapshotContextsSnapshot = _state.SnapshotContexts.Select(CloneSnapshotContext).ToList();
                }
            }

            SnapshotValidationResult freshParentSnapshotValidation = ValidateShareAgainstKnownSnapshotsIgnoringParent(
                share,
                winnersSnapshot,
                snapshotContextsSnapshot);
            BootShareValidationResult freshParentValidation = freshParentSnapshotValidation.Validation;
            if (IsTrustedFreshParentSource(share.Source))
            {
                if (!freshParentValidation.IsValid)
                {
                    RecordFreshParentRetryEvent(
                        "fresh-parent-retry-failed",
                        share.Source,
                        share.PrevBlockHash,
                        $"Fresh-parent retry failed validation after ignoring parent mismatch: {freshParentValidation.RejectionReason ?? "Unknown reason"}.");
                    validation = freshParentValidation;
                }
                else if (freshParentValidation.Difficulty < 1)
                {
                    RecordFreshParentRetryEvent(
                        "fresh-parent-retry-low-difficulty",
                        share.Source,
                        freshParentValidation.PrevBlockHash,
                        $"Fresh-parent retry validated but computed difficulty {freshParentValidation.Difficulty.ToString("F2", CultureInfo.InvariantCulture)} was below the floor.");
                    validation = freshParentValidation;
                }
                else
                {
                    RecordFreshParentRetryEvent(
                        "fresh-parent-rejected",
                        share.Source,
                        freshParentValidation.PrevBlockHash,
                        "A mining share cannot establish a new Bitcoin parent; local Bitcoin notification authority must validate the tip first.");
                    validation = RejectValidatedShare(
                        freshParentValidation,
                        "Fresh proof parent is outside the active Bitcoin tip; the attached Bitcoin source must validate it before direct ingress.");
                }
            }
        }

        lock (_sync)
        {
            if (validation.IsValid && ShouldQuarantinePreviousParentNoLock(validation.PrevBlockHash, DateTime.UtcNow))
            {
                validation = RejectValidatedShare(
                    validation,
                    "Previous-parent proof quarantined after the provisional peer-tip boundary.");
            }
            else if (validation.IsValid &&
                     IsNewDirectIngressOutsideActiveParentNoLock(validation.ShareId, validation.PrevBlockHash))
            {
                validation = RejectValidatedShare(
                    validation,
                    "Fresh proof parent is outside the active Bitcoin tip; retained proofs may be revalidated only through state reconciliation.");
            }
        }
        shareCoreValidationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
        DateTime validationCompletedUtc = DateTime.UtcNow;

        validationStopwatch.Stop();
        double validationDurationMs = validationStopwatch.Elapsed.TotalMilliseconds;

        ShareRecordingResult AttachTimings(ShareRecordingResult result)
        {
            result.SnapshotReadDurationMs = snapshotReadDurationMs;
            result.SnapshotReadLockWaitDurationMs = snapshotReadLockWaitDurationMs;
            result.SnapshotReadLockBodyDurationMs = snapshotReadLockBodyDurationMs;
            result.ShareCoreValidationDurationMs = shareCoreValidationDurationMs;
            result.StateMutationDurationMs = stateMutationDurationMs;
            result.StateMutationLockWaitDurationMs = stateMutationLockWaitDurationMs;
            result.StateMutationLockBodyDurationMs = stateMutationLockBodyDurationMs;
            result.TransportReceivedUtc = transportReceivedUtc;
            result.StateServiceReceivedUtc = arrivalUtc;
            result.DifficultyCheckedUtc = difficultyCheckedUtc;
            result.ValidationCompletedUtc = validationCompletedUtc;
            return result;
        }

        if (!validation.IsValid)
        {
            BootNetworkStatusDto networkStatus;
            DateTime nowUtc = DateTime.UtcNow;
            stageStopwatch.Restart();
            var mutationLockWaitStopwatch = Stopwatch.StartNew();
            lock (_sync)
            {
                mutationLockWaitStopwatch.Stop();
                stateMutationLockWaitDurationMs += mutationLockWaitStopwatch.Elapsed.TotalMilliseconds;
                var mutationLockBodyStopwatch = Stopwatch.StartNew();
                RecordShareDiagnosticNoLock(
                    share.Source,
                    share.MinerAddress,
                    string.IsNullOrWhiteSpace(share.Username) ? share.MinerAddress : share.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    validation.RejectionReason,
                    share.Difficulty,
                    nowUtc);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    validation.ShareId,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: validation.RejectionReason ?? "Invalid share",
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc,
                    proofClass: ResolveProofClass(share.ProofClass),
                    relayStage: ResolveRelayStage(share.RelayStage),
                    transportReceivedUtc: transportReceivedUtc,
                    stateServiceReceivedUtc: arrivalUtc,
                    difficultyCheckedUtc: difficultyCheckedUtc,
                    validationCompletedUtc: validationCompletedUtc,
                    stateMutationCompletedUtc: DateTime.UtcNow);
                RequestDeferredHistorySaveNoLock();
                networkStatus = BuildNetworkStatusNoLock();
                mutationLockBodyStopwatch.Stop();
                stateMutationLockBodyDurationMs += mutationLockBodyStopwatch.Elapsed.TotalMilliseconds;
            }
            stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            _logger.LogInformation(
                "Rejected {Source} share from {MinerAddress}: {Reason}",
                string.IsNullOrWhiteSpace(share.Source) ? "unknown" : share.Source,
                share.MinerAddress,
                validation.RejectionReason ?? "Invalid share");
            return AttachTimings(new ShareRecordingResult
            {
                Accepted = false,
                RejectionReason = validation.RejectionReason ?? "Invalid share",
                BestShare = GetBestShare(),
                OnDeckList = GetOnDeckList(),
                NetworkStatus = networkStatus
            });
        }

        if (validation.Difficulty < 1)
        {
            BootNetworkStatusDto networkStatus;
            DateTime nowUtc = DateTime.UtcNow;
            stageStopwatch.Restart();
            var mutationLockWaitStopwatch = Stopwatch.StartNew();
            lock (_sync)
            {
                mutationLockWaitStopwatch.Stop();
                stateMutationLockWaitDurationMs += mutationLockWaitStopwatch.Elapsed.TotalMilliseconds;
                var mutationLockBodyStopwatch = Stopwatch.StartNew();
                RecordShareDiagnosticNoLock(
                    share.Source,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    "Low difficulty",
                    validation.Difficulty,
                    nowUtc);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    validation.ShareId,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Low difficulty",
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc,
                    proofClass: ResolveProofClass(share.ProofClass),
                    relayStage: ResolveRelayStage(share.RelayStage),
                    transportReceivedUtc: transportReceivedUtc,
                    stateServiceReceivedUtc: arrivalUtc,
                    difficultyCheckedUtc: difficultyCheckedUtc,
                    validationCompletedUtc: validationCompletedUtc,
                    stateMutationCompletedUtc: DateTime.UtcNow);
                RequestDeferredHistorySaveNoLock();
                networkStatus = BuildNetworkStatusNoLock();
                mutationLockBodyStopwatch.Stop();
                stateMutationLockBodyDurationMs += mutationLockBodyStopwatch.Elapsed.TotalMilliseconds;
            }
            stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            _logger.LogInformation(
                "Rejected {Source} share from {MinerAddress}: low difficulty ({Difficulty})",
                string.IsNullOrWhiteSpace(share.Source) ? "unknown" : share.Source,
                share.MinerAddress,
                validation.Difficulty);
            return AttachTimings(new ShareRecordingResult
            {
                Accepted = false,
                RejectionReason = "Low difficulty",
                ComputedDifficulty = validation.Difficulty,
                BestShare = GetBestShare(),
                OnDeckList = GetOnDeckList(),
                NetworkStatus = networkStatus
            });
        }

        ShareRecordingResult result;
        bool shouldRelay = false;
        bool shouldNotifyNetwork = false;
        stageStopwatch.Restart();
        var acceptedMutationLockWaitStopwatch = Stopwatch.StartNew();
        lock (_sync)
        {
            acceptedMutationLockWaitStopwatch.Stop();
            stateMutationLockWaitDurationMs += acceptedMutationLockWaitStopwatch.Elapsed.TotalMilliseconds;
            var acceptedMutationLockBodyStopwatch = Stopwatch.StartNew();
            if (!string.Equals(currentStateSnapshot, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase) &&
                !HasSnapshotContextNoLock(matchedSnapshotId))
            {
                RecordShareDiagnosticNoLock(
                    share.Source,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    "Round changed during validation",
                    validation.Difficulty,
                    DateTime.UtcNow);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    validation.ShareId,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Round changed during validation",
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc);
                RequestDeferredHistorySaveNoLock();
                stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
                return AttachTimings(new ShareRecordingResult
                {
                    Accepted = false,
                    RejectionReason = "Round changed during validation",
                    ComputedDifficulty = validation.Difficulty,
                    BestShare = CloneBestShare(_state.BestShare),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                });
            }

            if (!IsAcceptedParentBlockHashNoLock(validation.PrevBlockHash))
            {
                RecordShareDiagnosticNoLock(
                    share.Source,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    "Accepted parent set changed during validation",
                    validation.Difficulty,
                    DateTime.UtcNow);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    validation.ShareId,
                    validation.MinerAddress,
                    string.IsNullOrWhiteSpace(validation.Username) ? validation.MinerAddress : validation.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Accepted parent set changed during validation",
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc);
                RequestDeferredHistorySaveNoLock();
                stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
                return AttachTimings(new ShareRecordingResult
                {
                    Accepted = false,
                    RejectionReason = "Accepted parent set changed during validation",
                    ComputedDifficulty = validation.Difficulty,
                    BestShare = CloneBestShare(_state.BestShare),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                });
            }

            BootShareProof proof = CreateProofNoLock(validation, share.Source);
            proof.PayoutSnapshotId = string.IsNullOrWhiteSpace(matchedSnapshotId)
                ? _state.ActiveSnapshotId
                : matchedSnapshotId;
            proof.TransportReceivedUtc = transportReceivedUtc;
            proof.StateServiceReceivedUtc = arrivalUtc;
            proof.DifficultyCheckedUtc = difficultyCheckedUtc;
            proof.ValidationCompletedUtc = validationCompletedUtc;

            int insertIndex = 0;

            while (insertIndex < _state.OnDeckProofs.Count &&
                   _state.OnDeckProofs[insertIndex].Difficulty >= proof.Difficulty)
            {
                insertIndex++;
            }

            bool affectedOnDeck = insertIndex < _poolConfig.WorkSetReserveLimit;
            bool pulseAccepted = !affectedOnDeck && _poolConfig.EnablePulseProofs;
            string proofClass = pulseAccepted ? BootProofClasses.Pulse : BootProofClasses.Work;
            proof.ProofClass = proofClass;
            proof.RelayStage = BootRelayStages.Validated;
            bool peerSource = BootPeerSource.TryParsePeerSource(share.Source, out _, out _);
            bool localGatewaySource =
                string.Equals(share.Source, "datum", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(share.Source, "sv2", StringComparison.OrdinalIgnoreCase);
            proof.RelayTtl = pulseAccepted
                ? peerSource
                    ? Math.Max(0, share.RelayTtl - 1)
                    : Math.Max(1, _poolConfig.PulseRelayTtl)
                : 0;

            bool pulseBelowFloor = pulseAccepted && proof.Difficulty < Math.Max(1d, _poolConfig.PulseMinDifficulty);
            if (pulseBelowFloor && !localGatewaySource)
            {
                RecordShareDiagnosticNoLock(
                    share.Source,
                    proof.MinerAddress,
                    string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    "Below pulse floor",
                    validation.Difficulty,
                    proof.Timestamp);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    proof.ShareId,
                    proof.MinerAddress,
                    string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Below pulse floor",
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc,
                    proofClass: BootProofClasses.Pulse,
                    relayStage: BootRelayStages.Validated,
                    transportReceivedUtc: transportReceivedUtc,
                    stateServiceReceivedUtc: arrivalUtc,
                    difficultyCheckedUtc: difficultyCheckedUtc,
                    validationCompletedUtc: validationCompletedUtc,
                    stateMutationCompletedUtc: DateTime.UtcNow);
                RequestDeferredHistorySaveNoLock();
                stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
                return AttachTimings(new ShareRecordingResult
                {
                    Accepted = false,
                    RejectionReason = "Below pulse floor",
                    ProofClass = BootProofClasses.Pulse,
                    RelayStage = BootRelayStages.Validated,
                    ComputedDifficulty = validation.Difficulty,
                    BestShare = CloneBestShare(_state.BestShare),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                });
            }
            if (pulseBelowFloor)
            {
                proof.RelayTtl = 0;
            }

            string pulseLimitReason = string.Empty;
            bool pulseRelayRateLimited = pulseAccepted &&
                !TryConsumePulseRateLimitNoLock(share.Source, proof.MinerAddress, proof.Timestamp, out pulseLimitReason);
            if (pulseRelayRateLimited && !localGatewaySource)
            {
                RecordShareDiagnosticNoLock(
                    share.Source,
                    proof.MinerAddress,
                    string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    pulseLimitReason,
                    validation.Difficulty,
                    proof.Timestamp);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    proof.ShareId,
                    proof.MinerAddress,
                    string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: pulseLimitReason,
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc,
                    proofClass: BootProofClasses.Pulse,
                    relayStage: BootRelayStages.Validated,
                    transportReceivedUtc: transportReceivedUtc,
                    stateServiceReceivedUtc: arrivalUtc,
                    difficultyCheckedUtc: difficultyCheckedUtc,
                    validationCompletedUtc: validationCompletedUtc,
                    stateMutationCompletedUtc: DateTime.UtcNow);
                RequestDeferredHistorySaveNoLock();
                stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
                return AttachTimings(new ShareRecordingResult
                {
                    Accepted = false,
                    RejectionReason = pulseLimitReason,
                    ProofClass = BootProofClasses.Pulse,
                    RelayStage = BootRelayStages.Validated,
                    ComputedDifficulty = validation.Difficulty,
                    BestShare = CloneBestShare(_state.BestShare),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                });
            }
            if (pulseRelayRateLimited)
            {
                proof.RelayTtl = 0;
            }

            if (!RememberShareIdNoLock(proof.ShareId))
            {
                RecordShareDiagnosticNoLock(
                    share.Source,
                    proof.MinerAddress,
                    string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    "Duplicate share",
                    validation.Difficulty,
                    proof.Timestamp);
                RecordPeerRelayObservationNoLock(
                    share.Source,
                    proof.ShareId,
                    proof.MinerAddress,
                    string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Duplicate share",
                    difficulty: validation.Difficulty,
                    payloadBytes: share.PayloadBytes,
                    validationDurationMs: validationDurationMs,
                    timestampUtc: arrivalUtc,
                    proofClass: proofClass,
                    relayStage: BootRelayStages.Validated,
                    transportReceivedUtc: transportReceivedUtc,
                    stateServiceReceivedUtc: arrivalUtc,
                    difficultyCheckedUtc: difficultyCheckedUtc,
                    validationCompletedUtc: validationCompletedUtc,
                    stateMutationCompletedUtc: DateTime.UtcNow);
                RequestDeferredHistorySaveNoLock();
                stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
                return AttachTimings(new ShareRecordingResult
                {
                    Accepted = false,
                    RejectionReason = "Duplicate share",
                    ProofClass = proofClass,
                    RelayStage = BootRelayStages.Validated,
                    ComputedDifficulty = validation.Difficulty,
                    IsBlock = validation.IsBlock,
                    BlockHash = validation.BlockHash,
                    AcceptedProof = CloneProof(proof),
                    BestShare = CloneBestShare(_state.BestShare),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                });
            }

            if (affectedOnDeck)
            {
                _state.OnDeckProofs.Insert(insertIndex, proof);

                _state.OnDeckProofs = _state.OnDeckProofs
                    .OrderByDescending(x => x.Difficulty)
                    .ThenBy(x => x.ShareId, StringComparer.Ordinal)
                    .ToList();

                while (_state.OnDeckProofs.Count > _poolConfig.WorkSetReserveLimit)
                {
                    _state.OnDeckProofs.RemoveAt(_state.OnDeckProofs.Count - 1);
                }

                RebuildOnDeckListNoLock();
            }

            bool newRecord = false;
            if (!pulseAccepted && proof.Difficulty > _state.BestShare.Difficulty)
            {
                _state.BestShare = new BestShareRecord
                {
                    Difficulty = proof.Difficulty,
                    MinerAddress = proof.Username,
                    Timestamp = proof.Timestamp
                };
                newRecord = true;
            }

            if (!pulseAccepted)
            {
                RecordAcceptedShareTelemetryNoLock(proof);
            }
            else if (!BootPeerSource.TryParsePeerSource(share.Source, out _, out _, out _))
            {
                _peerLoopHealth.RecordLocalPulse(proof.Timestamp);
            }
            RecordShareDiagnosticNoLock(
                share.Source,
                proof.MinerAddress,
                string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                accepted: true,
                affectedOnDeck: affectedOnDeck,
                rejectionReason: null,
                difficulty: validation.Difficulty,
                timestampUtc: proof.Timestamp);
            DateTime stateMutationCompletedUtc = DateTime.UtcNow;
            proof.StateMutationCompletedUtc = stateMutationCompletedUtc;
            RecordPeerRelayObservationNoLock(
                share.Source,
                proof.ShareId,
                proof.MinerAddress,
                string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
                accepted: true,
                affectedOnDeck: affectedOnDeck,
                rejectionReason: null,
                difficulty: validation.Difficulty,
                payloadBytes: share.PayloadBytes,
                validationDurationMs: validationDurationMs,
                timestampUtc: arrivalUtc,
                proofClass: proofClass,
                relayStage: BootRelayStages.Validated,
                transportReceivedUtc: transportReceivedUtc,
                stateServiceReceivedUtc: arrivalUtc,
                difficultyCheckedUtc: difficultyCheckedUtc,
                validationCompletedUtc: validationCompletedUtc,
                stateMutationCompletedUtc: stateMutationCompletedUtc);
            bool capturedHashrateSample = !pulseAccepted && MaybeCaptureHashrateSampleNoLock(proof.Timestamp, force: false);
            if (affectedOnDeck)
            {
                _state.CandidateStateId = ComputeCandidateStateIdNoLock();
                CacheCurrentCandidateBundleNoLock();
                RequestDeferredSaveNoLock();
            }

            RequestDeferredHistorySaveNoLock();
            bool notifyNetwork = newRecord || affectedOnDeck || capturedHashrateSample;

            result = new ShareRecordingResult
            {
                Accepted = true,
                ProofClass = proofClass,
                RelayStage = BootRelayStages.Validated,
                PulseAccepted = pulseAccepted,
                AffectedConsensusState = affectedOnDeck,
                AffectedOnDeck = affectedOnDeck,
                NewRecord = newRecord,
                ComputedDifficulty = validation.Difficulty,
                IsBlock = validation.IsBlock,
                BlockHash = validation.BlockHash,
                BestShare = newRecord ? CloneBestShare(_state.BestShare) : new BestShareRecord(),
                OnDeckList = affectedOnDeck ? ClonePayouts(_state.OnDeckList) : [],
                NetworkStatus = notifyNetwork ? BuildNetworkStatusNoLock() : new BootNetworkStatusDto(),
                AcceptedProof = CloneProof(proof),
                TransportReceivedUtc = transportReceivedUtc,
                StateServiceReceivedUtc = arrivalUtc,
                DifficultyCheckedUtc = difficultyCheckedUtc,
                ValidationCompletedUtc = validationCompletedUtc,
                StateMutationCompletedUtc = stateMutationCompletedUtc
            };

            shouldRelay = affectedOnDeck || (pulseAccepted && proof.RelayTtl > 0);
            shouldNotifyNetwork = notifyNetwork;
            acceptedMutationLockBodyStopwatch.Stop();
            stateMutationLockBodyDurationMs += acceptedMutationLockBodyStopwatch.Elapsed.TotalMilliseconds;
        }
        stateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
        result = AttachTimings(result);

        if (result.NewRecord)
        {
            QueueRealtimeSend(_hubContext.Clients.All.SendAsync("UpdateRecord", result.BestShare), "UpdateRecord");
        }

        if (result.AffectedOnDeck)
        {
            QueueRealtimeSend(_hubContext.Clients.All.SendAsync("UpdateOnDeck", result.OnDeckList), "UpdateOnDeck");
        }

        if (shouldNotifyNetwork)
        {
            QueueRealtimeSend(_hubContext.Clients.All.SendAsync("UpdateNetworkState", result.NetworkStatus), "UpdateNetworkState");
        }
        if (shouldRelay && result.AcceptedProof != null)
        {
            _peerLoopHealth.RecordShareQueued();
            await _acceptedShares.Writer.WriteAsync(result.AcceptedProof);
        }
        return result;
    }

    private void MaybeRelayOptimisticShare(
        RecordedShareSubmission share,
        BootShareHeaderEvaluationResult headerEvaluation,
        DateTime transportReceivedUtc,
        DateTime stateServiceReceivedUtc,
        DateTime difficultyCheckedUtc)
    {
        if (!_poolConfig.EnableOptimisticShareRelay ||
            !headerEvaluation.IsValid ||
            !string.Equals(ResolveProofClass(share.ProofClass), BootProofClasses.Work, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ResolveRelayStage(share.RelayStage), BootRelayStages.Optimistic, StringComparison.OrdinalIgnoreCase) ||
            BootPeerSource.TryParsePeerSource(share.Source, out _, out _))
        {
            return;
        }

        double minimumDifficulty;
        lock (_sync)
        {
            if (!IsAcceptedParentBlockHashNoLock(headerEvaluation.PrevBlockHash) ||
                ShouldQuarantinePreviousParentNoLock(headerEvaluation.PrevBlockHash, DateTime.UtcNow))
            {
                return;
            }

            minimumDifficulty = Math.Max(GetWorkSetAdmissionDifficultyNoLock(), _poolConfig.MinOptimisticRelayDifficulty);
            if (headerEvaluation.Difficulty < minimumDifficulty)
            {
                return;
            }

            if (!_optimisticRelayedShareIds.Add(headerEvaluation.ShareId))
            {
                return;
            }

            if (_optimisticRelayedShareIds.Count > MaxSeenShareIds)
            {
                _optimisticRelayedShareIds.Clear();
                _optimisticRelayedShareIds.Add(headerEvaluation.ShareId);
            }
        }

        var proof = new BootShareProof
        {
            ShareId = headerEvaluation.ShareId,
            MinerAddress = share.MinerAddress,
            Username = string.IsNullOrWhiteSpace(share.Username) ? share.MinerAddress : share.Username,
            HeaderHex = headerEvaluation.HeaderHex,
            CoinbaseHex = headerEvaluation.CoinbaseHex,
            MerklePath = share.MerklePath.ToList(),
            PayoutSnapshotId = share.PayoutSnapshotId,
            PrevBlockHash = headerEvaluation.PrevBlockHash,
            Difficulty = headerEvaluation.Difficulty,
            DiffString = ClientHandler.FormatDifficulty(headerEvaluation.Difficulty),
            Source = share.Source,
            Timestamp = stateServiceReceivedUtc,
            ProofClass = BootProofClasses.Work,
            RelayStage = BootRelayStages.Optimistic,
            TransportReceivedUtc = transportReceivedUtc,
            StateServiceReceivedUtc = stateServiceReceivedUtc,
            DifficultyCheckedUtc = difficultyCheckedUtc
        };

        if (_acceptedShares.Writer.TryWrite(proof))
        {
            _peerLoopHealth.RecordShareQueued();
        }
    }

    private bool TryConsumePulseRateLimitNoLock(string source, string minerAddress, DateTime nowUtc, out string reason)
    {
        reason = string.Empty;
        if (!_poolConfig.EnablePulseProofs)
        {
            return false;
        }

        DateTime cutoffUtc = nowUtc.AddMinutes(-1);
        string peerKey = ResolvePulsePeerKey(source);
        if (!string.IsNullOrWhiteSpace(peerKey) &&
            !TryConsumePulseBucketNoLock(_recentPulseByPeer, peerKey, cutoffUtc, nowUtc, Math.Max(1, _poolConfig.PulseMaxPerPeerPerMinute)))
        {
            reason = "Pulse rate limited";
            return false;
        }

        string addressKey = BitcoinScript.NormalizeAddress(minerAddress);
        if (!string.IsNullOrWhiteSpace(addressKey) &&
            !TryConsumePulseBucketNoLock(_recentPulseByAddress, addressKey, cutoffUtc, nowUtc, Math.Max(1, _poolConfig.PulseMaxPerSourceAddressPerMinute)))
        {
            reason = "Pulse rate limited";
            return false;
        }

        return true;
    }

    private static bool TryConsumePulseBucketNoLock(
        Dictionary<string, Queue<DateTime>> buckets,
        string key,
        DateTime cutoffUtc,
        DateTime nowUtc,
        int limit)
    {
        if (!buckets.TryGetValue(key, out Queue<DateTime>? bucket))
        {
            bucket = new Queue<DateTime>();
            buckets[key] = bucket;
        }

        while (bucket.Count > 0 && bucket.Peek() < cutoffUtc)
        {
            bucket.Dequeue();
        }

        if (bucket.Count >= limit)
        {
            return false;
        }

        bucket.Enqueue(nowUtc);
        return true;
    }

    private static string ResolvePulsePeerKey(string source)
    {
        return BootPeerSource.TryParsePeerSource(source, out _, out string endpoint, out string nodeId)
            ? string.IsNullOrWhiteSpace(endpoint) ? nodeId : NormalizePeerEndpoint(endpoint)
            : source.Trim();
    }

    private SnapshotValidationResult ValidateShareAgainstKnownSnapshots(
        RecordedShareSubmission share,
        IReadOnlyList<PayoutInfo> currentWinners,
        IReadOnlyCollection<BootPayoutSnapshotContext> snapshotContexts,
        IReadOnlyCollection<string> expectedPrevBlockHashes)
    {
        List<BootPayoutSnapshotContext> orderedContexts = snapshotContexts
            .Where(context => !string.IsNullOrWhiteSpace(context.SnapshotId))
            .OrderByDescending(context => context.CreatedAtUtc)
            .ToList();

        bool hasPreferredSnapshot = !string.IsNullOrWhiteSpace(share.PayoutSnapshotId);
        List<BootPayoutSnapshotContext> exactContexts = hasPreferredSnapshot
            ? orderedContexts
                .Where(context => string.Equals(context.SnapshotId, share.PayoutSnapshotId, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        BootShareValidationResult firstFailure = InvalidValidationResult();
        if (hasPreferredSnapshot)
        {
            foreach (BootPayoutSnapshotContext context in exactContexts)
            {
                foreach (List<PayoutInfo> payoutVariant in GetSnapshotPayoutVariants(context))
                {
                    BootShareValidationResult validation = _shareVerifier.ValidateShare(share, payoutVariant, expectedPrevBlockHashes);
                    if (validation.IsValid)
                    {
                        return new SnapshotValidationResult(validation, context.SnapshotId);
                    }

                    firstFailure = PreferInformativeFailure(firstFailure, validation);
                }
            }
        }

        BootShareValidationResult currentValidation = _shareVerifier.ValidateShare(share, currentWinners, expectedPrevBlockHashes);
        if (currentValidation.IsValid)
        {
            string snapshotId = orderedContexts.FirstOrDefault(context => WinnersMatch(context.WinnersList, currentWinners))?.SnapshotId
                ?? string.Empty;
            return new SnapshotValidationResult(currentValidation, snapshotId);
        }

        firstFailure = PreferInformativeFailure(firstFailure, currentValidation);
        if (IsSingleRecipientFallbackRejection(currentValidation.RejectionReason))
        {
            return new SnapshotValidationResult(currentValidation, string.Empty);
        }

        foreach (BootPayoutSnapshotContext context in orderedContexts.Where(context =>
                     !hasPreferredSnapshot ||
                     !string.Equals(context.SnapshotId, share.PayoutSnapshotId, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (List<PayoutInfo> payoutVariant in GetSnapshotPayoutVariants(context))
            {
                BootShareValidationResult validation = _shareVerifier.ValidateShare(share, payoutVariant, expectedPrevBlockHashes);
                if (validation.IsValid)
                {
                    return new SnapshotValidationResult(validation, context.SnapshotId);
                }

                firstFailure = PreferInformativeFailure(firstFailure, validation);
            }
        }

        return new SnapshotValidationResult(firstFailure, string.Empty);
    }

    private List<BootPayoutSnapshotContext> ClonePreferredSnapshotContextsNoLock(string? payoutSnapshotId)
    {
        if (string.IsNullOrWhiteSpace(payoutSnapshotId))
        {
            return [];
        }

        return _state.SnapshotContexts
            .Where(context => string.Equals(context.SnapshotId, payoutSnapshotId, StringComparison.OrdinalIgnoreCase))
            .Select(CloneSnapshotContext)
            .ToList();
    }

    private static bool ShouldRetryShareValidationWithAllSnapshotContexts(string? payoutSnapshotId, string? rejectionReason)
    {
        if (!string.IsNullOrWhiteSpace(payoutSnapshotId))
        {
            return false;
        }

        return !IsSingleRecipientFallbackRejection(rejectionReason);
    }

    private SnapshotValidationResult ValidateShareAgainstKnownSnapshotsIgnoringParent(
        RecordedShareSubmission share,
        IReadOnlyList<PayoutInfo> currentWinners,
        IReadOnlyCollection<BootPayoutSnapshotContext> snapshotContexts)
    {
        return ValidateShareAgainstKnownSnapshots(share, currentWinners, snapshotContexts, []);
    }

    private SnapshotValidationResult ValidateProofAgainstKnownSnapshots(
        BootShareProof proof,
        IReadOnlyList<PayoutInfo> currentWinners,
        IReadOnlyCollection<BootPayoutSnapshotContext> snapshotContexts,
        IReadOnlyCollection<string> expectedPrevBlockHashes)
    {
        List<BootPayoutSnapshotContext> orderedContexts = snapshotContexts
            .Where(context => !string.IsNullOrWhiteSpace(context.SnapshotId))
            .OrderByDescending(context => context.CreatedAtUtc)
            .ToList();

        bool hasPreferredSnapshot = !string.IsNullOrWhiteSpace(proof.PayoutSnapshotId);
        List<BootPayoutSnapshotContext> exactContexts = hasPreferredSnapshot
            ? orderedContexts
                .Where(context => string.Equals(context.SnapshotId, proof.PayoutSnapshotId, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        BootShareValidationResult firstFailure = InvalidValidationResult();
        if (hasPreferredSnapshot)
        {
            foreach (BootPayoutSnapshotContext context in exactContexts)
            {
                foreach (List<PayoutInfo> payoutVariant in GetSnapshotPayoutVariants(context))
                {
                    BootShareValidationResult validation = _shareVerifier.ValidateShare(proof, payoutVariant, expectedPrevBlockHashes);
                    if (validation.IsValid)
                    {
                        return new SnapshotValidationResult(validation, context.SnapshotId);
                    }

                    firstFailure = PreferInformativeFailure(firstFailure, validation);
                }
            }
        }

        BootShareValidationResult currentValidation = _shareVerifier.ValidateShare(proof, currentWinners, expectedPrevBlockHashes);
        if (currentValidation.IsValid)
        {
            string snapshotId = orderedContexts.FirstOrDefault(context => WinnersMatch(context.WinnersList, currentWinners))?.SnapshotId
                ?? proof.PayoutSnapshotId
                ?? string.Empty;
            return new SnapshotValidationResult(currentValidation, snapshotId);
        }

        firstFailure = PreferInformativeFailure(firstFailure, currentValidation);

        foreach (BootPayoutSnapshotContext context in orderedContexts.Where(context =>
                     !hasPreferredSnapshot ||
                     !string.Equals(context.SnapshotId, proof.PayoutSnapshotId, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (List<PayoutInfo> payoutVariant in GetSnapshotPayoutVariants(context))
            {
                BootShareValidationResult validation = _shareVerifier.ValidateShare(proof, payoutVariant, expectedPrevBlockHashes);
                if (validation.IsValid)
                {
                    return new SnapshotValidationResult(validation, context.SnapshotId);
                }

                firstFailure = PreferInformativeFailure(firstFailure, validation);
            }
        }

        return new SnapshotValidationResult(firstFailure, proof.PayoutSnapshotId ?? string.Empty);
    }

    private static bool IsSingleRecipientFallbackRejection(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason) &&
               reason.StartsWith("Coinbase appears to use a non-Boot single-recipient template", StringComparison.OrdinalIgnoreCase);
    }

    private static BootShareValidationResult InvalidValidationResult() => new()
    {
        IsValid = false
    };

    private static BootShareValidationResult PreferInformativeFailure(
        BootShareValidationResult current,
        BootShareValidationResult candidate)
    {
        if (current.IsValid || !string.IsNullOrWhiteSpace(current.RejectionReason))
        {
            return current;
        }

        return candidate;
    }

    private List<List<PayoutInfo>> GetSnapshotPayoutVariants(BootPayoutSnapshotContext context)
    {
        var variants = new List<List<PayoutInfo>>();
        if (context.WinnersList.Count > 0)
        {
            variants.Add(ClonePayouts(context.WinnersList));
        }

        return variants;
    }

    private static List<BootShareProof> SortAndTrimProofs(IEnumerable<BootShareProof> proofs, int limit)
    {
        return proofs
            .GroupBy(proof => proof.ShareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(proof => proof.Difficulty)
                .ThenBy(proof => proof.Timestamp)
                .First())
            .OrderByDescending(x => x.Difficulty)
            .ThenBy(x => x.ShareId, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .Select(CloneProof)
            .ToList();
    }

    private List<BootShareProof> MergeCandidateProofsIntoCanonicalReserveNoLock(IEnumerable<BootShareProof> remoteProofs)
    {
        string? currentTip = NormalizeCanonicalBlockHash(_state.CurrentTipBlockHash);
        HashSet<string> knownShareIds = _state.OnDeckProofs
            .Select(proof => proof.ShareId)
            .Where(shareId => !string.IsNullOrWhiteSpace(shareId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new List<BootShareProof>(_state.OnDeckProofs.Select(CloneProof));
        foreach (BootShareProof proof in remoteProofs)
        {
            if (ShouldMergeRemoteProofIntoCanonicalReserveNoLock(proof, currentTip, knownShareIds))
            {
                merged.Add(CloneProof(proof));
            }
        }

        return SortAndTrimProofs(merged, _poolConfig.WorkSetReserveLimit);
    }

    private bool ShouldMergeRemoteProofIntoCanonicalReserveNoLock(
        BootShareProof proof,
        string? currentTip,
        HashSet<string> knownShareIds)
    {
        if (string.IsNullOrWhiteSpace(proof.ShareId))
        {
            return false;
        }

        if (knownShareIds.Contains(proof.ShareId))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(currentTip))
        {
            return true;
        }

        return BitcoinHashes.AreEquivalent(proof.PrevBlockHash, currentTip);
    }

    private List<string> GetCanonicalParentBlockHashesForReserveNoLock()
    {
        return NormalizeAcceptedParentBlockHashes(
            _state.OnDeckProofs
                .Select(proof => proof.PrevBlockHash)
                .Append(_state.CurrentTipBlockHash));
    }

    private static bool ProofSetsEqualNoLock(IReadOnlyList<BootShareProof> left, IReadOnlyList<BootShareProof> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        List<string> leftIds = left
            .Select(proof => proof.ShareId)
            .OrderBy(shareId => shareId, StringComparer.Ordinal)
            .ToList();
        List<string> rightIds = right
            .Select(proof => proof.ShareId)
            .OrderBy(shareId => shareId, StringComparer.Ordinal)
            .ToList();
        return leftIds.SequenceEqual(rightIds, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ShareRecordingResult> SubmitShareAsync(RecordedShareSubmission share, string blockSource)
    {
        ShareRecordingResult result = await RecordShareAsync(share);
        result.BlockCandidate = result.IsBlock;
        result.IsBlock = false;
        if (!result.Accepted ||
            !result.BlockCandidate ||
            string.IsNullOrWhiteSpace(result.BlockHash))
        {
            return result;
        }

        RoundRotationResult? rotation = await TryConfirmGridPoolBlockCandidateAsync(
            result.BlockHash,
            blockSource,
            blockHeight: null,
            result.AcceptedProof,
            activeChainObservation: false);
        if (rotation == null)
        {
            RecordExternalNetworkEvent(
                "gridpool-block-candidate",
                blockSource,
                "Accepted a declared-target block candidate; payout state remains unchanged pending local Bitcoin active-chain confirmation.",
                result.BlockHash,
                InferFoundBlockHeight(result.BlockHash));
            return result;
        }

        result.IsBlock = rotation.Rotated;
        result.Rotation = rotation;
        result.NetworkStatus = rotation.NetworkStatus;
        result.OnDeckList = rotation.OnDeckList;
        return result;
    }

    private async Task<RoundRotationResult?> TryConfirmGridPoolBlockCandidateAsync(
        string blockHash,
        string source,
        long? blockHeight,
        BootShareProof? submittedProof = null,
        bool activeChainObservation = false)
    {
        BootShareProof? provenBlockShare;
        long? effectiveBlockHeight;
        bool boundaryAlreadyApplied;
        lock (_sync)
        {
            string normalizedBlockHash = NormalizeCanonicalBlockHash(blockHash) ?? string.Empty;
            if (BitcoinNotificationModes.Resolve(_poolConfig) != BitcoinNotificationModes.AttachedNode ||
                string.IsNullOrWhiteSpace(normalizedBlockHash) ||
                !_localChainTipHeaders.TryGetValue(normalizedBlockHash, out BitcoinHeaderEvaluation? localHeader) ||
                !localHeader.IsValid ||
                BitcoinHashes.AreEquivalent(_state.LastGridPoolBlockHash, normalizedBlockHash))
            {
                return null;
            }

            provenBlockShare = submittedProof == null
                ? _state.OnDeckProofs.FirstOrDefault(proof =>
                    ProofMatchesBlockHash(proof, normalizedBlockHash))
                : CloneProof(submittedProof);
            if (provenBlockShare == null ||
                !ProofMatchesBlockHash(provenBlockShare, normalizedBlockHash))
            {
                return null;
            }

            boundaryAlreadyApplied = BitcoinHashes.AreEquivalent(_state.CurrentTipBlockHash, normalizedBlockHash);
            if (!activeChainObservation &&
                (!boundaryAlreadyApplied ||
                 !BitcoinHashes.AreEquivalent(_state.TrustedLocalTipBlockHash, normalizedBlockHash)))
            {
                return null;
            }

            effectiveBlockHeight = blockHeight ?? (boundaryAlreadyApplied
                ? _state.CurrentTipBlockHeight
                : _state.CurrentTipBlockHeight.HasValue ? _state.CurrentTipBlockHeight.Value + 1 : null);
        }

        RoundRotationResult rotation = await RotateToNextRoundAsync(
            blockHash,
            source,
            manual: false,
            blockHeight: effectiveBlockHeight,
            provenSnapshotId: provenBlockShare.PayoutSnapshotId,
            localBitcoinActiveChainConfirmed: true,
            boundaryAlreadyApplied: boundaryAlreadyApplied,
            confirmedParentBlockHash: provenBlockShare.PrevBlockHash);
        if (!rotation.Rotated)
        {
            return rotation;
        }

        RecordGridPoolBlockFound(
            new ShareRecordingResult
            {
                Accepted = true,
                BlockCandidate = true,
                IsBlock = true,
                BlockHash = blockHash,
                ComputedDifficulty = provenBlockShare.Difficulty,
                AcceptedProof = CloneProof(provenBlockShare)
            },
            source,
            effectiveBlockHeight);
        rotation.NetworkStatus = GetNetworkStatus();
        return rotation;
    }

    private static bool ProofMatchesBlockHash(BootShareProof proof, string blockHash)
    {
        try
        {
            return BitcoinHashes.AreEquivalent(
                BitcoinHashes.ComputeBlockHashFromHeader(proof.HeaderHex),
                blockHash);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public LocalMiningTelemetryResultDto RecordLocalMiningTelemetryBatch(
        LocalMiningTelemetryBatchDto batch,
        string source)
    {
        if (batch.Entries == null)
        {
            throw new ArgumentException("Telemetry entries are required.");
        }

        string normalizedSource = string.IsNullOrWhiteSpace(source) ? "adapter" : source.Trim().ToLowerInvariant();
        var result = new LocalMiningTelemetryResultDto();
        lock (_sync)
        {
            foreach (LocalMiningTelemetryEntryDto entry in batch.Entries)
            {
                ValidateLocalMiningTelemetryEntry(entry);
                entry.WindowStartUtc = NormalizeTelemetryTimestampUtc(entry.WindowStartUtc);
                entry.WindowEndUtc = NormalizeTelemetryTimestampUtc(entry.WindowEndUtc);
                string address = BitcoinScript.NormalizeAddress(entry.PayoutAddress);
                if (!_localDatumHashrateByAddress.TryGetValue(address, out LocalDatumAddressHashrateTracker? tracker))
                {
                    tracker = new LocalDatumAddressHashrateTracker { Address = address };
                    _localDatumHashrateByAddress[address] = tracker;
                }

                NormalizeLocalDatumTrackerRoundNoLock(tracker);
                tracker.Sources.Add(normalizedSource);
                tracker.Username = string.IsNullOrWhiteSpace(entry.Username) ? address : entry.Username.Trim();
                string channelId = entry.ChannelId?.Trim() ?? string.Empty;
                bool duplicateWindow = tracker.WorkSamples.Any(sample =>
                    string.Equals(sample.Source, normalizedSource, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(sample.ChannelId, channelId, StringComparison.Ordinal) &&
                    sample.WindowStartUtc == entry.WindowStartUtc &&
                    sample.WindowEndUtc == entry.WindowEndUtc);
                if (duplicateWindow)
                {
                    continue;
                }

                tracker.TotalAcceptedShareCount += entry.AcceptedShareCount;
                if (!_state.LastRotationUtc.HasValue || entry.WindowEndUtc >= _state.LastRotationUtc.Value)
                {
                    tracker.CurrentRoundAcceptedShareCount = (int)Math.Min(
                        int.MaxValue,
                        (long)tracker.CurrentRoundAcceptedShareCount + entry.AcceptedShareCount);
                    tracker.CurrentRoundBestDifficulty = Math.Max(tracker.CurrentRoundBestDifficulty, entry.BestDifficulty);
                }
                tracker.LastShareUtc = !tracker.LastShareUtc.HasValue || entry.WindowEndUtc > tracker.LastShareUtc.Value
                    ? entry.WindowEndUtc
                    : tracker.LastShareUtc;
                tracker.WorkSamples.Add(new LocalMiningWorkSample
                {
                    Source = normalizedSource,
                    ChannelId = channelId,
                    WindowStartUtc = entry.WindowStartUtc,
                    WindowEndUtc = entry.WindowEndUtc,
                    AcceptedShareCount = entry.AcceptedShareCount,
                    AcceptedWorkDifficulty = entry.AcceptedWorkDifficulty,
                    FeeWorkDifficulty = entry.FeeWorkDifficulty,
                    BestDifficulty = entry.BestDifficulty
                });

                result.AcceptedEntries++;
                result.AcceptedShares += entry.AcceptedShareCount;
                result.AcceptedWorkDifficulty += entry.AcceptedWorkDifficulty;
                TrimLocalDatumAddressTrackerNoLock(tracker, entry.WindowEndUtc);
            }

            if (batch.Entries.Count > 0)
            {
                MaybeCaptureHashrateSampleNoLock(DateTime.UtcNow, force: false);
            }
        }

        return result;
    }

    public void RecordLocalMiningSourceGauge(
        string source,
        double hashrateThs,
        int activeMinerCount,
        DateTime observedUtc)
    {
        if (!TryNormalizeLocalMiningSource(source, out string normalizedSource) ||
            !double.IsFinite(hashrateThs) ||
            hashrateThs < 0 ||
            activeMinerCount < 0)
        {
            throw new ArgumentException("Local mining source gauge is invalid.");
        }

        lock (_sync)
        {
            _localMiningSourceGauges[normalizedSource] = new LocalMiningSourceGauge
            {
                HashrateThs = hashrateThs,
                ActiveMinerCount = activeMinerCount,
                ObservedUtc = observedUtc == default ? DateTime.UtcNow : observedUtc
            };
        }
    }

    private void ValidateLocalMiningTelemetryEntry(LocalMiningTelemetryEntryDto entry)
    {
        string address = BitcoinScript.NormalizeAddress(entry.PayoutAddress);
        if (!BitcoinScript.TryAddressToScriptPubKey(address, _poolConfig.BitcoinNetwork, out _))
        {
            throw new ArgumentException($"Invalid {_poolConfig.BitcoinNetwork} payout address in telemetry entry.");
        }

        if (entry.WindowStartUtc == default || entry.WindowEndUtc == default || entry.WindowEndUtc < entry.WindowStartUtc)
        {
            throw new ArgumentException("Telemetry window must contain valid increasing UTC timestamps.");
        }

        if (entry.AcceptedShareCount < 0 || entry.RejectedShareCount < 0 ||
            !IsFiniteNonNegative(entry.AcceptedWorkDifficulty) ||
            !IsFiniteNonNegative(entry.FeeWorkDifficulty) ||
            !IsFiniteNonNegative(entry.BestDifficulty))
        {
            throw new ArgumentException("Telemetry counts and difficulty values must be finite and non-negative.");
        }
    }

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static DateTime NormalizeTelemetryTimestampUtc(DateTime timestamp)
    {
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };
    }

    public async Task<RoundRotationResult> RotateToNextRoundAsync(
        string blockHash,
        string source,
        bool manual,
        long? blockHeight = null,
        string? provenSnapshotId = null,
        bool localBitcoinActiveChainConfirmed = false,
        bool boundaryAlreadyApplied = false,
        string? confirmedParentBlockHash = null)
    {
        RoundRotationResult result;
        bool winnersChanged = false;
        lock (_sync)
        {
            string? previousTipBlockHash = NormalizeCanonicalBlockHash(_state.CurrentTipBlockHash);
            long? previousTipBlockHeight = _state.CurrentTipBlockHeight;
            string? submittedBlockHash = NormalizeCanonicalBlockHash(blockHash);
            string? effectiveBlockHash = manual
                ? submittedBlockHash ?? previousTipBlockHash
                : submittedBlockHash;
            long? effectiveBlockHeight = manual
                ? blockHeight ?? previousTipBlockHeight
                : blockHeight;

            if (!manual && !localBitcoinActiveChainConfirmed)
            {
                return new RoundRotationResult
                {
                    Rotated = false,
                    Reason = "GridPool block is not confirmed by the local Bitcoin active chain",
                    BlockHash = previousTipBlockHash,
                    WinnersList = ClonePayouts(_state.WinnersList),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                };
            }

            if (!manual &&
                IsStaleTipObservationNoLock(effectiveBlockHash, effectiveBlockHeight, previousTipBlockHash, previousTipBlockHeight))
            {
                RecordNetworkEventNoLock(
                    "chain-tip-stale",
                    source,
                    $"Ignored stale round-rotation block at height {effectiveBlockHeight}; current tip height is {previousTipBlockHeight}.",
                    effectiveBlockHash,
                    effectiveBlockHeight);
                RequestDeferredSaveNoLock();
                RequestDeferredHistorySaveNoLock();
                return new RoundRotationResult
                {
                    Rotated = false,
                    Reason = "Stale block notification",
                    BlockHash = previousTipBlockHash,
                    WinnersList = ClonePayouts(_state.WinnersList),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                };
            }

            if (!manual && !boundaryAlreadyApplied &&
                !string.IsNullOrWhiteSpace(effectiveBlockHash) &&
                BitcoinHashes.AreEquivalent(effectiveBlockHash, previousTipBlockHash) &&
                BitcoinHashes.AreEquivalent(effectiveBlockHash, _state.TrustedLocalTipBlockHash))
            {
                return new RoundRotationResult
                {
                    Rotated = false,
                    Reason = "Block already applied",
                    BlockHash = previousTipBlockHash,
                    WinnersList = ClonePayouts(_state.WinnersList),
                    OnDeckList = ClonePayouts(_state.OnDeckList),
                    NetworkStatus = BuildNetworkStatusNoLock()
                };
            }

            if (!manual && effectiveBlockHeight.HasValue)
            {
                _state.TrustedLocalTipBlockHash = effectiveBlockHash;
                _state.TrustedLocalTipBlockHeight = effectiveBlockHeight;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (manual && _state.OnDeckProofs.Count == 0)
            {
                _state.CurrentTipBlockHash = effectiveBlockHash;
                _state.CurrentTipBlockHeight = effectiveBlockHeight;
                ResetAcceptedParentBlockHashesNoLock(effectiveBlockHash);
                _state.LastRotationUtc = nowUtc;
                _state.OnDeckProofs = [];
                _state.OnDeckList = [];
            }
            else if (manual)
            {
                _state.OnDeckProofs = [];
                _state.OnDeckList = [];
                ApplySnapshotFromWorkSetNoLock(effectiveBlockHash, effectiveBlockHeight, "manual-reset", nowUtc, advanceRound: true);
                winnersChanged = true;
            }
            else
            {
                EnsureActiveSnapshotNoLock(nowUtc);
                string previousStateId = _state.CurrentStateId;
                string paidSnapshotId = string.IsNullOrWhiteSpace(provenSnapshotId)
                    ? _state.ActiveSnapshotId
                    : provenSnapshotId;
                BootPayoutSnapshotContext? paidContext = _state.SnapshotContexts
                    .FirstOrDefault(context => string.Equals(context.SnapshotId, paidSnapshotId, StringComparison.OrdinalIgnoreCase));
                if (paidContext == null)
                {
                    throw new InvalidOperationException($"Winning block proved unknown payout snapshot {paidSnapshotId}.");
                }
                List<PayoutInfo> paidWinners = paidContext == null
                    ? ClonePayouts(_state.WinnersList)
                    : ClonePayouts(paidContext.WinnersList);

                ApplyPaidSnapshotRemovalNoLock(source, effectiveBlockHash, effectiveBlockHeight, nowUtc, paidSnapshotId);
                _state.CurrentTipBlockHash = effectiveBlockHash;
                _state.CurrentTipBlockHeight = effectiveBlockHeight;
                string? paidParentBlockHash = NormalizeCanonicalBlockHash(confirmedParentBlockHash) ?? previousTipBlockHash;
                PreserveAcceptedParentContinuityAfterRotationNoLock(paidParentBlockHash, effectiveBlockHash);
                ApplySnapshotFromWorkSetNoLock(
                    effectiveBlockHash,
                    effectiveBlockHeight,
                    source,
                    nowUtc,
                    advanceRound: !boundaryAlreadyApplied);

                BootStateBundle lockedBundle = BuildBundleFromCurrentCandidateNoLock();
                lockedBundle.StateId = _state.CurrentStateId;
                lockedBundle.PreviousStateId = previousStateId;
                lockedBundle.Kind = source;
                lockedBundle.CurrentRoundNumber = _state.CurrentRoundNumber;
                lockedBundle.LockedByBlockHash = effectiveBlockHash;
                lockedBundle.LockedByBlockHeight = effectiveBlockHeight;
                lockedBundle.ParentBlockHash = paidParentBlockHash;
                lockedBundle.ParentBlockHeight = boundaryAlreadyApplied && effectiveBlockHeight.HasValue
                    ? effectiveBlockHeight.Value - 1
                    : previousTipBlockHeight;
                lockedBundle.CreatedAtUtc = nowUtc;
                lockedBundle.ValidParentBlockHashes = GetAcceptedParentBlockHashesNoLock();
                lockedBundle.ProofWinnersList = paidWinners;
                lockedBundle.PaidSnapshotId = paidSnapshotId;
                lockedBundle.PaidSnapshotProofIds = _state.LastPaidSnapshotProofIds.ToList();
                lockedBundle.Commitment = BuildCommitmentNoLock();
                UpsertArchivedBundleNoLock(lockedBundle);
                winnersChanged = true;
            }

            _state.CandidateStateId = ComputeCandidateStateIdNoLock();
            CacheCurrentCandidateBundleNoLock();
            RecordNetworkEventNoLock(
                manual ? "manual-reset" : "round-rotation",
                source,
                manual && !winnersChanged
                    ? "Manual reset cleared the active On Deck state and preserved the current Winners List."
                    : manual ? "Manual reset completed." : $"GridPool block paid the active snapshot and activated the next payout snapshot from {source}.",
                effectiveBlockHash,
                effectiveBlockHeight);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();

            result = new RoundRotationResult
            {
                Rotated = !manual || winnersChanged,
                Reason = manual && !winnersChanged
                    ? "Manual reset cleared On Deck state and preserved the current Winners List"
                    : manual ? "Manual reset completed" : $"GridPool block paid active snapshot from {source}",
                BlockHash = effectiveBlockHash,
                WinnersList = ClonePayouts(_state.WinnersList),
                OnDeckList = ClonePayouts(_state.OnDeckList),
                NetworkStatus = BuildNetworkStatusNoLock(),
                LockedStateBundle = winnersChanged ? GetStateBundle(_state.CurrentStateId) : null
            };
        }

        await _hubContext.Clients.All.SendAsync("UpdateOnDeck", result.OnDeckList);
        await _hubContext.Clients.All.SendAsync("UpdateNetworkState", result.NetworkStatus);
        await _hubContext.Clients.All.SendAsync("UpdateRoundHistory", GetRoundHistory());
        if (winnersChanged)
        {
            await _hubContext.Clients.All.SendAsync("UpdateWinners", result.WinnersList);
            await NotifyWinnersListChangedAsync(manual ? "manual-reset" : source);
        }
        return result;
    }

    public async Task<BootNetworkStatusDto> ResetHistoryToGenesisAsync()
    {
        BootNetworkStatusDto networkStatus;
        List<PayoutInfo> winnersSnapshot;
        List<PayoutInfo> onDeckSnapshot;

        lock (_sync)
        {
            List<BootPeerStatus> peers = _state.Peers.Select(ClonePeer).ToList();
            Dictionary<string, string> knownDatumPayouts = new(_state.KnownDatumPayoutAddresses, StringComparer.Ordinal);
            BestShareRecord bestShare = CloneBestShare(_state.BestShare);
            string? currentTipBlockHash = _state.CurrentTipBlockHash;
            long? currentTipBlockHeight = _state.CurrentTipBlockHeight;
            string? trustedLocalTipBlockHash = _state.TrustedLocalTipBlockHash;
            long? trustedLocalTipBlockHeight = _state.TrustedLocalTipBlockHeight;

            InitializeDefaultsNoLock();
            _state.Peers = peers;
            _state.KnownDatumPayoutAddresses = knownDatumPayouts;
            _state.BestShare = bestShare;
            _state.CurrentTipBlockHash = currentTipBlockHash;
            _state.CurrentTipBlockHeight = currentTipBlockHeight;
            _state.TrustedLocalTipBlockHash = trustedLocalTipBlockHash;
            _state.TrustedLocalTipBlockHeight = trustedLocalTipBlockHeight;
            _state.LastRotationUtc = DateTime.UtcNow;
            _state.LastTestingTriggerBlockHash = null;
            _state.LastTestingTriggerBlockHeight = null;
            _state.RecentAcceptedShares = [];
            _state.RecentRejectedShareDiagnostics = [];
            _state.RecentCoinbaserDiagnostics = [];
            _state.RecentDatumShareResponses = [];
            _state.RecentDatumSessions = [];
            _state.RecentNetworkEvents = [];
            _state.RecentPeerRelayObservations = [];
            _activeDatumSessions.Clear();
            _peerRelayFirstArrivals.Clear();
            _recentShareDiagnostics.Clear();
            _state.HashrateSamples = [];
            _state.LocalDatumMinerHashrateSamples = [];
            ResetAcceptedParentBlockHashesNoLock(currentTipBlockHash);
            _localDatumHashrateByAddress.Clear();
            _lastLocalDatumHashrateRollupByAddress.Clear();
            RecordNetworkEventNoLock(
                "genesis-reset",
                "admin",
                "Reset local history back to the genesis Winners List.",
                currentTipBlockHash,
                currentTipBlockHeight);
            SaveStateNoLock();

            winnersSnapshot = ClonePayouts(_state.WinnersList);
            onDeckSnapshot = ClonePayouts(_state.OnDeckList);
            networkStatus = BuildNetworkStatusNoLock();
        }

        await _hubContext.Clients.All.SendAsync("UpdateWinners", winnersSnapshot);
        await _hubContext.Clients.All.SendAsync("UpdateOnDeck", onDeckSnapshot);
        await _hubContext.Clients.All.SendAsync("UpdateNetworkState", networkStatus);
        await _hubContext.Clients.All.SendAsync("UpdateRoundHistory", GetRoundHistory());
        await NotifyWinnersListChangedAsync("genesis-reset");
        await NotifyWorkTemplatesInvalidatedAsync("genesis-reset");
        return networkStatus;
    }

    public bool ObserveLocalChainTipHeader(
        string headerHex,
        string source,
        DateTime transportReceivedUtc,
        long? blockHeight = null)
    {
        BitcoinHeaderEvaluation evaluation = BitcoinHashes.EvaluateHeader(headerHex, transportReceivedUtc);
        if (!evaluation.IsValid)
        {
            _logger.LogWarning(
                "Ignored invalid local Bitcoin block header from {Source}: {Reason}",
                source,
                evaluation.RejectionReason);
            return false;
        }

        lock (_sync)
        {
            _localChainTipHeaders[evaluation.BlockHash] = evaluation;
            if (BitcoinHashes.AreEquivalent(evaluation.BlockHash, _state.CurrentTipBlockHash))
            {
                _state.CurrentTipCompactTarget = evaluation.CompactTarget;
                RequestDeferredSaveNoLock();
            }
            if (_localChainTipHeaders.Count > 32)
            {
                foreach (string expired in _localChainTipHeaders
                             .OrderBy(entry => entry.Value.ReceivedUtc)
                             .Take(_localChainTipHeaders.Count - 32)
                             .Select(entry => entry.Key)
                             .ToList())
                {
                    _localChainTipHeaders.Remove(expired);
                }
            }
        }

        RecordExternalNetworkEvent(
            "local-chain-tip-header",
            source,
            source.StartsWith("rpc", StringComparison.OrdinalIgnoreCase)
                ? "Local Bitcoin RPC reconciliation delivered a block header."
                : "Local Bitcoin source delivered a raw block header.",
            evaluation.BlockHash,
            blockHeight,
            transportReceivedUtc,
            source.StartsWith("rpc", StringComparison.OrdinalIgnoreCase)
                ? "bitcoin-rpc"
                : "bitcoin-zmq-rawblock",
            payloadBytes: 80);

        int activeConsensusVersion = GetActiveConsensusVersion();
        PublishChainTipAnnouncement(new BootChainTipAnnouncement
        {
            SenderEndpoint = GetSelfEndpoint(),
            Source = source,
            HeaderHex = evaluation.HeaderHex,
            BlockHash = evaluation.BlockHash,
            BlockHeight = blockHeight,
            ObservedUtc = transportReceivedUtc,
            ProtocolVersion = activeConsensusVersion,
            ConsensusVersion = activeConsensusVersion,
            PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
            NetworkId = _poolConfig.BootNetworkId
        });
        return true;
    }

    public async Task<BootNetworkStatusDto> ObserveChainTipAsync(string blockHash, string source, long? blockHeight = null)
    {
        RoundRotationResult? confirmedGridPoolBlock = await TryConfirmGridPoolBlockCandidateAsync(
            blockHash,
            $"local-bitcoin-confirmed:{source}",
            blockHeight,
            activeChainObservation: IsLocalBitcoinActiveChainSource(source));
        if (confirmedGridPoolBlock?.Rotated == true)
        {
            return confirmedGridPoolBlock.NetworkStatus;
        }

        BootNetworkStatusDto status;
        string? normalizedBlockHash;
        long? effectiveBlockHeight;
        bool shouldRotateTestRound = false;
        bool metadataChanged = false;
        bool snapshotChanged = false;
        bool provisionalResolved = false;
        bool activationAtExistingTip = false;
        double? provisionalLeadMs = null;
        List<PayoutInfo> winnersSnapshot = [];
        List<PayoutInfo> onDeckSnapshot = [];
        lock (_sync)
        {
            normalizedBlockHash = NormalizeCanonicalBlockHash(blockHash);
            if (string.IsNullOrWhiteSpace(normalizedBlockHash))
            {
                _logger.LogWarning("Ignored invalid chain tip hash from {Source}: {BlockHash}", source, blockHash);
                return BuildNetworkStatusNoLock();
            }

            effectiveBlockHeight = blockHeight;
            if (!effectiveBlockHeight.HasValue &&
                !string.IsNullOrWhiteSpace(_state.CurrentTipBlockHash) &&
                !BitcoinHashes.AreEquivalent(normalizedBlockHash, _state.CurrentTipBlockHash) &&
                _state.CurrentTipBlockHeight.HasValue)
            {
                // Most notification sources deliver blocks sequentially even if they omit height.
                // Infer the next height immediately for UI/history purposes, then allow exact data
                // from a later peer/backlog update to overwrite it if needed.
                effectiveBlockHeight = _state.CurrentTipBlockHeight.Value + 1;
            }
            long? trustedEffectiveBlockHeight = blockHeight;
            if (!trustedEffectiveBlockHeight.HasValue &&
                !string.IsNullOrWhiteSpace(_state.CurrentTipBlockHash) &&
                !BitcoinHashes.AreEquivalent(normalizedBlockHash, _state.CurrentTipBlockHash) &&
                _state.TrustedLocalTipBlockHeight.HasValue)
            {
                trustedEffectiveBlockHeight = _state.TrustedLocalTipBlockHeight.Value + 1;
            }

            if (BitcoinHashes.AreEquivalent(normalizedBlockHash, _state.CurrentTipBlockHash))
            {
                int previousActiveConsensusVersion = GetActiveConsensusVersionNoLock();
                metadataChanged = UpdateKnownBlockHeightNoLock(normalizedBlockHash, effectiveBlockHeight);
                if (blockHeight.HasValue)
                {
                    _state.TrustedLocalTipBlockHash = normalizedBlockHash;
                    _state.TrustedLocalTipBlockHeight = blockHeight;
                    metadataChanged = true;
                }
                if (_localChainTipHeaders.TryGetValue(normalizedBlockHash, out BitcoinHeaderEvaluation? duplicateHeader))
                {
                    _state.CurrentTipCompactTarget = duplicateHeader.CompactTarget;
                    metadataChanged = true;
                }
                activationAtExistingTip = previousActiveConsensusVersion < BootProtocolVersions.ConsensusVersion &&
                                          GetActiveConsensusVersionNoLock() >= BootProtocolVersions.ConsensusVersion;
                if (metadataChanged && !activationAtExistingTip)
                {
                    RequestDeferredSaveNoLock();
                }
                if (!activationAtExistingTip)
                {
                    return BuildNetworkStatusNoLock();
                }
            }

            bool trustedReorganization = source.Contains("reorg", StringComparison.OrdinalIgnoreCase);
            if (!trustedReorganization && IsStaleTipObservationNoLock(
                normalizedBlockHash,
                effectiveBlockHeight,
                _state.CurrentTipBlockHash,
                _state.CurrentTipBlockHeight))
            {
                RecordNetworkEventNoLock(
                    "chain-tip-stale",
                    source,
                    $"Ignored stale chain-tip observation at height {effectiveBlockHeight}; current tip height is {_state.CurrentTipBlockHeight}.",
                    normalizedBlockHash,
                    effectiveBlockHeight);
                RequestDeferredSaveNoLock();
                RequestDeferredHistorySaveNoLock();
                return BuildNetworkStatusNoLock();
            }

            bool oneBlockReorg = GetActiveConsensusVersionNoLock() >= BootProtocolVersions.ConsensusVersion &&
                effectiveBlockHeight.HasValue &&
                (_state.CurrentTipBlockHeight == effectiveBlockHeight || trustedReorganization) &&
                !BitcoinHashes.AreEquivalent(normalizedBlockHash, _state.CurrentTipBlockHash);
            if (oneBlockReorg)
            {
                RestorePredecessorForRemovedBoundaryNoLock(source, normalizedBlockHash, effectiveBlockHeight!.Value);
            }

            shouldRotateTestRound = ShouldTriggerTestingRoundResetNoLock(normalizedBlockHash);
            if (shouldRotateTestRound)
            {
                _state.LastTestingTriggerBlockHash = normalizedBlockHash;
                _state.LastTestingTriggerBlockHeight = effectiveBlockHeight;
                RecordNetworkEventNoLock(
                    "chain-tip",
                    source,
                    "Observed chain tip that qualified for deterministic test marker; v2 consensus snapshots without payment on chain tips.",
                    normalizedBlockHash,
                    effectiveBlockHeight);
            }

            BootProvisionalTipState? provisional = _state.ProvisionalTip;
            IReadOnlyCollection<BootShareProof>? frozenProofs = null;
            if (provisional != null)
            {
                provisionalResolved = true;
                if (BitcoinHashes.AreEquivalent(provisional.BlockHash, normalizedBlockHash))
                {
                    frozenProofs = provisional.SnapshotProofs;
                    provisionalLeadMs = Math.Max(0, (DateTime.UtcNow - provisional.ObservedUtc).TotalMilliseconds);
                    RecordNetworkEventNoLock(
                        "peer-tip-confirmed",
                        source,
                        $"Local Bitcoin source confirmed provisional header after {provisionalLeadMs.Value:F1} ms; activating frozen snapshot {provisional.SnapshotId}.",
                        normalizedBlockHash,
                        effectiveBlockHeight);
                }
                else
                {
                    RecordNetworkEventNoLock(
                        "peer-tip-discarded",
                        source,
                        $"Local Bitcoin source validated competing block {normalizedBlockHash}; discarded provisional header {provisional.BlockHash}.",
                        normalizedBlockHash,
                        effectiveBlockHeight);
                }

                _state.ProvisionalTip = null;
                _provisionalTipGeneration++;
            }

            _state.CurrentTipBlockHash = normalizedBlockHash;
            _state.CurrentTipBlockHeight = effectiveBlockHeight;
            if (trustedEffectiveBlockHeight.HasValue)
            {
                _state.TrustedLocalTipBlockHash = normalizedBlockHash;
                _state.TrustedLocalTipBlockHeight = trustedEffectiveBlockHeight;
            }
            if (_localChainTipHeaders.TryGetValue(normalizedBlockHash, out BitcoinHeaderEvaluation? localHeader))
            {
                _state.CurrentTipCompactTarget = localHeader.CompactTarget;
            }
            RememberAcceptedParentBlockHashNoLock(normalizedBlockHash);
            UpdateKnownBlockHeightNoLock(normalizedBlockHash, effectiveBlockHeight);
            snapshotChanged = ApplySnapshotFromWorkSetNoLock(
                normalizedBlockHash,
                effectiveBlockHeight,
                $"chain-tip:{source}",
                DateTime.UtcNow,
                advanceRound: !activationAtExistingTip,
                frozenProofs);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();

            status = BuildNetworkStatusNoLock();
            winnersSnapshot = ClonePayouts(_state.WinnersList);
            onDeckSnapshot = ClonePayouts(_state.OnDeckList);
        }

        if (shouldRotateTestRound && !string.IsNullOrWhiteSpace(normalizedBlockHash))
        {
            _logger.LogWarning(
                "Deterministic test marker fired from {Source}: {BlockHash}",
                source,
                normalizedBlockHash);
        }

        _logger.LogInformation("Observed new chain tip from {Source}: {BlockHash}", source, blockHash);
        if (snapshotChanged)
        {
            await _hubContext.Clients.All.SendAsync("UpdateWinners", winnersSnapshot);
            await _hubContext.Clients.All.SendAsync("UpdateOnDeck", onDeckSnapshot);
            await _hubContext.Clients.All.SendAsync("UpdateRoundHistory", GetRoundHistory());
            await NotifyWinnersListChangedAsync($"chain-tip:{source}");
        }

        await _hubContext.Clients.All.SendAsync("UpdateNetworkState", status);
        if (!snapshotChanged)
        {
            await NotifyWorkTemplatesInvalidatedAsync($"chain-tip:{source}");
        }

        if (provisionalResolved)
        {
            _logger.LogInformation(
                "Resolved provisional peer tip {BlockHash} from local Bitcoin confirmation (lead={LeadMs} ms).",
                normalizedBlockHash,
                provisionalLeadMs);
        }

        return status;
    }

    private static bool IsLocalBitcoinActiveChainSource(string source)
    {
        return source.Equals("zmq", StringComparison.OrdinalIgnoreCase) ||
               source.StartsWith("zmq-", StringComparison.OrdinalIgnoreCase) ||
               source.StartsWith("rpc", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<BootNetworkStatusDto> ObservePeerChainTipAsync(
        BootChainTipAnnouncement announcement,
        string remoteEndpoint,
        string remoteNodeId,
        string transport,
        int payloadBytes,
        DateTime? transportReceivedUtc = null)
    {
        DateTime receivedUtc = transportReceivedUtc ?? DateTime.UtcNow;
        BitcoinHeaderEvaluation evaluation = BitcoinHashes.EvaluateHeader(announcement.HeaderHex, receivedUtc);
        if (!evaluation.IsValid)
        {
            RecordExternalNetworkEvent(
                "peer-chain-tip-rejected",
                string.IsNullOrWhiteSpace(remoteEndpoint) ? $"peer-tip-node:{remoteNodeId}" : $"peer-tip:{remoteEndpoint}",
                $"Rejected peer chain-tip announcement: {evaluation.RejectionReason}",
                announcement.BlockHash,
                announcement.BlockHeight,
                receivedUtc,
                transport,
                remoteEndpoint,
                remoteNodeId,
                announcement.ObservedUtc,
                payloadBytes: payloadBytes);
            return GetNetworkStatus();
        }

        if (!string.IsNullOrWhiteSpace(announcement.BlockHash) &&
            !BitcoinHashes.AreEquivalent(evaluation.BlockHash, announcement.BlockHash))
        {
            RecordExternalNetworkEvent(
                "peer-chain-tip-rejected",
                string.IsNullOrWhiteSpace(remoteEndpoint) ? $"peer-tip-node:{remoteNodeId}" : $"peer-tip:{remoteEndpoint}",
                "Rejected measurement-only chain-tip announcement because its header hash did not match.",
                evaluation.BlockHash,
                announcement.BlockHeight,
                receivedUtc,
                transport,
                remoteEndpoint,
                remoteNodeId,
                announcement.ObservedUtc,
                payloadBytes: payloadBytes);
            return GetNetworkStatus();
        }

        _bitcoinNotificationHealth?.RequestReconciliation();

        string source = string.IsNullOrWhiteSpace(remoteEndpoint)
            ? $"peer-tip-node:{remoteNodeId}"
            : $"peer-tip:{remoteEndpoint}";

        if (!_poolConfig.EnablePeerTipStaleProtection)
        {
            RecordExternalNetworkEvent(
                "peer-chain-tip",
                source,
                $"Received measurement-only peer block header from {announcement.Source}; awaiting independent local Bitcoin confirmation.",
                evaluation.BlockHash,
                announcement.BlockHeight,
                receivedUtc,
                transport,
                remoteEndpoint,
                remoteNodeId,
                announcement.ObservedUtc,
                relayLatencyMs: null,
                payloadBytes);
            return GetNetworkStatus();
        }

        bool provisionalCreated = false;
        string rejection = string.Empty;
        bool expectedDifficultyValidated = false;
        lock (_sync)
        {
            DateTime oldestAllowedUtc = receivedUtc.AddSeconds(-_poolConfig.PeerTipMaxHeaderAgeSeconds);
            DateTime newestAllowedUtc = receivedUtc.AddSeconds(_poolConfig.PeerTipMaxFutureSeconds);
            if (evaluation.HeaderTimeUtc < oldestAllowedUtc || evaluation.HeaderTimeUtc > newestAllowedUtc)
            {
                rejection = "header timestamp is outside the configured freshness window";
            }
            else if (!BitcoinHashes.AreEquivalent(evaluation.ParentBlockHash, _state.CurrentTipBlockHash))
            {
                rejection = "header does not directly extend the locally active Bitcoin tip";
            }
            else if (BitcoinScript.NormalizeNetwork(_poolConfig.BitcoinNetwork) != BitcoinScript.Mainnet)
            {
                rejection = "operational peer-tip protection does not yet implement testnet4 contextual target rules";
            }
            else if (!_state.CurrentTipCompactTarget.HasValue)
            {
                rejection = "the expected target is unavailable until a local raw block header has been observed";
            }
            else if (IsNextBitcoinRetargetBoundaryNoLock())
            {
                rejection = "operational peer-tip protection is disabled at retarget boundaries pending contextual target validation";
            }
            else if (evaluation.CompactTarget != _state.CurrentTipCompactTarget.Value)
            {
                rejection = "header target does not match the expected mainnet target";
            }
            else if (_state.ProvisionalTip != null &&
                     !BitcoinHashes.AreEquivalent(_state.ProvisionalTip.BlockHash, evaluation.BlockHash))
            {
                rejection = "a competing provisional header already exists for this parent";
            }
            else if (_state.ProvisionalTip == null)
            {
                expectedDifficultyValidated = true;
                List<BootShareProof> frozenProofs = SortAndTrimProofs(
                    _state.OnDeckProofs,
                    _poolConfig.SnapshotProofSlotCount);
                BootPayoutSnapshotContext context = BuildSnapshotContextFromProofsNoLock(
                    frozenProofs,
                    evaluation.BlockHash,
                    announcement.BlockHeight ?? (_state.CurrentTipBlockHeight + 1),
                    receivedUtc,
                    _state.CurrentRoundNumber + 1);
                _state.ProvisionalTip = new BootProvisionalTipState
                {
                    BlockHash = evaluation.BlockHash,
                    ParentBlockHash = evaluation.ParentBlockHash,
                    HeaderHex = evaluation.HeaderHex,
                    CompactTarget = evaluation.CompactTarget,
                    HeaderTimeUtc = evaluation.HeaderTimeUtc,
                    ObservedUtc = receivedUtc,
                    GraceDeadlineUtc = receivedUtc.AddSeconds(_poolConfig.PeerTipGraceSeconds),
                    Source = source,
                    SnapshotId = context.SnapshotId,
                    SnapshotProofs = frozenProofs.Select(CloneProof).ToList(),
                    ExpectedDifficultyValidated = expectedDifficultyValidated
                };
                _provisionalTipGeneration++;
                provisionalCreated = true;
                RecordNetworkEventNoLock(
                    "peer-tip-provisional",
                    source,
                    $"Froze provisional snapshot {context.SnapshotId} with {context.ProofIds.Count} proof(s); awaiting local Bitcoin confirmation.",
                    evaluation.BlockHash,
                    announcement.BlockHeight,
                    receivedUtc,
                    transport,
                    remoteEndpoint,
                    remoteNodeId,
                    announcement.ObservedUtc,
                    payloadBytes: payloadBytes);
                RequestDeferredSaveNoLock();
                RequestDeferredHistorySaveNoLock();
            }
        }

        if (!string.IsNullOrWhiteSpace(rejection))
        {
            RecordExternalNetworkEvent(
                "peer-chain-tip-rejected",
                source,
                $"Rejected operational peer chain-tip announcement because {rejection}.",
                evaluation.BlockHash,
                announcement.BlockHeight,
                receivedUtc,
                transport,
                remoteEndpoint,
                remoteNodeId,
                announcement.ObservedUtc,
                payloadBytes: payloadBytes);
            return GetNetworkStatus();
        }

        if (provisionalCreated)
        {
            ScheduleProvisionalTipGraceCheck(evaluation.BlockHash, _provisionalTipGeneration);
        }

        RecordExternalNetworkEvent(
            "peer-chain-tip",
            source,
            $"Received validated peer block header from {announcement.Source}; provisional boundary awaits local Bitcoin confirmation.",
            evaluation.BlockHash,
            announcement.BlockHeight,
            receivedUtc,
            transport,
            remoteEndpoint,
            remoteNodeId,
            announcement.ObservedUtc,
            relayLatencyMs: null,
            payloadBytes);
        return await Task.FromResult(GetNetworkStatus());
    }

    public async Task<bool> TryImportCandidateStateAsync(BootStateBundle bundle, string sourceEndpoint)
    {
        BootVersionCompatibilityDto compatibility = EvaluateStateBundleCompatibility(bundle);
        if (!compatibility.CanSyncState)
        {
            _logger.LogWarning(
                "Rejected candidate state bundle from {SourceEndpoint}: {Reason}.",
                sourceEndpoint,
                compatibility.Reason);
            RecordExternalNetworkEvent(
                "peer-version-mismatch",
                sourceEndpoint,
                $"Rejected candidate state bundle: {compatibility.Reason}.",
                bundle.ParentBlockHash ?? bundle.LockedByBlockHash,
                bundle.ParentBlockHeight ?? bundle.LockedByBlockHeight);
            return false;
        }

        List<BootShareProof> remoteWorkSetProofs = bundle.WorkSetProofs.Count > 0
            ? bundle.WorkSetProofs
            : bundle.ShareProofs;

        if (bundle.WinnersList.Count > _poolConfig.WinnersListSize ||
            bundle.ShareProofs.Count > _poolConfig.SnapshotProofSlotCount ||
            remoteWorkSetProofs.Count > _poolConfig.WorkSetReserveLimit)
        {
            return false;
        }

        List<PayoutInfo> winnersSnapshot;
        string? currentTipSnapshot;
        string currentStateSnapshot;
        List<string> acceptedParentBlockHashesSnapshot;
        lock (_sync)
        {
            winnersSnapshot = ClonePayouts(_state.WinnersList);
            currentTipSnapshot = _state.CurrentTipBlockHash;
            currentStateSnapshot = _state.CurrentStateId;
            acceptedParentBlockHashesSnapshot = GetAcceptedParentBlockHashesNoLock();
            if (remoteWorkSetProofs.Any(proof => ShouldQuarantinePreviousParentNoLock(proof.PrevBlockHash, DateTime.UtcNow)))
            {
                RecordNetworkEventNoLock(
                    "state-import-quarantined",
                    sourceEndpoint,
                    "Rejected candidate bundle containing proofs on the parent frozen by a provisional peer-tip boundary.",
                    _state.ProvisionalTip?.BlockHash,
                    _state.CurrentTipBlockHeight.HasValue ? _state.CurrentTipBlockHeight + 1 : null);
                RequestDeferredHistorySaveNoLock();
                return false;
            }
        }

        List<string> remoteAcceptedParentBlockHashes = NormalizeAcceptedParentBlockHashes(
            bundle.ValidParentBlockHashes
                .Append(bundle.ParentBlockHash ?? string.Empty)
                .Concat(remoteWorkSetProofs.Select(proof => proof.PrevBlockHash)));
        List<string> validationParentBlockHashes = MergeAcceptedParentBlockHashes(
            acceptedParentBlockHashesSnapshot,
            remoteAcceptedParentBlockHashes);

        bool parentSetsOverlap =
            acceptedParentBlockHashesSnapshot.Count == 0 ||
            remoteAcceptedParentBlockHashes.Count == 0 ||
            acceptedParentBlockHashesSnapshot.Any(local =>
                remoteAcceptedParentBlockHashes.Any(remote => BitcoinHashes.AreEquivalent(local, remote)));

        bool currentTipIsCompatible =
            string.IsNullOrWhiteSpace(currentTipSnapshot) ||
            validationParentBlockHashes.Any(hash => BitcoinHashes.AreEquivalent(hash, currentTipSnapshot)) ||
            BitcoinHashes.AreEquivalent(bundle.ParentBlockHash, currentTipSnapshot);

        if (!parentSetsOverlap || !currentTipIsCompatible)
        {
            return false;
        }

        List<BootShareProof> validatedProofs;
        List<PayoutInfo> expectedPayouts;

        try
        {
            IReadOnlyList<PayoutInfo> proofWinners = bundle.ProofWinnersList.Count > 0
                ? bundle.ProofWinnersList
                : winnersSnapshot;
            validatedProofs = ValidateImportedProofs(
                remoteWorkSetProofs,
                proofWinners,
                validationParentBlockHashes,
                $"peer-state:{sourceEndpoint}",
                bundle.SnapshotContexts);
            expectedPayouts = BuildPayoutsFromProofs(validatedProofs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rejected candidate state bundle from {SourceEndpoint}.", sourceEndpoint);
            return false;
        }

        string expectedStateId = ComputeCandidateStateId(currentStateSnapshot, validatedProofs);
        if (!string.Equals(expectedStateId, bundle.StateId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!WinnersMatch(expectedPayouts, bundle.WinnersList))
        {
            return false;
        }

        bool imported = false;
        BootNetworkStatusDto networkStatus;
        List<PayoutInfo> onDeckSnapshot;

        lock (_sync)
        {
            if (!string.Equals(currentStateSnapshot, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            List<string> currentAcceptedParentBlockHashes = GetAcceptedParentBlockHashesNoLock();
            List<string> mergedAcceptedParentBlockHashes = MergeAcceptedParentBlockHashes(
                currentAcceptedParentBlockHashes,
                remoteAcceptedParentBlockHashes);
            if (!validatedProofs.All(proof =>
                    mergedAcceptedParentBlockHashes.Any(hash => BitcoinHashes.AreEquivalent(hash, proof.PrevBlockHash))))
            {
                return false;
            }

            List<BootShareProof> mergedCanonicalProofs = MergeCandidateProofsIntoCanonicalReserveNoLock(validatedProofs);
            if (ProofSetsEqualNoLock(mergedCanonicalProofs, _state.OnDeckProofs))
            {
                return false;
            }

            foreach (BootPayoutSnapshotContext context in bundle.SnapshotContexts)
            {
                UpsertSnapshotContextNoLock(context);
            }
            _state.OnDeckProofs = mergedCanonicalProofs;
            RebuildOnDeckListNoLock();
            SetAcceptedParentBlockHashesNoLock(GetCanonicalParentBlockHashesForReserveNoLock(), _state.CurrentTipBlockHash);
            _state.CandidateStateId = ComputeCandidateStateIdNoLock();
            CacheCurrentCandidateBundleNoLock();
            foreach (var proof in _state.OnDeckProofs)
            {
                RememberShareIdNoLock(proof.ShareId);
            }
            RequestDeferredSaveNoLock();

            imported = true;
            networkStatus = BuildNetworkStatusNoLock();
            onDeckSnapshot = ClonePayouts(_state.OnDeckList);
        }

        if (imported)
        {
            _logger.LogInformation("Merged candidate reserve proofs from {StateId} via {SourceEndpoint}.", bundle.StateId, sourceEndpoint);
            await _hubContext.Clients.All.SendAsync("UpdateOnDeck", onDeckSnapshot);
            await _hubContext.Clients.All.SendAsync("UpdateNetworkState", networkStatus);
        }

        return imported;
    }

    public async Task<bool> TryAdoptCurrentStateAsync(BootStateBundle bundle, string? observedTipBlockHash, long? observedTipBlockHeight, string sourceEndpoint)
    {
        BootVersionCompatibilityDto compatibility = EvaluateStateBundleCompatibility(bundle);
        if (!compatibility.CanSyncState)
        {
            _logger.LogWarning(
                "Rejected locked state bundle from {SourceEndpoint}: {Reason}.",
                sourceEndpoint,
                compatibility.Reason);
            RecordExternalNetworkEvent(
                "peer-version-mismatch",
                sourceEndpoint,
                $"Rejected locked state bundle: {compatibility.Reason}.",
                bundle.LockedByBlockHash ?? observedTipBlockHash,
                bundle.LockedByBlockHeight ?? observedTipBlockHeight);
            return false;
        }

        if (bundle.WinnersList.Count > _poolConfig.WinnersListSize ||
            bundle.ShareProofs.Count > _poolConfig.SnapshotProofSlotCount ||
            bundle.WorkSetProofs.Count > _poolConfig.WorkSetReserveLimit ||
            bundle.SnapshotContexts.Count > GetMaxSnapshotContextCountNoLock() ||
            (bundle.SnapshotFamilyMember?.BoundaryReserveProofs.Count ?? 0) > _poolConfig.WorkSetReserveLimit)
        {
            return false;
        }

        List<PayoutInfo> currentWinnersSnapshot;
        string? currentTipSnapshot;
        string currentStateSnapshot;
        int currentRoundSnapshot;
        lock (_sync)
        {
            currentWinnersSnapshot = ClonePayouts(_state.WinnersList);
            currentTipSnapshot = _state.CurrentTipBlockHash;
            currentStateSnapshot = _state.CurrentStateId;
            currentRoundSnapshot = _state.CurrentRoundNumber;
            string? incomingTip = NormalizeCanonicalBlockHash(bundle.LockedByBlockHash) ??
                NormalizeCanonicalBlockHash(observedTipBlockHash);
            if (_poolConfig.EnablePeerTipStaleProtection &&
                _state.ProvisionalTip != null &&
                BitcoinHashes.AreEquivalent(incomingTip, _state.ProvisionalTip.BlockHash))
            {
                RecordNetworkEventNoLock(
                    "state-adoption-deferred-peer-tip",
                    sourceEndpoint,
                    "Deferred peer locked-state adoption until the local Bitcoin node validates the provisional block.",
                    incomingTip,
                    bundle.LockedByBlockHeight ?? observedTipBlockHeight);
                RequestDeferredHistorySaveNoLock();
                return false;
            }
        }

        string? lockedTipSnapshot = NormalizeCanonicalBlockHash(bundle.LockedByBlockHash) ??
            NormalizeCanonicalBlockHash(observedTipBlockHash);
        string? observedTipSnapshot = NormalizeCanonicalBlockHash(observedTipBlockHash) ?? lockedTipSnapshot;
        long? lockedTipHeightSnapshot = bundle.LockedByBlockHeight ?? observedTipBlockHeight;
        bool localStateIsEmpty =
            currentWinnersSnapshot.Count == 0 ||
            (currentWinnersSnapshot.Count == 1 &&
             currentWinnersSnapshot[0].Difficulty <= 0 &&
             currentWinnersSnapshot[0].Value == GetSharedPayoutValueSatsNoLock(1));

        if (string.IsNullOrWhiteSpace(currentTipSnapshot) ||
            string.IsNullOrWhiteSpace(lockedTipSnapshot))
        {
            return false;
        }

        if (!localStateIsEmpty &&
            bundle.CurrentRoundNumber <= currentRoundSnapshot &&
            !string.IsNullOrWhiteSpace(observedTipSnapshot) &&
            !BitcoinHashes.AreEquivalent(currentTipSnapshot, observedTipSnapshot))
        {
            return false;
        }

        if (bundle.ShareProofs.Count == 0 && bundle.WinnersList.Count > 0)
        {
            _logger.LogWarning(
                "Rejected proofless locked state bundle {StateId} from {SourceEndpoint}.",
                bundle.StateId,
                sourceEndpoint);
            RecordExternalNetworkEvent(
                "state-proofless-rejected",
                sourceEndpoint,
                "Rejected remote payout state because it did not include the proofs required to reconstruct its Winners List.",
                lockedTipSnapshot,
                lockedTipHeightSnapshot);
            return false;
        }

        List<BootShareProof> validatedProofs;
        List<BootShareProof> validatedWorkSetProofs = [];
        List<PayoutInfo> expectedPayouts;
        string expectedStateId;
        string legacyExpectedStateId;
        double remoteLockedTotalDifficulty;
        try
        {
            IReadOnlyList<PayoutInfo> proofWinners = bundle.ProofWinnersList.Count > 0
                ? bundle.ProofWinnersList
                : currentWinnersSnapshot;
            List<string> lockedStateParentBlockHashes = NormalizeAcceptedParentBlockHashes(
                bundle.ValidParentBlockHashes
                    .Append(bundle.ParentBlockHash ?? string.Empty)
                    .Concat(bundle.ShareProofs.Select(proof => proof.PrevBlockHash)));
            validatedProofs = ValidateImportedProofs(
                bundle.ShareProofs,
                proofWinners,
                lockedStateParentBlockHashes,
                $"peer-locked:{sourceEndpoint}",
                bundle.SnapshotContexts);
            if (bundle.WorkSetProofs.Count > 0)
            {
                validatedWorkSetProofs = ValidateImportedProofs(
                    bundle.WorkSetProofs,
                    proofWinners,
                    lockedStateParentBlockHashes,
                    $"peer-workset:{sourceEndpoint}",
                    bundle.SnapshotContexts);
            }
            expectedPayouts = BuildPayoutsFromProofs(validatedProofs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rejected locked state bundle from {SourceEndpoint}.", sourceEndpoint);
            return false;
        }

        expectedStateId = ComputeStateIdNoLock(validatedProofs, lockedTipSnapshot);
        legacyExpectedStateId = ComputeStateIdNoLock(validatedProofs, null);
        bool stateIdMatches =
            string.Equals(expectedStateId, bundle.StateId, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(bundle.LockedByBlockHash) &&
             string.Equals(legacyExpectedStateId, bundle.StateId, StringComparison.OrdinalIgnoreCase));
        if (!stateIdMatches || !WinnersMatch(expectedPayouts, bundle.WinnersList))
        {
            return false;
        }

        if (bundle.ActiveSnapshotProofIds.Count > 0 &&
            !bundle.ActiveSnapshotProofIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                validatedProofs.Select(proof => proof.ShareId).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        remoteLockedTotalDifficulty = validatedProofs.Sum(x => x.Difficulty);

        bool hasLocalActiveFamily;
        lock (_sync)
        {
            hasLocalActiveFamily = GetActiveSnapshotFamilyNoLock() != null;
        }
        if (GetActiveConsensusVersion() >= BootProtocolVersions.ConsensusVersion &&
            bundle.SnapshotFamilyMember != null &&
            hasLocalActiveFamily)
        {
            return await TryReconcileSiblingSnapshotAsync(
                bundle,
                bundle.SnapshotFamilyMember,
                currentStateSnapshot,
                currentTipSnapshot,
                currentWinnersSnapshot,
                sourceEndpoint);
        }

        BootNetworkStatusDto networkStatus;
        List<PayoutInfo> winnersSnapshot;
        List<PayoutInfo> onDeckSnapshot;
        bool adopted = false;

        lock (_sync)
        {
            if (!string.Equals(currentStateSnapshot, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase) ||
                !BitcoinHashes.AreEquivalent(currentTipSnapshot, _state.CurrentTipBlockHash))
            {
                return false;
            }

            if (string.Equals(bundle.StateId, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(bundle.PaidSnapshotId ?? string.Empty, _state.LastPaidSnapshotId ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !bundle.PaidSnapshotProofIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                    _state.LastPaidSnapshotProofIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase))
            {
                RecordNetworkEventNoLock(
                    "state-paid-lineage-rejected",
                    sourceEndpoint,
                    "Rejected remote state because its paid lineage does not match locally validated payment history.",
                    lockedTipSnapshot,
                    lockedTipHeightSnapshot);
                RequestDeferredHistorySaveNoLock();
                return false;
            }

            if (validatedWorkSetProofs.Any(proof =>
                    _state.LastPaidSnapshotProofIds.Contains(proof.ShareId, StringComparer.OrdinalIgnoreCase)))
            {
                RecordNetworkEventNoLock(
                    "state-paid-proof-reintroduced",
                    sourceEndpoint,
                    "Rejected remote state because its unpaid reserve reintroduced a locally paid proof.",
                    lockedTipSnapshot,
                    lockedTipHeightSnapshot);
                RequestDeferredHistorySaveNoLock();
                return false;
            }

            double localLockedTotalDifficulty = _state.WinnersList.Sum(x => x.Difficulty);
            const double difficultyEpsilon = 0.0000001;
            bool proofBackedRemoteBeatsProoflessLocal =
                validatedProofs.Count > 0 &&
                bundle.CurrentRoundNumber == _state.CurrentRoundNumber &&
                !CurrentStateHasShareProofsNoLock();
            bool remoteLooksStronger =
                proofBackedRemoteBeatsProoflessLocal ||
                localStateIsEmpty ||
                bundle.CurrentRoundNumber > _state.CurrentRoundNumber ||
                (bundle.CurrentRoundNumber == _state.CurrentRoundNumber &&
                 !CurrentStateHasShareProofsNoLock() &&
                 remoteLockedTotalDifficulty > localLockedTotalDifficulty + difficultyEpsilon);
            if (!remoteLooksStronger)
            {
                return false;
            }

            string adoptedStateId = string.IsNullOrWhiteSpace(bundle.StateId)
                ? (string.IsNullOrWhiteSpace(bundle.LockedByBlockHash) ? legacyExpectedStateId : expectedStateId)
                : bundle.StateId;
            string adoptedActiveSnapshotId = string.IsNullOrWhiteSpace(bundle.ActiveSnapshotId)
                ? adoptedStateId
                : bundle.ActiveSnapshotId;

            _state.CurrentStateId = adoptedStateId;
            _state.CurrentRoundNumber = Math.Max(0, bundle.CurrentRoundNumber);
            _state.LastRotationUtc = bundle.CreatedAtUtc == default ? DateTime.UtcNow : bundle.CreatedAtUtc;
            _state.WinnersList = ClonePayouts(expectedPayouts);
            _state.CurrentTipBlockHash = observedTipSnapshot ?? currentTipSnapshot;
            _state.CurrentTipBlockHeight = observedTipBlockHeight ?? bundle.LockedByBlockHeight ?? _state.CurrentTipBlockHeight;
            TrimAcceptedParentBlockHashesToRoundNoLock(lockedTipSnapshot, _state.CurrentTipBlockHash);
            _state.ActiveSnapshotId = adoptedActiveSnapshotId;
            _state.ActiveSnapshotProofIds = (bundle.ActiveSnapshotProofIds ?? []).ToList();
            if (_state.ActiveSnapshotProofIds.Count == 0 && validatedProofs.Count > 0)
            {
                _state.ActiveSnapshotProofIds = validatedProofs.Select(proof => proof.ShareId).ToList();
            }
            foreach (BootPayoutSnapshotContext context in bundle.SnapshotContexts)
            {
                UpsertSnapshotContextNoLock(context);
            }
            _state.OnDeckProofs = SortAndTrimProofs(
                validatedWorkSetProofs.Count > 0 ? validatedWorkSetProofs : _state.OnDeckProofs,
                _poolConfig.WorkSetReserveLimit);
            RebuildOnDeckListNoLock();
            foreach (var proof in validatedProofs)
            {
                RememberShareIdNoLock(proof.ShareId);
            }
            foreach (var proof in _state.OnDeckProofs)
            {
                RememberShareIdNoLock(proof.ShareId);
            }

            BootStateBundle lockedBundle = CloneBundle(bundle);
            lockedBundle.ShareProofs = validatedProofs.Select(CloneProof).ToList();
            lockedBundle.WorkSetProofs = _state.OnDeckProofs.Select(CloneProof).ToList();
            lockedBundle.WinnersList = ClonePayouts(expectedPayouts);
            lockedBundle.ProofWinnersList = ClonePayouts(bundle.ProofWinnersList.Count > 0
                ? bundle.ProofWinnersList
                : currentWinnersSnapshot);
            lockedBundle.PreviousStateId = bundle.PreviousStateId;
            lockedBundle.CurrentRoundNumber = Math.Max(0, bundle.CurrentRoundNumber);
            lockedBundle.StateId = adoptedStateId;
            lockedBundle.TotalDifficulty = remoteLockedTotalDifficulty;
            lockedBundle.LockedByBlockHash = lockedTipSnapshot;
            lockedBundle.LockedByBlockHeight = lockedTipHeightSnapshot;
            lockedBundle.ParentBlockHash = BitcoinHashes.NormalizeHex(bundle.ParentBlockHash);
            lockedBundle.ParentBlockHeight = bundle.ParentBlockHeight;
            lockedBundle.ValidParentBlockHashes = GetAcceptedParentBlockHashesNoLock();
            lockedBundle.ActiveSnapshotId = adoptedActiveSnapshotId;
            lockedBundle.ActiveSnapshotProofIds = _state.ActiveSnapshotProofIds.ToList();
            lockedBundle.PaidSnapshotId = _state.LastPaidSnapshotId ?? string.Empty;
            lockedBundle.PaidSnapshotProofIds = _state.LastPaidSnapshotProofIds.ToList();
            lockedBundle.SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled;
            lockedBundle.PayoutVariant = BuildPayoutVariantNoLock();
            lockedBundle.SnapshotContexts = BuildSnapshotContextsForBundleNoLock(
                lockedBundle.ShareProofs.Concat(lockedBundle.WorkSetProofs),
                lockedBundle.SnapshotContexts);
            lockedBundle.Commitment = BuildCommitmentNoLock();
            UpsertArchivedBundleNoLock(lockedBundle);

            _state.CandidateStateId = ComputeCandidateStateIdNoLock();
            CacheCurrentCandidateBundleNoLock();
            RecordNetworkEventNoLock(
                "state-adopted",
                sourceEndpoint,
                proofBackedRemoteBeatsProoflessLocal
                    ? "Adopted a proof-backed peer current state over a proofless local state."
                    : "Adopted a fully proof-backed locked current state from a peer.",
                _state.CurrentTipBlockHash,
                _state.CurrentTipBlockHeight);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();

            winnersSnapshot = ClonePayouts(_state.WinnersList);
            onDeckSnapshot = ClonePayouts(_state.OnDeckList);
            networkStatus = BuildNetworkStatusNoLock();
            adopted = true;
        }

        if (adopted)
        {
            _logger.LogInformation("Adopted locked state {StateId} from {SourceEndpoint}.", bundle.StateId, sourceEndpoint);
            await _hubContext.Clients.All.SendAsync("UpdateWinners", winnersSnapshot);
            await _hubContext.Clients.All.SendAsync("UpdateOnDeck", onDeckSnapshot);
            await _hubContext.Clients.All.SendAsync("UpdateNetworkState", networkStatus);
            await _hubContext.Clients.All.SendAsync("UpdateRoundHistory", GetRoundHistory());
            await NotifyWinnersListChangedAsync($"adopted-state:{sourceEndpoint}");
        }

        return adopted;
    }

    private async Task<bool> TryReconcileSiblingSnapshotAsync(
        BootStateBundle bundle,
        BootSnapshotFamilyMember member,
        string currentStateSnapshot,
        string? currentTipSnapshot,
        IReadOnlyList<PayoutInfo> currentWinnersSnapshot,
        string sourceEndpoint)
    {
        BootSnapshotFamilyState? familySnapshot;
        HashSet<string> knownUnionIds;
        lock (_sync)
        {
            familySnapshot = GetActiveSnapshotFamilyNoLock();
            if (familySnapshot == null ||
                !familySnapshot.IsOpen ||
                !familySnapshot.BoundaryOnActiveChain ||
                member.ConsensusVersion != bundle.ConsensusVersion ||
                !string.Equals(member.PayoutVariant, bundle.PayoutVariant, StringComparison.OrdinalIgnoreCase) ||
                bundle.SupportFeeEnabled != _poolConfig.GridLabsSupportFeeEnabled ||
                !BootSnapshotReconciliation.MatchesFamily(familySnapshot, member) ||
                !BitcoinHashes.AreEquivalent(member.BoundaryBlockHash, _state.CurrentTipBlockHash) ||
                member.BoundaryBlockHeight != _state.CurrentTipBlockHeight)
            {
                _state.ReconciliationCounters.FamilyMismatchRejections++;
                RecordNetworkEventNoLock(
                    "snapshot-family-mismatch",
                    sourceEndpoint,
                    $"Rejected snapshot {member.SnapshotId} outside the active V2.2 reconciliation family.",
                    member.BoundaryBlockHash,
                    member.BoundaryBlockHeight);
                RequestDeferredHistorySaveNoLock();
                return false;
            }

            knownUnionIds = familySnapshot.ReconciledProofs
                .Select(proof => proof.ShareId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (member.BoundaryReserveProofs.All(proof => knownUnionIds.Contains(proof.ShareId)))
            {
                HashSet<string> claimedIds = member.BoundaryReserveProofs
                    .Select(proof => proof.ShareId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                List<BootShareProof> claimedKnownReserve = familySnapshot.ReconciledProofs
                    .Where(proof => claimedIds.Contains(proof.ShareId))
                    .OrderByDescending(proof => proof.Difficulty)
                    .ThenBy(proof => proof.ShareId, StringComparer.Ordinal)
                    .Take(_poolConfig.WorkSetReserveLimit)
                    .Select(CloneProof)
                    .ToList();
                List<BootShareProof> claimedSnapshotProofs = claimedKnownReserve
                    .Take(_poolConfig.SharedWinnerSlotCount)
                    .ToList();
                string claimedSnapshotId = ComputeStateIdNoLock(claimedSnapshotProofs, member.BoundaryBlockHash);
                if (!string.Equals(claimedSnapshotId, member.SnapshotId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(member.SnapshotId, bundle.ActiveSnapshotId, StringComparison.OrdinalIgnoreCase) ||
                    !WinnersMatch(BuildPayoutsFromProofs(claimedSnapshotProofs), bundle.WinnersList))
                {
                    return false;
                }

                AdmitNoOpFamilyMemberNoLock(familySnapshot, member.SnapshotId);
                RequestDeferredSaveNoLock();
                RequestDeferredHistorySaveNoLock();
                return true;
            }

            familySnapshot = CloneSnapshotFamily(familySnapshot);
        }

        List<string> boundaryParents = NormalizeAcceptedParentBlockHashes(
            bundle.ValidParentBlockHashes
                .Append(bundle.ParentBlockHash ?? string.Empty)
                .Concat(member.BoundaryReserveProofs.Select(proof => proof.PrevBlockHash)));
        List<BootShareProof> validatedBoundaryProofs;
        try
        {
            validatedBoundaryProofs = ValidateImportedProofs(
                member.BoundaryReserveProofs,
                currentWinnersSnapshot,
                boundaryParents,
                $"peer-family:{sourceEndpoint}",
                bundle.SnapshotContexts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rejected invalid V2.2 sibling boundary reserve from {SourceEndpoint}.", sourceEndpoint);
            return false;
        }

        List<BootShareProof> memberReserve = BootSnapshotReconciliation.Reconcile(
            [],
            validatedBoundaryProofs,
            familySnapshot.PaidProofIds,
            _poolConfig.WorkSetReserveLimit);
        List<BootShareProof> memberSnapshotProofs = memberReserve
            .Take(_poolConfig.SharedWinnerSlotCount)
            .Select(CloneProof)
            .ToList();
        string expectedMemberSnapshotId = ComputeStateIdNoLock(memberSnapshotProofs, member.BoundaryBlockHash);
        if (!string.Equals(expectedMemberSnapshotId, member.SnapshotId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(member.SnapshotId, bundle.ActiveSnapshotId, StringComparison.OrdinalIgnoreCase) ||
            !WinnersMatch(BuildPayoutsFromProofs(memberSnapshotProofs), bundle.WinnersList))
        {
            return false;
        }

        BootNetworkStatusDto networkStatus;
        List<PayoutInfo> winnersSnapshot;
        List<PayoutInfo> onDeckSnapshot;
        bool payoutChanged;
        lock (_sync)
        {
            if (!string.Equals(currentStateSnapshot, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase) ||
                !BitcoinHashes.AreEquivalent(currentTipSnapshot, _state.CurrentTipBlockHash))
            {
                return false;
            }

            BootSnapshotFamilyState? family = GetActiveSnapshotFamilyNoLock();
            if (family == null ||
                !family.IsOpen ||
                !family.BoundaryOnActiveChain ||
                !BootSnapshotReconciliation.MatchesFamily(family, member))
            {
                _state.ReconciliationCounters.FamilyMismatchRejections++;
                return false;
            }

            List<BootShareProof> reconciled = BootSnapshotReconciliation.Reconcile(
                family.ReconciledProofs,
                validatedBoundaryProofs,
                family.PaidProofIds,
                _poolConfig.WorkSetReserveLimit);
            int unionAdditions = reconciled.Count(proof =>
                !family.ReconciledProofs.Any(existing =>
                    string.Equals(existing.ShareId, proof.ShareId, StringComparison.OrdinalIgnoreCase)));
            if (unionAdditions == 0)
            {
                AdmitNoOpFamilyMemberNoLock(family, member.SnapshotId);
                RequestDeferredSaveNoLock();
                RequestDeferredHistorySaveNoLock();
                return true;
            }

            family.SiblingAdmissions++;
            family.UnionAdditions += unionAdditions;
            _state.ReconciliationCounters.SiblingAdmissions++;
            _state.ReconciliationCounters.UnionAdditions += unionAdditions;
            RememberFamilyMemberNoLock(family, member.SnapshotId, noOp: false);
            family.ReconciledProofs = reconciled;

            _state.OnDeckProofs = BootSnapshotReconciliation.Reconcile(
                _state.OnDeckProofs,
                reconciled,
                family.PaidProofIds,
                _poolConfig.WorkSetReserveLimit);
            foreach (BootPayoutSnapshotContext context in bundle.SnapshotContexts)
            {
                UpsertSnapshotContextNoLock(context);
            }

            BootPayoutSnapshotContext reconciledContext = BuildSnapshotContextFromProofsNoLock(
                reconciled,
                family.BoundaryBlockHash,
                family.BoundaryBlockHeight,
                DateTime.UtcNow,
                _state.CurrentRoundNumber,
                family.PredecessorSnapshotId);
            reconciledContext.FamilyId = family.FamilyId;
            payoutChanged = !_state.ActiveSnapshotProofIds.SequenceEqual(
                reconciledContext.ProofIds,
                StringComparer.OrdinalIgnoreCase);
            UpsertSnapshotContextNoLock(reconciledContext);

            if (payoutChanged)
            {
                _state.ActiveSnapshotId = reconciledContext.SnapshotId;
                _state.ActiveSnapshotProofIds = reconciledContext.ProofIds.ToList();
                _state.WinnersList = ClonePayouts(reconciledContext.WinnersList);
                _state.CurrentStateId = reconciledContext.SnapshotId;
                family.MemberSnapshotIds.RemoveAll(id =>
                    string.Equals(id, reconciledContext.SnapshotId, StringComparison.OrdinalIgnoreCase));
                RememberFamilyMemberNoLock(family, reconciledContext.SnapshotId, noOp: false);
                family.PayoutChanges++;
                family.ConvergenceCount++;
                _state.ReconciliationCounters.PayoutChanges++;
                _state.ReconciliationCounters.ConvergenceCount++;
            }

            RebuildOnDeckListNoLock();
            _state.CandidateStateId = ComputeCandidateStateIdNoLock();
            CacheCurrentCandidateBundleNoLock();
            RecordNetworkEventNoLock(
                payoutChanged ? "snapshot-reconciled" : "snapshot-family-union",
                sourceEndpoint,
                payoutChanged
                    ? $"Reconciled snapshot family {family.FamilyId}; activated payout snapshot {reconciledContext.SnapshotId}."
                    : $"Extended snapshot family {family.FamilyId} reserve without changing active payouts.",
                family.BoundaryBlockHash,
                family.BoundaryBlockHeight);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();

            networkStatus = BuildNetworkStatusNoLock();
            winnersSnapshot = ClonePayouts(_state.WinnersList);
            onDeckSnapshot = ClonePayouts(_state.OnDeckList);
        }

        await _hubContext.Clients.All.SendAsync("UpdateOnDeck", onDeckSnapshot);
        await _hubContext.Clients.All.SendAsync("UpdateNetworkState", networkStatus);
        if (payoutChanged)
        {
            await _hubContext.Clients.All.SendAsync("UpdateWinners", winnersSnapshot);
            await _hubContext.Clients.All.SendAsync("UpdateRoundHistory", GetRoundHistory());
            await NotifyWinnersListChangedAsync($"snapshot-reconciled:{sourceEndpoint}");
        }

        return true;
    }

    private void AdmitNoOpFamilyMemberNoLock(BootSnapshotFamilyState family, string snapshotId)
    {
        family.SiblingAdmissions++;
        family.NoOpAdmissions++;
        _state.ReconciliationCounters.SiblingAdmissions++;
        _state.ReconciliationCounters.NoOpAdmissions++;
        RememberFamilyMemberNoLock(family, snapshotId, noOp: true);
    }

    private void RememberFamilyMemberNoLock(BootSnapshotFamilyState family, string snapshotId, bool noOp)
    {
        if (!BootSnapshotReconciliation.TryRetainMemberId(family.MemberSnapshotIds, snapshotId))
        {
            if (noOp)
            {
                family.DroppedNoOpMembers++;
                _state.ReconciliationCounters.DroppedNoOpMembers++;
            }
        }
    }

    public async Task<bool> TryBootstrapCurrentStateAsync(BootStateBundle bundle, string? observedTipBlockHash, long? observedTipBlockHeight, string sourceEndpoint)
    {
        if (bundle.ShareProofs.Count == 0 || bundle.WinnersList.Count == 0)
        {
            RecordExternalNetworkEvent(
                "state-bootstrap-proofless-rejected",
                sourceEndpoint,
                "Rejected peer bootstrap because its payout state cannot be reconstructed from included proofs.",
                bundle.LockedByBlockHash ?? observedTipBlockHash,
                bundle.LockedByBlockHeight ?? observedTipBlockHeight);
            return false;
        }

        lock (_sync)
        {
            if (!string.Equals(bundle.PaidSnapshotId ?? string.Empty, _state.LastPaidSnapshotId ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !bundle.PaidSnapshotProofIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                    _state.LastPaidSnapshotProofIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase))
            {
                RecordNetworkEventNoLock(
                    "state-bootstrap-paid-lineage-rejected",
                    sourceEndpoint,
                    "Rejected peer bootstrap because paid history cannot be established from an untrusted state bundle.",
                    bundle.LockedByBlockHash ?? observedTipBlockHash,
                    bundle.LockedByBlockHeight ?? observedTipBlockHeight);
                RequestDeferredHistorySaveNoLock();
                return false;
            }
        }

        return await TryAdoptCurrentStateAsync(
            bundle,
            observedTipBlockHash,
            observedTipBlockHeight,
            sourceEndpoint);
    }

    public async Task<bool> TrySyncCurrentRoundMetadataAsync(
        string stateId,
        int remoteCurrentRoundNumber,
        DateTime? remoteLastRotationUtc,
        string sourceEndpoint)
    {
        if (string.IsNullOrWhiteSpace(stateId) || !remoteLastRotationUtc.HasValue || remoteLastRotationUtc.Value == default)
        {
            return false;
        }

        BootNetworkStatusDto? networkStatus = null;
        bool changed = false;

        lock (_sync)
        {
            if (!string.Equals(_state.CurrentStateId, stateId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!_state.LastRotationUtc.HasValue ||
                _state.LastRotationUtc.Value == default ||
                remoteLastRotationUtc.Value > _state.LastRotationUtc.Value)
            {
                _state.LastRotationUtc = remoteLastRotationUtc.Value;
                changed = true;
            }

            if (remoteCurrentRoundNumber > _state.CurrentRoundNumber)
            {
                _state.CurrentRoundNumber = remoteCurrentRoundNumber;
                changed = true;
            }

            if (changed)
            {
                RequestDeferredSaveNoLock();
                networkStatus = BuildNetworkStatusNoLock();
            }
        }

        if (changed && networkStatus != null)
        {
            _logger.LogInformation(
                "Synced current-round metadata for state {StateId} from {SourceEndpoint}.",
                stateId,
                sourceEndpoint);
            await _hubContext.Clients.All.SendAsync("UpdateNetworkState", networkStatus);
        }

        return changed;
    }

    private void LoadState()
    {
        lock (_sync)
        {
            if (File.Exists(BootPortalPaths.PoolStateFilePath))
            {
                if (TryLoadStateFromPathNoLock(BootPortalPaths.PoolStateFilePath, "primary"))
                {
                    LoadHistoryStateNoLock();
                    FinalizeLoadedStateNoLock("primary");
                    return;
                }

                string backupPath = GetPoolStateBackupPath();
                if (File.Exists(backupPath) && TryLoadStateFromPathNoLock(backupPath, "backup"))
                {
                    _logger.LogWarning(
                        "Recovered Boot protocol state from backup after failing to read the primary state file.");
                    LoadHistoryStateNoLock();
                    FinalizeLoadedStateNoLock("backup");
                    SaveStateNoLock();
                    return;
                }
            }

            InitializeDefaultsNoLock();
            SeedSeenSharesNoLock();
            SaveStateNoLock();
        }
    }

    private void FinalizeLoadedStateNoLock(string label)
    {
        int repairedContexts = RepairMissingWorkSetSnapshotContextsNoLock(DateTime.UtcNow);
        int removedProofs = RemoveUnrecoverableWorkSetProofsNoLock();
        (int invalidProofs, int canonicalizedProofs) = NormalizeWorkSetProofsNoLock();
        PruneSnapshotContextsNoLock();
        CacheCurrentCandidateBundleNoLock();
        if (repairedContexts > 0 ||
            removedProofs > 0 ||
            invalidProofs > 0 ||
            canonicalizedProofs > 0)
        {
            if (repairedContexts > 0)
            {
                _logger.LogWarning(
                    "Repaired {Count} missing snapshot context(s) for unpaid Work Set proofs while loading {Label} state.",
                    repairedContexts,
                    label);
            }

            if (removedProofs > 0)
            {
                _logger.LogWarning(
                    "Removed {Count} unpaid Work Set proof(s) with unrecoverable payout snapshot contexts while loading {Label} state.",
                    removedProofs,
                    label);
            }

            if (invalidProofs > 0)
            {
                _logger.LogWarning(
                    "Removed {Count} invalid unpaid Work Set proof(s) while loading {Label} state.",
                    invalidProofs,
                    label);
            }

            if (canonicalizedProofs > 0)
            {
                _logger.LogInformation(
                    "Canonicalized {Count} unpaid Work Set proof(s) while loading {Label} state.",
                    canonicalizedProofs,
                    label);
            }

            SaveStateNoLock();
        }
    }

    private void InitializeDefaultsNoLock()
    {
        _state = new PoolState
        {
            Metadata = new BootProtocolMetadata
            {
                NetworkId = _poolConfig.BootNetworkId,
                ProtocolVersion = GetActiveConsensusVersionNoLock()
            },
            BestShare = new BestShareRecord(),
            CurrentRoundNumber = 0,
            CurrentTipBlockHash = null,
            CurrentTipBlockHeight = null,
            LastTestingTriggerBlockHeight = null,
            LastRotationUtc = ResolveConfiguredGenesisRoundStartUtcNoLock(),
            GenesisRoundStartedUtc = ResolveConfiguredGenesisRoundStartUtcNoLock(),
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock()
        };

        _state.WinnersList = BuildGenesisWinnersListNoLock();
        _state.CurrentStateId = ComputeStateIdFromPayoutsNoLock(_state.WinnersList, null);
        _state.ActiveSnapshotId = _state.CurrentStateId;
        _state.ActiveSnapshotProofIds = [];
        _state.CandidateStateId = ComputeCandidateStateIdNoLock();
        UpsertSnapshotContextNoLock(new BootPayoutSnapshotContext
        {
            SnapshotId = _state.ActiveSnapshotId,
            CurrentRoundNumber = _state.CurrentRoundNumber,
            CreatedAtUtc = _state.LastRotationUtc ?? DateTime.UtcNow,
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock(),
            ProofIds = [],
            WinnersList = ClonePayouts(_state.WinnersList),
            FeeFreeWinnersList = RemoveSupportFeePayoutsNoLock(_state.WinnersList)
        });
        EnsureGenesisRoundStartNoLock(DateTime.UtcNow);
    }

    private List<PayoutInfo> BuildGenesisWinnersListNoLock()
    {
        string genesisAddress = GetGenesisFoundationAddress(_poolConfig.BitcoinNetwork);
        ulong reward = GetSharedPayoutValueSatsNoLock(1);
        return
        [
            new PayoutInfo
            {
                Value = reward,
                Address = genesisAddress,
                Username = genesisAddress
            }
        ];
    }

    private ulong GetBlockSubsidySatsNoLock()
    {
        return GetCurrentBlockSubsidySats(_poolConfig.BitcoinNetwork);
    }

    private ulong GetSharedPayoutValueSatsNoLock(int sharedPayoutCount)
    {
        return GetBlockSubsidySatsNoLock() / (ulong)Math.Max(2, _poolConfig.TotalPayoutSlotCount);
    }

    private void SaveStateNoLock([CallerMemberName] string? reason = null)
    {
        CombinedStateSaveSnapshot snapshot = CaptureFullStateSnapshotNoLock();
        string effectiveReason = reason ?? "unknown";
        WriteStateFileSnapshot(snapshot.Core, effectiveReason);
        WriteStateFileSnapshot(snapshot.History, $"{effectiveReason}-history", compact: true);
    }

    private void RequestDeferredSaveNoLock()
    {
        _deferredSavePending = true;
        if (_deferredSaveTask is { IsCompleted: false })
        {
            return;
        }

        _deferredSaveTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DeferredSaveInterval);
                StateFileSnapshot<PoolState>? snapshot = null;
                lock (_sync)
                {
                    if (!_deferredSavePending)
                    {
                        return;
                    }

                    _deferredSavePending = false;
                    snapshot = CaptureCoreStateSnapshotNoLock();
                }

                if (snapshot != null)
                {
                    WriteStateFileSnapshot(snapshot, "deferred");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred pool-state save failed.");
            }
        });
    }

    private void RequestDeferredHistorySaveNoLock()
    {
        _deferredHistorySavePending = true;
        if (_deferredHistorySaveTask is { IsCompleted: false })
        {
            return;
        }

        _deferredHistorySaveTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DeferredHistorySaveInterval);
                StateFileSnapshot<PoolStateHistory>? snapshot = null;
                lock (_sync)
                {
                    if (!_deferredHistorySavePending)
                    {
                        return;
                    }

                    _deferredHistorySavePending = false;
                    snapshot = CaptureHistoryStateSnapshotNoLock();
                }

                if (snapshot != null)
                {
                    WriteStateFileSnapshot(snapshot, "deferred-history", compact: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred pool-state history save failed.");
            }
        });
    }

    private void QueueRealtimeSend(Task sendTask, string updateName)
    {
        _ = sendTask.ContinueWith(
            completed =>
            {
                if (completed.Exception != null)
                {
                    _logger.LogDebug(
                        completed.Exception,
                        "Realtime dashboard update {UpdateName} failed.",
                        updateName);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private CombinedStateSaveSnapshot CaptureFullStateSnapshotNoLock()
    {
        return new CombinedStateSaveSnapshot
        {
            Core = CaptureCoreStateSnapshotNoLock(),
            History = CaptureHistoryStateSnapshotNoLock()
        };
    }

    private StateFileSnapshot<PoolState> CaptureCoreStateSnapshotNoLock()
    {
        _state.Metadata.NetworkId = _poolConfig.BootNetworkId;
        _state.Metadata.NodeId = _peerIdentity?.NodeId ?? _state.Metadata.NodeId;
        BootNodeVersionInfo localVersion = GetLocalVersionInfoNoLock();
        _state.Metadata.ProtocolVersion = localVersion.ProtocolVersion;
        _state.Metadata.ConsensusVersion = localVersion.ConsensusVersion;
        _state.Metadata.StateBundleSchemaVersion = localVersion.StateBundleSchemaVersion;
        _state.Metadata.HttpApiVersion = BootProtocolVersions.HttpApiVersion;
        _state.Metadata.PeerTransportVersion = BootProtocolVersions.PeerTransportVersion;
        _state.Metadata.UdpRelayVersion = BootProtocolVersions.UdpRelayVersion;
        _state.Metadata.ReleaseVersion = localVersion.ReleaseVersion;

        return new StateFileSnapshot<PoolState>
        {
            Payload = BuildCoreStateSnapshotNoLock(),
            TargetPath = BootPortalPaths.PoolStateFilePath,
            BackupPath = GetPoolStateBackupPath()
        };
    }

    private StateFileSnapshot<PoolStateHistory> CaptureHistoryStateSnapshotNoLock()
    {
        return new StateFileSnapshot<PoolStateHistory>
        {
            Payload = BuildHistoryStateSnapshotNoLock(),
            TargetPath = BootPortalPaths.PoolStateHistoryFilePath,
            BackupPath = GetPoolStateHistoryBackupPath()
        };
    }

    private void WriteStateFileSnapshot<T>(StateFileSnapshot<T> snapshot, string reason, bool compact = false)
    {
        var saveStopwatch = Stopwatch.StartNew();
        BootPortalPaths.EnsureParentDirectory(snapshot.TargetPath);
        string tempPath = $"{snapshot.TargetPath}.tmp";
        JsonSerializerOptions options = compact ? _compactJsonOptions : _jsonOptions;
        long bytes;
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, snapshot.Payload, options);
            stream.Flush(flushToDisk: true);
            bytes = stream.Length;
        }
        if (File.Exists(snapshot.TargetPath))
        {
            File.Replace(tempPath, snapshot.TargetPath, snapshot.BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, snapshot.TargetPath);
        }

        saveStopwatch.Stop();
        if (saveStopwatch.Elapsed.TotalMilliseconds >= SlowStateSaveWarningMs)
        {
            _logger.LogWarning(
                "Slow pool-state save: {DurationMs:F1} ms (reason={Reason}, bytes={Bytes}).",
                saveStopwatch.Elapsed.TotalMilliseconds,
                reason,
                bytes);
        }
    }

    private sealed class CombinedStateSaveSnapshot
    {
        public StateFileSnapshot<PoolState> Core { get; init; } = new();
        public StateFileSnapshot<PoolStateHistory> History { get; init; } = new();
    }

    private sealed class StateFileSnapshot<T>
    {
        public T Payload { get; init; } = default!;
        public string TargetPath { get; init; } = string.Empty;
        public string BackupPath { get; init; } = string.Empty;
    }

    private PoolState BuildCoreStateSnapshotNoLock()
    {
        return new PoolState
        {
            Metadata = new BootProtocolMetadata
            {
                NetworkId = _poolConfig.BootNetworkId,
                ProtocolVersion = GetActiveConsensusVersionNoLock(),
                ConsensusVersion = GetActiveConsensusVersionNoLock(),
                StateBundleSchemaVersion = BootProtocolVersions.GetStateBundleSchemaVersion(GetActiveConsensusVersionNoLock()),
                HttpApiVersion = BootProtocolVersions.HttpApiVersion,
                PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
                UdpRelayVersion = BootProtocolVersions.UdpRelayVersion,
                ReleaseVersion = GetLocalVersionInfoNoLock().ReleaseVersion
            },
            CurrentStateId = _state.CurrentStateId,
            CandidateStateId = _state.CandidateStateId,
            CurrentRoundNumber = _state.CurrentRoundNumber,
            CurrentTipBlockHash = _state.CurrentTipBlockHash,
            CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
            TrustedLocalTipBlockHash = _state.TrustedLocalTipBlockHash,
            TrustedLocalTipBlockHeight = _state.TrustedLocalTipBlockHeight,
            CurrentTipCompactTarget = _state.CurrentTipCompactTarget,
            ProvisionalTip = CloneProvisionalTip(_state.ProvisionalTip),
            LastTestingTriggerBlockHash = _state.LastTestingTriggerBlockHash,
            LastTestingTriggerBlockHeight = _state.LastTestingTriggerBlockHeight,
            LastGridPoolBlockHash = _state.LastGridPoolBlockHash,
            LastGridPoolBlockHeight = _state.LastGridPoolBlockHeight,
            LastGridPoolBlockUtc = _state.LastGridPoolBlockUtc,
            LastGridPoolBlockMinerAddress = _state.LastGridPoolBlockMinerAddress,
            LastGridPoolBlockDifficulty = _state.LastGridPoolBlockDifficulty,
            ActiveSnapshotId = _state.ActiveSnapshotId,
            LastPaidSnapshotId = _state.LastPaidSnapshotId,
            ActiveSnapshotProofIds = _state.ActiveSnapshotProofIds.ToList(),
            LastPaidSnapshotProofIds = _state.LastPaidSnapshotProofIds.ToList(),
            SupportFeeEnabled = _state.SupportFeeEnabled,
            PayoutVariant = _state.PayoutVariant,
            SnapshotContexts = _state.SnapshotContexts.Select(CloneSnapshotContext).ToList(),
            SnapshotFamilies = _state.SnapshotFamilies.Select(CloneSnapshotFamily).ToList(),
            ReconciliationCounters = CloneReconciliationCounters(_state.ReconciliationCounters),
            AcceptedParentBlockHashes = _state.AcceptedParentBlockHashes.ToList(),
            LastRotationUtc = _state.LastRotationUtc,
            GenesisRoundStartedUtc = _state.GenesisRoundStartedUtc,
            WinnersList = ClonePayouts(_state.WinnersList),
            OnDeckList = ClonePayouts(_state.OnDeckList),
            OnDeckProofs = _state.OnDeckProofs.Select(CloneProof).ToList(),
            Peers = _state.Peers.Select(ClonePeer).ToList(),
            KnownDatumPayoutAddresses = new Dictionary<string, string>(_state.KnownDatumPayoutAddresses, StringComparer.Ordinal),
            BestShare = CloneBestShare(_state.BestShare),
            RecentAcceptedShares = [],
            RecentRejectedShareDiagnostics = [],
            RecentCoinbaserDiagnostics = [],
            RecentDatumShareResponses = [],
            RecentDatumSessions = [],
            RecentNetworkEvents = [],
            RecentPeerRelayObservations = [],
            HashrateSamples = [],
            LocalDatumMinerHashrateSamples = [],
            ArchivedStateBundles = []
        };
    }

    private PoolStateHistory BuildHistoryStateSnapshotNoLock()
    {
        return new PoolStateHistory
        {
            RecentAcceptedShares = _state.RecentAcceptedShares.Select(CloneAcceptedShareTelemetry).ToList(),
            RecentRejectedShareDiagnostics = _state.RecentRejectedShareDiagnostics.Select(CloneShareDiagnostic).ToList(),
            RecentCoinbaserDiagnostics = _state.RecentCoinbaserDiagnostics.Select(CloneCoinbaserDiagnostic).ToList(),
            RecentDatumShareResponses = _state.RecentDatumShareResponses.Select(CloneDatumShareResponse).ToList(),
            RecentDatumSessions = _state.RecentDatumSessions.Select(CloneDatumSession).ToList(),
            RecentNetworkEvents = _state.RecentNetworkEvents.Select(CloneNetworkEvent).ToList(),
            RecentPeerRelayObservations = _state.RecentPeerRelayObservations.Select(ClonePeerRelayObservation).ToList(),
            HashrateSamples = _state.HashrateSamples.Select(CloneHashratePoint).ToList(),
            LocalDatumMinerHashrateSamples = _state.LocalDatumMinerHashrateSamples.Select(CloneLocalDatumMinerHashrateRollupPoint).ToList(),
            ArchivedStateBundles = _state.ArchivedStateBundles.Select(CloneBundle).ToList(),
            SnapshotContexts = _state.SnapshotContexts.Select(CloneSnapshotContext).ToList()
        };
    }

    private bool TryLoadStateFromPathNoLock(string path, string label)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            var loaded = JsonSerializer.Deserialize<PoolState>(stream);
            if (loaded == null)
            {
                return false;
            }

            _state = loaded;
            string persistedNodeId = _state.Metadata.NodeId?.Trim() ?? string.Empty;
            string currentNodeId = _peerIdentity?.NodeId?.Trim() ?? string.Empty;
            _identityChanged = !string.IsNullOrWhiteSpace(persistedNodeId) &&
                               !string.IsNullOrWhiteSpace(currentNodeId) &&
                               !string.Equals(persistedNodeId, currentNodeId, StringComparison.Ordinal);
            if (_identityChanged)
            {
                _logger.LogError("Node identity differs from the identity stored with existing pool state. Restore the prior keys before accepting mining traffic.");
            }
            _state.Metadata.NodeId = string.IsNullOrWhiteSpace(currentNodeId) ? persistedNodeId : currentNodeId;
            _state.Metadata.NetworkId = string.IsNullOrWhiteSpace(_state.Metadata.NetworkId)
                ? _poolConfig.BootNetworkId
                : _state.Metadata.NetworkId;
            BootNodeVersionInfo localVersion = GetLocalVersionInfoNoLock();
            _state.Metadata.ProtocolVersion = localVersion.ProtocolVersion;
            _state.Metadata.ConsensusVersion = localVersion.ConsensusVersion;
            _state.Metadata.StateBundleSchemaVersion = localVersion.StateBundleSchemaVersion;
            _state.Metadata.HttpApiVersion = BootProtocolVersions.HttpApiVersion;
            _state.Metadata.PeerTransportVersion = BootProtocolVersions.PeerTransportVersion;
            _state.Metadata.UdpRelayVersion = BootProtocolVersions.UdpRelayVersion;
            _state.Metadata.ReleaseVersion = localVersion.ReleaseVersion;
            string? loadedTip = NormalizeCanonicalBlockHash(_state.CurrentTipBlockHash);
            if (!string.IsNullOrWhiteSpace(_state.CurrentTipBlockHash) && string.IsNullOrWhiteSpace(loadedTip))
            {
                _logger.LogWarning(
                    "Discarding non-canonical persisted chain tip marker: {Tip}",
                    _state.CurrentTipBlockHash);
            }

            _state.CurrentTipBlockHash = loadedTip;
            _state.LastTestingTriggerBlockHash = NormalizeCanonicalBlockHash(_state.LastTestingTriggerBlockHash);
            _state.LastGridPoolBlockHash = NormalizeCanonicalBlockHash(_state.LastGridPoolBlockHash);
            _state.KnownDatumPayoutAddresses ??= [];
            _state.RecentAcceptedShares ??= [];
            _state.RecentRejectedShareDiagnostics ??= [];
            _state.RecentCoinbaserDiagnostics ??= [];
            _state.RecentDatumShareResponses ??= [];
            _state.RecentDatumSessions ??= [];
            _state.RecentNetworkEvents ??= [];
            _state.RecentPeerRelayObservations ??= [];
            _state.HashrateSamples ??= [];
            _state.LocalDatumMinerHashrateSamples ??= [];
            _state.Peers ??= [];
            _state.ActiveSnapshotProofIds ??= [];
            _state.LastPaidSnapshotProofIds ??= [];
            _state.SnapshotContexts ??= [];
            _state.SnapshotFamilies ??= [];
            _state.ReconciliationCounters ??= new BootSnapshotReconciliationCounters();
            EnsureGenesisRoundStartNoLock(DateTime.UtcNow);
            NormalizeNetworkSensitivePayoutValuesNoLock();
            NormalizeArchivedBundlesNoLock();
            MigrateSnapshotReserveStateNoLock(DateTime.UtcNow);
            EnsureRoundMetadataNoLock();
            UpdateKnownBlockHeightNoLock(_state.CurrentTipBlockHash, _state.CurrentTipBlockHeight);
            UpdateKnownBlockHeightNoLock(_state.LastTestingTriggerBlockHash, _state.LastTestingTriggerBlockHeight);
            // Seed trusted local tip from the persisted current tip so height-gated
            // consensus activation can count down immediately after restart. Fail-closed
            // behavior still applies when no tip height is known.
            _state.TrustedLocalTipBlockHash = NormalizeCanonicalBlockHash(_state.TrustedLocalTipBlockHash);
            if (string.IsNullOrWhiteSpace(_state.TrustedLocalTipBlockHash) &&
                !string.IsNullOrWhiteSpace(_state.CurrentTipBlockHash) &&
                _state.CurrentTipBlockHeight.HasValue)
            {
                _state.TrustedLocalTipBlockHash = _state.CurrentTipBlockHash;
                _state.TrustedLocalTipBlockHeight = _state.CurrentTipBlockHeight;
            }
            else if (!string.IsNullOrWhiteSpace(_state.TrustedLocalTipBlockHash) &&
                     !_state.TrustedLocalTipBlockHeight.HasValue &&
                     BitcoinHashes.AreEquivalent(_state.TrustedLocalTipBlockHash, _state.CurrentTipBlockHash) &&
                     _state.CurrentTipBlockHeight.HasValue)
            {
                _state.TrustedLocalTipBlockHeight = _state.CurrentTipBlockHeight;
            }
            _state.AcceptedParentBlockHashes = GetAcceptedParentBlockHashesNoLock();
            TrimAcceptedShareTelemetryNoLock(DateTime.UtcNow);
            TrimShareDiagnosticsNoLock(DateTime.UtcNow);
            TrimCoinbaserDiagnosticsNoLock(DateTime.UtcNow);
            TrimDatumShareResponsesNoLock(DateTime.UtcNow);
            FinalizeStaleDatumSessionsNoLock(DateTime.UtcNow, "service-restart", "Recovered open DATUM session from prior process state.");
            RebuildActiveDatumSessionIndexNoLock();
            TrimDatumSessionsNoLock(DateTime.UtcNow);
            TrimNetworkEventsNoLock(DateTime.UtcNow);
            TrimPeerRelayObservationsNoLock(DateTime.UtcNow);
            TrimHashrateSamplesNoLock(DateTime.UtcNow);
            TrimLocalDatumMinerHashrateSamplesNoLock(DateTime.UtcNow);
            RebuildLocalDatumHashrateRollupIndexNoLock();
            NormalizePeerAddressBookNoLock(DateTime.UtcNow);
            RebuildLocalDatumAddressHashrateNoLock();
            RebuildPeerRelayFirstArrivalsNoLock();
            _recentShareDiagnostics.Clear();
            _recentShareDiagnostics.AddRange(_state.RecentRejectedShareDiagnostics.Select(CloneShareDiagnostic));

            if (_state.OnDeckProofs.Count == 0 && _state.OnDeckList.Count > 0)
            {
                _state.OnDeckProofs = _state.OnDeckList.Select(CreatePlaceholderProofNoLock).ToList();
            }

            if (_state.WinnersList.Count == 0)
            {
                InitializeDefaultsNoLock();
            }

            _state.CurrentStateId = string.IsNullOrWhiteSpace(_state.CurrentStateId)
                ? ComputeStateIdFromPayoutsNoLock(_state.WinnersList, _state.CurrentTipBlockHash)
                : _state.CurrentStateId;
            _state.CandidateStateId = ComputeCandidateStateIdNoLock();
            CacheCurrentCandidateBundleNoLock();
            SeedSeenSharesNoLock();
            _logger.LogInformation("Loaded Boot protocol state from {Label} disk file.", label);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Boot protocol state from {Label} disk file.", label);
            return false;
        }
    }

    private void LoadHistoryStateNoLock()
    {
        if (File.Exists(BootPortalPaths.PoolStateHistoryFilePath))
        {
            if (TryLoadHistoryFromPathNoLock(BootPortalPaths.PoolStateHistoryFilePath, "history-primary"))
            {
                return;
            }

            string backupPath = GetPoolStateHistoryBackupPath();
            if (File.Exists(backupPath) && TryLoadHistoryFromPathNoLock(backupPath, "history-backup"))
            {
                _logger.LogWarning(
                    "Recovered Boot protocol history from backup after failing to read the primary history file.");
                SaveStateNoLock();
            }
        }
    }

    private bool TryLoadHistoryFromPathNoLock(string path, string label)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            var loaded = JsonSerializer.Deserialize<PoolStateHistory>(stream);
            if (loaded == null)
            {
                return false;
            }

            _state.RecentAcceptedShares = loaded.RecentAcceptedShares ?? [];
            _state.RecentRejectedShareDiagnostics = loaded.RecentRejectedShareDiagnostics ?? [];
            _state.RecentCoinbaserDiagnostics = loaded.RecentCoinbaserDiagnostics ?? [];
            _state.RecentDatumShareResponses = loaded.RecentDatumShareResponses ?? [];
            _state.RecentDatumSessions = loaded.RecentDatumSessions ?? [];
            _state.RecentNetworkEvents = loaded.RecentNetworkEvents ?? [];
            _state.RecentPeerRelayObservations = loaded.RecentPeerRelayObservations ?? [];
            _state.HashrateSamples = loaded.HashrateSamples ?? [];
            _state.LocalDatumMinerHashrateSamples = loaded.LocalDatumMinerHashrateSamples ?? [];
            _state.ArchivedStateBundles = loaded.ArchivedStateBundles ?? [];
            if (loaded.SnapshotContexts is { Count: > 0 })
            {
                foreach (BootPayoutSnapshotContext context in loaded.SnapshotContexts)
                {
                    UpsertSnapshotContextNoLock(context);
                }
            }

            EnsureGenesisRoundStartNoLock(DateTime.UtcNow);
            NormalizeNetworkSensitivePayoutValuesNoLock();
            NormalizeArchivedBundlesNoLock();
            MigrateSnapshotReserveStateNoLock(DateTime.UtcNow);
            EnsureRoundMetadataNoLock();
            TrimAcceptedShareTelemetryNoLock(DateTime.UtcNow);
            TrimShareDiagnosticsNoLock(DateTime.UtcNow);
            TrimCoinbaserDiagnosticsNoLock(DateTime.UtcNow);
            TrimDatumShareResponsesNoLock(DateTime.UtcNow);
            FinalizeStaleDatumSessionsNoLock(DateTime.UtcNow, "service-restart", "Recovered open DATUM session from prior process history.");
            RebuildActiveDatumSessionIndexNoLock();
            TrimDatumSessionsNoLock(DateTime.UtcNow);
            TrimNetworkEventsNoLock(DateTime.UtcNow);
            TrimPeerRelayObservationsNoLock(DateTime.UtcNow);
            TrimHashrateSamplesNoLock(DateTime.UtcNow);
            TrimLocalDatumMinerHashrateSamplesNoLock(DateTime.UtcNow);
            RebuildLocalDatumHashrateRollupIndexNoLock();
            RebuildLocalDatumAddressHashrateNoLock();
            RebuildPeerRelayFirstArrivalsNoLock();
            _recentShareDiagnostics.Clear();
            // Rewrite once after loading so retention changes compact legacy
            // history files without requiring a later mining or peer event.
            RequestDeferredHistorySaveNoLock();
            _recentShareDiagnostics.AddRange(_state.RecentRejectedShareDiagnostics.Select(CloneShareDiagnostic));
            _logger.LogInformation("Loaded Boot protocol history from {Label} disk file.", label);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Boot protocol history from {Label} disk file.", label);
            return false;
        }
    }

    private string GetPoolStateBackupPath()
    {
        return $"{BootPortalPaths.PoolStateFilePath}.bak";
    }

    private string GetPoolStateHistoryBackupPath()
    {
        return $"{BootPortalPaths.PoolStateHistoryFilePath}.bak";
    }

    private void RebuildOnDeckListNoLock()
    {
        _state.OnDeckProofs = SortAndTrimProofs(_state.OnDeckProofs, _poolConfig.WorkSetReserveLimit);
        _state.OnDeckList = BuildFeeFreePayoutsFromProofs(_state.OnDeckProofs);
    }

    private bool HasSnapshotContextNoLock(string? snapshotId)
    {
        return !string.IsNullOrWhiteSpace(snapshotId) &&
            _state.SnapshotContexts.Any(context => string.Equals(context.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
    }

    private BootPayoutSnapshotContext? GetSnapshotContextNoLock(string? snapshotId)
    {
        return string.IsNullOrWhiteSpace(snapshotId)
            ? null
            : _state.SnapshotContexts.FirstOrDefault(context =>
                string.Equals(context.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
    }

    private BootSnapshotFamilyState? GetActiveSnapshotFamilyNoLock()
    {
        string? familyId = GetSnapshotContextNoLock(_state.ActiveSnapshotId)?.FamilyId;
        return string.IsNullOrWhiteSpace(familyId)
            ? null
            : _state.SnapshotFamilies.FirstOrDefault(family =>
                string.Equals(family.FamilyId, familyId, StringComparison.OrdinalIgnoreCase));
    }

    private void UpsertLocalSnapshotFamilyNoLock(
        BootPayoutSnapshotContext context,
        IEnumerable<BootShareProof> boundaryReserveProofs)
    {
        if (GetActiveConsensusVersionNoLock() < BootProtocolVersions.ConsensusVersion || string.IsNullOrWhiteSpace(context.FamilyId))
        {
            return;
        }

        BootSnapshotFamilyState? family = _state.SnapshotFamilies.FirstOrDefault(existing =>
            string.Equals(existing.FamilyId, context.FamilyId, StringComparison.OrdinalIgnoreCase));
        if (family == null)
        {
            family = new BootSnapshotFamilyState
            {
                FamilyId = context.FamilyId,
                ConsensusVersion = GetActiveConsensusVersionNoLock(),
                NetworkId = BuildSnapshotFamilyNetworkIdNoLock(),
                PredecessorSnapshotId = context.PreviousSnapshotId,
                BoundaryBlockHash = NormalizeCanonicalBlockHash(context.LockedByBlockHash) ?? string.Empty,
                BoundaryBlockHeight = context.LockedByBlockHeight ?? 0,
                PayoutVariant = context.PayoutVariant,
                IsOpen = true,
                BoundaryOnActiveChain = true
            };
            _state.SnapshotFamilies.Insert(0, family);
        }

        if (!family.MemberSnapshotIds.Contains(context.SnapshotId, StringComparer.OrdinalIgnoreCase))
        {
            family.MemberSnapshotIds.Add(context.SnapshotId);
        }

        family.ReconciledProofs = BootSnapshotReconciliation.Reconcile(
            family.ReconciledProofs,
            boundaryReserveProofs,
            family.PaidProofIds,
            _poolConfig.WorkSetReserveLimit);
        PruneSnapshotFamiliesNoLock();
    }

    private void PruneSnapshotFamiliesNoLock()
    {
        int limit = Math.Max(4, _poolConfig.MaxStateBundleHistory);
        HashSet<string> activeIds = _state.SnapshotContexts
            .Where(context => string.Equals(context.SnapshotId, _state.ActiveSnapshotId, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(context.SnapshotId, _state.LastPaidSnapshotId, StringComparison.OrdinalIgnoreCase))
            .Select(context => context.FamilyId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _state.SnapshotFamilies = _state.SnapshotFamilies
            .OrderByDescending(family => activeIds.Contains(family.FamilyId))
            .ThenByDescending(family => family.BoundaryBlockHeight)
            .Take(limit)
            .Select(CloneSnapshotFamily)
            .ToList();
    }

    private BootSnapshotFamilyMember? BuildActiveSnapshotFamilyMemberNoLock()
    {
        BootSnapshotFamilyState? family = GetActiveSnapshotFamilyNoLock();
        if (family == null)
        {
            return null;
        }

        return new BootSnapshotFamilyMember
        {
            FamilyId = family.FamilyId,
            ConsensusVersion = family.ConsensusVersion,
            NetworkId = family.NetworkId,
            PredecessorSnapshotId = family.PredecessorSnapshotId,
            BoundaryBlockHash = family.BoundaryBlockHash,
            BoundaryBlockHeight = family.BoundaryBlockHeight,
            PayoutVariant = family.PayoutVariant,
            SnapshotId = _state.ActiveSnapshotId,
            BoundaryReserveProofs = family.ReconciledProofs.Select(CloneProof).ToList()
        };
    }

    private bool IsNewDirectIngressOutsideActiveParentNoLock(string? shareId, string? parentBlockHash)
    {
        if (string.IsNullOrWhiteSpace(parentBlockHash))
        {
            return false;
        }

        if (_state.OnDeckProofs.Any(proof =>
                string.Equals(proof.ShareId, shareId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string? currentTip = NormalizeCanonicalBlockHash(_state.CurrentTipBlockHash);
        return !string.IsNullOrWhiteSpace(currentTip) &&
               !BitcoinHashes.AreEquivalent(parentBlockHash, currentTip);
    }

    private void RestorePredecessorForRemovedBoundaryNoLock(
        string source,
        string replacementBlockHash,
        long replacementBlockHeight)
    {
        while (true)
        {
            BootSnapshotFamilyState? removedFamily = GetActiveSnapshotFamilyNoLock();
            if (removedFamily == null ||
                removedFamily.BoundaryBlockHeight < replacementBlockHeight ||
                (removedFamily.BoundaryBlockHeight == replacementBlockHeight &&
                 BitcoinHashes.AreEquivalent(removedFamily.BoundaryBlockHash, replacementBlockHash)))
            {
                return;
            }

            removedFamily.IsOpen = false;
            removedFamily.BoundaryOnActiveChain = false;
            BootPayoutSnapshotContext? predecessor = GetSnapshotContextNoLock(removedFamily.PredecessorSnapshotId);
            if (predecessor == null)
            {
                return;
            }

            _state.ActiveSnapshotId = predecessor.SnapshotId;
            _state.ActiveSnapshotProofIds = predecessor.ProofIds.ToList();
            _state.WinnersList = ClonePayouts(predecessor.WinnersList);
            _state.CurrentStateId = predecessor.SnapshotId;
            _state.CurrentRoundNumber = Math.Max(0, predecessor.CurrentRoundNumber);
            _state.CurrentTipBlockHash = NormalizeCanonicalBlockHash(predecessor.LockedByBlockHash);
            _state.CurrentTipBlockHeight = predecessor.LockedByBlockHeight;

            RecordNetworkEventNoLock(
                "snapshot-family-reorg",
                source,
                $"Deactivated snapshot family {removedFamily.FamilyId} after boundary {removedFamily.BoundaryBlockHash} left the active chain.",
                replacementBlockHash,
                replacementBlockHeight);
        }
    }

    private void EnsureActiveSnapshotNoLock(DateTime nowUtc)
    {
        _state.SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled;
        _state.PayoutVariant = BuildPayoutVariantNoLock();
        _state.ActiveSnapshotProofIds ??= [];
        _state.LastPaidSnapshotProofIds ??= [];
        _state.SnapshotContexts ??= [];

        if (!string.IsNullOrWhiteSpace(_state.ActiveSnapshotId) &&
            HasSnapshotContextNoLock(_state.ActiveSnapshotId))
        {
            return;
        }

        var context = new BootPayoutSnapshotContext
        {
            SnapshotId = string.IsNullOrWhiteSpace(_state.CurrentStateId)
                ? ComputeStateIdFromPayoutsNoLock(_state.WinnersList, _state.CurrentTipBlockHash)
                : _state.CurrentStateId,
            PreviousSnapshotId = string.Empty,
            CurrentRoundNumber = _state.CurrentRoundNumber,
            LockedByBlockHash = _state.CurrentTipBlockHash,
            LockedByBlockHeight = _state.CurrentTipBlockHeight,
            CreatedAtUtc = _state.LastRotationUtc ?? nowUtc,
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock(),
            ProofIds = _state.ActiveSnapshotProofIds.ToList(),
            WinnersList = ClonePayouts(_state.WinnersList),
            FeeFreeWinnersList = RemoveSupportFeePayoutsNoLock(_state.WinnersList)
        };

        _state.ActiveSnapshotId = context.SnapshotId;
        UpsertSnapshotContextNoLock(context);
    }

    private BootPayoutSnapshotContext BuildSnapshotContextFromWorkSetNoLock(
        string? blockHash,
        long? blockHeight,
        DateTime createdUtc,
        int currentRoundNumber)
    {
        return BuildSnapshotContextFromProofsNoLock(
            _state.OnDeckProofs,
            blockHash,
            blockHeight,
            createdUtc,
            currentRoundNumber);
    }

    private BootPayoutSnapshotContext BuildSnapshotContextFromProofsNoLock(
        IEnumerable<BootShareProof> sourceProofs,
        string? blockHash,
        long? blockHeight,
        DateTime createdUtc,
        int currentRoundNumber,
        string? predecessorSnapshotId = null)
    {
        List<BootShareProof> source = sourceProofs.Select(CloneProof).ToList();
        List<BootShareProof> feeFreeSnapshotProofs = SortAndTrimProofs(source, _poolConfig.SnapshotProofSlotCount);
        List<BootShareProof> paidSnapshotProofs = SortAndTrimProofs(source, _poolConfig.SharedWinnerSlotCount);
        string snapshotId = ComputeStateIdNoLock(paidSnapshotProofs, blockHash);
        return new BootPayoutSnapshotContext
        {
            SnapshotId = snapshotId,
            PreviousSnapshotId = predecessorSnapshotId ?? _state.ActiveSnapshotId,
            CurrentRoundNumber = currentRoundNumber,
            LockedByBlockHash = NormalizeCanonicalBlockHash(blockHash),
            LockedByBlockHeight = blockHeight,
            CreatedAtUtc = createdUtc,
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock(),
            ProofIds = paidSnapshotProofs.Select(proof => proof.ShareId).ToList(),
            WinnersList = BuildPayoutsFromProofs(paidSnapshotProofs, includeSupportFee: _poolConfig.GridLabsSupportFeeEnabled),
            FeeFreeWinnersList = BuildFeeFreePayoutsFromProofs(feeFreeSnapshotProofs)
        };
    }

    private bool ApplySnapshotFromWorkSetNoLock(
        string? blockHash,
        long? blockHeight,
        string source,
        DateTime createdUtc,
        bool advanceRound,
        IReadOnlyCollection<BootShareProof>? frozenProofs = null)
    {
        string? normalizedBlockHash = NormalizeCanonicalBlockHash(blockHash);
        int nextRoundNumber = advanceRound ? _state.CurrentRoundNumber + 1 : _state.CurrentRoundNumber;
        BootPayoutSnapshotContext context = frozenProofs == null
            ? BuildSnapshotContextFromWorkSetNoLock(
                normalizedBlockHash,
                blockHeight,
                createdUtc,
                nextRoundNumber)
            : BuildSnapshotContextFromProofsNoLock(
                frozenProofs,
                normalizedBlockHash,
                blockHeight,
                createdUtc,
                nextRoundNumber);

        if (GetActiveConsensusVersionNoLock() >= BootProtocolVersions.ConsensusVersion &&
            !string.IsNullOrWhiteSpace(normalizedBlockHash) &&
            blockHeight.HasValue)
        {
            context.FamilyId = BootSnapshotReconciliation.ComputeFamilyId(
                GetActiveConsensusVersionNoLock(),
                BuildSnapshotFamilyNetworkIdNoLock(),
                context.PreviousSnapshotId,
                normalizedBlockHash,
                blockHeight.Value,
                context.PayoutVariant);
            UpsertLocalSnapshotFamilyNoLock(context, frozenProofs ?? _state.OnDeckProofs);
        }

        bool changed = !string.Equals(context.SnapshotId, _state.ActiveSnapshotId, StringComparison.OrdinalIgnoreCase);
        _state.CurrentTipBlockHash = normalizedBlockHash ?? _state.CurrentTipBlockHash;
        _state.CurrentTipBlockHeight = blockHeight ?? _state.CurrentTipBlockHeight;
        _state.LastRotationUtc = createdUtc;
        _state.CurrentRoundNumber = nextRoundNumber;
        _state.ActiveSnapshotId = context.SnapshotId;
        _state.ActiveSnapshotProofIds = context.ProofIds.ToList();
        _state.SupportFeeEnabled = context.SupportFeeEnabled;
        _state.PayoutVariant = context.PayoutVariant;
        _state.WinnersList = ClonePayouts(context.WinnersList);
        _state.CurrentStateId = context.SnapshotId;
        UpsertSnapshotContextNoLock(context);
        RebuildOnDeckListNoLock();
        _state.CandidateStateId = ComputeCandidateStateIdNoLock();
        CacheCurrentCandidateBundleNoLock();
        RecordNetworkEventNoLock(
            "payout-snapshot",
            source,
            $"Activated payout snapshot {context.SnapshotId} with {context.ProofIds.Count} proof(s).",
            normalizedBlockHash,
            blockHeight,
            createdUtc);
        return changed;
    }

    private void ApplyPaidSnapshotRemovalNoLock(
        string source,
        string? blockHash,
        long? blockHeight,
        DateTime nowUtc,
        string? provenSnapshotId = null)
    {
        EnsureActiveSnapshotNoLock(nowUtc);
        string paidSnapshotId = string.IsNullOrWhiteSpace(provenSnapshotId)
            ? _state.ActiveSnapshotId
            : provenSnapshotId;
        BootPayoutSnapshotContext? paidContext = GetSnapshotContextNoLock(paidSnapshotId);
        if (paidContext == null)
        {
            throw new InvalidOperationException($"Cannot remove payment for unknown payout snapshot {paidSnapshotId}.");
        }

        List<string> paidProofIds = paidContext.ProofIds.ToList();
        if (paidProofIds.Count > 0)
        {
            HashSet<string> paid = paidProofIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _state.OnDeckProofs = _state.OnDeckProofs
                .Where(proof => !paid.Contains(proof.ShareId))
                .Select(CloneProof)
                .ToList();
        }

        _state.LastPaidSnapshotId = paidSnapshotId;
        _state.LastPaidSnapshotProofIds = paidProofIds;
        BootSnapshotFamilyState? paidFamily = _state.SnapshotFamilies.FirstOrDefault(family =>
            family.MemberSnapshotIds.Contains(paidSnapshotId, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(family.FamilyId, GetSnapshotContextNoLock(paidSnapshotId)?.FamilyId, StringComparison.OrdinalIgnoreCase));
        if (paidFamily != null)
        {
            paidFamily.PaidProofIds = paidFamily.PaidProofIds
                .Concat(paidProofIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            paidFamily.ReconciledProofs = paidFamily.ReconciledProofs
                .Where(proof => !paidProofIds.Contains(proof.ShareId, StringComparer.OrdinalIgnoreCase))
                .Select(CloneProof)
                .ToList();
            paidFamily.IsOpen = false;
        }
        RecordNetworkEventNoLock(
            "snapshot-paid",
            source,
            $"GridPool block paid snapshot {paidSnapshotId}; removed {paidProofIds.Count} paid proof(s) from the unpaid Work Set.",
            blockHash,
            blockHeight,
            nowUtc);
    }

    private void UpsertSnapshotContextNoLock(BootPayoutSnapshotContext context)
    {
        if (string.IsNullOrWhiteSpace(context.SnapshotId))
        {
            return;
        }

        _state.SnapshotContexts.RemoveAll(existing =>
            string.Equals(existing.SnapshotId, context.SnapshotId, StringComparison.OrdinalIgnoreCase));
        _state.SnapshotContexts.Insert(0, CloneSnapshotContext(context));
        PruneSnapshotContextsNoLock();
    }

    private void PruneSnapshotContextsNoLock()
    {
        HashSet<string> protectedIds = new(StringComparer.OrdinalIgnoreCase);
        void protect(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                protectedIds.Add(id);
            }
        }

        protect(_state.ActiveSnapshotId);
        protect(_state.LastPaidSnapshotId);
        protect(GetSnapshotContextNoLock(_state.ActiveSnapshotId)?.PreviousSnapshotId);
        foreach (BootSnapshotFamilyState family in _state.SnapshotFamilies.Where(family => family.IsOpen))
        {
            protect(family.PredecessorSnapshotId);
        }
        foreach (BootShareProof proof in _state.OnDeckProofs)
        {
            protect(proof.PayoutSnapshotId);
        }

        int maxContexts = GetMaxSnapshotContextCountNoLock();
        List<BootPayoutSnapshotContext> protectedContexts = _state.SnapshotContexts
            .Where(context => protectedIds.Contains(context.SnapshotId))
            .GroupBy(context => context.SnapshotId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(context => context.CreatedAtUtc).First())
            .OrderByDescending(context => context.CreatedAtUtc)
            .ToList();
        HashSet<string> retainedIds = protectedContexts
            .Select(context => context.SnapshotId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int unprotectedLimit = Math.Max(0, maxContexts - protectedContexts.Count);
        List<BootPayoutSnapshotContext> unprotectedContexts = _state.SnapshotContexts
            .Where(context => !retainedIds.Contains(context.SnapshotId) && context.ProofIds.Count > 0)
            .GroupBy(context => context.SnapshotId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(context => context.CreatedAtUtc).First())
            .OrderByDescending(context => context.CreatedAtUtc)
            .Take(unprotectedLimit)
            .ToList();

        _state.SnapshotContexts = protectedContexts
            .Concat(unprotectedContexts)
            .Select(CloneSnapshotContext)
            .ToList();
    }

    private int GetMaxSnapshotContextCountNoLock()
    {
        return Math.Max(_poolConfig.MaxStateBundleHistory, _poolConfig.WorkSetReserveMultiplier * 16);
    }

    private int RepairMissingWorkSetSnapshotContextsNoLock(DateTime nowUtc)
    {
        HashSet<string> existingContextIds = _state.SnapshotContexts
            .Select(context => context.SnapshotId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<IGrouping<string, BootShareProof>> missingGroups = _state.OnDeckProofs
            .Where(proof => !string.IsNullOrWhiteSpace(proof.PayoutSnapshotId) &&
                            !existingContextIds.Contains(proof.PayoutSnapshotId))
            .GroupBy(proof => proof.PayoutSnapshotId!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int repaired = 0;
        foreach (IGrouping<string, BootShareProof> group in missingGroups)
        {
            BootShareProof? contextSource = group
                .OrderByDescending(proof => proof.Difficulty)
                .FirstOrDefault(proof => !string.IsNullOrWhiteSpace(proof.CoinbaseHex));
            if (contextSource == null)
            {
                continue;
            }

            List<PayoutInfo> winners = TryBuildSnapshotWinnersFromProofCoinbaseNoLock(contextSource);
            if (winners.Count == 0)
            {
                continue;
            }

            _state.SnapshotContexts.Insert(0, new BootPayoutSnapshotContext
            {
                SnapshotId = group.Key,
                CurrentRoundNumber = _state.CurrentRoundNumber,
                CreatedAtUtc = contextSource.Timestamp == default ? nowUtc : contextSource.Timestamp,
                SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
                PayoutVariant = "recovered-from-local-proof",
                ProofIds = group
                    .Select(proof => proof.ShareId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                WinnersList = winners,
                FeeFreeWinnersList = RemoveSupportFeePayoutsNoLock(winners)
            });
            existingContextIds.Add(group.Key);
            repaired++;
        }

        return repaired;
    }

    private int RemoveUnrecoverableWorkSetProofsNoLock()
    {
        HashSet<string> existingContextIds = _state.SnapshotContexts
            .Select(context => context.SnapshotId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int before = _state.OnDeckProofs.Count;
        _state.OnDeckProofs = _state.OnDeckProofs
            .Where(proof => string.IsNullOrWhiteSpace(proof.PayoutSnapshotId) ||
                            existingContextIds.Contains(proof.PayoutSnapshotId))
            .ToList();
        int removed = before - _state.OnDeckProofs.Count;
        if (removed <= 0)
        {
            return 0;
        }

        RebuildOnDeckListNoLock();
        _state.CandidateStateId = ComputeCandidateStateIdNoLock();
        return removed;
    }

    private (int InvalidProofs, int CanonicalizedProofs) NormalizeWorkSetProofsNoLock()
    {
        if (_state.OnDeckProofs.Count == 0)
        {
            return (0, 0);
        }

        HashSet<string> recoveredContextIds = _state.SnapshotContexts
            .Where(context => string.Equals(context.PayoutVariant, "recovered-from-local-proof", StringComparison.OrdinalIgnoreCase))
            .Select(context => context.SnapshotId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (recoveredContextIds.Count == 0)
        {
            return (0, 0);
        }

        List<string> validationParentBlockHashes = NormalizeAcceptedParentBlockHashes(
            _state.AcceptedParentBlockHashes
                .Append(_state.CurrentTipBlockHash)
                .Concat(_state.OnDeckProofs.Select(proof => proof.PrevBlockHash)));
        var validProofs = new List<BootShareProof>(_state.OnDeckProofs.Count);
        int invalidProofs = 0;
        int canonicalizedProofs = 0;

        foreach (BootShareProof proof in _state.OnDeckProofs)
        {
            if (string.IsNullOrWhiteSpace(proof.PayoutSnapshotId) ||
                !recoveredContextIds.Contains(proof.PayoutSnapshotId))
            {
                validProofs.Add(CloneProof(proof));
                continue;
            }

            SnapshotValidationResult snapshotValidation;
            try
            {
                snapshotValidation = ValidateProofAgainstKnownSnapshots(
                    proof,
                    _state.WinnersList,
                    _state.SnapshotContexts,
                    validationParentBlockHashes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Removed malformed unpaid Work Set proof {ShareId} while loading state.",
                    proof.ShareId);
                invalidProofs++;
                continue;
            }

            BootShareValidationResult validation = snapshotValidation.Validation;
            if (!validation.IsValid)
            {
                invalidProofs++;
                continue;
            }

            string source = string.IsNullOrWhiteSpace(proof.Source) ? "state-load" : proof.Source;
            string? payoutSnapshotId = string.IsNullOrWhiteSpace(snapshotValidation.SnapshotId)
                ? proof.PayoutSnapshotId
                : snapshotValidation.SnapshotId;
            BootShareProof canonicalProof = CreateProofNoLock(validation, source, proof.Timestamp, payoutSnapshotId);
            if (!ProofConsensusFieldsMatch(proof, canonicalProof))
            {
                canonicalizedProofs++;
            }

            validProofs.Add(canonicalProof);
        }

        List<BootShareProof> trimmed = SortAndTrimProofs(validProofs, _poolConfig.WorkSetReserveLimit);
        int duplicateOrOverflowProofs = validProofs.Count - trimmed.Count;
        if (invalidProofs == 0 && canonicalizedProofs == 0 && duplicateOrOverflowProofs == 0)
        {
            return (0, 0);
        }

        _state.OnDeckProofs = trimmed;
        RebuildOnDeckListNoLock();
        _state.CandidateStateId = ComputeCandidateStateIdNoLock();
        return (invalidProofs + duplicateOrOverflowProofs, canonicalizedProofs);
    }

    private static bool ProofConsensusFieldsMatch(BootShareProof left, BootShareProof right)
    {
        return string.Equals(left.ShareId, right.ShareId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ScriptPubKeyHex, right.ScriptPubKeyHex, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.PayoutSnapshotId ?? string.Empty, right.PayoutSnapshotId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
               Math.Abs(left.Difficulty - right.Difficulty) <= 0.0000001;
    }

    private List<PayoutInfo> TryBuildSnapshotWinnersFromProofCoinbaseNoLock(BootShareProof proof)
    {
        try
        {
            List<BitcoinTransactionOutput> outputs = BitcoinTransactionParser.ParseOutputs(Convert.FromHexString(proof.CoinbaseHex));
            return outputs
                .Skip(1)
                .Where(output => output.Value > 0)
                .Select(output => new
                {
                    output.Value,
                    Address = BitcoinScript.ScriptToAddress(output.ScriptPubKey, _poolConfig.BitcoinNetwork)
                })
                .Where(output => !string.IsNullOrWhiteSpace(output.Address) &&
                                 !string.Equals(output.Address, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                .Select(output =>
                {
                    string normalizedAddress = BitcoinScript.NormalizeAddress(output.Address);
                    return new PayoutInfo
                    {
                        Value = output.Value,
                        Address = normalizedAddress,
                        Username = normalizedAddress
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Unable to recover snapshot context {SnapshotId} from local proof {ShareId}.",
                proof.PayoutSnapshotId,
                proof.ShareId);
            return [];
        }
    }

    private List<PayoutInfo> RemoveSupportFeePayoutsNoLock(IEnumerable<PayoutInfo> payouts)
    {
        string supportAddress = GetGridLabsSupportAddress(_poolConfig.BitcoinNetwork);
        string supportScript = BitcoinScript.AddressToScriptPubKeyHex(supportAddress, _poolConfig.BitcoinNetwork);
        return payouts
            .Where(payout =>
            {
                string script = BitcoinScript.AddressToScriptPubKeyHex(payout.Address, _poolConfig.BitcoinNetwork);
                return !string.Equals(script, supportScript, StringComparison.OrdinalIgnoreCase);
            })
            .Select(ClonePayout)
            .ToList();
    }

    private string BuildPayoutVariantNoLock()
    {
        string baseVariant = _poolConfig.GridLabsSupportFeeEnabled ? "gridlabs-support-v1" : "fee-free";
        return GetActiveConsensusVersionNoLock() >= BootProtocolVersions.ConsensusVersion
            ? $"{baseVariant}:shared={_poolConfig.SharedWinnerSlotCount}:snapshot={_poolConfig.SnapshotProofSlotCount}:reserve={_poolConfig.WorkSetReserveLimit}"
            : baseVariant;
    }

    private string BuildSnapshotFamilyNetworkIdNoLock()
    {
        return $"{_poolConfig.BootNetworkId.Trim()}|bitcoin={BitcoinScript.NormalizeNetwork(_poolConfig.BitcoinNetwork)}";
    }

    private void NormalizeNetworkSensitivePayoutValuesNoLock()
    {
        if (_state.CurrentRoundNumber == 0 &&
            _state.WinnersList.Count == 1 &&
            _state.WinnersList[0].Difficulty <= 0)
        {
            PayoutInfo genesisWinner = _state.WinnersList[0];
            string expectedGenesisAddress = GetGenesisFoundationAddress(_poolConfig.BitcoinNetwork);
            string normalizedWinnerAddress = BitcoinScript.NormalizeAddress(genesisWinner.Address);
            bool isKnownGenesisAddress =
                string.Equals(normalizedWinnerAddress, GenesisFoundationAddress, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedWinnerAddress, TestnetGenesisFoundationAddress, StringComparison.OrdinalIgnoreCase);

            if (isKnownGenesisAddress)
            {
                genesisWinner.Address = expectedGenesisAddress;
                genesisWinner.Username = string.IsNullOrWhiteSpace(genesisWinner.Username) ||
                                         string.Equals(BitcoinScript.NormalizeAddress(genesisWinner.Username), normalizedWinnerAddress, StringComparison.OrdinalIgnoreCase)
                    ? expectedGenesisAddress
                    : genesisWinner.Username;
                genesisWinner.Value = GetSharedPayoutValueSatsNoLock(1);
            }
        }

        if (_state.OnDeckProofs.Count > 0)
        {
            RebuildOnDeckListNoLock();
        }
    }

    private void MigrateSnapshotReserveStateNoLock(DateTime nowUtc)
    {
        _state.SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled;
        _state.PayoutVariant = BuildPayoutVariantNoLock();
        _state.ActiveSnapshotProofIds ??= [];
        _state.LastPaidSnapshotProofIds ??= [];
        _state.SnapshotContexts ??= [];
        _state.OnDeckProofs = SortAndTrimProofs(_state.OnDeckProofs, _poolConfig.WorkSetReserveLimit);

        if (string.IsNullOrWhiteSpace(_state.ActiveSnapshotId))
        {
            _state.ActiveSnapshotId = string.IsNullOrWhiteSpace(_state.CurrentStateId)
                ? ComputeStateIdFromPayoutsNoLock(_state.WinnersList, _state.CurrentTipBlockHash)
                : _state.CurrentStateId;
        }

        if (_state.ActiveSnapshotProofIds.Count == 0)
        {
            BootStateBundle? activeBundle = _state.ArchivedStateBundles
                .FirstOrDefault(bundle => string.Equals(bundle.StateId, _state.ActiveSnapshotId, StringComparison.OrdinalIgnoreCase));
            if (activeBundle?.ShareProofs.Count > 0)
            {
                _state.ActiveSnapshotProofIds = activeBundle.ShareProofs
                    .Select(proof => proof.ShareId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
            }
        }

        foreach (BootShareProof proof in _state.OnDeckProofs)
        {
            if (string.IsNullOrWhiteSpace(proof.PayoutSnapshotId))
            {
                proof.PayoutSnapshotId = _state.ActiveSnapshotId;
            }
        }

        if (!HasSnapshotContextNoLock(_state.ActiveSnapshotId))
        {
            UpsertSnapshotContextNoLock(new BootPayoutSnapshotContext
            {
                SnapshotId = _state.ActiveSnapshotId,
                PreviousSnapshotId = string.Empty,
                CurrentRoundNumber = _state.CurrentRoundNumber,
                LockedByBlockHash = _state.CurrentTipBlockHash,
                LockedByBlockHeight = _state.CurrentTipBlockHeight,
                CreatedAtUtc = _state.LastRotationUtc ?? nowUtc,
                SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
                PayoutVariant = BuildPayoutVariantNoLock(),
                ProofIds = _state.ActiveSnapshotProofIds.ToList(),
                WinnersList = ClonePayouts(_state.WinnersList),
                FeeFreeWinnersList = RemoveSupportFeePayoutsNoLock(_state.WinnersList)
            });
        }

        RebuildOnDeckListNoLock();
    }

    private BootNetworkStatusDto BuildNetworkStatusNoLock()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime? currentRoundStartUtc = ResolveCurrentRoundStartUtcNoLock(nowUtc);
        long? currentRoundElapsedSeconds = GetElapsedSeconds(currentRoundStartUtc, nowUtc);
        long? currentRoundHashrateElapsedSeconds = ResolveCurrentRoundHashrateElapsedSecondsNoLock(nowUtc);
        List<double> onDeckDifficulties = _state.OnDeckProofs
            .Select(x => x.Difficulty)
            .Where(x => x > 0)
            .ToList();
        double currentStateTotalDifficulty = _state.WinnersList.Sum(x => x.Difficulty);
        double onDeckTotalDifficulty = onDeckDifficulties.Sum();
        double? currentRoundObservedHashrateThs =
            currentRoundHashrateElapsedSeconds.HasValue && currentRoundHashrateElapsedSeconds.Value >= 60
                ? EstimateRankAdjustedHashrateThs(onDeckDifficulties, currentRoundHashrateElapsedSeconds)
                : null;
        BootDatumDiagnosticsDto localDatumDiagnostics = BuildLocalDatumDiagnosticsNoLock(nowUtc);
        List<BootLocalDatumMinerSummaryDto> allLocalDatumMiners = BuildLocalDatumMinerSummariesNoLock(nowUtc, GetLocalDatumMaxTrackedAddresses());
        List<BootLocalDatumMinerSummaryDto> activeLocalDatumMiners = allLocalDatumMiners
            .Where(summary => IsActiveLocalDatumMinerSummaryNoLock(summary, nowUtc))
            .ToList();
        List<BootLocalDatumMinerSummaryDto> activeNonTemporaryLocalDatumMiners = activeLocalDatumMiners
            .Where(summary => !IsTemporaryFoundationLocalDatumSummary(summary, _poolConfig.BitcoinNetwork))
            .ToList();
        List<BootLocalDatumMinerSummaryDto> displayableLocalDatumMiners = activeNonTemporaryLocalDatumMiners
            .Where(summary => IsDisplayableLocalDatumMinerSummaryNoLock(summary, nowUtc))
            .ToList();
        List<BootLocalDatumMinerSummaryDto> localDatumMiners = displayableLocalDatumMiners
            .Take(GetLocalDatumMinerSummaryLimit())
            .ToList();
        double? localDatumHashrateThs = EstimateLocalDatumHashrateThsNoLock(activeNonTemporaryLocalDatumMiners);
        List<BootLocalMiningSourceSummaryDto> localMiningSources = BuildLocalMiningSourceSummariesNoLock(nowUtc);
        double localMiningHashrateTotalThs = localMiningSources
            .Where(summary => summary.CurrentHashrateThs.HasValue)
            .Sum(summary => summary.CurrentHashrateThs!.Value);
        double? localMiningHashrateThs = localMiningHashrateTotalThs > 0 ? localMiningHashrateTotalThs : null;
        BootCoinbaserDiagnosticsSummaryDto coinbaserDiagnostics = BuildCoinbaserDiagnosticsSummaryNoLock(nowUtc);
        List<BootPeerStatus> peers = CloneExternalPeersNoLock();
        BootNodeVersionInfo localVersion = GetLocalVersionInfoNoLock();
        BootSnapshotFamilyState? activeFamily = GetActiveSnapshotFamilyNoLock();
        bool peerLoopsHealthy = !_poolConfig.EnablePeerSync ||
                                !_peerLoopHealth.IsPeerPollStale(nowUtc, _poolConfig.PeerLoopStaleSeconds);
        bool localMiningActive = activeNonTemporaryLocalDatumMiners.Count > 0 || _activeDatumSessions.Count > 0;
        bool outboundRelayHealthy = !localMiningActive ||
                                    !_poolConfig.EnablePeerSync ||
                                    !_poolConfig.EnablePulseProofs ||
                                    !_peerLoopHealth.IsOutboundRelayStale(nowUtc, _poolConfig.OutboundRelayStaleSeconds);
        string outboundRelayReason = outboundRelayHealthy
            ? string.Empty
            : "No successful outbound share or pulse relay completed within the configured stale threshold.";
        List<string> configWarnings = BuildConfigWarningsNoLock(peerLoopsHealthy, outboundRelayHealthy);
        BootBitcoinNotificationDto bitcoinNotification =
            _bitcoinNotificationHealth?.Snapshot(nowUtc) ?? new BootBitcoinNotificationDto();

        return new BootNetworkStatusDto
        {
            NodeId = _peerIdentity?.NodeId ?? string.Empty,
            IdentityChanged = _identityChanged,
            SelfEndpoint = NormalizePeerEndpoint(_poolConfig.PublicBaseUrl),
            DatumPublicHost = _poolConfig.DatumPublicHost?.Trim() ?? string.Empty,
            DatumPublicPort = _poolConfig.DatumPublicPort,
            DatumListenPort = _poolConfig.DatumPort,
            ConfigWarnings = configWarnings,
            ServiceStartedUtc = _serviceStartedUtc,
            ActiveDatumSessionCount = _activeDatumSessions.Count,
            LastDatumSessionOpenedUtc = _peerLoopHealth.LastDatumSessionOpenedUtc,
            LastDatumHelloReceivedUtc = _peerLoopHealth.LastDatumHelloReceivedUtc,
            LastDatumCoinbaserRequestUtc = _peerLoopHealth.LastDatumCoinbaserRequestUtc,
            LastPeerPollCompletedUtc = _peerLoopHealth.LastPeerPollCompletedUtc,
            LastShareRelayDequeuedUtc = _peerLoopHealth.LastShareRelayDequeuedUtc,
            LastShareRelayQueuedUtc = _peerLoopHealth.LastShareRelayQueuedUtc,
            LastSuccessfulOutboundRelayUtc = _peerLoopHealth.LastSuccessfulOutboundRelayUtc,
            LastUdpShareRelayUtc = _peerLoopHealth.LastUdpShareRelayUtc,
            LastWebSocketShareRelayUtc = _peerLoopHealth.LastWebSocketShareRelayUtc,
            LastHttpShareRelayUtc = _peerLoopHealth.LastHttpShareRelayUtc,
            LastChainTipRelayUtc = _peerLoopHealth.LastChainTipRelayUtc,
            LastValidLocalDatumShareUtc = _peerLoopHealth.LastValidLocalDatumShareUtc,
            LastSuccessfulDatumCoinbaserResponseUtc = _peerLoopHealth.LastSuccessfulDatumCoinbaserResponseUtc,
            LastDatumSessionClosedUtc = _peerLoopHealth.LastDatumSessionClosedUtc,
            LastDatumSessionCloseReason = _peerLoopHealth.LastDatumSessionCloseReason,
            ShareRelayQueueDepth = _acceptedShares.Reader.CanCount ? _acceptedShares.Reader.Count : -1,
            PeerLoopFaults = _peerLoopHealth.GetFaults(),
            PeerLoopsHealthy = peerLoopsHealthy,
            OutboundRelayHealthy = outboundRelayHealthy,
            OutboundRelayHealthReason = outboundRelayReason,
            LastLocalPulseUtc = _peerLoopHealth.LastLocalPulseUtc,
            LocalPulseAcceptedCount = _peerLoopHealth.LocalPulseAcceptedCount,
            LocalPulseAcceptRatePerMinute = _peerLoopHealth.LocalPulseAcceptedCount /
                Math.Max(1d, (nowUtc - _peerLoopHealth.StartedUtc).TotalMinutes),
            SoftwareConsensusVersion = localVersion.SoftwareConsensusVersion,
            ProtocolVersion = localVersion.ProtocolVersion,
            ConsensusVersion = localVersion.ConsensusVersion,
            V22ActivationBlockHeight = _poolConfig.V22ActivationBlockHeight,
            V22ActivationTipBlockHeight = _state.TrustedLocalTipBlockHeight,
            BlocksToV22Activation = _poolConfig.V22ActivationBlockHeight > 0 &&
                                    _state.TrustedLocalTipBlockHeight.HasValue &&
                                    _state.TrustedLocalTipBlockHeight.Value < _poolConfig.V22ActivationBlockHeight
                ? _poolConfig.V22ActivationBlockHeight - _state.TrustedLocalTipBlockHeight.Value
                : _poolConfig.V22ActivationBlockHeight == 0 ||
                  (_state.TrustedLocalTipBlockHeight.HasValue &&
                   _state.TrustedLocalTipBlockHeight.Value >= _poolConfig.V22ActivationBlockHeight)
                    ? 0
                    : null,
            StateBundleSchemaVersion = localVersion.StateBundleSchemaVersion,
            HttpApiVersion = localVersion.HttpApiVersion,
            PeerTransportVersion = localVersion.PeerTransportVersion,
            UdpRelayVersion = localVersion.UdpRelayVersion,
            EnablePeerPersistentSessions = _poolConfig.EnablePeerPersistentSessions,
            EnablePeerUdpFastRelay = _poolConfig.EnablePeerUdpFastRelay,
            PeerUdpPublicHost = _poolConfig.PeerUdpPublicHost?.Trim() ?? string.Empty,
            PeerUdpPort = _poolConfig.PeerUdpPort,
            PeerUdpMaxDatagramBytes = _poolConfig.PeerUdpMaxDatagramBytes,
            PeerRelayLatencyProbeAllTransports = _poolConfig.PeerRelayLatencyProbeAllTransports,
            PulseProofsEnabled = _poolConfig.EnablePulseProofs,
            MinimumPulseDifficulty = Math.Max(1d, _poolConfig.PulseMinDifficulty),
            PulseTargetIntervalSeconds = Math.Max(1, _poolConfig.PulseTargetIntervalSeconds),
            PulseRelayTtl = Math.Max(1, _poolConfig.PulseRelayTtl),
            OptimisticShareRelayEnabled = _poolConfig.EnableOptimisticShareRelay,
            MinimumOptimisticRelayDifficulty = Math.Max(GetWorkSetAdmissionDifficultyNoLock(), _poolConfig.MinOptimisticRelayDifficulty),
            PublicTelemetryOptIn = _poolConfig.PublicTelemetryOptIn,
            PublicNodeDisplayName = _poolConfig.PublicTelemetryOptIn ? _poolConfig.PublicNodeDisplayName.Trim() : string.Empty,
            PublicNodeRegion = _poolConfig.PublicTelemetryOptIn ? _poolConfig.PublicNodeRegion.Trim() : string.Empty,
            PublicNodeRole = _poolConfig.PublicTelemetryOptIn ? _poolConfig.PublicNodeRole.Trim() : string.Empty,
            PublicNodeApproxLatitude = _poolConfig.PublicTelemetryOptIn ? _poolConfig.PublicNodeApproxLatitude : null,
            PublicNodeApproxLongitude = _poolConfig.PublicTelemetryOptIn ? _poolConfig.PublicNodeApproxLongitude : null,
            ReleaseVersion = localVersion.ReleaseVersion,
            VersionInfo = localVersion,
            NetworkId = _poolConfig.BootNetworkId,
            BitcoinNetwork = BitcoinScript.NormalizeNetwork(_poolConfig.BitcoinNetwork),
            CurrentRoundNumber = _state.CurrentRoundNumber,
            SharedWinnerSlotCount = _poolConfig.SharedWinnerSlotCount,
            TotalPayoutSlotCount = _poolConfig.TotalPayoutSlotCount,
            CurrentStateId = _state.CurrentStateId,
            CandidateStateId = _state.CandidateStateId,
            ActiveSnapshotId = _state.ActiveSnapshotId,
            LastPaidSnapshotId = _state.LastPaidSnapshotId,
            ActiveSnapshotProofCount = _state.ActiveSnapshotProofIds.Count,
            WorkSetCount = _state.OnDeckProofs.Count,
            WorkSetReserveLimit = _poolConfig.WorkSetReserveLimit,
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock(),
            CoinbaseOutputMode = _poolConfig.CoinbaseUncondensedOutputsEnabled ? "uncondensed-test" : "condensed",
            CoinbaseOutputCount = BuildCoinbaseOutputsNoLock(_state.WinnersList).Count,
            CurrentTipBlockHash = _state.CurrentTipBlockHash,
            CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
            CurrentTipCompactTarget = _state.CurrentTipCompactTarget,
            PeerTipStaleProtectionEnabled = _poolConfig.EnablePeerTipStaleProtection,
            MiningWorkSafe = IsMiningWorkSafeNoLock(nowUtc),
            LocalBitcoinLagging = !bitcoinNotification.MiningSafe ||
                                  (_poolConfig.EnablePeerTipStaleProtection &&
                                   _state.ProvisionalTip != null &&
                                   nowUtc >= _state.ProvisionalTip.GraceDeadlineUtc),
            MiningWorkSafetyReason = BuildMiningWorkSafetyReasonNoLock(),
            BitcoinNotification = bitcoinNotification,
            ProvisionalTipBlockHash = _state.ProvisionalTip?.BlockHash,
            ProvisionalTipParentBlockHash = _state.ProvisionalTip?.ParentBlockHash,
            ProvisionalSnapshotId = _state.ProvisionalTip?.SnapshotId,
            ProvisionalSnapshotProofCount = _state.ProvisionalTip?.SnapshotProofs.Count ?? 0,
            ProvisionalTipObservedUtc = _state.ProvisionalTip?.ObservedUtc,
            ProvisionalTipGraceDeadlineUtc = _state.ProvisionalTip?.GraceDeadlineUtc,
            ProvisionalExpectedDifficultyValidated = _state.ProvisionalTip?.ExpectedDifficultyValidated ?? false,
            LastRotationUtc = currentRoundStartUtc,
            WinnersCount = _state.WinnersList.Count,
            CurrentStateProofCount = GetCurrentStateProofCountNoLock(),
            CurrentStateTotalDifficulty = currentStateTotalDifficulty,
            OnDeckCount = _state.OnDeckList.Count,
            OnDeckTotalDifficulty = onDeckTotalDifficulty,
            CurrentRoundElapsedSeconds = currentRoundElapsedSeconds,
            CurrentRoundObservedHashrateThs = currentRoundObservedHashrateThs,
            CurrentRoundObservedHashrateDisplay = FormatObservedHashrate(currentRoundObservedHashrateThs),
            LocalDatumHashrateThs = localDatumHashrateThs,
            LocalDatumHashrateDisplay = FormatObservedHashrate(localDatumHashrateThs),
            LocalMiningHashrateThs = localMiningHashrateThs,
            LocalMiningHashrateDisplay = FormatObservedHashrate(localMiningHashrateThs),
            LocalMiningSourceCount = localMiningSources.Count,
            LocalMiningSources = localMiningSources,
            PeerCount = peers.Count,
            AdminApiEnabled = _poolConfig.EnableAdminApi,
            TestingRoundResetEnabled = _poolConfig.TestingRoundResetEnabled,
            RoundTriggerMode = BuildRoundTriggerModeNoLock(),
            TestingRoundResetMode = _poolConfig.TestingRoundResetMode,
            TestingRoundResetLowNibbleThreshold = _poolConfig.TestingRoundResetLowNibbleThreshold,
            TestingRoundResetDescription = BuildTestingRoundResetDescriptionNoLock(),
            LastTestingTriggerBlockHash = _state.LastTestingTriggerBlockHash,
            LastTestingTriggerBlockHeight = _state.LastTestingTriggerBlockHeight,
            LastGridPoolBlockHash = _state.LastGridPoolBlockHash,
            LastGridPoolBlockHeight = _state.LastGridPoolBlockHeight,
            LastGridPoolBlockUtc = _state.LastGridPoolBlockUtc,
            LastGridPoolBlockMinerAddress = _state.LastGridPoolBlockMinerAddress,
            LastGridPoolBlockDifficulty = _state.LastGridPoolBlockDifficulty,
            LaunchReadiness = BuildLaunchReadinessNoLock(peers),
            LocalDatumDiagnostics = localDatumDiagnostics,
            LocalDatumMinerCount = displayableLocalDatumMiners.Count,
            LocalDatumMiners = localDatumMiners,
            CoinbaserDiagnostics = coinbaserDiagnostics,
            Peers = peers,
            Commitment = BuildCommitmentNoLock(),
            ActiveSnapshotFamilyId = activeFamily?.FamilyId ?? string.Empty,
            SnapshotFamilyMemberCount = activeFamily?.MemberSnapshotIds.Count ?? 0,
            SnapshotFamilyUnionProofCount = activeFamily?.ReconciledProofs.Count ?? 0,
            ReconciliationCounters = CloneReconciliationCounters(_state.ReconciliationCounters)
        };
    }

    private BootHashrateSeriesDto BuildHashrateSeriesNoLock(string? windowKey)
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime cutoffUtc = ResolveHashrateSeriesCutoffUtc(windowKey, nowUtc);
        TrimHashrateSamplesNoLock(nowUtc);

        return new BootHashrateSeriesDto
        {
            SampleIntervalSeconds = GetHashrateSampleIntervalSeconds(),
            LocalWindowSeconds = GetHashrateLocalWindowSeconds(),
            Points = _state.HashrateSamples
                .Where(point => point.TimestampUtc >= cutoffUtc)
                .OrderBy(point => point.TimestampUtc)
                .Select(CloneHashratePoint)
                .ToList()
        };
    }

    private BootShareDiagnosticsSeriesDto BuildShareDiagnosticsSeriesNoLock(
        string? windowKey,
        string? source,
        bool? accepted,
        int limit,
        string? minerAddress,
        string? category)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimShareDiagnosticsNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetShareDiagnosticRetentionHours());
        string? normalizedMinerAddress = string.IsNullOrWhiteSpace(minerAddress)
            ? null
            : BitcoinScript.NormalizeAddress(minerAddress);
        string? normalizedCategory = NormalizeDiagnosticReason(category);

        IEnumerable<BootShareDiagnosticTelemetry> query = _recentShareDiagnostics
            .Where(item => item.TimestampUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase));
        }

        if (accepted.HasValue)
        {
            query = query.Where(item => item.Accepted == accepted.Value);
        }

        if (!string.IsNullOrWhiteSpace(normalizedMinerAddress))
        {
            query = query.Where(item => string.Equals(
                BitcoinScript.NormalizeAddress(item.MinerAddress),
                normalizedMinerAddress,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedCategory))
        {
            query = query.Where(item => string.Equals(
                item.RejectionCategory,
                normalizedCategory,
                StringComparison.OrdinalIgnoreCase));
        }

        List<BootShareDiagnosticTelemetry> events = query
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(Math.Clamp(limit, 1, 5000))
            .Select(CloneShareDiagnostic)
            .ToList();

        return new BootShareDiagnosticsSeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = events.Count,
            Events = events
        };
    }

    private BootCoinbaserDiagnosticsSeriesDto BuildCoinbaserDiagnosticsSeriesNoLock(
        string? windowKey,
        int limit,
        string? remoteEndpoint,
        bool? temporarySlotZero)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimCoinbaserDiagnosticsNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetShareDiagnosticRetentionHours());

        IEnumerable<BootCoinbaserFetchTelemetry> query = _state.RecentCoinbaserDiagnostics
            .Where(item => item.TimestampUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            query = query.Where(item => string.Equals(item.RemoteEndpoint, remoteEndpoint, StringComparison.OrdinalIgnoreCase));
        }

        if (temporarySlotZero.HasValue)
        {
            query = query.Where(item => item.UsingTemporarySlotZero == temporarySlotZero.Value);
        }

        List<BootCoinbaserFetchTelemetry> events = query
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(Math.Clamp(limit, 1, 5000))
            .Select(CloneCoinbaserDiagnostic)
            .ToList();

        return new BootCoinbaserDiagnosticsSeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = events.Count,
            Events = events
        };
    }

    private BootDatumShareResponseSeriesDto BuildDatumShareResponseSeriesNoLock(
        string? windowKey,
        int limit,
        string? remoteEndpoint,
        bool? accepted,
        string? reason)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimDatumShareResponsesNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetShareDiagnosticRetentionHours());

        IEnumerable<BootDatumShareResponseTelemetry> query = _state.RecentDatumShareResponses
            .Where(item => item.TimestampUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            query = query.Where(item => string.Equals(item.RemoteEndpoint, remoteEndpoint, StringComparison.OrdinalIgnoreCase));
        }

        if (accepted.HasValue)
        {
            query = query.Where(item => item.Accepted == accepted.Value);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            string? normalizedReason = NormalizeDiagnosticReason(reason);
            if (!string.IsNullOrWhiteSpace(normalizedReason))
            {
                query = query.Where(item => string.Equals(
                    NormalizeDiagnosticReason(item.RejectionReason),
                    normalizedReason,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        List<BootDatumShareResponseTelemetry> events = query
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(Math.Clamp(limit, 1, 5000))
            .Select(CloneDatumShareResponse)
            .ToList();

        return new BootDatumShareResponseSeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = events.Count,
            Events = events
        };
    }

    private BootDatumSessionSeriesDto BuildDatumSessionSeriesNoLock(
        string? windowKey,
        int limit,
        string? remoteEndpoint,
        bool? active,
        string? protocol)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimDatumSessionsNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetShareDiagnosticRetentionHours());

        IEnumerable<BootDatumSessionTelemetry> query = _state.RecentDatumSessions
            .Where(item => item.StartedUtc >= cutoffUtc || item.ClosedUtc == null || item.ClosedUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            query = query.Where(item => string.Equals(item.RemoteEndpoint, remoteEndpoint, StringComparison.OrdinalIgnoreCase));
        }

        if (active.HasValue)
        {
            query = query.Where(item => (item.ClosedUtc == null) == active.Value);
        }

        if (!string.IsNullOrWhiteSpace(protocol))
        {
            query = query.Where(item => string.Equals(item.Protocol, protocol, StringComparison.OrdinalIgnoreCase));
        }

        List<BootDatumSessionTelemetry> events = query
            .OrderBy(item => item.StartedUtc)
            .TakeLast(Math.Clamp(limit, 1, 5000))
            .Select(CloneDatumSession)
            .ToList();

        return new BootDatumSessionSeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = events.Count,
            Events = events
        };
    }

    private BootDatumProtocolEventSeriesDto BuildDatumProtocolEventSeriesNoLock(
        string? windowKey,
        int limit,
        string? sessionId,
        string? remoteEndpoint,
        string? eventType,
        string? direction,
        string? messageLabel)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimDatumProtocolEventsNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetShareDiagnosticRetentionHours());

        IEnumerable<BootDatumProtocolEvent> query = _recentDatumProtocolEvents
            .Where(item => item.TimestampUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            query = query.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            query = query.Where(item => string.Equals(item.RemoteEndpoint, remoteEndpoint, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(item => string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            query = query.Where(item => string.Equals(item.Direction, direction, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(messageLabel))
        {
            query = query.Where(item => string.Equals(item.MessageLabel, messageLabel, StringComparison.OrdinalIgnoreCase));
        }

        List<BootDatumProtocolEvent> events = query
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.Sequence)
            .TakeLast(Math.Clamp(limit, 1, MaxRecentDatumProtocolEvents))
            .Select(CloneDatumProtocolEvent)
            .ToList();

        return new BootDatumProtocolEventSeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = events.Count,
            Events = events
        };
    }

    private BootNetworkEventSeriesDto BuildNetworkEventSeriesNoLock(
        string? windowKey,
        int limit,
        string? eventType,
        string? source)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimNetworkEventsNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetNetworkEventRetentionHours());

        IEnumerable<BootNetworkEvent> query = _state.RecentNetworkEvents
            .Where(item => item.TimestampUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(item => string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            query = query.Where(item => string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase));
        }

        List<BootNetworkEvent> events = query
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(Math.Clamp(limit, 1, 5000))
            .Select(CloneNetworkEvent)
            .ToList();

        return new BootNetworkEventSeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = events.Count,
            Events = events
        };
    }

    private BootPeerRelayLatencySeriesDto BuildPeerRelayLatencySeriesNoLock(
        string? windowKey,
        int limit,
        string? remoteEndpoint,
        string? transport,
        string? proofClass,
        string? relayStage)
    {
        DateTime nowUtc = DateTime.UtcNow;
        TrimPeerRelayObservationsNoLock(nowUtc);
        DateTime cutoffUtc = ResolveTelemetryCutoffUtc(windowKey, nowUtc, GetShareDiagnosticRetentionHours());
        string normalizedRemoteEndpoint = NormalizePeerEndpoint(remoteEndpoint ?? string.Empty);
        string normalizedTransport = string.IsNullOrWhiteSpace(transport)
            ? string.Empty
            : transport.Trim();
        string normalizedProofClass = string.IsNullOrWhiteSpace(proofClass)
            ? string.Empty
            : ResolveProofClass(proofClass);
        string normalizedRelayStage = string.IsNullOrWhiteSpace(relayStage)
            ? string.Empty
            : ResolveRelayStage(relayStage);

        IEnumerable<BootPeerRelayObservation> query = _state.RecentPeerRelayObservations
            .Where(item => item.TimestampUtc >= cutoffUtc);

        if (!string.IsNullOrWhiteSpace(normalizedRemoteEndpoint))
        {
            query = query.Where(item => string.Equals(
                NormalizePeerEndpoint(item.RemoteEndpoint),
                normalizedRemoteEndpoint,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedTransport))
        {
            query = query.Where(item => string.Equals(
                item.Transport,
                normalizedTransport,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedProofClass))
        {
            query = query.Where(item => string.Equals(
                ResolveProofClass(item.ProofClass),
                normalizedProofClass,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedRelayStage))
        {
            query = query.Where(item => string.Equals(
                ResolveRelayStage(item.RelayStage),
                normalizedRelayStage,
                StringComparison.OrdinalIgnoreCase));
        }

        List<BootPeerRelayObservation> matching = query
            .OrderBy(item => item.TimestampUtc)
            .ToList();
        List<BootPeerRelayTransportSummaryDto> summaries = matching
            .GroupBy(
                item => new
                {
                    Transport = item.Transport,
                    ProofClass = ResolveProofClass(item.ProofClass),
                    RelayStage = ResolveRelayStage(item.RelayStage)
                })
            .Select(group =>
            {
                List<double> deltas = group
                    .Select(item => item.DeltaFromFirstMs)
                    .Where(value => value >= 0)
                    .OrderBy(value => value)
                    .ToList();
                List<int> payloadSizes = group
                    .Select(item => item.PayloadBytes)
                    .Where(value => value > 0)
                    .OrderBy(value => value)
                    .ToList();

                return new BootPeerRelayTransportSummaryDto
                {
                    Transport = group.Key.Transport,
                    ProofClass = group.Key.ProofClass,
                    RelayStage = group.Key.RelayStage,
                    ArrivalCount = group.Count(),
                    FirstArrivalCount = group.Count(item => item.IsFirstArrival),
                    AcceptedCount = group.Count(item => item.Accepted),
                    DuplicateCount = group.Count(item => string.Equals(item.RejectionReason, "Duplicate share", StringComparison.OrdinalIgnoreCase)),
                    RejectedCount = group.Count(item => !item.Accepted),
                    AverageDeltaFromFirstMs = deltas.Count > 0 ? deltas.Average() : null,
                    MedianDeltaFromFirstMs = Percentile(deltas, 0.5),
                    P95DeltaFromFirstMs = Percentile(deltas, 0.95),
                    AveragePayloadBytes = payloadSizes.Count > 0 ? payloadSizes.Average() : null,
                    MinPayloadBytes = payloadSizes.Count > 0 ? payloadSizes[0] : null,
                    MaxPayloadBytes = payloadSizes.Count > 0 ? payloadSizes[^1] : null
                };
            })
            .OrderByDescending(item => item.FirstArrivalCount)
            .ThenBy(item => item.Transport, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<BootPeerRelayObservation> observations = matching
            .TakeLast(Math.Clamp(limit, 1, MaxRecentPeerRelayObservations))
            .Select(ClonePeerRelayObservation)
            .ToList();

        return new BootPeerRelayLatencySeriesDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalEvents = matching.Count,
            Transports = summaries,
            Observations = observations
        };
    }

    private bool TryGetDatumSessionNoLock(string sessionId, out BootDatumSessionTelemetry session)
    {
        if (_activeDatumSessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }

        BootDatumSessionTelemetry? existing = _state.RecentDatumSessions.LastOrDefault(item =>
            string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
        if (existing == null)
        {
            session = null!;
            return false;
        }

        session = existing;
        if (session.ClosedUtc == null)
        {
            _activeDatumSessions[sessionId] = session;
        }

        return true;
    }

    private BootDatumSessionTelemetry FindOrCreateDatumSessionNoLock(
        string sessionId,
        string remoteEndpoint,
        DateTime startedUtc)
    {
        if (TryGetDatumSessionNoLock(sessionId, out var existing) && existing.ClosedUtc == null)
        {
            return existing;
        }

        var session = new BootDatumSessionTelemetry
        {
            SessionId = sessionId,
            RemoteEndpoint = remoteEndpoint,
            StartedUtc = startedUtc,
            LastActivityUtc = startedUtc,
            LastActivityType = "opened"
        };
        _state.RecentDatumSessions.Add(session);
        _activeDatumSessions[sessionId] = session;
        return session;
    }

    private void RebuildActiveDatumSessionIndexNoLock()
    {
        _activeDatumSessions.Clear();
        foreach (BootDatumSessionTelemetry session in _state.RecentDatumSessions.Where(item => item.ClosedUtc == null))
        {
            _activeDatumSessions[session.SessionId] = session;
        }
    }

    private void FinalizeStaleDatumSessionsNoLock(DateTime closedUtc, string closeDisposition, string closeReason)
    {
        foreach (BootDatumSessionTelemetry session in _state.RecentDatumSessions.Where(item => item.ClosedUtc == null))
        {
            session.ServerInitiatedClose = true;
            session.ServerCloseEventType = "service-restart";
            session.CloseDisposition = closeDisposition;
            session.CloseReason = closeReason;
            session.ClosedUtc = closedUtc;
            session.DurationMs = Math.Max(0, (closedUtc - session.StartedUtc).TotalMilliseconds);
            session.IdleBeforeCloseMs = session.LastActivityUtc.HasValue
                ? Math.Max(0, (closedUtc - session.LastActivityUtc.Value).TotalMilliseconds)
                : null;
        }

        _activeDatumSessions.Clear();
    }

    private BootCoinbaserDiagnosticsSummaryDto BuildCoinbaserDiagnosticsSummaryNoLock(DateTime nowUtc)
    {
        TrimCoinbaserDiagnosticsNoLock(nowUtc);
        DateTime cutoffUtc = nowUtc.AddMinutes(-30);
        List<BootCoinbaserFetchTelemetry> recent = _state.RecentCoinbaserDiagnostics
            .Where(item => item.TimestampUtc >= cutoffUtc)
            .OrderBy(item => item.TimestampUtc)
            .ToList();

        if (recent.Count == 0)
        {
            return new BootCoinbaserDiagnosticsSummaryDto
            {
                WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds)
            };
        }

        List<double> durations = recent
            .Select(item => item.DurationMs)
            .OrderBy(x => x)
            .ToList();
        int p95Index = Math.Clamp((int)Math.Ceiling(durations.Count * 0.95) - 1, 0, durations.Count - 1);
        List<double> sendDurations = recent
            .Select(item => item.SendDurationMs)
            .OrderBy(x => x)
            .ToList();
        int sendP95Index = Math.Clamp((int)Math.Ceiling(sendDurations.Count * 0.95) - 1, 0, sendDurations.Count - 1);

        return new BootCoinbaserDiagnosticsSummaryDto
        {
            WindowSeconds = (int)Math.Max(0, (nowUtc - cutoffUtc).TotalSeconds),
            TotalFetches = recent.Count,
            LastFetchUtc = recent[^1].TimestampUtc,
            AverageDurationMs = durations.Average(),
            P95DurationMs = durations[p95Index],
            AverageParseDurationMs = recent.Average(item => item.ParseDurationMs),
            AverageStateReadDurationMs = recent.Average(item => item.StateReadDurationMs),
            AverageBuildDurationMs = recent.Average(item => item.BuildDurationMs),
            AverageSerializeDurationMs = recent.Average(item => item.SerializeDurationMs),
            AverageSendDurationMs = recent.Average(item => item.SendDurationMs),
            P95SendDurationMs = sendDurations[sendP95Index],
            TemporarySlotZeroCount = recent.Count(item => item.UsingTemporarySlotZero),
            SlowFetchCount = recent.Count(item => item.DurationMs >= 1000),
            SlowStateReadCount = recent.Count(item => item.StateReadDurationMs >= 250),
            SlowBuildCount = recent.Count(item => item.BuildDurationMs >= 250),
            SlowSerializeCount = recent.Count(item => item.SerializeDurationMs >= 250),
            SlowSendCount = recent.Count(item => item.SendDurationMs >= 250)
        };
    }

    private List<BootRoundHistoryEntry> BuildRoundHistoryNoLock(int limit)
    {
        int effectiveLimit = Math.Clamp(limit, 1, Math.Max(1, _poolConfig.MaxStateBundleHistory));
        List<BootStateBundle> completedRounds = _state.ArchivedStateBundles
            .Where(bundle => bundle.WinnersList.Count > 0 || bundle.ProofWinnersList.Count > 0)
            .ToList();
        HashSet<string> canonicalStateIds = BuildCanonicalStateIdSetNoLock();

        return completedRounds
            .Take(effectiveLimit)
            .Select((bundle, index) => BuildRoundHistoryEntryNoLock(
                bundle,
                canonicalStateIds.Contains(bundle.StateId),
                index + 1 < completedRounds.Count ? completedRounds[index + 1] : null))
            .ToList();
    }

    private BootRoundHistoryEntry BuildRoundHistoryEntryNoLock(BootStateBundle bundle, bool isCanonical, BootStateBundle? priorBundle)
    {
        List<BootRoundPayoutAggregate> paidRecipients = AggregateRoundPayoutsNoLock(
            bundle.ProofWinnersList.Count > 0 ? bundle.ProofWinnersList : bundle.WinnersList);
        List<BootRoundPayoutAggregate> nextRecipients = AggregateRoundPayoutsNoLock(bundle.WinnersList);
        long? roundElapsedSeconds = GetElapsedSeconds(priorBundle?.CreatedAtUtc, bundle.CreatedAtUtc);
        List<double> roundDifficulties = bundle.ShareProofs
            .Select(x => x.Difficulty)
            .Where(x => x > 0)
            .ToList();
        double? observedHashrateThs = EstimateRankAdjustedHashrateThs(roundDifficulties, roundElapsedSeconds);
        int roundNumber = Math.Max(0, bundle.CurrentRoundNumber - 1);

        return new BootRoundHistoryEntry
        {
            RoundNumber = roundNumber,
            StateId = bundle.StateId,
            PreviousStateId = bundle.PreviousStateId,
            Kind = bundle.Kind,
            IsCanonical = isCanonical,
            IsOrphaned = !isCanonical,
            TriggerBlockHash = bundle.LockedByBlockHash,
            TriggerBlockHeight = bundle.LockedByBlockHeight,
            ParentBlockHash = bundle.ParentBlockHash,
            ParentBlockHeight = bundle.ParentBlockHeight,
            LockedAtUtc = bundle.CreatedAtUtc,
            RoundElapsedSeconds = roundElapsedSeconds,
            WinningShareCount = bundle.ShareProofs.Count,
            WinningTotalDifficulty = bundle.TotalDifficulty,
            WinningTotalDifficultyDisplay = ClientHandler.FormatDifficulty(bundle.TotalDifficulty),
            ObservedHashrateThs = observedHashrateThs,
            ObservedHashrateDisplay = FormatObservedHashrate(observedHashrateThs),
            PaidSlotCount = bundle.ProofWinnersList.Count > 0 ? bundle.ProofWinnersList.Count : bundle.WinnersList.Count,
            PaidRecipientCount = paidRecipients.Count,
            PaidTotalValue = paidRecipients.Aggregate<BootRoundPayoutAggregate, ulong>(0, (sum, item) => sum + item.TotalValue),
            NextWinnerSlotCount = bundle.WinnersList.Count,
            NextWinnerRecipientCount = nextRecipients.Count,
            NextWinnerTotalValue = nextRecipients.Aggregate<BootRoundPayoutAggregate, ulong>(0, (sum, item) => sum + item.TotalValue),
            PaidRecipients = paidRecipients,
            NextRecipients = nextRecipients
        };
    }

    private HashSet<string> BuildCanonicalStateIdSetNoLock()
    {
        var canonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = _state.CurrentStateId;

        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!canonical.Add(cursor))
            {
                break;
            }

            BootStateBundle? bundle = _state.ArchivedStateBundles.FirstOrDefault(existing =>
                string.Equals(existing.StateId, cursor, StringComparison.OrdinalIgnoreCase));
            if (bundle == null)
            {
                break;
            }

            cursor = bundle.PreviousStateId;
        }

        return canonical;
    }

    private static List<BootRoundPayoutAggregate> AggregateRoundPayoutsNoLock(IEnumerable<PayoutInfo> payouts)
    {
        return payouts
            .GroupBy(payout => BitcoinScript.NormalizeAddress(payout.Address), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                string username = group
                    .Select(item => string.IsNullOrWhiteSpace(item.Username) ? item.Address : item.Username)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? group.Key;
                double totalDifficulty = group.Sum(item => item.Difficulty);

                return new BootRoundPayoutAggregate
                {
                    Address = group.Key,
                    Username = username,
                    SlotCount = group.Count(),
                    TotalValue = group.Aggregate<PayoutInfo, ulong>(0, (sum, item) => sum + item.Value),
                    TotalDifficulty = totalDifficulty,
                    TotalDifficultyDisplay = ClientHandler.FormatDifficulty(totalDifficulty)
                };
            })
            .OrderByDescending(item => item.TotalValue)
            .ThenByDescending(item => item.SlotCount)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long? GetElapsedSeconds(DateTime? startedAtUtc, DateTime? endedAtUtc)
    {
        if (!startedAtUtc.HasValue || !endedAtUtc.HasValue)
        {
            return null;
        }

        if (startedAtUtc.Value == default || endedAtUtc.Value == default)
        {
            return null;
        }

        double totalSeconds = (endedAtUtc.Value - startedAtUtc.Value).TotalSeconds;
        if (totalSeconds < 0)
        {
            return null;
        }

        return (long)Math.Floor(totalSeconds);
    }

    private DateTime? ResolveConfiguredGenesisRoundStartUtcNoLock()
    {
        DateTime? configured = _poolConfig.GenesisRoundStartUtc;
        if (configured.HasValue && configured.Value != default)
        {
            return configured.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(configured.Value, DateTimeKind.Utc)
                : configured.Value.ToUniversalTime();
        }

        return null;
    }

    private void EnsureGenesisRoundStartNoLock(DateTime nowUtc)
    {
        DateTime? configured = ResolveConfiguredGenesisRoundStartUtcNoLock();
        DateTime? oldestShareUtc = _state.OnDeckProofs
            .Where(proof => proof.Timestamp != default)
            .Select(proof => (DateTime?)proof.Timestamp.ToUniversalTime())
            .OrderBy(timestamp => timestamp)
            .FirstOrDefault();

        DateTime candidate = configured ?? _state.GenesisRoundStartedUtc ?? oldestShareUtc ?? _serviceStartedUtc;
        if (oldestShareUtc.HasValue && oldestShareUtc.Value < candidate)
        {
            candidate = oldestShareUtc.Value;
        }

        if (candidate > nowUtc)
        {
            candidate = nowUtc;
        }

        _state.GenesisRoundStartedUtc ??= candidate;

        if (_state.CurrentRoundNumber == 0 &&
            (!_state.LastRotationUtc.HasValue || _state.LastRotationUtc.Value == default))
        {
            _state.LastRotationUtc = _state.GenesisRoundStartedUtc;
        }
    }

    private DateTime? ResolveCurrentRoundStartUtcNoLock(DateTime nowUtc)
    {
        EnsureGenesisRoundStartNoLock(nowUtc);
        DateTime? startedAtUtc = _state.LastRotationUtc;
        if (_state.CurrentRoundNumber == 0)
        {
            DateTime? oldestShareUtc = _state.OnDeckProofs
                .Where(proof => proof.Timestamp != default)
                .Select(proof => (DateTime?)proof.Timestamp.ToUniversalTime())
                .OrderBy(timestamp => timestamp)
                .FirstOrDefault();

            if (oldestShareUtc.HasValue &&
                (!startedAtUtc.HasValue || oldestShareUtc.Value < startedAtUtc.Value))
            {
                startedAtUtc = oldestShareUtc.Value;
            }
        }

        return startedAtUtc;
    }

    private long? ResolveCurrentRoundHashrateElapsedSecondsNoLock(DateTime nowUtc)
    {
        DateTime? startedAtUtc = ResolveActiveOnDeckHashrateStartUtcNoLock() ??
                                 ResolveCurrentRoundStartUtcNoLock(nowUtc);
        return GetElapsedSeconds(startedAtUtc, nowUtc);
    }

    private DateTime? ResolveActiveOnDeckHashrateStartUtcNoLock()
    {
        List<DateTime> proofTimes = _state.OnDeckProofs
            .Where(proof => proof.Timestamp != default)
            .Select(proof => proof.Timestamp.ToUniversalTime())
            .OrderBy(timestamp => timestamp)
            .ToList();
        if (proofTimes.Count == 0)
        {
            return null;
        }

        // The on-deck list is a rolling high-difficulty sample. During long
        // public-beta/genesis rounds, a single old high-luck share from a tiny
        // miner can otherwise anchor the elapsed time and make the displayed
        // team hashrate look far lower than the active mining set. Trim only a
        // tiny number of oldest timestamps so the estimate remains conservative
        // while ignoring isolated stale anchors.
        int trimCount = proofTimes.Count >= 100
            ? Math.Clamp((int)Math.Floor(proofTimes.Count * 0.01d), 1, 5)
            : 0;
        return proofTimes[Math.Min(trimCount, proofTimes.Count - 1)];
    }

    private BootDatumDiagnosticsDto BuildLocalDatumDiagnosticsNoLock(DateTime nowUtc)
    {
        TrimShareDiagnosticsNoLock(nowUtc);

        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        if (_serviceStartedUtc > cutoffUtc)
        {
            cutoffUtc = _serviceStartedUtc;
        }

        List<BootShareDiagnosticTelemetry> localDatumDiagnostics = _recentShareDiagnostics
            .Where(item => string.Equals(item.Source, "datum", StringComparison.OrdinalIgnoreCase) &&
                           item.TimestampUtc >= cutoffUtc)
            .OrderBy(item => item.TimestampUtc)
            .ToList();

        BootShareDiagnosticTelemetry? lastRejection = localDatumDiagnostics.LastOrDefault(item => !item.Accepted);
        return new BootDatumDiagnosticsDto
        {
            WindowSeconds = GetShareDiagnosticRetentionHours() * 3600,
            TotalSubmissions = localDatumDiagnostics.Count,
            AcceptedCount = localDatumDiagnostics.Count(item => item.Accepted),
            AcceptedOnDeckCount = localDatumDiagnostics.Count(item => item.Accepted && item.AffectedOnDeck),
            RejectedCount = localDatumDiagnostics.Count(item => !item.Accepted),
            LastAcceptedUtc = localDatumDiagnostics.LastOrDefault(item => item.Accepted)?.TimestampUtc,
            LastRejectedUtc = localDatumDiagnostics.LastOrDefault(item => !item.Accepted)?.TimestampUtc,
            LastRejectionReason = lastRejection?.RejectionReason ?? string.Empty,
            RejectionReasons = localDatumDiagnostics
                .Where(item => !item.Accepted && !string.IsNullOrWhiteSpace(item.RejectionCategory))
                .GroupBy(item => item.RejectionCategory!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new BootReasonCountDto
                {
                    Reason = group.Key,
                    Count = group.Count()
                })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Reason, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList()
        };
    }

    private void RecordShareDiagnosticNoLock(
        string source,
        string minerAddress,
        string username,
        bool accepted,
        bool affectedOnDeck,
        string? rejectionReason,
        double difficulty,
        DateTime timestampUtc)
    {
        BootShareDiagnosticTelemetry diagnostic = CreateShareDiagnosticNoLock(
            source,
            minerAddress,
            username,
            accepted,
            affectedOnDeck,
            rejectionReason,
            difficulty,
            timestampUtc);
        _recentShareDiagnostics.Add(diagnostic);
        if (!accepted)
        {
            _state.RecentRejectedShareDiagnostics.Add(CloneShareDiagnostic(diagnostic));
        }

        TrimShareDiagnosticsNoLock(timestampUtc);
    }

    private BootShareDiagnosticTelemetry CreateShareDiagnosticNoLock(
        string source,
        string minerAddress,
        string username,
        bool accepted,
        bool affectedOnDeck,
        string? rejectionReason,
        double difficulty,
        DateTime timestampUtc)
    {
        return new BootShareDiagnosticTelemetry
        {
            Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            MinerAddress = minerAddress,
            Username = username,
            Accepted = accepted,
            AffectedOnDeck = affectedOnDeck,
            RejectionReason = rejectionReason,
            RejectionCategory = NormalizeDiagnosticReason(rejectionReason),
            Difficulty = difficulty,
            CurrentRoundNumber = _state.CurrentRoundNumber,
            CurrentStateId = _state.CurrentStateId,
            CandidateStateId = _state.CandidateStateId,
            CurrentTipBlockHash = _state.CurrentTipBlockHash,
            CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
            TimestampUtc = timestampUtc
        };
    }

    private void RecordPeerRelayObservationNoLock(
        string source,
        string shareId,
        string minerAddress,
        string username,
        bool accepted,
        bool affectedOnDeck,
        string? rejectionReason,
        double difficulty,
        int payloadBytes,
        double validationDurationMs,
        DateTime timestampUtc,
        string? proofClass = null,
        string? relayStage = null,
        DateTime? transportReceivedUtc = null,
        DateTime? stateServiceReceivedUtc = null,
        DateTime? difficultyCheckedUtc = null,
        DateTime? validationCompletedUtc = null,
        DateTime? stateMutationCompletedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(shareId) ||
            !BootPeerSource.TryParsePeerSource(source, out string transport, out string remoteEndpoint))
        {
            return;
        }

        string normalizedShareId = BitcoinHashes.NormalizeHex(shareId);
        if (string.IsNullOrWhiteSpace(normalizedShareId))
        {
            normalizedShareId = shareId.Trim();
        }

        if (!_peerRelayFirstArrivals.TryGetValue(normalizedShareId, out PeerRelayFirstArrival? firstArrival) ||
            timestampUtc < firstArrival.TimestampUtc)
        {
            firstArrival = new PeerRelayFirstArrival(timestampUtc, transport);
            _peerRelayFirstArrivals[normalizedShareId] = firstArrival;
        }

        _state.RecentPeerRelayObservations.Add(new BootPeerRelayObservation
        {
            ShareId = normalizedShareId,
            ProofClass = ResolveProofClass(proofClass),
            RelayStage = ResolveRelayStage(relayStage),
            Transport = transport,
            Source = source,
            RemoteEndpoint = NormalizePeerEndpoint(remoteEndpoint),
            MinerAddress = minerAddress,
            Username = string.IsNullOrWhiteSpace(username) ? minerAddress : username,
            Difficulty = difficulty,
            Accepted = accepted,
            AffectedOnDeck = affectedOnDeck,
            RejectionReason = rejectionReason,
            IsFirstArrival = timestampUtc == firstArrival.TimestampUtc &&
                string.Equals(transport, firstArrival.Transport, StringComparison.OrdinalIgnoreCase),
            FirstTransport = firstArrival.Transport,
            DeltaFromFirstMs = Math.Max(0, (timestampUtc - firstArrival.TimestampUtc).TotalMilliseconds),
            PayloadBytes = Math.Max(0, payloadBytes),
            ValidationDurationMs = Math.Max(0, validationDurationMs),
            TransportReceivedUtc = transportReceivedUtc,
            StateServiceReceivedUtc = stateServiceReceivedUtc,
            DifficultyCheckedUtc = difficultyCheckedUtc,
            ValidationCompletedUtc = validationCompletedUtc,
            StateMutationCompletedUtc = stateMutationCompletedUtc,
            TransportToStateServiceMs = MillisecondsBetween(transportReceivedUtc, stateServiceReceivedUtc),
            StateServiceToDifficultyMs = MillisecondsBetween(stateServiceReceivedUtc, difficultyCheckedUtc),
            DifficultyToValidationMs = MillisecondsBetween(difficultyCheckedUtc, validationCompletedUtc),
            ValidationToMutationMs = MillisecondsBetween(validationCompletedUtc, stateMutationCompletedUtc),
            CurrentRoundNumber = _state.CurrentRoundNumber,
            CurrentStateId = _state.CurrentStateId,
            CandidateStateId = _state.CandidateStateId,
            CurrentTipBlockHash = _state.CurrentTipBlockHash,
            CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
            TimestampUtc = timestampUtc
        });

        TrimPeerRelayObservationsNoLock(timestampUtc);
    }

    private static string ResolveProofClass(string? proofClass)
    {
        return string.Equals(proofClass, BootProofClasses.Pulse, StringComparison.OrdinalIgnoreCase)
            ? BootProofClasses.Pulse
            : BootProofClasses.Work;
    }

    private static string ResolveRelayStage(string? relayStage)
    {
        return string.Equals(relayStage, BootRelayStages.Optimistic, StringComparison.OrdinalIgnoreCase)
            ? BootRelayStages.Optimistic
            : BootRelayStages.Validated;
    }

    private static double? MillisecondsBetween(DateTime? startUtc, DateTime? endUtc)
    {
        if (!startUtc.HasValue || !endUtc.HasValue)
        {
            return null;
        }

        double value = (NormalizeUtc(endUtc.Value) - NormalizeUtc(startUtc.Value)).TotalMilliseconds;
        return value >= 0 && value <= 300000 ? value : null;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private void RecordNetworkEventNoLock(
        string eventType,
        string source,
        string? message,
        string? blockHash,
        long? blockHeight,
        DateTime? timestampUtc = null,
        string transport = "",
        string remoteEndpoint = "",
        string remoteNodeId = "",
        DateTime? announcedAtUtc = null,
        double? relayLatencyMs = null,
        int payloadBytes = 0)
    {
        _state.RecentNetworkEvents.Add(new BootNetworkEvent
        {
            EventType = string.IsNullOrWhiteSpace(eventType) ? "unknown" : eventType,
            Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
            Message = string.IsNullOrWhiteSpace(message) ? null : message,
            Transport = string.IsNullOrWhiteSpace(transport) ? string.Empty : transport,
            RemoteEndpoint = string.IsNullOrWhiteSpace(remoteEndpoint) ? string.Empty : remoteEndpoint,
            RemoteNodeId = string.IsNullOrWhiteSpace(remoteNodeId) ? string.Empty : remoteNodeId,
            AnnouncedAtUtc = announcedAtUtc,
            RelayLatencyMs = relayLatencyMs,
            PayloadBytes = Math.Max(0, payloadBytes),
            BlockHash = NormalizeCanonicalBlockHash(blockHash),
            BlockHeight = blockHeight,
            CurrentRoundNumber = _state.CurrentRoundNumber,
            CurrentStateId = _state.CurrentStateId,
            CandidateStateId = _state.CandidateStateId,
            CurrentTipBlockHash = _state.CurrentTipBlockHash,
            CurrentTipBlockHeight = _state.CurrentTipBlockHeight,
            TimestampUtc = timestampUtc ?? DateTime.UtcNow
        });

        TrimNetworkEventsNoLock(timestampUtc ?? DateTime.UtcNow);
    }

    private void PublishChainTipAnnouncement(BootChainTipAnnouncement announcement)
    {
        announcement.RelayQueuedUtc = DateTime.UtcNow;
        _chainTipAnnouncements.Writer.TryWrite(announcement);
    }

    private void RecordAcceptedShareTelemetryNoLock(BootShareProof proof)
    {
        var telemetry = new BootAcceptedShareTelemetry
        {
            MinerAddress = proof.MinerAddress,
            Username = proof.Username,
            Source = proof.Source,
            Difficulty = proof.Difficulty,
            TimestampUtc = proof.Timestamp
        };

        _state.RecentAcceptedShares.Add(telemetry);
        RecordLocalDatumAddressHashrateNoLock(telemetry);

        TrimAcceptedShareTelemetryNoLock(proof.Timestamp);
    }

    private void RecordLocalDatumAddressHashrateNoLock(BootAcceptedShareTelemetry share)
    {
        if (!TryNormalizeLocalMiningSource(share.Source, out string normalizedSource) ||
            string.IsNullOrWhiteSpace(share.MinerAddress) ||
            share.Difficulty <= 0)
        {
            return;
        }

        string address = BitcoinScript.NormalizeAddress(share.MinerAddress);
        if (!_localDatumHashrateByAddress.TryGetValue(address, out LocalDatumAddressHashrateTracker? tracker))
        {
            tracker = new LocalDatumAddressHashrateTracker
            {
                Address = address
            };
            _localDatumHashrateByAddress[address] = tracker;
        }

        NormalizeLocalDatumTrackerRoundNoLock(tracker);
        tracker.Sources.Add(normalizedSource);
        tracker.Username = string.IsNullOrWhiteSpace(share.Username) ? address : share.Username;
        tracker.TotalAcceptedShareCount += 1;
        if (!_state.LastRotationUtc.HasValue || share.TimestampUtc >= _state.LastRotationUtc.Value)
        {
            tracker.CurrentRoundAcceptedShareCount += 1;
            tracker.CurrentRoundBestDifficulty = Math.Max(tracker.CurrentRoundBestDifficulty, share.Difficulty);
        }
        tracker.LastShareUtc = share.TimestampUtc;
        tracker.Samples.Add(new LocalDatumShareSample
        {
            Source = normalizedSource,
            Difficulty = share.Difficulty,
            TimestampUtc = share.TimestampUtc
        });

        TrimLocalDatumAddressTrackerNoLock(tracker, share.TimestampUtc);
    }

    private static bool TryNormalizeLocalMiningSource(string? source, out string normalizedSource)
    {
        normalizedSource = (source ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedSource) ||
            BootPeerSource.TryParsePeerSource(normalizedSource, out _, out _) ||
            normalizedSource is "peer" or "admin" or "state-load" or "legacy-state" or "unknown")
        {
            normalizedSource = string.Empty;
            return false;
        }

        return true;
    }

    private void NormalizeLocalDatumTrackerRoundNoLock(LocalDatumAddressHashrateTracker tracker)
    {
        if (tracker.CurrentRoundNumber == _state.CurrentRoundNumber)
        {
            return;
        }

        tracker.CurrentRoundNumber = _state.CurrentRoundNumber;
        tracker.CurrentRoundAcceptedShareCount = 0;
        tracker.CurrentRoundBestDifficulty = 0;
    }

    private void RebuildLocalDatumAddressHashrateNoLock()
    {
        _localDatumHashrateByAddress.Clear();
        foreach (BootAcceptedShareTelemetry share in _state.RecentAcceptedShares.OrderBy(share => share.TimestampUtc))
        {
            RecordLocalDatumAddressHashrateNoLock(share);
        }
    }

    private void RebuildPeerRelayFirstArrivalsNoLock()
    {
        _peerRelayFirstArrivals.Clear();
        foreach (BootPeerRelayObservation observation in _state.RecentPeerRelayObservations
            .Where(item => !string.IsNullOrWhiteSpace(item.ShareId))
            .OrderBy(item => item.TimestampUtc))
        {
            if (!_peerRelayFirstArrivals.ContainsKey(observation.ShareId))
            {
                _peerRelayFirstArrivals[observation.ShareId] = new PeerRelayFirstArrival(
                    observation.TimestampUtc,
                    string.IsNullOrWhiteSpace(observation.Transport) ? "unknown" : observation.Transport);
            }
        }
    }

    private bool MaybeCaptureHashrateSampleNoLock(DateTime nowUtc, bool force)
    {
        int intervalSeconds = GetHashrateSampleIntervalSeconds();
        BootHashratePoint? lastSample = _state.HashrateSamples.Count > 0 ? _state.HashrateSamples[^1] : null;
        if (!force && lastSample != null && (nowUtc - lastSample.TimestampUtc).TotalSeconds < intervalSeconds)
        {
            return false;
        }

        TrimAcceptedShareTelemetryNoLock(nowUtc);
        TrimHashrateSamplesNoLock(nowUtc);

        long? currentRoundElapsedSeconds = ResolveCurrentRoundHashrateElapsedSecondsNoLock(nowUtc);
        List<double> onDeckDifficulties = _state.OnDeckProofs
            .Select(x => x.Difficulty)
            .Where(x => x > 0)
            .ToList();
        // Avoid persisting obviously low-confidence "just rotated" samples into the chart history.
        double? teamEstimatedHashrateThs =
            currentRoundElapsedSeconds.HasValue && currentRoundElapsedSeconds.Value >= 60
                ? EstimateRankAdjustedHashrateThs(onDeckDifficulties, currentRoundElapsedSeconds)
                : null;
        List<BootLocalDatumMinerSummaryDto> localDatumMinerSummaries =
            BuildLocalDatumMinerSummariesNoLock(nowUtc, GetLocalDatumMaxTrackedAddresses());
        double? localDatumHashrateThs = EstimateLocalDatumHashrateThsNoLock(localDatumMinerSummaries);
        CaptureLocalDatumMinerHashrateRollupsNoLock(nowUtc, localDatumMinerSummaries);

        _state.HashrateSamples.Add(new BootHashratePoint
        {
            TimestampUtc = nowUtc,
            CurrentRoundNumber = _state.CurrentRoundNumber,
            TeamEstimatedHashrateThs = teamEstimatedHashrateThs,
            TeamEstimatedHashrateDisplay = FormatObservedHashrate(teamEstimatedHashrateThs),
            LocalDatumHashrateThs = localDatumHashrateThs,
            LocalDatumHashrateDisplay = FormatObservedHashrate(localDatumHashrateThs)
        });

        TrimHashrateSamplesNoLock(nowUtc);
        return true;
    }

    private static double? EstimateLocalDatumHashrateThsNoLock(IEnumerable<BootLocalDatumMinerSummaryDto> minerSummaries)
    {
        double total = minerSummaries
            .Select(miner => miner.CurrentHashrateThs)
            .Where(hashrate => hashrate.HasValue && hashrate.Value > 0)
            .Sum(hashrate => hashrate!.Value);
        return total > 0 ? total : null;
    }

    private bool IsActiveLocalDatumMinerSummaryNoLock(BootLocalDatumMinerSummaryDto summary, DateTime nowUtc)
    {
        if (summary.HashrateSampleCount <= 0 || !summary.LastShareUtc.HasValue)
        {
            return false;
        }

        DateTime activeCutoffUtc = nowUtc.AddSeconds(-GetHashrateLocalWindowSeconds());
        return summary.LastShareUtc.Value >= activeCutoffUtc;
    }

    private bool IsDisplayableLocalDatumMinerSummaryNoLock(BootLocalDatumMinerSummaryDto summary, DateTime nowUtc)
    {
        return IsActiveLocalDatumMinerSummaryNoLock(summary, nowUtc) &&
            summary.HashrateSampleCount >= MinLocalDatumMinerDisplaySamples &&
            !IsTemporaryFoundationLocalDatumSummary(summary, _poolConfig.BitcoinNetwork);
    }

    private static bool IsTemporaryFoundationLocalDatumSummary(BootLocalDatumMinerSummaryDto summary, string bitcoinNetwork)
    {
        string genesisAddress = GetGenesisFoundationAddress(bitcoinNetwork);
        string address = BitcoinScript.NormalizeAddress(summary.Address);
        if (!string.Equals(address, genesisAddress, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string username = summary.Username ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) ||
            string.Equals(BitcoinScript.NormalizeAddress(username), genesisAddress, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ExtractAddressTokens(username, bitcoinNetwork)
            .Any(token => !string.Equals(token, genesisAddress, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractAddressTokens(string value, string bitcoinNetwork)
    {
        char[] separators = ['.', ',', ';', ':', '/', '\\', '|', ' ', '\t', '\r', '\n'];
        foreach (string rawToken in value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = BitcoinScript.NormalizeAddress(rawToken);
            if (BitcoinScript.TryAddressToScriptPubKey(token, bitcoinNetwork, out _))
            {
                yield return token;
            }
        }
    }

    private List<BootLocalDatumMinerSummaryDto> BuildLocalDatumMinerSummariesNoLock(DateTime nowUtc, int? limit = null)
    {
        TrimAcceptedShareTelemetryNoLock(nowUtc);
        TrimLocalDatumAddressHashrateNoLock(nowUtc);

        int localWindowSeconds = GetHashrateLocalWindowSeconds();
        DateTime windowStartUtc = nowUtc.AddSeconds(-localWindowSeconds);
        int effectiveLimit = Math.Clamp(limit ?? GetLocalDatumMinerSummaryLimit(), 1, 5000);

        return _localDatumHashrateByAddress.Values
            .Select(tracker =>
            {
                NormalizeLocalDatumTrackerRoundNoLock(tracker);
                List<LocalDatumShareSample> samples = tracker.Samples
                    .Where(sample => sample.TimestampUtc >= windowStartUtc && sample.Difficulty > 0)
                    .OrderBy(share => share.TimestampUtc)
                    .ToList();
                List<LocalMiningWorkSample> workSamples = tracker.WorkSamples
                    .Where(sample => sample.WindowEndUtc >= windowStartUtc && sample.AcceptedWorkDifficulty > 0)
                    .OrderBy(sample => sample.WindowEndUtc)
                    .ToList();

                double combinedHashrateThs = tracker.Sources.Sum(source =>
                {
                    List<LocalDatumShareSample> sourceShares = samples
                        .Where(sample => string.Equals(sample.Source, source, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    List<LocalMiningWorkSample> sourceWork = workSamples
                        .Where(sample => string.Equals(sample.Source, source, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    double? reportedWorkHashrateThs = EstimateLocalWorkHashrateThsNoLock(sourceWork, windowStartUtc, nowUtc);
                    return reportedWorkHashrateThs ?? EstimateLocalShareHashrateThsNoLock(sourceShares, nowUtc) ?? 0;
                });
                double? hashrateThs = combinedHashrateThs > 0 ? combinedHashrateThs : null;
                long telemetryAcceptedShares = workSamples.Sum(sample => sample.AcceptedShareCount);
                int recentAcceptedShares = (int)Math.Min(int.MaxValue, (long)samples.Count + telemetryAcceptedShares);

                return new BootLocalDatumMinerSummaryDto
                {
                    Address = tracker.Address,
                    Username = string.IsNullOrWhiteSpace(tracker.Username) ? tracker.Address : tracker.Username,
                    Source = tracker.Sources.Count == 0
                        ? "unknown"
                        : string.Join(",", tracker.Sources.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                    TotalAcceptedShareCount = tracker.TotalAcceptedShareCount,
                    RecentAcceptedShareCount = recentAcceptedShares,
                    HashrateSampleCount = recentAcceptedShares,
                    CurrentRoundAcceptedShareCount = tracker.CurrentRoundAcceptedShareCount,
                    CurrentHashrateThs = hashrateThs,
                    CurrentHashrateDisplay = FormatObservedHashrate(hashrateThs),
                    CurrentRoundBestDifficulty = tracker.CurrentRoundBestDifficulty,
                    CurrentRoundBestDifficultyDisplay = ClientHandler.FormatDifficulty(tracker.CurrentRoundBestDifficulty),
                    LastShareUtc = tracker.LastShareUtc
                };
            })
            .OrderByDescending(item => item.CurrentHashrateThs ?? 0)
            .ThenByDescending(item => item.CurrentRoundBestDifficulty)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .ToList();
    }

    private static double? EstimateLocalShareHashrateThsNoLock(
        IReadOnlyList<LocalDatumShareSample> orderedSamples,
        DateTime nowUtc)
    {
        if (orderedSamples.Count < MinLocalDatumMinerDisplaySamples)
        {
            return null;
        }

        DateTime observationStartUtc = orderedSamples[0].TimestampUtc;
        long? elapsedSeconds = GetElapsedSeconds(observationStartUtc, nowUtc);
        if (!elapsedSeconds.HasValue || elapsedSeconds.Value < MinLocalHashrateObservationSeconds)
        {
            return null;
        }

        // The first arrival starts the observation window and is therefore not
        // an independent sample of work performed during that window. Including
        // a lucky first proof can inflate a new miner by orders of magnitude.
        return EstimateRankAdjustedHashrateThs(
            orderedSamples.Skip(1).Select(sample => sample.Difficulty),
            elapsedSeconds);
    }

    private static double? EstimateLocalWorkHashrateThsNoLock(
        IReadOnlyList<LocalMiningWorkSample> orderedSamples,
        DateTime windowStartUtc,
        DateTime nowUtc)
    {
        double workDifficulty = orderedSamples.Sum(sample => sample.AcceptedWorkDifficulty);
        if (orderedSamples.Count == 0 || workDifficulty <= 0)
        {
            return null;
        }

        DateTime workStartUtc = orderedSamples[0].WindowStartUtc > windowStartUtc
            ? orderedSamples[0].WindowStartUtc
            : windowStartUtc;
        long? elapsedSeconds = GetElapsedSeconds(workStartUtc, nowUtc);
        return elapsedSeconds.HasValue && elapsedSeconds.Value > 0
            ? workDifficulty * 4294967296d / elapsedSeconds.Value / 1_000_000_000_000d
            : null;
    }

    private List<BootLocalMiningSourceSummaryDto> BuildLocalMiningSourceSummariesNoLock(DateTime nowUtc)
    {
        int windowSeconds = GetHashrateLocalWindowSeconds();
        DateTime windowStartUtc = nowUtc.AddSeconds(-windowSeconds);
        var sources = _localDatumHashrateByAddress.Values
            .SelectMany(tracker => tracker.Sources)
            .Concat(_localMiningSourceGauges.Keys)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(LocalMiningSourceSortOrder)
            .ThenBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = new List<BootLocalMiningSourceSummaryDto>(sources.Count);
        foreach (string source in sources)
        {
            int activeMinerCount = 0;
            long acceptedShareCount = 0;
            int sampleCount = 0;
            double totalHashrateThs = 0;
            bool hasReportedWorkEstimate = false;
            bool hasProofEstimate = false;
            DateTime? lastShareUtc = null;
            LocalMiningSourceGauge? gauge = _localMiningSourceGauges.GetValueOrDefault(source);
            bool gaugeIsFresh = gauge != null && gauge.ObservedUtc >= nowUtc.AddMinutes(-2);

            foreach (LocalDatumAddressHashrateTracker tracker in _localDatumHashrateByAddress.Values)
            {
                if (IsTemporaryFoundationLocalDatumSummary(new BootLocalDatumMinerSummaryDto
                {
                    Address = tracker.Address,
                    Username = tracker.Username
                }, _poolConfig.BitcoinNetwork))
                {
                    continue;
                }

                List<LocalDatumShareSample> shareSamples = tracker.Samples
                    .Where(sample => string.Equals(sample.Source, source, StringComparison.OrdinalIgnoreCase) &&
                                     sample.TimestampUtc >= windowStartUtc &&
                                     sample.Difficulty > 0)
                    .OrderBy(sample => sample.TimestampUtc)
                    .ToList();
                List<LocalMiningWorkSample> workSamples = tracker.WorkSamples
                    .Where(sample => string.Equals(sample.Source, source, StringComparison.OrdinalIgnoreCase) &&
                                     sample.WindowEndUtc >= windowStartUtc &&
                                     sample.AcceptedWorkDifficulty > 0)
                    .OrderBy(sample => sample.WindowEndUtc)
                    .ToList();
                DateTime? minerLastShareUtc = shareSamples.Select(sample => (DateTime?)sample.TimestampUtc)
                    .Concat(workSamples.Select(sample => (DateTime?)sample.WindowEndUtc))
                    .OrderByDescending(timestamp => timestamp)
                    .FirstOrDefault();
                if (!minerLastShareUtc.HasValue || minerLastShareUtc.Value < windowStartUtc)
                {
                    continue;
                }

                activeMinerCount++;
                lastShareUtc = !lastShareUtc.HasValue || minerLastShareUtc.Value > lastShareUtc.Value
                    ? minerLastShareUtc
                    : lastShareUtc;
                long workAcceptedShares = workSamples.Sum(sample => sample.AcceptedShareCount);
                acceptedShareCount += shareSamples.Count + workAcceptedShares;
                sampleCount = (int)Math.Min(
                    int.MaxValue,
                    (long)sampleCount + shareSamples.Count + workAcceptedShares);

                double? proofHashrateThs = EstimateLocalShareHashrateThsNoLock(shareSamples, nowUtc);
                double? workHashrateThs = EstimateLocalWorkHashrateThsNoLock(workSamples, windowStartUtc, nowUtc);
                if (workHashrateThs.HasValue)
                {
                    totalHashrateThs += workHashrateThs.Value;
                    hasReportedWorkEstimate = true;
                }
                else if (proofHashrateThs.HasValue)
                {
                    totalHashrateThs += proofHashrateThs.Value;
                    hasProofEstimate = true;
                }
            }

            summaries.Add(new BootLocalMiningSourceSummaryDto
            {
                Source = source,
                DisplayName = FormatLocalMiningSourceName(source),
                ActiveMinerCount = gaugeIsFresh ? gauge!.ActiveMinerCount : activeMinerCount,
                RecentAcceptedShareCount = acceptedShareCount,
                HashrateSampleCount = sampleCount,
                CurrentHashrateThs = gaugeIsFresh
                    ? gauge!.HashrateThs
                    : totalHashrateThs > 0 ? totalHashrateThs : null,
                CurrentHashrateDisplay = FormatObservedHashrate(
                    gaugeIsFresh ? gauge!.HashrateThs : totalHashrateThs > 0 ? totalHashrateThs : null),
                EstimationMethod = gaugeIsFresh
                    ? "client-api"
                    : hasReportedWorkEstimate && hasProofEstimate
                        ? "reported-work-and-proofs"
                        : hasReportedWorkEstimate
                            ? "reported-work"
                            : hasProofEstimate
                                ? "proof-order-statistic"
                                : "insufficient-data",
                LastShareUtc = gaugeIsFresh && (!lastShareUtc.HasValue || gauge!.ObservedUtc > lastShareUtc.Value)
                    ? gauge!.ObservedUtc
                    : lastShareUtc
            });
        }

        return summaries
            .Where(summary => summary.ActiveMinerCount > 0 || summary.CurrentHashrateThs.HasValue)
            .ToList();
    }

    private static int LocalMiningSourceSortOrder(string source) => source.ToLowerInvariant() switch
    {
        "datum" => 0,
        "ckpool" or "atlaspool" => 1,
        "hydrapool" => 2,
        "sv2" => 3,
        "http" => 4,
        _ => 5
    };

    private static string FormatLocalMiningSourceName(string source) => source.ToLowerInvariant() switch
    {
        "datum" => "DATUM",
        "ckpool" => "CKPool",
        "atlaspool" => "AtlasPool",
        "hydrapool" => "Hydrapool",
        "sv2" => "Stratum V2",
        "http" => "Direct HTTP",
        _ => source
    };

    private void CaptureLocalDatumMinerHashrateRollupsNoLock(DateTime nowUtc, IEnumerable<BootLocalDatumMinerSummaryDto> minerSummaries)
    {
        int intervalSeconds = GetLocalDatumHashrateRollupIntervalSeconds();
        foreach (BootLocalDatumMinerSummaryDto summary in minerSummaries)
        {
            string address = BitcoinScript.NormalizeAddress(summary.Address);
            if (string.IsNullOrWhiteSpace(address) ||
                IsTemporaryFoundationLocalDatumSummary(summary, _poolConfig.BitcoinNetwork) ||
                !IsActiveLocalDatumMinerSummaryNoLock(summary, nowUtc) ||
                !summary.CurrentHashrateThs.HasValue ||
                summary.CurrentHashrateThs.Value <= 0)
            {
                continue;
            }

            if (_lastLocalDatumHashrateRollupByAddress.TryGetValue(address, out DateTime lastRollupUtc) &&
                (nowUtc - lastRollupUtc).TotalSeconds < intervalSeconds)
            {
                continue;
            }

            _state.LocalDatumMinerHashrateSamples.Add(new BootLocalDatumMinerHashrateRollupPoint
            {
                Address = address,
                Username = string.IsNullOrWhiteSpace(summary.Username) ? address : summary.Username,
                TimestampUtc = nowUtc,
                CurrentRoundNumber = _state.CurrentRoundNumber,
                HashrateThs = summary.CurrentHashrateThs,
                HashrateDisplay = summary.CurrentHashrateDisplay,
                SampleCount = summary.HashrateSampleCount
            });
            _lastLocalDatumHashrateRollupByAddress[address] = nowUtc;
        }

        TrimLocalDatumMinerHashrateSamplesNoLock(nowUtc);
    }

    private List<BootLocalDatumMinerHashratePointDto> BuildLocalDatumMinerHashratePointsNoLock(string address, DateTime nowUtc, string? windowKey)
    {
        string normalizedAddress = BitcoinScript.NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(normalizedAddress))
        {
            return [];
        }

        DateTime cutoffUtc = ResolveHashrateSeriesCutoffUtc(windowKey, nowUtc);
        TrimLocalDatumMinerHashrateSamplesNoLock(nowUtc);
        List<BootLocalDatumMinerHashratePointDto> points = _state.LocalDatumMinerHashrateSamples
            .Where(point =>
                point.TimestampUtc >= cutoffUtc &&
                string.Equals(BitcoinScript.NormalizeAddress(point.Address), normalizedAddress, StringComparison.OrdinalIgnoreCase) &&
                point.HashrateThs.HasValue &&
                point.HashrateThs.Value > 0)
            .OrderBy(point => point.TimestampUtc)
            .Select(point => new BootLocalDatumMinerHashratePointDto
            {
                TimestampUtc = point.TimestampUtc,
                HashrateThs = point.HashrateThs,
                HashrateDisplay = string.IsNullOrWhiteSpace(point.HashrateDisplay)
                    ? FormatObservedHashrate(point.HashrateThs)
                    : point.HashrateDisplay,
                SampleCount = point.SampleCount
            })
            .ToList();

        return points.Count > 0
            ? points
            : BuildLocalDatumMinerHashratePointsFromRawSamplesNoLock(normalizedAddress, nowUtc);
    }

    private List<BootLocalDatumMinerHashratePointDto> BuildLocalDatumMinerHashratePointsFromRawSamplesNoLock(string address, DateTime nowUtc)
    {
        string normalizedAddress = BitcoinScript.NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(normalizedAddress) ||
            !_localDatumHashrateByAddress.TryGetValue(normalizedAddress, out LocalDatumAddressHashrateTracker? tracker))
        {
            return [];
        }

        TrimLocalDatumAddressTrackerNoLock(tracker, nowUtc);
        List<LocalDatumShareSample> samples = tracker.Samples
            .Where(sample => sample.Difficulty > 0)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();
        if (samples.Count == 0)
        {
            return [];
        }

        int windowSeconds = GetHashrateLocalWindowSeconds();
        int intervalSeconds = Math.Clamp(GetHashrateSampleIntervalSeconds(), 10, 300);
        DateTime windowStartUtc = nowUtc.AddSeconds(-windowSeconds);
        DateTime firstPointUtc = samples[0].TimestampUtc > windowStartUtc ? samples[0].TimestampUtc : windowStartUtc;
        List<DateTime> pointTimes = [];

        for (DateTime pointTimeUtc = firstPointUtc; pointTimeUtc < nowUtc; pointTimeUtc = pointTimeUtc.AddSeconds(intervalSeconds))
        {
            pointTimes.Add(pointTimeUtc);
        }

        if (pointTimes.Count == 0 || (nowUtc - pointTimes[^1]).TotalSeconds >= 2)
        {
            pointTimes.Add(nowUtc);
        }

        List<BootLocalDatumMinerHashratePointDto> points = [];
        foreach (DateTime pointTimeUtc in pointTimes)
        {
            DateTime pointWindowStartUtc = pointTimeUtc.AddSeconds(-windowSeconds);
            List<LocalDatumShareSample> pointSamples = samples
                .Where(sample => sample.TimestampUtc >= pointWindowStartUtc && sample.TimestampUtc <= pointTimeUtc)
                .ToList();
            if (pointSamples.Count == 0)
            {
                continue;
            }

            DateTime firstRateShareUtc = pointSamples[0].TimestampUtc;
            DateTime effectiveRateStartUtc = firstRateShareUtc > pointWindowStartUtc
                ? firstRateShareUtc
                : pointWindowStartUtc;
            long? rateElapsedSeconds = GetElapsedSeconds(effectiveRateStartUtc, pointTimeUtc);
            double? hashrateThs = EstimateRankAdjustedHashrateThs(pointSamples.Select(share => share.Difficulty), rateElapsedSeconds);
            if (!hashrateThs.HasValue || hashrateThs.Value <= 0)
            {
                continue;
            }

            points.Add(new BootLocalDatumMinerHashratePointDto
            {
                TimestampUtc = pointTimeUtc,
                HashrateThs = hashrateThs,
                HashrateDisplay = FormatObservedHashrate(hashrateThs),
                SampleCount = pointSamples.Count
            });
        }

        return points;
    }

    private void TrimShareDiagnosticsNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        _recentShareDiagnostics.RemoveAll(item => item.TimestampUtc < cutoffUtc);
        while (_recentShareDiagnostics.Count > MaxSeenShareIds)
        {
            _recentShareDiagnostics.RemoveAt(0);
        }
        _state.RecentRejectedShareDiagnostics.RemoveAll(item => item.TimestampUtc < cutoffUtc || item.Accepted);
        int rejectedOverflow = _state.RecentRejectedShareDiagnostics.Count - MaxRecentRejectedShareDiagnostics;
        if (rejectedOverflow > 0)
        {
            _state.RecentRejectedShareDiagnostics.RemoveRange(0, rejectedOverflow);
        }
    }

    private void TrimCoinbaserDiagnosticsNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        _state.RecentCoinbaserDiagnostics = _state.RecentCoinbaserDiagnostics
            .Where(item => item.TimestampUtc >= cutoffUtc)
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(MaxRecentCoinbaserDiagnostics)
            .ToList();
    }

    private void TrimDatumShareResponsesNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        _state.RecentDatumShareResponses.RemoveAll(item => item.TimestampUtc < cutoffUtc);
        int overflow = _state.RecentDatumShareResponses.Count - MaxRecentDatumShareResponses;
        if (overflow > 0)
        {
            _state.RecentDatumShareResponses.RemoveRange(0, overflow);
        }
    }

    private void TrimDatumSessionsNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        _state.RecentDatumSessions = _state.RecentDatumSessions
            .Where(item => item.ClosedUtc == null || item.ClosedUtc >= cutoffUtc || item.StartedUtc >= cutoffUtc)
            .OrderBy(item => item.StartedUtc)
            .ToList();

        while (_state.RecentDatumSessions.Count > MaxRecentDatumSessions)
        {
            int removeIndex = _state.RecentDatumSessions.FindIndex(item => item.ClosedUtc != null);
            if (removeIndex < 0)
            {
                break;
            }

            _activeDatumSessions.Remove(_state.RecentDatumSessions[removeIndex].SessionId);
            _state.RecentDatumSessions.RemoveAt(removeIndex);
        }
    }

    private void TrimDatumProtocolEventsNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        _recentDatumProtocolEvents.RemoveAll(item => item.TimestampUtc < cutoffUtc);
        while (_recentDatumProtocolEvents.Count > MaxRecentDatumProtocolEvents)
        {
            _recentDatumProtocolEvents.RemoveAt(0);
        }
    }

    private void TrimNetworkEventsNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetNetworkEventRetentionHours());
        _state.RecentNetworkEvents = _state.RecentNetworkEvents
            .Where(item => item.TimestampUtc >= cutoffUtc)
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(MaxRecentNetworkEvents)
            .ToList();
    }

    private void TrimPeerRelayObservationsNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetShareDiagnosticRetentionHours());
        _state.RecentPeerRelayObservations = _state.RecentPeerRelayObservations
            .Where(item => item.TimestampUtc >= cutoffUtc)
            .OrderBy(item => item.TimestampUtc)
            .TakeLast(MaxRecentPeerRelayObservations)
            .ToList();

        HashSet<string> retainedShareIds = _state.RecentPeerRelayObservations
            .Select(item => item.ShareId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string shareId in _peerRelayFirstArrivals.Keys.ToList())
        {
            if (!retainedShareIds.Contains(shareId))
            {
                _peerRelayFirstArrivals.Remove(shareId);
            }
        }
    }

    private void TrimAcceptedShareTelemetryNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddHours(-GetAcceptedShareTelemetryRetentionHours());
        _state.RecentAcceptedShares.RemoveAll(share => share.TimestampUtc < cutoffUtc);
        int overflow = _state.RecentAcceptedShares.Count - GetMaxAcceptedShareTelemetryEntries();
        if (overflow > 0)
        {
            _state.RecentAcceptedShares.RemoveRange(0, overflow);
        }
    }

    private void TrimLocalDatumAddressHashrateNoLock(DateTime nowUtc)
    {
        DateTime staleAddressCutoffUtc = nowUtc.AddHours(-GetAcceptedShareTelemetryRetentionHours());
        foreach (LocalDatumAddressHashrateTracker tracker in _localDatumHashrateByAddress.Values)
        {
            TrimLocalDatumAddressTrackerNoLock(tracker, nowUtc);
        }

        List<string> staleAddresses = _localDatumHashrateByAddress
            .Where(item => item.Value.LastShareUtc.HasValue && item.Value.LastShareUtc.Value < staleAddressCutoffUtc)
            .Select(item => item.Key)
            .ToList();
        foreach (string address in staleAddresses)
        {
            _localDatumHashrateByAddress.Remove(address);
        }

        int maxAddresses = GetLocalDatumMaxTrackedAddresses();
        if (_localDatumHashrateByAddress.Count <= maxAddresses)
        {
            return;
        }

        List<string> overflow = _localDatumHashrateByAddress.Values
            .OrderBy(tracker => tracker.LastShareUtc ?? DateTime.MinValue)
            .Take(_localDatumHashrateByAddress.Count - maxAddresses)
            .Select(tracker => tracker.Address)
            .ToList();
        foreach (string address in overflow)
        {
            _localDatumHashrateByAddress.Remove(address);
        }
    }

    private void TrimLocalDatumAddressTrackerNoLock(LocalDatumAddressHashrateTracker tracker, DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddSeconds(-GetHashrateLocalWindowSeconds());
        int maxSamples = GetLocalDatumHashratePerAddressMaxSamples();
        tracker.Samples.RemoveAll(sample => sample.TimestampUtc < cutoffUtc);
        tracker.WorkSamples.RemoveAll(sample => sample.WindowEndUtc < cutoffUtc);
        int overflow = tracker.Samples.Count - maxSamples;
        if (overflow > 0)
        {
            tracker.Samples.RemoveRange(0, overflow);
        }
        foreach (IGrouping<string, LocalMiningWorkSample> sourceSamples in tracker.WorkSamples
                     .GroupBy(sample => sample.Source, StringComparer.OrdinalIgnoreCase)
                     .ToList())
        {
            int workOverflow = sourceSamples.Count() - maxSamples;
            if (workOverflow <= 0)
            {
                continue;
            }

            HashSet<LocalMiningWorkSample> remove = sourceSamples
                .OrderBy(sample => sample.WindowEndUtc)
                .Take(workOverflow)
                .ToHashSet();
            tracker.WorkSamples.RemoveAll(remove.Contains);
        }
    }

    private void TrimHashrateSamplesNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddDays(-GetHashrateSampleRetentionDays());
        _state.HashrateSamples = _state.HashrateSamples
            .Where(point => point.TimestampUtc >= cutoffUtc)
            .OrderBy(point => point.TimestampUtc)
            .ToList();
    }

    private void TrimLocalDatumMinerHashrateSamplesNoLock(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddDays(-GetLocalDatumHashrateRollupRetentionDays());
        int originalCount = _state.LocalDatumMinerHashrateSamples.Count;
        _state.LocalDatumMinerHashrateSamples = _state.LocalDatumMinerHashrateSamples
            .Where(point =>
                point.TimestampUtc >= cutoffUtc &&
                !string.IsNullOrWhiteSpace(point.Address) &&
                point.HashrateThs.HasValue &&
                point.HashrateThs.Value > 0)
            .OrderBy(point => point.TimestampUtc)
            .TakeLast(GetLocalDatumHashrateRollupMaxPoints())
            .ToList();
        if (_state.LocalDatumMinerHashrateSamples.Count != originalCount)
        {
            RebuildLocalDatumHashrateRollupIndexNoLock();
        }
    }

    private void RebuildLocalDatumHashrateRollupIndexNoLock()
    {
        _lastLocalDatumHashrateRollupByAddress.Clear();
        foreach (BootLocalDatumMinerHashrateRollupPoint point in _state.LocalDatumMinerHashrateSamples.OrderBy(point => point.TimestampUtc))
        {
            string address = BitcoinScript.NormalizeAddress(point.Address);
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            _lastLocalDatumHashrateRollupByAddress[address] = point.TimestampUtc;
        }
    }

    private int GetHashrateSampleIntervalSeconds() => Math.Clamp(_poolConfig.HashrateSampleIntervalSeconds, 10, 3600);

    private int GetHashrateLocalWindowSeconds() => Math.Clamp(_poolConfig.HashrateLocalWindowSeconds, 60, 86400);

    private int GetLocalDatumMinerSummaryLimit() => Math.Clamp(_poolConfig.LocalDatumMinerSummaryLimit, 1, 5000);

    private int GetLocalDatumHashratePerAddressMaxSamples() => Math.Clamp(_poolConfig.LocalDatumHashratePerAddressMaxSamples, 16, 10000);

    private int GetLocalDatumMaxTrackedAddresses() => Math.Clamp(_poolConfig.LocalDatumHashrateMaxAddresses, 1, 50000);

    private int GetLocalDatumHashrateRollupIntervalSeconds() => Math.Clamp(_poolConfig.LocalDatumHashrateRollupIntervalSeconds, 30, 3600);

    private int GetLocalDatumHashrateRollupRetentionDays() => Math.Clamp(_poolConfig.LocalDatumHashrateRollupRetentionDays, 1, 90);

    private int GetLocalDatumHashrateRollupMaxPoints() => Math.Clamp(_poolConfig.LocalDatumHashrateRollupMaxPoints, 1000, 5_000_000);

    private int GetMaxAcceptedShareTelemetryEntries() => Math.Clamp(_poolConfig.MaxAcceptedShareTelemetryEntries, 1000, 1_000_000);

    private int GetHashrateSampleRetentionDays() => Math.Clamp(_poolConfig.HashrateSampleRetentionDays, 1, 365);

    private int GetAcceptedShareTelemetryRetentionHours() => Math.Clamp(_poolConfig.AcceptedShareTelemetryRetentionHours, 1, 168);

    private int GetShareDiagnosticRetentionHours() => Math.Clamp(_poolConfig.ShareDiagnosticRetentionHours, 1, 168);

    private int GetNetworkEventRetentionHours() => Math.Clamp(_poolConfig.NetworkEventRetentionHours, 1, 24 * 30);

    private int GetDatumShareResponseSlowMs() => Math.Clamp(_poolConfig.DatumShareResponseSlowMs, 50, 30000);

    private int GetPeerOutboundTarget() => Math.Clamp(_poolConfig.PeerOutboundTarget, 1, GetPeerAddressBookMaxEntries());

    private int GetPeerShareRelayTarget() => Math.Clamp(_poolConfig.PeerShareRelayTarget, 1, GetPeerAddressBookMaxEntries());

    private int GetPeerSessionTarget() => Math.Clamp(_poolConfig.PeerSessionTarget, 1, GetPeerAddressBookMaxEntries());

    private int GetPeerRelayParallelismLimit() => Math.Clamp(_poolConfig.PeerRelayParallelism, 1, 256);

    private int GetPeerAddressBookMaxEntries() => Math.Clamp(Math.Max(_poolConfig.PeerAddressBookMaxEntries, _poolConfig.MaxPeers), 1, 100000);

    private int GetPeerAddressGossipLimit() => Math.Clamp(_poolConfig.PeerAddressGossipLimit, 1, 10000);

    private int GetPeerFailureBackoffMinSeconds() => Math.Clamp(_poolConfig.PeerFailureBackoffMinSeconds, 1, 3600);

    private int GetPeerFailureBackoffMaxSeconds() => Math.Clamp(Math.Max(_poolConfig.PeerFailureBackoffMaxSeconds, _poolConfig.PeerFailureBackoffMinSeconds), 1, 86400);

    private int GetPeerTombstoneSeconds() => Math.Clamp(_poolConfig.PeerTombstoneSeconds, 60, 31_536_000);

    private DateTime ResolveTelemetryCutoffUtc(string? windowKey, DateTime nowUtc, int retentionHours)
    {
        DateTime retentionCutoffUtc = nowUtc.AddHours(-Math.Clamp(retentionHours, 1, 168));
        TimeSpan? requestedWindow = windowKey?.ToLowerInvariant() switch
        {
            "1h" => TimeSpan.FromHours(1),
            "6h" => TimeSpan.FromHours(6),
            "12h" => TimeSpan.FromHours(12),
            "24h" => TimeSpan.FromHours(24),
            "48h" => TimeSpan.FromHours(48),
            "7d" => TimeSpan.FromDays(7),
            _ => null
        };

        if (!requestedWindow.HasValue)
        {
            return retentionCutoffUtc;
        }

        DateTime requestedCutoffUtc = nowUtc.Subtract(requestedWindow.Value);
        return requestedCutoffUtc > retentionCutoffUtc ? requestedCutoffUtc : retentionCutoffUtc;
    }

    private static double? Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        double clamped = Math.Clamp(percentile, 0, 1);
        int index = (int)Math.Ceiling(clamped * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private DateTime ResolveHashrateSeriesCutoffUtc(string? windowKey, DateTime nowUtc)
    {
        DateTime retentionCutoffUtc = nowUtc.AddDays(-GetHashrateSampleRetentionDays());
        TimeSpan? requestedWindow = windowKey?.ToLowerInvariant() switch
        {
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => null
        };

        if (!requestedWindow.HasValue)
        {
            return retentionCutoffUtc;
        }

        DateTime requestedCutoffUtc = nowUtc.Subtract(requestedWindow.Value);
        return requestedCutoffUtc > retentionCutoffUtc ? requestedCutoffUtc : retentionCutoffUtc;
    }

    private static string? NormalizeDiagnosticReason(string? rejectionReason)
    {
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            return null;
        }

        if (rejectionReason.StartsWith("Coinbase winners payouts do not match", StringComparison.OrdinalIgnoreCase))
        {
            return "Payout mismatch";
        }

        if (rejectionReason.StartsWith("Coinbase appears truncated by miner firmware", StringComparison.OrdinalIgnoreCase))
        {
            return "Firmware coinbase truncation";
        }

        if (rejectionReason.StartsWith("Coinbase appears to use a non-Boot single-recipient template", StringComparison.OrdinalIgnoreCase))
        {
            return "Solo fallback template";
        }

        if (rejectionReason.StartsWith("Share builds on the wrong parent block", StringComparison.OrdinalIgnoreCase))
        {
            return "Wrong parent block";
        }

        if (rejectionReason.StartsWith("Coinbase slot 0 does not pay", StringComparison.OrdinalIgnoreCase))
        {
            return "Slot 0 mismatch";
        }

        if (rejectionReason.StartsWith("Prev block hash does not match", StringComparison.OrdinalIgnoreCase))
        {
            return "Prev block mismatch";
        }

        if (rejectionReason.StartsWith("Round changed during validation", StringComparison.OrdinalIgnoreCase))
        {
            return "Round changed";
        }

        if (rejectionReason.StartsWith("Accepted parent set changed during validation", StringComparison.OrdinalIgnoreCase))
        {
            return "Accepted parent changed";
        }

        if (rejectionReason.StartsWith("Duplicate share", StringComparison.OrdinalIgnoreCase))
        {
            return "Duplicate share";
        }

        if (rejectionReason.StartsWith("Low difficulty", StringComparison.OrdinalIgnoreCase))
        {
            return "Low difficulty";
        }

        return rejectionReason;
    }

    private static double? EstimateRankAdjustedHashrateThs(IEnumerable<double> difficulties, long? elapsedSeconds)
    {
        if (!elapsedSeconds.HasValue || elapsedSeconds.Value <= 0)
        {
            return null;
        }

        List<double> rankedDifficulties = difficulties
            .Where(difficulty => difficulty > 0)
            .OrderByDescending(difficulty => difficulty)
            .ToList();
        if (rankedDifficulties.Count == 0)
        {
            return null;
        }

        var perShareEstimatesThs = new List<double>(rankedDifficulties.Count);
        for (int index = 0; index < rankedDifficulties.Count; index++)
        {
            double hashesPerSecond = (index + 1) * rankedDifficulties[index] * 4294967296d / elapsedSeconds.Value;
            perShareEstimatesThs.Add(hashesPerSecond / 1_000_000_000_000d);
        }

        perShareEstimatesThs.Sort();
        int middle = perShareEstimatesThs.Count / 2;
        if (perShareEstimatesThs.Count % 2 == 1)
        {
            return perShareEstimatesThs[middle];
        }

        return (perShareEstimatesThs[middle - 1] + perShareEstimatesThs[middle]) / 2d;
    }

    private static string FormatObservedHashrate(double? hashrateThs)
    {
        if (!hashrateThs.HasValue)
        {
            return "--";
        }

        double hashesPerSecond = hashrateThs.Value * 1_000_000_000_000d;
        if (hashesPerSecond >= 1_000_000_000_000_000_000d)
        {
            return $"{hashesPerSecond / 1_000_000_000_000_000_000d:0.##} EH/s";
        }

        if (hashesPerSecond >= 1_000_000_000_000_000d)
        {
            return $"{hashesPerSecond / 1_000_000_000_000_000d:0.##} PH/s";
        }

        if (hashesPerSecond >= 1_000_000_000_000d)
        {
            return $"{hashesPerSecond / 1_000_000_000_000d:0.##} TH/s";
        }

        if (hashesPerSecond >= 1_000_000_000d)
        {
            return $"{hashesPerSecond / 1_000_000_000d:0.##} GH/s";
        }

        if (hashesPerSecond >= 1_000_000d)
        {
            return $"{hashesPerSecond / 1_000_000d:0.##} MH/s";
        }

        if (hashesPerSecond >= 1_000d)
        {
            return $"{hashesPerSecond / 1_000d:0.##} kH/s";
        }

        return $"{hashesPerSecond:0.##} H/s";
    }

    private BootCommitmentInfo BuildCommitmentNoLock()
    {
        string previewState = string.IsNullOrWhiteSpace(_state.CandidateStateId)
            ? "pending"
            : _state.CandidateStateId[..Math.Min(16, _state.CandidateStateId.Length)];

        return new BootCommitmentInfo
        {
            ProtocolVersion = GetActiveConsensusVersionNoLock(),
            NetworkId = _poolConfig.BootNetworkId,
            NextStateId = _state.CandidateStateId,
            OnChainSupported = false,
            TagPreview = $"BOOT|v{GetActiveConsensusVersionNoLock()}|{_poolConfig.BootNetworkId}|{previewState}",
            SupportNote = "Per-round on-chain commitments require miner-side template support. The server computes state IDs now, but DATUM/Hydrapool must expose a dynamic coinbase hook before this can be embedded on-chain."
        };
    }

    private void CacheCurrentCandidateBundleNoLock()
    {
        CacheCandidateBundleNoLock(BuildBundleFromCurrentCandidateNoLock());
    }

    private void CacheCandidateBundleNoLock(BootStateBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(bundle.StateId))
        {
            return;
        }

        _recentCandidateBundles.RemoveAll(existing =>
            string.Equals(existing.StateId, bundle.StateId, StringComparison.OrdinalIgnoreCase));
        _recentCandidateBundles.Add(CloneBundle(bundle));

        if (_recentCandidateBundles.Count <= MaxRecentCandidateBundles)
        {
            return;
        }

        int overflow = _recentCandidateBundles.Count - MaxRecentCandidateBundles;
        _recentCandidateBundles.RemoveRange(0, overflow);
    }

    private BootStateBundle BuildBundleFromCurrentCandidateNoLock()
    {
        BootPayoutSnapshotContext candidateContext = BuildSnapshotContextFromWorkSetNoLock(
            _state.CurrentTipBlockHash,
            _state.CurrentTipBlockHeight,
            DateTime.UtcNow,
            _state.CurrentRoundNumber + 1);
        List<BootShareProof> snapshotProofs = SortAndTrimProofs(_state.OnDeckProofs, _poolConfig.SnapshotProofSlotCount);
        return new BootStateBundle
        {
            StateId = _state.CandidateStateId,
            PreviousStateId = _state.CurrentStateId,
            Kind = "candidate",
            CurrentRoundNumber = _state.CurrentRoundNumber + 1,
            ProtocolVersion = GetActiveConsensusVersionNoLock(),
            ConsensusVersion = GetActiveConsensusVersionNoLock(),
            StateBundleSchemaVersion = BootProtocolVersions.GetStateBundleSchemaVersion(GetActiveConsensusVersionNoLock()),
            HttpApiVersion = BootProtocolVersions.HttpApiVersion,
            PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
            UdpRelayVersion = BootProtocolVersions.UdpRelayVersion,
            ReleaseVersion = GetLocalVersionInfoNoLock().ReleaseVersion,
            VersionInfo = GetLocalVersionInfoNoLock(),
            NetworkId = _poolConfig.BootNetworkId,
            LockedByBlockHash = null,
            LockedByBlockHeight = null,
            ParentBlockHash = _state.CurrentTipBlockHash,
            ParentBlockHeight = _state.CurrentTipBlockHeight,
            CreatedAtUtc = DateTime.UtcNow,
            TotalDifficulty = _state.OnDeckProofs.Sum(x => x.Difficulty),
            ActiveSnapshotId = _state.ActiveSnapshotId,
            PaidSnapshotId = _state.LastPaidSnapshotId,
            ActiveSnapshotProofIds = _state.ActiveSnapshotProofIds.ToList(),
            PaidSnapshotProofIds = _state.LastPaidSnapshotProofIds.ToList(),
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock(),
            ValidParentBlockHashes = GetAcceptedParentBlockHashesNoLock(),
            WinnersList = ClonePayouts(candidateContext.WinnersList),
            ProofWinnersList = ClonePayouts(_state.WinnersList),
            ShareProofs = snapshotProofs.Select(CloneProof).ToList(),
            WorkSetProofs = _state.OnDeckProofs.Select(CloneProof).ToList(),
            SnapshotContexts = BuildSnapshotContextsForBundleNoLock(
                snapshotProofs.Concat(_state.OnDeckProofs),
                [candidateContext]),
            Commitment = BuildCommitmentNoLock(),
            SnapshotFamilyMember = BuildActiveSnapshotFamilyMemberNoLock()
        };
    }

    private BootStateBundle BuildBundleFromCurrentWinnersNoLock()
    {
        string? previousStateId = _state.ArchivedStateBundles
            .FirstOrDefault(bundle => string.Equals(bundle.StateId, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase))
            ?.PreviousStateId;
        HashSet<string> activeProofIds = _state.ActiveSnapshotProofIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        BootSnapshotFamilyState? activeFamily = GetActiveSnapshotFamilyNoLock();
        IEnumerable<BootShareProof> activeProofSource = activeFamily?.ReconciledProofs ?? _state.OnDeckProofs;
        List<BootShareProof> activeProofs = activeProofSource
            .Where(proof => activeProofIds.Contains(proof.ShareId))
            .Select(CloneProof)
            .ToList();

        return new BootStateBundle
        {
            StateId = _state.CurrentStateId,
            PreviousStateId = previousStateId,
            Kind = "current",
            CurrentRoundNumber = _state.CurrentRoundNumber,
            ProtocolVersion = GetActiveConsensusVersionNoLock(),
            ConsensusVersion = GetActiveConsensusVersionNoLock(),
            StateBundleSchemaVersion = BootProtocolVersions.GetStateBundleSchemaVersion(GetActiveConsensusVersionNoLock()),
            HttpApiVersion = BootProtocolVersions.HttpApiVersion,
            PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
            UdpRelayVersion = BootProtocolVersions.UdpRelayVersion,
            ReleaseVersion = GetLocalVersionInfoNoLock().ReleaseVersion,
            VersionInfo = GetLocalVersionInfoNoLock(),
            NetworkId = _poolConfig.BootNetworkId,
            LockedByBlockHash = _state.CurrentTipBlockHash,
            LockedByBlockHeight = _state.CurrentTipBlockHeight,
            ParentBlockHash = null,
            ParentBlockHeight = null,
            CreatedAtUtc = _state.LastRotationUtc ?? DateTime.UtcNow,
            TotalDifficulty = _state.WinnersList.Sum(x => x.Difficulty),
            ActiveSnapshotId = _state.ActiveSnapshotId,
            PaidSnapshotId = _state.LastPaidSnapshotId,
            ActiveSnapshotProofIds = _state.ActiveSnapshotProofIds.ToList(),
            PaidSnapshotProofIds = _state.LastPaidSnapshotProofIds.ToList(),
            SupportFeeEnabled = _poolConfig.GridLabsSupportFeeEnabled,
            PayoutVariant = BuildPayoutVariantNoLock(),
            ValidParentBlockHashes = GetAcceptedParentBlockHashesNoLock(),
            WinnersList = ClonePayouts(_state.WinnersList),
            ProofWinnersList = [],
            ShareProofs = activeProofs,
            WorkSetProofs = _state.OnDeckProofs.Select(CloneProof).ToList(),
            SnapshotContexts = BuildSnapshotContextsForBundleNoLock(
                activeProofs.Concat(_state.OnDeckProofs)),
            Commitment = BuildCommitmentNoLock(),
            SnapshotFamilyMember = BuildActiveSnapshotFamilyMemberNoLock()
        };
    }

    private List<BootPayoutSnapshotContext> BuildSnapshotContextsForBundleNoLock(
        IEnumerable<BootShareProof> proofs,
        IEnumerable<BootPayoutSnapshotContext>? additionalContexts = null)
    {
        var requiredSnapshotIds = proofs
            .Select(proof => proof.PayoutSnapshotId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(_state.ActiveSnapshotId))
        {
            requiredSnapshotIds.Add(_state.ActiveSnapshotId);
        }

        if (!string.IsNullOrWhiteSpace(_state.LastPaidSnapshotId))
        {
            requiredSnapshotIds.Add(_state.LastPaidSnapshotId);
        }

        var contextsById = new Dictionary<string, BootPayoutSnapshotContext>(StringComparer.OrdinalIgnoreCase);
        foreach (BootPayoutSnapshotContext context in _state.SnapshotContexts)
        {
            if (string.IsNullOrWhiteSpace(context.SnapshotId) ||
                !requiredSnapshotIds.Contains(context.SnapshotId) ||
                contextsById.ContainsKey(context.SnapshotId))
            {
                continue;
            }

            contextsById[context.SnapshotId] = CloneSnapshotContext(context);
        }

        if (additionalContexts != null)
        {
            foreach (BootPayoutSnapshotContext context in additionalContexts)
            {
                if (string.IsNullOrWhiteSpace(context.SnapshotId))
                {
                    continue;
                }

                contextsById[context.SnapshotId] = CloneSnapshotContext(context);
            }
        }

        return contextsById.Values
            .OrderByDescending(context => context.CreatedAtUtc)
            .ThenBy(context => context.SnapshotId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private BootShareProof CreateProofNoLock(
        BootShareValidationResult validation,
        string source,
        DateTime? timestamp = null,
        string? payoutSnapshotId = null)
    {
        return new BootShareProof
        {
            ShareId = validation.ShareId,
            MinerAddress = validation.MinerAddress,
            Username = validation.Username,
            ScriptPubKeyHex = validation.ScriptPubKeyHex,
            HeaderHex = validation.HeaderHex,
            CoinbaseHex = validation.CoinbaseHex,
            MerklePath = validation.MerklePath.ToList(),
            PayoutSnapshotId = payoutSnapshotId,
            PrevBlockHash = validation.PrevBlockHash,
            Difficulty = validation.Difficulty,
            DiffString = ClientHandler.FormatDifficulty(validation.Difficulty),
            Source = source,
            Timestamp = timestamp ?? DateTime.UtcNow
        };
    }

    private BootShareProof CreatePlaceholderProofNoLock(PayoutInfo payout)
    {
        string minerAddress = string.IsNullOrWhiteSpace(payout.Address) ? _poolConfig.PoolPayoutScript : payout.Address;
        return new BootShareProof
        {
            ShareId = ComputePlaceholderShareId(minerAddress, payout.DiffString, payout.Address),
            MinerAddress = minerAddress,
            Username = string.IsNullOrWhiteSpace(payout.Username) ? minerAddress : payout.Username,
            ScriptPubKeyHex = BitcoinScript.TryAddressToScriptPubKey(minerAddress, _poolConfig.BitcoinNetwork, out var script)
                ? Convert.ToHexString(script).ToLowerInvariant()
                : string.Empty,
            Difficulty = payout.Difficulty,
            DiffString = string.IsNullOrWhiteSpace(payout.DiffString)
                ? ClientHandler.FormatDifficulty(payout.Difficulty)
                : payout.DiffString,
            Timestamp = DateTime.UtcNow,
            Source = "legacy-state"
        };
    }

    private string ComputeCandidateStateIdNoLock()
    {
        return ComputeCandidateStateId(_state.CurrentStateId, _state.OnDeckProofs);
    }

    private string BuildTestingRoundResetDescriptionNoLock()
    {
        if (!_poolConfig.TestingRoundResetEnabled)
        {
            return "Disabled. Rounds rotate only when this node accepts a valid Grid Pool block share.";
        }

        return _poolConfig.TestingRoundResetMode switch
        {
            "block_hash_low_nibble" =>
                $"Auto-rotate when a new Bitcoin block hash ends in hex 0-{Math.Max(0, _poolConfig.TestingRoundResetLowNibbleThreshold - 1):x}.",
            _ => "Disabled"
        };
    }

    private string BuildRoundTriggerModeNoLock()
    {
        return _poolConfig.TestingRoundResetEnabled
            ? "deterministic-test-trigger"
            : "gridpool-block-found";
    }

    private bool IsPlaceholderOrEmptyCurrentStateNoLock()
    {
        return (_state.WinnersList.Count == 0 && _state.OnDeckList.Count == 0 && _state.OnDeckProofs.Count == 0) ||
               (_state.WinnersList.Count == 1 &&
                _state.OnDeckList.Count == 0 &&
                _state.OnDeckProofs.Count == 0 &&
                _state.WinnersList[0].Difficulty <= 0 &&
                _state.WinnersList[0].Value == GetSharedPayoutValueSatsNoLock(1));
    }

    private bool CurrentStateHasShareProofsNoLock()
    {
        return GetCurrentStateProofCountNoLock() > 0;
    }

    private List<BootPeerStatus> CloneExternalPeersNoLock()
    {
        DateTime nowUtc = DateTime.UtcNow;
        NormalizePeerAddressBookNoLock(nowUtc);
        RefreshPeerScoresNoLock(nowUtc);
        string selfEndpoint = GetSelfEndpoint();
        return _state.Peers
            .Where(peer =>
                !IsPeerTombstonedNoLock(peer, nowUtc) &&
                (!string.IsNullOrWhiteSpace(peer.Endpoint) ||
                 IsOutboundOnlySessionPeerNoLock(peer, nowUtc)) &&
                (string.IsNullOrWhiteSpace(selfEndpoint) ||
                 !string.Equals(NormalizePeerEndpoint(peer.Endpoint), selfEndpoint, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(peer => peer.Score)
            .ThenBy(peer => string.IsNullOrWhiteSpace(peer.Endpoint) ? peer.NodeId : peer.Endpoint, StringComparer.OrdinalIgnoreCase)
            .Select(ClonePeer)
            .ToList();
    }

    private int GetCurrentStateProofCountNoLock()
    {
        if (_state.ActiveSnapshotProofIds.Count > 0)
        {
            return _state.ActiveSnapshotProofIds.Count;
        }

        return _state.ArchivedStateBundles
            .Where(bundle =>
                string.Equals(bundle.StateId, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase) &&
                bundle.ShareProofs.Count > 0)
            .Select(bundle => bundle.ShareProofs.Count)
            .DefaultIfEmpty(0)
            .Max();
    }

    private List<string> GetAcceptedParentBlockHashesNoLock()
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void addHash(string? hash)
        {
            string? canonical = NormalizeCanonicalBlockHash(hash);
            if (string.IsNullOrWhiteSpace(canonical) || !seen.Add(canonical))
            {
                return;
            }

            normalized.Add(canonical);
        }

        foreach (string hash in _state.AcceptedParentBlockHashes)
        {
            addHash(hash);
            if (normalized.Count >= MaxAcceptedParentBlockHashes)
            {
                return normalized;
            }
        }

        foreach (BootShareProof proof in _state.OnDeckProofs)
        {
            addHash(proof.PrevBlockHash);
            if (normalized.Count >= MaxAcceptedParentBlockHashes)
            {
                return normalized;
            }
        }

        addHash(_state.CurrentTipBlockHash);

        return normalized;
    }

    private void ResetAcceptedParentBlockHashesNoLock(string? primaryHash)
    {
        _state.AcceptedParentBlockHashes = [];
        RememberAcceptedParentBlockHashNoLock(primaryHash);
    }

    private void PreserveAcceptedParentContinuityAfterRotationNoLock(string? previousTipBlockHash, string? newTipBlockHash)
    {
        _state.AcceptedParentBlockHashes = [];
        RememberAcceptedParentBlockHashNoLock(previousTipBlockHash);
        RememberAcceptedParentBlockHashNoLock(newTipBlockHash);
    }

    private void SetAcceptedParentBlockHashesNoLock(IEnumerable<string> hashes, string? currentTipBlockHash)
    {
        _state.AcceptedParentBlockHashes = [];
        foreach (string hash in hashes.Reverse())
        {
            RememberAcceptedParentBlockHashNoLock(hash);
        }

        RememberAcceptedParentBlockHashNoLock(currentTipBlockHash);
    }

    private void TrimAcceptedParentBlockHashesToRoundNoLock(string? roundStartBlockHash, string? currentTipBlockHash)
    {
        string? normalizedRoundStart = NormalizeCanonicalBlockHash(roundStartBlockHash);
        string? normalizedCurrentTip = NormalizeCanonicalBlockHash(currentTipBlockHash);

        if (string.IsNullOrWhiteSpace(normalizedRoundStart))
        {
            ResetAcceptedParentBlockHashesNoLock(normalizedCurrentTip);
            return;
        }

        List<string> accepted = GetAcceptedParentBlockHashesNoLock();
        int roundStartIndex = accepted.FindIndex(existing => BitcoinHashes.AreEquivalent(existing, normalizedRoundStart));
        if (roundStartIndex < 0)
        {
            ResetAcceptedParentBlockHashesNoLock(normalizedCurrentTip ?? normalizedRoundStart);
            if (!string.IsNullOrWhiteSpace(normalizedRoundStart) &&
                !BitcoinHashes.AreEquivalent(normalizedCurrentTip, normalizedRoundStart))
            {
                RememberAcceptedParentBlockHashNoLock(normalizedRoundStart);
            }

            return;
        }

        _state.AcceptedParentBlockHashes = accepted
            .Take(roundStartIndex + 1)
            .Select(NormalizeCanonicalBlockHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Cast<string>()
            .ToList();
    }

    private void RememberAcceptedParentBlockHashNoLock(string? blockHash)
    {
        string? normalized = NormalizeCanonicalBlockHash(blockHash);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        _state.AcceptedParentBlockHashes.RemoveAll(existing => BitcoinHashes.AreEquivalent(existing, normalized));
        _state.AcceptedParentBlockHashes.Insert(0, normalized);

        while (_state.AcceptedParentBlockHashes.Count > MaxAcceptedParentBlockHashes)
        {
            _state.AcceptedParentBlockHashes.RemoveAt(_state.AcceptedParentBlockHashes.Count - 1);
        }
    }

    private bool IsAcceptedParentBlockHashNoLock(string? blockHash)
    {
        string? normalized = NormalizeCanonicalBlockHash(blockHash);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (BitcoinHashes.AreEquivalent(_state.CurrentTipBlockHash, normalized))
        {
            return true;
        }

        foreach (string existing in _state.AcceptedParentBlockHashes)
        {
            if (BitcoinHashes.AreEquivalent(existing, normalized))
            {
                return true;
            }
        }

        foreach (BootShareProof proof in _state.OnDeckProofs)
        {
            if (BitcoinHashes.AreEquivalent(proof.PrevBlockHash, normalized))
            {
                return true;
            }
        }

        return false;
    }

    private List<BootShareProof> ValidateImportedProofs(
        IEnumerable<BootShareProof> shareProofs,
        IReadOnlyList<PayoutInfo> expectedWinners,
        IReadOnlyCollection<string> expectedPrevBlockHashes,
        string source,
        IReadOnlyCollection<BootPayoutSnapshotContext>? snapshotContexts = null)
    {
        var proofs = shareProofs
            .Select(CloneProof)
            .OrderByDescending(x => x.Difficulty)
            .ThenBy(x => x.ShareId, StringComparer.Ordinal)
            .ToList();

        var validatedProofs = new List<BootShareProof>(proofs.Count);
        foreach (var proof in proofs)
        {
            SnapshotValidationResult snapshotValidation = snapshotContexts is { Count: > 0 }
                ? ValidateProofAgainstKnownSnapshots(proof, expectedWinners, snapshotContexts, expectedPrevBlockHashes)
                : new SnapshotValidationResult(
                    _shareVerifier.ValidateShare(proof, expectedWinners, expectedPrevBlockHashes),
                    proof.PayoutSnapshotId ?? string.Empty);
            BootShareValidationResult validation = snapshotValidation.Validation;
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(validation.RejectionReason ?? "Imported share proof is invalid.");
            }

            validatedProofs.Add(CreateProofNoLock(
                validation,
                source,
                proof.Timestamp,
                string.IsNullOrWhiteSpace(snapshotValidation.SnapshotId) ? proof.PayoutSnapshotId : snapshotValidation.SnapshotId));
        }

        return validatedProofs
            .OrderByDescending(x => x.Difficulty)
            .ThenBy(x => x.ShareId, StringComparer.Ordinal)
            .ToList();
    }

    private List<PayoutInfo> BuildPayoutsFromProofs(IEnumerable<BootShareProof> proofs)
    {
        return BuildPayoutsFromProofs(proofs, includeSupportFee: _poolConfig.GridLabsSupportFeeEnabled);
    }

    private List<PayoutInfo> BuildFeeFreePayoutsFromProofs(IEnumerable<BootShareProof> proofs)
    {
        return BuildPayoutsFromProofs(proofs, includeSupportFee: false);
    }

    private List<PayoutInfo> BuildPayoutsFromProofs(IEnumerable<BootShareProof> proofs, bool includeSupportFee)
    {
        var list = proofs
            .OrderByDescending(x => x.Difficulty)
            .ThenBy(x => x.ShareId, StringComparer.Ordinal)
            .Take(includeSupportFee ? _poolConfig.SharedWinnerSlotCount : _poolConfig.SnapshotProofSlotCount)
            .ToList();
        var payouts = new List<PayoutInfo>();
        ulong reward = GetSharedPayoutValueSatsNoLock(list.Count);
        if (includeSupportFee)
        {
            string supportAddress = GetGridLabsSupportAddress(_poolConfig.BitcoinNetwork);
            payouts.Add(new PayoutInfo
            {
                Value = reward,
                Address = supportAddress,
                Username = "Grid Labs support",
                Difficulty = 0,
                DiffString = "support"
            });
        }

        if (list.Count == 0)
        {
            return payouts;
        }

        payouts.AddRange(list.Select(proof => new PayoutInfo
        {
            Value = reward,
            Address = proof.MinerAddress,
            Username = string.IsNullOrWhiteSpace(proof.Username) ? proof.MinerAddress : proof.Username,
            Difficulty = proof.Difficulty,
            DiffString = string.IsNullOrWhiteSpace(proof.DiffString)
                ? ClientHandler.FormatDifficulty(proof.Difficulty)
                : proof.DiffString
        }));
        return payouts;
    }

    private List<PayoutInfo> BuildCoinbaseOutputsNoLock(IEnumerable<PayoutInfo> payouts)
    {
        if (_poolConfig.CoinbaseUncondensedOutputsEnabled)
        {
            return payouts.Select(payout =>
            {
                string normalizedAddress = BitcoinScript.NormalizeAddress(payout.Address);
                _ = BitcoinScript.AddressToScriptPubKeyHex(normalizedAddress, _poolConfig.BitcoinNetwork);
                return new PayoutInfo
                {
                    Value = payout.Value,
                    Address = normalizedAddress,
                    Username = string.IsNullOrWhiteSpace(payout.Username) ? normalizedAddress : payout.Username,
                    Difficulty = payout.Difficulty,
                    DiffString = payout.DiffString
                };
            }).ToList();
        }

        var compressed = new List<PayoutInfo>();
        var indexByScript = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var payout in payouts)
        {
            string normalizedAddress = BitcoinScript.NormalizeAddress(payout.Address);
            string scriptPubKeyHex = BitcoinScript.AddressToScriptPubKeyHex(normalizedAddress, _poolConfig.BitcoinNetwork);
            if (indexByScript.TryGetValue(scriptPubKeyHex, out int existingIndex))
            {
                compressed[existingIndex].Value += payout.Value;
                continue;
            }

            indexByScript[scriptPubKeyHex] = compressed.Count;
            compressed.Add(new PayoutInfo
            {
                Value = payout.Value,
                Address = normalizedAddress,
                Username = string.IsNullOrWhiteSpace(payout.Username) ? normalizedAddress : payout.Username,
                Difficulty = payout.Difficulty,
                DiffString = payout.DiffString
            });
        }

        return ClonePayouts(compressed);
    }

    private bool WinnersMatch(IReadOnlyList<PayoutInfo> expected, IReadOnlyList<PayoutInfo> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i].Value != actual[i].Value)
            {
                return false;
            }

            string expectedScript = BitcoinScript.AddressToScriptPubKeyHex(expected[i].Address, _poolConfig.BitcoinNetwork);
            string actualScript = BitcoinScript.AddressToScriptPubKeyHex(actual[i].Address, _poolConfig.BitcoinNetwork);
            if (!string.Equals(expectedScript, actualScript, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Math.Abs(expected[i].Difficulty - actual[i].Difficulty) > 0.0000001)
            {
                return false;
            }
        }

        return true;
    }

    private string ComputeStateIdFromPayoutsNoLock(IEnumerable<PayoutInfo> payouts, string? blockHash)
    {
        var pseudoProofs = payouts.Select(x => new BootShareProof
        {
            MinerAddress = x.Address,
            ScriptPubKeyHex = BitcoinScript.TryAddressToScriptPubKey(x.Address, _poolConfig.BitcoinNetwork, out var script)
                ? Convert.ToHexString(script).ToLowerInvariant()
                : string.Empty,
            Difficulty = x.Difficulty
        });

        return ComputeStateIdNoLock(pseudoProofs, blockHash);
    }

    private string ComputeCandidateStateId(string? currentStateId, IEnumerable<BootShareProof> shares)
    {
        var builder = new StringBuilder();
        builder.Append("boot-protocol-candidate-state").Append('\n');
        builder.Append(GetActiveConsensusVersionNoLock()).Append('\n');
        builder.Append(_poolConfig.BootNetworkId).Append('\n');
        builder.Append(currentStateId ?? string.Empty).Append('\n');
        builder.Append(BuildPayoutVariantNoLock()).Append('\n');

        int index = 0;
        foreach (var share in shares
                     .OrderByDescending(x => x.Difficulty)
                     .ThenBy(x => x.ShareId, StringComparer.Ordinal))
        {
            builder.Append(index++).Append('|');
            builder.Append(share.ScriptPubKeyHex).Append('|');
            builder.Append(share.Difficulty.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            builder.Append(share.ShareId).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string ComputeStateIdNoLock(IEnumerable<BootShareProof> shares, string? blockHash)
    {
        var builder = new StringBuilder();
        builder.Append("boot-protocol-state").Append('\n');
        builder.Append(GetActiveConsensusVersionNoLock()).Append('\n');
        builder.Append(_poolConfig.BootNetworkId).Append('\n');
        builder.Append(NormalizeCanonicalBlockHash(blockHash) ?? string.Empty).Append('\n');
        builder.Append(BuildPayoutVariantNoLock()).Append('\n');

        int index = 0;
        foreach (var share in shares
                     .OrderByDescending(x => x.Difficulty)
                     .ThenBy(x => x.ShareId, StringComparer.Ordinal))
        {
            builder.Append(index++).Append('|');
            builder.Append(share.ScriptPubKeyHex).Append('|');
            builder.Append(share.Difficulty.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            builder.Append(share.ShareId).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeCanonicalBlockHash(string? blockHash)
    {
        string normalized = BitcoinHashes.NormalizeHex(blockHash);
        if (normalized.Length != 64)
        {
            return null;
        }

        for (int index = 0; index < normalized.Length; index++)
        {
            if (!Uri.IsHexDigit(normalized[index]))
            {
                return null;
            }
        }

        return BitcoinHashes.NormalizeLikelyDisplayHashHex(normalized);
    }

    private static List<string> NormalizeAcceptedParentBlockHashes(IEnumerable<string?> hashes)
    {
        var normalized = new List<string>();
        foreach (string? hash in hashes)
        {
            string? canonical = NormalizeCanonicalBlockHash(hash);
            if (string.IsNullOrWhiteSpace(canonical) ||
                normalized.Any(existing => BitcoinHashes.AreEquivalent(existing, canonical)))
            {
                continue;
            }

            normalized.Add(canonical);
        }

        return normalized;
    }

    private static List<string> MergeAcceptedParentBlockHashes(
        IEnumerable<string> primary,
        IEnumerable<string> secondary)
    {
        return NormalizeAcceptedParentBlockHashes(primary.Concat(secondary));
    }

    private bool ShouldTriggerTestingRoundResetNoLock(string normalizedBlockHash)
    {
        if (!_poolConfig.TestingRoundResetEnabled ||
            string.IsNullOrWhiteSpace(normalizedBlockHash) ||
            string.Equals(_poolConfig.TestingRoundResetMode, "none", StringComparison.OrdinalIgnoreCase) ||
            _state.OnDeckProofs.Count == 0)
        {
            return false;
        }

        if (BitcoinHashes.AreEquivalent(_state.LastTestingTriggerBlockHash, normalizedBlockHash))
        {
            return false;
        }

        if (string.Equals(_poolConfig.TestingRoundResetMode, "block_hash_low_nibble", StringComparison.OrdinalIgnoreCase))
        {
            int nibble = Convert.ToInt32(normalizedBlockHash[^1].ToString(), 16);
            return nibble < _poolConfig.TestingRoundResetLowNibbleThreshold;
        }

        return false;
    }

    private static bool IsStaleTipObservationNoLock(
        string? observedBlockHash,
        long? observedBlockHeight,
        string? currentTipBlockHash,
        long? currentTipBlockHeight)
    {
        if (string.IsNullOrWhiteSpace(observedBlockHash) ||
            !observedBlockHeight.HasValue ||
            !currentTipBlockHeight.HasValue ||
            observedBlockHeight.Value >= currentTipBlockHeight.Value)
        {
            return false;
        }

        return !BitcoinHashes.AreEquivalent(observedBlockHash, currentTipBlockHash);
    }

    private async Task NotifyWinnersListChangedAsync(string reason)
    {
        Func<string, Task>? handlers = WinnersListChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Func<string, Task> handler in handlers.GetInvocationList().Cast<Func<string, Task>>())
        {
            try
            {
                await handler(reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A WinnersListChanged handler failed for reason {Reason}.", reason);
            }
        }
    }

    private async Task NotifyWorkTemplatesInvalidatedAsync(string reason)
    {
        Func<string, Task>? handlers = WorkTemplatesInvalidated;
        if (handlers == null)
        {
            return;
        }

        foreach (Func<string, Task> handler in handlers.GetInvocationList().Cast<Func<string, Task>>())
        {
            try
            {
                await handler(reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A WorkTemplatesInvalidated handler failed for reason {Reason}.", reason);
            }
        }
    }

    private static string ComputePlaceholderShareId(string minerAddress, string diffString, string payoutAddress)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{minerAddress}|{diffString}|{payoutAddress}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void UpsertArchivedBundleNoLock(BootStateBundle bundle)
    {
        _state.ArchivedStateBundles.RemoveAll(existing =>
            string.Equals(existing.StateId, bundle.StateId, StringComparison.OrdinalIgnoreCase));
        _state.ArchivedStateBundles.Insert(0, CloneBundle(bundle));

        while (_state.ArchivedStateBundles.Count > _poolConfig.MaxStateBundleHistory)
        {
            _state.ArchivedStateBundles.RemoveAt(_state.ArchivedStateBundles.Count - 1);
        }
    }

    private void NormalizeArchivedBundlesNoLock()
    {
        for (int index = 0; index < _state.ArchivedStateBundles.Count; index++)
        {
            BootStateBundle bundle = _state.ArchivedStateBundles[index];
            StampBundleVersionNoLock(bundle);
            bundle.PreviousStateId = string.IsNullOrWhiteSpace(bundle.PreviousStateId) ? null : bundle.PreviousStateId;
            bundle.LockedByBlockHash = NormalizeCanonicalBlockHash(bundle.LockedByBlockHash);
            bundle.ParentBlockHash = NormalizeCanonicalBlockHash(bundle.ParentBlockHash);
            bundle.ValidParentBlockHashes = NormalizeAcceptedParentBlockHashes(
                bundle.ValidParentBlockHashes
                    .Append(bundle.ParentBlockHash)
                    .Append(bundle.LockedByBlockHash));
            bundle.WinnersList = ClonePayouts(bundle.WinnersList);
            bundle.ProofWinnersList = ClonePayouts(bundle.ProofWinnersList);
            bundle.ShareProofs = bundle.ShareProofs
                .Select(CloneProof)
                .OrderByDescending(x => x.Difficulty)
                .ThenBy(x => x.ShareId, StringComparer.Ordinal)
                .ToList();

            if (bundle.ShareProofs.Count > 0 && bundle.ProofWinnersList.Count == 0)
            {
                List<PayoutInfo>? inferredProofWinners = index + 1 < _state.ArchivedStateBundles.Count
                    ? ClonePayouts(_state.ArchivedStateBundles[index + 1].WinnersList)
                    : null;
                if (inferredProofWinners is { Count: > 0 })
                {
                    bundle.ProofWinnersList = inferredProofWinners;
                }
            }
        }

        int historyLimit = Math.Max(1, _poolConfig.MaxStateBundleHistory);
        if (_state.ArchivedStateBundles.Count > historyLimit)
        {
            _state.ArchivedStateBundles.RemoveRange(
                historyLimit,
                _state.ArchivedStateBundles.Count - historyLimit);
        }
    }

    private void EnsureRoundMetadataNoLock()
    {
        if (_state.ArchivedStateBundles.Count == 0)
        {
            _state.CurrentRoundNumber = Math.Max(0, _state.CurrentRoundNumber);
            return;
        }

        var orderedByTime = _state.ArchivedStateBundles
            .OrderBy(bundle => bundle.CreatedAtUtc == default ? DateTime.MaxValue : bundle.CreatedAtUtc)
            .ThenBy(bundle => bundle.StateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string genesisStateId = ComputeStateIdFromPayoutsNoLock(BuildGenesisWinnersListNoLock(), null);
        string? previousStateId = genesisStateId;
        int nextCurrentRoundNumber = 1;

        foreach (BootStateBundle bundle in orderedByTime)
        {
            if (string.IsNullOrWhiteSpace(bundle.PreviousStateId))
            {
                bundle.PreviousStateId = previousStateId;
            }

            if (bundle.CurrentRoundNumber <= 0)
            {
                bundle.CurrentRoundNumber = nextCurrentRoundNumber;
            }

            previousStateId = bundle.StateId;
            nextCurrentRoundNumber = Math.Max(nextCurrentRoundNumber + 1, bundle.CurrentRoundNumber + 1);
        }

        if (_state.CurrentRoundNumber <= 0)
        {
            BootStateBundle? currentBundle = _state.ArchivedStateBundles.FirstOrDefault(bundle =>
                string.Equals(bundle.StateId, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase));
            _state.CurrentRoundNumber = currentBundle?.CurrentRoundNumber ?? Math.Max(0, nextCurrentRoundNumber - 1);
        }
    }

    private bool UpdateKnownBlockHeightNoLock(string? blockHash, long? blockHeight)
    {
        if (string.IsNullOrWhiteSpace(blockHash) || !blockHeight.HasValue || blockHeight <= 0)
        {
            return false;
        }

        bool changed = false;
        if (BitcoinHashes.AreEquivalent(blockHash, _state.CurrentTipBlockHash) &&
            _state.CurrentTipBlockHeight != blockHeight)
        {
            _state.CurrentTipBlockHeight = blockHeight;
            changed = true;
        }

        if (BitcoinHashes.AreEquivalent(blockHash, _state.LastTestingTriggerBlockHash) &&
            _state.LastTestingTriggerBlockHeight != blockHeight)
        {
            _state.LastTestingTriggerBlockHeight = blockHeight;
            changed = true;
        }

        foreach (BootStateBundle bundle in _state.ArchivedStateBundles)
        {
            if (BitcoinHashes.AreEquivalent(blockHash, bundle.LockedByBlockHash) &&
                bundle.LockedByBlockHeight != blockHeight)
            {
                bundle.LockedByBlockHeight = blockHeight;
                changed = true;
            }

            if (BitcoinHashes.AreEquivalent(blockHash, bundle.ParentBlockHash) &&
                bundle.ParentBlockHeight != blockHeight)
            {
                bundle.ParentBlockHeight = blockHeight;
                changed = true;
            }
        }

        return changed;
    }

    private bool UpsertPeerNoLock(
        string endpoint,
        string status,
        double? latencyMs,
        DateTime? lastSeenUtc,
        bool persistStatusOnly,
        bool allowSuppressed,
        string source = "",
        bool isConfiguredSeed = false,
        bool allowPrivate = false)
    {
        if (!TryNormalizePeerEndpoint(endpoint, allowPrivate, out string normalized, out _))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(GetSelfEndpoint()) &&
            ArePeerEndpointHostsEquivalent(normalized, GetSelfEndpoint()))
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (allowSuppressed)
        {
            _suppressedPeerEndpoints.Remove(normalized);
        }
        else if (IsPeerSuppressedNoLock(normalized, DateTime.UtcNow))
        {
            return false;
        }

        var existing = _state.Peers.FirstOrDefault(x => string.Equals(x.Endpoint, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            _state.Peers.Add(new BootPeerStatus
            {
                Endpoint = normalized,
                Status = status,
                Source = source,
                IsConfiguredSeed = isConfiguredSeed,
                DiscoveredUtc = nowUtc,
                LatencyMs = latencyMs,
                LastSeenUtc = lastSeenUtc
            });
            TrimPeerAddressBookNoLock(nowUtc);
            return true;
        }

        if (IsPeerTombstonedNoLock(existing, nowUtc))
        {
            return false;
        }

        bool changed = false;
        bool discoveryRefresh = (string.Equals(source, "gossip", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(source, "header", StringComparison.OrdinalIgnoreCase)) &&
                                string.Equals(status, "discovered", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(status) &&
            !string.Equals(existing.Status, status, StringComparison.Ordinal) &&
            !(discoveryRefresh && IsPeerFailureStatus(existing.Status)))
        {
            existing.Status = status;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(existing.Source))
        {
            existing.Source = source;
            changed = true;
        }

        if (isConfiguredSeed && !existing.IsConfiguredSeed)
        {
            existing.IsConfiguredSeed = true;
            changed = true;
        }

        if (!existing.DiscoveredUtc.HasValue)
        {
            existing.DiscoveredUtc = nowUtc;
            changed = true;
        }

        if (latencyMs.HasValue && existing.LatencyMs != latencyMs)
        {
            existing.LatencyMs = latencyMs;
            changed = true;
        }

        if (lastSeenUtc.HasValue && existing.LastSeenUtc != lastSeenUtc)
        {
            existing.LastSeenUtc = lastSeenUtc;
            changed = true;
        }

        return changed && !persistStatusOnly ? true : changed;
    }

    private bool UpsertPeerSessionNoLock(
        string endpoint,
        string nodeId,
        string status,
        DateTime? lastSeenUtc,
        bool sessionConnected,
        double? latencyMs = null,
        bool allowSuppressed = true)
    {
        string normalizedEndpoint = string.Empty;
        bool hasEndpoint = TryNormalizePeerEndpoint(endpoint, allowPrivate: true, out normalizedEndpoint, out _);
        string normalizedNodeId = NormalizePeerNodeId(nodeId);
        if (!hasEndpoint && string.IsNullOrWhiteSpace(normalizedNodeId))
        {
            return false;
        }

        if (hasEndpoint &&
            !string.IsNullOrWhiteSpace(GetSelfEndpoint()) &&
            string.Equals(normalizedEndpoint, GetSelfEndpoint(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (hasEndpoint)
        {
            if (allowSuppressed)
            {
                _suppressedPeerEndpoints.Remove(normalizedEndpoint);
            }
            else if (IsPeerSuppressedNoLock(normalizedEndpoint, nowUtc))
            {
                return false;
            }
        }

        BootPeerStatus? existing = FindPeerByEndpointOrNodeNoLock(hasEndpoint ? normalizedEndpoint : string.Empty, normalizedNodeId);
        if (existing == null)
        {
            existing = new BootPeerStatus
            {
                Endpoint = hasEndpoint ? normalizedEndpoint : string.Empty,
                NodeId = normalizedNodeId,
                Status = status,
                Source = "session",
                DiscoveredUtc = nowUtc,
                LastSeenUtc = lastSeenUtc,
                LastSessionUtc = lastSeenUtc,
                LastSuccessUtc = sessionConnected ? lastSeenUtc : null,
                LatencyMs = latencyMs,
                ConnectionMode = hasEndpoint ? "public" : "outbound-only",
                SessionConnected = sessionConnected,
                Capabilities = BuildPeerSessionCapabilitiesNoLock(hasEndpoint)
            };
            _state.Peers.Add(existing);
            TrimPeerAddressBookNoLock(nowUtc);
            return true;
        }

        if (IsPeerTombstonedNoLock(existing, nowUtc))
        {
            return false;
        }

        bool changed = false;
        if (hasEndpoint && !string.Equals(existing.Endpoint, normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            existing.Endpoint = normalizedEndpoint;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedNodeId) && !string.Equals(existing.NodeId, normalizedNodeId, StringComparison.Ordinal))
        {
            existing.NodeId = normalizedNodeId;
            changed = true;
        }

        if (!string.Equals(existing.Status, status, StringComparison.Ordinal))
        {
            existing.Status = status;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(existing.Source))
        {
            existing.Source = "session";
            changed = true;
        }

        if (!existing.DiscoveredUtc.HasValue)
        {
            existing.DiscoveredUtc = nowUtc;
            changed = true;
        }

        if (lastSeenUtc.HasValue && existing.LastSeenUtc != lastSeenUtc)
        {
            existing.LastSeenUtc = lastSeenUtc;
            changed = true;
        }

        if (latencyMs.HasValue && existing.LatencyMs != latencyMs)
        {
            existing.LatencyMs = latencyMs;
            changed = true;
        }

        string connectionMode = hasEndpoint ? "public" : "outbound-only";
        if (!string.Equals(existing.ConnectionMode, connectionMode, StringComparison.Ordinal))
        {
            existing.ConnectionMode = connectionMode;
            changed = true;
        }

        if (existing.SessionConnected != sessionConnected)
        {
            existing.SessionConnected = sessionConnected;
            changed = true;
        }

        List<string> capabilities = BuildPeerSessionCapabilitiesNoLock(hasEndpoint);
        if (!existing.Capabilities.SequenceEqual(capabilities, StringComparer.Ordinal))
        {
            existing.Capabilities = capabilities;
            changed = true;
        }

        return changed;
    }

    private void NormalizePeerAddressBookNoLock(DateTime nowUtc)
    {
        string selfEndpoint = GetSelfEndpoint();
        TimeSpan sessionIdleTimeout = TimeSpan.FromSeconds(Math.Clamp(_poolConfig.PeerSessionIdleTimeoutSeconds, 30, 3600));
        _state.Peers = _state.Peers
            .Select(peer =>
            {
                DateTime? lastSessionActivityUtc = peer.LastSessionUtc ?? peer.LastSeenUtc;
                if (peer.SessionConnected &&
                    (!lastSessionActivityUtc.HasValue || nowUtc - lastSessionActivityUtc.Value > sessionIdleTimeout))
                {
                    peer.SessionConnected = false;
                    if (string.Equals(peer.Status, "session-connected", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(peer.Status, "session-relayed", StringComparison.OrdinalIgnoreCase))
                    {
                        peer.Status = "session-stale";
                    }
                }

                if (string.IsNullOrWhiteSpace(peer.Endpoint) && !string.IsNullOrWhiteSpace(peer.NodeId))
                {
                    if (!peer.SessionConnected &&
                        (!lastSessionActivityUtc.HasValue || nowUtc - lastSessionActivityUtc.Value > sessionIdleTimeout))
                    {
                        return null;
                    }

                    peer.NodeId = NormalizePeerNodeId(peer.NodeId);
                    peer.DiscoveredUtc ??= peer.LastSeenUtc ?? peer.LastFailureUtc ?? nowUtc;
                    peer.Source = string.IsNullOrWhiteSpace(peer.Source) ? "session" : peer.Source;
                    peer.ConnectionMode = string.IsNullOrWhiteSpace(peer.ConnectionMode) || string.Equals(peer.ConnectionMode, "unknown", StringComparison.OrdinalIgnoreCase)
                        ? "outbound-only"
                        : peer.ConnectionMode;
                    if (peer.Capabilities.Count == 0)
                    {
                        peer.Capabilities = BuildPeerSessionCapabilitiesNoLock(hasEndpoint: false);
                    }

                    return peer;
                }

                if (!TryNormalizePeerEndpoint(peer.Endpoint, allowPrivate: true, out string normalized, out _))
                {
                    return null;
                }

                peer.Endpoint = normalized;
                peer.DiscoveredUtc ??= peer.LastSeenUtc ?? peer.LastFailureUtc ?? nowUtc;
                peer.Source = string.IsNullOrWhiteSpace(peer.Source) ? "legacy" : peer.Source;
                peer.ConnectionMode = string.IsNullOrWhiteSpace(peer.ConnectionMode) || string.Equals(peer.ConnectionMode, "unknown", StringComparison.OrdinalIgnoreCase)
                    ? "public"
                    : peer.ConnectionMode;
                if (peer.Capabilities.Count == 0 && peer.LastSessionUtc.HasValue)
                {
                    peer.Capabilities = BuildPeerSessionCapabilitiesNoLock(hasEndpoint: true);
                }
                if (peer.TombstonedUntilUtc.HasValue && peer.TombstonedUntilUtc.Value <= nowUtc)
                {
                    peer.TombstonedUntilUtc = null;
                    if (string.Equals(peer.Status, "tombstoned", StringComparison.OrdinalIgnoreCase))
                    {
                        peer.Status = "discovered";
                    }
                }

                if (peer.SuppressedUntilUtc.HasValue && peer.SuppressedUntilUtc.Value <= nowUtc)
                {
                    peer.SuppressedUntilUtc = null;
                }

                if (!string.IsNullOrWhiteSpace(selfEndpoint) &&
                    ArePeerEndpointHostsEquivalent(normalized, selfEndpoint))
                {
                    return null;
                }

                return peer;
            })
            .OfType<BootPeerStatus>()
            .GroupBy(GetPeerIdentityKeyNoLock, StringComparer.OrdinalIgnoreCase)
            .Select(MergePeerIdentityGroupNoLock)
            .GroupBy(GetPeerHostIdentityKeyNoLock, StringComparer.OrdinalIgnoreCase)
            .Select(MergePeerIdentityGroupNoLock)
            .ToList();

        TrimPeerAddressBookNoLock(nowUtc);
    }

    private static BootPeerStatus MergePeerIdentityGroupNoLock(IEnumerable<BootPeerStatus> peers)
    {
        List<BootPeerStatus> group = peers.ToList();
        BootPeerStatus selected = group
            .OrderByDescending(peer => peer.SessionConnected)
            .ThenByDescending(peer => peer.LastSuccessUtc ?? peer.LastSeenUtc ?? peer.LastSessionUtc ?? DateTime.MinValue)
            .ThenByDescending(peer => peer.IsConfiguredSeed)
            .First();

        selected.IsConfiguredSeed = group.Any(peer => peer.IsConfiguredSeed);
        selected.DiscoveredUtc = MinDate(group.Select(peer => peer.DiscoveredUtc));
        selected.LastAttemptUtc = MaxDate(group.Select(peer => peer.LastAttemptUtc));
        selected.LastSuccessUtc = MaxDate(group.Select(peer => peer.LastSuccessUtc));
        selected.LastSessionUtc = MaxDate(group.Select(peer => peer.LastSessionUtc));
        selected.LastSeenUtc = MaxDate(group.Select(peer => peer.LastSeenUtc));
        selected.LastFailureUtc = MaxDate(group.Select(peer => peer.LastFailureUtc));
        selected.SessionConnected = group.Any(peer => peer.SessionConnected);
        selected.Capabilities = group.SelectMany(peer => peer.Capabilities).Distinct(StringComparer.Ordinal).ToList();
        selected.FailureCount = group.Max(peer => peer.FailureCount);
        selected.RelaySuccessCount = group.Max(peer => peer.RelaySuccessCount);
        selected.RelayFailureCount = group.Max(peer => peer.RelayFailureCount);
        selected.SessionSuccessCount = group.Max(peer => peer.SessionSuccessCount);
        selected.SessionFailureCount = group.Max(peer => peer.SessionFailureCount);
        selected.UdpRelaySuccessCount = group.Max(peer => peer.UdpRelaySuccessCount);
        selected.UdpRelayFailureCount = group.Max(peer => peer.UdpRelayFailureCount);
        return selected;
    }

    private static DateTime? MaxDate(IEnumerable<DateTime?> values)
    {
        DateTime[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static DateTime? MinDate(IEnumerable<DateTime?> values)
    {
        DateTime[] present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }

    private void TrimPeerAddressBookNoLock(DateTime nowUtc)
    {
        RefreshPeerScoresNoLock(nowUtc);
        int maxEntries = GetPeerAddressBookMaxEntries();
        if (_state.Peers.Count <= maxEntries)
        {
            return;
        }

        int removeCount = _state.Peers.Count - maxEntries;
        HashSet<string> removePeerKeys = _state.Peers
            .Where(peer => !peer.IsConfiguredSeed && !IsPeerTombstonedNoLock(peer, nowUtc))
            .OrderBy(peer => peer.Score)
            .ThenBy(peer => peer.LastSuccessUtc ?? peer.LastSeenUtc ?? peer.DiscoveredUtc ?? DateTime.MinValue)
            .Take(removeCount)
            .Select(GetPeerIdentityKeyNoLock)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removePeerKeys.Count == 0)
        {
            return;
        }

        _state.Peers = _state.Peers
            .Where(peer => !removePeerKeys.Contains(GetPeerIdentityKeyNoLock(peer)))
            .ToList();
    }

    private void RefreshPeerScoresNoLock(DateTime nowUtc)
    {
        foreach (BootPeerStatus peer in _state.Peers)
        {
            peer.Score = ComputePeerScoreNoLock(peer, nowUtc);
        }
    }

    private double ComputePeerScoreNoLock(BootPeerStatus peer, DateTime nowUtc)
    {
        if (IsPeerTombstonedNoLock(peer, nowUtc) || IsPeerSuppressedNoLock(peer.Endpoint, nowUtc))
        {
            return -100000;
        }

        double score = 0;
        if (string.Equals(peer.CompatibilityStatus, "incompatible", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(peer.Status, "version-mismatch", StringComparison.OrdinalIgnoreCase))
        {
            score -= 250;
        }

        if (peer.IsConfiguredSeed)
        {
            score += 20;
        }

        if (peer.LastSuccessUtc.HasValue)
        {
            double successAgeMinutes = Math.Max(0, (nowUtc - peer.LastSuccessUtc.Value).TotalMinutes);
            score += successAgeMinutes <= 10 ? 60 : successAgeMinutes <= 60 ? 35 : successAgeMinutes <= 360 ? 15 : 5;
        }
        else if (peer.LastSeenUtc.HasValue)
        {
            score += 15;
        }

        if (peer.LastSessionUtc.HasValue)
        {
            double sessionAgeMinutes = Math.Max(0, (nowUtc - peer.LastSessionUtc.Value).TotalMinutes);
            score += sessionAgeMinutes <= 10 ? 18 : sessionAgeMinutes <= 60 ? 8 : 2;
        }

        if (!string.IsNullOrWhiteSpace(peer.LastCurrentStateId) &&
            string.Equals(peer.LastCurrentStateId, _state.CurrentStateId, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
        }

        if (!string.IsNullOrWhiteSpace(peer.LastCandidateStateId) &&
            string.Equals(peer.LastCandidateStateId, _state.CandidateStateId, StringComparison.OrdinalIgnoreCase))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(peer.LastTipBlockHash) &&
            BitcoinHashes.AreEquivalent(peer.LastTipBlockHash, _state.CurrentTipBlockHash))
        {
            score += 10;
        }

        if (peer.LatencyMs.HasValue)
        {
            score += peer.LatencyMs.Value <= 100 ? 12 :
                peer.LatencyMs.Value <= 500 ? 8 :
                peer.LatencyMs.Value <= 1500 ? 3 : -5;
        }

        score += Math.Min(30, peer.RelaySuccessCount * 2);
        score -= Math.Min(60, peer.RelayFailureCount * 3);
        score += Math.Min(24, peer.SessionSuccessCount);
        score -= Math.Min(80, peer.SessionFailureCount * 4);
        score += Math.Min(20, peer.UdpRelaySuccessCount);
        score -= Math.Min(60, peer.UdpRelayFailureCount * 3);
        score -= Math.Min(80, peer.FailureCount * 10);
        if (IsPeerFailureStatus(peer.Status))
        {
            score -= 20;
        }

        return score;
    }

    private bool IsPeerEligibleForAttemptNoLock(BootPeerStatus peer, DateTime nowUtc, string? sourceEndpoint)
    {
        string endpoint = NormalizePeerEndpoint(peer.Endpoint);
        if (string.IsNullOrWhiteSpace(endpoint) ||
            (!string.IsNullOrWhiteSpace(sourceEndpoint) && string.Equals(endpoint, sourceEndpoint, StringComparison.OrdinalIgnoreCase)) ||
            IsPeerTombstonedNoLock(peer, nowUtc) ||
            IsPeerSuppressedNoLock(endpoint, nowUtc) ||
            IsPeerInFailureBackoffNoLock(peer, nowUtc))
        {
            return false;
        }

        return TryNormalizePeerEndpoint(endpoint, allowPrivate: true, out _, out _);
    }

    private bool IsPeerAdvertisableNoLock(BootPeerStatus peer, DateTime nowUtc)
    {
        if (!IsPeerEligibleForAttemptNoLock(peer, nowUtc, sourceEndpoint: null))
        {
            return false;
        }

        if (!AllowPrivatePeerAdvertisements() && IsPrivatePeerEndpoint(peer.Endpoint))
        {
            return false;
        }

        return !IsPeerFailureStatus(peer.Status) || peer.LastSuccessUtc.HasValue;
    }

    private static bool IsOutboundOnlySessionPeerNoLock(BootPeerStatus peer, DateTime nowUtc)
    {
        return string.IsNullOrWhiteSpace(peer.Endpoint) &&
               !string.IsNullOrWhiteSpace(peer.NodeId) &&
               (peer.SessionConnected ||
                peer.LastSessionUtc.HasValue && (nowUtc - peer.LastSessionUtc.Value) <= TimeSpan.FromMinutes(30) ||
                peer.LastSeenUtc.HasValue && (nowUtc - peer.LastSeenUtc.Value) <= TimeSpan.FromMinutes(30));
    }

    private bool IsPeerInFailureBackoffNoLock(BootPeerStatus peer, DateTime nowUtc)
    {
        if (!peer.LastFailureUtc.HasValue || peer.FailureCount <= 0)
        {
            return false;
        }

        int minSeconds = GetPeerFailureBackoffMinSeconds();
        int maxSeconds = GetPeerFailureBackoffMaxSeconds();
        int exponent = Math.Min(10, Math.Max(0, peer.FailureCount - 1));
        double backoffSeconds = Math.Min(maxSeconds, minSeconds * Math.Pow(2, exponent));
        return peer.LastFailureUtc.Value.AddSeconds(backoffSeconds) > nowUtc;
    }

    private void SuppressPeerNoLock(string endpoint, DateTime untilUtc)
    {
        string normalized = NormalizePeerEndpoint(endpoint);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            _suppressedPeerEndpoints[normalized] = untilUtc;
            BootPeerStatus? peer = FindPeerNoLock(normalized);
            if (peer != null)
            {
                peer.SuppressedUntilUtc = untilUtc;
            }
        }
    }

    private bool IsPeerSuppressedNoLock(string endpoint, DateTime nowUtc)
    {
        string normalized = NormalizePeerEndpoint(endpoint);
        if (!_suppressedPeerEndpoints.TryGetValue(normalized, out DateTime untilUtc))
        {
            BootPeerStatus? peer = FindPeerNoLock(normalized);
            if (peer?.SuppressedUntilUtc == null)
            {
                return false;
            }

            untilUtc = peer.SuppressedUntilUtc.Value;
        }

        if (untilUtc <= nowUtc)
        {
            _suppressedPeerEndpoints.Remove(normalized);
            BootPeerStatus? peer = FindPeerNoLock(normalized);
            if (peer != null)
            {
                peer.SuppressedUntilUtc = null;
            }

            return false;
        }

        return true;
    }

    private static bool IsPeerTombstonedNoLock(BootPeerStatus peer, DateTime nowUtc)
    {
        return peer.TombstonedUntilUtc.HasValue && peer.TombstonedUntilUtc.Value > nowUtc;
    }

    private bool AllowPrivatePeerAdvertisements()
    {
        return _poolConfig.PeerAllowPrivateAdvertisements ||
            string.Equals(_poolConfig.NodeMode, "development", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePeerEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        return endpoint.Trim().TrimEnd('/');
    }

    private static bool TryNormalizePeerEndpoint(
        string? endpoint,
        bool allowPrivate,
        out string normalized,
        out string rejectionReason)
    {
        normalized = string.Empty;
        rejectionReason = string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            rejectionReason = "empty";
            return false;
        }

        string trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            rejectionReason = "invalid-url";
            return false;
        }

        string host = uri.Host.Trim().ToLowerInvariant();
        if (IsPlaceholderPeerHost(host))
        {
            rejectionReason = "placeholder";
            return false;
        }

        if (!allowPrivate && IsPrivatePeerHost(host))
        {
            rejectionReason = "private-endpoint";
            return false;
        }

        var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), host, uri.IsDefaultPort ? -1 : uri.Port);
        normalized = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private static bool ShouldPreferDialedEndpoint(string normalizedDialed, string normalizedAdvertised)
    {
        if (!Uri.TryCreate(normalizedDialed, UriKind.Absolute, out Uri? dialedUri) ||
            !Uri.TryCreate(normalizedAdvertised, UriKind.Absolute, out Uri? advertisedUri))
        {
            return false;
        }

        if (!string.Equals(dialedUri.Scheme, advertisedUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(dialedUri.Host, advertisedUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !dialedUri.IsDefaultPort && advertisedUri.IsDefaultPort;
    }

    private static bool IsPlaceholderPeerHost(string host)
    {
        return string.Equals(host, "boot.example.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "datum.example.com", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "example.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivatePeerEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && IsPrivatePeerHost(uri.Host);
    }

    private static bool IsPrivatePeerHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                bytes[0] == 0;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal ||
                (bytes[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    private BootPeerStatus? FindPeerNoLock(string endpoint)
    {
        string normalized = NormalizePeerEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return _state.Peers.FirstOrDefault(peer =>
            string.Equals(NormalizePeerEndpoint(peer.Endpoint), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private BootPeerStatus? FindPeerByEndpointOrNodeNoLock(string endpoint, string nodeId)
    {
        BootPeerStatus? byEndpoint = FindPeerNoLock(endpoint);
        if (byEndpoint != null)
        {
            return byEndpoint;
        }

        string normalizedNodeId = NormalizePeerNodeId(nodeId);
        return string.IsNullOrWhiteSpace(normalizedNodeId)
            ? null
            : FindPeerByNodeIdNoLock(normalizedNodeId);
    }

    private BootPeerStatus? FindPeerByNodeIdNoLock(string nodeId)
    {
        string normalizedNodeId = NormalizePeerNodeId(nodeId);
        if (string.IsNullOrWhiteSpace(normalizedNodeId))
        {
            return null;
        }

        return _state.Peers.FirstOrDefault(peer =>
            string.Equals(NormalizePeerNodeId(peer.NodeId), normalizedNodeId, StringComparison.Ordinal));
    }

    private static string GetPeerIdentityKeyNoLock(BootPeerStatus peer)
    {
        string nodeId = NormalizePeerNodeId(peer.NodeId);
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            return $"node:{nodeId}";
        }

        string endpoint = NormalizePeerEndpoint(peer.Endpoint);
        return string.IsNullOrWhiteSpace(endpoint)
            ? "unknown:"
            : $"endpoint:{endpoint}";
    }

    private static string GetPeerHostIdentityKeyNoLock(BootPeerStatus peer)
    {
        string endpoint = NormalizePeerEndpoint(peer.Endpoint);
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return $"host:{uri.Host.Trim().ToLowerInvariant()}";
        }

        string nodeId = NormalizePeerNodeId(peer.NodeId);
        return string.IsNullOrWhiteSpace(nodeId)
            ? "unknown:"
            : $"node:{nodeId}";
    }

    private static bool ArePeerEndpointHostsEquivalent(string left, string right)
    {
        return Uri.TryCreate(left, UriKind.Absolute, out Uri? leftUri) &&
               Uri.TryCreate(right, UriKind.Absolute, out Uri? rightUri) &&
               string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePeerNodeId(string? nodeId)
    {
        return string.IsNullOrWhiteSpace(nodeId) ? string.Empty : nodeId.Trim();
    }

    private static string ShortPeerNodeId(string? nodeId)
    {
        string normalized = NormalizePeerNodeId(nodeId);
        return string.IsNullOrWhiteSpace(normalized)
            ? "unknown-peer"
            : $"node:{normalized[..Math.Min(12, normalized.Length)]}";
    }

    private static List<string> BuildPeerSessionCapabilitiesNoLock(bool hasEndpoint)
    {
        return hasEndpoint
            ? ["v2-session", "share-relay", "address-gossip", "ping-pong", "dialable"]
            : ["v2-session", "share-relay", "ping-pong", "outbound-only"];
    }

    private static bool ShouldPrunePeerNoLock(
        BootPeerStatus peer,
        DateTime cutoffUtc,
        int minimumFailureCount,
        HashSet<string> protectedEndpoints)
    {
        string endpoint = NormalizePeerEndpoint(peer.Endpoint);
        if (string.IsNullOrWhiteSpace(endpoint) || protectedEndpoints.Contains(endpoint))
        {
            return false;
        }

        if (!IsPeerFailureStatus(peer.Status))
        {
            return false;
        }

        if (!peer.LastSeenUtc.HasValue && !peer.LastFailureUtc.HasValue)
        {
            return true;
        }

        // LastFailureUtc refreshes on every failed poll. Use the last successful sighting
        // as the stale-age anchor so continuously failing peers can actually age out.
        DateTime staleReferenceUtc = peer.LastSeenUtc ?? DateTime.MinValue;
        return peer.FailureCount >= minimumFailureCount && staleReferenceUtc <= cutoffUtc;
    }

    private static bool IsPeerFailureStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        string normalized = status.Trim().ToLowerInvariant();
        return normalized is "timeout" or "error" or "empty" or "foreign-network" or "version-mismatch" or "relay-timeout" or "relay-error" or "relay-rate-limited" ||
               normalized is "session-timeout" or "session-error" or "session-rejected" or "session-closed" or "session-handshake-failed" ||
               normalized is "udp-error" or "udp-invalid" or "udp-too-large" ||
               normalized.StartsWith("relay-http-", StringComparison.OrdinalIgnoreCase);
    }

    private BootLaunchReadinessDto BuildLaunchReadinessNoLock(IReadOnlyCollection<BootPeerStatus> peers)
    {
        string roundTriggerMode = BuildRoundTriggerModeNoLock();
        bool productionRoundModeActive = !_poolConfig.TestingRoundResetEnabled;
        bool operatorProductionHardeningReady =
            string.Equals(_poolConfig.NodeMode, "production", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_poolConfig.PublicBaseUrl) &&
            !string.IsNullOrWhiteSpace(_poolConfig.DatumPublicHost) &&
            !_poolConfig.EnableAdminApi;
        bool mainnetPayoutsReal = string.Equals(_poolConfig.BitcoinNetwork, BitcoinScript.Mainnet, StringComparison.OrdinalIgnoreCase);
        var dto = new BootLaunchReadinessDto
        {
            ReadyForProductionRoundMode = productionRoundModeActive,
            ProductionRoundModeActive = productionRoundModeActive,
            OperatorProductionHardeningReady = operatorProductionHardeningReady,
            MainnetPayoutsReal = mainnetPayoutsReal,
            RoundTriggerMode = roundTriggerMode,
            TestingRoundResetEnabled = _poolConfig.TestingRoundResetEnabled,
            NodeMode = _poolConfig.NodeMode,
            StatusSummary = productionRoundModeActive
                ? "Production round mode is active: deterministic test resets are disabled and GridPool payout transitions require a validated GridPool block proof."
                : "Testing round reset mode is active: this node is rotating snapshots with a deterministic test trigger."
        };

        if (_poolConfig.TestingRoundResetEnabled)
        {
            dto.Warnings.Add("Deterministic testing round trigger is active. Switch testing_round_reset_mode to none before launch.");
        }

        if (!string.Equals(_poolConfig.NodeMode, "production", StringComparison.OrdinalIgnoreCase))
        {
            dto.Info.Add($"node_mode is '{_poolConfig.NodeMode}', not production. This is an operator hardening label, not a payout lock.");
        }

        if (_poolConfig.EnableAdminApi)
        {
            dto.Warnings.Add("Admin API is enabled. Disable it or protect it behind private access before launch.");
        }

        if (string.IsNullOrWhiteSpace(_poolConfig.PublicBaseUrl))
        {
            dto.Info.Add("public_base_url is not configured; this node will operate as outbound-only and will not advertise itself as a reachable relay.");
        }

        if (string.IsNullOrWhiteSpace(_poolConfig.DatumPublicHost))
        {
            dto.Warnings.Add("datum_public_host is not configured, so the UI cannot show a clean DATUM connection target.");
        }

        int healthyPeers = peers.Count(peer => !IsPeerFailureStatus(peer.Status));
        if (_poolConfig.EnablePeerSync && healthyPeers == 0)
        {
            dto.Warnings.Add("Peer sync is enabled but no currently healthy peers are visible.");
        }

        List<BootPeerStatus> incompatiblePeers = peers
            .Where(peer => string.Equals(peer.CompatibilityStatus, "incompatible", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(peer.Status, "version-mismatch", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (incompatiblePeers.Count > 0)
        {
            string examples = string.Join("; ", incompatiblePeers
                .Take(3)
                .Select(peer =>
                {
                    string label = string.IsNullOrWhiteSpace(peer.Endpoint) ? ShortPeerNodeId(peer.NodeId) : peer.Endpoint;
                    string reason = string.IsNullOrWhiteSpace(peer.CompatibilityReason) ? peer.Status : peer.CompatibilityReason;
                    return $"{label}: {reason}";
                }));
            dto.Warnings.Add($"Version-incompatible peer(s) detected: {examples}.");
        }

        dto.Info.Add(_poolConfig.TestingRoundResetEnabled
            ? BuildTestingRoundResetDescriptionNoLock()
            : "Production round mode: rounds rotate only when this node accepts a valid Grid Pool block share.");
        dto.Info.Add(mainnetPayoutsReal
            ? "Bitcoin network is mainnet; accepted block templates pay real BTC according to their coinbase outputs."
            : $"Bitcoin network is '{_poolConfig.BitcoinNetwork}'; payouts are not mainnet BTC.");
        dto.Info.Add($"Healthy visible peers: {healthyPeers}/{peers.Count}.");

        return dto;
    }

    private long? InferFoundBlockHeight(string? blockHash)
    {
        lock (_sync)
        {
            string? normalizedBlockHash = NormalizeCanonicalBlockHash(blockHash);
            if (string.IsNullOrWhiteSpace(normalizedBlockHash))
            {
                return null;
            }

            if (BitcoinHashes.AreEquivalent(normalizedBlockHash, _state.CurrentTipBlockHash))
            {
                return _state.CurrentTipBlockHeight;
            }

            return _state.CurrentTipBlockHeight.HasValue ? _state.CurrentTipBlockHeight.Value + 1 : null;
        }
    }

    private void RecordGridPoolBlockFound(ShareRecordingResult result, string source, long? blockHeight)
    {
        string? normalizedBlockHash = NormalizeCanonicalBlockHash(result.BlockHash);
        if (string.IsNullOrWhiteSpace(normalizedBlockHash))
        {
            return;
        }

        BootShareProof? proof = result.AcceptedProof;
        lock (_sync)
        {
            if (BitcoinHashes.AreEquivalent(_state.LastGridPoolBlockHash, normalizedBlockHash))
            {
                return;
            }

            _state.LastGridPoolBlockHash = normalizedBlockHash;
            _state.LastGridPoolBlockHeight = blockHeight;
            _state.LastGridPoolBlockUtc = DateTime.UtcNow;
            _state.LastGridPoolBlockMinerAddress = proof?.MinerAddress;
            _state.LastGridPoolBlockDifficulty = result.ComputedDifficulty > 0 ? result.ComputedDifficulty : proof?.Difficulty;
            RecordNetworkEventNoLock(
                "gridpool-block-found",
                string.IsNullOrWhiteSpace(source) ? "unknown" : source,
                $"Accepted valid Grid Pool block share from {proof?.MinerAddress ?? "unknown miner"} at difficulty {_state.LastGridPoolBlockDifficulty?.ToString("F2", CultureInfo.InvariantCulture) ?? "unknown"}.",
                normalizedBlockHash,
                blockHeight,
                _state.LastGridPoolBlockUtc);
            RequestDeferredSaveNoLock();
            RequestDeferredHistorySaveNoLock();
        }
    }

    private void RecordFreshParentRetryEvent(
        string eventType,
        string source,
        string? blockHash,
        string message)
    {
        lock (_sync)
        {
            RecordNetworkEventNoLock(eventType, source, message, blockHash, blockHeight: null);
            RequestDeferredHistorySaveNoLock();
        }
    }

    private static bool IsTrustedFreshParentSource(string source)
    {
        return string.Equals(source, "datum", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsMiningWorkSafeNoLock(DateTime nowUtc)
    {
        bool bitcoinTipSafe = !_poolConfig.EnablePeerTipStaleProtection ||
                              _state.ProvisionalTip == null ||
                              nowUtc < _state.ProvisionalTip.GraceDeadlineUtc;
        bool bitcoinSourceSafe = _bitcoinNotificationHealth?.IsMiningSafe(nowUtc, out _) ?? true;

        // Relay health is diagnostic, not a property of the Bitcoin work plan. Refusing
        // coinbaser requests here makes DATUM fall back to solo work, whose rejected
        // shares cannot refresh relay health and therefore create a permanent reconnect
        // loop. Peer-tip protection remains the fail-closed stale-parent boundary.
        return !_identityChanged && bitcoinTipSafe && bitcoinSourceSafe;
    }

    private string BuildMiningWorkSafetyReasonNoLock()
    {
        if (IsMiningWorkSafeNoLock(DateTime.UtcNow))
        {
            return string.Empty;
        }

        if (_identityChanged)
        {
            return "Node identity changed from the identity stored with existing state; fresh mining work is paused until the prior keys are restored or the operator explicitly migrates identity.";
        }

        if (_bitcoinNotificationHealth != null &&
            !_bitcoinNotificationHealth.IsMiningSafe(DateTime.UtcNow, out string bitcoinSourceReason))
        {
            return bitcoinSourceReason;
        }

        return $"Local Bitcoin node has not confirmed provisional peer tip {_state.ProvisionalTip!.BlockHash}; fresh mining work is paused to avoid stale-parent work.";
    }

    private List<string> BuildConfigWarningsNoLock(bool peerLoopsHealthy, bool outboundRelayHealthy)
    {
        var warnings = new List<string>();
        if (_poolConfig.EnablePeerSync && string.IsNullOrWhiteSpace(_poolConfig.PublicBaseUrl))
            warnings.Add("peer sync enabled without a dialable public_base_url");
        if (_poolConfig.EnablePeerSync && !_poolConfig.EnablePulseProofs)
            warnings.Add("pulse proofs disabled while peer sync is enabled");
        if (BootProtocolVersions.IsBareReleaseVersion(BootProtocolVersions.CurrentReleaseVersion))
            warnings.Add("release version lacks git/build provenance");
        if (_identityChanged)
            warnings.Add("node identity changed from the identity stored with existing state");
        if (!peerLoopsHealthy)
            warnings.Add("peer poll loop is stale");
        if (!outboundRelayHealthy)
            warnings.Add("outbound share/pulse relay is stale");
        if (_bitcoinNotificationHealth != null)
        {
            BootBitcoinNotificationDto notification = _bitcoinNotificationHealth.Snapshot(DateTime.UtcNow);
            if (!notification.MiningSafe)
                warnings.Add($"bitcoin notification source degraded: {notification.DegradedReason}");
            else if (!string.IsNullOrWhiteSpace(notification.DegradedReason))
                warnings.Add($"bitcoin notification latency path degraded: {notification.DegradedReason}");
        }
        if (!string.IsNullOrWhiteSpace(_poolConfig.PublicBaseUrl) &&
            IsPrivatePeerEndpoint(_poolConfig.PublicBaseUrl))
            warnings.Add("public_base_url advertises a private or loopback endpoint");
        return warnings;
    }

    private bool ShouldQuarantinePreviousParentNoLock(string? parentBlockHash, DateTime nowUtc)
    {
        return _poolConfig.EnablePeerTipStaleProtection &&
               _state.ProvisionalTip != null &&
               BitcoinHashes.AreEquivalent(parentBlockHash, _state.ProvisionalTip.ParentBlockHash);
    }

    private bool IsNextBitcoinRetargetBoundaryNoLock()
    {
        return _state.CurrentTipBlockHeight.HasValue &&
               (_state.CurrentTipBlockHeight.Value + 1) % 2016 == 0;
    }

    private void ScheduleProvisionalTipGraceCheck(string blockHash, long generation)
    {
        DateTime deadlineUtc;
        lock (_sync)
        {
            if (_state.ProvisionalTip == null ||
                generation != _provisionalTipGeneration ||
                !BitcoinHashes.AreEquivalent(_state.ProvisionalTip.BlockHash, blockHash))
            {
                return;
            }

            deadlineUtc = _state.ProvisionalTip.GraceDeadlineUtc;
        }

        _ = Task.Run(async () =>
        {
            TimeSpan delay = deadlineUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            BootNetworkStatusDto? status = null;
            lock (_sync)
            {
                if (_state.ProvisionalTip == null ||
                    generation != _provisionalTipGeneration ||
                    !BitcoinHashes.AreEquivalent(_state.ProvisionalTip.BlockHash, blockHash) ||
                    DateTime.UtcNow < _state.ProvisionalTip.GraceDeadlineUtc)
                {
                    return;
                }

                RecordNetworkEventNoLock(
                    "local-bitcoin-lagging",
                    _state.ProvisionalTip.Source,
                    $"Local Bitcoin node did not confirm peer tip within {_poolConfig.PeerTipGraceSeconds} second(s); fresh work paused and reconciliation requested.",
                    _state.ProvisionalTip.BlockHash,
                    _state.CurrentTipBlockHeight.HasValue ? _state.CurrentTipBlockHeight + 1 : null);
                RequestDeferredSaveNoLock();
                RequestDeferredHistorySaveNoLock();
                status = BuildNetworkStatusNoLock();
            }

            _logger.LogWarning("Local Bitcoin node is lagging peer tip {BlockHash}; paused fresh work.", blockHash);
            await NotifyWorkTemplatesInvalidatedAsync("peer-tip-stale-protection");
            if (status != null)
            {
                await _hubContext.Clients.All.SendAsync("UpdateNetworkState", status);
            }
        });
    }

    private static BootShareValidationResult RejectValidatedShare(BootShareValidationResult validation, string reason)
    {
        return new BootShareValidationResult
        {
            IsValid = false,
            RejectionReason = reason,
            ShareId = validation.ShareId,
            MinerAddress = validation.MinerAddress,
            Username = validation.Username,
            ScriptPubKeyHex = validation.ScriptPubKeyHex,
            HeaderHex = validation.HeaderHex,
            CoinbaseHex = validation.CoinbaseHex,
            MerklePath = validation.MerklePath.ToList(),
            PrevBlockHash = validation.PrevBlockHash,
            BlockHash = validation.BlockHash,
            Difficulty = validation.Difficulty,
            IsBlock = validation.IsBlock
        };
    }

    private static bool IsWrongParentRejection(string? rejectionReason)
    {
        return !string.IsNullOrWhiteSpace(rejectionReason) &&
               rejectionReason.StartsWith("Share builds on the wrong parent block", StringComparison.OrdinalIgnoreCase);
    }

    private void SeedSeenSharesNoLock()
    {
        foreach (var proof in _state.OnDeckProofs)
        {
            RememberShareIdNoLock(proof.ShareId);
        }

        foreach (var bundle in _state.ArchivedStateBundles)
        {
            foreach (var proof in bundle.ShareProofs)
            {
                RememberShareIdNoLock(proof.ShareId);
            }
        }
    }

    private bool RememberShareIdNoLock(string shareId)
    {
        if (!_seenShareIds.Add(shareId))
        {
            return false;
        }

        _seenShareQueue.Enqueue(shareId);
        while (_seenShareQueue.Count > MaxSeenShareIds)
        {
            string expired = _seenShareQueue.Dequeue();
            _seenShareIds.Remove(expired);
        }

        return true;
    }

    private static List<PayoutInfo> ClonePayouts(IEnumerable<PayoutInfo> payouts)
    {
        return (payouts ?? []).Select(x => new PayoutInfo
        {
            Value = x.Value,
            Address = x.Address,
            Username = x.Username,
            Difficulty = x.Difficulty,
            DiffString = x.DiffString
        }).ToList();
    }

    private static PayoutInfo ClonePayout(PayoutInfo payout)
    {
        return new PayoutInfo
        {
            Value = payout.Value,
            Address = payout.Address,
            Username = payout.Username,
            Difficulty = payout.Difficulty,
            DiffString = payout.DiffString
        };
    }

    private static BestShareRecord CloneBestShare(BestShareRecord? bestShare)
    {
        if (bestShare == null)
        {
            return new BestShareRecord();
        }

        return new BestShareRecord
        {
            Difficulty = bestShare.Difficulty,
            MinerAddress = bestShare.MinerAddress,
            Timestamp = bestShare.Timestamp
        };
    }

    private static BootShareProof CloneProof(BootShareProof proof)
    {
        return new BootShareProof
        {
            ShareId = proof.ShareId,
            MinerAddress = proof.MinerAddress,
            Username = proof.Username,
            ScriptPubKeyHex = proof.ScriptPubKeyHex,
            HeaderHex = proof.HeaderHex,
            CoinbaseHex = proof.CoinbaseHex,
            MerklePath = proof.MerklePath.ToList(),
            PayoutSnapshotId = proof.PayoutSnapshotId,
            PrevBlockHash = proof.PrevBlockHash,
            Difficulty = proof.Difficulty,
            DiffString = proof.DiffString,
            Source = proof.Source,
            Timestamp = proof.Timestamp,
            ProofClass = ResolveProofClass(proof.ProofClass),
            RelayStage = ResolveRelayStage(proof.RelayStage),
            RelayTtl = proof.RelayTtl,
            TransportReceivedUtc = proof.TransportReceivedUtc,
            StateServiceReceivedUtc = proof.StateServiceReceivedUtc,
            DifficultyCheckedUtc = proof.DifficultyCheckedUtc,
            ValidationCompletedUtc = proof.ValidationCompletedUtc,
            StateMutationCompletedUtc = proof.StateMutationCompletedUtc
        };
    }

    private static BootPeerStatus ClonePeer(BootPeerStatus peer)
    {
        return new BootPeerStatus
        {
            Endpoint = peer.Endpoint,
            Status = peer.Status,
            Source = peer.Source,
            NodeId = peer.NodeId,
            ConnectionMode = peer.ConnectionMode,
            SessionConnected = peer.SessionConnected,
            Capabilities = peer.Capabilities.ToList(),
            IsConfiguredSeed = peer.IsConfiguredSeed,
            DiscoveredUtc = peer.DiscoveredUtc,
            LastAttemptUtc = peer.LastAttemptUtc,
            LastSuccessUtc = peer.LastSuccessUtc,
            LastSessionUtc = peer.LastSessionUtc,
            LatencyMs = peer.LatencyMs,
            LastSeenUtc = peer.LastSeenUtc,
            LastFailureUtc = peer.LastFailureUtc,
            SuppressedUntilUtc = peer.SuppressedUntilUtc,
            TombstonedUntilUtc = peer.TombstonedUntilUtc,
            FailureCount = peer.FailureCount,
            RelaySuccessCount = peer.RelaySuccessCount,
            RelayFailureCount = peer.RelayFailureCount,
            SessionSuccessCount = peer.SessionSuccessCount,
            SessionFailureCount = peer.SessionFailureCount,
            UdpRelaySuccessCount = peer.UdpRelaySuccessCount,
            UdpRelayFailureCount = peer.UdpRelayFailureCount,
            LastCurrentStateId = peer.LastCurrentStateId,
            LastCandidateStateId = peer.LastCandidateStateId,
            LastTipBlockHash = peer.LastTipBlockHash,
            RemoteVersion = CloneVersionInfo(peer.RemoteVersion),
            CompatibilityStatus = peer.CompatibilityStatus,
            CompatibilityReason = peer.CompatibilityReason,
            CompatibilityWarnings = (peer.CompatibilityWarnings ?? []).ToList(),
            Score = peer.Score
        };
    }

    private static BootNodeVersionInfo CloneVersionInfo(BootNodeVersionInfo? version)
    {
        if (version == null)
        {
            return new BootNodeVersionInfo();
        }

        return new BootNodeVersionInfo
        {
            SoftwareConsensusVersion = version.SoftwareConsensusVersion,
            ConsensusVersion = version.ConsensusVersion,
            ProtocolVersion = version.ProtocolVersion,
            StateBundleSchemaVersion = version.StateBundleSchemaVersion,
            HttpApiVersion = version.HttpApiVersion,
            PeerTransportVersion = version.PeerTransportVersion,
            UdpRelayVersion = version.UdpRelayVersion,
            ReleaseVersion = version.ReleaseVersion
        };
    }

    private static BootHashratePoint CloneHashratePoint(BootHashratePoint point)
    {
        return new BootHashratePoint
        {
            TimestampUtc = point.TimestampUtc,
            CurrentRoundNumber = point.CurrentRoundNumber,
            TeamEstimatedHashrateThs = point.TeamEstimatedHashrateThs,
            TeamEstimatedHashrateDisplay = point.TeamEstimatedHashrateDisplay,
            LocalDatumHashrateThs = point.LocalDatumHashrateThs,
            LocalDatumHashrateDisplay = point.LocalDatumHashrateDisplay
        };
    }

    private static BootLocalDatumMinerHashrateRollupPoint CloneLocalDatumMinerHashrateRollupPoint(BootLocalDatumMinerHashrateRollupPoint point)
    {
        return new BootLocalDatumMinerHashrateRollupPoint
        {
            Address = point.Address,
            Username = point.Username,
            TimestampUtc = point.TimestampUtc,
            CurrentRoundNumber = point.CurrentRoundNumber,
            HashrateThs = point.HashrateThs,
            HashrateDisplay = point.HashrateDisplay,
            SampleCount = point.SampleCount
        };
    }

    private static BootAcceptedShareTelemetry CloneAcceptedShareTelemetry(BootAcceptedShareTelemetry telemetry)
    {
        return new BootAcceptedShareTelemetry
        {
            MinerAddress = telemetry.MinerAddress,
            Username = telemetry.Username,
            Source = telemetry.Source,
            Difficulty = telemetry.Difficulty,
            TimestampUtc = telemetry.TimestampUtc
        };
    }

    private static BootShareDiagnosticTelemetry CloneShareDiagnostic(BootShareDiagnosticTelemetry diagnostic)
    {
        return new BootShareDiagnosticTelemetry
        {
            Source = diagnostic.Source,
            MinerAddress = diagnostic.MinerAddress,
            Username = diagnostic.Username,
            Accepted = diagnostic.Accepted,
            AffectedOnDeck = diagnostic.AffectedOnDeck,
            RejectionReason = diagnostic.RejectionReason,
            RejectionCategory = diagnostic.RejectionCategory,
            Difficulty = diagnostic.Difficulty,
            CurrentRoundNumber = diagnostic.CurrentRoundNumber,
            CurrentStateId = diagnostic.CurrentStateId,
            CandidateStateId = diagnostic.CandidateStateId,
            CurrentTipBlockHash = diagnostic.CurrentTipBlockHash,
            CurrentTipBlockHeight = diagnostic.CurrentTipBlockHeight,
            TimestampUtc = diagnostic.TimestampUtc
        };
    }

    private static BootCoinbaserFetchTelemetry CloneCoinbaserDiagnostic(BootCoinbaserFetchTelemetry diagnostic)
    {
        return new BootCoinbaserFetchTelemetry
        {
            Source = diagnostic.Source,
            RemoteEndpoint = diagnostic.RemoteEndpoint,
            ClientIdentityPreview = diagnostic.ClientIdentityPreview,
            RequestSequence = diagnostic.RequestSequence,
            RewardValue = diagnostic.RewardValue,
            TeamPayoutTotal = diagnostic.TeamPayoutTotal,
            SlotZeroValue = diagnostic.SlotZeroValue,
            SlotZeroAddress = diagnostic.SlotZeroAddress,
            UsingTemporarySlotZero = diagnostic.UsingTemporarySlotZero,
            WinnersCount = diagnostic.WinnersCount,
            CoinbaseOutputCount = diagnostic.CoinbaseOutputCount,
            ResponsePayloadBytes = diagnostic.ResponsePayloadBytes,
            DurationMs = diagnostic.DurationMs,
            ParseDurationMs = diagnostic.ParseDurationMs,
            StateReadDurationMs = diagnostic.StateReadDurationMs,
            BuildDurationMs = diagnostic.BuildDurationMs,
            SerializeDurationMs = diagnostic.SerializeDurationMs,
            SendDurationMs = diagnostic.SendDurationMs,
            CurrentRoundNumber = diagnostic.CurrentRoundNumber,
            CurrentStateId = diagnostic.CurrentStateId,
            CandidateStateId = diagnostic.CandidateStateId,
            CurrentTipBlockHash = diagnostic.CurrentTipBlockHash,
            CurrentTipBlockHeight = diagnostic.CurrentTipBlockHeight,
            TimestampUtc = diagnostic.TimestampUtc
        };
    }

    private static BootDatumShareResponseTelemetry CloneDatumShareResponse(BootDatumShareResponseTelemetry telemetry)
    {
        return new BootDatumShareResponseTelemetry
        {
            SessionId = telemetry.SessionId,
            RemoteEndpoint = telemetry.RemoteEndpoint,
            MinerAddress = telemetry.MinerAddress,
            Username = telemetry.Username,
            Accepted = telemetry.Accepted,
            AffectedOnDeck = telemetry.AffectedOnDeck,
            RejectionReason = telemetry.RejectionReason,
            Difficulty = telemetry.Difficulty,
            PrevBlockHash = telemetry.PrevBlockHash,
            JobId = telemetry.JobId,
            CoinbaseId = telemetry.CoinbaseId,
            CoinbaserId = telemetry.CoinbaserId,
            PayoutSnapshotId = telemetry.PayoutSnapshotId,
            Nonce = telemetry.Nonce,
            IsBlock = telemetry.IsBlock,
            SubsidyOnly = telemetry.SubsidyOnly,
            QuickDiff = telemetry.QuickDiff,
            NonceOnlySubmit = telemetry.NonceOnlySubmit,
            UsedCachedJob = telemetry.UsedCachedJob,
            CachedJobAgeMs = telemetry.CachedJobAgeMs,
            TargetByte = telemetry.TargetByte,
            TargetByteIndex = telemetry.TargetByteIndex,
            PayloadBytes = telemetry.PayloadBytes,
            CoinbaseBytes = telemetry.CoinbaseBytes,
            Coinb1Bytes = telemetry.Coinb1Bytes,
            Coinb2Bytes = telemetry.Coinb2Bytes,
            MerkleBranchCount = telemetry.MerkleBranchCount,
            ParseDurationMs = telemetry.ParseDurationMs,
            BuildDurationMs = telemetry.BuildDurationMs,
            ValidationDurationMs = telemetry.ValidationDurationMs,
            SnapshotReadDurationMs = telemetry.SnapshotReadDurationMs,
            SnapshotReadLockWaitDurationMs = telemetry.SnapshotReadLockWaitDurationMs,
            SnapshotReadLockBodyDurationMs = telemetry.SnapshotReadLockBodyDurationMs,
            ShareCoreValidationDurationMs = telemetry.ShareCoreValidationDurationMs,
            StateMutationDurationMs = telemetry.StateMutationDurationMs,
            StateMutationLockWaitDurationMs = telemetry.StateMutationLockWaitDurationMs,
            StateMutationLockBodyDurationMs = telemetry.StateMutationLockBodyDurationMs,
            StaleHandlingDurationMs = telemetry.StaleHandlingDurationMs,
            ResponseSendDurationMs = telemetry.ResponseSendDurationMs,
            TotalDurationMs = telemetry.TotalDurationMs,
            CurrentRoundNumber = telemetry.CurrentRoundNumber,
            CurrentStateId = telemetry.CurrentStateId,
            CandidateStateId = telemetry.CandidateStateId,
            CurrentTipBlockHash = telemetry.CurrentTipBlockHash,
            CurrentTipBlockHeight = telemetry.CurrentTipBlockHeight,
            TimestampUtc = telemetry.TimestampUtc
        };
    }

    private static BootDatumSessionTelemetry CloneDatumSession(BootDatumSessionTelemetry session)
    {
        return new BootDatumSessionTelemetry
        {
            SessionId = session.SessionId,
            Protocol = session.Protocol,
            RemoteEndpoint = session.RemoteEndpoint,
            ClientIdentityKey = session.ClientIdentityKey,
            ClientEncryptIdentityKey = session.ClientEncryptIdentityKey,
            LockedPayoutAddress = session.LockedPayoutAddress,
            HandshakeCompleted = session.HandshakeCompleted,
            ServerInitiatedClose = session.ServerInitiatedClose,
            ServerCloseEventType = session.ServerCloseEventType,
            CloseDisposition = session.CloseDisposition,
            CloseReason = session.CloseReason,
            HelloCount = session.HelloCount,
            CoinbaserFetchCount = session.CoinbaserFetchCount,
            RefreshRequestCount = session.RefreshRequestCount,
            ShareResponseCount = session.ShareResponseCount,
            AcceptedShareCount = session.AcceptedShareCount,
            RejectedShareCount = session.RejectedShareCount,
            AffectedOnDeckCount = session.AffectedOnDeckCount,
            StartedUtc = session.StartedUtc,
            HelloReceivedUtc = session.HelloReceivedUtc,
            PayoutLockedUtc = session.PayoutLockedUtc,
            LastCoinbaserFetchUtc = session.LastCoinbaserFetchUtc,
            LastShareResponseUtc = session.LastShareResponseUtc,
            LastRefreshRequestUtc = session.LastRefreshRequestUtc,
            LastActivityUtc = session.LastActivityUtc,
            LastActivityType = session.LastActivityType,
            ClosedUtc = session.ClosedUtc,
            DurationMs = session.DurationMs,
            HandshakeMs = session.HandshakeMs,
            IdleBeforeCloseMs = session.IdleBeforeCloseMs
        };
    }

    private static BootDatumProtocolEvent CloneDatumProtocolEvent(BootDatumProtocolEvent telemetry)
    {
        return new BootDatumProtocolEvent
        {
            SessionId = telemetry.SessionId,
            Sequence = telemetry.Sequence,
            Protocol = telemetry.Protocol,
            RemoteEndpoint = telemetry.RemoteEndpoint,
            Direction = telemetry.Direction,
            EventType = telemetry.EventType,
            MessageLabel = telemetry.MessageLabel,
            ProtoCmd = telemetry.ProtoCmd,
            MiningSubcommand = telemetry.MiningSubcommand,
            IsSigned = telemetry.IsSigned,
            IsEncryptedPubKey = telemetry.IsEncryptedPubKey,
            IsEncryptedChannel = telemetry.IsEncryptedChannel,
            CmdLen = telemetry.CmdLen,
            BytesRead = telemetry.BytesRead,
            ExpectedBytes = telemetry.ExpectedBytes,
            DecryptedBytes = telemetry.DecryptedBytes,
            RawHeaderHex = telemetry.RawHeaderHex,
            DecodedHeaderHex = telemetry.DecodedHeaderHex,
            HeaderKeyBefore = telemetry.HeaderKeyBefore,
            HeaderKeyAfter = telemetry.HeaderKeyAfter,
            Accepted = telemetry.Accepted,
            AffectedOnDeck = telemetry.AffectedOnDeck,
            RejectionReason = telemetry.RejectionReason,
            Difficulty = telemetry.Difficulty,
            PrevBlockHash = telemetry.PrevBlockHash,
            JobId = telemetry.JobId,
            CoinbaseId = telemetry.CoinbaseId,
            NonceOnlySubmit = telemetry.NonceOnlySubmit,
            UsedCachedJob = telemetry.UsedCachedJob,
            CachedJobAgeMs = telemetry.CachedJobAgeMs,
            Username = telemetry.Username,
            CloseDisposition = telemetry.CloseDisposition,
            CloseReason = telemetry.CloseReason,
            Detail = telemetry.Detail,
            DurationMs = telemetry.DurationMs,
            CurrentRoundNumber = telemetry.CurrentRoundNumber,
            CurrentStateId = telemetry.CurrentStateId,
            CandidateStateId = telemetry.CandidateStateId,
            CurrentTipBlockHash = telemetry.CurrentTipBlockHash,
            CurrentTipBlockHeight = telemetry.CurrentTipBlockHeight,
            TimestampUtc = telemetry.TimestampUtc
        };
    }

    private static BootNetworkEvent CloneNetworkEvent(BootNetworkEvent networkEvent)
    {
        return new BootNetworkEvent
        {
            EventType = networkEvent.EventType,
            Source = networkEvent.Source,
            Message = networkEvent.Message,
            Transport = networkEvent.Transport,
            RemoteEndpoint = networkEvent.RemoteEndpoint,
            RemoteNodeId = networkEvent.RemoteNodeId,
            AnnouncedAtUtc = networkEvent.AnnouncedAtUtc,
            RelayLatencyMs = networkEvent.RelayLatencyMs,
            PayloadBytes = networkEvent.PayloadBytes,
            BlockHash = networkEvent.BlockHash,
            BlockHeight = networkEvent.BlockHeight,
            CurrentRoundNumber = networkEvent.CurrentRoundNumber,
            CurrentStateId = networkEvent.CurrentStateId,
            CandidateStateId = networkEvent.CandidateStateId,
            CurrentTipBlockHash = networkEvent.CurrentTipBlockHash,
            CurrentTipBlockHeight = networkEvent.CurrentTipBlockHeight,
            TimestampUtc = networkEvent.TimestampUtc
        };
    }

    private static BootPeerRelayObservation ClonePeerRelayObservation(BootPeerRelayObservation observation)
    {
        return new BootPeerRelayObservation
        {
            ShareId = observation.ShareId,
            ProofClass = ResolveProofClass(observation.ProofClass),
            RelayStage = ResolveRelayStage(observation.RelayStage),
            Transport = observation.Transport,
            Source = observation.Source,
            RemoteEndpoint = observation.RemoteEndpoint,
            MinerAddress = observation.MinerAddress,
            Username = observation.Username,
            Difficulty = observation.Difficulty,
            Accepted = observation.Accepted,
            AffectedOnDeck = observation.AffectedOnDeck,
            RejectionReason = observation.RejectionReason,
            IsFirstArrival = observation.IsFirstArrival,
            FirstTransport = observation.FirstTransport,
            DeltaFromFirstMs = observation.DeltaFromFirstMs,
            PayloadBytes = observation.PayloadBytes,
            ValidationDurationMs = observation.ValidationDurationMs,
            TransportReceivedUtc = observation.TransportReceivedUtc,
            StateServiceReceivedUtc = observation.StateServiceReceivedUtc,
            DifficultyCheckedUtc = observation.DifficultyCheckedUtc,
            ValidationCompletedUtc = observation.ValidationCompletedUtc,
            StateMutationCompletedUtc = observation.StateMutationCompletedUtc,
            TransportToStateServiceMs = observation.TransportToStateServiceMs,
            StateServiceToDifficultyMs = observation.StateServiceToDifficultyMs,
            DifficultyToValidationMs = observation.DifficultyToValidationMs,
            ValidationToMutationMs = observation.ValidationToMutationMs,
            CurrentRoundNumber = observation.CurrentRoundNumber,
            CurrentStateId = observation.CurrentStateId,
            CandidateStateId = observation.CandidateStateId,
            CurrentTipBlockHash = observation.CurrentTipBlockHash,
            CurrentTipBlockHeight = observation.CurrentTipBlockHeight,
            TimestampUtc = observation.TimestampUtc
        };
    }

    private static BootStateBundle CloneBundle(BootStateBundle bundle)
    {
        return new BootStateBundle
        {
            StateId = bundle.StateId,
            PreviousStateId = bundle.PreviousStateId,
            Kind = bundle.Kind,
            CurrentRoundNumber = bundle.CurrentRoundNumber,
            ProtocolVersion = bundle.ProtocolVersion,
            ConsensusVersion = bundle.ConsensusVersion,
            StateBundleSchemaVersion = bundle.StateBundleSchemaVersion,
            HttpApiVersion = bundle.HttpApiVersion,
            PeerTransportVersion = bundle.PeerTransportVersion,
            UdpRelayVersion = bundle.UdpRelayVersion,
            ReleaseVersion = bundle.ReleaseVersion,
            VersionInfo = new BootNodeVersionInfo
            {
                SoftwareConsensusVersion = bundle.VersionInfo?.SoftwareConsensusVersion ?? 0,
                ConsensusVersion = bundle.VersionInfo?.ConsensusVersion ?? 0,
                ProtocolVersion = bundle.VersionInfo?.ProtocolVersion ?? 0,
                StateBundleSchemaVersion = bundle.VersionInfo?.StateBundleSchemaVersion ?? 0,
                HttpApiVersion = bundle.VersionInfo?.HttpApiVersion ?? 0,
                PeerTransportVersion = bundle.VersionInfo?.PeerTransportVersion ?? 0,
                UdpRelayVersion = bundle.VersionInfo?.UdpRelayVersion ?? 0,
                ReleaseVersion = bundle.VersionInfo?.ReleaseVersion ?? string.Empty
            },
            NetworkId = bundle.NetworkId,
            LockedByBlockHash = bundle.LockedByBlockHash,
            LockedByBlockHeight = bundle.LockedByBlockHeight,
            ParentBlockHash = bundle.ParentBlockHash,
            ParentBlockHeight = bundle.ParentBlockHeight,
            CreatedAtUtc = bundle.CreatedAtUtc,
            TotalDifficulty = bundle.TotalDifficulty,
            ActiveSnapshotId = bundle.ActiveSnapshotId,
            PaidSnapshotId = bundle.PaidSnapshotId,
            ActiveSnapshotProofIds = (bundle.ActiveSnapshotProofIds ?? []).ToList(),
            PaidSnapshotProofIds = (bundle.PaidSnapshotProofIds ?? []).ToList(),
            SupportFeeEnabled = bundle.SupportFeeEnabled,
            PayoutVariant = bundle.PayoutVariant,
            ValidParentBlockHashes = (bundle.ValidParentBlockHashes ?? []).ToList(),
            WinnersList = ClonePayouts(bundle.WinnersList),
            ProofWinnersList = ClonePayouts(bundle.ProofWinnersList),
            ShareProofs = (bundle.ShareProofs ?? []).Select(CloneProof).ToList(),
            WorkSetProofs = (bundle.WorkSetProofs ?? []).Select(CloneProof).ToList(),
            SnapshotContexts = (bundle.SnapshotContexts ?? []).Select(CloneSnapshotContext).ToList(),
            SnapshotFamilyMember = CloneSnapshotFamilyMember(bundle.SnapshotFamilyMember),
            Commitment = new BootCommitmentInfo
            {
                ProtocolVersion = bundle.Commitment.ProtocolVersion,
                NetworkId = bundle.Commitment.NetworkId,
                NextStateId = bundle.Commitment.NextStateId,
                OnChainSupported = bundle.Commitment.OnChainSupported,
                TagPreview = bundle.Commitment.TagPreview,
                SupportNote = bundle.Commitment.SupportNote
            }
        };
    }

    private static BootPayoutSnapshotContext CloneSnapshotContext(BootPayoutSnapshotContext context)
    {
        return new BootPayoutSnapshotContext
        {
            SnapshotId = context.SnapshotId,
            FamilyId = context.FamilyId,
            PreviousSnapshotId = context.PreviousSnapshotId,
            CurrentRoundNumber = context.CurrentRoundNumber,
            LockedByBlockHash = context.LockedByBlockHash,
            LockedByBlockHeight = context.LockedByBlockHeight,
            CreatedAtUtc = context.CreatedAtUtc,
            SupportFeeEnabled = context.SupportFeeEnabled,
            PayoutVariant = context.PayoutVariant,
            ProofIds = (context.ProofIds ?? []).ToList(),
            WinnersList = ClonePayouts(context.WinnersList),
            FeeFreeWinnersList = ClonePayouts(context.FeeFreeWinnersList)
        };
    }

    private static BootSnapshotFamilyMember? CloneSnapshotFamilyMember(BootSnapshotFamilyMember? member)
    {
        if (member == null)
        {
            return null;
        }

        return new BootSnapshotFamilyMember
        {
            FamilyId = member.FamilyId,
            ConsensusVersion = member.ConsensusVersion,
            NetworkId = member.NetworkId,
            PredecessorSnapshotId = member.PredecessorSnapshotId,
            BoundaryBlockHash = member.BoundaryBlockHash,
            BoundaryBlockHeight = member.BoundaryBlockHeight,
            PayoutVariant = member.PayoutVariant,
            SnapshotId = member.SnapshotId,
            BoundaryReserveProofs = (member.BoundaryReserveProofs ?? []).Select(CloneProof).ToList()
        };
    }

    private static BootSnapshotFamilyState CloneSnapshotFamily(BootSnapshotFamilyState family) => new()
    {
        FamilyId = family.FamilyId,
        ConsensusVersion = family.ConsensusVersion,
        NetworkId = family.NetworkId,
        PredecessorSnapshotId = family.PredecessorSnapshotId,
        BoundaryBlockHash = family.BoundaryBlockHash,
        BoundaryBlockHeight = family.BoundaryBlockHeight,
        PayoutVariant = family.PayoutVariant,
        IsOpen = family.IsOpen,
        BoundaryOnActiveChain = family.BoundaryOnActiveChain,
        MemberSnapshotIds = (family.MemberSnapshotIds ?? []).ToList(),
        ReconciledProofs = (family.ReconciledProofs ?? []).Select(CloneProof).ToList(),
        PaidProofIds = (family.PaidProofIds ?? []).ToList(),
        SiblingAdmissions = family.SiblingAdmissions,
        UnionAdditions = family.UnionAdditions,
        NoOpAdmissions = family.NoOpAdmissions,
        DroppedNoOpMembers = family.DroppedNoOpMembers,
        PayoutChanges = family.PayoutChanges,
        ConvergenceCount = family.ConvergenceCount
    };

    private static BootSnapshotReconciliationCounters CloneReconciliationCounters(BootSnapshotReconciliationCounters? counters) => new()
    {
        SiblingAdmissions = counters?.SiblingAdmissions ?? 0,
        UnionAdditions = counters?.UnionAdditions ?? 0,
        NoOpAdmissions = counters?.NoOpAdmissions ?? 0,
        DroppedNoOpMembers = counters?.DroppedNoOpMembers ?? 0,
        PayoutChanges = counters?.PayoutChanges ?? 0,
        ConvergenceCount = counters?.ConvergenceCount ?? 0,
        FamilyMismatchRejections = counters?.FamilyMismatchRejections ?? 0
    };

    private static BootProvisionalTipState? CloneProvisionalTip(BootProvisionalTipState? provisional)
    {
        if (provisional == null)
        {
            return null;
        }

        return new BootProvisionalTipState
        {
            BlockHash = provisional.BlockHash,
            ParentBlockHash = provisional.ParentBlockHash,
            HeaderHex = provisional.HeaderHex,
            CompactTarget = provisional.CompactTarget,
            HeaderTimeUtc = provisional.HeaderTimeUtc,
            ObservedUtc = provisional.ObservedUtc,
            GraceDeadlineUtc = provisional.GraceDeadlineUtc,
            Source = provisional.Source,
            SnapshotId = provisional.SnapshotId,
            SnapshotProofs = provisional.SnapshotProofs.Select(CloneProof).ToList(),
            ExpectedDifficultyValidated = provisional.ExpectedDifficultyValidated
        };
    }

    private static string NormalizeSearchTerm(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private sealed class LocalDatumAddressHashrateTracker
    {
        public string Address { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public long TotalAcceptedShareCount { get; set; }
        public int CurrentRoundNumber { get; set; } = -1;
        public int CurrentRoundAcceptedShareCount { get; set; }
        public double CurrentRoundBestDifficulty { get; set; }
        public DateTime? LastShareUtc { get; set; }
        public HashSet<string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LocalDatumShareSample> Samples { get; set; } = [];
        public List<LocalMiningWorkSample> WorkSamples { get; set; } = [];
    }

    private sealed class LocalDatumShareSample
    {
        public string Source { get; set; } = string.Empty;
        public double Difficulty { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    private sealed class LocalMiningWorkSample
    {
        public string Source { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndUtc { get; set; }
        public long AcceptedShareCount { get; set; }
        public double AcceptedWorkDifficulty { get; set; }
        public double FeeWorkDifficulty { get; set; }
        public double BestDifficulty { get; set; }
    }

    private sealed class LocalMiningSourceGauge
    {
        public double HashrateThs { get; set; }
        public int ActiveMinerCount { get; set; }
        public DateTime ObservedUtc { get; set; }
    }
}
