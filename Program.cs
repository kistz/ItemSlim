using System.Globalization;
using GBX.NET;
using GBX.NET.LZO;
using ItemSlim;

Gbx.LZO = new Lzo();
Console.OutputEncoding = System.Text.Encoding.UTF8;
var inv = CultureInfo.InvariantCulture;

const string Usage = """
ItemSlim - makes Trackmania 2020 items (.Item.Gbx) smaller without visible change.

Rounds vertex positions to a 1/1024 m grid (max 0.49 mm shift) and UVs to a 1/16384 grid, which lets the files
compress much better (typically -20 to -25 % file size, -15 % when embedded in maps). Icons, materials and item
settings are untouched; when the visible mesh is also the collision mesh, collision gets the same sub-millimetre
rounding. Every item is verified in memory before it is written.
The input is never changed.

Usage:
  ItemSlim <file or folder> [more ...] [options]

Each input folder is written as a complete copy to <folder>_optimized next to it, with the items optimized and every
other file copied as it is (a single item goes to <name>_optimized.Item.Gbx). Rename it yourself to replace the
original. If the _optimized output already exists, ItemSlim stops: delete or rename it first.

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

// work list: (source file, path shown in the report, report group, output file)
var work = new List<(string Src, string Rel, string Group, string Dest)>();
foreach (var input in inputs)
{
    if (File.Exists(input))
    {
        work.Add((input, Path.GetFileName(input), "(files)", OutPath(input)));
        continue;
    }
    var root = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    foreach (var f in Directory.EnumerateFiles(root, "*.Item.Gbx", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).Order(StringComparer.OrdinalIgnoreCase))
    {
        var rel = Path.GetRelativePath(root, f);
        var sep = rel.IndexOfAny(['\\', '/']);
        // one input folder: a row per top-level subfolder; several inputs: a row per input folder
        var group = inputs.Count > 1 ? Path.GetFileName(root) : sep < 0 ? "(top level)" : rel[..sep];
        work.Add((f, inputs.Count > 1 ? Path.Combine(Path.GetFileName(root), rel) : rel, group, Path.Combine(OutPath(input), rel)));
    }
}
if (work.Count == 0) { Console.WriteLine("no .Item.Gbx files found"); return 0; }

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
    var (src, rel, _, dest) = work[i];
    ItemResult r;
    try
    {
        var bytes = File.ReadAllBytes(src);
        r = Slimmer.Process(rel, bytes, PosStep, UvStep);
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, r.Output ?? bytes);                  // skipped / already slim / failed: copied as they are
        }
    }
    catch (Exception ex)
    {
        var len = new FileInfo(src).Length;
        r = new ItemResult(rel, Outcome.Skipped, $"{ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}", len, len, 0, 0, 0, 0, 0, null);
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

// summary
var useKb = results.Where(r => r.Outcome == Outcome.Optimized).Sum(r => r.FileBefore) < 10L << 20;
var unit = useKb ? "KB" : "MB";
string Mb(long b) => useKb ? (b / 1024.0).ToString("F0", inv) : (b / 1048576.0).ToString("F1", inv);
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
var problems = results.Count(r => r.Outcome == Outcome.Problem);
rows.Add(["**Total**", opt.Count.ToString(), (skipped + problems).ToString(), $"{Mb(F0)} → {Mb(F1)}", Pct(F0, F1), $"{Mb(E0)} → {Mb(E1)}", Pct(E0, E1)]);
if (groups.Count == 1) rows.RemoveRange(1, rows.Count - 2);              // single folder: only the total row
var table = MdTable(rows);
var already = opt.Count(r => r.Detail == "already slim");
var countsLine = $"{opt.Count - already} optimized, {already} already slim, {skipped} skipped (not static mesh items), {problems} failed verification (copied unchanged).";
var savedLine = $"Saved {Mb(F0 - F1)} {unit} on disk ({Pct(F0, F1)}), {Mb(E0 - E1)} {unit} embedded ({Pct(E0, E1)}), max vertex shift {opt.DefaultIfEmpty().Max(r => r?.PosErr ?? 0) * 1000:F2} mm, {sw.Elapsed.TotalSeconds:F0} s.";
Console.WriteLine();
table.ForEach(Console.WriteLine);
Console.WriteLine();
Console.WriteLine(countsLine);
Console.WriteLine(savedLine);
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


// <folder>_optimized next to a folder, <name>_optimized.Item.Gbx next to a single item
static string OutPath(string input)
{
    var p = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (Directory.Exists(p)) return p + "_optimized";
    var name = Path.GetFileName(p);
    var cut = name.IndexOf(".Item.Gbx", StringComparison.OrdinalIgnoreCase);
    return Path.Combine(Path.GetDirectoryName(p)!, cut > 0 ? name[..cut] + "_optimized" + name[cut..] : name + "_optimized");
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
