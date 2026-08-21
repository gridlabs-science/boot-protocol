namespace boot_portal.Services;

public sealed record BitcoinRpcRecoveryPlan(
    IReadOnlyList<long> Heights,
    bool EstablishesBaseline,
    bool Reorganization);

public static class BitcoinRpcRecoveryPlanner
{
    private const int ShallowReorganizationLookback = 1;

    public static BitcoinRpcRecoveryPlan Build(
        long? localHeight,
        string? localHash,
        long rpcHeight,
        string rpcBestHash)
    {
        if (!localHeight.HasValue || string.IsNullOrWhiteSpace(localHash))
        {
            return new BitcoinRpcRecoveryPlan([rpcHeight], true, false);
        }

        if (localHeight.Value == rpcHeight &&
            Utils.BitcoinHashes.AreEquivalent(localHash, rpcBestHash))
        {
            return new BitcoinRpcRecoveryPlan([], false, false);
        }

        if (localHeight.Value > rpcHeight)
        {
            return new BitcoinRpcRecoveryPlan(ReorganizationHeights(rpcHeight), false, true);
        }

        if (localHeight.Value == rpcHeight)
        {
            return new BitcoinRpcRecoveryPlan(ReorganizationHeights(rpcHeight), false, true);
        }

        return new BitcoinRpcRecoveryPlan(
            Enumerable.Range(
                    checked((int)(localHeight.Value + 1)),
                    checked((int)(rpcHeight - localHeight.Value)))
                .Select(height => (long)height)
                .ToList(),
            false,
            false);
    }

    private static IReadOnlyList<long> ReorganizationHeights(long tipHeight)
    {
        long firstHeight = Math.Max(0, tipHeight - ShallowReorganizationLookback);
        return Enumerable.Range(
                checked((int)firstHeight),
                checked((int)(tipHeight - firstHeight + 1)))
            .Select(height => (long)height)
            .ToList();
    }
}
