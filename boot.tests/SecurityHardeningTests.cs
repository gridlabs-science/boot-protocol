using System.Net;
using boot_portal.Controllers;
using boot_portal.Services;
using boot_portal.Utils;

namespace boot.tests;

[TestClass]
public sealed class SecurityHardeningTests
{
    private const string MainnetPayoutAddress = "bc1qd9m04z95mglaxd9e9accmhyjdlmkfmzjprkq4p";

    [TestMethod]
    public void DatumResourceBoundsAreValidated()
    {
        var config = ValidConfig();
        config.DatumMaxConnections = 0;
        config.DatumReadTimeoutSeconds = 0;

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("datum_max_connections", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("datum_read_timeout_seconds", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AdminApiRequiresStrongKeyInSovereignMode()
    {
        var config = ValidConfig();
        config.EnableAdminApi = true;
        config.AdminApiKey = "change-this-admin-key";

        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("admin_api_key", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void StoredMinerLabelIsBoundedAndMarkupFree()
    {
        string normalized = BootShareVerifier.NormalizeUsernameForStorage(
            "<img src=x onerror=alert(1)>worker/../../" + new string('x', 256),
            MainnetPayoutAddress);

        Assert.IsTrue(normalized.Length <= 128);
        Assert.IsFalse(normalized.Contains('<'));
        Assert.IsFalse(normalized.Contains('>'));
        Assert.IsFalse(normalized.Contains('/'));
        Assert.IsTrue(normalized.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-' or ':'));
    }

    [DataTestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.1.2.3")]
    [DataRow("172.16.0.1")]
    [DataRow("192.168.1.1")]
    [DataRow("169.254.1.1")]
    [DataRow("100.64.0.1")]
    [DataRow("224.0.0.1")]
    [DataRow("::1")]
    [DataRow("fe80::1")]
    [DataRow("fc00::1")]
    public void ReachabilityProbeRejectsNonPublicDestinations(string value)
    {
        Assert.IsTrue(BootNetworkController.IsNonPublicAddress(IPAddress.Parse(value)));
    }

    [TestMethod]
    public void ReachabilityProbeAcceptsPublicUnicastDestination()
    {
        Assert.IsFalse(BootNetworkController.IsNonPublicAddress(IPAddress.Parse("1.1.1.1")));
    }

    [TestMethod]
    public void PeerBundleFetchLimiterBoundsChangingStateIdsPerPeer()
    {
        var limiter = new PeerBundleFetchLimiter(2, TimeSpan.FromMinutes(1));
        DateTime start = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

        Assert.IsTrue(limiter.TryAcquire("https://peer.example", start));
        Assert.IsTrue(limiter.TryAcquire("https://peer.example", start.AddSeconds(1)));
        Assert.IsFalse(limiter.TryAcquire("https://peer.example", start.AddSeconds(2)));
        Assert.IsTrue(limiter.TryAcquire("https://other.example", start.AddSeconds(2)));
        Assert.IsTrue(limiter.TryAcquire("https://peer.example", start.AddMinutes(1).AddSeconds(1)));
    }

    private static PoolConfig ValidConfig()
    {
        return new PoolConfig
        {
            PoolPayoutScript = MainnetPayoutAddress,
            EnableAdminApi = false
        };
    }
}
