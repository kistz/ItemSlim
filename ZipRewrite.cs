using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ItemSlim;

/// <summary>
/// Rewrites a zip archive (a map's embedded items) with some entries replaced, keeping the original entry order,
/// names, dates and attributes. Unchanged entries are copied with their original compressed bytes; replaced entries
/// are deflated at the highest level. Zip64, encrypted and multi-disk archives are not supported (throws).
/// </summary>
public static class ZipRewrite
{
    public static byte[] Rewrite(byte[] zip, IReadOnlyDictionary<string, byte[]> replace)
    {
        var span = zip.AsSpan();
        var eocd = -1;
        for (var i = zip.Length - 22; i >= Math.Max(0, zip.Length - 22 - 65535); i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[i..]) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new NotSupportedException("zip end record not found");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 10)..]);
        var cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[(eocd + 16)..]);
        var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 20)..]);
        if (count == 0xFFFF || cdOffset == 0xFFFFFFFF || BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 4)..]) != 0)
            throw new NotSupportedException("zip64 / multi-disk archive");

        using var outData = new MemoryStream();
        using var central = new MemoryStream();
        var pos = (int)cdOffset;
        Span<byte> lh = stackalloc byte[30];          // local header, reused per entry
        Span<byte> ch = stackalloc byte[46];          // central directory header, reused per entry
        for (var n = 0; n < count; n++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]) != 0x02014b50) throw new NotSupportedException("bad central directory");
            var madeBy = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 4)..]);
            var needed = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 6)..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 8)..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 10)..]);
            var time = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 12)..]);
            var date = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 14)..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 16)..]);
            var csize = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 20)..]);
            var usize = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 24)..]);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 28)..]);
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 30)..]);
            var cmtLen = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 32)..]);
            var intAttr = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 36)..]);
            var extAttr = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 38)..]);
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 42)..]);
            var nameBytes = span.Slice(pos + 46, nameLen).ToArray();
            var extra = span.Slice(pos + 46 + nameLen, extraLen).ToArray();
            var comment = span.Slice(pos + 46 + nameLen + extraLen, cmtLen).ToArray();
            pos += 46 + nameLen + extraLen + cmtLen;
            if ((flags & 1) != 0) throw new NotSupportedException("encrypted entry");
            if (csize == 0xFFFFFFFF || usize == 0xFFFFFFFF || local == -1) throw new NotSupportedException("zip64 entry");

            var name = Encoding.UTF8.GetString(nameBytes);           // same decoding as .NET's ZipArchive (names are ASCII in practice)
            byte[] data;
            if (replace.TryGetValue(name, out var newBytes))
            {
                using var ms = new MemoryStream();
                using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize, true)) z.Write(newBytes);
                data = ms.ToArray();
                method = 8; crc = Crc32(newBytes); csize = (uint)data.Length; usize = (uint)newBytes.Length;
                needed = Math.Max(needed, (ushort)20);
            }
            else
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(span[local..]) != 0x04034b50) throw new NotSupportedException("bad local header");
                var lName = BinaryPrimitives.ReadUInt16LittleEndian(span[(local + 26)..]);
                var lExtra = BinaryPrimitives.ReadUInt16LittleEndian(span[(local + 28)..]);
                data = span.Slice(local + 30 + lName + lExtra, (int)csize).ToArray();      // original compressed bytes
            }
            flags = (ushort)(flags & ~0x0008);           // sizes are written in the local header, no data descriptor

            var offset = (uint)outData.Position;
            lh.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(lh, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[4..], needed);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[6..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[8..], method);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[10..], time);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[12..], date);
            BinaryPrimitives.WriteUInt32LittleEndian(lh[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(lh[18..], csize);
            BinaryPrimitives.WriteUInt32LittleEndian(lh[22..], usize);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[26..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(lh[28..], 0);
            outData.Write(lh); outData.Write(nameBytes); outData.Write(data);

            ch.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(ch, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[4..], madeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[6..], needed);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[8..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[10..], method);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[12..], time);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[14..], date);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[20..], csize);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[24..], usize);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[28..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[30..], (ushort)extra.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[32..], (ushort)comment.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(ch[36..], intAttr);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[38..], extAttr);
            BinaryPrimitives.WriteUInt32LittleEndian(ch[42..], offset);
            central.Write(ch); central.Write(nameBytes); central.Write(extra); central.Write(comment);
        }
        var cdStart = (uint)outData.Position;
        central.Position = 0; central.CopyTo(outData);
        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], cdStart);
        BinaryPrimitives.WriteUInt16LittleEndian(end[20..], commentLen);
        outData.Write(end);
        outData.Write(span.Slice(eocd + 22, commentLen));
        return outData.ToArray();
    }

    static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
