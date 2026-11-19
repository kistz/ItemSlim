using System.Collections.Concurrent;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.GameData;

namespace ItemSlim;

/// <summary>
/// A map being processed: the parsed map, its embedded files in zip order (with their compressed size in the zip),
/// the new item bytes and the outcome per entry name.
/// </summary>
public sealed record MapJob(string Path, Gbx<CGameCtnChallenge> Gbx)
{
    public List<(string Name, DateTimeOffset Date, byte[] Bytes, long Packed)> Entries { get; } = [];
    public ConcurrentDictionary<string, byte[]> NewBytes { get; } = new();
    public ConcurrentDictionary<string, Outcome> Outcomes { get; } = new();

    /// <summary>
    /// The game stores embedded items with an uncompressed body and lets the map's zip compress them. An item whose
    /// body is LZO-compressed hardly compresses in the zip, so it is re-stored uncompressed (lossless). Returns the
    /// input unchanged if it is already uncompressed or the converted item does not read back.
    /// </summary>
    public static byte[] UncompressedBody(byte[] item)
    {
        if (item.Length < 9 || item[0] != 'G' || item[1] != 'B' || item[2] != 'X' || item[7] != 'C') return item;
        try
        {
            using var output = new MemoryStream();
            GBX.NET.Gbx.Decompress(new MemoryStream(item), output);
            var bytes = output.ToArray();
            bytes[7] = (byte)'U';
            GBX.NET.Gbx.ParseNode<CGameItemModel>(new MemoryStream(bytes));      // must still read as an item
            return bytes;
        }
        catch
        {
            return item;
        }
    }
}
