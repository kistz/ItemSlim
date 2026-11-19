using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using ItemSlim;

Gbx.LZO = new Lzo();
Console.OutputEncoding = System.Text.Encoding.UTF8;
var inv = CultureInfo.InvariantCulture;

const string Usage = """
ItemSlim - makes Trackmania 2020 items (.Item.Gbx) and the items embedded in maps smaller without visible change.

Rounds vertex positions to a 1/1024 m grid (max 0.49 mm shift) and UVs to a 1/16384 grid, which lets the files
compress much better (typically -20 to -25 % file size, -15 % when embedded in maps). Icons, materials and item
settings are untouched; when the visible mesh is also the collision mesh, collision gets the same sub-millimetre
rounding. Every item is verified in memory before it is written.
The input is never changed.

Usage:
  ItemSlim <item, map or folder> [more ...] [options]

Each input folder is written as a complete copy to <folder>_optimized next to it, with the items optimized and every
other file copied as it is (a single item goes to <name>_optimized.Item.Gbx). Rename it yourself to replace the
original. If the _optimized output already exists, ItemSlim stops: delete or rename it first.
A map (.Map.Gbx) gets its embedded items optimized and is saved as <name>_optimized.Map.Gbx; everything else in
the map stays as it is.

Options:
  --dry-run           only measure what would be gained, write no items
  --quiet             no per-item lines, only the summary

A Markdown report (summary table, skipped and failed items, every item) is always written next to the first input,
as <name>_ItemSlim.md.
Items that are not static mesh items (Mesh Modeler items, prefabs, variant lists) are skipped and copied as they are.
Exit code: 0 = ok, 1 = some items failed verification (copied unchanged), 2 = bad arguments.
""";
const double PosStep = 1.0 / 1024, UvStep = 1.0 / 16384;     // 0.98 mm position grid, 1/16384 UV grid

var inputs = new List<string>();
bool dryRun = false, quiet = false;
try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-h": case "--help": case "/?": Console.WriteLine(Usage); return 0;
            case "--dry-run": dryRun = true; break;
            case "--quiet": quiet = true; break;
            default:
                if (args[i].StartsWith("--")) throw new ArgumentException($"unknown option {args[i]}");
                inputs.Add(Path.GetFullPath(args[i])); break;
        }
    }
    if (inputs.Count == 0) { Console.WriteLine(Usage); return 2; }
    foreach (var p in inputs) if (!File.Exists(p) && !Directory.Exists(p)) throw new ArgumentException($"not found: {p}");
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"error: {ex.Message}\n\n{Usage}");
    return 2;
}

// report: next to the first input
var first = inputs[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var firstName = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileName(first).Split('.')[0];
var reportPath = Path.Combine(Path.GetDirectoryName(first)!, $"{firstName}_ItemSlim.md");

// work list: (load the item bytes, path shown in the report, report group, store the result)
var work = new List<(Func<byte[]> Load, string Rel, string Group, Action<byte[], ItemResult> Store)>();
var maps = new List<MapJob>();
foreach (var input in inputs)
{
    if (File.Exists(input) && input.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase))
    {
        // a map: every embedded .Item.Gbx in its zip is a work item; the results go back into the zip later
        var job = new MapJob(input, Gbx.Parse<CGameCtnChallenge>(input));
        if (job.Gbx.Node.EmbeddedZipData is { Length: > 0 })
            using (var zip = job.Gbx.Node.OpenReadEmbeddedZipData())
                foreach (var e in zip.Entries)
                {
                    using var es = e.Open(); using var ms = new MemoryStream(); es.CopyTo(ms);
                    job.Entries.Add((e.FullName, e.LastWriteTime, ms.ToArray(), e.CompressedLength));
                }
        maps.Add(job);
        var mapName = Path.GetFileName(input);
        var itemEntries = job.Entries.Where(x => x.Name.EndsWith(".Item.Gbx", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (name, _, bytes, _) in itemEntries)
        {
            var norm = name.Replace('\\', '/');
            var cut = norm.IndexOf("/Items/", StringComparison.OrdinalIgnoreCase);
            var shown = mapName + ": " + (cut >= 0 ? norm[(cut + 7)..] : norm);
            work.Add((() => bytes, shown, mapName, (b, r) => { job.NewBytes[name] = MapJob.UncompressedBody(b); job.Outcomes[name] = r.Outcome; }));
        }
        Console.WriteLine($"Map {mapName}: {itemEntries.Count} embedded items ({job.Entries.Count} embedded files)");
        continue;
    }
    if (File.Exists(input))
    {
        var dest = OutPath(input);
        work.Add((() => File.ReadAllBytes(input), Path.GetFileName(input), "(files)", (b, _) => { if (!dryRun) File.WriteAllBytes(dest, b); }));
        continue;
    }
    var root = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    foreach (var f in Directory.EnumerateFiles(root, "*.Item.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).Order(StringComparer.OrdinalIgnoreCase))
    {
        var rel = Path.GetRelativePath(root, f);
        var sep = rel.IndexOfAny(['\\', '/']);
        // one input folder: a row per top-level subfolder; several inputs: a row per input folder
        var group = inputs.Count > 1 ? Path.GetFileName(root) : sep < 0 ? "(top level)" : rel[..sep];
        var dest = Path.Combine(OutPath(input), rel);
        work.Add((() => File.ReadAllBytes(f), inputs.Count > 1 ? Path.Combine(Path.GetFileName(root), rel) : rel, group,
                  (b, _) => { if (dryRun) return; Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.WriteAllBytes(dest, b); }));
    }
}
if (work.Count == 0) { Console.WriteLine("no items found (no .Item.Gbx files and no items embedded in the maps)"); return 0; }

var mapProblems = 0;
var mode = dryRun ? "dry run (no items written)" : "writing <name>_optimized, input untouched";
Console.WriteLine($"ItemSlim: {work.Count} items, {mode}, {Environment.ProcessorCount} threads");

// outputs must not exist yet: never mix a new run with old files
if (!dryRun)
    foreach (var input in inputs)
        if (Directory.Exists(OutPath(input)) || File.Exists(OutPath(input)))
        {
            Console.Error.WriteLine($"error: {OutPath(input)} already exists - delete or rename it first");
            return 2;
        }
// non-item files (and anything else in the folder) are copied unchanged so the output is a complete copy
if (!dryRun)
    foreach (var input in inputs.Where(Directory.Exists))
    {
        var src = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(OutPath(input));
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(OutPath(input), Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            if (!f.EndsWith(".Item.Gbx", StringComparison.OrdinalIgnoreCase))
                File.Copy(f, Path.Combine(OutPath(input), Path.GetRelativePath(src, f)));
    }

var results = new ItemResult[work.Count];
var done = 0;
var gate = new object();
var sw = System.Diagnostics.Stopwatch.StartNew();
Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
{
    var (load, rel, _, store) = work[i];
    ItemResult r;
    try
    {
        var bytes = load();
        r = Slimmer.Process(rel, bytes, PosStep, UvStep);
        store(r.Output ?? bytes, r);                                      // skipped / already slim / failed: copied as they are
    }
    catch (Exception ex)
    {
        r = new ItemResult(rel, Outcome.Skipped, $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}", 0, 0, 0, 0, 0, 0, 0, null);
    }
    results[i] = r with { Output = null };
    lock (gate)
    {
        done++;
        if (!quiet)
            Console.WriteLine(r.Outcome switch
            {
                Outcome.Optimized when r.Detail != "" => $"OK      {rel}: {r.Detail}",
                Outcome.Optimized => $"OK      {rel}: {r.FileBefore / 1024.0:F0} -> {r.FileAfter / 1024.0:F0} KB, embedded {r.EmbedBefore / 1024.0:F0} -> {r.EmbedAfter / 1024.0:F0} KB, max shift {r.PosErr * 1000:F2} mm",
                Outcome.Problem => $"PROBLEM {rel}: {r.Detail} (copied unchanged)",
                _ => $"SKIP    {rel}: {r.Detail}",
            });
        else if (done % 1000 == 0) Console.WriteLine($"  {done}/{work.Count} ...");
    }
});

// maps: put the optimized items back into the embedded zip (same order, names and dates), build the new map in
// memory, verify it, then write it (not on a dry run)
var mapLines = new List<string>();
var mapTables = new List<(string Title, List<string> Table, List<string> Notes)>();
foreach (var job in maps)
{
    var mapFile = Path.GetFileName(job.Path);
    var before = new FileInfo(job.Path).Length;
    if (job.Entries.Count == 0) { mapLines.Add($"Map {mapFile}: no embedded items, nothing to do"); continue; }
    var expected = job.Entries.Select(e => (e.Name, Bytes: job.NewBytes.TryGetValue(e.Name, out var nb) ? nb : e.Bytes)).ToList();
    // only the entries that changed are replaced; every other entry keeps its original compressed bytes and the
    // order stays (so the map's embedded-item list keeps its order too)
    var changed = job.Entries.Where(e => job.NewBytes.TryGetValue(e.Name, out var nb) && !nb.AsSpan().SequenceEqual(e.Bytes))
                             .ToDictionary(e => e.Name, e => job.NewBytes[e.Name]);
    if (changed.Count > 0)
    {
        try
        {
            job.Gbx.Node.EmbeddedZipData = ZipRewrite.Rewrite(job.Gbx.Node.EmbeddedZipData!, changed);
        }
        catch (NotSupportedException)
        {
            // unusual zip (zip64 ...): let .NET rewrite it; changed entries move to the end of the archive
            job.Gbx.Node.UpdateEmbeddedZipData(zip =>
            {
                foreach (var (name, bytes) in changed)
                {
                    var old = zip.GetEntry(name)!; var date = old.LastWriteTime; old.Delete();
                    var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                    entry.LastWriteTime = date;
                    using var es = entry.Open();
                    es.Write(bytes);
                }
            });
        }
    }
    using var newMap = new MemoryStream();
    job.Gbx.Save(newMap);
    var after = newMap.Length;

    // verify from the saved bytes: every embedded file exactly as intended
    var check = Gbx.Parse<CGameCtnChallenge>(new MemoryStream(newMap.ToArray()));
    Dictionary<string, (byte[] Bytes, long Packed)> got;
    bool sameOrder;
    using (var chk = check.Node.OpenReadEmbeddedZipData())
    {
        got = chk.Entries.ToDictionary(e => e.FullName, e => { using var s2 = e.Open(); using var m2 = new MemoryStream(); s2.CopyTo(m2); return (m2.ToArray(), e.CompressedLength); });
        sameOrder = chk.Entries.Select(e => e.FullName).SequenceEqual(job.Entries.Select(e => e.Name));
    }
    var bad = expected.Count(x => !got.TryGetValue(x.Name, out var g) || !g.Bytes.AsSpan().SequenceEqual(x.Bytes)) + Math.Max(0, got.Count - expected.Count);
    if (bad > 0)
    {
        mapLines.Add($"Map {mapFile}: PROBLEM - {bad} embedded files did not read back correctly, map not saved");
        mapProblems++;
        continue;
    }
    var outMap = OutPath(job.Path);
    if (!dryRun) File.WriteAllBytes(outMap, newMap.ToArray());

    // composition: where the bytes are, before and after
    string K(long b) => (b / 1024.0).ToString("F0", inv);
    string P(long a, long b) => a == 0 ? "–" : (100.0 * (b - a) / a).ToString("F0", inv).Replace("-", "−") + "%";
    var isItem = (string n) => n.EndsWith(".Item.Gbx", StringComparison.OrdinalIgnoreCase);
    var parts = new (string Label, Func<string, bool> Match)[]
    {
        ("Regular items (optimized)", n => isItem(n) && job.Outcomes.TryGetValue(n, out var o) && o == Outcome.Optimized),
        ("Mesh Modeler / other items (can't be optimized)", n => isItem(n) && !(job.Outcomes.TryGetValue(n, out var o) && o == Outcome.Optimized)),
        ("Other embedded files", n => !isItem(n)),
    };
    // map data measured exactly: the same map saved without its embedded files (ItemSlim never changes it)
    var mapData = -1L;
    try
    {
        var bare = Gbx.Parse<CGameCtnChallenge>(new MemoryStream(newMap.ToArray()));
        bare.Node.EmbeddedZipData = [];
        using var bareBytes = new MemoryStream(); bare.Save(bareBytes);
        mapData = bareBytes.Length;
    }
    catch { }
    var crows = new List<string[]> { new[] { "Part", "Count", "Before KB", "After KB", "Change" } };
    if (mapData >= 0)
        crows.Add(["Embedded items and files", job.Entries.Count.ToString(), K(before - mapData), K(after - mapData), P(before - mapData, after - mapData)]);
    foreach (var (label, match) in parts)
    {
        var es = job.Entries.Where(e => match(e.Name)).ToList();
        if (es.Count == 0) continue;
        long b0 = es.Sum(e => e.Packed), b1 = es.Sum(e => got[e.Name].Packed);
        crows.Add([(mapData >= 0 ? "↳ " : "") + label, es.Count.ToString(), K(b0), K(b1), P(b0, b1)]);
    }
    if (mapData >= 0)
        crows.Add(["Map data (blocks, placed items, lightmap, thumbnail, ...)", "", K(mapData), K(mapData), "0%"]);
    crows.Add(["**Map file**", "", K(before), K(after), P(before, after)]);
    // what is in the map data that ItemSlim leaves alone
    var lightmap = 0L;
    try
    {
        var noLm = Gbx.Parse<CGameCtnChallenge>(new MemoryStream(newMap.ToArray()));
        noLm.Node.HasLightmaps = false; noLm.Node.LightmapCache = null; noLm.Node.LightmapFrames = null; noLm.Node.LightmapCacheData = null;
        using var noLmBytes = new MemoryStream(); noLm.Save(noLmBytes);
        lightmap = Math.Max(0, after - noLmBytes.Length);
    }
    catch { }
    var notes = new List<string>
    {
        $"Lightmap (baked shadows) in the map data: {K(lightmap)} KB, {P(after, after - lightmap).TrimStart('−')} of the map. Kept: removing it changes the lighting until the map is recalculated in the editor.",
        $"Thumbnail: {K(check.Node.Thumbnail?.Length ?? 0)} KB, kept.",
        "The ↳ rows are the compressed sizes inside the map's embedded archive; the map file compresses that archive once more, so they add up to the embedded total only approximately.",
    };
    mapTables.Add(($"Map {mapFile}", MdTable(crows), notes));
    mapLines.Add($"Map {mapFile}: {before / 1024.0:F0} KB → {after / 1024.0:F0} KB ({P(before, after)}), {(dryRun ? "dry run, not saved" : $"saved as {Path.GetFileName(outMap)}")}{(sameOrder ? "" : " (embedded file order changed)")}");
}

// summary
var useKb = results.Where(r => r.Outcome == Outcome.Optimized).Sum(r => r.FileBefore) < 10L << 20;
var unit = useKb ? "KB" : "MB";
string Mb(long b)
{
    var t = useKb ? (b / 1024.0).ToString("F0", inv) : (b / 1048576.0).ToString("F1", inv);
    return t is "-0" or "-0.0" ? t[1..] : t;                               // a few bytes more is still "0", not "-0.0"
}
string Pct(long a, long b) => a == 0 ? "–" : (100.0 * (b - a) / a).ToString("F0", inv).Replace("-", "−") + "%";
var opt = results.Where(r => r.Outcome == Outcome.Optimized).ToList();
var groups = work.Select((w, i) => (w.Group, R: results[i])).GroupBy(x => x.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
var rows = new List<string[]> { new[] { "Folder", "Items", "Skipped", $"Files {unit}", "Files %", $"Embedded {unit}", "Embedded %" } };
foreach (var g in groups)
{
    var o = g.Where(x => x.R.Outcome == Outcome.Optimized).Select(x => x.R).ToList();
    long f0 = o.Sum(r => r.FileBefore), f1 = o.Sum(r => r.FileAfter), e0 = o.Sum(r => r.EmbedBefore), e1 = o.Sum(r => r.EmbedAfter);
    rows.Add([g.Key, o.Count.ToString(), g.Count(x => x.R.Outcome != Outcome.Optimized).ToString(),
              o.Count > 0 ? $"{Mb(f0)} → {Mb(f1)}" : "–", Pct(f0, f1), o.Count > 0 ? $"{Mb(e0)} → {Mb(e1)}" : "–", Pct(e0, e1)]);
}
long F0 = opt.Sum(r => r.FileBefore), F1 = opt.Sum(r => r.FileAfter), E0 = opt.Sum(r => r.EmbedBefore), E1 = opt.Sum(r => r.EmbedAfter);
var skipped = results.Count(r => r.Outcome == Outcome.Skipped);
var problems = results.Count(r => r.Outcome == Outcome.Problem) + mapProblems;
rows.Add(["**Total**", opt.Count.ToString(), (skipped + problems).ToString(), $"{Mb(F0)} → {Mb(F1)}", Pct(F0, F1), $"{Mb(E0)} → {Mb(E1)}", Pct(E0, E1)]);
if (groups.Count == 1) rows.RemoveRange(1, rows.Count - 2);              // single folder: only the total row
var table = MdTable(rows);
var already = opt.Count(r => r.Detail == "already slim");
var countsLine = $"{opt.Count - already} optimized, {already} already slim, {skipped} skipped (not static mesh items), {problems} failed verification (copied unchanged).";
var savedLine = $"Saved {Mb(F0 - F1)} {unit} item file size ({Pct(F0, F1)}), {Mb(E0 - E1)} {unit} embedded ({Pct(E0, E1)}), max vertex shift {opt.DefaultIfEmpty().Max(r => r?.PosErr ?? 0) * 1000:F2} mm, {sw.Elapsed.TotalSeconds:F0} s.";
Console.WriteLine();
table.ForEach(Console.WriteLine);
Console.WriteLine();
Console.WriteLine(countsLine);
Console.WriteLine(savedLine);
mapLines.ForEach(Console.WriteLine);
foreach (var (title, t, notes) in mapTables)
{
    Console.WriteLine();
    Console.WriteLine(title);
    t.ForEach(Console.WriteLine);
    notes.ForEach(Console.WriteLine);
}
if (dryRun) Console.WriteLine("Dry run: no items were written.");

// Markdown report (always)
{
    string Kb(long b) => (b / 1024.0).ToString("F1", inv);
    string Cell(string t) => t.Replace("|", "\\|");
    var md = new List<string>
    {
        "# ItemSlim report", "",
        $"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}",
        $"- Input: {string.Join(", ", inputs.Select(x => $"`{x}`"))}",
        $"- Mode: {mode}",
        $"- Output: {(dryRun ? "none (dry run)" : string.Join(", ", inputs.Select(x => $"`{OutPath(x)}`")))}",
        $"- Grid: positions {PosStep * 1000:0.####} mm, UVs {UvStep:0.##########}",
        "", "## Summary", "",
    };
    md.AddRange(table);
    md.AddRange(["", countsLine, "", savedLine]);
    foreach (var l in mapLines) md.AddRange(["", l]);
    foreach (var (title, t, notes) in mapTables)
    {
        md.AddRange(["", $"## {title}", ""]);
        md.AddRange(t);
        md.Add("");
        foreach (var n in notes) md.Add($"- {n}");
    }
    if (dryRun) md.AddRange(["", "Dry run: no items were written."]);
    var failed = results.Where(r => r.Outcome == Outcome.Problem).ToList();
    if (failed.Count > 0)
    {
        md.AddRange(["", $"## Failed verification ({failed.Count}, copied unchanged)", ""]);
        md.AddRange(MdTable([["Item", "Reason"], .. failed.Select(r => new[] { Cell(r.RelPath), Cell(r.Detail) })]));
    }
    var skip = results.Where(r => r.Outcome == Outcome.Skipped).ToList();
    if (skip.Count > 0)
    {
        md.AddRange(["", $"## Skipped ({skip.Count}, copied unchanged)", ""]);
        md.AddRange(MdTable([["Reason", "Items"], .. skip.GroupBy(r => r.Detail).OrderByDescending(g => g.Count())
            .Select(g => new[] { Cell(g.Key), g.Count().ToString() })]));
        md.Add("");
        md.AddRange(MdTable([["Item", "Reason"], .. skip.Select(r => new[] { Cell(r.RelPath), Cell(r.Detail) })]));
    }
    md.AddRange(["", "## Items", ""]);
    md.AddRange(MdTable([["Item", "Files KB", "Files %", "Embedded KB", "Embedded %", "Max shift mm", "Vertices"],
        .. opt.Select(r => new[] { Cell(r.RelPath) + (r.Detail == "already slim" ? " (already slim)" : ""),
            $"{Kb(r.FileBefore)} → {Kb(r.FileAfter)}", Pct(r.FileBefore, r.FileAfter),
            $"{Kb(r.EmbedBefore)} → {Kb(r.EmbedAfter)}", Pct(r.EmbedBefore, r.EmbedAfter),
            (r.PosErr * 1000).ToString("F2", inv), r.Vertices.ToString() })]));
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllLines(reportPath, md);
    Console.WriteLine($"Report: {reportPath}");
}
return problems > 0 ? 1 : 0;


// <folder>_optimized next to a folder, <name>_optimized.Item.Gbx / .Map.Gbx next to a single item or map
static string OutPath(string input)
{
    var p = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (Directory.Exists(p)) return p + "_optimized";
    var name = Path.GetFileName(p);
    foreach (var ext in new[] { ".Item.Gbx", ".Map.Gbx" })
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetDirectoryName(p)!, name[..^ext.Length] + "_optimized" + name[^ext.Length..]);
    return p + "_optimized";
}

// Markdown table with padded columns: first column left-aligned, the rest right-aligned
static List<string> MdTable(List<string[]> rows)
{
    var w = Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
    var lines = new List<string>();
    for (var ri = 0; ri < rows.Count; ri++)
    {
        lines.Add("| " + string.Join(" | ", rows[ri].Select((c, k) => k == 0 ? c.PadRight(w[k]) : c.PadLeft(w[k]))) + " |");
        if (ri == 0) lines.Add("|" + string.Join("|", w.Select((x, k) => k == 0 ? new string('-', x + 2) : new string('-', x + 1) + ":")) + "|");
    }
    return lines;
}
