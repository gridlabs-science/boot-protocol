using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using boot_portal.Models;
using boot_portal.Utils;

namespace boot_portal.Services;

public sealed class BootShareValidationResult
{
    public bool IsValid { get; init; }
    public string? RejectionReason { get; init; }
    public string ShareId { get; init; } = string.Empty;
    public string MinerAddress { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string ScriptPubKeyHex { get; init; } = string.Empty;
    public string HeaderHex { get; init; } = string.Empty;
    public string CoinbaseHex { get; init; } = string.Empty;
    public List<string> MerklePath { get; init; } = [];
    public string PrevBlockHash { get; init; } = string.Empty;
    public string BlockHash { get; init; } = string.Empty;
    public double Difficulty { get; init; }
    public bool IsBlock { get; init; }
}

public sealed class BootShareHeaderEvaluationResult
{
    public bool IsValid { get; init; }
    public string? RejectionReason { get; init; }
    public string ShareId { get; init; } = string.Empty;
    public string HeaderHex { get; init; } = string.Empty;
    public string CoinbaseHex { get; init; } = string.Empty;
    public string PrevBlockHash { get; init; } = string.Empty;
    public string BlockHash { get; init; } = string.Empty;
    public double Difficulty { get; init; }
    public bool IsBlock { get; init; }
}

public class BootShareVerifier
{
    private static readonly BigInteger DifficultyOneTarget = DecodeCompactTarget(0x1d00ffff);
    private const int MaxExpectedOutputPlanCacheEntries = 512;
    private readonly string _bitcoinNetwork;
    private readonly object _expectedOutputPlanCacheLock = new();
    private readonly Dictionary<string, ExpectedWinnerOutputPlan> _expectedOutputPlanCache = new(StringComparer.Ordinal);

    public BootShareVerifier()
        : this(new PoolConfig())
    {
    }

    public BootShareVerifier(PoolConfig poolConfig)
    {
        _bitcoinNetwork = BitcoinScript.NormalizeNetwork(poolConfig.BitcoinNetwork);
    }

    public BootShareValidationResult ValidateShare(
        RecordedShareSubmission share,
        IReadOnlyList<PayoutInfo> expectedWinners,
        string? expectedPrevBlockHash)
    {
        return ValidateShare(
            share,
            expectedWinners,
            string.IsNullOrWhiteSpace(expectedPrevBlockHash) ? [] : [expectedPrevBlockHash]);
    }

    public BootShareValidationResult ValidateShare(
        RecordedShareSubmission share,
        IReadOnlyList<PayoutInfo> expectedWinners,
        IReadOnlyCollection<string> expectedPrevBlockHashes)
    {
        return ValidateCore(
            share.MinerAddress,
            share.Username,
            share.HeaderHex,
            share.CoinbaseHex,
            share.MerklePath,
            share.PrevBlockHash,
            expectedWinners,
            expectedPrevBlockHashes,
            expectedShareId: null);
    }

    public BootShareValidationResult ValidateShare(
        BootShareProof proof,
        IReadOnlyList<PayoutInfo> expectedWinners,
        string? expectedPrevBlockHash)
    {
        return ValidateShare(
            proof,
            expectedWinners,
            string.IsNullOrWhiteSpace(expectedPrevBlockHash) ? [] : [expectedPrevBlockHash]);
    }

    public BootShareValidationResult ValidateShare(
        BootShareProof proof,
        IReadOnlyList<PayoutInfo> expectedWinners,
        IReadOnlyCollection<string> expectedPrevBlockHashes)
    {
        return ValidateCore(
            proof.MinerAddress,
            proof.Username,
            proof.HeaderHex,
            proof.CoinbaseHex,
            proof.MerklePath,
            proof.PrevBlockHash,
            expectedWinners,
            expectedPrevBlockHashes,
            proof.ShareId);
    }

    public BootShareHeaderEvaluationResult EvaluateHeaderDifficulty(RecordedShareSubmission share)
    {
        try
        {
            string normalizedHeaderHex = BitcoinHashes.NormalizeHex(share.HeaderHex);
            if (normalizedHeaderHex.Length != 160)
            {
                return InvalidHeader("Header must be exactly 80 bytes.");
            }

            string normalizedCoinbaseHex = BitcoinHashes.NormalizeHex(share.CoinbaseHex);
            if (string.IsNullOrWhiteSpace(normalizedCoinbaseHex) || normalizedCoinbaseHex.Length % 2 != 0)
            {
                return InvalidHeader("Coinbase transaction hex is invalid.");
            }

            byte[] headerBytes = Convert.FromHexString(normalizedHeaderHex);
            byte[] headerPrevBlockHash = headerBytes.AsSpan(4, 32).ToArray();
            string actualPrevBlockHash = BitcoinHashes.ToDisplayHashHex(headerPrevBlockHash);
            if (!string.IsNullOrWhiteSpace(share.PrevBlockHash) &&
                !BitcoinHashes.AreEquivalent(share.PrevBlockHash, actualPrevBlockHash))
            {
                return InvalidHeader("Prev block hash does not match the submitted header.");
            }

            byte[] headerHash = DoubleSha256(headerBytes);
            string blockHash = BitcoinHashes.ToDisplayHashHex(headerHash);
            uint compactTarget = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(72, 4));
            BigInteger target = DecodeCompactTarget(compactTarget);
            if (target <= BigInteger.Zero)
            {
                return InvalidHeader("Header target is invalid.");
            }

            BigInteger hashValue = ToPositiveBigInteger(headerHash);
            double difficulty = hashValue.IsZero ? double.MaxValue : (double)DifficultyOneTarget / (double)hashValue;
            return new BootShareHeaderEvaluationResult
            {
                IsValid = true,
                ShareId = ComputeShareId(normalizedHeaderHex, normalizedCoinbaseHex),
                HeaderHex = normalizedHeaderHex,
                CoinbaseHex = normalizedCoinbaseHex,
                PrevBlockHash = actualPrevBlockHash,
                BlockHash = blockHash,
                Difficulty = difficulty,
                IsBlock = hashValue <= target
            };
        }
        catch (FormatException)
        {
            return InvalidHeader("Share payload is not valid hex.");
        }
        catch (Exception ex)
        {
            return InvalidHeader(ex.Message);
        }
    }

    private BootShareValidationResult ValidateCore(
        string minerAddress,
        string username,
        string headerHex,
        string coinbaseHex,
        List<string> merklePath,
        string? providedPrevBlockHash,
        IReadOnlyList<PayoutInfo> expectedWinners,
        IReadOnlyCollection<string> expectedPrevBlockHashes,
        string? expectedShareId)
    {
        try
        {
            string normalizedHeaderHex = BitcoinHashes.NormalizeHex(headerHex);
            if (normalizedHeaderHex.Length != 160)
            {
                return Invalid("Header must be exactly 80 bytes.");
            }

            string normalizedCoinbaseHex = BitcoinHashes.NormalizeHex(coinbaseHex);
            if (string.IsNullOrWhiteSpace(normalizedCoinbaseHex) || normalizedCoinbaseHex.Length % 2 != 0)
            {
                return Invalid("Coinbase transaction hex is invalid.");
            }

            byte[] headerBytes = Convert.FromHexString(normalizedHeaderHex);
            byte[] coinbaseBytes = Convert.FromHexString(normalizedCoinbaseHex);

            List<byte[]> branchBytes = [];
            List<string> normalizedMerklePath = [];
            foreach (string branchHex in merklePath)
            {
                string normalizedBranchHex = BitcoinHashes.NormalizeHex(branchHex);
                if (normalizedBranchHex.Length != 64)
                {
                    return Invalid("Merkle path entries must be 32-byte hashes.");
                }

                branchBytes.Add(Convert.FromHexString(normalizedBranchHex));
                normalizedMerklePath.Add(normalizedBranchHex);
            }

            byte[] headerPrevBlockHash = headerBytes.AsSpan(4, 32).ToArray();
            string actualPrevBlockHash = BitcoinHashes.ToDisplayHashHex(headerPrevBlockHash);
            if (!string.IsNullOrWhiteSpace(providedPrevBlockHash) &&
                !BitcoinHashes.AreEquivalent(providedPrevBlockHash, actualPrevBlockHash))
            {
                return Invalid("Prev block hash does not match the submitted header.");
            }

            bool hasExpectedParents = expectedPrevBlockHashes.Any(hash => !string.IsNullOrWhiteSpace(hash));
            if (hasExpectedParents &&
                !expectedPrevBlockHashes.Any(hash =>
                    !string.IsNullOrWhiteSpace(hash) &&
                    BitcoinHashes.AreEquivalent(hash, actualPrevBlockHash)))
            {
                return Invalid($"Share builds on the wrong parent block ({actualPrevBlockHash}).");
            }

            byte[] coinbaseHash = BitcoinTransactionParser.ComputeTransactionIdHash(coinbaseBytes);
            byte[] expectedMerkleRoot = headerBytes.AsSpan(36, 32).ToArray();
            byte[] computedMerkleRoot = ComputeMerkleRoot(coinbaseHash, branchBytes);

            if (!expectedMerkleRoot.SequenceEqual(computedMerkleRoot))
            {
                List<byte[]> reversedBranches = branchBytes
                    .Select(branch => branch.Reverse().ToArray())
                    .ToList();
                byte[] alternateMerkleRoot = ComputeMerkleRoot(coinbaseHash, reversedBranches);
                if (!expectedMerkleRoot.SequenceEqual(alternateMerkleRoot))
                {
                    return Invalid("Coinbase transaction does not match the header merkle root.");
                }

                branchBytes = reversedBranches;
                normalizedMerklePath = reversedBranches
                    .Select(branch => Convert.ToHexString(branch).ToLowerInvariant())
                    .ToList();
            }

            List<BitcoinTransactionOutput> outputs = BitcoinTransactionParser.ParseOutputs(coinbaseBytes);
            if (outputs.Count == 0)
            {
                return Invalid("Coinbase payout list is too short.");
            }

            string normalizedMinerAddress = ExtractSlotZeroAddress(outputs[0].ScriptPubKey);
            string scriptPubKeyHex = Convert.ToHexString(outputs[0].ScriptPubKey).ToLowerInvariant();
            ValidatePayoutOutputs(outputs, expectedWinners);

            byte[] headerHash = DoubleSha256(headerBytes);
            string blockHash = BitcoinHashes.ToDisplayHashHex(headerHash);
            uint compactTarget = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(72, 4));
            BigInteger target = DecodeCompactTarget(compactTarget);
            if (target <= BigInteger.Zero)
            {
                return Invalid("Header target is invalid.");
            }

            BigInteger hashValue = ToPositiveBigInteger(headerHash);
            double difficulty = hashValue.IsZero ? double.MaxValue : (double)DifficultyOneTarget / (double)hashValue;
            bool isBlock = hashValue <= target;
            string shareId = ComputeShareId(normalizedHeaderHex, normalizedCoinbaseHex);
            string legacyShareId = ComputeLegacyShareId(normalizedHeaderHex, normalizedCoinbaseHex, normalizedMinerAddress);

            if (!string.IsNullOrWhiteSpace(expectedShareId) &&
                !string.Equals(BitcoinHashes.NormalizeHex(expectedShareId), shareId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(BitcoinHashes.NormalizeHex(expectedShareId), legacyShareId, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("Share identifier mismatch.");
            }

            return new BootShareValidationResult
            {
                IsValid = true,
                ShareId = shareId,
                MinerAddress = normalizedMinerAddress,
                Username = NormalizeUsernameForStorage(username, normalizedMinerAddress),
                ScriptPubKeyHex = scriptPubKeyHex,
                HeaderHex = normalizedHeaderHex,
                CoinbaseHex = normalizedCoinbaseHex,
                MerklePath = normalizedMerklePath,
                PrevBlockHash = actualPrevBlockHash,
                BlockHash = blockHash,
                Difficulty = difficulty,
                IsBlock = isBlock
            };
        }
        catch (FormatException)
        {
            return Invalid("Share payload is not valid hex.");
        }
        catch (Exception ex)
        {
            return Invalid(ex.Message);
        }
    }

    internal static string NormalizeUsernameForStorage(string? username, string fallbackAddress)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return fallbackAddress;
        }

        string trimmed = username.Trim();
        var safe = new StringBuilder(Math.Min(trimmed.Length, 128));
        foreach (char value in trimmed.Take(128))
        {
            safe.Append(char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-' or ':'
                ? value
                : '_');
        }

        string normalized = safe.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? fallbackAddress : normalized;
    }

    private string ExtractSlotZeroAddress(byte[] scriptPubKey)
    {
        string address = BitcoinScript.ScriptToAddress(scriptPubKey, _bitcoinNetwork);
        if (string.IsNullOrWhiteSpace(address) || string.Equals(address, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Slot 0 payout address is not a supported standard script.");
        }

        return BitcoinScript.NormalizeAddress(address);
    }

    private void ValidatePayoutOutputs(
        IReadOnlyList<BitcoinTransactionOutput> outputs,
        IReadOnlyList<PayoutInfo> expectedWinners)
    {
        if (outputs.Count == 0)
        {
            throw new InvalidOperationException("Coinbase payout list is too short.");
        }

        ExpectedWinnerOutputPlan expectedOutputPlan = GetExpectedWinnerOutputPlan(expectedWinners);
        List<ExpectedWinnerOutput> legacyOutputs = expectedOutputPlan.LegacyOutputs;
        List<ExpectedWinnerOutput> compressedOutputs = expectedOutputPlan.CompressedOutputs;
        IReadOnlyList<BitcoinTransactionOutput> winnerOutputs = outputs
            .Skip(1)
            .Where(IsPositivePayoutOutput)
            .ToList();

        if (MatchesWinnerOutputs(winnerOutputs, legacyOutputs) ||
            MatchesWinnerOutputs(winnerOutputs, compressedOutputs) ||
            MatchesAggregatedWinnerOutputs(outputs, compressedOutputs))
        {
            return;
        }

        if (LooksLikeSingleRecipientFallbackTemplate(outputs))
        {
            throw new InvalidOperationException(
                $"Coinbase appears to use a non-Boot single-recipient template (likely stale or solo fallback work). Expected {DescribeExpectedOutputs(compressedOutputs)}; actual {DescribeActualOutputs(outputs)}.");
        }

        int matchedTruncatedOutputs = CountTruncatedWinnerOutputPrefix(winnerOutputs, compressedOutputs);
        if (matchedTruncatedOutputs > 0)
        {
            throw new InvalidOperationException(
                $"Coinbase appears truncated by miner firmware/DATUM coinbase-size selection; matched {matchedTruncatedOutputs} of {compressedOutputs.Count} required GridPool payout outputs. Expected {DescribeExpectedOutputs(compressedOutputs)}; actual {DescribeActualOutputs(outputs)}.");
        }

        throw new InvalidOperationException(
            $"Coinbase winners payouts do not match the required Boot outputs. Expected {DescribeExpectedOutputs(compressedOutputs)}; actual {DescribeActualOutputs(outputs)}.");
    }

    private static byte[] ComputeMerkleRoot(byte[] coinbaseHash, IReadOnlyList<byte[]> merkleBranches)
    {
        byte[] current = coinbaseHash;
        foreach (byte[] branch in merkleBranches)
        {
            current = DoubleSha256(current.Concat(branch).ToArray());
        }

        return current;
    }

    private static string ComputeShareId(string headerHex, string coinbaseHex)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{headerHex}|{coinbaseHex}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeLegacyShareId(string headerHex, string coinbaseHex, string minerAddress)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{headerHex}|{coinbaseHex}|{minerAddress}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private ExpectedWinnerOutputPlan GetExpectedWinnerOutputPlan(IReadOnlyList<PayoutInfo> expectedWinners)
    {
        string cacheKey = BuildExpectedWinnerOutputPlanCacheKey(expectedWinners);
        lock (_expectedOutputPlanCacheLock)
        {
            if (_expectedOutputPlanCache.TryGetValue(cacheKey, out ExpectedWinnerOutputPlan? cached))
            {
                return cached;
            }
        }

        var plan = new ExpectedWinnerOutputPlan
        {
            LegacyOutputs = BuildLegacyWinnerOutputs(expectedWinners),
            CompressedOutputs = BuildCompressedWinnerOutputs(expectedWinners)
        };

        lock (_expectedOutputPlanCacheLock)
        {
            if (_expectedOutputPlanCache.Count >= MaxExpectedOutputPlanCacheEntries)
            {
                _expectedOutputPlanCache.Clear();
            }

            _expectedOutputPlanCache[cacheKey] = plan;
        }

        return plan;
    }

    private string BuildExpectedWinnerOutputPlanCacheKey(IReadOnlyList<PayoutInfo> expectedWinners)
    {
        var builder = new StringBuilder(_bitcoinNetwork.Length + expectedWinners.Count * 96);
        builder.Append(_bitcoinNetwork);
        foreach (PayoutInfo payout in expectedWinners)
        {
            builder
                .Append('|')
                .Append(payout.Value)
                .Append(':')
                .Append(BitcoinScript.NormalizeAddress(payout.Address));
        }

        return builder.ToString();
    }

    private List<ExpectedWinnerOutput> BuildLegacyWinnerOutputs(IReadOnlyList<PayoutInfo> expectedWinners)
    {
        return expectedWinners.Select(payout => new ExpectedWinnerOutput
        {
            Value = payout.Value,
            ScriptPubKey = BitcoinScript.AddressToScriptPubKey(BitcoinScript.NormalizeAddress(payout.Address), _bitcoinNetwork)
        }).ToList();
    }

    private List<ExpectedWinnerOutput> BuildCompressedWinnerOutputs(IReadOnlyList<PayoutInfo> expectedWinners)
    {
        var compressed = new List<ExpectedWinnerOutput>();
        var indexByScript = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var payout in expectedWinners)
        {
            byte[] script = BitcoinScript.AddressToScriptPubKey(BitcoinScript.NormalizeAddress(payout.Address), _bitcoinNetwork);
            string scriptKey = Convert.ToHexString(script).ToLowerInvariant();
            if (indexByScript.TryGetValue(scriptKey, out int existingIndex))
            {
                compressed[existingIndex].Value += payout.Value;
                continue;
            }

            indexByScript[scriptKey] = compressed.Count;
            compressed.Add(new ExpectedWinnerOutput
            {
                Value = payout.Value,
                ScriptPubKey = script
            });
        }

        return compressed;
    }

    private static bool MatchesWinnerOutputs(
        IReadOnlyList<BitcoinTransactionOutput> actualOutputs,
        IReadOnlyList<ExpectedWinnerOutput> expectedOutputs)
    {
        if (actualOutputs.Count < expectedOutputs.Count)
        {
            return false;
        }

        for (int index = 0; index < expectedOutputs.Count; index++)
        {
            if (actualOutputs[index].Value != expectedOutputs[index].Value)
            {
                return false;
            }

            if (!actualOutputs[index].ScriptPubKey.SequenceEqual(expectedOutputs[index].ScriptPubKey))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesAggregatedWinnerOutputs(
        IReadOnlyList<BitcoinTransactionOutput> actualOutputs,
        IReadOnlyList<ExpectedWinnerOutput> expectedOutputs)
    {
        Dictionary<string, ulong> actualTotals = AggregateActualOutputs(actualOutputs);
        Dictionary<string, ulong> expectedTotals = AggregateExpectedOutputs(expectedOutputs);
        bool sawSlotZeroResidual = false;

        foreach (string scriptHex in actualTotals.Keys.Union(expectedTotals.Keys, StringComparer.OrdinalIgnoreCase))
        {
            expectedTotals.TryGetValue(scriptHex, out ulong expectedValue);
            actualTotals.TryGetValue(scriptHex, out ulong actualValue);

            if (actualValue < expectedValue)
            {
                return false;
            }

            if (actualValue > expectedValue)
            {
                if (sawSlotZeroResidual)
                {
                    return false;
                }

                sawSlotZeroResidual = true;
            }
        }

        return sawSlotZeroResidual;
    }

    private static int CountTruncatedWinnerOutputPrefix(
        IReadOnlyList<BitcoinTransactionOutput> actualWinnerOutputs,
        IReadOnlyList<ExpectedWinnerOutput> expectedOutputs)
    {
        List<BitcoinTransactionOutput> positiveWinnerOutputs = actualWinnerOutputs
            .Where(output => output.Value > 0)
            .ToList();

        if (positiveWinnerOutputs.Count == 0 ||
            positiveWinnerOutputs.Count >= expectedOutputs.Count)
        {
            return 0;
        }

        for (int index = 0; index < positiveWinnerOutputs.Count; index++)
        {
            BitcoinTransactionOutput actual = positiveWinnerOutputs[index];
            ExpectedWinnerOutput expected = expectedOutputs[index];
            if (actual.Value != expected.Value ||
                !actual.ScriptPubKey.SequenceEqual(expected.ScriptPubKey))
            {
                return 0;
            }
        }

        return positiveWinnerOutputs.Count;
    }

    private static Dictionary<string, ulong> AggregateActualOutputs(IReadOnlyList<BitcoinTransactionOutput> outputs)
    {
        var totals = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (BitcoinTransactionOutput output in outputs.Where(IsPositivePayoutOutput))
        {
            string scriptHex = Convert.ToHexString(output.ScriptPubKey).ToLowerInvariant();
            if (totals.TryGetValue(scriptHex, out ulong existing))
            {
                totals[scriptHex] = existing + output.Value;
            }
            else
            {
                totals[scriptHex] = output.Value;
            }
        }

        return totals;
    }

    private static bool IsPositivePayoutOutput(BitcoinTransactionOutput output)
    {
        return output.Value > 0;
    }

    private static Dictionary<string, ulong> AggregateExpectedOutputs(IReadOnlyList<ExpectedWinnerOutput> outputs)
    {
        var totals = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (ExpectedWinnerOutput output in outputs)
        {
            string scriptHex = Convert.ToHexString(output.ScriptPubKey).ToLowerInvariant();
            if (totals.TryGetValue(scriptHex, out ulong existing))
            {
                totals[scriptHex] = existing + output.Value;
            }
            else
            {
                totals[scriptHex] = output.Value;
            }
        }

        return totals;
    }

    private static bool LooksLikeSingleRecipientFallbackTemplate(IReadOnlyList<BitcoinTransactionOutput> outputs)
    {
        List<BitcoinTransactionOutput> positiveOutputs = outputs
            .Where(output => output.Value > 0)
            .ToList();

        return positiveOutputs.Count == 1;
    }

    private static string DescribeExpectedOutputs(IReadOnlyList<ExpectedWinnerOutput> outputs)
    {
        return string.Join(
            ", ",
            AggregateExpectedOutputs(outputs)
                .OrderByDescending(item => item.Value)
                .Select(item => $"{ShortScript(item.Key)}:{item.Value}"));
    }

    private static string DescribeActualOutputs(IReadOnlyList<BitcoinTransactionOutput> outputs)
    {
        return string.Join(
            ", ",
            AggregateActualOutputs(outputs)
                .OrderByDescending(item => item.Value)
                .Select(item => $"{ShortScript(item.Key)}:{item.Value}"));
    }

    private static string ShortScript(string scriptHex)
    {
        if (string.IsNullOrWhiteSpace(scriptHex) || scriptHex.Length <= 12)
        {
            return scriptHex;
        }

        return $"{scriptHex[..6]}...{scriptHex[^6..]}";
    }

    private static byte[] DoubleSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(sha256.ComputeHash(data));
    }

    private static BigInteger ToPositiveBigInteger(byte[] littleEndianBytes)
    {
        byte[] buffer = new byte[littleEndianBytes.Length + 1];
        Array.Copy(littleEndianBytes, buffer, littleEndianBytes.Length);
        return new BigInteger(buffer);
    }

    private static BigInteger DecodeCompactTarget(uint compact)
    {
        int exponent = (int)(compact >> 24);
        uint mantissa = compact & 0x007fffff;
        bool isNegative = (compact & 0x00800000) != 0;

        if (mantissa == 0 || isNegative)
        {
            return BigInteger.Zero;
        }

        BigInteger value = mantissa;
        int shift = 8 * (exponent - 3);
        if (shift >= 0)
        {
            value <<= shift;
        }
        else
        {
            value >>= -shift;
        }

        return value;
    }

    private static BootShareValidationResult Invalid(string reason)
    {
        return new BootShareValidationResult
        {
            IsValid = false,
            RejectionReason = reason
        };
    }

    private static BootShareHeaderEvaluationResult InvalidHeader(string reason)
    {
        return new BootShareHeaderEvaluationResult
        {
            IsValid = false,
            RejectionReason = reason
        };
    }

    private sealed class ExpectedWinnerOutput
    {
        public ulong Value { get; set; }
        public byte[] ScriptPubKey { get; set; } = [];
    }

    private sealed class ExpectedWinnerOutputPlan
    {
        public List<ExpectedWinnerOutput> LegacyOutputs { get; set; } = [];
        public List<ExpectedWinnerOutput> CompressedOutputs { get; set; } = [];
    }
}
