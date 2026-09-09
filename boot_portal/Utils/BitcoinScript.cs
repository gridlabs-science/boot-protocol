using boot_portal.Utils;

namespace boot_portal.Utils;

public static class BitcoinScript
{
    public const string Mainnet = "mainnet";
    public const string Testnet4 = "testnet4";
    public const string Regtest = "regtest";

    public static string NormalizeNetwork(string? network)
    {
        string value = (network ?? Mainnet).Trim().ToLowerInvariant();
        return value switch
        {
            "" or "main" or "mainnet" => Mainnet,
            "test" or "testnet" or "testnet3" or "testnet4" => Testnet4,
            "regtest" => Regtest,
            _ => throw new InvalidOperationException($"Unsupported bitcoin_network '{network}'. Expected mainnet, testnet4, or regtest.")
        };
    }

    public static string NormalizeAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        int dotIndex = address.IndexOf('.');
        return dotIndex >= 0 ? address[..dotIndex] : address;
    }

    public static bool TryAddressToScriptPubKey(string address, out byte[] scriptPubKey)
    {
        return TryAddressToScriptPubKey(address, Mainnet, out scriptPubKey);
    }

    public static bool TryAddressToScriptPubKey(string address, string? network, out byte[] scriptPubKey)
    {
        try
        {
            scriptPubKey = AddressToScriptPubKey(address, network);
            return true;
        }
        catch
        {
            scriptPubKey = [];
            return false;
        }
    }

    public static byte[] AddressToScriptPubKey(string address)
    {
        return AddressToScriptPubKey(address, Mainnet);
    }

    public static byte[] AddressToScriptPubKey(string address, string? network)
    {
        string normalizedNetwork = NormalizeNetwork(network);
        string normalized = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Address is required.");
        }

        if (normalized.StartsWith("bc1", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("tb1", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("bcrt1", StringComparison.OrdinalIgnoreCase))
        {
            var (hrp, version, program) = Bech32.Decode(normalized);
            string expectedHrp = GetBech32Hrp(normalizedNetwork);
            if (!string.Equals(hrp, expectedHrp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Address {address} is not valid for bitcoin_network {normalizedNetwork}.");
            }

            byte witnessVersionOpCode = version switch
            {
                0 => (byte)0x00,
                >= 1 and <= 16 => (byte)(0x50 + version),
                _ => throw new InvalidOperationException($"Unsupported witness version {version} for address {address}.")
            };

            byte[] script = new byte[2 + program.Length];
            script[0] = witnessVersionOpCode;
            script[1] = (byte)program.Length;
            Array.Copy(program, 0, script, 2, program.Length);
            return script;
        }

        byte[] payload = Base58Check.Decode(normalized);
        if (payload.Length != 21)
        {
            throw new InvalidOperationException($"Unsupported Base58 payload length {payload.Length} for address {address}.");
        }

        return payload[0] switch
        {
            0x00 when normalizedNetwork == Mainnet => BuildP2PkhScript(payload),
            0x05 when normalizedNetwork == Mainnet => BuildP2ShScript(payload),
            0x6f when normalizedNetwork != Mainnet => BuildP2PkhScript(payload),
            0xc4 when normalizedNetwork != Mainnet => BuildP2ShScript(payload),
            _ => throw new InvalidOperationException($"Unsupported Base58 version 0x{payload[0]:X2} for address {address}.")
        };
    }

    public static string AddressToScriptPubKeyHex(string address)
    {
        return AddressToScriptPubKeyHex(address, Mainnet);
    }

    public static string AddressToScriptPubKeyHex(string address, string? network)
    {
        return Convert.ToHexString(AddressToScriptPubKey(address, network)).ToLowerInvariant();
    }

    public static string ScriptToAddress(byte[] script)
    {
        return ScriptToAddress(script, Mainnet);
    }

    public static string ScriptToAddress(byte[] script, string? network)
    {
        string normalizedNetwork = NormalizeNetwork(network);
        if (script.Length == 25 &&
            script[0] == 0x76 &&
            script[1] == 0xA9 &&
            script[2] == 0x14 &&
            script[23] == 0x88 &&
            script[24] == 0xAC)
        {
            byte[] payload = new byte[21];
            payload[0] = normalizedNetwork == Mainnet ? (byte)0x00 : (byte)0x6f;
            Array.Copy(script, 3, payload, 1, 20);
            return Base58Check.Encode(payload);
        }

        if (script.Length == 23 &&
            script[0] == 0xA9 &&
            script[1] == 0x14 &&
            script[22] == 0x87)
        {
            byte[] payload = new byte[21];
            payload[0] = normalizedNetwork == Mainnet ? (byte)0x05 : (byte)0xc4;
            Array.Copy(script, 2, payload, 1, 20);
            return Base58Check.Encode(payload);
        }

        if (script.Length == 22 && script[0] == 0x00 && script[1] == 0x14)
        {
            byte[] program = script.Skip(2).Take(20).ToArray();
            return Bech32.Encode(GetBech32Hrp(normalizedNetwork), 0, program);
        }

        if (script.Length == 34 && script[0] == 0x00 && script[1] == 0x20)
        {
            byte[] program = script.Skip(2).Take(32).ToArray();
            return Bech32.Encode(GetBech32Hrp(normalizedNetwork), 0, program);
        }

        if (script.Length == 34 && script[0] == 0x51 && script[1] == 0x20)
        {
            byte[] program = script.Skip(2).Take(32).ToArray();
            return Bech32.Encode(GetBech32Hrp(normalizedNetwork), 1, program);
        }

        return "UNKNOWN";
    }

    private static string GetBech32Hrp(string normalizedNetwork) => normalizedNetwork switch
    {
        Mainnet => "bc",
        Testnet4 => "tb",
        Regtest => "bcrt",
        _ => throw new InvalidOperationException($"Unsupported bitcoin network '{normalizedNetwork}'.")
    };

    private static byte[] BuildP2PkhScript(byte[] payload)
    {
        byte[] script = new byte[25];
        script[0] = 0x76;
        script[1] = 0xA9;
        script[2] = 0x14;
        Array.Copy(payload, 1, script, 3, 20);
        script[23] = 0x88;
        script[24] = 0xAC;
        return script;
    }

    private static byte[] BuildP2ShScript(byte[] payload)
    {
        byte[] script = new byte[23];
        script[0] = 0xA9;
        script[1] = 0x14;
        Array.Copy(payload, 1, script, 2, 20);
        script[22] = 0x87;
        return script;
    }
}
