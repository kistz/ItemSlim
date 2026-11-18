# ItemSlim
> [!IMPORTANT]
> While i had the idea for this tool because of knowledge how compression algorithms work this project is slopped up.
> I verified it on multiple sets and gave the code a read for correctness but did not make any changes to the slop machines output.
> The README is human authored. 

Makes Trackmania 2020 custom items (`.Item.Gbx`) smaller without a visible change.

Exported items store vertex positions and UVs as full-precision floats. Those values are noisy in their lowest
bits, so they barely compress. 
Vertex and triangle counts don't change, and neither do icons, materials or item settings.
ItemSlim only snaps vertices to a fine grid:

- positions: 1/1024 m, so each vertex moves by at most 0.49 mm (or roughly a grain of salt for the americans)
- UVs: 1/16384

The files then compress far better. Typical results on [real item packs](https://github.com/ski-freak/TrackmaniaItemsSorted):

- File Size: −20 to −25 %
- Embedded Size: −15 %
  - Embedded in this context means "size while in a trackmania map file". Inside a map, the game stores each custom item uncompressed in the map's zip archive. The "embedded" figure is
the item's decompressed content after deflate compression. It approximates how much the item adds to a map that
uses it, and it is the number that matters for map size limits.


## Usage

### Windows
```
ItemSlim "<file or folder>" [options]
```
for example:
```
ItemSlim "C:\Users\me\Documents\Trackmania\Items\MySet" --dry-run
ItemSlim "C:\Users\me\Documents\Trackmania\Items\MySet"
```

### Linux
```
tar -xzf ItemSlim-linux-x64.tar.gz
./ItemSlim ~/Trackmania/Items/MySet
```
The `.tar.gz` archives keep the executable bit. If you copy the bare file instead, run `chmod +x ItemSlim` first.

### Info
The input files are never changed.
For each input folder, ItemSlim writes a complete copy, `<folder>_optimized`, next to it, using all CPU cores.
The items in the copy are optimized and skipped items/files that aren't items are copied as they are. 
A single item is written as `<name>_optimized.Item.Gbx`. 
Once you're happy with the result, rename the output to replace the original.
If the `_optimized` output already exists, ItemSlim stops instead of mixing old and new files. Delete or rename it
first.

| Option | Meaning |
|---|---|
| `--dry-run` | Only measure what would be gained; no items are written. |
| `--quiet` | Summary only, no line per item. |

## Report
Every run, including a dry run, also writes a Markdown report, `<name>_ItemSlim.md`, next to the first input.
It shows stats from the run.

## Downloads / Build
You can [download it on the Release page](https://github.com/kistz/ItemSlim/releases) in the repo for windows and liunux x64.
You can also build it yourself with the .NET 10 SDK:
```
dotnet build -c Release
dotnet publish -c Release -r win-x64     -o publish/win-x64
dotnet publish -c Release -r linux-x64   -o publish/linux-x64
```

## Safety (slop machine wording)
- **Your originals are never touched.** Everything is written to the `_optimized` copy.
- **Verified before writing.** Each item is rounded in memory, saved, read back and checked before it is written:
  - same vertex counts
  - every coordinate on the grid
  - no vertex moved more than half a grid step
  - the icon unchanged

  An item that fails a check is reported and copied unchanged (exit code 1).
- **Safe to run on optimized items.** Items that are already on the grid are reported as "already slim" and copied
  unchanged.
- **Unsupported items are skipped.** Mesh Modeler (crystal) items, prefab items and variant lists have no
  static mesh to round. They are reported as skipped and copied byte-for-byte.

