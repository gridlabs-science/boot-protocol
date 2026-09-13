using System.ComponentModel.DataAnnotations;
using boot_portal.Models;
using boot_portal.Services;
using boot_portal.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;

namespace boot_portal.Pages;

public sealed class SetupModel(
    PoolConfig poolConfig,
    NodeSetupState setupState,
    IHostApplicationLifetime applicationLifetime,
    ILogger<SetupModel> logger) : PageModel
{
    private readonly PoolConfig _poolConfig = poolConfig;
    private readonly NodeSetupState _setupState = setupState;
    private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
    private readonly ILogger<SetupModel> _logger = logger;

    [BindProperty]
    [Required(ErrorMessage = "Bitcoin payout address is required.")]
    public string PoolPayoutScript { get; set; } = string.Empty;

    public string? SavedAddress { get; private set; }

    public bool RestartRequired => _setupState.RestartRequired;

    public bool AutomaticRestart => RestartRequired && _poolConfig.RestartAfterSetup;

    public string BitcoinNetwork => BitcoinScript.NormalizeNetwork(_poolConfig.BitcoinNetwork);

    public IActionResult OnGet()
    {
        if (_setupState.OperationalAtStartup)
        {
            return Redirect("/");
        }

        SavedAddress = _setupState.RestartRequired
            ? _setupState.PendingPayoutAddress
            : _poolConfig.PoolPayoutScript;

        return Page();
    }

    public IActionResult OnPost()
    {
        if (_setupState.OperationalAtStartup)
        {
            return Redirect("/");
        }

        if (_setupState.RestartRequired)
        {
            SavedAddress = _setupState.PendingPayoutAddress;
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var bitcoinNetwork = BitcoinScript.NormalizeNetwork(_poolConfig.BitcoinNetwork);
        if (!BitcoinScript.TryAddressToScriptPubKey(PoolPayoutScript.Trim(), bitcoinNetwork, out _))
        {
            ModelState.AddModelError(nameof(PoolPayoutScript), $"Bitcoin payout address is not valid for bitcoin network {bitcoinNetwork}.");
            return Page();
        }

        string payoutAddress = PoolPayoutScript.Trim();

        try
        {
            PoolConfigValidator.SaveSetupConfig(_poolConfig, payoutAddress);
            _poolConfig.PoolPayoutScript = payoutAddress;
            _setupState.MarkSaved(payoutAddress);
            SavedAddress = payoutAddress;
            if (_poolConfig.RestartAfterSetup)
            {
                HttpContext.Response.OnCompleted(() =>
                {
                    _applicationLifetime.StopApplication();
                    return Task.CompletedTask;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist the initial GridPool payout address");
            ModelState.AddModelError(
                string.Empty,
                "GridPool could not save the payout address. Check the app data permissions and try again.");
            return Page();
        }

        return Page();
    }
}
