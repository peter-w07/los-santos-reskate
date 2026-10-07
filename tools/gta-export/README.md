# gta-export

C# console tool that reads the user's own GTA V install (read-only) through
[CodeWalker.Core](https://github.com/dexyfex/CodeWalker) and writes the static world collision,
material table and water data to `work/export/`. Output layout and the binary tile format are
documented in `docs/export-format.md`.

```
git clone --depth 1 https://github.com/dexyfex/CodeWalker ../../third_party/CodeWalker   # tested at 485d56b, built unmodified
dotnet build -c Release
dotnet bin/Release/net8.0/gta-export.dll all        # about 3 minutes, 6.4 GB under Z:\LosSantosMC\work\export
dotnet bin/Release/net8.0/gta-export.dll interiors            # interior (MLO) layer -> work/export/interiors (0.6 GB)
dotnet bin/Release/net8.0/gta-export.dll interiors-preview    # plan views + metro sections -> interiors/preview
dotnet bin/Release/net8.0/gta-export.dll --help
```

- Keys: derived in memory from `GTA5.exe` on each run (about a second); never written, never printed.
- The game folder is only opened for reading; the tool refuses an output folder inside it.
- Resumable and deterministic: up-to-date files are skipped by header signature; `--force` rebuilds.
- `--profile online` (default) is the multiplayer map with the story-start state of every scripted
  group; `--profile story` is the single-player map. `--hour H` picks the hour for time-dependent
  groups (default 12). See "Which map, and its state" in `docs/export-format.md`.
- The state of scripted map groups is read from the game's own scripts at every run (about 15 s);
  `--no-scripts` falls back to the built-in table.

| file | role |
|---|---|
| `GameContext.cs` | open the game (mods off), classify every mounted `.ybn`, merge manifest group entries, add the story-only groups |
| `MapSet.cs` | which level archives are mounted: base, startup change sets, GROUP_MAP change sets (profiles) |
| `ScriptScan.cs` | `.ysc` reader and disassembler (131-opcode table), uses of map data names in scripts |
| `ScriptEvidence.cs` | the building controller's state table and the request / remove natives, derived from the scripts |
| `GroupDefaults.cs` | story-start state of a scripted group: hours mask, state table, script requests, built-in table |
| `StateAudit.cs` | `audit`: dumps for the map-state audit (change sets, all ymaps / manifests / ybn, script uses) |
| `YbnFlatten.cs` | one `.ybn` -> flat world-space mesh, primitives kept and tessellated |
| `CollisionExport.cs` | phase 1: meshes, `sources.json`, `interiors.json` |
| `Tiler.cs`, `Stats.cs` | phase 2: 256 m tiles, statistics, `index.json` |
| `Lsct.cs` | the binary container |
| `MaterialsExport.cs`, `WaterExport.cs` | JSON tables |
| `Preview.cs`, `Png.cs` | verification renders from the exported tiles |
| `InteriorScan.cs` | MLO archetypes (all archives), MLO instances (mounted ymaps + story-only placements), train tracks |
| `InteriorsExport.cs` | interior layer: resolve bounds, place them in world space, categories, attached/detached, tiles |
| `InteriorsJson.cs` | `interiors/index.json`, `portals.json`, `tracks.json` |
| `InteriorsPreview.cs` | plan views and vertical track sections of the interior layer |
| `Probe.cs` | development helpers (`probe`), and `check-static`: read-only tile signature check |
| `lsct.py` | independent numpy reader; `python lsct.py check <export_dir>` validates every tile |
| `interiors_check.py` | numpy checks of the interior layer: `tiles`, `tracks`, `floors`, `column` |
| `examples/read_lsct.rs` | std-only Rust reader to copy into the voxelizer |
