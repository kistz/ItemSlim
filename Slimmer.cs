using System.IO.Compression;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace ItemSlim;

public enum Outcome { Optimized, Problem, Skipped }

public sealed record ItemResult(
    string RelPath, Outcome Outcome, string Detail,
    long FileBefore, long FileAfter, long EmbedBefore, long EmbedAfter,
    double PosErr, double UvErr, int Vertices, int Protected, byte[]? Output);

/// <summary>
/// Rounds the vertex data of a Trackmania 2020 item (.Item.Gbx with a static mesh) so the file compresses better:
/// positions onto a metric grid, UVs onto a UV grid. Everything else (icon, materials, collision, settings) is kept.
/// The result is re-read from memory and verified before anything is written.
/// </summary>
public static class Slimmer
{
    static float Q(float v, double step) => step <= 0 ? v : (float)(Math.Round(v / step) * step);

    /// <summary>The item's static mesh. Throws for other item kinds.</summary>
    public static CPlugSolid2Model Mesh(CGameItemModel item)
    {
        if (item.EntityModel is null && item.EntityModelEdition is not null)
            throw new NotSupportedException("Mesh Modeler item (no static mesh)");
        if (item.EntityModel is not CGameCommonItemEntityModel ent)
            throw new NotSupportedException(item.EntityModel is null ? "no entity model" : $"{item.EntityModel.GetType().Name} item, not a static mesh item");
        if (ent.StaticObject is not CPlugStaticObjectModel so || so.Mesh is not { } mesh)
            throw new NotSupportedException("no static mesh (Mesh Modeler / crystal item)");
        return mesh;
    }

    /// <summary>All vertex streams of the item's static mesh (every visual, every LOD). Throws for other item kinds.</summary>
    public static List<CPlugVertexStream> Streams(CGameItemModel item) => Streams(Mesh(item)).Select(x => x.Stream).ToList();

    static IEnumerable<(int Visual, CPlugVertexStream Stream)> Streams(CPlugSolid2Model mesh) =>
        mesh.Visuals!.Select((v, i) => (v, i)).Where(x => x.v is CPlugVisualIndexedTriangles)
            .SelectMany(x => ((CPlugVisualIndexedTriangles)x.v!).VertexStreams!.OfType<CPlugVertexStream>().Select(s => (x.i, s)));

    // ---- driving-edge protection ------------------------------------------------------------------------------
    // Where a road meets the next block the driving surfaces must line up exactly, so the vertices on the outer
    // edges of the driving surface keep their original position. An edge is protected when:
    //  1. it is an edge of an upward-facing triangle (normal.y >= 0.3: facing up, flatter than ~73 degrees),
    //  2. both its vertices lie in the same block-boundary plane: x or z within 10 mm of a multiple of 16 m,
    //  3. all upward triangles that use this edge (matched by position, across all materials and visuals) lie on
    //     ONE side of that plane - an edge with driving surface on both sides is an interior line, not a border.
    public const double BlockSize = 16, BoundaryTolerance = 0.01, UpNormal = 0.3;

    /// <summary>Protected vertex indices per stream (same order as <see cref="Streams(CGameItemModel)"/>) and the protected edges.</summary>
    public sealed record Protection(HashSet<int>[] PerStream, List<(int Visual, Vec3 A, Vec3 B)> Edges)
    {
        public int Count => PerStream.Sum(x => x.Count);
    }

    public static Protection DrivingEdges(CPlugSolid2Model mesh)
    {
        static bool OnBoundary(double c) => Math.Abs(c - Math.Round(c / BlockSize) * BlockSize) < BoundaryTolerance;
        static IEnumerable<(char Axis, long N)> Planes(Vec3 q)     // a vertex can lie on two planes (block corner)
        {
            if (OnBoundary(q.X)) yield return ('x', (long)Math.Round(q.X / BlockSize));
            if (OnBoundary(q.Z)) yield return ('z', (long)Math.Round(q.Z / BlockSize));
        }
        static (long, long, long) Key(Vec3 q) => ((long)Math.Round(q.X * 1e4), (long)Math.Round(q.Y * 1e4), (long)Math.Round(q.Z * 1e4));

        var streams = Streams(mesh).ToList();
        var sides = new Dictionary<((long, long, long), (long, long, long), char, long), HashSet<int>>();
        var uses = new Dictionary<((long, long, long), (long, long, long), char, long), List<(int Visual, int I0, int I1)>>();
        var visualPos = new Dictionary<int, Vec3[]>();
        for (var vi = 0; vi < mesh.Visuals!.Count; vi++)
        {
            if (mesh.Visuals[vi] is not CPlugVisualIndexedTriangles vis) continue;
            var pos = streams.FirstOrDefault(s => s.Visual == vi && s.Stream.Positions is { Length: > 0 }).Stream?.Positions;
            var idx = vis.IndexBuffer?.Indices;
            if (pos is null || idx is null) continue;
            visualPos[vi] = pos;
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                if (idx[t] >= pos.Length || idx[t + 1] >= pos.Length || idx[t + 2] >= pos.Length) continue;
                Vec3 a = pos[idx[t]], b = pos[idx[t + 1]], c = pos[idx[t + 2]];
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, wx = c.X - a.X, wy = c.Y - a.Y, wz = c.Z - a.Z;
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                // counter-clockwise = front (the glTF convention, checked on Blendermania items): +y is up, so
                // bottoms and overhang undersides are not driving surfaces
                if (len < 1e-12 || ny / len < UpNormal) continue;
                for (var k = 0; k < 3; k++)
                {
                    int i0 = idx[t + k], i1 = idx[t + (k + 1) % 3], i2 = idx[t + (k + 2) % 3];
                    foreach (var (axis, n) in Planes(pos[i0]).Intersect(Planes(pos[i1])))
                    {
                        var off = (axis == 'x' ? pos[i2].X : pos[i2].Z) - n * BlockSize;
                        var side = off > BoundaryTolerance ? 1 : off < -BoundaryTolerance ? -1 : 0;
                        var (k0, k1) = (Key(pos[i0]), Key(pos[i1]));
                        var key = k0.CompareTo(k1) <= 0 ? (k0, k1, axis, n) : (k1, k0, axis, n);
                        if (!sides.TryGetValue(key, out var ss)) { sides[key] = ss = []; uses[key] = []; }
                        ss.Add(side);
                        uses[key].Add((vi, i0, i1));
                    }
                }
            }
        }
        var perVisual = new Dictionary<int, HashSet<int>>();
        var edges = new List<(int, Vec3, Vec3)>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var (key, ss) in sides)
        {
            if (ss.Count(x => x != 0) > 1) continue;                       // driving surface on both sides: interior line
            foreach (var (vi, i0, i1) in uses[key])
            {
                if (!perVisual.TryGetValue(vi, out var set)) perVisual[vi] = set = [];
                set.Add(i0); set.Add(i1);
                if (seen.Add((vi, Math.Min(i0, i1), Math.Max(i0, i1)))) edges.Add((vi, visualPos[vi][i0], visualPos[vi][i1]));
            }
        }
        // the same vertex indices in every stream of that visual (one stream per visual in practice)
        var perStream = streams.Select(s => perVisual.TryGetValue(s.Visual, out var set) && s.Stream.Positions is { } p
            ? set.Where(i => i < p.Length).ToHashSet() : []).ToArray();
        return new Protection(perStream, edges);
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
            var keep = DrivingEdges(Mesh(gbx.Node)).PerStream;             // driving-surface edges stay exact
            var webp = gbx.Node.IconWebP;
            var raw = gbx.Node.Icon;
            for (var k = 0; k < streams.Count; k++)
            {
                var s = streams[k];
                if (posStep > 0 && s.Positions is { } p)
                    for (var i = 0; i < p.Length; i++)
                        if (!keep[k].Contains(i)) p[i] = new Vec3(Q(p[i].X, posStep), Q(p[i].Y, posStep), Q(p[i].Z, posStep));
                if (uvStep > 0 && s.UVs is { } uvs)
                    foreach (var arr in uvs.Values)
                        for (var i = 0; i < arr.Length; i++) arr[i] = new Vec2(Q(arr[i].X, uvStep), Q(arr[i].Y, uvStep));
            }
            using var ms = new MemoryStream();
            gbx.Save(ms);
            var output = SignatureChunk.Strip(ms.ToArray());               // as the game writes it (see SignatureChunk)
            var after = Measure(output);

            // verify: re-read the written bytes and compare with the original vertex by vertex
            var now = Streams(Gbx.ParseNode<CGameItemModel>(new MemoryStream(output)));
            var orig = Streams(Gbx.ParseNode<CGameItemModel>(new MemoryStream(original)));
            var head = Gbx.ParseHeaderNode<CGameItemModel>(new MemoryStream(output));
            double pe = 0, ue = 0; var offGrid = 0; var verts = 0; var moved = 0;
            var problems = new List<string>();
            if (now.Count != orig.Count) problems.Add("stream count changed");
            for (var k = 0; k < Math.Min(now.Count, orig.Count); k++)
            {
                var pn = now[k].Positions ?? []; var po = orig[k].Positions ?? [];
                if (pn.Length != po.Length) { problems.Add("vertex count changed"); continue; }
                verts += pn.Length;
                for (var i = 0; i < pn.Length; i++)
                {
                    if (keep[k].Contains(i))
                    {
                        if (!pn[i].X.Equals(po[i].X) || !pn[i].Y.Equals(po[i].Y) || !pn[i].Z.Equals(po[i].Z)) moved++;
                        continue;                                           // protected: must be bit-identical
                    }
                    foreach (var (x, y) in new[] { (pn[i].X, po[i].X), (pn[i].Y, po[i].Y), (pn[i].Z, po[i].Z) })
                    {
                        pe = Math.Max(pe, Math.Abs(x - y));
                        if (posStep > 0 && Math.Abs(x / posStep - Math.Round(x / posStep)) > 1e-3) offGrid++;
                    }
                }
                if (now[k].UVs is { } un && orig[k].UVs is { } uo)
                    foreach (var (key, arr) in un)
                        if (uo.TryGetValue(key, out var oa) && oa.Length == arr.Length)
                            for (var i = 0; i < arr.Length; i++)
                                ue = Math.Max(ue, Math.Max(Math.Abs(arr[i].X - oa[i].X), Math.Abs(arr[i].Y - oa[i].Y)));
            }
            if (moved > 0) problems.Add($"{moved} protected driving-edge vertices moved");
            if (offGrid > 0) problems.Add($"{offGrid} coordinates off the grid");
            if (posStep > 0 && pe > posStep / 2 + 1e-6) problems.Add($"position error {pe * 1000:F2} mm");
            if (uvStep > 0 && ue > uvStep / 2 + 1e-7) problems.Add($"uv error {ue:E1}");
            var iconKept = webp is not null ? head.IconWebP is { } w && w.AsSpan().SequenceEqual(webp)
                         : raw is not null ? head.Icon is { } hi && hi.Length == raw.Length
                                             && hi.Cast<GBX.NET.Color>().SequenceEqual(raw.Cast<GBX.NET.Color>())
                         : head.Icon is null && head.IconWebP is null;          // had no icon, still has none
            if (!iconKept) problems.Add("icon changed");

            var prot = keep.Sum(x => x.Count);
            if (problems.Count == 0 && pe == 0 && ue == 0)      // already on the grid: nothing to gain, keep the file as is
            {
                var clean = SignatureChunk.Strip(original);    // except a GBX.NET signature (from an earlier ItemSlim)
                return ReferenceEquals(clean, original)
                    ? new ItemResult(relPath, Outcome.Optimized, "already slim", before.File, before.File, before.Embed, before.Embed, 0, 0, verts, prot, null)
                    : new ItemResult(relPath, Outcome.Optimized, "already slim, signature removed", before.File, clean.Length, before.Embed, Measure(clean).Embed, 0, 0, verts, prot, clean);
            }
            return problems.Count == 0
                ? new ItemResult(relPath, Outcome.Optimized, "", before.File, after.File, before.Embed, after.Embed, pe, ue, verts, prot, output)
                : new ItemResult(relPath, Outcome.Problem, string.Join("; ", problems), before.File, before.File, before.Embed, before.Embed, pe, ue, verts, prot, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidCastException or NullReferenceException or InvalidDataException or EndOfStreamException or NotImplementedException)
        {
            return new ItemResult(relPath, Outcome.Skipped, ex.Message.ReplaceLineEndings(" "), before.File, before.File, before.Embed, before.Embed, 0, 0, 0, 0, null);
        }
        catch (Exception ex)
        {
            return new ItemResult(relPath, Outcome.Skipped, $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}", before.File, before.File, before.Embed, before.Embed, 0, 0, 0, 0, null);
        }
    }
}
