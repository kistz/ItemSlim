using System.IO.Compression;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace ItemSlim;

public enum Outcome { Optimized, Problem, Skipped }

public sealed record ItemResult(
    string RelPath, Outcome Outcome, string Detail,
    long FileBefore, long FileAfter, long EmbedBefore, long EmbedAfter,
    double PosErr, double UvErr, int Vertices, byte[]? Output);

/// <summary>
/// Rounds the vertex data of a Trackmania 2020 item (.Item.Gbx with a static mesh) so the file compresses better:
/// positions onto a metric grid, UVs onto a UV grid. Everything else (icon, materials, collision, settings) is kept.
/// The result is re-read from memory and verified before anything is written.
/// </summary>
public static class Slimmer
{
    static float Q(float v, double step) => step <= 0 ? v : (float)(Math.Round(v / step) * step);

    /// <summary>All vertex streams of the item's static mesh (every visual, every LOD). Throws for other item kinds.</summary>
    public static List<CPlugVertexStream> Streams(CGameItemModel item)
    {
        if (item.EntityModel is null && item.EntityModelEdition is not null)
            throw new NotSupportedException("Mesh Modeler item (no static mesh)");
        if (item.EntityModel is not CGameCommonItemEntityModel ent)
            throw new NotSupportedException(item.EntityModel is null ? "no entity model" : $"{item.EntityModel.GetType().Name} item, not a static mesh item");
        if (ent.StaticObject is not CPlugStaticObjectModel so || so.Mesh is not { } mesh)
            throw new NotSupportedException("no static mesh (Mesh Modeler / crystal item)");
        return mesh.Visuals!.OfType<CPlugVisualIndexedTriangles>()
            .SelectMany(v => v.VertexStreams!.OfType<CPlugVertexStream>()).ToList();
    }

    /// <summary>(file size, decompressed size, deflated decompressed size ~ size when embedded in a map)</summary>
    public static (long File, long Raw, long Embed) Measure(byte[] gbxBytes)
    {
        using var raw = new MemoryStream();
        using (var input = new MemoryStream(gbxBytes)) Gbx.Decompress(input, raw);
        using var deflated = new MemoryStream();
        using (var z = new DeflateStream(deflated, CompressionLevel.Optimal, true)) z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        return (gbxBytes.Length, raw.Length, deflated.Length);
    }

    public static ItemResult Process(string relPath, byte[] original, double posStep, double uvStep)
    {
        var before = Measure(original);
        try
        {
            var gbx = Gbx.Parse<CGameItemModel>(new MemoryStream(original));
            var streams = Streams(gbx.Node);
            var webp = gbx.Node.IconWebP;
            var raw = gbx.Node.Icon;
            foreach (var s in streams)
            {
                if (posStep > 0 && s.Positions is { } p)
                    for (var i = 0; i < p.Length; i++) p[i] = new Vec3(Q(p[i].X, posStep), Q(p[i].Y, posStep), Q(p[i].Z, posStep));
                if (uvStep > 0 && s.UVs is { } uvs)
                    foreach (var arr in uvs.Values)
                        for (var i = 0; i < arr.Length; i++) arr[i] = new Vec2(Q(arr[i].X, uvStep), Q(arr[i].Y, uvStep));
            }
            using var ms = new MemoryStream();
            gbx.Save(ms);
            var output = ms.ToArray();
            var after = Measure(output);

            // verify: re-read the written bytes and compare with the original vertex by vertex
            var now = Streams(Gbx.ParseNode<CGameItemModel>(new MemoryStream(output)));
            var orig = Streams(Gbx.ParseNode<CGameItemModel>(new MemoryStream(original)));
            var head = Gbx.ParseHeaderNode<CGameItemModel>(new MemoryStream(output));
            double pe = 0, ue = 0; var offGrid = 0; var verts = 0;
            var problems = new List<string>();
            if (now.Count != orig.Count) problems.Add("stream count changed");
            for (var k = 0; k < Math.Min(now.Count, orig.Count); k++)
            {
                var pn = now[k].Positions ?? []; var po = orig[k].Positions ?? [];
                if (pn.Length != po.Length) { problems.Add("vertex count changed"); continue; }
                verts += pn.Length;
                for (var i = 0; i < pn.Length; i++)
                    foreach (var (x, y) in new[] { (pn[i].X, po[i].X), (pn[i].Y, po[i].Y), (pn[i].Z, po[i].Z) })
                    {
                        pe = Math.Max(pe, Math.Abs(x - y));
                        if (posStep > 0 && Math.Abs(x / posStep - Math.Round(x / posStep)) > 1e-3) offGrid++;
                    }
                if (now[k].UVs is { } un && orig[k].UVs is { } uo)
                    foreach (var (key, arr) in un)
                        if (uo.TryGetValue(key, out var oa) && oa.Length == arr.Length)
                            for (var i = 0; i < arr.Length; i++)
                                ue = Math.Max(ue, Math.Max(Math.Abs(arr[i].X - oa[i].X), Math.Abs(arr[i].Y - oa[i].Y)));
            }
            if (offGrid > 0) problems.Add($"{offGrid} coordinates off the grid");
            if (posStep > 0 && pe > posStep / 2 + 1e-6) problems.Add($"position error {pe * 1000:F2} mm");
            if (uvStep > 0 && ue > uvStep / 2 + 1e-7) problems.Add($"uv error {ue:E1}");
            var iconKept = webp is not null ? head.IconWebP is { } w && w.AsSpan().SequenceEqual(webp)
                         : raw is not null ? head.Icon is { } hi && hi.Length == raw.Length
                                             && hi.Cast<GBX.NET.Color>().SequenceEqual(raw.Cast<GBX.NET.Color>())
                         : head.Icon is null && head.IconWebP is null;          // had no icon, still has none
            if (!iconKept) problems.Add("icon changed");

            if (problems.Count == 0 && pe == 0 && ue == 0)      // already on the grid: nothing to gain, keep the file as is
                return new ItemResult(relPath, Outcome.Optimized, "already slim", before.File, before.File, before.Embed, before.Embed, 0, 0, verts, null);
            return problems.Count == 0
                ? new ItemResult(relPath, Outcome.Optimized, "", before.File, after.File, before.Embed, after.Embed, pe, ue, verts, output)
                : new ItemResult(relPath, Outcome.Problem, string.Join("; ", problems), before.File, before.File, before.Embed, before.Embed, pe, ue, verts, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidCastException or NullReferenceException or InvalidDataException or EndOfStreamException or NotImplementedException)
        {
            return new ItemResult(relPath, Outcome.Skipped, ex.Message.ReplaceLineEndings(" "), before.File, before.File, before.Embed, before.Embed, 0, 0, 0, null);
        }
        catch (Exception ex)
        {
            return new ItemResult(relPath, Outcome.Skipped, $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}", before.File, before.File, before.Embed, before.Embed, 0, 0, 0, null);
        }
    }
}
