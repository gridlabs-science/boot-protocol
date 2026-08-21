using System.Net;
using System.Net.WebSockets;
using boot_portal.Models;
using boot_portal.Services;
using boot_portal.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace boot_portal.Controllers;

[ApiController]
[Route("api/peer")]
[EnableRateLimiting("peer-write")]
public class BootPeerController : ControllerBase
{
    private readonly PoolConfig _poolConfig;
    private readonly BootProtocolStateService _stateService;
    private readonly BootPeerSessionManager _sessionManager;
    private readonly ILogger<BootPeerController> _logger;

    public BootPeerController(
        PoolConfig poolConfig,
        BootProtocolStateService stateService,
        BootPeerSessionManager sessionManager,
        ILogger<BootPeerController> logger)
    {
        _poolConfig = poolConfig;
        _stateService = stateService;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    [HttpGet("session")]
    public async Task GetPeerSession()
    {
        if (!_poolConfig.EnablePeerSync || !_poolConfig.EnablePeerPersistentSessions)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { status = "rejected", reason = "Expected WebSocket upgrade" });
            return;
        }

        using WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await _sessionManager.AcceptInboundSessionAsync(socket, HttpContext.RequestAborted);
    }

    [HttpPost("share")]
    public async Task<IActionResult> SubmitPeerShare([FromBody] PeerShareAnnouncement? announcement)
    {
        DateTime transportReceivedUtc = DateTime.UtcNow;
        if (announcement?.Share == null)
        {
            return BadRequest(new { status = "rejected", reason = "Missing share payload" });
        }

        BootRequestValidationFailure? requestValidation = BootRequestGuards.ValidateShareRequest(
            _poolConfig,
            Request,
            announcement.Share.MinerAddress,
            announcement.Share.HeaderHex,
            announcement.Share.CoinbaseHex,
            announcement.Share.MerklePath);
        if (requestValidation.HasValue)
        {
            return StatusCode(requestValidation.Value.StatusCode, new { status = "rejected", reason = requestValidation.Value.Reason });
        }

        BootVersionCompatibilityDto compatibility = _stateService.EvaluatePeerShareCompatibility(announcement);
        if (!compatibility.NetworkCompatible || !compatibility.ConsensusCompatible || !compatibility.HttpApiCompatible)
        {
            _stateService.RecordExternalNetworkEvent(
                "peer-version-mismatch",
                string.IsNullOrWhiteSpace(announcement.SenderEndpoint) ? "peer-http" : announcement.SenderEndpoint,
                $"Rejected peer share relay: {compatibility.Reason}.");
            return BadRequest(new { status = "rejected", reason = compatibility.Reason });
        }

        string senderEndpoint = string.IsNullOrWhiteSpace(announcement.SenderEndpoint)
            ? string.Empty
            : announcement.SenderEndpoint;

        if (!string.IsNullOrWhiteSpace(senderEndpoint))
        {
            _stateService.MergeDiscoveredPeers([senderEndpoint]);
            _stateService.UpdatePeerHeartbeat(senderEndpoint, "share", null, DateTime.UtcNow);
        }

        var result = await _stateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = announcement.Share.MinerAddress,
            Username = string.IsNullOrWhiteSpace(announcement.Share.Username) ? string.Empty : announcement.Share.Username,
            HeaderHex = announcement.Share.HeaderHex,
            CoinbaseHex = announcement.Share.CoinbaseHex,
            MerklePath = announcement.Share.MerklePath,
            PayoutSnapshotId = announcement.Share.PayoutSnapshotId,
            PrevBlockHash = announcement.Share.PrevBlockHash,
            Difficulty = announcement.Share.Difficulty,
            PayloadBytes = Request.ContentLength.HasValue
                ? (int)Math.Min(int.MaxValue, Request.ContentLength.Value)
                : 0,
            TransportReceivedUtc = transportReceivedUtc,
            ProofClass = string.IsNullOrWhiteSpace(announcement.Share.ProofClass) ? announcement.ProofClass : announcement.Share.ProofClass,
            RelayStage = string.IsNullOrWhiteSpace(announcement.Share.RelayStage) ? announcement.RelayStage : announcement.Share.RelayStage,
            RelayTtl = announcement.Share.RelayTtl != 0 ? announcement.Share.RelayTtl : announcement.RelayTtl,
            Source = string.IsNullOrWhiteSpace(senderEndpoint) ? "peer-http" : $"peer-http:{senderEndpoint}"
        }, "peer-block");

        if (!result.Accepted && !string.Equals(result.RejectionReason, "Duplicate share", StringComparison.Ordinal))
        {
            _logger.LogWarning("Rejected peer share from {SenderEndpoint}: {Reason}", senderEndpoint, result.RejectionReason);
            return BadRequest(new { status = "rejected", reason = result.RejectionReason });
        }

        return Ok(new
        {
            status = result.Accepted ? "accepted" : "duplicate",
            reason = result.RejectionReason,
            proofClass = result.ProofClass,
            relayStage = result.RelayStage,
            stateId = result.IsBlock
                ? result.NetworkStatus.CurrentStateId
                : result.NetworkStatus.CandidateStateId,
            difficulty = result.ComputedDifficulty,
            blockCandidate = result.BlockCandidate,
            isBlock = result.IsBlock,
            blockHash = result.BlockHash
        });
    }
}
