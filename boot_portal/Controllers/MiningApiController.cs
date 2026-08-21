using Microsoft.AspNetCore.Mvc;
using boot_portal.HostedServices;
using boot_portal.Models;
using boot_portal.Services;
using boot_portal.Utils;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace boot_portal.Controllers;

[ApiController]
[Route("api/mining")]
public class MiningApiController : ControllerBase
{
    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PoolConfig _poolConfig;
    private readonly BootProtocolStateService _stateService;
    private readonly LocalMiningAdapterAuth? _localAdapterAuth;
    private readonly ILogger<MiningApiController> _logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public MiningApiController(
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        LocalMiningAdapterAuth localAdapterAuth,
        ILogger<MiningApiController> logger)
    {
        _poolConfig = poolConfig;
        _stateService = stateService;
        _localAdapterAuth = localAdapterAuth;
        _logger = logger;
    }

    // Retained for controller-level tests and embedders that do not expose local adapter routes.
    public MiningApiController(
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        ILogger<MiningApiController> logger)
    {
        _poolConfig = poolConfig;
        _stateService = stateService;
        _logger = logger;
    }

    // GET: api/mining/payouts
    // Returns the current list of winners (the required coinbase outputs)
    [EnableRateLimiting("network-read")]
    [HttpGet("payouts")]
    public IActionResult GetPayouts()
    {
        return Ok(_stateService.GetPayoutResponse());
    }

    // GET: api/mining/share-advice
    // Returns the current minimum difficulty needed for a direct client share to affect the on-deck list.
    [EnableRateLimiting("network-read")]
    [HttpGet("share-advice")]
    public IActionResult GetShareAdvice()
    {
        return Ok(_stateService.GetShareAdviceResponse());
    }

    // GET: api/mining/sv2-work-selection
    // Returns the exact GridPool payout-output commitment an SV2 Job Declarator should include.
    [EnableRateLimiting("network-read")]
    [HttpGet("sv2-work-selection")]
    public IActionResult GetSv2WorkSelection()
    {
        return GetWorkPlan();
    }

    // GET: api/mining/work-plan
    // Generic adapter contract for SV1, SV2, and future local mining gateways.
    [EnableRateLimiting("network-read")]
    [HttpGet("work-plan")]
    public IActionResult GetWorkPlan()
    {
        if (!_stateService.CanIssueMiningWork(out string reason))
        {
            return Conflict(new
            {
                error = "local-bitcoin-lagging",
                message = reason
            });
        }

        return Ok(_stateService.GetSv2WorkSelectionResponse());
    }

    // GET: api/mining/local/work-plan
    // Authenticated alias used by sidecars that should not depend on a public route.
    [HttpGet("local/work-plan")]
    public IActionResult GetLocalWorkPlan()
    {
        if (!IsLocalAdapterAuthorized())
        {
            return Unauthorized(new { status = "rejected", reason = "Invalid local adapter token" });
        }

        return GetWorkPlan();
    }

    // GET: api/mining/local/work-plan/events
    // Server-sent plan updates. The short internal poll observes state atomically through
    // GetSv2WorkSelectionResponse and avoids coupling adapters to UI SignalR messages.
    [HttpGet("local/work-plan/events")]
    public async Task StreamLocalWorkPlans(CancellationToken cancellationToken)
    {
        if (!IsLocalAdapterAuthorized())
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsJsonAsync(
                new { status = "rejected", reason = "Invalid local adapter token" },
                cancellationToken);
            return;
        }

        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.ContentType = "text/event-stream";

        string? lastPlanId = null;
        DateTime nextHeartbeatUtc = DateTime.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        do
        {
            Sv2WorkSelectionDto plan = _stateService.GetSv2WorkSelectionResponse();
            DateTime now = DateTime.UtcNow;
            if (!string.Equals(lastPlanId, plan.PlanId, StringComparison.Ordinal) || now >= nextHeartbeatUtc)
            {
                string eventName = lastPlanId == null || !string.Equals(lastPlanId, plan.PlanId, StringComparison.Ordinal)
                    ? "work-plan"
                    : "heartbeat";
                await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
                await Response.WriteAsync($"id: {plan.PlanId}\n", cancellationToken);
                await Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(plan, EventJsonOptions)}\n\n",
                    cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
                lastPlanId = plan.PlanId;
                nextHeartbeatUtc = now.AddSeconds(15);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    // GET: api/mining/connect-info
    // Returns the live DATUM endpoint details miners need to configure DATUM Gateway.
    [EnableRateLimiting("network-read")]
    [HttpGet("connect-info")]
    public IActionResult GetConnectInfo()
    {
        BootNetworkStatusDto network = _stateService.GetNetworkStatus();
        string datumHost = ResolveDatumHost();
        int datumPort = ResolveDatumPort();

        return Ok(new
        {
            network = network.NetworkId,
            bitcoinNetwork = network.BitcoinNetwork,
            protocolVersion = network.ProtocolVersion,
            datum = new
            {
                host = datumHost,
                port = datumPort,
                endpoint = $"{datumHost}:{datumPort}",
                publicKey = DatumServer.ServerPubKeyHex
            },
            stratumV1 = new
            {
                nativeListenerAvailable = false,
                workPlanEndpoint = "/api/mining/work-plan",
                localWorkPlanEndpoint = "/api/mining/local/work-plan",
                localWorkPlanEventsEndpoint = "/api/mining/local/work-plan/events",
                localProofEndpoint = "/api/mining/local/share",
                localTelemetryEndpoint = "/api/mining/local/share-telemetry",
                note = "boot-portal is not a native Stratum V1 server. Run DATUM Gateway, Hydrapool, or the GridPool CKPool adapter and compatible CKPool fork beside this node."
            },
            stratumV2 = new
            {
                nativeListenerAvailable = false,
                workSelectionEndpoint = "/api/mining/sv2-work-selection",
                localProofEndpoint = "/api/mining/local/share",
                localTelemetryEndpoint = "/api/mining/local/share-telemetry",
                note = "Run the gridpool-sv2-pool SRI fork beside this node for native SV2 Standard or Extended channels. The adapter endpoints require the local token and are not public miner endpoints."
            }
        });
    }

    // POST: api/mining/share
    // Receives a high-difficulty share
    [EnableRateLimiting("mining-write")]
    [HttpPost("share")]
    public async Task<IActionResult> SubmitShare([FromBody] ShareSubmissionDto? share)
    {
        string source = ResolvePublicMiningSource();
        return await SubmitShareCore(share, source, $"{source}-block");
    }

    // POST: api/mining/local/share
    // Trusted loopback/sidecar path. Full GridPool proof validation still applies.
    [HttpPost("local/share")]
    public async Task<IActionResult> SubmitLocalShare([FromBody] ShareSubmissionDto? share)
    {
        if (!IsLocalAdapterAuthorized())
        {
            return Unauthorized(new { status = "rejected", reason = "Invalid local adapter token" });
        }

        string source = ResolveLocalAdapterSource();
        return await SubmitShareCore(share, source, $"{source}-block");
    }

    // POST: api/mining/local/share-telemetry
    // Batches non-consensus vardiff accounting without submitting incomplete proofs.
    [HttpPost("local/share-telemetry")]
    public IActionResult SubmitLocalShareTelemetry([FromBody] LocalMiningTelemetryBatchDto? batch)
    {
        if (!IsLocalAdapterAuthorized())
        {
            return Unauthorized(new { status = "rejected", reason = "Invalid local adapter token" });
        }

        if (batch == null)
        {
            return BadRequest(new { status = "rejected", reason = "Missing telemetry payload" });
        }

        if (batch.Entries.Count > _poolConfig.LocalAdapterTelemetryMaxBatchSize)
        {
            return BadRequest(new
            {
                status = "rejected",
                reason = $"Telemetry batch exceeds {_poolConfig.LocalAdapterTelemetryMaxBatchSize} entries"
            });
        }

        try
        {
            return Ok(_stateService.RecordLocalMiningTelemetryBatch(batch, ResolveLocalAdapterSource()));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { status = "rejected", reason = ex.Message });
        }
    }

    private async Task<IActionResult> SubmitShareCore(ShareSubmissionDto? share, string source, string blockSource)
    {
        DateTime transportReceivedUtc = DateTime.UtcNow;
        if (share == null)
        {
            return BadRequest(new { status = "rejected", reason = "Missing share payload" });
        }

        BootRequestValidationFailure? requestValidation = BootRequestGuards.ValidateShareRequest(
            _poolConfig,
            Request,
            share.MinerAddress,
            share.HeaderHex,
            share.CoinbaseHex,
            share.MerklePath);
        if (requestValidation.HasValue)
        {
            return StatusCode(requestValidation.Value.StatusCode, new { status = "rejected", reason = requestValidation.Value.Reason });
        }

        try
        {
            var result = await _stateService.SubmitShareAsync(new RecordedShareSubmission
            {
                MinerAddress = share.MinerAddress,
                Username = string.IsNullOrWhiteSpace(share.Username) ? string.Empty : share.Username,
                HeaderHex = share.HeaderHex,
                CoinbaseHex = share.CoinbaseHex,
                MerklePath = share.MerklePath,
                PayoutSnapshotId = share.PayoutSnapshotId,
                PrevBlockHash = share.PrevBlockHash,
                Difficulty = share.Difficulty,
                TransportReceivedUtc = transportReceivedUtc,
                Source = source
            }, blockSource);

            if (result.Accepted || string.Equals(result.RejectionReason, "Duplicate share", StringComparison.Ordinal))
            {
                return Ok(new
                {
                    status = result.Accepted ? "accepted" : "duplicate",
                    difficulty = result.ComputedDifficulty,
                    proofClass = result.ProofClass,
                    relayStage = result.RelayStage,
                    pulseAccepted = result.PulseAccepted,
                    affectedConsensusState = result.AffectedConsensusState,
                    blockCandidate = result.BlockCandidate,
                    isBlock = result.IsBlock,
                    blockHash = result.BlockHash,
                    stateId = result.IsBlock
                        ? result.NetworkStatus.CurrentStateId
                        : result.NetworkStatus.CandidateStateId
                });
            }
            else
            {
                // In production, don't be too verbose about WHY it failed to prevent gaming
                return BadRequest(new { status = "rejected", reason = result.RejectionReason ?? "Low difficulty or invalid proof" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing API share");
            return StatusCode(500, "Internal Server Error");
        }
    }

    private bool IsLocalAdapterAuthorized()
    {
        return _localAdapterAuth != null &&
            Request.Headers.TryGetValue(LocalMiningAdapterAuth.HeaderName, out var values) &&
            _localAdapterAuth.IsAuthorized(values.FirstOrDefault());
    }

    private string ResolveLocalAdapterSource()
    {
        const string sourceHeader = "X-GridPool-Adapter-Type";
        string source = Request.Headers.TryGetValue(sourceHeader, out var values)
            ? values.FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;

        return Regex.IsMatch(source, "^[a-z0-9][a-z0-9-]{0,31}$") ? source : "sv2";
    }

    private string ResolvePublicMiningSource()
    {
        const string sourceHeader = "X-GridPool-Mining-Source";
        string source = Request.Headers.TryGetValue(sourceHeader, out var values)
            ? values.FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty
            : string.Empty;

        // This header is an observability label only. It grants no local-adapter
        // privileges and full proof validation plus public rate limits still apply.
        return source is "hydrapool" or "ckpool" or "atlaspool" or "sv2"
            ? source
            : "http";
    }

    private string ResolveDatumHost()
    {
        if (!string.IsNullOrWhiteSpace(_poolConfig.DatumPublicHost))
        {
            string configured = _poolConfig.DatumPublicHost.Trim().TrimEnd('/');
            if (Uri.TryCreate(configured, UriKind.Absolute, out Uri? hostUri))
            {
                return hostUri.IsDefaultPort ? hostUri.Host : hostUri.Authority;
            }

            return configured;
        }

        if (Uri.TryCreate(_poolConfig.PublicBaseUrl, UriKind.Absolute, out Uri? publicUri))
        {
            return publicUri.IsDefaultPort ? publicUri.Host : publicUri.Authority;
        }

        return "--";
    }

    private int ResolveDatumPort()
    {
        return _poolConfig.DatumPublicPort > 0
            ? _poolConfig.DatumPublicPort
            : DatumServer.PoolPort;
    }
}
