using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using boot_portal.Models;

namespace boot_portal.Services;

public sealed record BitcoinBlockchainInfo(
    long Blocks,
    long Headers,
    string BestBlockHash,
    bool InitialBlockDownload,
    double? VerificationProgress,
    string Chain = "");

public sealed record BitcoinZmqPublisher(string Topic, string Address);
public sealed record BitcoinNetworkInfo(int Connections, int ConnectionsIn, int ConnectionsOut);
public sealed record BitcoinPeerInfo(long Id, bool Inbound, double? PingTimeSeconds, string ConnectionType);

public interface IBitcoinRpcClient
{
    bool IsConfigured { get; }
    Task<BitcoinBlockchainInfo> GetBlockchainInfoAsync(CancellationToken cancellationToken);
    Task<string> GetBestBlockHashAsync(CancellationToken cancellationToken);
    Task<string> GetBlockHashAsync(long height, CancellationToken cancellationToken);
    Task<string> GetBlockHeaderHexAsync(string blockHash, CancellationToken cancellationToken);
    Task<IReadOnlyList<BitcoinZmqPublisher>> GetZmqNotificationsAsync(CancellationToken cancellationToken);
    Task<BitcoinNetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BitcoinPeerInfo>> GetPeerInfoAsync(CancellationToken cancellationToken);
    Task<double?> GetNetworkHashrateAsync(CancellationToken cancellationToken);
}

public sealed class BitcoinRpcClient : IBitcoinRpcClient
{
    private readonly PoolConfig _config;
    private readonly HttpClient _httpClient;
    private long _requestId;

    public BitcoinRpcClient(PoolConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, config.BitcoinRpcTimeoutSeconds));
    }

    public bool IsConfigured =>
        Uri.TryCreate(_config.BitcoinRpcUrl, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<BitcoinBlockchainInfo> GetBlockchainInfoAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getblockchaininfo", [], cancellationToken);
        return new BitcoinBlockchainInfo(
            result.GetProperty("blocks").GetInt64(),
            result.GetProperty("headers").GetInt64(),
            result.GetProperty("bestblockhash").GetString() ?? string.Empty,
            result.TryGetProperty("initialblockdownload", out JsonElement ibd) && ibd.GetBoolean(),
            result.TryGetProperty("verificationprogress", out JsonElement progress) ? progress.GetDouble() : null,
            result.TryGetProperty("chain", out JsonElement chain) ? chain.GetString() ?? string.Empty : string.Empty);
    }

    public async Task<string> GetBestBlockHashAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getbestblockhash", [], cancellationToken);
        return result.GetString() ?? string.Empty;
    }

    public async Task<string> GetBlockHashAsync(long height, CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getblockhash", [height], cancellationToken);
        return result.GetString() ?? string.Empty;
    }

    public async Task<string> GetBlockHeaderHexAsync(string blockHash, CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getblockheader", [blockHash, false], cancellationToken);
        return result.GetString() ?? string.Empty;
    }

    public async Task<IReadOnlyList<BitcoinZmqPublisher>> GetZmqNotificationsAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getzmqnotifications", [], cancellationToken);
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return result.EnumerateArray()
            .Select(item => new BitcoinZmqPublisher(
                item.TryGetProperty("type", out JsonElement type)
                    ? type.GetString() ?? string.Empty
                    : string.Empty,
                item.TryGetProperty("address", out JsonElement address)
                    ? address.GetString() ?? string.Empty
                    : string.Empty))
            .Where(publisher => !string.IsNullOrWhiteSpace(publisher.Topic))
            .ToList();
    }

    public async Task<BitcoinNetworkInfo> GetNetworkInfoAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getnetworkinfo", [], cancellationToken);
        return new BitcoinNetworkInfo(
            result.TryGetProperty("connections", out JsonElement connections) ? connections.GetInt32() : 0,
            result.TryGetProperty("connections_in", out JsonElement inbound) ? inbound.GetInt32() : 0,
            result.TryGetProperty("connections_out", out JsonElement outbound) ? outbound.GetInt32() : 0);
    }

    public async Task<IReadOnlyList<BitcoinPeerInfo>> GetPeerInfoAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getpeerinfo", [], cancellationToken);
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return result.EnumerateArray()
            .Select(item => new BitcoinPeerInfo(
                item.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : -1,
                item.TryGetProperty("inbound", out JsonElement inbound) && inbound.GetBoolean(),
                item.TryGetProperty("pingtime", out JsonElement ping) && ping.ValueKind == JsonValueKind.Number
                    ? ping.GetDouble()
                    : null,
                item.TryGetProperty("connection_type", out JsonElement type)
                    ? type.GetString() ?? string.Empty
                    : string.Empty))
            .Where(peer => peer.Id >= 0)
            .ToList();
    }

    public async Task<double?> GetNetworkHashrateAsync(CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync("getnetworkhashps", [], cancellationToken);
        return result.ValueKind == JsonValueKind.Number ? result.GetDouble() : null;
    }

    private async Task<JsonElement> CallAsync(
        string method,
        object?[] parameters,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Bitcoin RPC is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.BitcoinRpcUrl);
        string credentials = LoadCredentials();
        if (!string.IsNullOrWhiteSpace(credentials))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        request.Content = JsonContent.Create(new
        {
            jsonrpc = "1.0",
            id = Interlocked.Increment(ref _requestId),
            method,
            @params = parameters
        });

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Bitcoin RPC returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error) &&
            error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            string message = error.TryGetProperty("message", out JsonElement rpcMessage)
                ? rpcMessage.GetString() ?? "unknown RPC error"
                : "unknown RPC error";
            throw new InvalidOperationException($"Bitcoin RPC {method} failed: {message}");
        }

        if (!root.TryGetProperty("result", out JsonElement result))
        {
            throw new InvalidOperationException($"Bitcoin RPC {method} returned no result.");
        }

        return result.Clone();
    }

    private string LoadCredentials()
    {
        if (!string.IsNullOrWhiteSpace(_config.BitcoinRpcCookieFile))
        {
            try
            {
                return File.ReadAllText(_config.BitcoinRpcCookieFile).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Bitcoin RPC cookie file could not be read.");
            }
        }

        if (string.IsNullOrWhiteSpace(_config.BitcoinRpcUsername) &&
            string.IsNullOrWhiteSpace(_config.BitcoinRpcPassword))
        {
            return string.Empty;
        }

        return $"{_config.BitcoinRpcUsername}:{_config.BitcoinRpcPassword}";
    }
}
