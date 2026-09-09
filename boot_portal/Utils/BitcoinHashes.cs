using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace boot_portal.Utils;

public static class BitcoinHashes
{
    private static readonly BigInteger BitcoinPowLimit = DecodeCompactTarget(0x1d00ffff);
    private static readonly BigInteger RegtestPowLimit = DecodeCompactTarget(0x207fffff);
    public static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return string.Empty;
        }

        string normalized = hex.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        return normalized.Replace(" ", string.Empty).ToLowerInvariant();
    }

    public static string ToDisplayHashHex(byte[] hashBytes)
    {
        return Convert.ToHexString(hashBytes.Reverse().ToArray()).ToLowerInvariant();
    }

    public static string ToLikelyDisplayHashHex(byte[] hashBytes)
    {
        string raw = Convert.ToHexString(hashBytes).ToLowerInvariant();
        string reversed = ToDisplayHashHex(hashBytes);
        return CountLeadingZeroNibbles(raw) > CountLeadingZeroNibbles(reversed)
            ? raw
            : reversed;
    }

    public static string ReverseHexByteOrder(string hex)
    {
        string normalized = NormalizeHex(hex);
        if (normalized.Length % 2 != 0)
        {
            return normalized;
        }

        char[] reversed = new char[normalized.Length];
        int targetIndex = 0;
        for (int sourceIndex = normalized.Length - 2; sourceIndex >= 0; sourceIndex -= 2)
        {
            reversed[targetIndex++] = normalized[sourceIndex];
            reversed[targetIndex++] = normalized[sourceIndex + 1];
        }

        return new string(reversed);
    }

    public static string NormalizeLikelyDisplayHashHex(string? hex)
    {
        string normalized = NormalizeHex(hex);
        if (normalized.Length != 64)
        {
            return normalized;
        }

        string reversed = ReverseHexByteOrder(normalized);
        return CountLeadingZeroNibbles(normalized) > CountLeadingZeroNibbles(reversed)
            ? normalized
            : reversed;
    }

    private static int CountLeadingZeroNibbles(string hex)
    {
        int count = 0;
        foreach (char c in NormalizeHex(hex))
        {
            if (c != '0')
            {
                break;
            }

            count++;
        }

        return count;
    }

    public static bool AreEquivalent(string? first, string? second)
    {
        string left = NormalizeHex(first);
        string right = NormalizeHex(second);

        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return left.Length == 64 &&
               right.Length == 64 &&
               string.Equals(ReverseHexByteOrder(left), right, StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeBlockHashFromHeader(string? headerHex)
    {
        string normalized = NormalizeHex(headerHex);
        if (normalized.Length != 160)
        {
            throw new ArgumentException("Bitcoin block header must be exactly 80 bytes.", nameof(headerHex));
        }

        byte[] header = Convert.FromHexString(normalized);
        byte[] first = SHA256.HashData(header);
        byte[] second = SHA256.HashData(first);
        return ToDisplayHashHex(second);
    }

    public static BitcoinHeaderEvaluation EvaluateHeader(
        string? headerHex,
        DateTime receivedUtc,
        string? bitcoinNetwork = BitcoinScript.Mainnet)
    {
        try
        {
            string normalized = NormalizeHex(headerHex);
            if (normalized.Length != 160)
            {
                return BitcoinHeaderEvaluation.Invalid("Bitcoin block header must be exactly 80 bytes.");
            }

            byte[] header = Convert.FromHexString(normalized);
            byte[] first = SHA256.HashData(header);
            byte[] second = SHA256.HashData(first);
            string blockHash = ToDisplayHashHex(second);
            string parentHash = ToDisplayHashHex(header.AsSpan(4, 32).ToArray());
            uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68, 4));
            uint compactTarget = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72, 4));
            BigInteger target = DecodeCompactTarget(compactTarget);
            BigInteger proofOfWorkLimit = BitcoinScript.NormalizeNetwork(bitcoinNetwork) == BitcoinScript.Regtest
                ? RegtestPowLimit
                : BitcoinPowLimit;
            if (target <= BigInteger.Zero || target > proofOfWorkLimit)
            {
                return BitcoinHeaderEvaluation.Invalid("Header target is outside the Bitcoin proof-of-work limit.");
            }

            BigInteger hashValue = new(second, isUnsigned: true, isBigEndian: false);
            if (hashValue > target)
            {
                return BitcoinHeaderEvaluation.Invalid("Header does not satisfy its encoded proof-of-work target.");
            }

            return new BitcoinHeaderEvaluation
            {
                IsValid = true,
                HeaderHex = normalized,
                BlockHash = blockHash,
                ParentBlockHash = parentHash,
                CompactTarget = compactTarget,
                HeaderTimeUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime,
                ReceivedUtc = receivedUtc
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return BitcoinHeaderEvaluation.Invalid(ex.Message);
        }
    }

    public static BigInteger DecodeCompactTarget(uint compact)
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
        return shift >= 0 ? value << shift : value >> -shift;
    }
}

public sealed class BitcoinHeaderEvaluation
{
    public bool IsValid { get; init; }
    public string RejectionReason { get; init; } = string.Empty;
    public string HeaderHex { get; init; } = string.Empty;
    public string BlockHash { get; init; } = string.Empty;
    public string ParentBlockHash { get; init; } = string.Empty;
    public uint CompactTarget { get; init; }
    public DateTime HeaderTimeUtc { get; init; }
    public DateTime ReceivedUtc { get; init; }

    public static BitcoinHeaderEvaluation Invalid(string reason) => new()
    {
        RejectionReason = reason
    };
}
