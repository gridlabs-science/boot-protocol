using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using boot_portal.Services;
using Microsoft.AspNetCore.SignalR;
using NSec.Cryptography;

namespace boot_portal.HostedServices;

public class DatumServer : BackgroundService
{
    private readonly TcpListener _listener;
    private readonly Key _serverKey; 
    private readonly Key _serverXKey;
    private readonly PoolConfig _poolConfig;
    private readonly BootProtocolStateService _stateService;
    private readonly ILogger<DatumServer> _logger;
    private readonly ConcurrentDictionary<int, (TcpClient Client, ClientHandler Handler)> _activeClients = new();
    private readonly SemaphoreSlim _connectionSlots;
    private int _nextClientId;

    public static BootProtocolStateService StateService { get; private set; } = null!;
    // Store the hex string for the UI
    public static string ServerPubKeyHex { get; private set; } = string.Empty;
    public static int PoolPort { get; private set; }

    // Testing stuff:
    public static readonly int RESET_THRESHOLD = 1000000000;

    public DatumServer(
        IPAddress address,
        int port,
        Key serverKey,
        Key serverXKey,
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        IHubContext<PoolStatsHub> hubContext,
        ILogger<DatumServer> logger)
    {
        _listener = new TcpListener(address, port);
        _serverKey = serverKey;
        _serverXKey = serverXKey;
        _poolConfig = poolConfig;
        _stateService = stateService;
        _logger = logger;
        _connectionSlots = new SemaphoreSlim(poolConfig.DatumMaxConnections, poolConfig.DatumMaxConnections);
        StateService = stateService;
        PoolPort = port;
        _stateService.WinnersListChanged += HandleWinnersListChangedAsync;
        _stateService.WorkTemplatesInvalidated += HandleWorkTemplatesInvalidatedAsync;

        // Calculate the Hex Public Key once on startup for the UI
        GeneratePubKeyHex();
    }

    private void GeneratePubKeyHex()
    {
        var ed25519PubKeyBytes = _serverKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var x25519PubKeyBytes = _serverXKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var combinedPubKey = new byte[ed25519PubKeyBytes.Length + x25519PubKeyBytes.Length];
        Buffer.BlockCopy(ed25519PubKeyBytes, 0, combinedPubKey, 0, ed25519PubKeyBytes.Length);
        Buffer.BlockCopy(x25519PubKeyBytes, 0, combinedPubKey, ed25519PubKeyBytes.Length, x25519PubKeyBytes.Length);
        ServerPubKeyHex = Convert.ToHexString(combinedPubKey).ToLower();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
        _logger.LogInformation("\ud83d\ude80 DATUM Prime Server started. Key: {KeyShort}...", ServerPubKeyHex.Substring(0, 16));
        
        while (!stoppingToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(stoppingToken);
            if (!_connectionSlots.Wait(0))
            {
                _logger.LogWarning(
                    "Rejected DATUM connection because the configured limit of {Limit} active sessions was reached.",
                    _poolConfig.DatumMaxConnections);
                client.Dispose();
                continue;
            }
            int clientId = Interlocked.Increment(ref _nextClientId);
            var clientHandler = new ClientHandler(client, _serverKey, _serverXKey, _poolConfig, _stateService, stoppingToken);
            _activeClients[clientId] = (client, clientHandler);
            _ = Task.Run(async () =>
            {
                try
                {
                    await clientHandler.HandleClientAsync();
                }
                finally
                {
                    _activeClients.TryRemove(clientId, out _);
                    client.Dispose();
                    _connectionSlots.Release();
                }
            }, stoppingToken);
        }
    }

    private async Task HandleWinnersListChangedAsync(string reason)
    {
        if (IsChainTipSnapshotReason(reason))
        {
            _logger.LogDebug(
                "Skipping DATUM urgent refresh for chain-tip snapshot ({Reason}); DATUM will refresh from its own Bitcoin work-update path.",
                reason);
            return;
        }

        await RefreshActiveClientsAsync(reason);
    }

    private async Task HandleWorkTemplatesInvalidatedAsync(string reason)
    {
        if (IsChainTipSnapshotReason(reason))
        {
            _logger.LogDebug(
                "Skipping DATUM urgent refresh for chain-tip invalidation ({Reason}); DATUM will refresh from its own Bitcoin work-update path.",
                reason);
            return;
        }

        await RefreshActiveClientsAsync(reason);
    }

    private static bool IsChainTipSnapshotReason(string? reason)
    {
        return !string.IsNullOrWhiteSpace(reason) &&
               reason.StartsWith("chain-tip:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshActiveClientsAsync(string reason)
    {
        if (_activeClients.IsEmpty)
        {
            return;
        }

        await Task.Delay(250);

        int refreshed = 0;
        int disconnected = 0;
        foreach (var entry in _activeClients.ToArray())
        {
            try
            {
                bool refreshSent = await entry.Value.Handler.RequestBlockTemplateRefreshAsync(reason);
                if (refreshSent)
                {
                    refreshed++;
                    continue;
                }

                entry.Value.Client.Close();
                disconnected++;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to refresh DATUM client {ClientId}; disconnecting instead.", entry.Key);
                try
                {
                    entry.Value.Client.Close();
                    disconnected++;
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        if (refreshed > 0 || disconnected > 0)
        {
            _logger.LogInformation(
                "Requested DATUM work refresh for {Refreshed} client(s) after Winners List change ({Reason}); disconnected {Disconnected} fallback client(s).",
                refreshed,
                reason,
                disconnected);
        }
    }
}
