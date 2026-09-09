using System.Text.Json.Serialization;
using boot_portal.Utils;

namespace boot_portal.Models;

/// <summary>
/// Runtime configuration for a sovereign GridPool node.
/// </summary>
public class PoolConfig
{
    [JsonPropertyName("NotificationSource")]
    public string NotificationSource { get; set; } = "MempoolSpace";

    [JsonPropertyName("bitcoin_notification_mode")]
    public string BitcoinNotificationMode { get; set; } = string.Empty;

    [JsonPropertyName("bitcoin_network")]
    public string BitcoinNetwork { get; set; } = BitcoinScript.Mainnet;

    [JsonPropertyName("allow_empty_snapshot_bootstrap")]
    public bool AllowEmptySnapshotBootstrap { get; set; } = false;

    [JsonPropertyName("pool_payout_script")]
    public string PoolPayoutScript { get; set; } = string.Empty;

    [JsonPropertyName("winners_list_size")]
    public int WinnersListSize { get; set; } = 299;

    [JsonPropertyName("grid_labs_support_fee_enabled")]
    public bool GridLabsSupportFeeEnabled { get; set; } = true;

    [JsonPropertyName("work_set_reserve_multiplier")]
    public int WorkSetReserveMultiplier { get; set; } = 3;

    [JsonIgnore]
    public int SupportFeeSlotCount => GridLabsSupportFeeEnabled ? 1 : 0;

    [JsonIgnore]
    public int SharedWinnerSlotCount => Math.Max(1, WinnersListSize - SupportFeeSlotCount);

    [JsonIgnore]
    public int SnapshotProofSlotCount => WinnersListSize;

    [JsonIgnore]
    public int WorkSetReserveLimit => Math.Max(SnapshotProofSlotCount, SnapshotProofSlotCount * Math.Max(1, WorkSetReserveMultiplier));

    [JsonIgnore]
    public int TotalPayoutSlotCount => WinnersListSize + 1;

    [JsonPropertyName("coinbase_tag")]
    public string CoinbaseTag { get; set; } = "Grid Pool";

    [JsonPropertyName("coinbase_uncondensed_outputs_enabled")]
    public bool CoinbaseUncondensedOutputsEnabled { get; set; } = false;

    [JsonPropertyName("compatibility_page_enabled")]
    public bool CompatibilityPageEnabled { get; set; } = true;

    [JsonPropertyName("compatibility_telemetry_path")]
    public string CompatibilityTelemetryPath { get; set; } = string.Empty;

    [JsonPropertyName("compatibility_stratum_public_host")]
    public string CompatibilityStratumPublicHost { get; set; } = string.Empty;

    [JsonPropertyName("compatibility_stratum_public_port")]
    public int CompatibilityStratumPublicPort { get; set; } = 0;

    [JsonPropertyName("compatibility_unsafe_override_phrase")]
    public string CompatibilityUnsafeOverridePhrase { get; set; } = "UNSAFE_FULL_COINBASE";

    [JsonPropertyName("genesis_round_start_utc")]
    public DateTime? GenesisRoundStartUtc { get; set; }

    [JsonPropertyName("node_mode")]
    public string NodeMode { get; set; } = "development";

    [JsonPropertyName("prime_id")]
    public uint PrimeId { get; set; } = 21;

    [JsonPropertyName("min_diff")]
    public ulong MinDiff { get; set; } = 1024;

    [JsonPropertyName("boot_network_id")]
    public string BootNetworkId { get; set; } = "mainnet-beta";

    [JsonPropertyName("boot_protocol_version")]
    public int BootProtocolVersion { get; set; } = BootProtocolVersions.ConsensusVersion;

    [JsonPropertyName("v22_activation_block_height")]
    public long V22ActivationBlockHeight { get; set; } = BootProtocolVersions.MainnetV22ActivationBlockHeight;

    [JsonPropertyName("admin_api_key")]
    public string? AdminApiKey { get; set; }

    [JsonPropertyName("enable_admin_api")]
    public bool EnableAdminApi { get; set; } = true;

    [JsonPropertyName("public_operator_diagnostics_enabled")]
    public bool PublicOperatorDiagnosticsEnabled { get; set; } = false;

    [JsonPropertyName("max_state_bundle_history")]
    public int MaxStateBundleHistory { get; set; } = 8;

    [JsonPropertyName("Datum_Port")]
    public int DatumPort { get; set; } = 3008;

    [JsonPropertyName("WebUI_Port_http")]
    public int WebUiPortHttp { get; set; } = 5000;

    [JsonPropertyName("WebUI_Port_https")]
    public int WebUiPortHttps { get; set; } = 0;

    [JsonPropertyName("enable_web_ui")]
    public bool EnableWebUi { get; set; } = true;

    [JsonPropertyName("enable_legacy_ui")]
    public bool EnableLegacyUi { get; set; } = true;

    [JsonPropertyName("peer_listener_port")]
    public int PeerListenerPort { get; set; } = 0;

    [JsonPropertyName("enable_peer_sync")]
    public bool EnablePeerSync { get; set; } = true;

    [JsonPropertyName("public_base_url")]
    public string PublicBaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("datum_public_host")]
    public string DatumPublicHost { get; set; } = string.Empty;

    [JsonPropertyName("datum_public_port")]
    public int DatumPublicPort { get; set; } = 0;

    [JsonPropertyName("bootstrap_peers")]
    public List<string> BootstrapPeers { get; set; } = [];

    [JsonPropertyName("peer_sync_interval_seconds")]
    public int PeerSyncIntervalSeconds { get; set; } = 15;

    [JsonPropertyName("peer_request_timeout_seconds")]
    public int PeerRequestTimeoutSeconds { get; set; } = 5;

    [JsonPropertyName("peer_session_send_timeout_seconds")]
    public int PeerSessionSendTimeoutSeconds { get; set; } = 5;

    [JsonPropertyName("peer_loop_stale_seconds")]
    public int PeerLoopStaleSeconds { get; set; } = 600;

    [JsonPropertyName("outbound_relay_stale_seconds")]
    public int OutboundRelayStaleSeconds { get; set; } = 300;

    [JsonPropertyName("max_peers")]
    public int MaxPeers { get; set; } = 64;

    [JsonPropertyName("peer_outbound_target")]
    public int PeerOutboundTarget { get; set; } = 16;

    [JsonPropertyName("peer_share_relay_target")]
    public int PeerShareRelayTarget { get; set; } = 32;

    [JsonPropertyName("peer_relay_parallelism")]
    public int PeerRelayParallelism { get; set; } = 16;

    [JsonPropertyName("peer_address_book_max_entries")]
    public int PeerAddressBookMaxEntries { get; set; } = 2048;

    [JsonPropertyName("peer_address_gossip_limit")]
    public int PeerAddressGossipLimit { get; set; } = 128;

    [JsonPropertyName("peer_failure_backoff_min_seconds")]
    public int PeerFailureBackoffMinSeconds { get; set; } = 30;

    [JsonPropertyName("peer_failure_backoff_max_seconds")]
    public int PeerFailureBackoffMaxSeconds { get; set; } = 1800;

    [JsonPropertyName("peer_tombstone_seconds")]
    public int PeerTombstoneSeconds { get; set; } = 86400;

    [JsonPropertyName("peer_allow_private_advertisements")]
    public bool PeerAllowPrivateAdvertisements { get; set; } = false;

    [JsonPropertyName("peer_prune_after_seconds")]
    public int PeerPruneAfterSeconds { get; set; } = 3600;

    [JsonPropertyName("peer_prune_failure_count")]
    public int PeerPruneFailureCount { get; set; } = 3;

    [JsonPropertyName("network_read_rate_limit_per_minute")]
    public int NetworkReadRateLimitPerMinute { get; set; } = 180;

    [JsonPropertyName("dashboard_read_rate_limit_per_minute")]
    public int DashboardReadRateLimitPerMinute { get; set; } = 180;

    [JsonPropertyName("peer_write_rate_limit_per_minute")]
    public int PeerWriteRateLimitPerMinute { get; set; } = 3000;

    [JsonPropertyName("peer_state_bundle_fetch_rate_limit_per_minute")]
    public int PeerStateBundleFetchRateLimitPerMinute { get; set; } = 12;

    [JsonPropertyName("enable_peer_persistent_sessions")]
    public bool EnablePeerPersistentSessions { get; set; } = true;

    [JsonPropertyName("peer_session_target")]
    public int PeerSessionTarget { get; set; } = 8;

    [JsonPropertyName("peer_session_connect_interval_seconds")]
    public int PeerSessionConnectIntervalSeconds { get; set; } = 15;

    [JsonPropertyName("peer_session_idle_timeout_seconds")]
    public int PeerSessionIdleTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("peer_session_max_frame_bytes")]
    public int PeerSessionMaxFrameBytes { get; set; } = 262144;

    [JsonPropertyName("peer_session_clock_skew_seconds")]
    public int PeerSessionClockSkewSeconds { get; set; } = 900;

    [JsonPropertyName("enable_peer_tip_stale_protection")]
    public bool EnablePeerTipStaleProtection { get; set; } = false;

    [JsonPropertyName("peer_tip_grace_seconds")]
    public int PeerTipGraceSeconds { get; set; } = 3;

    [JsonPropertyName("peer_tip_max_header_age_seconds")]
    public int PeerTipMaxHeaderAgeSeconds { get; set; } = 86400;

    [JsonPropertyName("peer_tip_max_future_seconds")]
    public int PeerTipMaxFutureSeconds { get; set; } = 7200;

    [JsonPropertyName("enable_peer_udp_fast_relay")]
    public bool EnablePeerUdpFastRelay { get; set; } = true;

    [JsonPropertyName("peer_udp_bind_port")]
    public int PeerUdpBindPort { get; set; } = 5001;

    [JsonPropertyName("peer_udp_port")]
    public int PeerUdpPort { get; set; } = 5001;

    [JsonPropertyName("peer_udp_public_host")]
    public string PeerUdpPublicHost { get; set; } = string.Empty;

    [JsonPropertyName("peer_udp_max_datagram_bytes")]
    public int PeerUdpMaxDatagramBytes { get; set; } = 1200;

    [JsonPropertyName("peer_udp_replay_window")]
    public int PeerUdpReplayWindow { get; set; } = 4096;

    [JsonPropertyName("peer_relay_latency_probe_all_transports")]
    public bool PeerRelayLatencyProbeAllTransports { get; set; } = false;

    [JsonPropertyName("enable_pulse_proofs")]
    public bool EnablePulseProofs { get; set; } = true;

    [JsonPropertyName("pause_mining_on_outbound_relay_stale")]
    // Deprecated compatibility key. Relay staleness is reported as health data;
    // it must not make DATUM coinbaser requests fail closed into solo fallback.
    public bool PauseMiningOnOutboundRelayStale { get; set; } = false;

    [JsonPropertyName("pulse_min_difficulty")]
    public double PulseMinDifficulty { get; set; } = 1d;

    [JsonPropertyName("pulse_target_interval_seconds")]
    public int PulseTargetIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("pulse_relay_ttl")]
    public int PulseRelayTtl { get; set; } = 1;

    [JsonPropertyName("pulse_max_per_peer_per_minute")]
    public int PulseMaxPerPeerPerMinute { get; set; } = 2;

    [JsonPropertyName("pulse_max_per_source_address_per_minute")]
    public int PulseMaxPerSourceAddressPerMinute { get; set; } = 2;

    [JsonPropertyName("enable_optimistic_share_relay")]
    public bool EnableOptimisticShareRelay { get; set; } = false;

    [JsonPropertyName("min_optimistic_relay_difficulty")]
    public double MinOptimisticRelayDifficulty { get; set; } = 1d;

    [JsonPropertyName("public_telemetry_opt_in")]
    public bool PublicTelemetryOptIn { get; set; } = false;

    [JsonPropertyName("public_node_display_name")]
    public string PublicNodeDisplayName { get; set; } = string.Empty;

    [JsonPropertyName("public_node_region")]
    public string PublicNodeRegion { get; set; } = string.Empty;

    [JsonPropertyName("public_node_role")]
    public string PublicNodeRole { get; set; } = string.Empty;

    [JsonPropertyName("public_node_approx_lat")]
    public double? PublicNodeApproxLatitude { get; set; }

    [JsonPropertyName("public_node_approx_lon")]
    public double? PublicNodeApproxLongitude { get; set; }

    [JsonPropertyName("mining_api_share_rate_limit_per_minute")]
    public int MiningApiShareRateLimitPerMinute { get; set; } = 120;

    [JsonPropertyName("local_adapter_token_file")]
    public string LocalAdapterTokenFile { get; set; } = "data/local-adapter.token";

    [JsonPropertyName("local_adapter_telemetry_max_batch_size")]
    public int LocalAdapterTelemetryMaxBatchSize { get; set; } = 1000;

    [JsonPropertyName("local_mining_api_poll_seconds")]
    public int LocalMiningApiPollSeconds { get; set; } = 15;

    [JsonPropertyName("local_datum_api_url")]
    public string LocalDatumApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("local_sv2_api_url")]
    public string LocalSv2ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("admin_rate_limit_per_minute")]
    public int AdminRateLimitPerMinute { get; set; } = 12;

    [JsonPropertyName("max_share_request_bytes")]
    public int MaxShareRequestBytes { get; set; } = 262144;

    [JsonPropertyName("datum_max_connections")]
    public int DatumMaxConnections { get; set; } = 32;

    [JsonPropertyName("datum_read_timeout_seconds")]
    public int DatumReadTimeoutSeconds { get; set; } = 15;

    [JsonPropertyName("max_coinbase_hex_chars")]
    public int MaxCoinbaseHexChars { get; set; } = 100000;

    [JsonPropertyName("max_merkle_path_entries")]
    public int MaxMerklePathEntries { get; set; } = 64;

    [JsonPropertyName("testing_round_reset_mode")]
    public string TestingRoundResetMode { get; set; } = "none";

    [JsonPropertyName("testing_round_reset_low_nibble_threshold")]
    public int TestingRoundResetLowNibbleThreshold { get; set; } = 0;

    [JsonPropertyName("hashrate_sample_interval_seconds")]
    public int HashrateSampleIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("hashrate_local_window_seconds")]
    public int HashrateLocalWindowSeconds { get; set; } = 1800;

    [JsonPropertyName("local_datum_miner_summary_limit")]
    public int LocalDatumMinerSummaryLimit { get; set; } = 50;

    [JsonPropertyName("local_datum_hashrate_per_address_max_samples")]
    public int LocalDatumHashratePerAddressMaxSamples { get; set; } = 512;

    [JsonPropertyName("local_datum_hashrate_max_addresses")]
    public int LocalDatumHashrateMaxAddresses { get; set; } = 5000;

    [JsonPropertyName("local_datum_hashrate_rollup_interval_seconds")]
    public int LocalDatumHashrateRollupIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("local_datum_hashrate_rollup_retention_days")]
    public int LocalDatumHashrateRollupRetentionDays { get; set; } = 7;

    [JsonPropertyName("local_datum_hashrate_rollup_max_points")]
    public int LocalDatumHashrateRollupMaxPoints { get; set; } = 500000;

    [JsonPropertyName("max_accepted_share_telemetry_entries")]
    public int MaxAcceptedShareTelemetryEntries { get; set; } = 20000;

    [JsonPropertyName("hashrate_sample_retention_days")]
    public int HashrateSampleRetentionDays { get; set; } = 60;

    [JsonPropertyName("accepted_share_telemetry_retention_hours")]
    public int AcceptedShareTelemetryRetentionHours { get; set; } = 2;

    [JsonPropertyName("share_diagnostic_retention_hours")]
    public int ShareDiagnosticRetentionHours { get; set; } = 12;

    [JsonPropertyName("network_event_retention_hours")]
    public int NetworkEventRetentionHours { get; set; } = 168;

    [JsonPropertyName("datum_share_response_slow_ms")]
    public int DatumShareResponseSlowMs { get; set; } = 500;

    [JsonPropertyName("datum_share_response_accepted_sample_every")]
    public int DatumShareResponseAcceptedSampleEvery { get; set; } = 100;

    [JsonPropertyName("datum_low_diff_fast_accept_enabled")]
    public bool DatumLowDiffFastAcceptEnabled { get; set; } = true;

    [JsonPropertyName("datum_low_diff_courtesy_validate_every")]
    public int DatumLowDiffCourtesyValidateEvery { get; set; } = 256;

    [JsonPropertyName("datum_low_diff_courtesy_validate_seconds")]
    public int DatumLowDiffCourtesyValidateSeconds { get; set; } = 60;

    [JsonPropertyName("trusted_forwarded_proxy_ranges")]
    public List<string> TrustedForwardedProxyRanges { get; set; } = [];

    [JsonPropertyName("stale_datum_payout_mismatch_threshold")]
    public int StaleDatumPayoutMismatchThreshold { get; set; } = 4;

    [JsonPropertyName("stale_datum_disconnect_min_seconds")]
    public int StaleDatumDisconnectMinSeconds { get; set; } = 20;

    [JsonPropertyName("stale_datum_disconnect_cooldown_seconds")]
    public int StaleDatumDisconnectCooldownSeconds { get; set; } = 60;

    [JsonPropertyName("stale_datum_force_disconnect_enabled")]
    public bool StaleDatumForceDisconnectEnabled { get; set; } = false;

    [JsonPropertyName("stale_datum_refresh_interval_seconds")]
    public int StaleDatumRefreshIntervalSeconds { get; set; } = 10;

    [JsonPropertyName("datum_keepalive_interval_seconds")]
    public int DatumKeepaliveIntervalSeconds { get; set; } = 30;

    [JsonPropertyName("bitcoin_zmq_endpoint")]
    public string BitcoinZmqEndpoint { get; set; } = "tcp://127.0.0.1:28332";

    [JsonPropertyName("bitcoin_zmq_rawblock_endpoint")]
    public string BitcoinZmqRawBlockEndpoint { get; set; } = "tcp://127.0.0.1:28333";

    [JsonPropertyName("bitcoin_rpc_url")]
    public string BitcoinRpcUrl { get; set; } = string.Empty;

    [JsonPropertyName("bitcoin_rpc_username")]
    public string BitcoinRpcUsername { get; set; } = string.Empty;

    [JsonPropertyName("bitcoin_rpc_password")]
    public string BitcoinRpcPassword { get; set; } = string.Empty;

    [JsonPropertyName("bitcoin_rpc_cookie_file")]
    public string BitcoinRpcCookieFile { get; set; } = string.Empty;

    [JsonPropertyName("bitcoin_rpc_poll_interval_seconds")]
    public int BitcoinRpcPollIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("bitcoin_rpc_timeout_seconds")]
    public int BitcoinRpcTimeoutSeconds { get; set; } = 3;

    [JsonPropertyName("bitcoin_rpc_lag_grace_seconds")]
    public int BitcoinRpcLagGraceSeconds { get; set; } = 5;

    [JsonPropertyName("stratum_v1_proxy_host")]
    public string StratumV1ProxyHost { get; set; } = string.Empty;

    [JsonPropertyName("stratum_v1_proxy_port")]
    public int StratumV1ProxyPort { get; set; } = 0;

    public bool IsSetupComplete() =>
        !string.IsNullOrWhiteSpace(PoolPayoutScript) &&
        BitcoinScript.TryAddressToScriptPubKey(PoolPayoutScript, BitcoinNetwork, out _);

    [JsonIgnore]
    public bool TestingRoundResetEnabled =>
        string.Equals(TestingRoundResetMode, "block_hash_low_nibble", StringComparison.OrdinalIgnoreCase) &&
        TestingRoundResetLowNibbleThreshold > 0;
}
