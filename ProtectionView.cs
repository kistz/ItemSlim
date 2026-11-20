using System.Diagnostics;
using System.Globalization;
using System.Text;
using GBX.NET;
using GBX.NET.Engines.GameData;
using GBX.NET.Engines.Plug;

namespace ItemSlim;

/// <summary>
/// --show-protection: renders every item with its protected driving-surface edges drawn in red, using Blender in the
/// background (Workbench renderer, no add-ons). ItemSlim writes one small scene file per item (the highest-detail
/// LOD and the protected edges); a Python script in Blender turns them into PNGs, two views side by side.
/// </summary>
public static class ProtectionView
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Blender from PATH, the usual install folders and Steam libraries; the newest version wins.</summary>
    public static string? FindBlender()
    {
        var exe = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
        var found = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            Add(Path.Combine(dir.Trim('"'), exe));
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (pf == "") continue;
                var foundation = Path.Combine(pf, "Blender Foundation");
                if (Directory.Exists(foundation))
                    foreach (var d in Directory.EnumerateDirectories(foundation)) Add(Path.Combine(d, exe));
            }
            foreach (var steam in SteamLibraries()) Add(Path.Combine(steam, "steamapps", "common", "Blender", exe));
        }
        else if (OperatingSystem.IsMacOS())
            Add("/Applications/Blender.app/Contents/MacOS/Blender");
        else
        {
            foreach (var p in new[] { "/usr/bin/blender", "/usr/local/bin/blender", "/snap/bin/blender" }) Add(p);
            foreach (var root in new[] { "/opt", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
                if (Directory.Exists(root))
                    foreach (var d in Directory.EnumerateDirectories(root, "blender*")) Add(Path.Combine(d, exe));
            foreach (var steam in SteamLibraries()) Add(Path.Combine(steam, "steamapps", "common", "Blender", exe));
        }
        // newest first: version from the folder name ("Blender 5.3"), otherwise by file date
        return found.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => VersionIn(p)).ThenByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

        void Add(string p) { try { if (File.Exists(p)) found.Add(Path.GetFullPath(p)); } catch { } }
    }

    static Version VersionIn(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0);
    }

    static IEnumerable<string> SteamLibraries()
    {
        var roots = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam") }
            : new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam"),
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam") };
        foreach (var root in roots)
        {
            yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadLines(vdf))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "\"path\"\\s+\"(.+)\"");
                if (m.Success) yield return m.Groups[1].Value.Replace(@"\\", @"\");
            }
        }
    }

    /// <summary>Does this path look like a Blender executable (or a folder holding one)? Used for the optional argument.</summary>
    public static string? AsBlender(string arg)
    {
        try
        {
            var p = Path.GetFullPath(arg);
            if (File.Exists(p) && !p.EndsWith(".Gbx", StringComparison.OrdinalIgnoreCase)) return p;
            foreach (var exe in new[] { "blender.exe", "blender", "Contents/MacOS/Blender" })
                if (Directory.Exists(p) && File.Exists(Path.Combine(p, exe))) return Path.Combine(p, exe);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Scene file for one item: the highest-detail LOD (game coordinates) and the protected edges of that LOD.
    /// Returns the number of protected edges drawn.
    /// </summary>
    public static int WriteScene(byte[] itemBytes, string title, string scenePath, string pngPath)
    {
        var item = Gbx.ParseNode<CGameItemModel>(new MemoryStream(itemBytes));
        var mesh = Slimmer.Mesh(item);
        var prot = Slimmer.DrivingEdges(mesh);
        // LOD 0: the shaded geoms of the lowest LOD bit; without that information every visual
        var visuals = Enumerable.Range(0, mesh.Visuals!.Count).ToHashSet();
        if (mesh.ShadedGeoms is { Length: > 0 } geoms)
        {
            var masks = geoms.Select(g => g.LodMask).Where(m => m != 0).ToList();
            var low = masks.Count > 0 ? masks.Select(m => m & -m).Min() : 0;
            visuals = geoms.Where(g => low == 0 || (g.LodMask & low) != 0).Select(g => g.VisualIndex).ToHashSet();
        }
        var sb = new StringBuilder();
        sb.Append("{\"title\":").Append(Json(title)).Append(",\"out\":").Append(Json(pngPath)).Append(",\"parts\":[");
        var firstPart = true;
        foreach (var vi in visuals.Order())
        {
            if (vi >= mesh.Visuals.Count || mesh.Visuals[vi] is not CPlugVisualIndexedTriangles vis) continue;
            var pos = vis.VertexStreams?.OfType<CPlugVertexStream>().FirstOrDefault(s => s.Positions is { Length: > 0 })?.Positions;
            var idx = vis.IndexBuffer?.Indices;
            if (pos is null || idx is null) continue;
            if (!firstPart) sb.Append(',');
            firstPart = false;
            sb.Append("{\"pos\":[");
            for (var i = 0; i < pos.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(F(pos[i].X)).Append(',').Append(F(pos[i].Y)).Append(',').Append(F(pos[i].Z));
            }
            sb.Append("],\"tri\":[");
            var n = 0;
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                if (idx[t] >= pos.Length || idx[t + 1] >= pos.Length || idx[t + 2] >= pos.Length) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append(idx[t]).Append(',').Append(idx[t + 1]).Append(',').Append(idx[t + 2]);
            }
            sb.Append("]}");
        }
        sb.Append("],\"edges\":[");
        var edges = prot.Edges.Where(e => visuals.Contains(e.Visual)).ToList();
        for (var i = 0; i < edges.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var (_, a, b) = edges[i];
            sb.Append(F(a.X)).Append(',').Append(F(a.Y)).Append(',').Append(F(a.Z)).Append(',')
              .Append(F(b.X)).Append(',').Append(F(b.Y)).Append(',').Append(F(b.Z));
        }
        sb.Append("]}");
        File.WriteAllText(scenePath, sb.ToString());
        return edges.Count;

        static string F(float v) => v.ToString("0.#####", Inv);
    }

    static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>Renders the scene files with Blender, a few Blender processes side by side. Returns the number of PNGs written.</summary>
    public static int Render(string blender, IReadOnlyList<string> scenes, string workDir, Action<string> log)
    {
        var script = Path.Combine(workDir, "render_protection.py");
        File.WriteAllText(script, Script);
        var procs = scenes.Count < 40 ? 1 : Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        var chunks = Enumerable.Range(0, procs).Select(k => scenes.Where((_, i) => i % procs == k).ToList()).Where(c => c.Count > 0).ToList();
        var rendered = 0; var gate = new object(); var lastShown = DateTime.UtcNow;
        var errors = new List<string>();
        Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = chunks.Count }, (chunk, _, k) =>
        {
            var list = Path.Combine(workDir, $"scenes_{k}.txt");
            File.WriteAllLines(list, chunk);
            var psi = new ProcessStartInfo(blender)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--background", "--factory-startup", "--python", script, "--", list }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d && d.Contains("Error", StringComparison.OrdinalIgnoreCase)) lock (gate) errors.Add(d); };
            p.BeginErrorReadLine();
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                if (line.StartsWith("ITEMSLIM_RENDERED "))
                    lock (gate)
                    {
                        rendered++;
                        if (scenes.Count <= 20 || (DateTime.UtcNow - lastShown).TotalSeconds > 5 || rendered == scenes.Count)
                        {
                            log($"  rendered {rendered}/{scenes.Count}");
                            lastShown = DateTime.UtcNow;
                        }
                    }
                else if (line.StartsWith("ITEMSLIM_ERROR ") || line.StartsWith("Traceback") || line.StartsWith("  File "))
                    lock (gate) errors.Add(line);
            }
            p.WaitForExit();
        });
        foreach (var e in errors.Distinct().Take(10)) log("  blender: " + e);
        return rendered;
    }

    // Blender side. The game is y-up, Blender z-up: game x = Blender x, game z = -Blender y, game y = Blender z
    // (the orientation Blendermania exports from).
    const string Script = """
import bpy, json, math, os, sys, traceback
import numpy as np
from mathutils import Vector

def g2b(x, y, z):
    return (x, -z, y)

def setup():
    sc = bpy.context.scene
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    sc.render.engine = "BLENDER_WORKBENCH"
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGB"
    sc.render.resolution_x, sc.render.resolution_y = 960, 720
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = False
    sh = sc.display.shading
    sh.light = "STUDIO"; sh.color_type = "OBJECT"; sh.show_cavity = True; sh.cavity_type = "WORLD"
    sh.show_object_outline = True; sh.show_backface_culling = False
    sc.display_settings.display_device = "sRGB"
    try: sc.view_settings.view_transform = "Standard"
    except Exception: pass
    if sc.world is None: sc.world = bpy.data.worlds.new("w")
    sc.world.color = (0.32, 0.34, 0.38)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    sc.collection.objects.link(cam); sc.camera = cam
    cam.data.type = "ORTHO"; cam.data.clip_start = 0.01; cam.data.clip_end = 100000
    return sc, cam

def clear(sc, cam):
    for o in list(sc.collection.objects):
        if o is not cam:
            data = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if data is not None and data.users == 0:
                (bpy.data.meshes if isinstance(data, bpy.types.Mesh) else bpy.data.curves).remove(data)
    for img in list(bpy.data.images):
        if img.users == 0: bpy.data.images.remove(img)

def build(sc, job):
    lo = [1e9] * 3; hi = [-1e9] * 3
    for k, part in enumerate(job["parts"]):
        p = part["pos"]; t = part["tri"]
        verts = [g2b(p[i], p[i + 1], p[i + 2]) for i in range(0, len(p), 3)]
        faces = [(t[i], t[i + 1], t[i + 2]) for i in range(0, len(t), 3)]
        me = bpy.data.meshes.new(f"part{k}")
        me.from_pydata(verts, [], faces)
        me.validate(clean_customdata=False)
        o = bpy.data.objects.new(f"part{k}", me)
        o.color = (0.78, 0.78, 0.8, 1)
        sc.collection.objects.link(o)
        for v in verts:
            for a in range(3):
                lo[a] = min(lo[a], v[a]); hi[a] = max(hi[a], v[a])
    if lo[0] > hi[0]:
        lo = [0, 0, 0]; hi = [1, 1, 1]
    size = max(hi[a] - lo[a] for a in range(3)) or 1.0
    e = job["edges"]
    if e:
        cu = bpy.data.curves.new("protected", "CURVE"); cu.dimensions = "3D"
        cu.bevel_depth = max(0.04, size * 0.006); cu.bevel_resolution = 2
        for i in range(0, len(e), 6):
            sp = cu.splines.new("POLY"); sp.points.add(1)
            sp.points[0].co = (*g2b(e[i], e[i + 1], e[i + 2]), 1)
            sp.points[1].co = (*g2b(e[i + 3], e[i + 4], e[i + 5]), 1)
        eo = bpy.data.objects.new("protected", cu); eo.color = (1, 0.06, 0.04, 1)
        sc.collection.objects.link(eo)
    return Vector([(lo[a] + hi[a]) / 2 for a in range(3)]), Vector([hi[a] - lo[a] for a in range(3)])

def shoot(sc, cam, centre, dims, direction, path):
    d = Vector(direction).normalized()
    r = max(dims.length / 2, 0.5)
    cam.location = centre + d * (r * 4)
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    cam.data.ortho_scale = r * 2.1 * max(1.0, sc.render.resolution_x / sc.render.resolution_y)
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)

def stitch(a, b, out):
    ia = bpy.data.images.load(a); ib = bpy.data.images.load(b)
    w, h = ia.size
    pa = np.empty(w * h * 4, np.float32); ia.pixels.foreach_get(pa)
    pb = np.empty(w * h * 4, np.float32); ib.pixels.foreach_get(pb)
    gap = 8
    arr = np.ones((h, w * 2 + gap, 4), np.float32)
    arr[:, :w] = pa.reshape(h, w, 4); arr[:, w + gap:] = pb.reshape(h, w, 4)
    img = bpy.data.images.new("stitched", w * 2 + gap, h, alpha=False)
    img.pixels.foreach_set(arr.ravel())
    img.filepath_raw = out; img.file_format = "PNG"; img.save()
    for i in (ia, ib, img): bpy.data.images.remove(i)
    os.remove(a); os.remove(b)

try:
    listfile = sys.argv[sys.argv.index("--") + 1]
    scenes = [l.strip() for l in open(listfile, encoding="utf-8") if l.strip()]
    sc, cam = setup()
    for path in scenes:
        try:
            job = json.load(open(path, encoding="utf-8"))
            clear(sc, cam)
            centre, dims = build(sc, job)
            out = job["out"]
            os.makedirs(os.path.dirname(out), exist_ok=True)
            base = out[:-4]
            shoot(sc, cam, centre, dims, (-0.55, -0.85, 0.62), base + "~a.png")
            shoot(sc, cam, centre, dims, (0.55, 0.85, 0.62), base + "~b.png")
            stitch(base + "~a.png", base + "~b.png", out)
            print("ITEMSLIM_RENDERED " + out, flush=True)
        except Exception as ex:
            print("ITEMSLIM_ERROR " + path + ": " + repr(ex), flush=True)
except Exception:
    traceback.print_exc()
sys.stdout.flush()
os._exit(0)
""";
}
