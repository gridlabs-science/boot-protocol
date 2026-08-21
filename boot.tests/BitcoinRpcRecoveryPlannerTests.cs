using boot_portal.Services;

namespace boot.tests;

[TestClass]
public sealed class BitcoinRpcRecoveryPlannerTests
{
    [TestMethod]
    public void MissingLocalTipEstablishesRpcBaseline()
    {
        BitcoinRpcRecoveryPlan plan = BitcoinRpcRecoveryPlanner.Build(
            null,
            null,
            100,
            "block-100");

        CollectionAssert.AreEqual(new long[] { 100 }, plan.Heights.ToArray());
        Assert.IsTrue(plan.EstablishesBaseline);
        Assert.IsFalse(plan.Reorganization);
    }

    [TestMethod]
    public void MissedBlocksAreRecoveredSequentially()
    {
        BitcoinRpcRecoveryPlan plan = BitcoinRpcRecoveryPlanner.Build(
            100,
            "block-100",
            103,
            "block-103");

        CollectionAssert.AreEqual(new long[] { 101, 102, 103 }, plan.Heights.ToArray());
        Assert.IsFalse(plan.EstablishesBaseline);
        Assert.IsFalse(plan.Reorganization);
    }

    [TestMethod]
    public void SameHeightReplacementUsesReorganizationPath()
    {
        BitcoinRpcRecoveryPlan plan = BitcoinRpcRecoveryPlanner.Build(
            100,
            "old-block-100",
            100,
            "new-block-100");

        CollectionAssert.AreEqual(new long[] { 99, 100 }, plan.Heights.ToArray());
        Assert.IsTrue(plan.Reorganization);
    }

    [TestMethod]
    public void LowerRpcHeightReplaysReplacementTipAndParent()
    {
        BitcoinRpcRecoveryPlan plan = BitcoinRpcRecoveryPlanner.Build(
            101,
            "disconnected-block-101",
            100,
            "replacement-block-100");

        CollectionAssert.AreEqual(new long[] { 99, 100 }, plan.Heights.ToArray());
        Assert.IsTrue(plan.Reorganization);
    }

    [TestMethod]
    public void MatchingTipRequiresNoRecovery()
    {
        BitcoinRpcRecoveryPlan plan = BitcoinRpcRecoveryPlanner.Build(
            100,
            "AA",
            100,
            "aa");

        Assert.AreEqual(0, plan.Heights.Count);
        Assert.IsFalse(plan.Reorganization);
    }
}
