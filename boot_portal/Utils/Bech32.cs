namespace boot_portal.Utils;

public static class Bech32
{
    private static readonly string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly uint[] Generator = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };

    public static (string hrp, int version, byte[] program) Decode(string address)
    {
        int sepIndex = address.LastIndexOf('1');
        if (sepIndex < 1) throw new FormatException("Invalid Bech32 address: no separator");
        string hrp = address.Substring(0, sepIndex).ToLower();
        if (hrp != "bc" && hrp != "tb" && hrp != "bcrt") throw new FormatException($"Invalid HRP: {hrp}");

        string dataPart = address.Substring(sepIndex + 1);
        if (dataPart.Length < 6) throw new FormatException("Bech32 data too short");

        byte[] data = new byte[dataPart.Length];
        for (int i = 0; i < dataPart.Length; i++)
        {
            int index = Charset.IndexOf(dataPart[i]);
            if (index == -1) throw new FormatException($"Invalid character in Bech32 address: {dataPart[i]}");
            data[i] = (byte)index;
        }

        uint checksum = Polymod(ExpandHrp(hrp).Concat(data).ToArray());
    
        int version = data[0]; // Get version from the first data byte (5-bit)
        if (version > 16) throw new FormatException($"Invalid witness version: {version}");

        // Determine the expected checksum constant based on the witness version
        uint expectedChecksum;
        if (version == 0)
        {
            // Bech32 for Witness Version 0 (P2WPKH, P2WSH)
            expectedChecksum = 1;
        }
        else
        {
            // Bech32m for Witness Version 1 to 16 (P2TR and future)
            // This is the constant 0x2bc830a3
            expectedChecksum = 0x2bc830a3; 
        }
        
        if (checksum != expectedChecksum) throw new FormatException("Invalid Bech32/Bech32m checksum");

        //uint checksum = Polymod(ExpandHrp(hrp).Concat(data).ToArray());
        //if (checksum != 1) throw new FormatException("Invalid Bech32 checksum");

        //int version = data[0];
        //if (version > 16) throw new FormatException($"Invalid witness version: {version}");
        
        byte[] program5bit = data.Skip(1).Take(data.Length - 7).ToArray();
        byte[] program = ConvertBits(program5bit, 5, 8, false);

        // Taproot addresses (V1) have a 32-byte program length (V0 can be 20 or 32)
        if (program.Length < 2 || program.Length > 40) throw new FormatException($"Invalid program length: {program.Length}");
        
        // The V0 specific check needs to be updated to allow V1 (32 bytes)
        if (version == 0 && program.Length != 20 && program.Length != 32) 
            throw new FormatException("Invalid program length for version 0");
        
        // Add V1 check for completeness (V1 must be 32 bytes)
        if (version == 1 && program.Length != 32)
            throw new FormatException("Invalid program length for version 1 (Taproot)");

        if(version == 1)
        {
            //Console.WriteLine($"Decoding: {address}");
            //Console.WriteLine($"  into..: {BitConverter.ToString(program)}");
        }
        

        return (hrp, version, program);
    }

    public static string Encode(string hrp, int version, byte[] program)
    {
        if (hrp != "bc" && hrp != "tb" && hrp != "bcrt") throw new ArgumentException($"Invalid HRP: {hrp}");
        if (version < 0 || version > 16) throw new ArgumentException($"Invalid witness version: {version}");
        if (program.Length < 2 || program.Length > 40) throw new ArgumentException($"Invalid program length: {program.Length}");
        
        // Validate program length for known versions
        if (version == 0 && program.Length != 20 && program.Length != 32) 
            throw new ArgumentException("Invalid program length for version 0");
        if (version == 1 && program.Length != 32) // Taproot must be 32 bytes
            throw new ArgumentException("Invalid program length for version 1 (Taproot)");

        // Convert program to 5-bit
        byte[] data = ConvertBits(program, 8, 5, true);
        byte[] values = new byte[data.Length + 1];
        values[0] = (byte)version; // The first 5-bit value is the witness version
        Array.Copy(data, 0, values, 1, data.Length);

        // --- START FIX: Determine Checksum Constant ---
        uint checksumConstant;
        if (version == 0)
        {
            // Bech32 constant for Witness Version 0 (P2WPKH, P2WSH)
            checksumConstant = 1;
        }
        else
        {
            // Bech32m constant for Witness Version 1 to 16 (P2TR and future)
            checksumConstant = 0x2bc830a3;
        }
        // --- END FIX ---

        // Compute checksum
        byte[] expandedHrp = ExpandHrp(hrp);
        byte[] checksum = new byte[6];
        
        // XOR with the appropriate constant (1 for Bech32, 0x2bc830a3 for Bech32m)
        uint polymod = Polymod(expandedHrp.Concat(values).Concat(new byte[6]).ToArray()) ^ checksumConstant;
        
        for (int i = 0; i < 6; i++)
        {
            checksum[i] = (byte)((polymod >> (5 * (5 - i))) & 31);
        }

        // Combine HRP, version, data, and checksum
        var chars = new List<char>(hrp.Length + 1 + values.Length + checksum.Length);
        chars.AddRange(hrp);
        chars.Add('1');
        
        // Note: The original code had chars.Add(Charset[version]) which is incorrect.
        // The version is already included as the first byte in 'values' (values[0]).
        // The loop below correctly adds the character representation of values[i], starting with the version.
        
        foreach (byte b in values)
        {
            chars.Add(Charset[b]);
        }
        foreach (byte b in checksum)
        {
            chars.Add(Charset[b]);
        }
        if(version == 1)
        {
            Console.WriteLine($"Encoding: {hrp}");
            Console.WriteLine($"  into..: {new string(chars.ToArray())}");
        }

        return new string(chars.ToArray());
    }
    private static uint Polymod(byte[] values)
    {
        uint chk = 1;
        foreach (byte v in values)
        {
            uint top = chk >> 25;
            chk = (chk & 0x1ffffff) << 5 ^ v;
            for (int i = 0; i < 5; i++)
            {
                if ((top >> i & 1) != 0)
                    chk ^= Generator[i];
            }
        }
        return chk;
    }

    private static byte[] ExpandHrp(string hrp)
    {
        byte[] ret = new byte[hrp.Length * 2 + 1];
        for (int i = 0; i < hrp.Length; i++)
        {
            ret[i] = (byte)(hrp[i] >> 5);
            ret[i + hrp.Length + 1] = (byte)(hrp[i] & 31);
        }
        return ret;
    }

    private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
    {
        int acc = 0;
        int bits = 0;
        var ret = new List<byte>();
        int maxv = (1 << toBits) - 1;
        int max_acc = (1 << (fromBits + toBits - 1)) - 1;
        foreach (byte value in data)
        {
            if (value < 0 || (value >> fromBits) != 0) throw new FormatException("Invalid Bech32 data");
            acc = ((acc << fromBits) | value) & max_acc;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                ret.Add((byte)((acc >> bits) & maxv));
            }
        }
        if (pad && bits > 0)
        {
            ret.Add((byte)((acc << (toBits - bits)) & maxv));
        }
        else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
        {
            throw new FormatException("Invalid Bech32 padding");
        }
        return ret.ToArray();
    }
}
