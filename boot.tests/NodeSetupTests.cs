using System.Text.Json.Nodes;
using System.Text.Json;
using boot_portal.Models;
using boot_portal.Services;
using boot_portal.Utils;
using Microsoft.AspNetCore.Http;

namespace boot.tests;

[TestClass]
[DoNotParallelize]
public sealed class NodeSetupTests
{
    private const string MainnetAddress = "bc1qd9m04z95mglaxd9e9accmhyjdlmkfmzjprkq4p";
    private string? _previousLocalConfigPath;
    private string _temporaryDirectory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _previousLocalConfigPath = Environment.GetEnvironmentVariable("BOOT_PORTAL_LOCAL_CONFIG_PATH");
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gridpool-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        Environment.SetEnvironmentVariable(
            "BOOT_PORTAL_LOCAL_CONFIG_PATH",
            Path.Combine(_temporaryDirectory, "boot_portal_config.local.json"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("BOOT_PORTAL_LOCAL_CONFIG_PATH", _previousLocalConfigPath);
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void HeadlessNodeRequiresPayoutAddress()
    {
        var config = new PoolConfig
        {
            EnableWebUi = false,
            PoolPayoutScript = string.Empty
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Contains("pool_payout_script is required when enable_web_ui is false"));
    }

    [TestMethod]
    public void SetupSaveIsAtomicAndPreservesExistingOverrides()
    {
        string path = BootPortalPaths.LocalConfigFilePath;
        File.WriteAllText(path, "{\"coinbase_tag\":\"operator\"}");
        var config = new PoolConfig { BitcoinNetwork = BitcoinScript.Mainnet };

        PoolConfigValidator.SaveSetupConfig(config, MainnetAddress);

        JsonObject saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.AreEqual("operator", saved["coinbase_tag"]?.GetValue<string>());
        Assert.AreEqual(MainnetAddress, saved["pool_payout_script"]?.GetValue<string>());
        Assert.AreEqual(true, saved["setup_completed"]?.GetValue<bool>());
        Assert.AreEqual(0, Directory.GetFiles(_temporaryDirectory, "*.tmp").Length);
    }

    [TestMethod]
    public void SetupSaveDoesNotReplaceMalformedExistingConfig()
    {
        string path = BootPortalPaths.LocalConfigFilePath;
        File.WriteAllText(path, "not-json");
        var config = new PoolConfig { BitcoinNetwork = BitcoinScript.Mainnet };

        try
        {
            PoolConfigValidator.SaveSetupConfig(config, MainnetAddress);
            Assert.Fail("Malformed local configuration should not be overwritten.");
        }
        catch (JsonException)
        {
        }
        Assert.AreEqual("not-json", File.ReadAllText(path));
    }

    [TestMethod]
    public void SetupStateRequiresRestartAfterSave()
    {
        var state = new NodeSetupState(operationalAtStartup: false);

        state.MarkSaved(MainnetAddress);

        Assert.IsFalse(state.OperationalAtStartup);
        Assert.IsTrue(state.RestartRequired);
        Assert.AreEqual(MainnetAddress, state.PendingPayoutAddress);
    }

    [TestMethod]
    public void SetupModeAllowsOnlySetupAndHealthPaths()
    {
        Assert.IsTrue(NodeSetupPolicy.IsAllowedSetupPath("/setup"));
        Assert.IsTrue(NodeSetupPolicy.IsAllowedSetupPath("/setup.css"));
        Assert.IsFalse(NodeSetupPolicy.IsAllowedSetupPath("/setup.js"));
        Assert.IsTrue(NodeSetupPolicy.IsAllowedSetupPath("/health/live"));
        Assert.IsTrue(NodeSetupPolicy.IsAllowedSetupPath("/health/ready"));
        Assert.IsFalse(NodeSetupPolicy.IsAllowedSetupPath("/api/mining/share"));
        Assert.IsFalse(NodeSetupPolicy.IsAllowedSetupPath("/api/peer/session"));
        Assert.IsFalse(NodeSetupPolicy.IsAllowedSetupPath("/dashboardHub"));
    }

    [TestMethod]
    public void BrowserNavigationIsSeparatedFromApiRequests()
    {
        var browser = new DefaultHttpContext();
        browser.Request.Method = HttpMethods.Get;
        browser.Request.Headers.Accept = "text/html,application/xhtml+xml";
        var api = new DefaultHttpContext();
        api.Request.Method = HttpMethods.Get;
        api.Request.Headers.Accept = "application/json";

        Assert.IsTrue(NodeSetupPolicy.WantsHtml(browser.Request));
        Assert.IsFalse(NodeSetupPolicy.WantsHtml(api.Request));
    }
}
