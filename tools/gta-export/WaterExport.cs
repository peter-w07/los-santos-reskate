using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>water.xml quads, waterheight.dat (rivers/lakes/pools) and heightmap.dat -> JSON (+ raw grids).</summary>
public static class WaterExport
{
    public static void Run(GameContext ctx, string outDir)
    {
        var dir = Path.Combine(outDir, "water");
        Directory.CreateDirectory(dir);
        ExportWaterXml(ctx, dir);
        ExportWaterHeight(ctx, dir);
        ExportHeightmaps(ctx, dir);
    }

    // ------------------------------------------------------------------ water.xml

    static void ExportWaterXml(GameContext ctx, string dir)
    {
        var files = new List<(string key, RpfFileEntry entry)>();
        var main = ctx.FindCommon(@"levels\gta5\water.xml");
        if (main != null) files.Add(("main", main));
        var island = ctx.FindCommon(@"levels\gta5\water_heistisland.xml");
        if (island != null) files.Add(("heist_island", island));
        if (files.Count == 0) { Log.Warn("water.xml not found"); return; }

        Paths.WriteJson(Path.Combine(dir, "water.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "GTA V water quads (water.xml). Axis-aligned in plan view, GTA world metres; z is the water surface height. " +
                                   "type 0 = full rectangle, 1..4 = right triangle (half of the rectangle); 'corners' lists the actual outline. " +
                                   "'main' is Los Santos + Blaine County (ocean, Alamo Sea, lakes, reservoirs, pools); 'heist_island' is Cayo Perico (only present in game while that island is loaded).");
            w.WriteString("type_note", "corner order for triangles follows CodeWalker: 1 = (min,min),(max,min),(min,max); 2 = (min,min),(max,max),(min,max); 3 = (max,min),(max,max),(min,max); 4 = (min,min),(max,min),(max,max)");
            foreach (var (key, entry) in files)
            {
                var doc = new XmlDocument();
                doc.LoadXml(CodeWalker.TextUtil.GetUTF8Text(ctx.ReadEntry(entry)));
                var root = doc.DocumentElement;
                w.WriteStartObject(key);
                w.WriteString("source", Paths.Fwd(entry.Path));
                int nq = WriteQuads(w, root, "WaterQuads", "quads", true);
                int nc = WriteQuads(w, root, "CalmingQuads", "calming_quads", false);
                int nw = WriteQuads(w, root, "WaveQuads", "wave_quads", false);
                w.WriteEndObject();
                Log.Info($"water: {entry.Path}: {nq} water quads, {nc} calming quads, {nw} wave quads");
            }
            w.WriteEndObject();
        });
    }

    static int WriteQuads(Utf8JsonWriter w, XmlElement root, string section, string outName, bool corners)
    {
        var items = root.SelectNodes(section + "/Item");
        w.WriteStartArray(outName);
        int n = 0;
        float zmin = float.MaxValue, zmax = float.MinValue;
        foreach (XmlNode item in items)
        {
            w.WriteStartObject();
            double minX = 0, maxX = 0, minY = 0, maxY = 0; int type = 0;
            foreach (XmlNode c in item.ChildNodes)
            {
                if (c.NodeType != XmlNodeType.Element) continue;
                var v = c.Attributes?["value"]?.Value ?? c.InnerText.Trim();
                if (v == "true" || v == "false") w.WriteBoolean(c.Name, v == "true");
                else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    w.WriteNumber(c.Name, d);
                    switch (c.Name)
                    {
                        case "minX": minX = d; break;
                        case "maxX": maxX = d; break;
                        case "minY": minY = d; break;
                        case "maxY": maxY = d; break;
                        case "Type": type = (int)d; break;
                        case "z": zmin = Math.Min(zmin, (float)d); zmax = Math.Max(zmax, (float)d); break;
                    }
                }
                else w.WriteString(c.Name, v);
            }
            if (corners)
            {
                (double, double)[] pts = type switch
                {
                    1 => new[] { (minX, minY), (maxX, minY), (minX, maxY) },
                    2 => new[] { (minX, minY), (maxX, maxY), (minX, maxY) },
                    3 => new[] { (maxX, minY), (maxX, maxY), (minX, maxY) },
                    4 => new[] { (minX, minY), (maxX, minY), (maxX, maxY) },
                    _ => new[] { (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY) },
                };
                w.WriteStartArray("corners");
                foreach (var (x, y) in pts) { w.WriteStartArray(); w.WriteNumberValue(x); w.WriteNumberValue(y); w.WriteEndArray(); }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            n++;
        }
        w.WriteEndArray();
        return n;
    }

    // ------------------------------------------------------------------ waterheight.dat

    static void ExportWaterHeight(GameContext ctx, string dir)
    {
        var entry = ctx.FindCommon(@"levels\gta5\waterheight.dat");
        if (entry == null) { Log.Warn("waterheight.dat not found"); return; }
        WatermapFile wm;
        try
        {
            wm = new WatermapFile();
            wm.Load(ctx.ReadEntry(entry), entry);
        }
        catch (Exception ex)
        {
            Log.Warn("waterheight.dat could not be parsed: " + ex.Message);
            return;
        }

        void Item(Utf8JsonWriter w, WatermapFile.WaterItem it, int index)
        {
            w.WriteStartObject();
            w.WriteNumber("index", index);
            w.Vec3("position", it.Position);
            w.Vec3("size", it.Size);
            w.WriteStartArray("colour_rgba");
            w.WriteNumberValue(it.Colour.R); w.WriteNumberValue(it.Colour.G); w.WriteNumberValue(it.Colour.B); w.WriteNumberValue(it.Colour.A);
            w.WriteEndArray();
            if (it.Vectors != null)
            {
                w.WriteStartArray("vectors");
                foreach (var v in it.Vectors)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(JsonExt.R(v.X)); w.WriteNumberValue(JsonExt.R(v.Y)); w.WriteNumberValue(JsonExt.R(v.Z)); w.WriteNumberValue(JsonExt.R(v.W));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        int cells = 0;
        Paths.WriteJson(Path.Combine(dir, "waterheight.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "waterheight.dat as parsed by CodeWalker (WatermapFile): a coarse grid referencing rivers, lakes and pools that are NOT sea-level water quads. " +
                                   "Item position/size are in world metres (position.z = water surface height for lakes/pools); river/lake 'vectors' are per-segment data (xyz + w) whose exact meaning is not fully reverse engineered. Each cell ref carries 'z', the water height CodeWalker derives for that cell (river: vectors[vector].z, lake/pool: position.z). Treat as hints; the river water surfaces themselves are in the collision tiles as groups with the MAP_RIVER type flag.");
            w.WriteString("source", Paths.Fwd(entry.Path));
            w.WriteNumber("corner_x", wm.CornerX);
            w.WriteNumber("corner_y", wm.CornerY);
            w.WriteNumber("cell_size_x", wm.TileX);
            w.WriteNumber("cell_size_y", wm.TileY);
            w.WriteNumber("width", wm.Width);
            w.WriteNumber("height", wm.Height);
            w.WriteString("grid_note", "cell (col,row) covers x in [corner_x + col*cell_size_x, +cell_size_x), y in [corner_y - (row+1)*cell_size_y, corner_y - row*cell_size_y) (row 0 is the northern edge; checked: the Alamo Sea lake cells land on the z=30 water quads only with this orientation)");
            w.WriteStartArray("rivers"); for (int i = 0; i < (wm.Rivers?.Length ?? 0); i++) Item(w, wm.Rivers[i], i); w.WriteEndArray();
            w.WriteStartArray("lakes"); for (int i = 0; i < (wm.Lakes?.Length ?? 0); i++) Item(w, wm.Lakes[i], i); w.WriteEndArray();
            w.WriteStartArray("pools"); for (int i = 0; i < (wm.Pools?.Length ?? 0); i++) Item(w, wm.Pools[i], i); w.WriteEndArray();
            w.WriteStartArray("cells");
            if (wm.GridWatermapRefs != null)
            {
                for (int y = 0; y < wm.Height; y++)
                    for (int x = 0; x < wm.Width; x++)
                    {
                        var refs = wm.GridWatermapRefs[y * wm.Width + x];
                        if (refs == null || refs.Length == 0) continue;
                        cells++;
                        w.WriteStartObject();
                        w.WriteNumber("col", x);
                        w.WriteNumber("row", y);
                        w.WriteStartArray("refs");
                        foreach (var r in refs)
                        {
                            w.WriteStartObject();
                            w.WriteString("type", r.Type.ToString().ToLowerInvariant());
                            w.WriteNumber("item", r.ItemIndex);
                            w.WriteNumber("vector", r.VectorIndex);
                            float z = r.Type == WatermapFile.WaterItemType.River ? r.Vector.Z : (r.Item != null ? r.Item.Position.Z : r.Vector.Z);
                            w.WriteNumber("z", JsonExt.R(z));
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        Log.Info($"waterheight: {wm.Width}x{wm.Height} grid, {wm.Rivers?.Length} rivers, {wm.Lakes?.Length} lakes, {wm.Pools?.Length} pools, {cells} non-empty cells");
    }

    // ------------------------------------------------------------------ heightmap.dat

    static void ExportHeightmaps(GameContext ctx, string dir)
    {
        var list = new List<(string key, RpfFileEntry e)>();
        var main = ctx.FindCommon(@"levels\gta5\heightmap.dat");
        if (main != null) list.Add(("heightmap", main));
        var island = ctx.FindCommon(@"levels\gta5\heightmapheistisland.dat");
        if (island != null) list.Add(("heightmap_heistisland", island));

        Paths.WriteJson(Path.Combine(dir, "heightmap.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "heightmap.dat: the game's own coarse min/max height grids (used for aircraft/camera). Raw grids are width*height unsigned bytes, row-major, row 0 = southern edge. " +
                                   "Sample (col,row) sits at x = bb_min.x + col*(bb_max.x-bb_min.x)/(width-1), y = bb_min.y + row*(bb_max.y-bb_min.y)/(height-1), height = bb_min.z + byte*(bb_max.z-bb_min.z)/255 (layout as rendered by CodeWalker). Useful as an independent sanity check of the collision export.");
            w.WriteStartArray("maps");
            foreach (var (key, e) in list)
            {
                try
                {
                    var hm = new HeightmapFile();
                    hm.Load(ctx.ReadEntry(e), e);
                    Paths.AtomicWriteBytes(Path.Combine(dir, key + "_max.u8"), hm.MaxHeights);
                    Paths.AtomicWriteBytes(Path.Combine(dir, key + "_min.u8"), hm.MinHeights);
                    w.WriteStartObject();
                    w.WriteString("name", key);
                    w.WriteString("source", Paths.Fwd(e.Path));
                    w.WriteNumber("width", hm.Width);
                    w.WriteNumber("height", hm.Height);
                    w.Vec3("bb_min", hm.BBMin);
                    w.Vec3("bb_max", hm.BBMax);
                    w.WriteString("max_file", key + "_max.u8");
                    w.WriteString("min_file", key + "_min.u8");
                    w.WriteNumber("max_byte", hm.MaxHeights.Max());
                    w.WriteEndObject();
                    Log.Info($"heightmap: {e.Path}: {hm.Width}x{hm.Height}, bb {hm.BBMin} .. {hm.BBMax}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"heightmap {e.Path} failed: {ex.Message}");
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
