using System.Text;
using boot_portal.Utils;

namespace boot.tests;

[TestClass]
public sealed class PoolConfigValidatorTests
{
    private const string MainnetPayoutAddress = "bc1qd9m04z95mglaxd9e9accmhyjdlmkfmzjprkq4p";

    [TestMethod]
    public void DefaultConfigurationRequiresExplicitPayoutAddress()
    {
        var config = new PoolConfig();

        Assert.AreEqual("Grid Pool", config.CoinbaseTag);
        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("pool_payout_script", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PulseProofsDefaultToEnabled()
    {
        Assert.IsTrue(new PoolConfig().EnablePulseProofs);
    }

    [TestMethod]
    public void BitcoinNotificationConfigurationRejectsUnsafeOrAmbiguousRpcSettings()
    {
        var config = new PoolConfig
        {
            BitcoinNotificationMode = "unknown",
            BitcoinRpcUrl = "http://user:password@bitcoin:8332",
            BitcoinRpcCookieFile = "/data/.cookie",
            BitcoinRpcUsername = "user"
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("bitcoin_notification_mode", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("bitcoin_rpc_url", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("bitcoin_rpc_cookie_file", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProductionRejectsPrivateAdvertisedEndpoint()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "http://192.168.1.245:5000",
            DatumPublicHost = "datum.gridpool.net",
            EnableAdminApi = false
        };

        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("private", StringComparison.OrdinalIgnoreCase)));
    }

    [DataTestMethod]
    [DataRow("https://boot.example.com")]
    [DataRow("http://localhost:5000")]
    [DataRow("https://rebuilt.example.com")]
    public void ProductionRejectsPlaceholderPublicUrl(string publicBaseUrl)
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = publicBaseUrl,
            DatumPublicHost = "datum.gridpool.net",
            EnableAdminApi = false
        };

        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("public_base_url", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("must not", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProductionMainnetBetaRejectsDisabledPulseProofs()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "https://node.gridpool.net",
            DatumPublicHost = "datum.gridpool.net",
            EnableAdminApi = false,
            EnablePulseProofs = false
        };

        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("enable_pulse_proofs", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void V22ActivationHeightMustBeNonNegative()
    {
        var config = new PoolConfig { V22ActivationBlockHeight = -1 };

        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("v22_activation_block_height", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProductionMainnetRejectsImmediateV22Activation()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "https://use1.gridlabs.science",
            DatumPublicHost = "datum-use1.gridlabs.science",
            EnableAdminApi = false,
            TestingRoundResetMode = "none",
            V22ActivationBlockHeight = 0
        };

        Assert.IsTrue(PoolConfigValidator.Validate(config).Any(error =>
            error.Contains("v22_activation_block_height", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void EmptyCoinbaseTagIsAllowed()
    {
        var config = new PoolConfig
        {
            CoinbaseTag = string.Empty,
            PoolPayoutScript = MainnetPayoutAddress,
            EnableAdminApi = false
        };

        CollectionAssert.AreEqual(Array.Empty<string>(), PoolConfigValidator.Validate(config));
    }

    [TestMethod]
    public void TooLongCoinbaseTagFailsValidation()
    {
        var config = new PoolConfig
        {
            CoinbaseTag = new string('x', PoolConfigValidator.MaxDatumCoinbaseTagBytes + 1)
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("coinbase_tag", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void MultibyteCoinbaseTagLengthUsesUtf8Bytes()
    {
        var config = new PoolConfig
        {
            CoinbaseTag = string.Concat(Enumerable.Repeat("é", 128))
        };

        Assert.AreEqual(256, Encoding.UTF8.GetByteCount(config.CoinbaseTag));
        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("coinbase_tag", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void InvalidPayoutAddressFailsValidation()
    {
        var config = new PoolConfig
        {
            PoolPayoutScript = "not-a-bitcoin-address"
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("pool_payout_script", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void MainnetRejectsTestnetPayoutAddress()
    {
        byte[] script = Enumerable.Range(0, 22).Select(i => (byte)i).ToArray();
        script[0] = 0x00;
        script[1] = 0x14;
        string testnetAddress = BitcoinScript.ScriptToAddress(script, BitcoinScript.Testnet4);

        var config = new PoolConfig
        {
            BitcoinNetwork = BitcoinScript.Mainnet,
            PoolPayoutScript = testnetAddress
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("pool_payout_script", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Testnet4AcceptsTestnetPayoutAddressAndRejectsMainnetAddress()
    {
        byte[] script = Enumerable.Range(0, 22).Select(i => (byte)i).ToArray();
        script[0] = 0x00;
        script[1] = 0x14;
        string testnetAddress = BitcoinScript.ScriptToAddress(script, BitcoinScript.Testnet4);

        var validConfig = new PoolConfig
        {
            BitcoinNetwork = BitcoinScript.Testnet4,
            PoolPayoutScript = testnetAddress,
            EnableAdminApi = false
        };

        CollectionAssert.AreEqual(Array.Empty<string>(), PoolConfigValidator.Validate(validConfig));

        validConfig.PoolPayoutScript = "bc1qrwsx8fs0l6z7ugp5cvzy6lhss7jlyru3kg9s8y";
        List<string> errors = PoolConfigValidator.Validate(validConfig);

        Assert.IsTrue(errors.Any(error => error.Contains("pool_payout_script", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void UnsupportedBitcoinNetworkFailsValidation()
    {
        var config = new PoolConfig
        {
            BitcoinNetwork = "regtest"
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("bitcoin_network", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void BadRateLimitFailsValidation()
    {
        var config = new PoolConfig
        {
            PeerWriteRateLimitPerMinute = 0
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("peer_write_rate_limit_per_minute", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SovereignModeIsAcceptedForInstallerNodes()
    {
        var config = new PoolConfig
        {
            NodeMode = "sovereign",
            PublicBaseUrl = "http://edge-node.local:5000",
            DatumPublicHost = "edge-node.local",
            PoolPayoutScript = MainnetPayoutAddress,
            EnableAdminApi = false
        };

        CollectionAssert.AreEqual(Array.Empty<string>(), PoolConfigValidator.Validate(config));
    }

    [TestMethod]
    public void ProductionRequiresPublicEndpointsAndDisablesTestingReset()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = string.Empty,
            DatumPublicHost = string.Empty,
            TestingRoundResetMode = "block_hash_low_nibble"
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("public_base_url", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("datum_public_host", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(errors.Any(error => error.Contains("testing_round_reset_mode", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProductionRejectsUncondensedCoinbaseStressMode()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "https://use1.gridlabs.science",
            DatumPublicHost = "datum-use1.gridlabs.science",
            EnableAdminApi = false,
            TestingRoundResetMode = "none",
            CoinbaseUncondensedOutputsEnabled = true
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("coinbase_uncondensed_outputs_enabled", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProductionAcceptsExplicitPublicEndpoints()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "https://use1.gridlabs.science",
            DatumPublicHost = "datum-use1.gridlabs.science",
            EnableAdminApi = false,
            TestingRoundResetMode = "none",
            PoolPayoutScript = MainnetPayoutAddress
        };

        CollectionAssert.AreEqual(Array.Empty<string>(), PoolConfigValidator.Validate(config));
    }

    [TestMethod]
    public void ProductionRejectsWeakAdminKeyWhenAdminApiIsEnabled()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "https://use1.gridlabs.science",
            DatumPublicHost = "datum-use1.gridlabs.science",
            EnableAdminApi = true,
            AdminApiKey = "change-this-admin-key",
            TestingRoundResetMode = "none"
        };

        List<string> errors = PoolConfigValidator.Validate(config);

        Assert.IsTrue(errors.Any(error => error.Contains("admin_api_key", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ProductionAcceptsStrongAdminKeyWhenAdminApiIsEnabled()
    {
        var config = new PoolConfig
        {
            NodeMode = "production",
            PublicBaseUrl = "https://use1.gridlabs.science",
            DatumPublicHost = "datum-use1.gridlabs.science",
            EnableAdminApi = true,
            AdminApiKey = new string('a', 32),
            TestingRoundResetMode = "none",
            PoolPayoutScript = MainnetPayoutAddress
        };

        CollectionAssert.AreEqual(Array.Empty<string>(), PoolConfigValidator.Validate(config));
    }
}
