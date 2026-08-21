using System.Text;
using System.Net;
using boot_portal.Models;

namespace boot_portal.Utils;

public static class PoolConfigValidator
{
    public const int MaxDatumCoinbaseTagBytes = 255;
    private static readonly HashSet<string> ValidNodeModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "development",
        "developer-preview",
        "sovereign",
        "staging",
        "production"
    };
    private static readonly HashSet<string> ValidBitcoinNotificationModes = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        BitcoinNotificationModes.AttachedNode,
        BitcoinNotificationModes.ExternalFallback
    };

    public static void ValidateOrThrow(PoolConfig config)
    {
        List<string> errors = Validate(config);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid Boot pool config: " + string.Join("; ", errors));
        }
    }

    public static List<string> Validate(PoolConfig config)
    {
        var errors = new List<string>();

        if (!ValidNodeModes.Contains(config.NodeMode))
        {
            errors.Add("node_mode must be one of development, developer-preview, sovereign, staging, or production");
        }

        if (!ValidBitcoinNotificationModes.Contains(config.BitcoinNotificationMode ?? string.Empty))
        {
            errors.Add("bitcoin_notification_mode must be attached-node or external-fallback");
        }

        ValidatePositive(errors, config.BitcoinRpcPollIntervalSeconds, "bitcoin_rpc_poll_interval_seconds");
        ValidatePositive(errors, config.BitcoinRpcTimeoutSeconds, "bitcoin_rpc_timeout_seconds");
        ValidatePositive(errors, config.BitcoinRpcLagGraceSeconds, "bitcoin_rpc_lag_grace_seconds");
        if (!string.IsNullOrWhiteSpace(config.BitcoinRpcUrl) &&
            (!Uri.TryCreate(config.BitcoinRpcUrl, UriKind.Absolute, out Uri? rpcUri) ||
             (rpcUri.Scheme != Uri.UriSchemeHttp && rpcUri.Scheme != Uri.UriSchemeHttps) ||
             string.IsNullOrWhiteSpace(rpcUri.Host) ||
             !string.IsNullOrWhiteSpace(rpcUri.UserInfo)))
        {
            errors.Add("bitcoin_rpc_url must be an absolute HTTP(S) URL without embedded credentials");
        }

        if (!string.IsNullOrWhiteSpace(config.BitcoinRpcCookieFile) &&
            (!string.IsNullOrWhiteSpace(config.BitcoinRpcUsername) ||
             !string.IsNullOrWhiteSpace(config.BitcoinRpcPassword)))
        {
            errors.Add("bitcoin_rpc_cookie_file cannot be combined with bitcoin_rpc_username or bitcoin_rpc_password");
        }

        string bitcoinNetwork = string.Empty;
        try
        {
            bitcoinNetwork = BitcoinScript.NormalizeNetwork(config.BitcoinNetwork);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(config.PoolPayoutScript))
        {
            errors.Add("pool_payout_script must be explicitly configured; GridPool has no vendor payout fallback");
        }
        else if (!string.IsNullOrWhiteSpace(bitcoinNetwork) &&
                 !BitcoinScript.TryAddressToScriptPubKey(config.PoolPayoutScript, bitcoinNetwork, out _))
        {
            errors.Add($"pool_payout_script must be a supported Bitcoin payout address for bitcoin_network {bitcoinNetwork}");
        }

        int coinbaseTagBytes = Encoding.UTF8.GetByteCount(config.CoinbaseTag ?? string.Empty);
        if (coinbaseTagBytes > MaxDatumCoinbaseTagBytes)
        {
            errors.Add($"coinbase_tag is {coinbaseTagBytes} UTF-8 bytes, maximum is {MaxDatumCoinbaseTagBytes}");
        }

        if (config.CoinbaseUncondensedOutputsEnabled && IsProduction(config))
        {
            errors.Add("coinbase_uncondensed_outputs_enabled is a firmware stress-test mode and must be false in production");
        }

        if (config.WinnersListSize <= 0)
        {
            errors.Add("winners_list_size must be greater than 0");
        }

        if (config.GridLabsSupportFeeEnabled && config.WinnersListSize < 2)
        {
            errors.Add("winners_list_size must be at least 2 when grid_labs_support_fee_enabled is true");
        }

        if (config.WinnersListSize > 10_000)
        {
            errors.Add("winners_list_size is unreasonably large; expected 10000 or less");
        }

        ValidatePositive(errors, config.WorkSetReserveMultiplier, "work_set_reserve_multiplier");

        if (config.BootProtocolVersion is < 21 or > 22)
        {
            errors.Add("boot_protocol_version must be 21 or 22");
        }

        if (config.V22ActivationBlockHeight < 0)
        {
            errors.Add("v22_activation_block_height must be greater than or equal to 0");
        }

        if (config.MinDiff == 0)
        {
            errors.Add("min_diff must be greater than 0");
        }

        ValidateRequiredPort(errors, config.DatumPort, "Datum_Port");
        ValidateNonNegativePort(errors, config.DatumPublicPort, "datum_public_port");
        ValidateNonNegativePort(errors, config.WebUiPortHttp, "WebUI_Port_http");
        ValidateNonNegativePort(errors, config.WebUiPortHttps, "WebUI_Port_https");
        ValidateNonNegativePort(errors, config.PeerListenerPort, "peer_listener_port");
        if (config.WebUiPortHttp == 0 && config.WebUiPortHttps == 0)
        {
            errors.Add("at least one WebUI port must be greater than 0");
        }

        ValidatePositive(errors, config.PeerSyncIntervalSeconds, "peer_sync_interval_seconds");
        ValidatePositive(errors, config.PeerRequestTimeoutSeconds, "peer_request_timeout_seconds");
        ValidatePositive(errors, config.MaxPeers, "max_peers");
        ValidatePositive(errors, config.PeerOutboundTarget, "peer_outbound_target");
        ValidatePositive(errors, config.PeerShareRelayTarget, "peer_share_relay_target");
        ValidatePositive(errors, config.PeerRelayParallelism, "peer_relay_parallelism");
        ValidatePositive(errors, config.PeerAddressBookMaxEntries, "peer_address_book_max_entries");
        ValidatePositive(errors, config.PeerAddressGossipLimit, "peer_address_gossip_limit");
        ValidatePositive(errors, config.PeerFailureBackoffMinSeconds, "peer_failure_backoff_min_seconds");
        ValidatePositive(errors, config.PeerFailureBackoffMaxSeconds, "peer_failure_backoff_max_seconds");
        ValidatePositive(errors, config.PeerTombstoneSeconds, "peer_tombstone_seconds");
        if (config.PeerFailureBackoffMaxSeconds < config.PeerFailureBackoffMinSeconds)
        {
            errors.Add("peer_failure_backoff_max_seconds must be greater than or equal to peer_failure_backoff_min_seconds");
        }
        ValidateNonNegative(errors, config.PeerPruneAfterSeconds, "peer_prune_after_seconds");
        ValidatePositive(errors, config.PeerPruneFailureCount, "peer_prune_failure_count");
        ValidatePositive(errors, config.NetworkReadRateLimitPerMinute, "network_read_rate_limit_per_minute");
        ValidatePositive(errors, config.PeerWriteRateLimitPerMinute, "peer_write_rate_limit_per_minute");
        ValidatePositive(errors, config.PeerStateBundleFetchRateLimitPerMinute, "peer_state_bundle_fetch_rate_limit_per_minute");
        ValidatePositive(errors, config.PeerSessionTarget, "peer_session_target");
        ValidatePositive(errors, config.PeerSessionConnectIntervalSeconds, "peer_session_connect_interval_seconds");
        ValidatePositive(errors, config.PeerSessionIdleTimeoutSeconds, "peer_session_idle_timeout_seconds");
        ValidatePositive(errors, config.PeerSessionMaxFrameBytes, "peer_session_max_frame_bytes");
        ValidatePositive(errors, config.PeerSessionClockSkewSeconds, "peer_session_clock_skew_seconds");
        ValidatePositive(errors, config.PeerTipGraceSeconds, "peer_tip_grace_seconds");
        ValidatePositive(errors, config.PeerTipMaxHeaderAgeSeconds, "peer_tip_max_header_age_seconds");
        ValidatePositive(errors, config.PeerTipMaxFutureSeconds, "peer_tip_max_future_seconds");
        ValidatePositive(errors, config.PeerSessionSendTimeoutSeconds, "peer_session_send_timeout_seconds");
        ValidatePositive(errors, config.PeerLoopStaleSeconds, "peer_loop_stale_seconds");
        ValidatePositive(errors, config.OutboundRelayStaleSeconds, "outbound_relay_stale_seconds");
        if (config.PeerSessionMaxFrameBytes > config.MaxShareRequestBytes)
        {
            errors.Add("peer_session_max_frame_bytes must be less than or equal to max_share_request_bytes");
        }
        ValidateNonNegativePort(errors, config.PeerUdpBindPort, "peer_udp_bind_port");
        ValidateRequiredPort(errors, config.PeerUdpPort, "peer_udp_port");
        ValidatePositive(errors, config.PeerUdpMaxDatagramBytes, "peer_udp_max_datagram_bytes");
        ValidatePositive(errors, config.PeerUdpReplayWindow, "peer_udp_replay_window");
        if (config.PeerUdpMaxDatagramBytes < 512)
        {
            errors.Add("peer_udp_max_datagram_bytes must be at least 512");
        }
        ValidatePositive(errors, config.PulseMinDifficulty, "pulse_min_difficulty");
        ValidatePositive(errors, config.PulseTargetIntervalSeconds, "pulse_target_interval_seconds");
        ValidatePositive(errors, config.PulseRelayTtl, "pulse_relay_ttl");
        ValidatePositive(errors, config.PulseMaxPerPeerPerMinute, "pulse_max_per_peer_per_minute");
        ValidatePositive(errors, config.PulseMaxPerSourceAddressPerMinute, "pulse_max_per_source_address_per_minute");
        ValidatePositive(errors, config.MinOptimisticRelayDifficulty, "min_optimistic_relay_difficulty");
        ValidatePositive(errors, config.MiningApiShareRateLimitPerMinute, "mining_api_share_rate_limit_per_minute");
        ValidatePositive(errors, config.LocalAdapterTelemetryMaxBatchSize, "local_adapter_telemetry_max_batch_size");
        if (string.IsNullOrWhiteSpace(config.LocalAdapterTokenFile))
        {
            errors.Add("local_adapter_token_file must not be empty");
        }
        ValidatePositive(errors, config.AdminRateLimitPerMinute, "admin_rate_limit_per_minute");
        ValidatePositive(errors, config.MaxShareRequestBytes, "max_share_request_bytes");
        ValidatePositive(errors, config.DatumMaxConnections, "datum_max_connections");
        ValidatePositive(errors, config.DatumReadTimeoutSeconds, "datum_read_timeout_seconds");
        if (config.DatumMaxConnections > 1024)
        {
            errors.Add("datum_max_connections must be 1024 or less");
        }
        ValidatePositive(errors, config.MaxCoinbaseHexChars, "max_coinbase_hex_chars");
        ValidatePositive(errors, config.MaxMerklePathEntries, "max_merkle_path_entries");
        ValidatePositive(errors, config.HashrateSampleIntervalSeconds, "hashrate_sample_interval_seconds");
        ValidatePositive(errors, config.HashrateLocalWindowSeconds, "hashrate_local_window_seconds");
        ValidatePositive(errors, config.LocalDatumMinerSummaryLimit, "local_datum_miner_summary_limit");
        ValidatePositive(errors, config.LocalDatumHashratePerAddressMaxSamples, "local_datum_hashrate_per_address_max_samples");
        ValidatePositive(errors, config.LocalDatumHashrateMaxAddresses, "local_datum_hashrate_max_addresses");
        ValidatePositive(errors, config.MaxAcceptedShareTelemetryEntries, "max_accepted_share_telemetry_entries");
        ValidatePositive(errors, config.HashrateSampleRetentionDays, "hashrate_sample_retention_days");
        ValidatePositive(errors, config.AcceptedShareTelemetryRetentionHours, "accepted_share_telemetry_retention_hours");
        ValidatePositive(errors, config.ShareDiagnosticRetentionHours, "share_diagnostic_retention_hours");
        ValidatePositive(errors, config.NetworkEventRetentionHours, "network_event_retention_hours");
        ValidatePositive(errors, config.DatumShareResponseSlowMs, "datum_share_response_slow_ms");
        ValidatePositive(errors, config.DatumShareResponseAcceptedSampleEvery, "datum_share_response_accepted_sample_every");
        ValidatePositive(errors, config.DatumLowDiffCourtesyValidateEvery, "datum_low_diff_courtesy_validate_every");
        ValidatePositive(errors, config.DatumLowDiffCourtesyValidateSeconds, "datum_low_diff_courtesy_validate_seconds");
        ValidatePositive(errors, config.StaleDatumPayoutMismatchThreshold, "stale_datum_payout_mismatch_threshold");
        ValidatePositive(errors, config.StaleDatumDisconnectMinSeconds, "stale_datum_disconnect_min_seconds");
        ValidatePositive(errors, config.StaleDatumDisconnectCooldownSeconds, "stale_datum_disconnect_cooldown_seconds");
        ValidatePositive(errors, config.StaleDatumRefreshIntervalSeconds, "stale_datum_refresh_interval_seconds");
        ValidateNonNegativePort(errors, config.StratumV1ProxyPort, "stratum_v1_proxy_port");
        if (string.IsNullOrWhiteSpace(config.StratumV1ProxyHost) && config.StratumV1ProxyPort > 0)
        {
            errors.Add("stratum_v1_proxy_host is required when stratum_v1_proxy_port is configured");
        }
        if (!string.IsNullOrWhiteSpace(config.StratumV1ProxyHost) && config.StratumV1ProxyPort <= 0)
        {
            errors.Add("stratum_v1_proxy_port must be greater than 0 when stratum_v1_proxy_host is configured");
        }

        if (!string.Equals(config.TestingRoundResetMode, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.TestingRoundResetMode, "block_hash_low_nibble", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("testing_round_reset_mode must be none or block_hash_low_nibble");
        }

        if (config.TestingRoundResetLowNibbleThreshold is < 0 or > 16)
        {
            errors.Add("testing_round_reset_low_nibble_threshold must be between 0 and 16");
        }

        if (IsProduction(config))
        {
            ValidateRequiredAbsoluteUrl(errors, config.PublicBaseUrl, "public_base_url");
            ValidateRequiredHost(errors, config.DatumPublicHost, "datum_public_host");

            if (IsPlaceholderPublicUrl(config.PublicBaseUrl))
            {
                errors.Add("public_base_url must not use boot.example.com, example.com, or localhost in production");
            }
            else if (IsPrivatePublicUrl(config.PublicBaseUrl))
            {
                errors.Add("public_base_url must not advertise a private or loopback IP address in production");
            }

            if (string.Equals(config.BootNetworkId, "mainnet-beta", StringComparison.OrdinalIgnoreCase) &&
                !config.EnablePulseProofs)
            {
                errors.Add("enable_pulse_proofs must be true for production mainnet-beta nodes");
            }

            if (!string.Equals(config.TestingRoundResetMode, "none", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("testing_round_reset_mode must be none when node_mode is production");
            }

            if (string.Equals(bitcoinNetwork, BitcoinScript.Mainnet, StringComparison.OrdinalIgnoreCase) &&
                config.BootProtocolVersion >= 22 &&
                config.V22ActivationBlockHeight == 0)
            {
                errors.Add("v22_activation_block_height must be non-zero for a production mainnet V2.2 node");
            }
        }

        if (config.EnableAdminApi && !HasStrongAdminKey(config.AdminApiKey))
        {
            errors.Add("admin_api_key must be a strong non-placeholder value whenever enable_admin_api is true");
        }

        return errors;
    }

    private static bool IsProduction(PoolConfig config)
    {
        return string.Equals(config.NodeMode, "production", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholderPublicUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host.Trim().TrimEnd('.');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "boot.example.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivatePublicUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            !IPAddress.TryParse(uri.Host, out IPAddress? address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
               (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    private static bool HasStrongAdminKey(string? adminKey)
    {
        string value = adminKey?.Trim() ?? string.Empty;
        return value.Length >= 32 &&
               !string.Equals(value, "change-this-admin-key", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "changeme", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidatePositive(List<string> errors, int value, string name)
    {
        if (value <= 0)
        {
            errors.Add($"{name} must be greater than 0");
        }
    }

    private static void ValidatePositive(List<string> errors, double value, string name)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            errors.Add($"{name} must be a finite value greater than 0");
        }
    }

    private static void ValidateNonNegative(List<string> errors, int value, string name)
    {
        if (value < 0)
        {
            errors.Add($"{name} must be greater than or equal to 0");
        }
    }

    private static void ValidateNonNegativePort(List<string> errors, int value, string name)
    {
        if (value is < 0 or > 65535)
        {
            errors.Add($"{name} must be between 0 and 65535");
        }
    }

    private static void ValidateRequiredPort(List<string> errors, int value, string name)
    {
        if (value is <= 0 or > 65535)
        {
            errors.Add($"{name} must be between 1 and 65535");
        }
    }

    private static void ValidateRequiredAbsoluteUrl(List<string> errors, string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            errors.Add($"{name} must be an absolute http or https URL in production");
        }
    }

    private static void ValidateRequiredHost(List<string> errors, string value, string name)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains(' ') ||
            trimmed.Contains('/'))
        {
            errors.Add($"{name} must be a host name or IP address without a URL scheme or path in production");
        }
    }
}
