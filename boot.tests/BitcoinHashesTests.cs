using boot_portal.Utils;

namespace boot.tests;

[TestClass]
public sealed class BitcoinHashesTests
{
    private const string RegtestGenesisHeader =
        "01000000" +
        "0000000000000000000000000000000000000000000000000000000000000000" +
        "3ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a" +
        "dae5494d" +
        "ffff7f20" +
        "02000000";

    [TestMethod]
    public void RegtestPowLimitAcceptsRegtestGenesisAndMainnetRejectsIt()
    {
        BitcoinHeaderEvaluation regtest = BitcoinHashes.EvaluateHeader(
            RegtestGenesisHeader,
            DateTime.UtcNow,
            BitcoinScript.Regtest);
        BitcoinHeaderEvaluation mainnet = BitcoinHashes.EvaluateHeader(
            RegtestGenesisHeader,
            DateTime.UtcNow,
            BitcoinScript.Mainnet);

        Assert.IsTrue(regtest.IsValid, regtest.RejectionReason);
        Assert.AreEqual(
            "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206",
            regtest.BlockHash);
        Assert.IsFalse(mainnet.IsValid);
        StringAssert.Contains(mainnet.RejectionReason, "proof-of-work limit");
    }
}
