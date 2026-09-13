using boot_portal.Models;
using boot_portal.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace boot_portal.Controllers;

[ApiController]
[Route("api/dashboard/v1")]
public sealed class DashboardController : ControllerBase
{
    private readonly BootProtocolStateService _stateService;
    private readonly DashboardReadModelService _dashboard;
    private readonly DashboardVisualizationJournalService _visualization;
    private readonly PoolConfig _poolConfig;

    public DashboardController(
        BootProtocolStateService stateService,
        DashboardReadModelService dashboard,
        DashboardVisualizationJournalService visualization,
        PoolConfig poolConfig)
    {
        _stateService = stateService;
        _dashboard = dashboard;
        _visualization = visualization;
        _poolConfig = poolConfig;
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("summary")]
    public IActionResult GetSummary([FromQuery] string? window = "24h") =>
        Ok(_dashboard.BuildSummary(window));

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("history")]
    public IActionResult GetHistory([FromQuery] string? window = "24h") =>
        Ok(_dashboard.BuildHistory(window));

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("address/{address}")]
    public IActionResult GetAddress(string address)
    {
        try
        {
            return Ok(_dashboard.BuildAddress(address));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return BadRequest(new
            {
                status = "rejected",
                reason = "Address is not valid for this node's Bitcoin network."
            });
        }
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("operator")]
    public IActionResult GetOperator()
    {
        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        if (!CanViewOperatorDiagnostics(
                _poolConfig,
                _stateService.IsAdminAuthorized(apiKey)))
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(_dashboard.BuildOperator());
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("diagram")]
    public IActionResult GetDiagram()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(_dashboard.BuildDiagram(
            includeOperatorDetails: _poolConfig.TrustedPrivateDashboardEnabled));
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("diagram/events")]
    public IActionResult GetDiagramEvents([FromQuery] long after = 0, [FromQuery] int limit = 256)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(_visualization.Read(
            Math.Max(0, after),
            limit,
            redacted: !_poolConfig.TrustedPrivateDashboardEnabled));
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("diagram/history")]
    public IActionResult GetDiagramHistory(
        [FromQuery] string? window = "24h",
        [FromQuery] int limit = 256)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(_dashboard.BuildDiagramHistory(
            window,
            limit,
            includeOperatorDetails: _poolConfig.TrustedPrivateDashboardEnabled));
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("diagram/operator")]
    public IActionResult GetOperatorDiagram()
    {
        if (!IsAdminAuthorized())
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(_dashboard.BuildDiagram(includeOperatorDetails: true));
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("diagram/operator/events")]
    public IActionResult GetOperatorDiagramEvents([FromQuery] long after = 0, [FromQuery] int limit = 256)
    {
        if (!IsAdminAuthorized())
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(_visualization.Read(Math.Max(0, after), limit, redacted: false));
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("diagram/operator/history")]
    public IActionResult GetOperatorDiagramHistory(
        [FromQuery] string? window = "24h",
        [FromQuery] int limit = 256)
    {
        if (!IsAdminAuthorized())
        {
            return Unauthorized(new { status = "rejected", reason = "Missing or invalid admin key" });
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(_dashboard.BuildDiagramHistory(window, limit, includeOperatorDetails: true));
    }

    [EnableRateLimiting("dashboard-read")]
    [HttpGet("schema")]
    public IActionResult GetSchema() =>
        Ok(new
        {
            schemaVersion = 1,
            endpoints = new
            {
                summary = "/api/dashboard/v1/summary?window=24h",
                history = "/api/dashboard/v1/history?window=24h",
                address = "/api/dashboard/v1/address/{address}",
                @operator = "/api/dashboard/v1/operator",
                diagram = "/api/dashboard/v1/diagram",
                diagramEvents = "/api/dashboard/v1/diagram/events?after={sequence}",
                diagramHistory = "/api/dashboard/v1/diagram/history?window=24h&limit=256",
                operatorDiagram = "/api/dashboard/v1/diagram/operator",
                operatorDiagramEvents = "/api/dashboard/v1/diagram/operator/events?after={sequence}",
                operatorDiagramHistory = "/api/dashboard/v1/diagram/operator/history?window=24h&limit=256"
            },
            windows = DashboardWindows.Supported.Keys,
            realtime = new
            {
                hub = "/dashboardHub",
                method = "DashboardChanged",
                payload = new[] { "revision", "timestampUtc", "topics" }
            },
            authentication = new
            {
                operatorHeader = "X-Boot-Admin-Key",
                trustedPrivateDashboard = _poolConfig.TrustedPrivateDashboardEnabled,
                storageGuidance = "Keep operator credentials in memory only."
            }
        });

    private bool IsAdminAuthorized()
    {
        string? apiKey = Request.Headers["X-Boot-Admin-Key"].FirstOrDefault();
        return CanViewOperatorDiagnostics(
            _poolConfig,
            _stateService.IsAdminAuthorized(apiKey));
    }

    internal static bool CanViewOperatorDiagnostics(PoolConfig config, bool adminAuthorized) =>
        config.TrustedPrivateDashboardEnabled || adminAuthorized;
}
