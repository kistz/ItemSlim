using GBX.NET;

namespace ItemSlim;

/// <summary>
/// GBX.NET 2.5 appends a signature chunk (0xBB111200, skippable: its commit, the save time and a SHA-256) at the end of
/// every body it writes. The game doesn't know it and drops it when it re-saves a file, and the map review rejects
/// maps whose embedded items carry it. Removing it is lossless: the rest of the file stays byte for byte.
/// </summary>
public static class SignatureChunk
{
    static readonly byte[] Marker = [0x00, 0x12, 0x11, 0xBB, (byte)'P', (byte)'I', (byte)'K', (byte)'S'];   // chunk id + "SKIP"
    static readonly byte[] Facade = [0x01, 0xDE, 0xCA, 0xFA];                                                  // end of the body

    /// <summary>The Gbx without the signature chunk; the input itself when it has none.</summary>
    public static byte[] Strip(byte[] gbx)
    {
        if (gbx.Length < 9 || gbx[0] != 'G' || gbx[1] != 'B' || gbx[2] != 'X') return gbx;
        var compressed = gbx[7] == 'C';
        byte[] raw;
        if (compressed)
        {
            using var output = new MemoryStream();
            Gbx.Decompress(new MemoryStream(gbx), output);
            raw = output.ToArray();
        }
        else raw = gbx;

        var at = Find(raw);
        if (at < 0) return gbx;
        var stripped = new byte[at + Facade.Length];
        raw.AsSpan(0, at).CopyTo(stripped);
        Facade.CopyTo(stripped, at);
        if (!compressed) return stripped;

        stripped[7] = (byte)'U';
        using var packed = new MemoryStream();
        Gbx.Compress(new MemoryStream(stripped), packed);
        return packed.ToArray();
    }

    /// <summary>Start of a signature chunk that ends right before the final facade, or -1.</summary>
    static int Find(byte[] raw)
    {
        if (raw.Length < Marker.Length + 8 || !raw.AsSpan(raw.Length - 4).SequenceEqual(Facade)) return -1;
        var at = raw.AsSpan().LastIndexOf(Marker);
        if (at < 0 || at + Marker.Length + 4 > raw.Length) return -1;
        var size = BitConverter.ToInt32(raw, at + Marker.Length);
        return size >= 0 && at + Marker.Length + 4 + (long)size == raw.Length - 4 ? at : -1;
    }
}
