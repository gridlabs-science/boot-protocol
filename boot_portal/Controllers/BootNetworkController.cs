using boot_portal.Services;
using boot_portal.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace boot_portal.Controllers;

[ApiController]
[Route("api/network")]
public class BootNetworkController : ControllerBase
{
    public const string PeerEndpointHeader = "X-Boot-Peer-Endpoint";
    public const string PeerProtocolVersionHeader = "X-Boot-Protocol-Version";
    public const string PeerNetworkIdHeader = "X-Boot-Network-Id";
    public const string PeerStateBundleSchemaVersionHeader = "X-GridPool-State-Bundle-Schema-Version";
    public const string PeerHttpApiVersionHeader = "X-GridPool-Http-Api-Version";
    public const string PeerTransportVersionHeader = "X-GridPool-Peer-Transport-Version";
    public const string PeerReleaseVersionHeader = "X-GridPool-Release-Version";

    private readonly BootProtocolStateService _stateService;
    private readonly BootPeerUdpRelayService _udpRelayService;
    private readonly BootNatPortMappingService _natPortMappingService;
    private readonly PoolConfig _poolConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BootNetworkController> _logger;

    public BootNetworkController(
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        BootPeerUdpRelayService udpRelayService,
        BootNatPortMappingService natPortMappingService,
        IHttpClientFactory httpClientFactory,
        ILogger<BootNetworkController> logger)
    {
        _poolConfig = poolConfig;
        _stateService = stateService;
        _udpRelayService = udpRelayService;
        _natPortMappingService = natPortMappingService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("summary")]
    public IActionResult GetSummary()
    {
        return Ok(_stateService.GetNetworkStatus());
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("launch-readiness")]
    public IActionResult GetLaunchReadiness()
    {
        return Ok(_stateService.GetNetworkStatus().LaunchReadiness);
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("peer-addresses")]
    public IActionResult GetPeerAddresses([FromQuery] int limit = 128)
    {
        return Ok(_stateService.GetPeerAddressBook(limit));
    }

    [EnableRateLimiting("network-read")]
    [HttpPost("reachability-test")]
    public async Task<IActionResult> RunReachabilityTest([FromBody] BootReachabilityProbeRequest? request, CancellationToken cancellationToken)
    {
        if (!_poolConfig.EnableAdminApi)
        {
            return NotFound();
        }

        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        if (!_stateService.IsAdminAuthorized(apiKey))
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.TargetBaseUrl))
        {
            return BadRequest(new { status = "rejected", reason = "targetBaseUrl is required" });
        }

        if (!TryNormalizeReachabilityTarget(request.TargetBaseUrl, out Uri? targetBaseUri, out string rejectionReason) ||
            targetBaseUri == null)
        {
            return BadRequest(new { status = "rejected", reason = rejectionReason });
        }

        if (!await IsPublicReachabilityTargetAsync(targetBaseUri, cancellationToken))
        {
            return BadRequest(new { status = "rejected", reason = "targetBaseUrl must resolve only to public unicast addresses" });
        }

        var result = new BootReachabilityProbeResult
        {
            TargetBaseUrl = targetBaseUri.ToString().TrimEnd('/'),
            TestedAtUtc = DateTime.UtcNow,
            ObservedRequesterIp = string.Empty
        };

        using HttpClient client = _httpClientFactory.CreateClient("ReachabilityProbeClient");
        await ProbeHttpAsync(client, new Uri(targetBaseUri, "/health"), cancellationToken, (status, latency, warning) =>
        {
            result.HttpStatusCode = status;
            result.HttpLatencyMs = latency;
            result.HttpReachable = status is >= 200 and < 500;
            if (!string.IsNullOrWhiteSpace(warning))
            {
                result.Warnings.Add(warning);
            }
        });

        await ProbeHttpAsync(client, new Uri(targetBaseUri, "/api/network/summary"), cancellationToken, (status, latency, warning) =>
        {
            result.NetworkSummaryStatusCode = status;
            result.NetworkSummaryLatencyMs = latency;
            result.NetworkSummaryReachable = status is >= 200 and < 300;
            if (!string.IsNullOrWhiteSpace(warning))
            {
                result.Warnings.Add(warning);
            }
        });

        await ProbeHttpAsync(client, new Uri(targetBaseUri, "/api/peer/session"), cancellationToken, (status, latency, warning) =>
        {
            result.PeerSessionRouteStatusCode = status;
            result.PeerSessionRouteLatencyMs = latency;
            result.PeerSessionRouteReachable = status is 400 or 426 or 101 or (>= 200 and < 500);
            if (!string.IsNullOrWhiteSpace(warning))
            {
                result.Warnings.Add(warning);
            }
        });

        if (request.IncludeUdpProbe && request.UdpPort is > 0 and <= 65535)
        {
            result.UdpProbeAttempted = true;
            result.UdpPort = request.UdpPort.Value;
            result.Warnings.Add("Plaintext UDP reachability probes are disabled; use authenticated peer-session telemetry.");
        }

        result.Summary = BuildReachabilitySummary(result);
        _stateService.RecordExternalNetworkEvent(
            "reachability-test",
            result.TargetBaseUrl,
            result.Summary);

        return Ok(result);
    }

    [EnableRateLimiting("network-read")]
    [HttpPost("reachability-ack")]
    public IActionResult AckReachabilityProbe([FromBody] BootUdpReachabilityAckRequest? request)
    {
        return NotFound();
    }

    [EnableRateLimiting("admin-write")]
    [HttpPost("admin/port-map")]
    public async Task<IActionResult> TryMapPeerPorts([FromBody] BootPortMappingRequest? request, CancellationToken cancellationToken)
    {
        if (!_poolConfig.EnableAdminApi)
        {
            return NotFound();
        }

        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        if (!_stateService.IsAdminAuthorized(apiKey))
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }

        BootPortMappingResponse response = await _natPortMappingService.TryMapAsync(
            request ?? new BootPortMappingRequest(),
            _poolConfig,
            cancellationToken);
        _stateService.RecordExternalNetworkEvent(
            "port-mapping",
            "admin",
            response.Summary);
        return Ok(response);
    }

    private static bool TryNormalizeReachabilityTarget(string target, out Uri? normalized, out string rejectionReason)
    {
        normalized = null;
        rejectionReason = string.Empty;
        if (!Uri.TryCreate(target.Trim(), UriKind.Absolute, out Uri? uri))
        {
            rejectionReason = "targetBaseUrl must be an absolute URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "targetBaseUrl must use http or https";
            return false;
        }

        normalized = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
        return true;
    }

    internal static async Task<bool> IsPublicReachabilityTargetAsync(Uri target, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(address => !IsNonPublicAddress(address));
    }

    internal static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
               (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    internal static async ValueTask<Stream> ConnectToPublicHostAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        IPAddress[] publicAddresses = addresses.Where(address => !IsNonPublicAddress(address)).ToArray();
        if (publicAddresses.Length == 0 || publicAddresses.Length != addresses.Length)
        {
            throw new HttpRequestException("Reachability target resolved to a non-public address.");
        }

        Exception? lastError = null;
        foreach (IPAddress address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
            }
        }

        throw new HttpRequestException("Reachability target connection failed.", lastError);
    }

    private static async Task ProbeHttpAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken,
        Action<int?, double?, string?> record)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken);
            stopwatch.Stop();
            record((int)response.StatusCode, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            stopwatch.Stop();
            record(null, stopwatch.Elapsed.TotalMilliseconds, $"{uri.AbsolutePath} probe failed: {ex.GetType().Name}");
        }
    }

    private static string BuildReachabilitySummary(BootReachabilityProbeResult result)
    {
        if (result.NetworkSummaryReachable && result.PeerSessionRouteReachable)
        {
            return result.UdpProbeAttempted
                ? $"Peer TCP reachable; UDP probe sent={result.UdpProbeSent}, ack={result.UdpChallengeAcknowledged}."
                : "Peer TCP reachable.";
        }

        if (result.HttpReachable)
        {
            return "HTTP host reachable, but peer protocol routes did not pass reachability checks.";
        }

        return "Target was not reachable over HTTP from this node.";
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("state/{stateId}")]
    public IActionResult GetStateBundle(string stateId)
    {
        var bundle = _stateService.GetStateBundle(stateId);
        return bundle == null ? NotFound() : Ok(bundle);
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("history")]
    public IActionResult GetHistory([FromQuery] int limit = 24)
    {
        return Ok(_stateService.GetRoundHistory(limit));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("history/{stateId}")]
    public IActionResult GetHistoryEntry(string stateId)
    {
        var entry = _stateService.GetRoundHistoryEntry(stateId);
        return entry == null ? NotFound() : Ok(entry);
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("hashrate")]
    public IActionResult GetHashrateSeries([FromQuery] string? window = "24h")
    {
        return Ok(_stateService.GetHashrateSeries(window));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("local-miners")]
    public IActionResult GetLocalDatumMiners(
        [FromQuery] string? address = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? window = "24h")
    {
        return Ok(_stateService.GetLocalDatumMinerSummaries(address, limit, window));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("share-diagnostics")]
    public IActionResult GetShareDiagnostics(
        [FromQuery] string? window = "12h",
        [FromQuery] string? source = "datum",
        [FromQuery] bool? accepted = false,
        [FromQuery] int limit = 500,
        [FromQuery] string? minerAddress = null,
        [FromQuery] string? category = null)
    {
        return Ok(_stateService.GetShareDiagnostics(window, source, accepted, limit, minerAddress, category));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("events")]
    public IActionResult GetNetworkEvents(
        [FromQuery] string? window = "12h",
        [FromQuery] int limit = 500,
        [FromQuery] string? eventType = null,
        [FromQuery] string? source = null)
    {
        return Ok(_stateService.GetNetworkEvents(window, limit, eventType, source));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("peer-relay-latency")]
    public IActionResult GetPeerRelayLatency(
        [FromQuery] string? window = "12h",
        [FromQuery] int limit = 500,
        [FromQuery] string? remoteEndpoint = null,
        [FromQuery] string? transport = null,
        [FromQuery] string? proofClass = null,
        [FromQuery] string? relayStage = null)
    {
        return Ok(_stateService.GetPeerRelayLatency(window, limit, remoteEndpoint, transport, proofClass, relayStage));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("coinbaser-diagnostics")]
    public IActionResult GetCoinbaserDiagnostics(
        [FromQuery] string? window = "12h",
        [FromQuery] int limit = 500,
        [FromQuery] string? remoteEndpoint = null,
        [FromQuery] bool? temporarySlotZero = null)
    {
        return Ok(_stateService.GetCoinbaserDiagnostics(window, limit, remoteEndpoint, temporarySlotZero));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("datum-share-responses")]
    public IActionResult GetDatumShareResponses(
        [FromQuery] string? window = "12h",
        [FromQuery] int limit = 500,
        [FromQuery] string? remoteEndpoint = null,
        [FromQuery] bool? accepted = null,
        [FromQuery] string? reason = null)
    {
        return Ok(_stateService.GetDatumShareResponses(window, limit, remoteEndpoint, accepted, reason));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("datum-sessions")]
    public IActionResult GetDatumSessions(
        [FromQuery] string? window = "12h",
        [FromQuery] int limit = 500,
        [FromQuery] string? remoteEndpoint = null,
        [FromQuery] bool? active = null,
        [FromQuery] string? protocol = null)
    {
        return Ok(_stateService.GetDatumSessions(window, limit, remoteEndpoint, active, protocol));
    }

    [EnableRateLimiting("network-read")]
    [HttpGet("datum-protocol-events")]
    public IActionResult GetDatumProtocolEvents(
        [FromQuery] string? window = "12h",
        [FromQuery] int limit = 500,
        [FromQuery] string? sessionId = null,
        [FromQuery] string? remoteEndpoint = null,
        [FromQuery] string? eventType = null,
        [FromQuery] string? direction = null,
        [FromQuery] string? messageLabel = null)
    {
        return Ok(_stateService.GetDatumProtocolEvents(window, limit, sessionId, remoteEndpoint, eventType, direction, messageLabel));
    }

    [EnableRateLimiting("admin-write")]
    [HttpPost("admin/reset")]
    public async Task<IActionResult> ResetRound()
    {
        if (!_poolConfig.EnableAdminApi)
        {
            return NotFound();
        }

        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        if (!_stateService.IsAdminAuthorized(apiKey))
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }

        var result = await _stateService.RotateToNextRoundAsync(string.Empty, "manual-reset", manual: true);
        _logger.LogWarning("Manual round reset triggered via admin API. New state: {StateId}", result.NetworkStatus.CurrentStateId);
        return Ok(result);
    }

    [EnableRateLimiting("admin-write")]
    [HttpPost("admin/reset-genesis")]
    public async Task<IActionResult> ResetHistoryToGenesis()
    {
        if (!_poolConfig.EnableAdminApi)
        {
            return NotFound();
        }

        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        if (!_stateService.IsAdminAuthorized(apiKey))
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }

        var status = await _stateService.ResetHistoryToGenesisAsync();
        _logger.LogWarning(
            "Genesis history reset triggered via admin API. New state: {StateId}, round: {RoundNumber}",
            status.CurrentStateId,
            status.CurrentRoundNumber);
        return Ok(status);
    }

    [EnableRateLimiting("admin-write")]
    [HttpPost("admin/peer/tombstone")]
    public IActionResult TombstonePeer([FromBody] BootPeerTombstoneRequest request)
    {
        if (!_poolConfig.EnableAdminApi)
        {
            return NotFound();
        }

        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        if (!_stateService.IsAdminAuthorized(apiKey))
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }

        if (string.IsNullOrWhiteSpace(request.Endpoint))
        {
            return BadRequest(new { status = "rejected", reason = "Missing peer endpoint" });
        }

        bool removed = _stateService.TombstonePeer(request.Endpoint);
        if (!removed)
        {
            return NotFound(new { status = "not-found", endpoint = request.Endpoint });
        }

        _logger.LogWarning("Peer endpoint manually tombstoned via admin API: {Endpoint}", request.Endpoint);
        return Ok(new { status = "removed", endpoint = request.Endpoint });
    }
}
