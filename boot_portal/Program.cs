using System.Buffers.Binary;
using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using boot_portal;
using boot_portal.Models;
using boot_portal.HostedServices;
using boot_portal.Services;
using boot_portal.Utils;
using boot_portal.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using NSec.Cryptography;
using Microsoft.AspNetCore.SignalR;


// =================================================================================
// 1. MAIN PROGRAM ENTRY POINT
// =================================================================================
// This class is responsible for parsing command-line arguments, managing the
// server's primary cryptographic key, and starting the TCP server.
// =================================================================================
// JSON configuration class for boot_portal_config.json

public static class CryptoUtils
{
    //TODO: This class and function are probably unnecessary, and could just be integrated into the other code.  Idk.
    public static byte[] ComputeSharedSecretForCryptoBox(byte[] serverPrivateKey, byte[] clientPublicKey)
    {
        // Step 1: X25519 key agreement
        byte[] rawSharedSecret = new byte[LibSodium.CryptoBox.SharedKeyLen];
        LibSodium.CryptoBox.CalculateSharedKey(rawSharedSecret, clientPublicKey, serverPrivateKey);
        return rawSharedSecret;
    }
}

//This class stores primary configurations like the payout address. 
//It's written on the assumption that each user will run their own boot_portal, and not rely on other people's portals
//  (though that could be done, it's just not a preferable outcome)
public class PoolConfig
{
    [JsonPropertyName("NotificationSource")]
    public string NotificationSource { get; set; } = "MempoolSpace";

    [JsonPropertyName("bitcoin_notification_mode")]
    public string BitcoinNotificationMode { get; set; } = string.Empty;

    [JsonPropertyName("bitcoin_network")]
    public string BitcoinNetwork { get; set; } = BitcoinScript.Mainnet;

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

    [JsonPropertyName("max_state_bundle_history")]
    public int MaxStateBundleHistory { get; set; } = 8;

    [JsonPropertyName("Datum_Port")]
    public int DatumPort { get; set; } = 3008;

    [JsonPropertyName("WebUI_Port_http")]
    public int WebUiPortHttp { get; set; } = 5000;

    [JsonPropertyName("WebUI_Port_https")]
    public int WebUiPortHttps { get; set; } = 0;

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

    [JsonIgnore]
    public bool TestingRoundResetEnabled =>
        string.Equals(TestingRoundResetMode, "block_hash_low_nibble", StringComparison.OrdinalIgnoreCase) &&
        TestingRoundResetLowNibbleThreshold > 0;
}

//This just stores the server's primary, long term keys.  These get loaded from a config file or from the command line on startup
//If they change, then the client's won't be able to reach the server until this key is updated on each one manually.
// TODO: Do I really need a separate class to store these strings?  Should they be proper LibSodium style Span<T>'s instead for security?
public class ServerConfig
{
    [JsonPropertyName("ed25519_private_key")]
    public string? Ed25519PrivateKey { get; set; }

    [JsonPropertyName("x25519_private_key")]
    public string? X25519PrivateKey { get; set; }
}

public class Program
{
    public const string DefaultPublicSeedEndpoint = "https://main.gridpool.net";
    public static readonly string[] DefaultPublicSeedEndpoints =
        ["https://main.gridpool.net", "https://dallas.gridpool.net", "https://detroit.gridpool.net"];
    // TODO: I should optionally load this from config, instead of hard-coded like this.
    private static int DatumPort = 3008;  //Defaults to 3008.  Should get set by config file.
    public static ulong BLOCK_REWARD = 312_500_000;  //TODO: Need to detect this from the blockchain, so it gracefully handles the next epoch
    public static int TeamSize = 300;

    public static async Task Main(string[] args)
    {
        var rootCommand = new RootCommand("DATUM Prime C# Server");
        var ed25519PrivateKeyOption = new Option<string?>(
            name: "--ed25519-private-key",
            description: "The Base64 encoded Ed25519 private key for the server. If not provided, loads from config or generates a new key pair."
        );
        var x25519PrivateKeyOption = new Option<string?>(
            name: "--x25519-private-key",
            description: "The Base64 encoded X25519 private key for the server. If not provided, loads from config or generates a new key pair."
        );
        rootCommand.AddOption(ed25519PrivateKeyOption);
        rootCommand.AddOption(x25519PrivateKeyOption);

        rootCommand.SetHandler(async (ed25519PrivateKeyBase64, x25519PrivateKeyBase64) =>
        {
            Key ed25519Key;
            Key x25519Key;
            bool keysGenerated = false;
            bool configCanBeSafelyRewritten = false;
            JsonObject? writableConfigJsonObject = null;

            var signatureAlgorithm = SignatureAlgorithm.Ed25519;
            var keyExchangeAlgorithm = KeyAgreementAlgorithm.X25519;

            // Load config from boot_portal_config.json and an optional adjacent local override if it exists
            ServerConfig config = new ServerConfig();
            string configFilePath = BootPortalPaths.ConfigFilePath;
            string localConfigFilePath = BootPortalPaths.LocalConfigFilePath;
            bool localConfigPathExplicit = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOOT_PORTAL_LOCAL_CONFIG_PATH"));
            bool useLocalConfigOverlay = !string.Equals(configFilePath, localConfigFilePath, StringComparison.OrdinalIgnoreCase) &&
                                         (localConfigPathExplicit || File.Exists(localConfigFilePath));
            JsonObject? baseConfigJsonObject = null;
            JsonObject? localConfigJsonObject = null;

            if (File.Exists(configFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(configFilePath);
                    baseConfigJsonObject = JsonNode.Parse(json) as JsonObject;
                    configCanBeSafelyRewritten = baseConfigJsonObject != null;
                    Console.WriteLine($"✅ Loaded config from {configFilePath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to load {configFilePath}: {ex.Message}. Using default or command-line keys.");
                    configCanBeSafelyRewritten = false;
                }
            }
            else
            {
                configCanBeSafelyRewritten = true;
            }

            if (useLocalConfigOverlay && File.Exists(localConfigFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(localConfigFilePath);
                    localConfigJsonObject = JsonNode.Parse(json) as JsonObject;
                    if (localConfigJsonObject == null)
                    {
                        Console.WriteLine($"⚠️ Failed to parse local config from {localConfigFilePath}. Ignoring local override.");
                        useLocalConfigOverlay = false;
                    }
                    else
                    {
                        Console.WriteLine($"✅ Loaded local config override from {localConfigFilePath}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Failed to load local config override from {localConfigFilePath}: {ex.Message}. Ignoring local override.");
                    useLocalConfigOverlay = false;
                }
            }

            JsonObject? effectiveConfigJsonObject = MergeJsonObjects(baseConfigJsonObject, useLocalConfigOverlay ? localConfigJsonObject : null);
            config = effectiveConfigJsonObject?.Deserialize<ServerConfig>() ?? new ServerConfig();
            writableConfigJsonObject = useLocalConfigOverlay
                ? (localConfigJsonObject ?? new JsonObject())
                : (baseConfigJsonObject ?? new JsonObject());

            // Handle Ed25519 key
            //TODO: These load as NSec Key objects, but I don't really use the NSec library anywhere else.
            //  So ideally I'd convert these to whatever secure Span storage LibSodium uses natively, and skip the awkward ".Export()" calls everywhere.
            string? ed25519KeySource = ed25519PrivateKeyBase64 ?? config.Ed25519PrivateKey;
            if (!string.IsNullOrEmpty(ed25519KeySource))
            {
                try
                {
                    ReadOnlySpan<byte> privateKey = Convert.FromBase64String(ed25519KeySource);
                    var privateKeyBytes = Convert.FromBase64String(ed25519KeySource);
                    ed25519Key = Key.Import(signatureAlgorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                    Console.WriteLine("✅ Successfully loaded Ed25519 server key.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to load Ed25519 private key: {ex.Message}. Generating new key.");
                    ed25519Key = Key.Create(signatureAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                    config.Ed25519PrivateKey = Convert.ToBase64String(ed25519Key.Export(KeyBlobFormat.RawPrivateKey));
                    keysGenerated = true;
                }
            }
            else
            {
                ed25519Key = Key.Create(signatureAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                config.Ed25519PrivateKey = Convert.ToBase64String(ed25519Key.Export(KeyBlobFormat.RawPrivateKey));
                Console.WriteLine("⚠️ No Ed25519 private key provided. Generated a new long term Ed25519 key pair.");
                keysGenerated = true;
            }

            // Handle X25519 key
            string? x25519KeySource = x25519PrivateKeyBase64 ?? config.X25519PrivateKey;
            if (!string.IsNullOrEmpty(x25519KeySource))
            {
                try
                {
                    var privateKeyBytes = Convert.FromBase64String(x25519KeySource);
                    //Key.Create(signatureAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                    x25519Key = Key.Import(keyExchangeAlgorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                    Console.WriteLine("✅ Successfully loaded X25519 server key.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to load X25519 private key: {ex.Message}. Generating new key.");
                    x25519Key = Key.Create(keyExchangeAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                    config.X25519PrivateKey = Convert.ToBase64String(x25519Key.Export(KeyBlobFormat.RawPrivateKey));
                    keysGenerated = true;
                }
            }
            else
            {
                x25519Key = Key.Create(keyExchangeAlgorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                config.X25519PrivateKey = Convert.ToBase64String(x25519Key.Export(KeyBlobFormat.RawPrivateKey));
                Console.WriteLine("⚠️ No X25519 private key provided. Generated a new long term X25519 key pair.");
                keysGenerated = true;
            }

            // Save config if keys were generated or file doesn't exist
            if (keysGenerated || !File.Exists(configFilePath))
            {
                try
                {
                    string writableConfigPath = useLocalConfigOverlay ? localConfigFilePath : configFilePath;
                    bool writableTargetExists = File.Exists(writableConfigPath);

                    if (!configCanBeSafelyRewritten && File.Exists(configFilePath) && !useLocalConfigOverlay)
                    {
                        Console.WriteLine($"⚠️ Skipping config rewrite for {configFilePath} because the existing JSON could not be parsed. Generated keys are only in memory for this run.");
                    }
                    else
                    {
                        writableConfigJsonObject ??= new JsonObject();
                        writableConfigJsonObject["ed25519_private_key"] = config.Ed25519PrivateKey;
                        writableConfigJsonObject["x25519_private_key"] = config.X25519PrivateKey;

                        if (!writableTargetExists || useLocalConfigOverlay || configCanBeSafelyRewritten)
                        {
                            BootPortalPaths.EnsureParentDirectory(writableConfigPath);
                            var json = writableConfigJsonObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                            await File.WriteAllTextAsync(writableConfigPath, json);
                            Console.WriteLine($"✅ Saved keys to {writableConfigPath}");
                        }
                        else
                        {
                            Console.WriteLine($"⚠️ Skipping config rewrite for {writableConfigPath} because the existing JSON could not be parsed. Generated keys are only in memory for this run.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to save generated keys: {ex.Message}");
                }
            }

            string privateConfigPath = useLocalConfigOverlay ? localConfigFilePath : configFilePath;
            if (!OperatingSystem.IsWindows() && File.Exists(privateConfigPath))
            {
                try
                {
                    File.SetUnixFileMode(
                        privateConfigPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"⚠️ Could not restrict identity config permissions for {privateConfigPath}: {ex.Message}");
                }
            }

            // Export public keys
            var ed25519PubKeyBytes = ed25519Key.PublicKey.Export(KeyBlobFormat.RawPublicKey); // 32 bytes
            var x25519PubKeyBytes = x25519Key.PublicKey.Export(KeyBlobFormat.RawPublicKey); // 32 bytes

            // Concatenate Ed25519 and X25519 public keys
            var combinedPubKey = new byte[ed25519PubKeyBytes.Length + x25519PubKeyBytes.Length];
            Buffer.BlockCopy(ed25519PubKeyBytes, 0, combinedPubKey, 0, ed25519PubKeyBytes.Length);
            Buffer.BlockCopy(x25519PubKeyBytes, 0, combinedPubKey, ed25519PubKeyBytes.Length, x25519PubKeyBytes.Length);

            // Convert to hex for client
            var combinedPubKeyHex = Convert.ToHexString(combinedPubKey).ToLower(); // 128 hex characters

            //Now load or setup the pool config options, like default payout address and coinbase tag
            PoolConfig _poolConfig = LoadPoolConfig(configFilePath, useLocalConfigOverlay ? localConfigFilePath : null);
            Program.TeamSize = _poolConfig.TotalPayoutSlotCount;
            DatumPort = _poolConfig.DatumPort;

            Console.WriteLine("\n====================== IMPORTANT ======================");
            Console.WriteLine("Copy this combined public key (Ed25519 + X25519, hex-encoded) into your DATUM Gateway's config.json:");
            Console.WriteLine($"🔑 Server Public Key (Hex): {combinedPubKeyHex}");
            Console.WriteLine("Private identity keys are stored in the configured local file and are never printed.");
            Console.WriteLine("=======================================================\n");

            //UI Server stuff:
            var builder = WebApplication.CreateBuilder(args);
            builder.Configuration.AddJsonFile(configFilePath, optional: false, reloadOnChange: true);
            if (useLocalConfigOverlay)
            {
                builder.Configuration.AddJsonFile(localConfigFilePath, optional: true, reloadOnChange: true);
            }

            var listenUrls = new List<string>();
            if (_poolConfig.WebUiPortHttp > 0)
            {
                listenUrls.Add($"http://0.0.0.0:{_poolConfig.WebUiPortHttp}");
            }

            if (_poolConfig.WebUiPortHttps > 0)
            {
                listenUrls.Add($"https://0.0.0.0:{_poolConfig.WebUiPortHttps}");
            }

            if (_poolConfig.PeerListenerPort > 0 &&
                _poolConfig.PeerListenerPort != _poolConfig.WebUiPortHttp &&
                _poolConfig.PeerListenerPort != _poolConfig.WebUiPortHttps)
            {
                listenUrls.Add($"http://0.0.0.0:{_poolConfig.PeerListenerPort}");
            }

            if (listenUrls.Count == 0)
            {
                throw new InvalidOperationException("At least one WebUI port must be configured.");
            }

            builder.WebHost.UseUrls(listenUrls.ToArray());
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = Math.Max(32_768L, _poolConfig.MaxShareRequestBytes);
            });

            builder.Services.AddRazorPages(); // For serving simple HTML pages
            builder.Services.AddControllers();
            builder.Services.AddSignalR();    // For real-time updates
            builder.Services.AddSingleton(_poolConfig);
            builder.Services.AddSingleton<BitcoinNotificationHealth>();
            builder.Services.AddSingleton(new BootPeerIdentity(ed25519Key, x25519Key));
            builder.Services.AddSingleton<BootPeerLoopHealth>();
            builder.Services.AddSingleton<BootShareVerifier>();
            builder.Services.AddSingleton<BootProtocolStateService>();
            builder.Services.AddSingleton<LocalMiningAdapterAuth>();
            builder.Services.AddSingleton<BootPeerSessionManager>();
            builder.Services.AddSingleton<BootPeerUdpRelayService>();
            builder.Services.AddSingleton<BootNatPortMappingService>();
            builder.Services.AddHttpClient("BootPeerClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(2, _poolConfig.PeerRequestTimeoutSeconds));
            });
            builder.Services.AddHttpClient("ReachabilityProbeClient", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(2, _poolConfig.PeerRequestTimeoutSeconds));
            }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = BootNetworkController.ConnectToPublicHostAsync
            });
            builder.Services.AddHttpClient<BitcoinRpcClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _poolConfig.BitcoinRpcTimeoutSeconds));
            });
            builder.Logging.AddFilter(
                "System.Net.Http.HttpClient.BitcoinRpcClient",
                LogLevel.Warning);
            builder.Services.AddTransient<IBitcoinRpcClient>(serviceProvider =>
                serviceProvider.GetRequiredService<BitcoinRpcClient>());
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = async (context, token) =>
                {
                    context.HttpContext.Response.Headers["Retry-After"] = "60";
                    if (!context.HttpContext.Response.HasStarted)
                    {
                        context.HttpContext.Response.ContentType = "application/json";
                        await context.HttpContext.Response.WriteAsJsonAsync(
                            new { status = "rejected", reason = "Rate limit exceeded" },
                            cancellationToken: token);
                    }
                };

                options.AddPolicy("network-read", context =>
                    CreateRateLimitPartition(context, _poolConfig, "network-read", _poolConfig.NetworkReadRateLimitPerMinute));
                options.AddPolicy("peer-write", context =>
                    CreateRateLimitPartition(context, _poolConfig, "peer-write", _poolConfig.PeerWriteRateLimitPerMinute));
                options.AddPolicy("mining-write", context =>
                    CreateRateLimitPartition(context, _poolConfig, "mining-write", _poolConfig.MiningApiShareRateLimitPerMinute));
                options.AddPolicy("admin-write", context =>
                    CreateRateLimitPartition(context, _poolConfig, "admin-write", _poolConfig.AdminRateLimitPerMinute));
            });
            
            // Start your background services
            //builder.Services.AddHostedService<BitcoinZmqSubscriber>();
            // *** START CONFIGURABLE SERVICE SECTION ***

            string notificationMode = BitcoinNotificationModes.Resolve(_poolConfig);
            if (notificationMode == BitcoinNotificationModes.AttachedNode)
            {
                builder.Services.AddHostedService<BitcoinZmqSubscriber>();
                builder.Services.AddHostedService<BitcoinRpcReconciliationService>();
                Console.WriteLine("Block notification mode set to attached-node (ZMQ + RPC reconciliation)");
            }
            else if (notificationMode == BitcoinNotificationModes.ExternalFallback)
            {
                builder.Services.AddHostedService<MempoolSpaceSocketSubscriber>();
                Console.WriteLine("Block notification mode set to external-fallback (Mempool.Space)");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unknown bitcoin_notification_mode '{notificationMode}'. Expected 'attached-node' or 'external-fallback'.");
            }

            // *** END CONFIGURABLE SERVICE SECTION ***
            builder.Services.AddHostedService<DatumServer>(serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<DatumServer>>();
                var hubContext = serviceProvider.GetRequiredService<IHubContext<PoolStatsHub>>();
                var stateService = serviceProvider.GetRequiredService<BootProtocolStateService>();
                // The 'serviceProvider' allows you to get other services if needed,
                // but here we just use the local variables from Program.cs

                // We pass in all the necessary constructor arguments here:
                return new DatumServer(
                    IPAddress.Any,
                    DatumPort,
                    ed25519Key,
                    x25519Key,
                    _poolConfig,
                    stateService,
                    hubContext,
                    logger);
            });
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BootPeerSessionManager>());
            builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BootPeerUdpRelayService>());
            builder.Services.AddHostedService<BootPeerSyncService>();
            builder.Services.AddHostedService<LocalMiningSourcePoller>();

            var app = builder.Build();
            _ = app.Services.GetRequiredService<LocalMiningAdapterAuth>();

            // 2. Configure the web app
            app.Use(async (context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["Content-Security-Policy"] =
                        "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; " +
                        "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
                        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                        "font-src 'self' https://fonts.gstatic.com data:; img-src 'self' data:; " +
                        "connect-src 'self' ws: wss: https://mempool.space";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    return Task.CompletedTask;
                });

                if (IsPeerOnlyListenerRequest(context, _poolConfig) &&
                    !IsAllowedPeerOnlyPath(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        status = "not_found",
                        reason = "This listener exposes GridPool peer protocol endpoints only."
                    });
                    return;
                }

                await next();
            });

            app.UseStaticFiles(); // Serve static files like CSS, JS, images
            app.UseWebSockets();
            app.UseRouting();
            app.UseRateLimiter();
            app.MapRazorPages(); // Use a simple page system
            app.MapControllers();

            // 3. Tell the app where your SignalR Hub lives
            app.MapHub<PoolStatsHub>("/poolStatsHub"); // This is the URL your JS will use
            
            // Runs and blocks this thread while all other services run
            // Graceful shutdown is handled by the "AddHostedService" call above
            await app.RunAsync();

            // TODO: Start the Stratum V1 and V2 servers as well, or with .config options just start the chosen servers.
            
            // TODO: Also start the peer to peer node so we can actually connect to the boot-protocol network

            Console.WriteLine("All services stopped.");
        }, ed25519PrivateKeyOption, x25519PrivateKeyOption);

        await rootCommand.InvokeAsync(args);
    }

    private static bool IsPeerOnlyListenerRequest(HttpContext context, PoolConfig config)
    {
        return config.PeerListenerPort > 0 &&
            context.Connection.LocalPort == config.PeerListenerPort &&
            config.PeerListenerPort != config.WebUiPortHttp &&
            config.PeerListenerPort != config.WebUiPortHttps;
    }

    private static bool IsAllowedPeerOnlyPath(PathString path)
    {
        string value = path.Value ?? string.Empty;
        return value.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/api/peer/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/network/summary", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/network/peer-addresses", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/network/reachability-test", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/network/reachability-ack", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("/api/network/state/", StringComparison.OrdinalIgnoreCase);
    }

    private static PoolConfig LoadPoolConfig(string configPath, string? localConfigPath = null)
    {
        try
        {
            JsonObject? baseConfig = null;
            JsonObject? localConfig = null;

            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                baseConfig = JsonNode.Parse(json) as JsonObject;
            }

            if (!string.IsNullOrWhiteSpace(localConfigPath) && File.Exists(localConfigPath))
            {
                string json = File.ReadAllText(localConfigPath);
                localConfig = JsonNode.Parse(json) as JsonObject;
            }

            JsonObject? effectiveConfig = MergeJsonObjects(baseConfig, localConfig);
            if (effectiveConfig != null)
            {
                var config = effectiveConfig.Deserialize<PoolConfig>();
                if (config != null)
                {
                    ApplyPoolConfigDefaults(config);
                    PoolConfigValidator.ValidateOrThrow(config);
                    Console.WriteLine(
                        string.IsNullOrWhiteSpace(localConfigPath) || !File.Exists(localConfigPath)
                            ? $"🔧 Loaded config from {configPath}"
                            : $"🔧 Loaded config from {configPath} with local override {localConfigPath}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to load config from {configPath}: {ex.Message}");
        }
        Console.WriteLine($"🔧 Using default pool config");
        var fallbackConfig = new PoolConfig();
        ApplyPoolConfigDefaults(fallbackConfig);
        PoolConfigValidator.ValidateOrThrow(fallbackConfig);
        return fallbackConfig;
    }

    private static JsonObject? MergeJsonObjects(JsonObject? baseConfig, JsonObject? overrideConfig)
    {
        if (baseConfig == null && overrideConfig == null)
        {
            return null;
        }

        JsonObject result = baseConfig?.DeepClone() as JsonObject ?? new JsonObject();
        if (overrideConfig == null)
        {
            return result;
        }

        MergeInto(result, overrideConfig);
        return result;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach ((string key, JsonNode? value) in source)
        {
            if (value is JsonObject sourceObject)
            {
                if (target[key] is JsonObject targetObject)
                {
                    MergeInto(targetObject, sourceObject);
                }
                else
                {
                    target[key] = sourceObject.DeepClone();
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static void ApplyPoolConfigDefaults(PoolConfig config)
    {
        config.CoinbaseTag ??= string.Empty;
        config.NotificationSource = string.IsNullOrWhiteSpace(config.NotificationSource)
            ? "MempoolSpace"
            : config.NotificationSource.Trim();
        config.BitcoinNotificationMode = string.IsNullOrWhiteSpace(config.BitcoinNotificationMode)
            ? string.Empty
            : config.BitcoinNotificationMode.Trim().ToLowerInvariant();
        config.BitcoinRpcUrl = config.BitcoinRpcUrl?.Trim() ?? string.Empty;
        config.BitcoinRpcUsername = config.BitcoinRpcUsername?.Trim() ?? string.Empty;
        config.BitcoinRpcCookieFile = config.BitcoinRpcCookieFile?.Trim() ?? string.Empty;
        config.NodeMode = string.IsNullOrWhiteSpace(config.NodeMode)
            ? "development"
            : config.NodeMode.Trim().ToLowerInvariant();
        config.BootNetworkId = string.IsNullOrWhiteSpace(config.BootNetworkId)
            ? "mainnet-beta"
            : config.BootNetworkId.Trim();

        config.TestingRoundResetMode = string.IsNullOrWhiteSpace(config.TestingRoundResetMode)
            ? "none"
            : config.TestingRoundResetMode.Trim().ToLowerInvariant();

        if (!string.Equals(config.TestingRoundResetMode, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.TestingRoundResetMode, "block_hash_low_nibble", StringComparison.OrdinalIgnoreCase))
        {
            config.TestingRoundResetMode = "none";
        }

        config.TestingRoundResetLowNibbleThreshold = Math.Clamp(config.TestingRoundResetLowNibbleThreshold, 0, 16);

        config.BootstrapPeers ??= [];
        config.BootstrapPeers = config.BootstrapPeers
            .Where(peer => !string.IsNullOrWhiteSpace(peer))
            .Select(peer => peer.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.TrustedForwardedProxyRanges ??= [];
        config.TrustedForwardedProxyRanges = config.TrustedForwardedProxyRanges
            .Where(range => !string.IsNullOrWhiteSpace(range))
            .Select(range => range.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.DatumKeepaliveIntervalSeconds = Math.Clamp(config.DatumKeepaliveIntervalSeconds, 0, 300);
        config.PeerSessionTarget = Math.Clamp(config.PeerSessionTarget, 1, Math.Max(1, config.PeerShareRelayTarget));
        config.PeerSessionConnectIntervalSeconds = Math.Clamp(config.PeerSessionConnectIntervalSeconds, 5, 300);
        config.PeerSessionIdleTimeoutSeconds = Math.Clamp(config.PeerSessionIdleTimeoutSeconds, 30, 3600);
        config.PeerSessionMaxFrameBytes = Math.Clamp(config.PeerSessionMaxFrameBytes, 4096, Math.Max(4096, config.MaxShareRequestBytes));
        config.PeerSessionClockSkewSeconds = Math.Clamp(config.PeerSessionClockSkewSeconds, 60, 86400);
        config.PeerListenerPort = Math.Clamp(config.PeerListenerPort, 0, 65535);
        config.PeerUdpBindPort = Math.Clamp(config.PeerUdpBindPort, 0, 65535);
        config.PeerUdpPort = Math.Clamp(config.PeerUdpPort, 1, 65535);
        config.PeerUdpMaxDatagramBytes = Math.Clamp(config.PeerUdpMaxDatagramBytes, 512, 65507);
        config.PeerUdpReplayWindow = Math.Clamp(config.PeerUdpReplayWindow, 128, 1_000_000);
        config.BitcoinZmqEndpoint = string.IsNullOrWhiteSpace(config.BitcoinZmqEndpoint)
            ? "tcp://127.0.0.1:28332"
            : config.BitcoinZmqEndpoint.Trim();

        if (string.Equals(config.BootNetworkId, "mainnet-beta", StringComparison.OrdinalIgnoreCase) &&
            config.BootstrapPeers.Count == 0)
        {
            config.BootstrapPeers.AddRange(DefaultPublicSeedEndpoints.Where(seed =>
                !string.Equals(config.PublicBaseUrl.Trim().TrimEnd('/'), seed, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static RateLimitPartition<string> CreateRateLimitPartition(HttpContext context, PoolConfig poolConfig, string policyName, int permitLimit)
    {
        string clientKey = BootRequestIdentity.GetClientKey(context, poolConfig);
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policyName}:{clientKey}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permitLimit),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    }
}

// =================================================================================
// 2. DATUM SERVER CLASS
// =================================================================================
// This class opens a TCP socket and listens for incoming connections. When a new
// client connects, it spins up a dedicated 'ClientHandler' to manage it.
// =================================================================================


// =================================================================================
// 3. CLIENT HANDLER CLASS
// =================================================================================
// This class does the bulk of the work in managing the connection and passing
// messages to/from clients.
// TODO: Implement some sort of keep/alive, so that DATUM clients don't drop after
//        60 seconds of no contact. 
//        Build out the functions to recieve and respond to POW mining messages
// =================================================================================
public class ClientHandler
{
    private static long _nextSessionId = 0;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Key _ed25519LongTermKey; // The server's main Ed25519 key.
    private readonly Key _x25519KeyLongTerm; // The server's long-term x25519 key.
    private readonly BootProtocolStateService _stateService;

    // --- Per-Session State ---
    private PublicKey? _clientSessionPubKey;
    private Key? _serverSessionSigningKey; //ed25519
    private Key? _serverSessionEncryptKey; //x25519
    private SharedSecret? _channelSharedSecret; // The key for symmetric encryption
    private byte[]? _channelSharedSecretBytes;
    private byte[]? _sessionNonceSender; // Server’s send nonce (client’s receive nonce)
    private byte[]? _sessionNonceReceiver; // Server's receive nonce (client's send nonce)
    private UInt32 _sendingHeaderKey;
    private UInt32 _receivingHeaderKey;
    private HelloMessage? _helloMessage;
    private readonly PoolConfig _poolConfig;
    private readonly PowSubmitMessage?[] _jobCache = new PowSubmitMessage?[8];
    private readonly DateTime?[] _jobCacheUpdatedUtc = new DateTime?[8];
    private readonly string?[] _jobPayoutSnapshotIds = new string?[8];
    private readonly Dictionary<byte, string> _coinbaserSnapshotIds = new();
    private string _clientPayoutAddress = "";
    private string _clientIdentityKey = "";
    private string _clientEncryptIdentityKey = "";
    private int _consecutivePayoutMismatchRejections = 0;
    private DateTime? _staleTemplateSeriesStartedUtc = null;
    private DateTime _lastStaleTemplateRefreshUtc = DateTime.MinValue;
    private DateTime _lastForcedStaleTemplateDisconnectUtc = DateTime.MinValue;
    private DateTime _lastStaleTemplateWarningUtc = DateTime.MinValue;
    private bool _sessionPayoutAddressLocked = false;
    private readonly HashSet<string> _loggedUnexpectedPayoutAddresses = new(StringComparer.OrdinalIgnoreCase);
    private string? _serverInitiatedCloseEventType;
    private string? _serverInitiatedCloseMessage;
    private bool _serverInitiatedCloseLogged = false;
    private long _coinbaserFetchSequence = 0;
    private long _datumShareResponseSequence = 0;
    private long _datumProtocolEventSequence = 0;
    private int _lowDiffFastAcceptedSinceCourtesy = 0;
    private DateTime _lastLowDiffCourtesyValidationUtc = DateTime.MinValue;
    private byte _nextCoinbaserResponseId = 0;
    private readonly CancellationToken _stoppingToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _datumKeepaliveCts;
    private Task? _datumKeepaliveTask;
    private DateTime _lastServerMessageSentUtc = DateTime.MinValue;
    private readonly string _sessionId;
    private readonly DateTime _sessionStartedUtc;
    private string _sessionCloseDisposition = "open";
    private string? _sessionCloseReason;
    private string _sessionProtocol = "unknown";


    public ClientHandler(
        TcpClient client,
        Key serverLongTermKey,
        Key serverLongTermXKey,
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        CancellationToken st)
    {
        _client = client;
        _stream = client.GetStream();
        _ed25519LongTermKey = serverLongTermKey;
        _receivingHeaderKey = 0xDC871829; // initial send header key ... changed by handshake function
        _sendingHeaderKey = 0;
        _x25519KeyLongTerm = serverLongTermXKey;
        _poolConfig = poolConfig;
        _stateService = stateService;
        _clientPayoutAddress = BitcoinScript.NormalizeAddress(_poolConfig.PoolPayoutScript);
        _sessionPayoutAddressLocked = IsValidAddress(_clientPayoutAddress);
        _stoppingToken = st;
        _sessionStartedUtc = DateTime.UtcNow;
        _sessionId = $"datum-{Interlocked.Increment(ref _nextSessionId)}";
        _stateService.RecordDatumSessionOpened(_sessionId, RemoteEndpointLabel, _sessionStartedUtc);
        if (_sessionPayoutAddressLocked)
        {
            RememberClientPayoutAddress(_clientPayoutAddress);
            _stateService.RecordDatumSessionPayoutLock(_sessionId, _clientPayoutAddress);
        }
        RecordDatumProtocolEvent(new BootDatumProtocolEvent
        {
            Direction = "internal",
            EventType = "session-open",
            MessageLabel = "tcp-connect",
            Detail = "TCP client connected.",
            TimestampUtc = _sessionStartedUtc
        });
        Console.WriteLine($"🔌 Client {_client.Client.RemoteEndPoint} connected.");
    }

    private string RemoteEndpointLabel => _client.Client.RemoteEndPoint?.ToString() ?? "unknown";

    private void MarkSessionClose(string disposition, string? reason = null)
    {
        _sessionCloseDisposition = string.IsNullOrWhiteSpace(disposition) ? "closed" : disposition;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            _sessionCloseReason = reason;
        }
    }

    private void MarkSessionProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol) || string.Equals(_sessionProtocol, protocol, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sessionProtocol = protocol;
        _stateService.RecordDatumSessionProtocol(_sessionId, protocol);
        RecordDatumProtocolEvent(new BootDatumProtocolEvent
        {
            Direction = "internal",
            EventType = "protocol-detected",
            MessageLabel = protocol,
            Detail = $"Protocol determined as {protocol}."
        });
    }

    private void ScheduleServerInitiatedClose(string message, string eventType = "datum-session-close")
    {
        if (_serverInitiatedCloseLogged || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _serverInitiatedCloseEventType ??= eventType;
        _serverInitiatedCloseMessage ??= message;
    }

    private void FlushServerInitiatedCloseLog()
    {
        if (_serverInitiatedCloseLogged || string.IsNullOrWhiteSpace(_serverInitiatedCloseMessage))
        {
            return;
        }

        _serverInitiatedCloseLogged = true;
        _stateService.RecordExternalNetworkEvent(
            _serverInitiatedCloseEventType ?? "datum-session-close",
            "datum",
            _serverInitiatedCloseMessage);
        Console.WriteLine($"⚠️ {_serverInitiatedCloseMessage}");
    }

    private void RecordDatumProtocolEvent(BootDatumProtocolEvent telemetry)
    {
        telemetry.SessionId = _sessionId;
        telemetry.Sequence = Interlocked.Increment(ref _datumProtocolEventSequence);
        telemetry.Protocol = string.IsNullOrWhiteSpace(telemetry.Protocol) ? _sessionProtocol : telemetry.Protocol;
        telemetry.RemoteEndpoint = string.IsNullOrWhiteSpace(telemetry.RemoteEndpoint) ? RemoteEndpointLabel : telemetry.RemoteEndpoint;
        telemetry.TimestampUtc = telemetry.TimestampUtc == default ? DateTime.UtcNow : telemetry.TimestampUtc;
        if (!string.IsNullOrWhiteSpace(telemetry.Detail) && telemetry.Detail.Length > 256)
        {
            telemetry.Detail = telemetry.Detail[..256];
        }

        if (!string.IsNullOrWhiteSpace(telemetry.Username) && telemetry.Username.Length > 128)
        {
            telemetry.Username = telemetry.Username[..128];
        }

        _stateService.RecordDatumProtocolEvent(telemetry);
    }

    private void RecordPowSubmitProtocolOutcome(
        PowSubmitMessage powSubmit,
        bool accepted,
        bool affectedOnDeck,
        string? rejectionReason,
        double difficulty,
        string? prevBlockHash,
        bool nonceOnlySubmit,
        bool usedCachedJob,
        double? cachedJobAgeMs,
        string? detail = null)
    {
        RecordDatumProtocolEvent(new BootDatumProtocolEvent
        {
            Direction = "internal",
            EventType = "pow-submit-outcome",
            MessageLabel = "pow-submit",
            ProtoCmd = 0x05,
            MiningSubcommand = 0x27,
            Accepted = accepted,
            AffectedOnDeck = affectedOnDeck,
            RejectionReason = rejectionReason,
            Difficulty = difficulty,
            PrevBlockHash = prevBlockHash,
            JobId = powSubmit.JobId,
            CoinbaseId = powSubmit.CoinbaseId,
            NonceOnlySubmit = nonceOnlySubmit,
            UsedCachedJob = usedCachedJob,
            CachedJobAgeMs = cachedJobAgeMs,
            Username = powSubmit.Username,
            Detail = detail
        });
    }

    private async Task<int> ReadExactOrUntilClosedAsync(byte[] buffer, int length)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _poolConfig.DatumReadTimeoutSeconds)));
        int offset = 0;
        while (offset < length)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset, length - offset), timeout.Token);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    public async Task HandleClientAsync()
    {

        try
        {
            // We only peek the protocol once at the very start of the connection
            bool protocolDetermined = false;
            while (_client.Connected)
            {
                // Step 1: Read the 4-byte header
                var headerBuffer = new byte[4];
                int bytesRead = await ReadExactOrUntilClosedAsync(headerBuffer, headerBuffer.Length);
                if (bytesRead == 0)
                {
                    RecordDatumProtocolEvent(new BootDatumProtocolEvent
                    {
                        Direction = "recv",
                        EventType = "recv-header-eof",
                        MessageLabel = "header",
                        BytesRead = 0,
                        ExpectedBytes = headerBuffer.Length,
                        HeaderKeyBefore = _receivingHeaderKey,
                        HeaderKeyAfter = _receivingHeaderKey,
                        Detail = "Client closed before sending a full DATUM header."
                    });
                    MarkSessionClose("client-disconnected-no-data", "Client closed DATUM session before sending a full header.");
                    Console.WriteLine($"🔌 Client {_client.Client.RemoteEndPoint} disconnected (no data).");
                    break;
                }
                // --- NEW: Protocol Detection Logic ---
                if (!protocolDetermined)
                {
                    // Stratum V1 JSON usually starts with '{' (0x7B)
                    // We check if the first byte is '{'. 
                    if (headerBuffer[0] == 0x7B) 
                    {
                        RecordDatumProtocolEvent(new BootDatumProtocolEvent
                        {
                            Direction = "recv",
                            EventType = "stratum-proxy-detected",
                            MessageLabel = "stratum-v1-proxy",
                            BytesRead = bytesRead,
                            ExpectedBytes = headerBuffer.Length,
                            RawHeaderHex = Convert.ToHexString(headerBuffer, 0, bytesRead).ToLowerInvariant(),
                            HeaderKeyBefore = _receivingHeaderKey,
                            HeaderKeyAfter = _receivingHeaderKey,
                            Detail = "Initial bytes matched Stratum V1 JSON prefix."
                        });
                        MarkSessionProtocol("stratum-v1-proxy");
                        MarkSessionClose("stratum-proxy", "Connection was forwarded to the Stratum V1 proxy path.");
                        Console.WriteLine($"🔀 Stratum V1 detected from {_client.Client.RemoteEndPoint}. Forwarding to Gateway...");
                        
                        // Hand off control to the proxy method. 
                        // We pass the 4 bytes we already read so they aren't lost.
                        await HandleStratumProxyAsync(headerBuffer, bytesRead);
                        
                        // Once the proxy session ends, we break the loop and disconnect.
                        break; 
                    }
                    MarkSessionProtocol("datum");
                    protocolDetermined = true;
                }
                // -------------------------------------
                if (bytesRead < 4)
                {
                    RecordDatumProtocolEvent(new BootDatumProtocolEvent
                    {
                        Direction = "recv",
                        EventType = "partial-header",
                        MessageLabel = "header",
                        BytesRead = bytesRead,
                        ExpectedBytes = headerBuffer.Length,
                        RawHeaderHex = Convert.ToHexString(headerBuffer, 0, bytesRead).ToLowerInvariant(),
                        HeaderKeyBefore = _receivingHeaderKey,
                        HeaderKeyAfter = _receivingHeaderKey,
                        Detail = $"Received only {bytesRead} header bytes."
                    });
                    MarkSessionClose("partial-header", $"Received only {bytesRead} header bytes.");
                    ScheduleServerInitiatedClose(
                        $"Closing DATUM session {RemoteEndpointLabel} after receiving a partial header ({bytesRead} bytes).");
                    Console.WriteLine($"⚠️ Partial header received ({bytesRead} bytes): {BitConverter.ToString(headerBuffer, 0, bytesRead)}");
                    break;
                }
                //Console.WriteLine($"📥 Received header bytes: {BitConverter.ToString(headerBuffer)}");
                // Step 1.2: Decode header with XOR key
                uint receivingHeaderKeyBefore = _receivingHeaderKey;
                uint headerValue = BitConverter.ToUInt32(headerBuffer, 0); // Read as little-endian
                headerValue ^= _receivingHeaderKey; // XOR as 32-bit integer
                var deXoredHeaderBytes = BitConverter.GetBytes(headerValue); // Convert back to bytes
                _receivingHeaderKey = DatumHeaderXorFeedback(_receivingHeaderKey);
                //Console.WriteLine($"📥 De-XORed header bytes: {BitConverter.ToString(deXoredHeaderBytes)}");


                // Step 1.3: Parse header
                var header = DatumHeader.FromBytes(deXoredHeaderBytes);
                RecordDatumProtocolEvent(new BootDatumProtocolEvent
                {
                    Direction = "recv",
                    EventType = "recv-header",
                    MessageLabel = header.ProtoCmd switch
                    {
                        0x01 => "hello",
                        0x05 => "mining-command",
                        _ => "unknown"
                    },
                    ProtoCmd = header.ProtoCmd,
                    IsSigned = header.IsSigned,
                    IsEncryptedPubKey = header.IsEncryptedPubKey,
                    IsEncryptedChannel = header.IsEncryptedChannel,
                    CmdLen = header.CmdLen,
                    BytesRead = bytesRead,
                    ExpectedBytes = headerBuffer.Length,
                    RawHeaderHex = Convert.ToHexString(headerBuffer).ToLowerInvariant(),
                    DecodedHeaderHex = Convert.ToHexString(deXoredHeaderBytes).ToLowerInvariant(),
                    HeaderKeyBefore = receivingHeaderKeyBefore,
                    HeaderKeyAfter = _receivingHeaderKey
                });
                //Console.WriteLine($"📋 Parsed header: Cmd={header.ProtoCmd}, Len={header.CmdLen}, Signed={header.IsSigned}, EncryptedPubKey={header.IsEncryptedPubKey}, EncryptedChannel={header.IsEncryptedChannel}");

                // Step 2: Read in the message body
                if (header.CmdLen > _poolConfig.MaxShareRequestBytes)
                {
                    MarkSessionClose(
                        "message-too-large",
                        $"DATUM message declared {header.CmdLen} bytes; maximum is {_poolConfig.MaxShareRequestBytes}.");
                    ScheduleServerInitiatedClose(
                        $"Closing DATUM session {RemoteEndpointLabel} after an oversized message header.");
                    break;
                }
                var bodyBuffer = new byte[header.CmdLen];
                bytesRead = await ReadExactOrUntilClosedAsync(bodyBuffer, bodyBuffer.Length);
                if (bytesRead == 0)
                {
                    RecordDatumProtocolEvent(new BootDatumProtocolEvent
                    {
                        Direction = "recv",
                        EventType = "recv-body-eof",
                        MessageLabel = header.ProtoCmd switch
                        {
                            0x01 => "hello",
                            0x05 => "mining-command",
                            _ => "unknown"
                        },
                        ProtoCmd = header.ProtoCmd,
                        BytesRead = 0,
                        ExpectedBytes = bodyBuffer.Length,
                        CmdLen = header.CmdLen,
                        Detail = "Client closed before sending the encrypted DATUM body."
                    });
                    MarkSessionClose("client-disconnected-no-body", "Client closed DATUM session before sending the encrypted body.");
                    Console.WriteLine($"🔌 Client {_client.Client.RemoteEndPoint} disconnected (no body).");
                    break;
                }
                if (bytesRead < bodyBuffer.Length)
                {
                    RecordDatumProtocolEvent(new BootDatumProtocolEvent
                    {
                        Direction = "recv",
                        EventType = "partial-body",
                        MessageLabel = header.ProtoCmd switch
                        {
                            0x01 => "hello",
                            0x05 => "mining-command",
                            _ => "unknown"
                        },
                        ProtoCmd = header.ProtoCmd,
                        BytesRead = bytesRead,
                        ExpectedBytes = bodyBuffer.Length,
                        CmdLen = header.CmdLen,
                        Detail = $"Received only {bytesRead} of {bodyBuffer.Length} encrypted body bytes."
                    });
                    MarkSessionClose("partial-body", $"Received only {bytesRead} of {bodyBuffer.Length} encrypted body bytes.");
                    ScheduleServerInitiatedClose(
                        $"Closing DATUM session {RemoteEndpointLabel} after receiving a partial body ({bytesRead}/{bodyBuffer.Length} bytes).");
                    Console.WriteLine($"⚠️ Partial body received ({bytesRead}/{bodyBuffer.Length} bytes).");
                    break;
                }
                RecordDatumProtocolEvent(new BootDatumProtocolEvent
                {
                    Direction = "recv",
                    EventType = "recv-body",
                    MessageLabel = header.ProtoCmd switch
                    {
                        0x01 => "hello",
                        0x05 => "mining-command",
                        _ => "unknown"
                    },
                    ProtoCmd = header.ProtoCmd,
                    BytesRead = bytesRead,
                    ExpectedBytes = bodyBuffer.Length,
                    CmdLen = header.CmdLen
                });
                //Console.WriteLine($"📦 Received body ({bytesRead} bytes)");
                
                // Step 3: Decrypt the body
                byte[]? decryptedBody = null;
                //TODO: This if-else could be more robust, and check header.isEncryptedChannel as well
                if (header.IsEncryptedPubKey)
                {
                    //Console.WriteLine("Decrypting Signed message");
                    decryptedBody = DecryptSigned(bodyBuffer, bytesRead);
                    if (decryptedBody == null)
                    {
                        RecordDatumProtocolEvent(new BootDatumProtocolEvent
                        {
                            Direction = "internal",
                            EventType = "decrypt-failed",
                            MessageLabel = "signed-payload",
                            ProtoCmd = header.ProtoCmd,
                            CmdLen = header.CmdLen,
                            BytesRead = bytesRead,
                            ExpectedBytes = bodyBuffer.Length,
                            Detail = "Signed DATUM payload decryption returned null."
                        });
                        MarkSessionClose("decrypt-failed", "Signed DATUM payload decryption failed.");
                        ScheduleServerInitiatedClose(
                            $"Closing DATUM session {RemoteEndpointLabel} after signed payload decryption failed.");
                        Console.WriteLine("decrypted signed body is null");
                        break;
                    }
                    // Verify cmd_len matches decrypted body length
                    //TODO: change "48" to actually reference the libsodium constant instead.
                    //Modified (+48) to account for CryptoBoxSealBytes, the signature that is added to the encrypted payload.
                    if (header.CmdLen != decryptedBody.Length + 48)
                    {
                        RecordDatumProtocolEvent(new BootDatumProtocolEvent
                        {
                            Direction = "internal",
                            EventType = "signed-length-mismatch",
                            MessageLabel = "signed-payload",
                            ProtoCmd = header.ProtoCmd,
                            CmdLen = header.CmdLen,
                            BytesRead = bytesRead,
                            DecryptedBytes = decryptedBody.Length,
                            Detail = $"Signed payload length {header.CmdLen} did not match decrypted length {decryptedBody.Length}."
                        });
                        MarkSessionClose("signed-length-mismatch", $"Signed payload length {header.CmdLen} did not match decrypted length {decryptedBody.Length}.");
                        ScheduleServerInitiatedClose(
                            $"Closing DATUM session {RemoteEndpointLabel} because signed payload length {header.CmdLen} did not match decrypted length {decryptedBody.Length}.");
                        Console.WriteLine($"⚠️ Header cmd_len ({header.CmdLen}) does not match decrypted body length ({decryptedBody.Length})");
                        break;
                    }
                }  //      We need to use a different decryption key depending on the header.protoCmmd
                else if (header.IsEncryptedChannel)
                {
                    //Console.WriteLine("Decrypting Standard message");
                    decryptedBody = DecryptStandard(bodyBuffer, bytesRead);
                    // Verify cmd_len matches decrypted body length
                    //TODO: change "16" to actually reference the libsodium constant instead.
                    //Modified (+16) to account for MAC bytes, the signature that is added to the encrypted payload.  I think.
                    if (decryptedBody == null)
                    {
                        RecordDatumProtocolEvent(new BootDatumProtocolEvent
                        {
                            Direction = "internal",
                            EventType = "decrypt-failed",
                            MessageLabel = "encrypted-channel-payload",
                            ProtoCmd = header.ProtoCmd,
                            CmdLen = header.CmdLen,
                            BytesRead = bytesRead,
                            ExpectedBytes = bodyBuffer.Length,
                            Detail = "Encrypted DATUM channel payload decryption returned null."
                        });
                        MarkSessionClose("decrypt-failed", "Encrypted DATUM channel payload decryption failed.");
                        ScheduleServerInitiatedClose(
                            $"Closing DATUM session {RemoteEndpointLabel} after encrypted channel decryption failed.");
                        Console.WriteLine("decrypted body is null");
                        break;
                    }
                    if (header.CmdLen != decryptedBody.Length + 16)
                    {
                        RecordDatumProtocolEvent(new BootDatumProtocolEvent
                        {
                            Direction = "internal",
                            EventType = "encrypted-length-mismatch",
                            MessageLabel = "encrypted-channel-payload",
                            ProtoCmd = header.ProtoCmd,
                            CmdLen = header.CmdLen,
                            BytesRead = bytesRead,
                            DecryptedBytes = decryptedBody.Length,
                            Detail = $"Encrypted payload length {header.CmdLen} did not match decrypted length {decryptedBody.Length}."
                        });
                        MarkSessionClose("encrypted-length-mismatch", $"Encrypted payload length {header.CmdLen} did not match decrypted length {decryptedBody.Length}.");
                        ScheduleServerInitiatedClose(
                            $"Closing DATUM session {RemoteEndpointLabel} because encrypted channel payload length {header.CmdLen} did not match decrypted length {decryptedBody.Length}.");
                        Console.WriteLine($"⚠️ Header cmd_len ({header.CmdLen}) does not match decrypted body length ({decryptedBody.Length})");
                        break;
                    }

                }
                if (decryptedBody == null)
                {
                    RecordDatumProtocolEvent(new BootDatumProtocolEvent
                    {
                        Direction = "internal",
                        EventType = "decrypt-failed",
                        MessageLabel = "payload",
                        ProtoCmd = header.ProtoCmd,
                        CmdLen = header.CmdLen,
                        BytesRead = bytesRead,
                        ExpectedBytes = bodyBuffer.Length,
                        Detail = "Payload decryption returned null."
                    });
                    MarkSessionClose("decrypt-failed", "Payload decryption returned null.");
                    ScheduleServerInitiatedClose(
                        $"Closing DATUM session {RemoteEndpointLabel} after a payload decryption failure.");
                    Console.WriteLine(" Header info: Cmd=" + (header.ProtoCmd) + " / CmdLen=" + header.CmdLen + " / isSigned=" + header.IsSigned + " / isEncryptedPubKey=" + header.IsEncryptedPubKey + " / isEncryptedChannel=" + header.IsEncryptedChannel);
                    Console.WriteLine($"❌ Failed to decrypt body for client {_client.Client.RemoteEndPoint}");
                    Console.WriteLine(BitConverter.ToString(bodyBuffer));

                    break;
                }
                RecordDatumProtocolEvent(new BootDatumProtocolEvent
                {
                    Direction = "internal",
                    EventType = "decrypt-ok",
                    MessageLabel = header.ProtoCmd switch
                    {
                        0x01 => "hello",
                        0x05 => "mining-command",
                        _ => "unknown"
                    },
                    ProtoCmd = header.ProtoCmd,
                    CmdLen = header.CmdLen,
                    BytesRead = bytesRead,
                    ExpectedBytes = bodyBuffer.Length,
                    DecryptedBytes = decryptedBody.Length
                });
                //Console.WriteLine($"🔓 Decrypted body ({decryptedBody.Length} bytes)");
                
                // Step 4: Parse the message appropriately.  Responses are generated in the appropriate "Handle" function.
                //Console.WriteLine($"[RECV] Command: 0x{header.ProtoCmd:X2}, Length: {header.CmdLen} bytes");
                RecordDatumProtocolEvent(new BootDatumProtocolEvent
                {
                    Direction = "internal",
                    EventType = "dispatch",
                    MessageLabel = header.ProtoCmd switch
                    {
                        0x01 => "hello",
                        0x05 => "mining-command",
                        _ => "unknown"
                    },
                    ProtoCmd = header.ProtoCmd,
                    CmdLen = header.CmdLen
                });
                switch (header.ProtoCmd)
                {
                    case 0x01: await HandleHelloAsync(header, decryptedBody); break;
                    case 0x05: await HandleMiningCommandAsync(header, decryptedBody); break;
                    default:
                        RecordDatumProtocolEvent(new BootDatumProtocolEvent
                        {
                            Direction = "internal",
                            EventType = "unknown-command",
                            MessageLabel = "unknown",
                            ProtoCmd = header.ProtoCmd,
                            CmdLen = header.CmdLen,
                            Detail = $"Unknown DATUM command 0x{header.ProtoCmd:X2}."
                        });
                        Console.WriteLine(" Header info: Cmd=" + (header.ProtoCmd) + " / CmdLen=" + header.CmdLen + " / isSigned=" + header.IsSigned + " / isEncryptedPubKey=" + header.IsEncryptedPubKey + " / isEncryptedChannel=" + header.IsEncryptedChannel);
                        Console.WriteLine($"⚠️ Received unknown command: 0x{header.ProtoCmd:X2}"); break;
                }
                //Finally back to the top of the loop and await the next incoming message
            }
        }
        catch (OperationCanceledException) when (!_stoppingToken.IsCancellationRequested)
        {
            MarkSessionClose("read-timeout", "DATUM header or body was not received before the read deadline.");
            ScheduleServerInitiatedClose($"Closing DATUM session {RemoteEndpointLabel} after a read timeout.");
        }
        catch (IOException ex)
        {
            MarkSessionClose("client-disconnected-io", $"I/O exception while handling DATUM session: {ex.Message}");
            Console.WriteLine($"🔌 Client {_client.Client.RemoteEndPoint} disconnected.");
        }
        catch (Exception ex)
        {
            MarkSessionClose("server-exception", $"{ex.GetType().Name}: {ex.Message}");
            ScheduleServerInitiatedClose(
                $"Closing DATUM session {RemoteEndpointLabel} after an internal server exception: {ex.GetType().Name}: {ex.Message}.");
            Console.WriteLine($"💥 An error occurred with client {_client.Client.RemoteEndPoint}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            FlushServerInitiatedCloseLog();
            if (string.Equals(_sessionCloseDisposition, "open", StringComparison.Ordinal))
            {
                if (_stoppingToken.IsCancellationRequested)
                {
                    MarkSessionClose("server-stopping", "Server shutdown stopped the DATUM session.");
                }
                else if (_serverInitiatedCloseLogged || !string.IsNullOrWhiteSpace(_serverInitiatedCloseMessage))
                {
                    MarkSessionClose("server-closed", _serverInitiatedCloseMessage);
                }
                else
                {
                    MarkSessionClose("closed", "DATUM session ended without an explicit close reason.");
                }
            }

            _stateService.CompleteDatumSession(
                _sessionId,
                _sessionCloseDisposition,
                !string.IsNullOrWhiteSpace(_serverInitiatedCloseMessage) ? _serverInitiatedCloseMessage : _sessionCloseReason,
                serverInitiated: !string.IsNullOrWhiteSpace(_serverInitiatedCloseMessage),
                serverCloseEventType: _serverInitiatedCloseEventType,
                timestampUtc: DateTime.UtcNow);
            RecordDatumProtocolEvent(new BootDatumProtocolEvent
            {
                Direction = "internal",
                EventType = "session-close",
                MessageLabel = _serverInitiatedCloseEventType ?? _sessionCloseDisposition,
                CloseDisposition = _sessionCloseDisposition,
                CloseReason = !string.IsNullOrWhiteSpace(_serverInitiatedCloseMessage) ? _serverInitiatedCloseMessage : _sessionCloseReason,
                Detail = $"serverInitiated={!string.IsNullOrWhiteSpace(_serverInitiatedCloseMessage)}",
                TimestampUtc = DateTime.UtcNow
            });
            _datumKeepaliveCts?.Cancel();
            _datumKeepaliveCts?.Dispose();
            _client.Close();
        }
    }

    private void RememberClientPayoutAddress(string payoutAddress)
    {
        if (string.IsNullOrWhiteSpace(payoutAddress))
        {
            return;
        }

        _stateService.RememberDatumPayoutAddress(_clientIdentityKey, payoutAddress);
        _stateService.RememberDatumPayoutAddress(_clientEncryptIdentityKey, payoutAddress);
    }

    private byte NextCoinbaserResponseId()
    {
        unchecked
        {
            _nextCoinbaserResponseId++;
            if (_nextCoinbaserResponseId == 0)
            {
                _nextCoinbaserResponseId = 1;
            }

            return _nextCoinbaserResponseId;
        }
    }

    private void RememberCoinbaserSnapshotId(byte coinbaserId, string? snapshotId)
    {
        if (coinbaserId == 0 || string.IsNullOrWhiteSpace(snapshotId))
        {
            return;
        }

        _coinbaserSnapshotIds[coinbaserId] = snapshotId;
    }

    private string? ResolvePayoutSnapshotId(PowSubmitMessage powSubmit)
    {
        if (powSubmit.CoinbaserId.HasValue &&
            _coinbaserSnapshotIds.TryGetValue(powSubmit.CoinbaserId.Value, out string? snapshotId) &&
            !string.IsNullOrWhiteSpace(snapshotId))
        {
            return snapshotId;
        }

        if (powSubmit.JobId < _jobPayoutSnapshotIds.Length &&
            !string.IsNullOrWhiteSpace(_jobPayoutSnapshotIds[powSubmit.JobId]))
        {
            return _jobPayoutSnapshotIds[powSubmit.JobId];
        }

        return null;
    }

    public async Task<bool> RequestBlockTemplateRefreshAsync(string reason = "unspecified")
    {
        if (!_client.Connected ||
            _channelSharedSecretBytes == null ||
            _sessionNonceSender == null ||
            _sendingHeaderKey == 0)
        {
            RecordDatumProtocolEvent(new BootDatumProtocolEvent
            {
                Direction = "internal",
                EventType = "template-refresh-skipped",
                MessageLabel = "template-refresh-request",
                Detail = $"connected={_client.Connected}; sharedSecretReady={_channelSharedSecretBytes != null}; senderNonceReady={_sessionNonceSender != null}; headerKeyReady={_sendingHeaderKey != 0}; reason={reason}"
            });
            return false;
        }

        await SendEncryptedMessageAsync(0x05, [0xF9], isSigned: false, isEncryptedChannel: true, isEncryptedPubKey: false, messageLabel: "template-refresh-request");
        _stateService.RecordDatumSessionRefreshRequest(_sessionId);
        _stateService.RecordExternalNetworkEvent(
            "datum-refresh-request",
            "datum",
            $"Requested DATUM template refresh for session {RemoteEndpointLabel}. reason={reason}; lockedPayoutAddress={_clientPayoutAddress}.");
        return true;
    }

    private void StartDatumKeepaliveLoop()
    {
        if (_poolConfig.DatumKeepaliveIntervalSeconds <= 0 || _datumKeepaliveTask != null)
        {
            return;
        }

        _datumKeepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        CancellationToken token = _datumKeepaliveCts.Token;
        int intervalSeconds = Math.Clamp(_poolConfig.DatumKeepaliveIntervalSeconds, 5, 300);
        int pollSeconds = Math.Max(5, intervalSeconds / 2);
        _datumKeepaliveTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), token);
                    if (token.IsCancellationRequested || !_client.Connected)
                    {
                        break;
                    }

                    if ((DateTime.UtcNow - _lastServerMessageSentUtc).TotalSeconds < intervalSeconds)
                    {
                        continue;
                    }

                    await SendEncryptedMessageAsync(
                        0x01,
                        [],
                        isSigned: false,
                        isEncryptedChannel: true,
                        isEncryptedPubKey: false,
                        messageLabel: "datum-keepalive");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RecordDatumProtocolEvent(new BootDatumProtocolEvent
                    {
                        Direction = "internal",
                        EventType = "datum-keepalive-failed",
                        MessageLabel = "datum-keepalive",
                        Detail = ex.Message
                    });
                    break;
                }
            }
        }, token);
    }

    private enum TemplateRejectKind
    {
        None,
        PayoutMismatch,
        SoloFallback
    }

    private static TemplateRejectKind GetTemplateRejectKind(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return TemplateRejectKind.None;
        }

        if (reason.StartsWith("Coinbase winners payouts do not match", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateRejectKind.PayoutMismatch;
        }

        if (reason.StartsWith("Coinbase appears to use a non-Boot single-recipient template", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateRejectKind.SoloFallback;
        }

        return TemplateRejectKind.None;
    }

    private void ResetStaleTemplateTracking()
    {
        _consecutivePayoutMismatchRejections = 0;
        _staleTemplateSeriesStartedUtc = null;
    }

    private async Task<bool> HandlePotentialStaleTemplateRejectAsync(string? rejectionReason)
    {
        TemplateRejectKind rejectKind = GetTemplateRejectKind(rejectionReason);
        if (rejectKind == TemplateRejectKind.None)
        {
            return false;
        }

        if (rejectKind == TemplateRejectKind.SoloFallback)
        {
            // DATUM in prefer mode can legitimately spend a short window on local fallback work
            // after a reconnect or coinbaser timeout. Re-sending blocknotify here just creates
            // a refresh loop that looks like repeated "new block" events in the DATUM log.
            ResetStaleTemplateTracking();
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        _staleTemplateSeriesStartedUtc ??= nowUtc;
        _consecutivePayoutMismatchRejections++;

        int refreshIntervalSeconds = Math.Clamp(_poolConfig.StaleDatumRefreshIntervalSeconds, 2, 300);
        if ((nowUtc - _lastStaleTemplateRefreshUtc).TotalSeconds >= refreshIntervalSeconds)
        {
            _lastStaleTemplateRefreshUtc = nowUtc;
            await RequestBlockTemplateRefreshAsync("stale-payout-mismatch");
        }

        int disconnectThreshold = Math.Clamp(_poolConfig.StaleDatumPayoutMismatchThreshold, 2, 20);
        if (_consecutivePayoutMismatchRejections < disconnectThreshold)
        {
            return false;
        }

        double staleDurationSeconds = _staleTemplateSeriesStartedUtc.HasValue
            ? (nowUtc - _staleTemplateSeriesStartedUtc.Value).TotalSeconds
            : 0;
        int disconnectMinSeconds = Math.Clamp(_poolConfig.StaleDatumDisconnectMinSeconds, 1, 600);
        if (staleDurationSeconds < disconnectMinSeconds)
        {
            return false;
        }

        int disconnectCooldownSeconds = Math.Clamp(_poolConfig.StaleDatumDisconnectCooldownSeconds, 5, 1800);
        if ((nowUtc - _lastStaleTemplateWarningUtc).TotalSeconds < disconnectCooldownSeconds)
        {
            return false;
        }

        _lastStaleTemplateWarningUtc = nowUtc;

        string staleMessage =
            $"DATUM client {RemoteEndpointLabel} submitted {_consecutivePayoutMismatchRejections} consecutive stale payout shares for {_clientPayoutAddress} over {staleDurationSeconds:F1}s. Requested template refresh and continuing the session.";

        if (_poolConfig.StaleDatumForceDisconnectEnabled)
        {
            _lastForcedStaleTemplateDisconnectUtc = nowUtc;
            ScheduleServerInitiatedClose(
                $"Reset DATUM session {RemoteEndpointLabel} after {_consecutivePayoutMismatchRejections} stale payout shares persisted for {staleDurationSeconds:F1}s while locked to payout address {_clientPayoutAddress}.",
                "datum-session-reset");

            Console.WriteLine(
                $"⚠️ DATUM client {RemoteEndpointLabel} submitted {_consecutivePayoutMismatchRejections} consecutive stale payout shares for {_clientPayoutAddress} over {staleDurationSeconds:F1}s. Disconnecting to force a clean reconnect.");
            return true;
        }

        _stateService.RecordExternalNetworkEvent(
            "datum-session-stale-warning",
            "datum",
            staleMessage);
        Console.WriteLine($"⚠️ {staleMessage}");
        return false;
    }

    /// <summary>
    /// Forwards traffic bi-directionally between the connected Client and the Onsite Gateway.
    /// Handles the "Handover" of the first 4 bytes transparently.
    /// </summary>
    private async Task HandleStratumProxyAsync(byte[] initialBuffer, int initialCount)
    {
        string gatewayIp = _poolConfig.StratumV1ProxyHost.Trim();
        int gatewayPort = _poolConfig.StratumV1ProxyPort;
        if (string.IsNullOrWhiteSpace(gatewayIp) || gatewayPort <= 0)
        {
            Console.WriteLine(
                $"Stratum V1 proxy request from {_client.Client.RemoteEndPoint} dropped because stratum_v1_proxy_host/port is not configured.");
            return;
        }

        Console.WriteLine($"🔄 Proxy: Connecting client {_client.Client.RemoteEndPoint} to Gateway ({gatewayIp}:{gatewayPort})...");

        using (var gatewayClient = new System.Net.Sockets.TcpClient())
        {
            try
            {
                // 1. Attempt to connect to the Gateway
                // We use a small timeout logic here to fail fast if the server is down
                var connectTask = gatewayClient.ConnectAsync(gatewayIp, gatewayPort);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                {
                    throw new TimeoutException("Timed out waiting for Gateway response.");
                }
                await connectTask; // Re-await to propagate exceptions if failed

                Console.WriteLine("✅ Proxy: Connected to Gateway. Starting pipe...");

                using (var gatewayStream = gatewayClient.GetStream())
                {
                    // 2. Replay the initial bytes (The 'Header' we peeked)
                    // We MUST write this before hooking up the pipes.
                    if (initialCount > 0)
                    {
                        await gatewayStream.WriteAsync(initialBuffer, 0, initialCount);
                        await gatewayStream.FlushAsync(); // Force push
                    }

                    // 3. Define the Pipe CancellationToken
                    // This token cancels the copy operation if one side disconnects
                    using (var cts = new CancellationTokenSource())
                    {
                        // Task A: Miner -> Gateway (Append to the stream we already started)
                        var clientToGateway = CopyStreamWithCloseAsync(_stream, gatewayStream, cts.Token, "Miner->Gateway");

                        // Task B: Gateway -> Miner
                        var gatewayToClient = CopyStreamWithCloseAsync(gatewayStream, _stream, cts.Token, "Gateway->Miner");

                        // 4. Wait for EITHER side to close the connection
                        await Task.WhenAny(clientToGateway, gatewayToClient);
                        
                        // Cancel the other task so we don't leave hanging sockets
                        cts.Cancel(); 
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Proxy Error: {ex.GetType().Name} - {ex.Message}");
                // Optional: Send a Stratum Error back to the miner so they know why they were dropped
                // var errorJson = "{\"id\":null,\"result\":null,\"error\":[20,\"Internal Proxy Error\",null]}\n";
                // byte[] errBytes = System.Text.Encoding.UTF8.GetBytes(errorJson);
                // await _stream.WriteAsync(errBytes, 0, errBytes.Length);
            }
            finally
            {
                Console.WriteLine($"🛑 Proxy: Session ended for {_client.Client.RemoteEndPoint}");
            }
        }
    }

    // Helper to copy streams and detect closure
    private async Task CopyStreamWithCloseAsync(Stream source, Stream destination, CancellationToken token, string name)
    {
        try
        {
            // Use a smaller buffer for Stratum (low latency)
            // Stratum messages are small; 4KB is plenty.
            await source.CopyToAsync(destination, 4096, token);
        }
        catch (OperationCanceledException) { /* Expected on shutdown */ }
        catch (IOException) { /* Connection broke */ }
        catch (Exception ex) { Console.WriteLine($"⚠️ Pipe Error ({name}): {ex.Message}"); }
    }


    private byte[]? DecryptSigned(byte[] encryptedBody, int bytesRead)
    {
        //Console.WriteLine($"📦 Ciphertext first 32 bytes: {BitConverter.ToString(encryptedBody, 0, 32)}");
        //Console.WriteLine($"📦 Ciphertext first all bytes: {BitConverter.ToString(encryptedBody)}");
        try
        {
            const int CryptoBoxSealBytes = 48; // 48 (32 ephemeral PK + 16 Poly1305 tag)
            if (bytesRead < CryptoBoxSealBytes) { Console.WriteLine($"❌ Ciphertext too short: {bytesRead} bytes"); return null; }

            // Use the X25519 key pair directly
            //TODO: Switch these from NSec keys to whatever Span<T> thing LibSodium recommends
            var privateKeyBytes = _x25519KeyLongTerm.Export(KeyBlobFormat.RawPrivateKey); // 32 bytes

            // Truncate input to actual length
            var cipherText = encryptedBody.AsSpan(0, bytesRead).ToArray();

            // Decrypt using crypto_box_seal_open
            //Span<byte> decrypted = new Span<byte>();
            byte[] decrypted = new byte[encryptedBody.Length - CryptoBoxSealBytes];
            LibSodium.CryptoBox.DecryptWithPrivateKey(decrypted, cipherText, privateKeyBytes);
            if (decrypted == null) { Console.WriteLine("❌ Decryption failed: Sodium.SealedPublicKeyBox.Open returned null"); return null; }
            
            //Console.WriteLine($"🔓 Decrypted {decrypted.Length} bytes");
            //Console.WriteLine($"-> {BitConverter.ToString(decrypted)}");
            //Console.WriteLine($"🔓 Client signing public key:    {BitConverter.ToString(decrypted, 0, 16)}...");
            //Console.WriteLine($"🔓 Client encryption public key: {BitConverter.ToString(decrypted, 32, 16)}...");
            return decrypted;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Decryption error: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private byte[]? DecryptStandard(byte[] encryptedBody, int bytesRead)
    {
        try
        {
            const int CryptoBoxSealBytes = 48; // 48 (32 ephemeral PK + 16 Poly1305 tag)
            if (bytesRead < CryptoBoxSealBytes) { Console.WriteLine($"❌ Ciphertext too short: {bytesRead} bytes"); return null; }

            // Use the X25519 key pair directly
            //TODO: Switch these from NSec keys to whatever Span<T> thing LibSodium recommends
            //if (_channelSharedSecretBytes == null) { Console.WriteLine("_serverSessionEncryptKey is null!"); return null; }
            //var privateKeyBytes = _channelSharedSecret.Export(KeyBlobFormat.RawPrivateKey);       //_x25519KeyLongTerm.Export(KeyBlobFormat.RawPrivateKey); // 32 bytes

            // Truncate input to actual length
            var cipherText = encryptedBody.AsSpan(0, bytesRead).ToArray();
            byte[] combinedCiphertext = new byte[bytesRead + LibSodium.CryptoBox.NonceLen];
            Array.Copy(_sessionNonceReceiver, 0, combinedCiphertext, 0, LibSodium.CryptoBox.NonceLen);
            Array.Copy(encryptedBody, 0, combinedCiphertext, LibSodium.CryptoBox.NonceLen, bytesRead);


            //Span<byte> decrypted = new Span<byte>();
            byte[] plaintext = new byte[bytesRead - LibSodium.CryptoBox.MacLen];
            LibSodium.CryptoBox.DecryptWithSharedKey(plaintext, combinedCiphertext, _channelSharedSecretBytes);
            //LibSodium.CryptoBox.DecryptWithSharedKey(decrypted, cipherText, _channelSharedSecretBytes, null, _sessionNonceReceiver);  //Prolly need to add nonce and MAC, or something.  Need to see how client does it.
            if (plaintext == null)
            {
                Console.WriteLine("❌ Decryption failed: Sodium.DecryptWithSharedKey returned null");
                return null;
            }
            _sessionNonceReceiver = IncrementNonce(_sessionNonceReceiver);

            //Console.WriteLine($"🔓 Decrypted {decrypted.Length} bytes");
            //Console.WriteLine($"-> {BitConverter.ToString(decrypted)}");            
            return plaintext;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Decryption error: {ex.Message}\n{ex.StackTrace}");
            Console.WriteLine($"🔌 Client {_client.Client.RemoteEndPoint} has a problem.  Or something.");
            return null;
        }
    }

    private byte[] InitializeNonce(uint nk, byte[] clientSessionEd25519PubKey)
    {
        var nonce = new byte[24];
        nk -= 42;
        nk ^= BitConverter.ToUInt32(clientSessionEd25519PubKey, 7);
        for (int j = 0; j < 24; j += 4)
        {
            uint value = DatumHeaderXorFeedback(nk - 42);
            nonce[j] = (byte)(value);
            nonce[j + 1] = (byte)(value >> 8);
            nonce[j + 2] = (byte)(value >> 16);
            nonce[j + 3] = (byte)(value >> 24);
            nk = BitConverter.ToUInt32(nonce, j);
            nk = ~nk;
        }
        return nonce;
    }

    private byte[] InitializeReceiverNonce(byte[] senderNonce)
    {
        var receiverNonce = new byte[24];
        for (int j = 0; j < 24; j += 4)
        {
            uint senderValue = BitConverter.ToUInt32(senderNonce, j);
            uint receiverValue = senderValue ^ 0x57575757;
            receiverNonce[j] = (byte)(receiverValue);
            receiverNonce[j + 1] = (byte)(receiverValue >> 8);
            receiverNonce[j + 2] = (byte)(receiverValue >> 16);
            receiverNonce[j + 3] = (byte)(receiverValue >> 24);
        }
        return receiverNonce;
    }

    private byte[] IncrementNonce(byte[] nonce)
    {
        // Increment nonce as a little-endian 192-bit integer
        for (int i = 0; i < nonce.Length; i++)
        {
            if (++nonce[i] != 0) break;
        }
        return nonce;
    }

    /// Handles the initial 0x01 handshake message from the client.
    private async Task HandleHelloAsync(DatumHeader header, byte[] decryptedBody)
    {
        //Console.WriteLine("   -> Received HELLO (0x01). Processing...");
        var bytesConsumed = 0;
        (_helloMessage, bytesConsumed) = HelloMessage.FromBytes(decryptedBody);
        if (_helloMessage == null || bytesConsumed < 0)
        {
            RecordDatumProtocolEvent(new BootDatumProtocolEvent
            {
                Direction = "internal",
                EventType = "hello-parse-failed",
                MessageLabel = "hello",
                ProtoCmd = header.ProtoCmd,
                DecryptedBytes = decryptedBody.Length,
                Detail = "Failed to parse DATUM hello message."
            });
            Console.WriteLine($"❌ Failed to parse hello message for client {_client.Client.RemoteEndPoint}");
            return;
        }
        if (bytesConsumed != decryptedBody.Length)
        {
            RecordDatumProtocolEvent(new BootDatumProtocolEvent
            {
                Direction = "internal",
                EventType = "hello-length-mismatch",
                MessageLabel = "hello",
                ProtoCmd = header.ProtoCmd,
                BytesRead = bytesConsumed,
                ExpectedBytes = decryptedBody.Length,
                DecryptedBytes = decryptedBody.Length,
                Detail = $"Parsed {bytesConsumed} hello bytes but decrypted body was {decryptedBody.Length} bytes."
            });
            Console.WriteLine($"⚠️ Parsed {bytesConsumed} bytes, but decrypted body is {decryptedBody.Length} bytes");
            return;
        }

        _clientIdentityKey = Convert.ToHexString(_helloMessage.ClientSigningPubKey).ToLowerInvariant();
        _clientEncryptIdentityKey = Convert.ToHexString(_helloMessage.ClientEncryptPubKey).ToLowerInvariant();
        string signingPreview = _clientIdentityKey.Length >= 16 ? _clientIdentityKey[..16] : _clientIdentityKey;
        string encryptPreview = _clientEncryptIdentityKey.Length >= 16 ? _clientEncryptIdentityKey[..16] : _clientEncryptIdentityKey;
        RecordDatumProtocolEvent(new BootDatumProtocolEvent
        {
            Direction = "internal",
            EventType = "hello-parsed",
            MessageLabel = "hello",
            ProtoCmd = header.ProtoCmd,
            DecryptedBytes = decryptedBody.Length,
            Detail = $"signingKey={signingPreview}; encryptKey={encryptPreview}; bytesConsumed={bytesConsumed}"
        });
        _stateService.RecordDatumSessionHello(_sessionId, _clientIdentityKey, _clientEncryptIdentityKey);
        string? rememberedPayoutAddress =
            _stateService.GetKnownDatumPayoutAddress(_clientIdentityKey) ??
            _stateService.GetKnownDatumPayoutAddress(_clientEncryptIdentityKey);
        if (!string.IsNullOrWhiteSpace(rememberedPayoutAddress))
        {
            string signingKeyPreview = _clientIdentityKey.Length >= 8
                ? _clientIdentityKey[..8]
                : _clientIdentityKey;
            Console.WriteLine(
                $"🔁 Known DATUM client payout address {rememberedPayoutAddress} for client {signingKeyPreview}... starting this session on the temporary 256 Foundation slot-0 address until the first share locks the payout address.");
        }

        //Initialize a new ed25519 key for signing the session messages with
        //TODO: Switch these from NSec Keys to whatever Span<T> LibSodium uses
        _serverSessionSigningKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _clientSessionPubKey = PublicKey.Import(KeyAgreementAlgorithm.X25519, _helloMessage.ClientSessionEncryptPubKey, KeyBlobFormat.RawPublicKey);
        _serverSessionEncryptKey = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        _channelSharedSecretBytes = CryptoUtils.ComputeSharedSecretForCryptoBox(_serverSessionEncryptKey.Export(KeyBlobFormat.RawPrivateKey), _clientSessionPubKey.Export(KeyBlobFormat.RawPublicKey));
        _channelSharedSecret = SharedSecret.Import(_channelSharedSecretBytes, SharedSecretBlobFormat.RawSharedSecret);

        uint nk = 0;
        if (_helloMessage.xorKey != null) { nk = BitConverter.ToUInt32(_helloMessage.xorKey, 0); }
        else return;
        _sendingHeaderKey = DatumHeaderXorFeedback(~nk);        //Increment the header key for sending and recieving future message headers
        _receivingHeaderKey = DatumHeaderXorFeedback(nk);
        if (nk == 0) throw new InvalidOperationException("Failed to extract XOR key");
        _sessionNonceSender = InitializeNonce(nk, _helloMessage.ClientSessionSigningPubKey);          // Initialize nonce     
        _sessionNonceReceiver = InitializeReceiverNonce(_sessionNonceSender);

        // Send response
        var responsePayload = new HandshakeResponseMessage { /* ... payload initialization ... */ };
        // (The rest of the response generation logic is unchanged, but the encryption call will be fixed in the helper method)
        // First we have to echo the 4 keys that the client sent, or they will reject the handshake
        responsePayload.ClientSigningPubKey = _helloMessage.ClientSigningPubKey;
        responsePayload.ClientEncryptPubKey = _helloMessage.ClientEncryptPubKey;
        responsePayload.ClientSessionSigningPubKey = _helloMessage.ClientSessionSigningPubKey;
        responsePayload.ClientSessionEncryptPubKey = _helloMessage.ClientSessionEncryptPubKey;
        // Next we need to send the client our session public keys for signing and encryption
        responsePayload.ServerSessionSigningPubKey = _serverSessionSigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey); //ed25519
        responsePayload.ServerSessionEncryptPubKey = _serverSessionEncryptKey.PublicKey.Export(KeyBlobFormat.RawPublicKey); //x25519

        var responsePayloadBytes = responsePayload.ToBytes();
        //Console.WriteLine($"📦 Response payload: {BitConverter.ToString(responsePayloadBytes)}");
        var signature = SignatureAlgorithm.Ed25519.Sign(_ed25519LongTermKey, responsePayloadBytes);
        //Console.WriteLine($"📦 Signature: {BitConverter.ToString(signature)}");
        var signedPayload = responsePayloadBytes.Concat(signature).ToArray();
        //Console.WriteLine($"📦 Signed payload (corrected): {BitConverter.ToString(signedPayload)}");
        await SendEncryptedMessageAsync(0x02, signedPayload, true, false, true, messageLabel: "handshake-response");
        //Console.WriteLine($"[SEND] Handshake Response (0x02), length " + signedPayload.Length);
        await SendClientConfigureAsync(_poolConfig);          // Send 0x99 client configure message
        StartDatumKeepaliveLoop();
    }

    private async Task SendClientConfigureAsync(PoolConfig config)
    {
        // Construct payload
        var payload = new List<byte>();

        // Sub-command: 0x99
        payload.Add(0x99);

        // Version: 0x01
        payload.Add(0x01);

        // Pool payout script
        byte[] poolScriptBytes = ResolvePoolPayoutScriptBytes(config.PoolPayoutScript, config.BitcoinNetwork);
        if (poolScriptBytes.Length > 255)
        {
            Console.WriteLine($"⚠️ Pool payout script too long ({poolScriptBytes.Length} bytes), truncating to 255");
            Array.Resize(ref poolScriptBytes, 255);  // This is really dumb.  Stupid AI wrote it.
        }
        payload.Add((byte)poolScriptBytes.Length);
        payload.AddRange(poolScriptBytes);

        // Prime ID
        payload.AddRange(BitConverter.GetBytes(config.PrimeId)); // Little-endian uint32

        // Coinbase tag
        byte[] coinbaseTagBytes = Encoding.UTF8.GetBytes(config.CoinbaseTag);
        payload.Add((byte)coinbaseTagBytes.Length);
        payload.AddRange(coinbaseTagBytes);

        // Minimum difficulty
        payload.AddRange(BitConverter.GetBytes(config.MinDiff)); // Little-endian uint64

        // Terminator: 0x00 0xFE
        payload.Add(0x00);
        payload.Add(0xFE);

        // Convert to bytes
        byte[] payloadBytes = payload.ToArray();
        //Console.WriteLine($"📦 Client configure payload (before signing): {BitConverter.ToString(payloadBytes)}");

        // Generate signature
        if (_serverSessionSigningKey == null){ Console.WriteLine("Server Session Signing Key is null!"); return; }
        var signature = SignatureAlgorithm.Ed25519.Sign(_serverSessionSigningKey, payloadBytes);
        //Console.WriteLine($"📦 Signature: {BitConverter.ToString(signature)}");

        // Append signature
        var signedPayload = payloadBytes.Concat(signature).ToArray();
        //Console.WriteLine($"📦 Signed payload: {BitConverter.ToString(signedPayload)}");

        // Send encrypted message (mining command 0x05, channel encryption)
        //Console.WriteLine("[SEND} Sending client configuration message 0x05/0x99");
        await SendEncryptedMessageAsync(0x05, signedPayload, isSigned: true, isEncryptedChannel: true, isEncryptedPubKey: false, messageLabel: "client-configure");
    }

    /// Handles all mining-related commands (sub-commands under 0x05).
    private async Task HandleMiningCommandAsync(DatumHeader header, byte[] decryptedBody)
    {
        byte subCmd = decryptedBody[0];
        byte[] subCmdPayload = decryptedBody.Skip(1).ToArray();
        RecordDatumProtocolEvent(new BootDatumProtocolEvent
        {
            Direction = "internal",
            EventType = "mining-subcommand",
            MessageLabel = subCmd switch
            {
                0x10 => "coinbaser-fetch",
                0x27 => "pow-submit",
                _ => "unknown-mining-subcommand"
            },
            ProtoCmd = header.ProtoCmd,
            MiningSubcommand = subCmd,
            DecryptedBytes = decryptedBody.Length
        });
        //Console.WriteLine($"[RECV] Mining Command (0x05), Sub-Command: 0x{subCmd:X2}");
        switch (subCmd)
        {
            case 0x10: await HandleCoinbaserFetchAsync(subCmdPayload); break;
            case 0x27: await HandlePowSubmitAsync(subCmdPayload); break;
            default: Console.WriteLine($"   -> Received unknown mining sub-command: 0x{subCmd:X2}"); break;
        }
    }

    private async Task HandleCoinbaserFetchAsync(byte[] payload)
    {
        long requestSequence = Interlocked.Increment(ref _coinbaserFetchSequence);
        DateTime startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();
        double parseDurationMs = 0;
        double stateReadDurationMs = 0;
        double buildDurationMs = 0;
        double serializeDurationMs = 0;
        double sendDurationMs = 0;
        CoinbaserFetchMessage? fetchRequest = null;
        CoinbaserFetchResponseMessage? fetchResponse = null;
        byte coinbaserResponseId = 0;
        string activeSnapshotId = string.Empty;
        List<PayoutInfo> winnersList = [];
        List<PayoutInfo> coinbaseOutputs = [];
        ulong teamPayoutTotal = 0;
        ulong mySats = 0;
        byte[] responsePayload = [];
        _stateService.RecordDatumSessionCoinbaserFetch(_sessionId, startedUtc);

        try
        {
            fetchRequest = CoinbaserFetchMessage.FromBytes(payload);
            parseDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            if (!_sessionPayoutAddressLocked || string.IsNullOrWhiteSpace(_clientPayoutAddress))
            {
                MarkSessionClose(
                    "payout-address-required",
                    "DATUM coinbase work is unavailable until the miner locks a valid payout address.");
                ScheduleServerInitiatedClose(
                    $"Closing DATUM session {RemoteEndpointLabel} because no valid slot-0 payout address is locked.");
                return;
            }

            stageStopwatch.Restart();
            DatumCoinbaseTemplate coinbaseTemplate = _stateService.GetDatumCoinbaseTemplate();
            winnersList = coinbaseTemplate.WinnersList;
            coinbaseOutputs = coinbaseTemplate.CoinbaseOutputs;
            activeSnapshotId = coinbaseTemplate.ActiveSnapshotId;
            stateReadDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            stageStopwatch.Restart();
            coinbaserResponseId = NextCoinbaserResponseId();
            RememberCoinbaserSnapshotId(coinbaserResponseId, activeSnapshotId);
            fetchResponse = new CoinbaserFetchResponseMessage();
            fetchResponse.CoinbaserId = coinbaserResponseId;
            foreach (var payout in winnersList)
            {
                teamPayoutTotal += payout.Value;
            }

            mySats = fetchRequest.RewardValue > teamPayoutTotal
                ? fetchRequest.RewardValue - teamPayoutTotal
                : 0;
            ulong total = mySats + teamPayoutTotal;
            if (total > fetchRequest.RewardValue) Console.WriteLine("Reward too big!!!");

            var myPayout = new PayoutInfo
            {
                Value = mySats,
                Address = _clientPayoutAddress
            };
            fetchResponse.Payouts = new List<PayoutInfo>(coinbaseOutputs);
            fetchResponse.Payouts.Insert(0, myPayout);
            buildDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            stageStopwatch.Restart();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)0x11);
                writer.Write(fetchRequest.RewardValue);
                fetchResponse.BitcoinNetwork = _poolConfig.BitcoinNetwork;
                byte[] payoutBytes = fetchResponse.ToBytes();
                writer.Write((uint)payoutBytes.Length);
                writer.Write(payoutBytes);
                responsePayload = stream.ToArray();
            }
            serializeDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

            stageStopwatch.Restart();
            await SendEncryptedMessageAsync(0x05, responsePayload, isSigned: false, isEncryptedChannel: true, isEncryptedPubKey: false, messageLabel: "coinbaser-fetch-response");
            _stateService.RecordSuccessfulDatumCoinbaserResponse();
            sendDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Stop();

            bool usingTemporarySlotZero = string.Equals(
                BitcoinScript.NormalizeAddress(_clientPayoutAddress),
                BitcoinScript.NormalizeAddress(BootProtocolStateService.GetGenesisFoundationAddress(_poolConfig.BitcoinNetwork)),
                StringComparison.OrdinalIgnoreCase);
            string clientIdentityPreview = !string.IsNullOrWhiteSpace(_clientIdentityKey)
                ? (_clientIdentityKey.Length > 8 ? _clientIdentityKey[..8] : _clientIdentityKey)
                : string.Empty;

            _stateService.RecordCoinbaserFetch(
                _sessionId,
                "datum",
                RemoteEndpointLabel,
                clientIdentityPreview,
                requestSequence,
                fetchRequest.RewardValue,
                teamPayoutTotal,
                mySats,
                _clientPayoutAddress,
                usingTemporarySlotZero,
                winnersList.Count,
                fetchResponse.Payouts.Count,
                responsePayload.Length,
                stopwatch.Elapsed.TotalMilliseconds,
                parseDurationMs,
                stateReadDurationMs,
                buildDurationMs,
                serializeDurationMs,
                sendDurationMs,
                startedUtc);

            if (stopwatch.Elapsed.TotalMilliseconds >= 1000 ||
                stateReadDurationMs >= 250 ||
                buildDurationMs >= 250 ||
                serializeDurationMs >= 250 ||
                sendDurationMs >= 250)
            {
                BootNetworkStatusDto status = _stateService.GetNetworkStatus();
                _stateService.RecordExternalNetworkEvent(
                    "datum-coinbaser-fetch",
                    "datum",
                    $"Responded to coinbaser fetch #{requestSequence} for session {RemoteEndpointLabel} in {stopwatch.Elapsed.TotalMilliseconds:F1} ms (parse={parseDurationMs:F1}, state={stateReadDurationMs:F1}, build={buildDurationMs:F1}, serialize={serializeDurationMs:F1}, send={sendDurationMs:F1}); reward={fetchRequest.RewardValue}; slot0={_clientPayoutAddress}; temporarySlot0={usingTemporarySlotZero}; winners={winnersList.Count}; outputs={fetchResponse.Payouts.Count}; coinbaserId={coinbaserResponseId}; snapshot={activeSnapshotId}.",
                    status.CurrentTipBlockHash,
                    status.CurrentTipBlockHeight,
                    startedUtc);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            BootNetworkStatusDto status = _stateService.GetNetworkStatus();
            _stateService.RecordExternalNetworkEvent(
                "datum-coinbaser-fetch-failed",
                "datum",
                $"Coinbaser fetch #{requestSequence} for session {RemoteEndpointLabel} failed after {stopwatch.Elapsed.TotalMilliseconds:F1} ms (parse={parseDurationMs:F1}, state={stateReadDurationMs:F1}, build={buildDurationMs:F1}, serialize={serializeDurationMs:F1}, send={sendDurationMs:F1}): {ex.GetType().Name}: {ex.Message}",
                status.CurrentTipBlockHash,
                status.CurrentTipBlockHeight,
                startedUtc);
            throw;
        }
        //Console.WriteLine($"[SEND] Coinbaser Fetch Response (0x05, 0x11)");
    }

    private static byte[] ResolvePoolPayoutScriptBytes(string? configuredValue, string? bitcoinNetwork)
    {
        string value = (configuredValue ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        if (BitcoinScript.TryAddressToScriptPubKey(value, bitcoinNetwork, out byte[]? scriptBytes))
        {
            return scriptBytes;
        }

        if (IsHexString(value))
        {
            try
            {
                return Convert.FromHexString(value);
            }
            catch (FormatException)
            {
                // Fall through to the legacy UTF-8 behavior below.
            }
        }

        Console.WriteLine(
            $"⚠️ pool_payout_script '{value}' is neither a recognized address nor hex scriptPubKey. Falling back to legacy UTF-8 bytes.");
        return Encoding.UTF8.GetBytes(value);
    }

    private static bool IsHexString(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length % 2 != 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private async Task HandlePowSubmitAsync(byte[] payload)
    {
        DateTime startedUtc = DateTime.UtcNow;
        var totalStopwatch = Stopwatch.StartNew();
        var stageStopwatch = Stopwatch.StartNew();
        PowSubmitMessage powSubmit;
        try
        {
            powSubmit = PowSubmitMessage.FromBytes(payload);
        }
        catch (Exception ex)
        {
            _stateService.RecordExternalNetworkEvent(
                "datum-share-parse-failed",
                "datum",
                $"Failed to parse DATUM PoW submit from {RemoteEndpointLabel}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        double parseDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
        double buildDurationMs = 0;
        double validationDurationMs = 0;
        double staleHandlingDurationMs = 0;
        double responseSendDurationMs = 0;
        bool nonceOnlySubmit = powSubmit.PrevBlockHash == null;
        bool usedCachedJob = false;
        double? cachedJobAgeMs = null;
        long responseSequence = Interlocked.Increment(ref _datumShareResponseSequence);
        RecordDatumProtocolEvent(new BootDatumProtocolEvent
        {
            Direction = "internal",
            EventType = "pow-submit-parsed",
            MessageLabel = "pow-submit",
            ProtoCmd = 0x05,
            MiningSubcommand = 0x27,
            JobId = powSubmit.JobId,
            CoinbaseId = powSubmit.CoinbaseId,
            NonceOnlySubmit = nonceOnlySubmit,
            PrevBlockHash = powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
            Username = powSubmit.Username,
            Detail = $"quickDiff={powSubmit.QuickDiff}; subsidyOnly={powSubmit.SubsidyOnly}; targetByte={powSubmit.TargetByte}"
        });
        //Check for proper address and usernames:
        //  using _poolConfig.PoolPayoutScript as default fallback
        string[] parts = powSubmit.Username.Split('.');
        string? validatedAddress = null;
        bool usedSessionFallback = false;
        // 1. Check Priority: Address2 (Second part of username: address.address2.worker)
        if (parts.Length >= 2 && IsValidAddress(parts[1]))
        {
            validatedAddress = parts[1];
        }

        // 2. Check Priority: Address1 (First part of username: address.worker OR address)
        // Only check if we haven't found a valid address yet
        if (validatedAddress == null && parts.Length >= 1 && IsValidAddress(parts[0]))
        {
            validatedAddress = parts[0];
        }

        // 3. Fallback: Use Pool Default
        if (validatedAddress == null)
        {
            //Console.WriteLine($"[Warning] Invalid or missing address in username '{powSubmit.Username}'. Using default.");
            validatedAddress = _clientPayoutAddress; 
            usedSessionFallback = true;
        }

        string submittedAddress = BitcoinScript.NormalizeAddress(validatedAddress);
        if (!_sessionPayoutAddressLocked && !usedSessionFallback && IsValidAddress(submittedAddress))
        {
            _clientPayoutAddress = submittedAddress;
            _sessionPayoutAddressLocked = true;
            RememberClientPayoutAddress(_clientPayoutAddress);
            _stateService.RecordDatumSessionPayoutLock(_sessionId, _clientPayoutAddress);
            RecordDatumProtocolEvent(new BootDatumProtocolEvent
            {
                Direction = "internal",
                EventType = "payout-lock",
                MessageLabel = "pow-submit",
                JobId = powSubmit.JobId,
                CoinbaseId = powSubmit.CoinbaseId,
                Detail = $"Locked payout address to {_clientPayoutAddress}."
            });
            _stateService.RecordExternalNetworkEvent(
                "datum-session-lock",
                "datum",
                $"Locked DATUM session {_client.Client.RemoteEndPoint} to payout address {_clientPayoutAddress}.");
            Console.WriteLine(
                $"🔒 Locked DATUM session {_client.Client.RemoteEndPoint} to payout address {_clientPayoutAddress}.");
        }
        else if (_sessionPayoutAddressLocked &&
                 IsValidAddress(submittedAddress) &&
                 !string.Equals(submittedAddress, _clientPayoutAddress, StringComparison.OrdinalIgnoreCase) &&
                 _loggedUnexpectedPayoutAddresses.Add(submittedAddress))
        {
            string warningMessage =
                $"DATUM session {_client.Client.RemoteEndPoint} submitted usernames for alternate payout address {submittedAddress} after locking to {_clientPayoutAddress}. Treating the session as payout address {_clientPayoutAddress} and ignoring alternate address metadata.";
            _stateService.RecordExternalNetworkEvent(
                "datum-session-warning",
                "datum",
                warningMessage);
            Console.WriteLine(
                $"⚠️ {warningMessage}");
        }

        powSubmit.Address = _clientPayoutAddress;

        if (powSubmit.PrevBlockHash == null)  //This is just a nonce update, does not include complete header info
        {
            if (powSubmit.JobId >= _jobCache.Length || _jobCache[powSubmit.JobId] == null)
            {
                Console.WriteLine(
                    $"⚠️ Missing cached DATUM job {powSubmit.JobId} for nonce-only update from {powSubmit.Address}. Requesting fresh templates.");
                RecordPowSubmitProtocolOutcome(
                    powSubmit,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Missing cached DATUM job",
                    difficulty: 0,
                    prevBlockHash: null,
                    nonceOnlySubmit: nonceOnlySubmit,
                    usedCachedJob: false,
                    cachedJobAgeMs: null,
                    detail: "Nonce-only share referenced a missing cached DATUM job.");
                stageStopwatch.Restart();
                await SendShareResponseAsync(powSubmit, accepted: false);
                responseSendDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
                totalStopwatch.Stop();
                _stateService.RecordDatumSessionShareOutcome(_sessionId, accepted: false, affectedOnDeck: false, startedUtc);
                RecordDatumShareResponseTelemetry(
                    powSubmit,
                    accepted: false,
                    affectedOnDeck: false,
                    rejectionReason: "Missing cached DATUM job",
                    difficulty: 0,
                    prevBlockHash: null,
                    nonceOnlySubmit: nonceOnlySubmit,
                    usedCachedJob: false,
                    cachedJobAgeMs: null,
                    payloadBytes: payload.Length,
                    coinbaseBytes: 0,
                    coinb1Bytes: 0,
                    coinb2Bytes: 0,
                    parseDurationMs: parseDurationMs,
                    buildDurationMs: buildDurationMs,
                    validationDurationMs: validationDurationMs,
                    snapshotReadDurationMs: 0,
                    snapshotReadLockWaitDurationMs: 0,
                    snapshotReadLockBodyDurationMs: 0,
                    shareCoreValidationDurationMs: 0,
                    stateMutationDurationMs: 0,
                    stateMutationLockWaitDurationMs: 0,
                    stateMutationLockBodyDurationMs: 0,
                    staleHandlingDurationMs: staleHandlingDurationMs,
                    responseSendDurationMs: responseSendDurationMs,
                    totalDurationMs: totalStopwatch.Elapsed.TotalMilliseconds,
                    startedUtc: startedUtc,
                    responseSequence: responseSequence);
                await RequestBlockTemplateRefreshAsync("missing-job-cache");
                return;
            }

            usedCachedJob = true;
            cachedJobAgeMs = _jobCacheUpdatedUtc[powSubmit.JobId].HasValue
                ? (startedUtc - _jobCacheUpdatedUtc[powSubmit.JobId]!.Value).TotalMilliseconds
                : null;
            _jobCache[powSubmit.JobId]!.CoinbaseId = powSubmit.CoinbaseId;  
            _jobCache[powSubmit.JobId]!.IsBlock = powSubmit.IsBlock;
            _jobCache[powSubmit.JobId]!.SubsidyOnly = powSubmit.SubsidyOnly;
            _jobCache[powSubmit.JobId]!.QuickDiff = powSubmit.QuickDiff;
            _jobCache[powSubmit.JobId]!.TargetByte = powSubmit.TargetByte;
            _jobCache[powSubmit.JobId]!.NTime = powSubmit.NTime;
            _jobCache[powSubmit.JobId]!.Nonce = powSubmit.Nonce;
            _jobCache[powSubmit.JobId]!.Version = powSubmit.Version;
            _jobCache[powSubmit.JobId]!.ExtranonceSize = powSubmit.ExtranonceSize;  //Always 12, but whatever
            _jobCache[powSubmit.JobId]!.Extranonce = powSubmit.Extranonce;
            _jobCache[powSubmit.JobId]!.Username = powSubmit.Username;
            if (!string.IsNullOrWhiteSpace(_jobPayoutSnapshotIds[powSubmit.JobId]))
            {
                _jobCache[powSubmit.JobId]!.PayoutSnapshotId = _jobPayoutSnapshotIds[powSubmit.JobId];
            }
            //Now check if we got new coinbase data with this share:
            if (powSubmit.SubsidyOnlyCoinb1 != null) //This share includes subsidy only coinbase data
            {
                _jobCache[powSubmit.JobId]!.SubsidyOnlyCoinb1 = powSubmit.SubsidyOnlyCoinb1;
                _jobCache[powSubmit.JobId]!.SubsidyOnlyCoinb2 = powSubmit.SubsidyOnlyCoinb2;
            }
            else if (!powSubmit.SubsidyOnly &&
                     powSubmit.CoinbaseId < powSubmit.CoinbasePairs.Length &&
                     powSubmit.CoinbasePairs[powSubmit.CoinbaseId].Coinb1 != null)  // Got a new coinbase with this one
            {
                //Console.WriteLine("New coinbase data");
                _jobCache[powSubmit.JobId]!.CoinbasePairs[powSubmit.CoinbaseId] = powSubmit.CoinbasePairs[powSubmit.CoinbaseId];
            }
            powSubmit = _jobCache[powSubmit.JobId]!;  //Copies back over the Merkle Branch info.
        }
        else if (powSubmit.JobId < _jobCache.Length)
        {
            powSubmit.PayoutSnapshotId = ResolvePayoutSnapshotId(powSubmit);
            _jobCache[powSubmit.JobId] = powSubmit;  //New job, with complete header info.  
            _jobCacheUpdatedUtc[powSubmit.JobId] = startedUtc;
            _jobPayoutSnapshotIds[powSubmit.JobId] = powSubmit.PayoutSnapshotId;
        }
        //TODO: Technically, there is the very edge case that a miner could reuse old coinbase info with a new job and merkle branches.  This case isn't handled right now.

        

        stageStopwatch.Restart();

        if (powSubmit.PrevBlockHash == null ||
            powSubmit.NBits == null ||
            powSubmit.MerkleBranches == null ||
            !powSubmit.MerkleBranchCount.HasValue)
        {
            buildDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            RecordPowSubmitProtocolOutcome(
                powSubmit,
                accepted: false,
                affectedOnDeck: false,
                rejectionReason: "Incomplete DATUM job data",
                difficulty: 0,
                prevBlockHash: null,
                nonceOnlySubmit: nonceOnlySubmit,
                usedCachedJob: usedCachedJob,
                cachedJobAgeMs: cachedJobAgeMs,
                detail: "Share could not be reconstructed because required DATUM job fields were missing.");
            stageStopwatch.Restart();
            await SendShareResponseAsync(powSubmit, accepted: false);
            responseSendDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();
            _stateService.RecordDatumSessionShareOutcome(_sessionId, accepted: false, affectedOnDeck: false, startedUtc);
            RecordDatumShareResponseTelemetry(
                powSubmit,
                accepted: false,
                affectedOnDeck: false,
                rejectionReason: "Incomplete DATUM job data",
                difficulty: 0,
                prevBlockHash: null,
                nonceOnlySubmit: nonceOnlySubmit,
                usedCachedJob: usedCachedJob,
                cachedJobAgeMs: cachedJobAgeMs,
                payloadBytes: payload.Length,
                coinbaseBytes: 0,
                coinb1Bytes: 0,
                coinb2Bytes: 0,
                parseDurationMs: parseDurationMs,
                buildDurationMs: buildDurationMs,
                validationDurationMs: validationDurationMs,
                snapshotReadDurationMs: 0,
                snapshotReadLockWaitDurationMs: 0,
                snapshotReadLockBodyDurationMs: 0,
                shareCoreValidationDurationMs: 0,
                stateMutationDurationMs: 0,
                stateMutationLockWaitDurationMs: 0,
                stateMutationLockBodyDurationMs: 0,
                staleHandlingDurationMs: staleHandlingDurationMs,
                responseSendDurationMs: responseSendDurationMs,
                totalDurationMs: totalStopwatch.Elapsed.TotalMilliseconds,
                startedUtc: startedUtc,
                responseSequence: responseSequence);
            await RequestBlockTemplateRefreshAsync("incomplete-job-data");
            return;
        }

        // Now compute the latest Merkle Root.  We have to do this for every share submission, since the extranonce changes every time.
        byte[]? Coinb1;
        byte[]? Coinb2;
        if (powSubmit.SubsidyOnly)
        {
            Coinb1 = powSubmit.SubsidyOnlyCoinb1;
            Coinb2 = powSubmit.SubsidyOnlyCoinb2;
        }
        else if (powSubmit.CoinbaseId < powSubmit.CoinbasePairs.Length)
        {
            Coinb1 = powSubmit.CoinbasePairs[powSubmit.CoinbaseId].Coinb1;
            Coinb2 = powSubmit.CoinbasePairs[powSubmit.CoinbaseId].Coinb2;
        }
        else
        {
            Coinb1 = null;
            Coinb2 = null;
        }

        if (Coinb1 == null || Coinb2 == null)
        {
            buildDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            RecordPowSubmitProtocolOutcome(
                powSubmit,
                accepted: false,
                affectedOnDeck: false,
                rejectionReason: "Missing DATUM coinbase data",
                difficulty: 0,
                prevBlockHash: powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
                nonceOnlySubmit: nonceOnlySubmit,
                usedCachedJob: usedCachedJob,
                cachedJobAgeMs: cachedJobAgeMs,
                detail: "Share referenced a DATUM coinbase pair that was not cached.");
            stageStopwatch.Restart();
            await SendShareResponseAsync(powSubmit, accepted: false);
            responseSendDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();
            _stateService.RecordDatumSessionShareOutcome(_sessionId, accepted: false, affectedOnDeck: false, startedUtc);
            RecordDatumShareResponseTelemetry(
                powSubmit,
                accepted: false,
                affectedOnDeck: false,
                rejectionReason: "Missing DATUM coinbase data",
                difficulty: 0,
                prevBlockHash: powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
                nonceOnlySubmit: nonceOnlySubmit,
                usedCachedJob: usedCachedJob,
                cachedJobAgeMs: cachedJobAgeMs,
                payloadBytes: payload.Length,
                coinbaseBytes: 0,
                coinb1Bytes: 0,
                coinb2Bytes: 0,
                parseDurationMs: parseDurationMs,
                buildDurationMs: buildDurationMs,
                validationDurationMs: validationDurationMs,
                snapshotReadDurationMs: 0,
                snapshotReadLockWaitDurationMs: 0,
                snapshotReadLockBodyDurationMs: 0,
                shareCoreValidationDurationMs: 0,
                stateMutationDurationMs: 0,
                stateMutationLockWaitDurationMs: 0,
                stateMutationLockBodyDurationMs: 0,
                staleHandlingDurationMs: staleHandlingDurationMs,
                responseSendDurationMs: responseSendDurationMs,
                totalDurationMs: totalStopwatch.Elapsed.TotalMilliseconds,
                startedUtc: startedUtc,
                responseSequence: responseSequence);
            await RequestBlockTemplateRefreshAsync("missing-coinbase-data");
            return;
        }

        byte[] coinbaseTx = Coinb1.Concat(powSubmit.Extranonce).Concat(Coinb2).ToArray();

        if (powSubmit.QuickDiff)
        {
            //Console.WriteLine("   using quickdiff");
            // ----- quickdiff magic word (last 2 bytes of Coinb1) -----
            int quickDiffOffset = Coinb1.Length - 2;   // client: cb->coinb1_len - 2
            if (quickDiffOffset < 0)
            {
                Console.WriteLine("Coinb1 too short for quickdiff magic");
            }
            else
            {
                ushort current = BitConverter.ToUInt16(Coinb1, quickDiffOffset);
                ushort magic = current == 0x5144 ? (ushort)0xAEBB : (ushort)0x5144;

                byte[] magicBytes = BitConverter.GetBytes(magic);
                if (!BitConverter.IsLittleEndian) Array.Reverse(magicBytes);   // pk_u16le writes LE

                Array.Copy(magicBytes, 0, coinbaseTx, quickDiffOffset, 2);
            }

            // ----- quickdiff target byte -----
            // The client uses the *quick* difficulty that the miner was asked for
            // DATUM sends the already-encoded PoT byte from the quickdiff coinbase.
            // Applying FloorPoT again reconstructs a different coinbase than the miner hashed.
            byte quickPot = powSubmit.TargetByte;
            if (powSubmit.TargetByteIndex.HasValue)
            {
                int idx = powSubmit.TargetByteIndex.Value;
                if (idx >= 0 && idx < Coinb1.Length)
                    coinbaseTx[idx] = quickPot;
                else
                    Console.WriteLine($"QuickDiff TargetByteIndex {idx} out of range (coinbase size {coinbaseTx.Length})");
            }
            else Console.WriteLine($"QuickDiff TargetByteIndex has no value)");
        }
        else
        {
            // ----- normal (non-quickdiff) target byte -----
            // The client uses the difficulty that belongs to the current stratum job
            byte normalPot = FloorPoT(powSubmit.TargetByte);   // you must expose this value
            if (powSubmit.TargetByteIndex.HasValue)
            {
                int idx = powSubmit.TargetByteIndex.Value;
                if (idx >= 0 && idx < coinbaseTx.Length)
                {
                    coinbaseTx[idx] = powSubmit.TargetByte;
                }
                else
                    Console.WriteLine($"TargetByteIndex {idx} out of range (coinbase size {coinbaseTx.Length})");
            }
        }

        byte[] coinbaseHash = DoubleSha256(coinbaseTx);
        powSubmit.MerkleRoot = ComputeMerkleRoot(coinbaseHash, powSubmit.MerkleBranches, powSubmit.MerkleBranchCount.Value);
        if (powSubmit.JobId < _jobCache.Length && _jobCache[powSubmit.JobId] != null)
        {
            _jobCache[powSubmit.JobId]!.MerkleRoot = powSubmit.MerkleRoot; //For completeness, I guess.
        }

        // Reconstruct block header
        byte[] header = new byte[80];
        using (var stream = new MemoryStream(header))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(powSubmit.Version); // 4 bytes
            writer.Write(powSubmit.PrevBlockHash); // 32 bytes
            writer.Write(powSubmit.MerkleRoot);
            writer.Write(powSubmit.NTime); // 4 bytes
            writer.Write(powSubmit.NBits); // 4 bytes
            writer.Write(powSubmit.Nonce); // 4 bytes
        }
        //if (powSubmit.SubsidyOnly) Console.WriteLine("*** Got subsidy only coinbase message!");

        // Verify header

        // Compute hash
        byte[] testHash = DoubleSha256(header);  //testHeader

        // Achieved difficulty (hash-based)
        BigInteger hashInt = 0;
        for (int i = testHash.Length - 1; i >= 0; i--)
        {
            hashInt = (hashInt << 8) | testHash[i];
        }
        BigInteger maxTarget = BigInteger.Pow(2, 224) - 1;
        BigInteger achievedDifficultyBig = hashInt == 0 ? 0 : maxTarget / hashInt;
        double achievedDifficulty = achievedDifficultyBig <= 0 ? 0d : (double)achievedDifficultyBig;
        buildDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;

        //Console.WriteLine($"   -> ✅ Received PoW submission: JobID={powSubmit.JobId}, CoinbaseID={powSubmit.CoinbaseId}, IsBlock={powSubmit.IsBlock}, SubsidyOnly={powSubmit.SubsidyOnly}, QuickDiff={powSubmit.QuickDiff}, Username={powSubmit.Username}");

        bool shareAccepted = false;
        if (ShouldFastAcceptLowDifficultyDatumShare(
                achievedDifficulty,
                powSubmit.IsBlock,
                powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
                startedUtc,
                out string fastAcceptDetail))
        {
            stageStopwatch.Restart();
            ShareRecordingResult telemetryResult = _stateService.RecordDatumTelemetryShare(
                powSubmit.Address,
                powSubmit.Username,
                achievedDifficulty,
                startedUtc);
            double telemetryStateMutationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            shareAccepted = true;
            ResetStaleTemplateTracking();
            _stateService.RecordDatumSessionShareOutcome(_sessionId, accepted: true, affectedOnDeck: false, startedUtc);
            RecordPowSubmitProtocolOutcome(
                powSubmit,
                accepted: true,
                affectedOnDeck: false,
                rejectionReason: null,
                difficulty: achievedDifficulty,
                prevBlockHash: powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
                nonceOnlySubmit: nonceOnlySubmit,
                usedCachedJob: usedCachedJob,
                cachedJobAgeMs: cachedJobAgeMs,
                detail: fastAcceptDetail);

            stageStopwatch.Restart();
            await SendShareResponseAsync(powSubmit, accepted: true);
            responseSendDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
            totalStopwatch.Stop();
            RecordDatumShareResponseTelemetry(
                powSubmit,
                accepted: true,
                affectedOnDeck: false,
                rejectionReason: null,
                difficulty: telemetryResult.ComputedDifficulty,
                prevBlockHash: powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
                nonceOnlySubmit: nonceOnlySubmit,
                usedCachedJob: usedCachedJob,
                cachedJobAgeMs: cachedJobAgeMs,
                payloadBytes: payload.Length,
                coinbaseBytes: coinbaseTx.Length,
                coinb1Bytes: Coinb1.Length,
                coinb2Bytes: Coinb2.Length,
                parseDurationMs: parseDurationMs,
                buildDurationMs: buildDurationMs,
                validationDurationMs: 0,
                snapshotReadDurationMs: 0,
                snapshotReadLockWaitDurationMs: 0,
                snapshotReadLockBodyDurationMs: 0,
                shareCoreValidationDurationMs: 0,
                stateMutationDurationMs: telemetryStateMutationDurationMs,
                stateMutationLockWaitDurationMs: 0,
                stateMutationLockBodyDurationMs: 0,
                staleHandlingDurationMs: 0,
                responseSendDurationMs: responseSendDurationMs,
                totalDurationMs: totalStopwatch.Elapsed.TotalMilliseconds,
                startedUtc: startedUtc,
                responseSequence: responseSequence);
            return;
        }

        List<string> merklePath = [];
        if (powSubmit.MerkleBranches != null && powSubmit.MerkleBranchCount.HasValue)
        {
            for (int i = 0; i < powSubmit.MerkleBranchCount.Value; i++)
            {
                byte[] branch = powSubmit.MerkleBranches.Skip(i * 32).Take(32).ToArray();
                merklePath.Add(Convert.ToHexString(branch).ToLowerInvariant());
            }
        }

        stageStopwatch.Restart();
        var recordResult = await _stateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = powSubmit.Address,
            Username = powSubmit.Username,
            HeaderHex = Convert.ToHexString(header).ToLowerInvariant(),
            CoinbaseHex = Convert.ToHexString(coinbaseTx).ToLowerInvariant(),
            MerklePath = merklePath,
            PayoutSnapshotId = powSubmit.PayoutSnapshotId,
            PrevBlockHash = powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
            Difficulty = achievedDifficulty,
            TransportReceivedUtc = startedUtc,
            Source = "datum"
        }, "datum-block");
        validationDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
        shareAccepted = recordResult.Accepted;
        bool disconnectAfterResponse = false;

        stageStopwatch.Restart();
        if (recordResult.Accepted)
        {
            ResetStaleTemplateTracking();
        }
        else
        {
            disconnectAfterResponse = await HandlePotentialStaleTemplateRejectAsync(recordResult.RejectionReason);
        }
        staleHandlingDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
        _stateService.RecordDatumSessionShareOutcome(_sessionId, shareAccepted, recordResult.AffectedOnDeck, startedUtc);
        RecordPowSubmitProtocolOutcome(
            powSubmit,
            shareAccepted,
            recordResult.AffectedOnDeck,
            recordResult.RejectionReason,
            recordResult.ComputedDifficulty,
            powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
            nonceOnlySubmit,
            usedCachedJob,
            cachedJobAgeMs,
            detail: $"disconnectAfterResponse={disconnectAfterResponse}");

        //Console.WriteLine("-----------------------------------------");

        // Respond
        //#define DATUM_POW_SHARE_RESPONSE_ACCEPTED 0x50
        //#define DATUM_POW_SHARE_RESPONSE_ACCEPTED_TENTATIVELY 0x55
        //#define DATUM_POW_SHARE_RESPONSE_REJECTED 0x66

        stageStopwatch.Restart();
        await SendShareResponseAsync(powSubmit, shareAccepted);
        responseSendDurationMs = stageStopwatch.Elapsed.TotalMilliseconds;
        totalStopwatch.Stop();
        RecordDatumShareResponseTelemetry(
            powSubmit,
            shareAccepted,
            recordResult.AffectedOnDeck,
            recordResult.RejectionReason,
            recordResult.ComputedDifficulty,
            powSubmit.PrevBlockHash == null ? null : BitcoinHashes.ToDisplayHashHex(powSubmit.PrevBlockHash),
            nonceOnlySubmit,
            usedCachedJob,
            cachedJobAgeMs,
            payload.Length,
            coinbaseTx.Length,
            Coinb1.Length,
            Coinb2.Length,
            parseDurationMs,
            buildDurationMs,
            validationDurationMs,
            recordResult.SnapshotReadDurationMs,
            recordResult.SnapshotReadLockWaitDurationMs,
            recordResult.SnapshotReadLockBodyDurationMs,
            recordResult.ShareCoreValidationDurationMs,
            recordResult.StateMutationDurationMs,
            recordResult.StateMutationLockWaitDurationMs,
            recordResult.StateMutationLockBodyDurationMs,
            staleHandlingDurationMs,
            responseSendDurationMs,
            totalStopwatch.Elapsed.TotalMilliseconds,
            startedUtc,
            responseSequence);
        if (disconnectAfterResponse)
        {
            _client.Close();
        }
        //Console.WriteLine($"[SEND] Share Response [{(isHeaderValid && isValidCoinbase ? "ACCEPTED" : "REJECTED")}] (0x05, 0x{shareResponse.Status:X2})");
    }

    private async Task SendShareResponseAsync(PowSubmitMessage powSubmit, bool accepted)
    {
        var shareResponse = new ShareResponseMessage
        {
            Status = accepted ? (byte)0x50 : (byte)0x66,
            ReasonCode = (ushort)(accepted ? 0 : 1),
            Nonce = powSubmit.Nonce,
            TargetPot = powSubmit.TargetByte,
            JobId = powSubmit.JobId
        };

        var responsePayload = shareResponse.ToBytes();
        await SendEncryptedMessageAsync(0x05, responsePayload, isSigned: false, isEncryptedChannel: true, isEncryptedPubKey: false, messageLabel: accepted ? "share-response-accepted" : "share-response-rejected");
    }

    private bool ShouldFastAcceptLowDifficultyDatumShare(
        double achievedDifficulty,
        bool clientReportedBlock,
        string? prevBlockHash,
        DateTime nowUtc,
        out string detail)
    {
        detail = string.Empty;
        if (!_poolConfig.DatumLowDiffFastAcceptEnabled ||
            clientReportedBlock ||
            achievedDifficulty < 1d ||
            double.IsNaN(achievedDifficulty) ||
            double.IsInfinity(achievedDifficulty))
        {
            return false;
        }

        if (!_stateService.IsAcceptedParentBlockHash(prevBlockHash))
        {
            return false;
        }

        double admissionDifficulty = _stateService.GetWorkSetAdmissionDifficulty();
        if (achievedDifficulty >= admissionDifficulty)
        {
            return false;
        }

        int courtesyEvery = Math.Clamp(_poolConfig.DatumLowDiffCourtesyValidateEvery, 1, 1_000_000);
        int courtesySeconds = Math.Clamp(_poolConfig.DatumLowDiffCourtesyValidateSeconds, 1, 3600);
        bool countDue = _lowDiffFastAcceptedSinceCourtesy >= courtesyEvery;
        bool timeDue = _lastLowDiffCourtesyValidationUtc == DateTime.MinValue ||
                       (nowUtc - _lastLowDiffCourtesyValidationUtc).TotalSeconds >= courtesySeconds;
        if (countDue || timeDue)
        {
            _lowDiffFastAcceptedSinceCourtesy = 0;
            _lastLowDiffCourtesyValidationUtc = nowUtc;
            return false;
        }

        _lowDiffFastAcceptedSinceCourtesy += 1;
        detail =
            $"telemetryOnly=true; admissionFloor={FormatDifficulty(admissionDifficulty)}; fastAcceptedSinceCourtesy={_lowDiffFastAcceptedSinceCourtesy}";
        return true;
    }

    private void RecordDatumShareResponseTelemetry(
        PowSubmitMessage powSubmit,
        bool accepted,
        bool affectedOnDeck,
        string? rejectionReason,
        double difficulty,
        string? prevBlockHash,
        bool nonceOnlySubmit,
        bool usedCachedJob,
        double? cachedJobAgeMs,
        int payloadBytes,
        int coinbaseBytes,
        int coinb1Bytes,
        int coinb2Bytes,
        double parseDurationMs,
        double buildDurationMs,
        double validationDurationMs,
        double snapshotReadDurationMs,
        double snapshotReadLockWaitDurationMs,
        double snapshotReadLockBodyDurationMs,
        double shareCoreValidationDurationMs,
        double stateMutationDurationMs,
        double stateMutationLockWaitDurationMs,
        double stateMutationLockBodyDurationMs,
        double staleHandlingDurationMs,
        double responseSendDurationMs,
        double totalDurationMs,
        DateTime startedUtc,
        long responseSequence)
    {
        int slowThresholdMs = Math.Clamp(_poolConfig.DatumShareResponseSlowMs, 50, 30000);
        int acceptedSampleEvery = Math.Clamp(_poolConfig.DatumShareResponseAcceptedSampleEvery, 0, 100000);
        bool shouldSampleAccepted = acceptedSampleEvery > 0 && responseSequence % acceptedSampleEvery == 0;
        bool shouldRecord =
            !accepted ||
            affectedOnDeck ||
            totalDurationMs >= slowThresholdMs ||
            shouldSampleAccepted;

        if (!shouldRecord)
        {
            return;
        }

        _stateService.RecordDatumShareResponse(new BootDatumShareResponseTelemetry
        {
            SessionId = _sessionId,
            RemoteEndpoint = RemoteEndpointLabel,
            MinerAddress = powSubmit.Address,
            Username = powSubmit.Username,
            Accepted = accepted,
            AffectedOnDeck = affectedOnDeck,
            RejectionReason = rejectionReason,
            Difficulty = difficulty,
            PrevBlockHash = prevBlockHash,
            JobId = powSubmit.JobId,
            CoinbaseId = powSubmit.CoinbaseId,
            CoinbaserId = powSubmit.CoinbaserId,
            PayoutSnapshotId = powSubmit.PayoutSnapshotId,
            Nonce = powSubmit.Nonce,
            IsBlock = powSubmit.IsBlock,
            SubsidyOnly = powSubmit.SubsidyOnly,
            QuickDiff = powSubmit.QuickDiff,
            NonceOnlySubmit = nonceOnlySubmit,
            UsedCachedJob = usedCachedJob,
            CachedJobAgeMs = cachedJobAgeMs,
            TargetByte = powSubmit.TargetByte,
            TargetByteIndex = powSubmit.TargetByteIndex,
            PayloadBytes = payloadBytes,
            CoinbaseBytes = coinbaseBytes,
            Coinb1Bytes = coinb1Bytes,
            Coinb2Bytes = coinb2Bytes,
            MerkleBranchCount = powSubmit.MerkleBranchCount ?? 0,
            ParseDurationMs = parseDurationMs,
            BuildDurationMs = buildDurationMs,
            ValidationDurationMs = validationDurationMs,
            SnapshotReadDurationMs = snapshotReadDurationMs,
            SnapshotReadLockWaitDurationMs = snapshotReadLockWaitDurationMs,
            SnapshotReadLockBodyDurationMs = snapshotReadLockBodyDurationMs,
            ShareCoreValidationDurationMs = shareCoreValidationDurationMs,
            StateMutationDurationMs = stateMutationDurationMs,
            StateMutationLockWaitDurationMs = stateMutationLockWaitDurationMs,
            StateMutationLockBodyDurationMs = stateMutationLockBodyDurationMs,
            StaleHandlingDurationMs = staleHandlingDurationMs,
            ResponseSendDurationMs = responseSendDurationMs,
            TotalDurationMs = totalDurationMs,
            TimestampUtc = startedUtc
        });
    }
    
    private static byte FloorPoT(ulong x)
    {
        if (x == 0) return 0;

        byte pos = 0;
        while (x > 1)          // keep shifting while x > 1
        {
            x >>= 1;           // x = x >> 1
            pos++;
        }
        return pos;
    }

    private ulong CalculateDifficulty(byte targetByte, ushort? targetByteIndex, byte[]? nBits)
    {
        if (nBits == null || targetByteIndex == null) return 0; // Minimal check
        // Simplified: Assume target_byte is difficulty exponent
        return 1UL << targetByte; // Adjust based on actual PoT logic
    }

    private bool VerifyBlockHeader(int version, uint nTime, uint nonce, byte[]? prevBlockHash, byte[]? merkleRoot, byte[]? nBits)
    {
        // Debug: Log input parameters
        //Console.WriteLine($"VerifyBlockHeader Inputs:");
        //Console.WriteLine($"  Version: 0x{version:X8}");
        //Console.WriteLine($"  PrevBlockHash: {(prevBlockHash != null ? BitConverter.ToString(prevBlockHash).Replace("-", "") : "null")}");
        //Console.WriteLine($"  MerkleRoot: {(merkleRoot != null ? BitConverter.ToString(merkleRoot).Replace("-", "") : "null")}");
        //Console.WriteLine($"  nTime: 0x{nTime:X8} ({nTime})");
        //Console.WriteLine($"  nBits: {(nBits != null ? BitConverter.ToString(nBits).Replace("-", "") : "null")}");
        //Console.WriteLine($"  Nonce: 0x{nonce:X8}");

        // Check for null inputs
        if (prevBlockHash == null || merkleRoot == null || nBits == null)
        {
            Console.WriteLine("  Result: False (null input detected)");
            return false;
        }

        // Validate input lengths
        if (prevBlockHash.Length != 32 || merkleRoot.Length != 32 || nBits.Length != 4)
        {
            Console.WriteLine($"  Result: False (invalid lengths - PrevBlockHash: {prevBlockHash.Length}, MerkleRoot: {merkleRoot.Length}, nBits: {nBits.Length})");
            return false;
        }

        // Reconstruct block header
        byte[] header = new byte[80];
        using (var stream = new MemoryStream(header))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(version); // 4 bytes, little-endian
            writer.Write(prevBlockHash); // 32 bytes
            writer.Write(merkleRoot); // 32 bytes
            writer.Write(nTime); // 4 bytes, little-endian
            writer.Write(nBits); // 4 bytes
            writer.Write(nonce); // 4 bytes, little-endian
        }

        // Debug: Log constructed header
        //Console.WriteLine($"  Constructed Header: {BitConverter.ToString(header).Replace("-", "")}");

        // Compute double SHA256 hash
        byte[] hash = DoubleSha256(header);
        //Console.WriteLine($"  Block Hash: {BitConverter.ToString(hash).Replace("-", "")}");

        // Compute target from nBits
        //byte[] target = ComputeTargetFromNBits(nBits);  //This version is the target for a new block
        //Console.WriteLine($"  Target: {BitConverter.ToString(target).Replace("-", "")}");

        // Compare hash to target (Bitcoin: hash <= target in big-endian)
        bool isValid = true; //CompareHashToTarget(hash, target);
        //Console.WriteLine($"  Difficulty Check: Hash <= Target? {isValid}");

        // Debug: Log result
        //Console.WriteLine($"  Result: {isValid}");
        return isValid;
    }

    private byte[] ComputeTargetFromNBits(byte[] nBits)
    {
        if (nBits.Length != 4) throw new ArgumentException("nBits must be 4 bytes");
        uint nBitsValue = BitConverter.ToUInt32(nBits, 0);
        int exponent = (int)(nBitsValue >> 24); // First byte is exponent
        uint mantissa = nBitsValue & 0xFFFFFF; // Last 3 bytes are mantissa
        if (exponent < 3) exponent = 3; // Minimum size to avoid overflow

        // Target = mantissa * 2^(8*(exponent - 3))
        byte[] target = new byte[32];
        byte[] mantissaBytes = BitConverter.GetBytes(mantissa);
        if (BitConverter.IsLittleEndian) Array.Reverse(mantissaBytes); // Convert to big-endian
        int shift = 8 * (exponent - 3);
        int mantissaLength = mantissaBytes.TakeWhile(b => b != 0).Count() + 1; // Non-zero bytes + 1
        for (int i = 0; i < mantissaLength && i < 4; i++)
        {
            if (32 - mantissaLength + i >= 0 && 32 - mantissaLength + i < 32)
                target[32 - mantissaLength + i] = mantissaBytes[i];
        }

        Console.WriteLine($"  ComputeTargetFromNBits: nBits=0x{nBitsValue:X8}, Exponent={exponent}, Mantissa=0x{mantissa:X6}, Target={BitConverter.ToString(target).Replace("-", "")}");
        return target;
    }

    private bool CompareHashToTarget(byte[] hash, byte[] target)
    {
        // Bitcoin compares hash <= target in big-endian
        for (int i = 0; i < 32; i++)
        {
            if (hash[i] < target[i]) return true;
            if (hash[i] > target[i]) return false;
        }
        return true; // Equal
    }

    private byte[] ComputeMerkleRoot(byte[] coinbaseHash, byte[] merkleBranches, byte count)
    {
        // coinbaseHash is raw 32-byte little-endian from DoubleSha256
        byte[] current = coinbaseHash;

        for (int i = 0; i < count; i++)
        {
            // Extract branch (already little-endian, as sent by client)
            byte[] branch = merkleBranches.Skip(i * 32).Take(32).ToArray();

            // DO NOT REVERSE — keep little-endian
            byte[] combined = current.Concat(branch).ToArray();

            // Hash → output is little-endian
            current = DoubleSha256(combined);
        }

        return current; // little-endian Merkle root
    }

    private (bool isValid, List<(string Address, ulong Amount)> outputs) VerifyCoinbaseTransaction(byte[]? coinb1, byte[]? coinb2, ulong? coinbaseValue)
    {
        if (coinb2 == null || coinbaseValue == null)
            return (false, new List<(string, ulong)>());

        var outputs = new List<(string Address, ulong Amount)>();
        try
        {
            using var stream = new MemoryStream(coinb2);
            using var reader = new BinaryReader(stream);
            byte outputCount = reader.ReadByte(); // varint for output count
            if (outputCount > 100) return (false, outputs); // Sanity check

            ulong totalAmount = 0;
            for (int i = 0; i < outputCount; i++)
            {
                if (stream.Position >= stream.Length) return (false, outputs);
                ulong amount = reader.ReadUInt64(); // 8-byte amount
                totalAmount += amount;
                ulong scriptLen = ReadVarInt(reader);
                if (scriptLen > 100 || stream.Position + (long)scriptLen > stream.Length) return (false, outputs);
                byte[] script = reader.ReadBytes((int)scriptLen);
                string address = ScriptToAddress(script);
                outputs.Add((address, amount));
            }

            // Verify total amount does not exceed coinbase value
            bool isValid = totalAmount <= coinbaseValue.Value;
            return (isValid, outputs);
        }
        catch
        {
            return (false, outputs);
        }
    }

    private ulong ReadVarInt(BinaryReader reader)
    {
        byte b = reader.ReadByte();
        if (b < 0xFD) return b;
        if (b == 0xFD) return reader.ReadUInt16();
        if (b == 0xFE) return reader.ReadUInt32();
        return reader.ReadUInt64();
    }

    private string ScriptToAddress(byte[] script)
    {
        return BitcoinScript.ScriptToAddress(script, _poolConfig.BitcoinNetwork);
    }

    // Local function to validate an address candidate
    bool IsValidAddress(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        try
        {
            // Check format based on prefix
            if (candidate.StartsWith("bc1") || candidate.StartsWith("tb1"))
            {
                // Use your updated Bech32.Decode
                Bech32.Decode(candidate); 
                return true;
            }
            else
            {
                // Fallback to Base58Check (P2PKH/P2SH)
                // Basic length check to avoid costly math on obvious junk
                if (candidate.Length < 26 || candidate.Length > 35) return false;
                
                Base58Check.Decode(candidate);
                return true;
            }
        }
        catch (Exception)
        {
            // Address format is invalid (Format, Checksum, or Length exception)
            return false;
        }
    }

    private static byte[] DoubleSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        byte[] hash1 = sha256.ComputeHash(data);
        return sha256.ComputeHash(hash1);
    }

    public static string FormatDifficulty(double difficulty)
    {
        if (difficulty < 1000) return difficulty.ToString("F2"); // Less than 1k, show as is
        if (difficulty < 1000000) return (difficulty / 1000).ToString("F2") + "k"; // Thousands
        if (difficulty < 1000000000) return (difficulty / 1000000).ToString("F2") + "M"; // Millions
        if (difficulty < 1000000000000) return (difficulty / 1000000000).ToString("F2") + "G"; // Billions
        if (difficulty < 1000000000000000) return (difficulty / 1000000000000).ToString("F2") + "T"; // Trillions
        if (difficulty < 1000000000000000000) return (difficulty / 1000000000000000).ToString("F2") + "P"; // Quadrillions
        return (difficulty / 1000000000000000000).ToString("F2") + "E"; // Quintillions
    }

    /// Generic helper to encrypt and send a message using the client's public or (more likely) using the shared channel secret.
    private async Task SendEncryptedMessageAsync(byte protoCmd, byte[] payload, bool isSigned = false, bool isEncryptedChannel = true, bool isEncryptedPubKey = false, string? messageLabel = null)
    {
        await _sendLock.WaitAsync();
        try
        {
            var sendStopwatch = Stopwatch.StartNew();
            byte[] finalMessageBody = new byte[payload.Length + 48]; //Try +48 I guess?  + LibSodium.CryptoBox.MacLen wasn't enough
            bool sendCompleted = false;

            if (isEncryptedPubKey) //encrypt using the client's public key, usually for the 0x02 handshake response message
            {
                if (_clientSessionPubKey == null) throw new InvalidOperationException("Cannot send sealed message without client session public key.");
                var clientPubKey = _clientSessionPubKey.Export(KeyBlobFormat.RawPublicKey); // Client’s session X25519 public key
                finalMessageBody = LibSodium.CryptoBox.EncryptWithPublicKey(finalMessageBody, payload, clientPubKey).ToArray();
            }
            else if (isEncryptedChannel)  // Symmetric encryption with shared secret for other messages
            {
                if (_channelSharedSecret == null) throw new InvalidOperationException("Cannot send encrypted message without a shared secret.");
                if (_sessionNonceSender == null) throw new InvalidOperationException("Cannot send encrypted message without a sender nonce.");
                finalMessageBody = LibSodium.CryptoBox.EncryptWithSharedKey(finalMessageBody, payload, _channelSharedSecretBytes, null, _sessionNonceSender).ToArray();
            }
            else
            {
                // Unencrypted message (not typical, but handle for completeness)
                finalMessageBody = payload;
                Console.WriteLine($"📦 Plaintext payload: {BitConverter.ToString(payload, 0, Math.Min(payload.Length, 64))}...");
            }

            // Construct header
            var header = new DatumHeader
            {
                CmdLen = (uint)finalMessageBody.Length,
                IsEncryptedChannel = isEncryptedChannel,
                IsEncryptedPubKey = isEncryptedPubKey,
                IsSigned = isSigned,
                ProtoCmd = protoCmd
            };

            byte[] headerBytes = header.ToBytes();
            uint sendingHeaderKeyBefore = _sendingHeaderKey;
            uint headerValue = BitConverter.ToUInt32(headerBytes, 0);
            headerValue ^= _sendingHeaderKey;
            var xoredHeaderBytes = BitConverter.GetBytes(headerValue);
            _sendingHeaderKey = DatumHeaderXorFeedback(_sendingHeaderKey);  //Increment the sending header for next time

            try
            {
                // Send header and body together
                var message = xoredHeaderBytes.Concat(finalMessageBody.ToArray()).ToArray();
                await _stream.WriteAsync(message, 0, message.Length);
                await _stream.FlushAsync();
                sendCompleted = true;
                _lastServerMessageSentUtc = DateTime.UtcNow;
                sendStopwatch.Stop();
                RecordDatumProtocolEvent(new BootDatumProtocolEvent
                {
                    Direction = "send",
                    EventType = "send",
                    MessageLabel = messageLabel ?? $"proto-0x{protoCmd:X2}",
                    ProtoCmd = protoCmd,
                    IsSigned = isSigned,
                    IsEncryptedChannel = isEncryptedChannel,
                    IsEncryptedPubKey = isEncryptedPubKey,
                    CmdLen = (uint)finalMessageBody.Length,
                    BytesRead = message.Length,
                    ExpectedBytes = message.Length,
                    RawHeaderHex = Convert.ToHexString(xoredHeaderBytes).ToLowerInvariant(),
                    DecodedHeaderHex = Convert.ToHexString(headerBytes).ToLowerInvariant(),
                    HeaderKeyBefore = sendingHeaderKeyBefore,
                    HeaderKeyAfter = _sendingHeaderKey,
                    DurationMs = sendStopwatch.Elapsed.TotalMilliseconds,
                    Detail = $"payloadBytes={payload.Length}; bodyBytes={finalMessageBody.Length}"
                });
            }
            catch (Exception ex)
            {
                sendStopwatch.Stop();
                RecordDatumProtocolEvent(new BootDatumProtocolEvent
                {
                    Direction = "send",
                    EventType = "send-failed",
                    MessageLabel = messageLabel ?? $"proto-0x{protoCmd:X2}",
                    ProtoCmd = protoCmd,
                    IsSigned = isSigned,
                    IsEncryptedChannel = isEncryptedChannel,
                    IsEncryptedPubKey = isEncryptedPubKey,
                    CmdLen = (uint)finalMessageBody.Length,
                    RawHeaderHex = Convert.ToHexString(xoredHeaderBytes).ToLowerInvariant(),
                    DecodedHeaderHex = Convert.ToHexString(headerBytes).ToLowerInvariant(),
                    HeaderKeyBefore = sendingHeaderKeyBefore,
                    HeaderKeyAfter = _sendingHeaderKey,
                    DurationMs = sendStopwatch.Elapsed.TotalMilliseconds,
                    Detail = $"{ex.GetType().Name}: {ex.Message}"
                });
                throw;
            }

            // Increment nonce only for channel encryption
            if (sendCompleted && isEncryptedChannel && _sessionNonceSender != null)
            {
                _sessionNonceSender = IncrementNonce(_sessionNonceSender);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private uint DatumHeaderXorFeedback(uint i)
    {
        uint s = 0xb10cfeed;
        uint h = s;
        uint k = i;
        k *= 0xcc9e2d51;
        k = (k << 15) | (k >> 17);
        k *= 0x1b873593;
        h ^= k;
        h = (h << 13) | (h >> 19);
        h = h * 5 + 0xe6546b64;
        h ^= 4;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}


// =================================================================================
// 4. DATUM PROTOCOL MESSAGE CLASSES
// =================================================================================
// These classes represent the data structures of the DATUM protocol. They contain
// methods for serializing to a byte array ('ToBytes') and deserializing from a
// byte array ('FromBytes'), mimicking the C structs from the reference client implementation.
// =================================================================================

/// Represents the 4-byte header at the start of every DATUM message.
/// Provides methods to pack/unpack the bitfields into a uint32.
public class DatumHeader
{
    public uint CmdLen { get; set; }              // 22 bits
    public bool IsSigned { get; set; }            // 1 bit
    public bool IsEncryptedPubKey { get; set; }   // 1 bit
    public bool IsEncryptedChannel { get; set; }  // 1 bit
    public byte ProtoCmd { get; set; }            // 5 bits

    public byte[] ToBytes()
    {
        uint val = 0;
        val |= (CmdLen & 0x3FFFFF);
        val |= (IsSigned ? 1u : 0u) << 24;
        val |= (IsEncryptedPubKey ? 1u : 0u) << 25;
        val |= (IsEncryptedChannel ? 1u : 0u) << 26;
        val |= ((uint)ProtoCmd & 0x1F) << 27;

        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, val);
        return buffer;
    }

    public static DatumHeader FromBytes(byte[] buffer)
    {
        uint val = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return new DatumHeader
        {
            CmdLen = val & 0x3FFFFF,
            IsSigned = ((val >> 24) & 1) == 1,
            IsEncryptedPubKey = ((val >> 25) & 1) == 1,
            IsEncryptedChannel = ((val >> 26) & 1) == 1,
            ProtoCmd = (byte)((val >> 27) & 0x1F)
        };
    }
}

// CLIENT: Hello message (0x01)
public class HelloMessage
{
    public byte[] ClientSigningPubKey { get; set; } = new byte[32]; // Ed25519 public key
    public byte[] ClientEncryptPubKey { get; set; } = new byte[32]; // X25519 public key
    public byte[] ClientSessionSigningPubKey { get; set; } = new byte[32]; // Ed25519 public key
    public byte[] ClientSessionEncryptPubKey { get; set; } = new byte[32]; // X25519 public key
    public byte[]? version; // Variable length string ending with '/', max 127 bytes
    public byte[]? commitHash; // Variable length null-terminated string, max 127 bytes
    public byte[]? xorKey; // Exactly 4-byte key
    public byte[]? cryptoSignBytes; // Exactly 64 bytes (signature)
    public byte[]? cryptoBoxSealBytes; // Placeholder, assuming padding or ignored

    public static (HelloMessage? Message, int BytesConsumed) FromBytes(byte[] data)
    {
        try
        {
            const int cryptoSignBytes = 64; // CRYPTO_SIGN_BYTES  //TODO: this should just reference the LibSodium library contstants instead of hardcoded
            const int maxStringLength = 127; // Max length for version and commit hash
            const int publicKeyLength = 32; // Length of each public key
            const int xorKeyLength = 4; // Length of nk

            // Validate minimum length: public keys (128) + version (1) + '/' (1) + commit (1) + null (1) + 0xFE (1) + nk (4) + signature (64)
            const int minLength = 128 + 1 + 1 + 1 + 1 + 1 + 4 + cryptoSignBytes;
            if (data.Length < minLength)
            {
                Console.WriteLine($"❌ Hello message too short ({data.Length} bytes, expected at least {minLength})");
                return (null, -1);
            }

            using var stream = new MemoryStream(data);
            using var reader = new BinaryReader(stream);

            var msg = new HelloMessage();

            // Step 1: Read public keys (128 bytes total)
            reader.Read(msg.ClientSigningPubKey, 0, publicKeyLength);
            reader.Read(msg.ClientEncryptPubKey, 0, publicKeyLength);
            reader.Read(msg.ClientSessionSigningPubKey, 0, publicKeyLength);
            reader.Read(msg.ClientSessionEncryptPubKey, 0, publicKeyLength);

            // Step 2: Read version string (ends with '/', max 127 bytes)
            long versionStart = stream.Position;
            byte[] versionBuffer = new byte[maxStringLength + 1]; // +1 for '/'
            int versionIndex = 0;
            while (versionIndex < maxStringLength)
            {
                if (stream.Position >= data.Length)
                {
                    Console.WriteLine($"❌ No '/' separator found in version");
                    return (null, -1);
                }
                byte b = reader.ReadByte();
                versionBuffer[versionIndex++] = b;
                if (b == (byte)'/') break;
                if (b == 0)
                {
                    Console.WriteLine($"❌ Unexpected null in version at offset {stream.Position - 1}");
                    return (null, -1);
                }
            }
            if (versionIndex == maxStringLength && versionBuffer[versionIndex - 1] != (byte)'/')
            {
                Console.WriteLine($"❌ Version string too long or missing '/'");
                return (null, -1);
            }
            msg.version = new byte[versionIndex];
            Array.Copy(versionBuffer, msg.version, versionIndex);
            //Console.WriteLine($"🔓 Version: {Encoding.ASCII.GetString(msg.version)}");

            // Step 3: Read commit hash (null-terminated, max 127 bytes)
            long commitStart = stream.Position;
            byte[] commitBuffer = new byte[maxStringLength + 1]; // +1 for null
            int commitIndex = 0;
            while (commitIndex < maxStringLength)
            {
                if (stream.Position >= data.Length)
                {
                    Console.WriteLine($"❌ No null terminator for commit hash");
                    return (null, -1);
                }
                byte b = reader.ReadByte();
                commitBuffer[commitIndex++] = b;
                if (b == 0) break;
            }
            if (commitIndex == maxStringLength && commitBuffer[commitIndex - 1] != 0)
            {
                Console.WriteLine($"❌ Commit hash too long or missing null");
                return (null, -1);
            }
            msg.commitHash = new byte[commitIndex];
            Array.Copy(commitBuffer, msg.commitHash, commitIndex);
            //Console.WriteLine($"🔓 Commit hash: {Encoding.ASCII.GetString(msg.commitHash, 0, commitIndex - 1)}");

            // Step 4: Handle optional git tag (if present, wrapped in '()')
            long pos = stream.Position;
            if (pos < data.Length && reader.PeekChar() == '(')
            {
                reader.ReadByte(); // Skip '('
                long tagStart = stream.Position;
                byte[] tagBuffer = new byte[maxStringLength + 1];
                int tagIndex = 0;
                while (tagIndex < maxStringLength)
                {
                    if (stream.Position >= data.Length)
                    {
                        Console.WriteLine($"❌ No null terminator for git tag");
                        return (null, -1);
                    }
                    byte b = reader.ReadByte();
                    tagBuffer[tagIndex++] = b;
                    if (b == 0) break;
                }
                if (tagIndex == maxStringLength && tagBuffer[tagIndex - 1] != 0)
                {
                    Console.WriteLine($"❌ Git tag too long or missing null");
                    return (null, -1);
                }
                if (stream.Position >= data.Length || reader.ReadByte() != ')')
                {
                    Console.WriteLine($"❌ Expected ')' after git tag at offset {stream.Position}");
                    return (null, -1);
                }
                msg.commitHash = new byte[tagIndex + commitIndex + 2]; // Include '(' and ')'
                msg.commitHash[0] = (byte)'(';
                Array.Copy(tagBuffer, 0, msg.commitHash, 1, tagIndex);
                msg.commitHash[tagIndex + 1] = (byte)')';
                Array.Copy(commitBuffer, 0, msg.commitHash, tagIndex + 2, commitIndex);
                //Console.WriteLine($"🔓 Git tag: {Encoding.ASCII.GetString(tagBuffer, 0, tagIndex - 1)}");
            }
            else
            {
                // No git tag, use commit hash as is
                msg.commitHash = commitBuffer.Take(commitIndex).ToArray();
            }

            // Step 5: Check null terminator
            //if (stream.Position >= data.Length || reader.ReadByte() != 0)
            //{
            //    Console.WriteLine($"❌ Expected null at offset {stream.Position - 1}, found {(stream.Position <= data.Length ? data[stream.Position - 1].ToString("X2") : "EOF")}");
            //    return (null, -1);
            //}

            // Step 6: Check 0xFE marker
            if (stream.Position >= data.Length || reader.ReadByte() != 0xFE)
            {
                Console.WriteLine($"❌ Expected 0xFE at offset {stream.Position - 1}, found {(stream.Position <= data.Length ? data[stream.Position - 1].ToString("X2") : "EOF")}");
                return (null, -1);
            }

            // Step 7: Read XOR key (4 bytes)
            msg.xorKey = new byte[xorKeyLength];
            if (stream.Position + xorKeyLength > data.Length)
            {
                Console.WriteLine($"❌ Insufficient bytes for XOR key at offset {stream.Position}");
                return (null, -1);
            }
            reader.Read(msg.xorKey, 0, xorKeyLength);
            uint nk = BitConverter.ToUInt32(msg.xorKey, 0);
            //Console.WriteLine($"🔓 XOR key (nk): 0x{nk:X8} at offset {stream.Position - xorKeyLength}");

            // Step 8: Skip padding (variable, 1–200 bytes)
            long paddingStart = stream.Position;
            int paddingLength = 0;
            while (stream.Position < data.Length - cryptoSignBytes)
            {
                reader.ReadByte();
                paddingLength++;
                if (paddingLength > 200)
                {
                    Console.WriteLine($"❌ Padding too long (>200 bytes) at offset {stream.Position}");
                    return (null, -1);
                }
            }
            if (paddingLength < 1)
            {
                Console.WriteLine($"❌ Padding too short (<1 byte) at offset {paddingStart}");
                return (null, -1);
            }
            //Console.WriteLine($"🔓 Padding length: {paddingLength} bytes");

            // Step 9: Read signature (64 bytes)
            msg.cryptoSignBytes = new byte[cryptoSignBytes];
            if (stream.Position + cryptoSignBytes > data.Length)
            {
                Console.WriteLine($"❌ Insufficient bytes for signature at offset {stream.Position}");
                return (null, -1);
            }
            reader.Read(msg.cryptoSignBytes, 0, cryptoSignBytes);
            //Console.WriteLine($"🔓 Signature: {BitConverter.ToString(msg.cryptoSignBytes, 0, 16)}...");

            // Step 10: Handle cryptoBoxSealBytes (assuming placeholder or padding)
            msg.cryptoBoxSealBytes = new byte[0]; // Ignore for now, adjust if needed
            //Console.WriteLine($"🔓 Note: cryptoBoxSealBytes set to empty (adjust if needed)");

            // Return populated message and bytes consumed
            int bytesConsumed = (int)stream.Position;
            Console.WriteLine($"🔓 Hello message parsed successfully, consumed {bytesConsumed} bytes");
            return (msg, bytesConsumed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error parsing hello message: {ex.Message}");
            return (null, -1);
        }
    }
}
// SERVER: Handshake Response message (0x02)
public class HandshakeResponseMessage
{
    public byte[] ClientSigningPubKey { get; set; } = new byte[32];
    public byte[] ClientEncryptPubKey { get; set; } = new byte[32];
    public byte[] ClientSessionSigningPubKey { get; set; } = new byte[32];
    public byte[] ClientSessionEncryptPubKey { get; set; } = new byte[32];
    public byte[] ServerSessionSigningPubKey { get; set; } = new byte[32];
    public byte[] ServerSessionEncryptPubKey { get; set; } = new byte[32];
    public string MessageOfTheDay { get; set; } = "Hello...Neo.";
    
    // Helper to write a null-terminated string.  Seems silly, but whatever. It works.
    private void WriteNullTerminatedString(BinaryWriter writer, string s)
    {
        writer.Write(Encoding.UTF8.GetBytes(s));
        writer.Write((byte)0);
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(ClientSigningPubKey);
        writer.Write(ClientEncryptPubKey);
        writer.Write(ClientSessionSigningPubKey);
        writer.Write(ClientSessionEncryptPubKey);
        writer.Write(ServerSessionSigningPubKey);
        writer.Write(ServerSessionEncryptPubKey);
        WriteNullTerminatedString(writer, MessageOfTheDay);
        
        return stream.ToArray();
    }
}

// CLIENT: Coinbaser Fetch message (0x05, 0x10)
public class CoinbaserFetchMessage
{
    public ulong RewardValue { get; set; }

    public static CoinbaserFetchMessage FromBytes(byte[] data)
    {
        // The payload only contains the reward value.
        return new CoinbaserFetchMessage
        {
            RewardValue = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0, 8))
        };
    }
}

// SERVER: Coinbaser Fetch Response (0x05, 0x11)
public class CoinbaserFetchResponseMessage
{
    public byte CoinbaserId { get; set; }
    public List<PayoutInfo> Payouts { get; set; } = new();
    public string BitcoinNetwork { get; set; } = BitcoinScript.Mainnet;

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        
        // DATUM echoes this ID back in PoW submissions, which lets the server
        // validate old jobs against the exact payout snapshot they were built on.
        writer.Write(CoinbaserId);
        
        foreach (var payout in Payouts)
        {
            writer.Write(payout.Value); // 8 bytes (amount)

            byte[] script = BitcoinScript.AddressToScriptPubKey(payout.Address, BitcoinNetwork);
            if (script.Length > byte.MaxValue)
            {
                throw new InvalidOperationException($"Unsupported scriptPubKey length {script.Length} for payout address {payout.Address}.");
            }
            
            writer.Write((byte)script.Length); // 1 byte script length
            writer.Write(script); // Script bytes
        }
        return stream.ToArray();
    }
}
public class PayoutInfo
{
    public ulong Value { get; set; }  //in Satoshis, or 1/100,000,000 BTC
    public string Address { get; set; } = string.Empty;
    public string Username {get; set; } = string.Empty;
    public double Difficulty { get; set; } = 0;
    public string DiffString { get; set; } = "0";
}

// CLIENT: PoW Submit message (0x05, 0x27)
public class PowSubmitMessage
{
    public byte JobId { get; set; }
    public byte CoinbaseId { get; set; }
    public bool IsBlock { get; set; }
    public bool SubsidyOnly { get; set; }
    public bool QuickDiff { get; set; }
    public byte TargetByte { get; set; }
    public uint NTime { get; set; }
    public uint Nonce { get; set; }
    public int Version { get; set; }
    public byte ExtranonceSize { get; set; }
    public byte[] Extranonce { get; set; } = new byte[12];
    public string Username { get; set; } = string.Empty;
    public string Address {get; set; } = string.Empty;
    public byte[] Reserved { get; set; } = new byte[4];
    public byte[]? PrevBlockHash { get; set; }
    public ushort? TargetByteIndex { get; set; }
    public byte[]? NBits { get; set; }
    public byte? CoinbaserId { get; set; }
    public string? PayoutSnapshotId { get; set; }
    public uint? Height { get; set; }
    public ulong? CoinbaseValue { get; set; }
    public uint? TransactionCount { get; set; }
    public uint? TotalWeight { get; set; }
    public uint? TotalSize { get; set; }
    public uint? TotalSigops { get; set; }
    public byte? MerkleBranchCount { get; set; }
    public byte[]? MerkleBranches { get; set; }
    
    //public byte[,]? Coinb1 { get; set; }
    //public byte[,]? Coinb2 { get; set; }
    public (byte[] Coinb1, byte[] Coinb2)[] CoinbasePairs { get; set; } = new (byte[], byte[])[8];
    public byte[]? SubsidyOnlyCoinb1 { get; set; }
    public byte[]? SubsidyOnlyCoinb2 { get; set; }
    public byte[]? MerkleRoot { get; set; } // Added

    public static PowSubmitMessage FromBytes(byte[] data)
    {
        if (data.Length < 30) throw new ArgumentException("Invalid PoW submission length");
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        var result = new PowSubmitMessage
        {
            JobId = reader.ReadByte(), // offset 1
            CoinbaseId = reader.ReadByte(), // offset 2
        };
        byte flags = reader.ReadByte(); // offset 3
        result.IsBlock = (flags & 0x01) != 0;
        result.SubsidyOnly = (flags & 0x02) != 0;
        result.QuickDiff = (flags & 0x04) != 0;
        result.TargetByte = reader.ReadByte(); // offset 4
        result.NTime = reader.ReadUInt32(); // offset 5
        result.Nonce = reader.ReadUInt32(); // offset 9
        result.Version = reader.ReadInt32(); // offset 13
        result.ExtranonceSize = reader.ReadByte(); // offset 17
        if (result.ExtranonceSize != 12) throw new ArgumentException($"Unsupported extranonce size: {result.ExtranonceSize}");
        result.Extranonce = reader.ReadBytes(12); // offset 18
        var usernameBytes = new List<byte>();
        while (stream.Position < stream.Length)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            usernameBytes.Add(b);
        }
        result.Username = Encoding.UTF8.GetString(usernameBytes.ToArray());
        //Console.WriteLine($"POW share from: {result.Username}");
        
        string address = result.Username;
        int dotIndex = address.IndexOf('.');
        if (dotIndex != -1)
        {
            // Trim the string to include only the part before the dot
            address = address.Substring(0, dotIndex);
        }
        result.Address = address;
        result.Reserved = reader.ReadBytes(4); // offset 30 + username.Length

        // Process optional sections (0x01, 0x02) until 0xFE
        bool hasMerkleData = false;
        bool hasCoinbaseData = false;
        while (stream.Position < stream.Length)
        {
            byte flag = reader.ReadByte();
            if (flag == 0xFE) break; // Terminator
            if (flag == 0x01) // Merkle branches
            {
                hasMerkleData = true;
                result.PrevBlockHash = reader.ReadBytes(32);
                result.TargetByteIndex = reader.ReadUInt16();
                result.NBits = reader.ReadBytes(4);
                result.CoinbaserId = reader.ReadByte();
                result.Height = reader.ReadUInt32();
                result.CoinbaseValue = reader.ReadUInt64();
                result.TransactionCount = reader.ReadUInt32();
                result.TotalWeight = reader.ReadUInt32();
                result.TotalSize = reader.ReadUInt32();
                result.TotalSigops = reader.ReadUInt32();
                result.MerkleBranchCount = reader.ReadByte();
                result.MerkleBranches = reader.ReadBytes((int)(result.MerkleBranchCount * 32));
            }
            else if (flag == 0x02) // Coinbase data
            {
                //TODO: Deal with subsidyOnly coinbases, which currently are not set.
                hasCoinbaseData = true;
                byte coinbaseType = reader.ReadByte();
                ushort coinb1Len = reader.ReadUInt16();
                ushort coinb2Len = reader.ReadUInt16();
                byte[] coinb1 = reader.ReadBytes(coinb1Len);
                byte[] coinb2 = reader.ReadBytes(coinb2Len);
                
                if(result.CoinbaseId == 255)
                {
                    //Console.WriteLine($"result.CoinbaseID={result.CoinbaseId}");
                    //Console.WriteLine($"result.cb only = {result.SubsidyOnly}");
                    result.SubsidyOnlyCoinb1 = coinb1;
                    result.SubsidyOnlyCoinb2 = coinb2;
                }
                else
                {
                    result.CoinbasePairs[result.CoinbaseId] = (coinb1, coinb2);
                }
                
                //Console.WriteLine($"Stored CoinbaseId {result.CoinbaseId}: Coinb1={coinb1Len} bytes, Coinb2={coinb2Len} bytes");
            }
            else
            {
                throw new ArgumentException($"Unknown flag: 0x{flag:X2}");
            }
        }
        if(hasCoinbaseData ^ hasMerkleData)
        {
            //if (hasCoinbaseData) Console.WriteLine("*** Got coinbase without Merkle Data!!!");
            if (hasMerkleData) Console.WriteLine("*** Got Merkle Data without Coinbase data!!");
        }

        return result;
    }

    private static byte[] DoubleSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        byte[] hash1 = sha256.ComputeHash(data);
        return sha256.ComputeHash(hash1);
    }

    private static byte[] ComputeMerkleRoot(byte[] coinbaseHash, byte[] merkleBranches, byte count)
    {
        byte[] current = coinbaseHash;
        for (int i = 0; i < count; i++)
        {
            byte[] branch = merkleBranches.Skip(i * 32).Take(32).ToArray();
            current = DoubleSha256(current.Concat(branch).ToArray());
        }
        return current;
    }
}

// SERVER: Share Response message (0x05, 0x8F)
// TODO: This looks incomplete.  I think.
public class ShareResponseMessage
{
    public byte Status { get; set; } // 0x50, 0x55, or 0x66
    public ushort ReasonCode { get; set; } // For rejected shares
    public uint Nonce { get; set; } // Share nonce
    public byte TargetPot { get; set; } // Difficulty exponent
    public byte JobId { get; set; } // Job ID

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x8F);
        writer.Write(Status); // 1 byte
        writer.Write(ReasonCode); // 2 bytes, little-endian
        writer.Write(Nonce); // 4 bytes, little-endian
        writer.Write(TargetPot); // 1 byte
        writer.Write(JobId); // 1 byte
        return stream.ToArray();
    }
}

// Helper functions:
