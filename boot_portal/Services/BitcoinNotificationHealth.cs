using System.Text.RegularExpressions;
using boot_portal.Models;

namespace boot_portal.Services;

public enum BitcoinZmqSequenceObservation
{
    First,
    Normal,
    Gap,
    Duplicate,
    Reset,
    Wrap
}

public sealed class BitcoinZmqSequenceTracker
{
    public uint? LastSequence { get; private set; }
    public long SequenceGapCount { get; private set; }
    public long DuplicateCount { get; private set; }
    public long ResetCount { get; private set; }
    public long WrapCount { get; private set; }

    public BitcoinZmqSequenceObservation Observe(uint sequence)
    {
        if (!LastSequence.HasValue)
        {
            LastSequence = sequence;
            return BitcoinZmqSequenceObservation.First;
        }

        uint previous = LastSequence.Value;
        if (sequence == previous)
        {
            DuplicateCount++;
            return BitcoinZmqSequenceObservation.Duplicate;
        }

        uint expected = unchecked(previous + 1);
        LastSequence = sequence;
        if (previous == uint.MaxValue && sequence == 0)
        {
            WrapCount++;
            return BitcoinZmqSequenceObservation.Wrap;
        }

        if (sequence == expected)
        {
            return BitcoinZmqSequenceObservation.Normal;
        }

        if (sequence < previous)
        {
            ResetCount++;
            return BitcoinZmqSequenceObservation.Reset;
        }

        SequenceGapCount += sequence - expected;
        return BitcoinZmqSequenceObservation.Gap;
    }
}

public sealed class BitcoinNotificationHealth
{
    private sealed class ZmqTopicState
    {
        public required string Topic { get; init; }
        public required string EndpointLabel { get; init; }
        public bool Configured { get; init; }
        public bool SubscriberRunning { get; set; }
        public bool PublisherAdvertisedByRpc { get; set; }
        public List<string> PublisherEndpointLabels { get; set; } = [];
        public DateTime? LastEventUtc { get; set; }
        public string LastBlockHash { get; set; } = string.Empty;
        public long ReconnectCount { get; set; }
        public BitcoinZmqSequenceTracker Sequence { get; } = new();
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _reconciliationSignal = new(0, 1);
    private readonly Dictionary<string, ZmqTopicState> _zmqTopics = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _mode;
    private readonly bool _rpcConfigured;
    private readonly int _lagGraceSeconds;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private bool _rpcReachable;
    private bool _rpcSynced;
    private bool _initialBlockDownload;
    private long? _bestHeight;
    private long? _headerHeight;
    private string _bestBlockHash = string.Empty;
    private double? _verificationProgress;
    private DateTime? _lastRpcCheckUtc;
    private DateTime? _lastRpcSuccessUtc;
    private string _lastRpcError = string.Empty;
    private long _reconciliationCount;
    private long _recoveredMissedBlockCount;
    private DateTime? _lastReconciliationUtc;

    public BitcoinNotificationHealth(PoolConfig config)
    {
        _mode = BitcoinNotificationModes.Resolve(config);
        _rpcConfigured = Uri.TryCreate(config.BitcoinRpcUrl, UriKind.Absolute, out Uri? uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        _lagGraceSeconds = Math.Max(1, config.BitcoinRpcLagGraceSeconds);

        if (_mode == BitcoinNotificationModes.AttachedNode)
        {
            RegisterZmqTopic("hashblock", config.BitcoinZmqEndpoint);
            if (!string.IsNullOrWhiteSpace(config.BitcoinZmqRawBlockEndpoint))
            {
                RegisterZmqTopic("rawblock", config.BitcoinZmqRawBlockEndpoint);
            }
        }
    }

    public string Mode => _mode;
    public bool IsAttachedNode => _mode == BitcoinNotificationModes.AttachedNode;
    public bool RpcConfigured => _rpcConfigured;

    public void RequestReconciliation()
    {
        if (!_rpcConfigured)
        {
            return;
        }

        try
        {
            _reconciliationSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending reconciliation is sufficient.
        }
    }

    public async Task WaitForReconciliationRequestAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _reconciliationSignal.WaitAsync(timeout, cancellationToken);
    }

    public void RecordZmqSubscriberStarted(string topic, string endpoint)
    {
        lock (_sync)
        {
            ZmqTopicState state = GetOrCreateTopicNoLock(topic, endpoint);
            state.SubscriberRunning = true;
            state.ReconnectCount++;
        }
    }

    public void RecordZmqSubscriberStopped(string topic, string endpoint)
    {
        lock (_sync)
        {
            GetOrCreateTopicNoLock(topic, endpoint).SubscriberRunning = false;
        }
    }

    public BitcoinZmqSequenceObservation RecordZmqEvent(
        string topic,
        string endpoint,
        uint sequence,
        string blockHash,
        DateTime timestampUtc)
    {
        lock (_sync)
        {
            ZmqTopicState state = GetOrCreateTopicNoLock(topic, endpoint);
            state.LastEventUtc = timestampUtc;
            state.LastBlockHash = blockHash;
            BitcoinZmqSequenceObservation observation = state.Sequence.Observe(sequence);
            if (observation == BitcoinZmqSequenceObservation.Reset)
            {
                state.ReconnectCount++;
            }
            return observation;
        }
    }

    public void RecordAdvertisedZmqPublishers(IEnumerable<BitcoinZmqPublisher> publishers)
    {
        Dictionary<string, List<string>> advertised = publishers
            .Where(publisher => !string.IsNullOrWhiteSpace(publisher.Topic))
            .GroupBy(
                publisher => NormalizeZmqTopic(publisher.Topic),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(publisher => RedactEndpoint(publisher.Address))
                    .Where(address => !string.IsNullOrWhiteSpace(address))
                    .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            foreach (ZmqTopicState state in _zmqTopics.Values)
            {
                state.PublisherEndpointLabels = advertised.TryGetValue(state.Topic, out List<string>? endpoints)
                    ? endpoints
                    : [];
                state.PublisherAdvertisedByRpc = state.PublisherEndpointLabels.Count > 0;
            }
        }
    }

    public void RecordRpcSuccess(
        long blocks,
        long headers,
        string bestBlockHash,
        bool initialBlockDownload,
        double? verificationProgress,
        DateTime timestampUtc)
    {
        lock (_sync)
        {
            _rpcReachable = true;
            _rpcSynced = !initialBlockDownload && blocks >= headers;
            _initialBlockDownload = initialBlockDownload;
            _bestHeight = blocks;
            _headerHeight = headers;
            _bestBlockHash = bestBlockHash;
            _verificationProgress = verificationProgress;
            _lastRpcCheckUtc = timestampUtc;
            _lastRpcSuccessUtc = timestampUtc;
            _lastRpcError = string.Empty;
        }
    }

    public void RecordRpcFailure(string error, DateTime timestampUtc)
    {
        lock (_sync)
        {
            _rpcReachable = false;
            _rpcSynced = false;
            _lastRpcCheckUtc = timestampUtc;
            _lastRpcError = SanitizeError(error);
        }
    }

    public void RecordRpcTipMismatch(string error, DateTime timestampUtc)
    {
        lock (_sync)
        {
            _rpcReachable = true;
            _rpcSynced = false;
            _lastRpcCheckUtc = timestampUtc;
            _lastRpcSuccessUtc = timestampUtc;
            _lastRpcError = SanitizeError(error);
        }
    }

    public void RecordReconciliation(int recoveredBlocks, DateTime timestampUtc)
    {
        lock (_sync)
        {
            _reconciliationCount++;
            _recoveredMissedBlockCount += Math.Max(0, recoveredBlocks);
            _lastReconciliationUtc = timestampUtc;
        }
    }

    public bool IsMiningSafe(DateTime nowUtc, out string reason)
    {
        reason = string.Empty;
        if (!IsAttachedNode)
        {
            return true;
        }

        lock (_sync)
        {
            if (!_rpcConfigured)
            {
                reason = "Attached-node mode requires bitcoin_rpc_url and RPC authentication for reconciliation.";
                return false;
            }

            // Repeated failed polls must not renew the grace period indefinitely.
            DateTime reference = _lastRpcSuccessUtc ?? _startedUtc;
            if (!_rpcReachable && nowUtc - reference >= TimeSpan.FromSeconds(_lagGraceSeconds))
            {
                reason = string.IsNullOrWhiteSpace(_lastRpcError)
                    ? "The attached Bitcoin RPC source is unreachable."
                    : $"The attached Bitcoin RPC source is unreachable: {_lastRpcError}";
                return false;
            }

            if (_rpcReachable && !_rpcSynced)
            {
                reason = !string.IsNullOrWhiteSpace(_lastRpcError)
                    ? _lastRpcError
                    : _initialBlockDownload
                        ? "The attached Bitcoin node is still in initial block download."
                        : "The attached Bitcoin node has not caught up to its advertised header height.";
                return false;
            }
        }

        return true;
    }

    public BootBitcoinNotificationDto Snapshot(DateTime nowUtc)
    {
        lock (_sync)
        {
            bool miningSafe = IsMiningSafeNoLock(nowUtc, out string degradedReason);
            if (miningSafe && IsAttachedNode)
            {
                string zmqDegradedReason = BuildZmqDegradedReasonNoLock();
                if (!string.IsNullOrWhiteSpace(zmqDegradedReason))
                {
                    degradedReason = zmqDegradedReason;
                }
            }
            return new BootBitcoinNotificationDto
            {
                Mode = _mode,
                AuthorityClass = IsAttachedNode ? "local-full-node" : "external-observer",
                MiningSafe = miningSafe,
                DegradedReason = degradedReason,
                Rpc = new BootBitcoinRpcHealthDto
                {
                    Configured = _rpcConfigured,
                    Reachable = _rpcReachable,
                    Synced = _rpcSynced,
                    InitialBlockDownload = _initialBlockDownload,
                    BestHeight = _bestHeight,
                    HeaderHeight = _headerHeight,
                    BestBlockHash = _bestBlockHash,
                    VerificationProgress = _verificationProgress,
                    LastCheckUtc = _lastRpcCheckUtc,
                    LastSuccessUtc = _lastRpcSuccessUtc,
                    LastError = _lastRpcError
                },
                ZmqTopics = _zmqTopics.Values
                    .OrderBy(topic => topic.Topic, StringComparer.OrdinalIgnoreCase)
                    .Select(topic => new BootBitcoinZmqTopicHealthDto
                    {
                        Topic = topic.Topic,
                        EndpointLabel = topic.EndpointLabel,
                        Configured = topic.Configured,
                        SubscriberRunning = topic.SubscriberRunning,
                        PublisherAdvertisedByRpc = topic.PublisherAdvertisedByRpc,
                        PublisherCount = topic.PublisherEndpointLabels.Count,
                        PublisherEndpointLabels = [.. topic.PublisherEndpointLabels],
                        LastEventUtc = topic.LastEventUtc,
                        LastBlockHash = topic.LastBlockHash,
                        LastSequence = topic.Sequence.LastSequence,
                        SequenceGapCount = topic.Sequence.SequenceGapCount,
                        DuplicateCount = topic.Sequence.DuplicateCount,
                        ResetCount = topic.Sequence.ResetCount,
                        WrapCount = topic.Sequence.WrapCount,
                        ReconnectCount = topic.ReconnectCount
                    })
                    .ToList(),
                ReconciliationCount = _reconciliationCount,
                RecoveredMissedBlockCount = _recoveredMissedBlockCount,
                LastReconciliationUtc = _lastReconciliationUtc
            };
        }
    }

    private bool IsMiningSafeNoLock(DateTime nowUtc, out string reason)
    {
        reason = string.Empty;
        if (!IsAttachedNode)
        {
            return true;
        }

        if (!_rpcConfigured)
        {
            reason = "Attached-node mode requires bitcoin_rpc_url and RPC authentication for reconciliation.";
            return false;
        }

        DateTime reference = _lastRpcSuccessUtc ?? _startedUtc;
        if (!_rpcReachable && nowUtc - reference >= TimeSpan.FromSeconds(_lagGraceSeconds))
        {
            reason = string.IsNullOrWhiteSpace(_lastRpcError)
                ? "The attached Bitcoin RPC source is unreachable."
                : $"The attached Bitcoin RPC source is unreachable: {_lastRpcError}";
            return false;
        }

        if (_rpcReachable && !_rpcSynced)
        {
            reason = !string.IsNullOrWhiteSpace(_lastRpcError)
                ? _lastRpcError
                : _initialBlockDownload
                    ? "The attached Bitcoin node is still in initial block download."
                    : "The attached Bitcoin node has not caught up to its advertised header height.";
            return false;
        }

        return true;
    }

    private string BuildZmqDegradedReasonNoLock()
    {
        List<string> missingSubscribers = _zmqTopics.Values
            .Where(topic => topic.Configured && !topic.SubscriberRunning)
            .Select(topic => topic.Topic)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(topic => topic, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingSubscribers.Count > 0)
        {
            return $"Bitcoin RPC is authoritative, but ZMQ subscribers are not running for: {string.Join(", ", missingSubscribers)}.";
        }

        if (_lastRpcSuccessUtc.HasValue)
        {
            List<string> duplicatePublishers = _zmqTopics.Values
                .Where(topic => topic.Configured && topic.PublisherEndpointLabels.Count > 1)
                .Select(topic => $"{topic.Topic} ({topic.PublisherEndpointLabels.Count})")
                .OrderBy(topic => topic, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (duplicatePublishers.Count > 0)
            {
                return $"Bitcoin RPC is authoritative, but duplicate ZMQ publishers are configured: {string.Join(", ", duplicatePublishers)}.";
            }

            List<string> missingPublishers = _zmqTopics.Values
                .Where(topic => topic.Configured && !topic.PublisherAdvertisedByRpc)
                .Select(topic => topic.Topic)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(topic => topic, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missingPublishers.Count > 0)
            {
                return $"Bitcoin RPC is authoritative, but getzmqnotifications does not advertise: {string.Join(", ", missingPublishers)}.";
            }
        }

        return string.Empty;
    }

    private void RegisterZmqTopic(string topic, string endpoint)
    {
        string key = BuildTopicKey(topic, endpoint);
        _zmqTopics[key] = new ZmqTopicState
        {
            Topic = topic,
            EndpointLabel = RedactEndpoint(endpoint),
            Configured = !string.IsNullOrWhiteSpace(endpoint)
        };
    }

    private ZmqTopicState GetOrCreateTopicNoLock(string topic, string endpoint)
    {
        string key = BuildTopicKey(topic, endpoint);
        if (_zmqTopics.TryGetValue(key, out ZmqTopicState? state))
        {
            return state;
        }

        state = new ZmqTopicState
        {
            Topic = topic,
            EndpointLabel = RedactEndpoint(endpoint),
            Configured = !string.IsNullOrWhiteSpace(endpoint)
        };
        _zmqTopics[key] = state;
        return state;
    }

    private static string BuildTopicKey(string topic, string endpoint) =>
        $"{topic.Trim().ToLowerInvariant()}|{RedactEndpoint(endpoint)}";

    private static string NormalizeZmqTopic(string topic)
    {
        string value = topic.Trim();
        return value.StartsWith("pub", StringComparison.OrdinalIgnoreCase)
            ? value[3..]
            : value;
    }

    private static string RedactEndpoint(string? endpoint)
    {
        string value = endpoint?.Trim() ?? string.Empty;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            string authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return $"{uri.Scheme}://{authority}";
        }

        return Regex.Replace(value, @"//[^/@\s]+@", "//");
    }

    private static string SanitizeError(string? error)
    {
        string value = error?.Trim() ?? string.Empty;
        value = Regex.Replace(value, @"https?://[^\s]+", match =>
        {
            return Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri)
                ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}"
                : "RPC endpoint";
        });
        return value.Length > 240 ? value[..240] : value;
    }
}
