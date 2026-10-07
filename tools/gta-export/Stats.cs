using System.Text.Json;

namespace GtaExport;

/// <summary>Statistics over the unique (per-ybn, not per-tile) geometry.</summary>
public sealed class Stats
{
    public const int Layers = 3;
    public readonly long[] LayerYbns = new long[Layers];
    public readonly long[] LayerGroups = new long[Layers];
    public readonly long[] LayerTris = new long[Layers];       // real triangles
    public readonly long[] LayerPrimTris = new long[Layers];   // tessellated primitive triangles
    public readonly long[,] LayerPrims = new long[Layers, 5];  // by kind
    public readonly long[,] MatTris = new long[Layers, 256];
    public readonly double[,] MatArea = new double[Layers, 256];
    public readonly double[,] MatPlanArea = new double[Layers, 256];
    public readonly long[] MatFlagTris = new long[16];
    public readonly Dictionary<(uint, uint), (long groups, long tris)> FlagCombos = new();
    public V3 Min = new(float.MaxValue, float.MaxValue, float.MaxValue), Max = new(float.MinValue, float.MinValue, float.MinValue);

    // the story map area of interest, ungrouped (always loaded) solid geometry only
    public const float AoiX0 = -4200, AoiX1 = 4700, AoiY0 = -4400, AoiY1 = 8600;
    public float AoiZMin = float.MaxValue, AoiZMax = float.MinValue;
    public V3 AoiLowest, AoiHighest;
    public string AoiLowestYbn = "", AoiHighestYbn = "";
    public long AoiTris;
    public readonly long[] AoiZHist = new long[64]; // 20 m bins from -300 m

    void Merge(Stats o)
    {
        for (int l = 0; l < Layers; l++)
        {
            LayerYbns[l] += o.LayerYbns[l]; LayerGroups[l] += o.LayerGroups[l]; LayerTris[l] += o.LayerTris[l]; LayerPrimTris[l] += o.LayerPrimTris[l];
            for (int k = 0; k < 5; k++) LayerPrims[l, k] += o.LayerPrims[l, k];
            for (int m = 0; m < 256; m++) { MatTris[l, m] += o.MatTris[l, m]; MatArea[l, m] += o.MatArea[l, m]; MatPlanArea[l, m] += o.MatPlanArea[l, m]; }
        }
        for (int i = 0; i < 16; i++) MatFlagTris[i] += o.MatFlagTris[i];
        foreach (var kv in o.FlagCombos)
        {
            FlagCombos.TryGetValue(kv.Key, out var c);
            FlagCombos[kv.Key] = (c.groups + kv.Value.groups, c.tris + kv.Value.tris);
        }
        if (o.Min.X < Min.X) Min.X = o.Min.X; if (o.Min.Y < Min.Y) Min.Y = o.Min.Y; if (o.Min.Z < Min.Z) Min.Z = o.Min.Z;
        if (o.Max.X > Max.X) Max.X = o.Max.X; if (o.Max.Y > Max.Y) Max.Y = o.Max.Y; if (o.Max.Z > Max.Z) Max.Z = o.Max.Z;
        if (o.AoiZMin < AoiZMin) { AoiZMin = o.AoiZMin; AoiLowest = o.AoiLowest; AoiLowestYbn = o.AoiLowestYbn; }
        if (o.AoiZMax > AoiZMax) { AoiZMax = o.AoiZMax; AoiHighest = o.AoiHighest; AoiHighestYbn = o.AoiHighestYbn; }
        AoiTris += o.AoiTris;
        for (int i = 0; i < AoiZHist.Length; i++) AoiZHist[i] += o.AoiZHist[i];
    }

    public static Stats Gather(string colDir, Sources src, int threads)
    {
        var total = new Stats();
        var gate = new object();
        Parallel.ForEach(src.Ybns.Where(y => y.Ok), new ParallelOptions { MaxDegreeOfParallelism = threads },
            () => new Stats(),
            (y, _, local) => { local.Add(colDir, y); return local; },
            local => { lock (gate) total.Merge(local); });
        return total;
    }

    void Add(string colDir, SrcYbn y)
    {
        var m = Mesh.Read(Path.Combine(colDir, y.File.Replace('/', Path.DirectorySeparatorChar)));
        int l = Math.Clamp(y.Layer, 0, Layers - 1);
        LayerYbns[l]++;
        LayerGroups[l] += m.Groups.Length;
        if (m.Verts.Length > 0)
        {
            var h = m.Header;
            if (h.Min.X < Min.X) Min.X = h.Min.X; if (h.Min.Y < Min.Y) Min.Y = h.Min.Y; if (h.Min.Z < Min.Z) Min.Z = h.Min.Z;
            if (h.Max.X > Max.X) Max.X = h.Max.X; if (h.Max.Y > Max.Y) Max.Y = h.Max.Y; if (h.Max.Z > Max.Z) Max.Z = h.Max.Z;
        }
        foreach (var p in m.Prims) if (p.Kind < 5) LayerPrims[l, p.Kind]++;
        bool aoiCandidate = y.DefaultOn && l < 2;
        var v = m.Verts; var t = m.Tris; var at = m.Attrs;
        foreach (ref readonly var g in m.Groups.AsSpan())
        {
            FlagCombos.TryGetValue((g.TypeFlags, g.IncludeFlags), out var fc);
            FlagCombos[(g.TypeFlags, g.IncludeFlags)] = (fc.groups + 1, fc.tris + g.NTris);
            bool solid = aoiCandidate && (g.TypeFlags & (BoundFlags.MAP_RIVER | BoundFlags.FOLIAGE)) == 0 && (g.TypeFlags != BoundFlags.MAP_STAIRS);
            for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
            {
                ref readonly var a = ref v[t[i * 3]]; ref readonly var b = ref v[t[i * 3 + 1]]; ref readonly var c = ref v[t[i * 3 + 2]];
                var attr = at[i];
                if (attr.Kind == 0) LayerTris[l]++; else LayerPrimTris[l]++;
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, wx = c.X - a.X, wy = c.Y - a.Y, wz = c.Z - a.Z;
                double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                MatTris[l, attr.Material]++;
                MatArea[l, attr.Material] += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                MatPlanArea[l, attr.Material] += 0.5 * Math.Abs(cz);
                for (int bit = 0; bit < 16; bit++) if ((attr.MatFlags & (1 << bit)) != 0) MatFlagTris[bit]++;
                if (solid)
                {
                    float mx = (a.X + b.X + c.X) / 3f, my = (a.Y + b.Y + c.Y) / 3f;
                    if (mx >= AoiX0 && mx <= AoiX1 && my >= AoiY0 && my <= AoiY1)
                    {
                        AoiTris++;
                        AoiPoint(a, y.Name); AoiPoint(b, y.Name); AoiPoint(c, y.Name);
                        float mz = (a.Z + b.Z + c.Z) / 3f;
                        int bin = Math.Clamp((int)Math.Floor((mz + 300f) / 20f), 0, AoiZHist.Length - 1);
                        AoiZHist[bin]++;
                    }
                }
            }
        }
    }

    void AoiPoint(in V3 p, string ybn)
    {
        if (p.Z < AoiZMin) { AoiZMin = p.Z; AoiLowest = p; AoiLowestYbn = ybn; }
        if (p.Z > AoiZMax) { AoiZMax = p.Z; AoiHighest = p; AoiHighestYbn = ybn; }
    }
}

public static class IndexWriter
{
    public static void Write(string colDir, Sources src, List<Tiler.TileResult> tiles, Stats st, float margin, double tileSecs)
    {
        string[] matNames = LoadMaterialNames(Path.Combine(Path.GetDirectoryName(colDir)!, "materials.json"));
        long totalTris = 0, totalPrimTris = 0;
        for (int l = 0; l < Stats.Layers; l++) { totalTris += st.LayerTris[l]; totalPrimTris += st.LayerPrimTris[l]; }
        long meshBytes = src.Ybns.Where(y => y.Ok).Sum(y => y.Bytes);
        long tileBytes = tiles.Sum(t => t.Bytes);

        Paths.WriteJson(Path.Combine(colDir, "index.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "GTA V static world collision exported by tools/gta-export. Binary layout: docs/export-format.md");
            w.WriteStartObject("format");
            w.WriteString("magic", "LSCT");
            w.WriteNumber("version", LsctHeader.CurrentVersion);
            w.WriteString("endian", "little");
            w.WriteNumber("tile_size", Tiler.TileSize);
            w.WriteNumber("tile_margin", margin);
            w.WriteString("tile_rule", "tile (tx,ty) covers gta x in [tx*256,(tx+1)*256), y in [ty*256,(ty+1)*256); a triangle is stored in every tile whose rectangle, grown by tile_margin, its plan-view projection overlaps");
            w.WriteString("coords", "GTA world space, metres: +x east, +y north, +z up. Minecraft: mc_x = gta_x, mc_z = -gta_y, mc_y = gta_z");
            w.WriteString("tile_file", "tiles/tile_{tx}_{ty}.lsct");
            w.WriteString("mesh_file", "ybn/{name}.lsct");
            w.WriteEndObject();

            if (src.Root.TryGetProperty("game", out var game)) { w.WritePropertyName("game"); game.WriteTo(w); }

            w.WriteStartObject("extents");
            w.Vec3("min", st.Min.X, st.Min.Y, st.Min.Z);
            w.Vec3("max", st.Max.X, st.Max.Y, st.Max.Z);
            if (tiles.Count > 0)
            {
                w.WriteStartArray("tile_range_x"); w.WriteNumberValue(tiles.Min(t => t.X)); w.WriteNumberValue(tiles.Max(t => t.X)); w.WriteEndArray();
                w.WriteStartArray("tile_range_y"); w.WriteNumberValue(tiles.Min(t => t.Y)); w.WriteNumberValue(tiles.Max(t => t.Y)); w.WriteEndArray();
            }
            w.WriteStartObject("main_map");
            w.WriteString("note", "default_on mover+weapon geometry (layers 0 and 1), excluding river surfaces, foliage and stair helpers, with centroid inside the area of interest");
            w.WriteStartArray("aoi_x"); w.WriteNumberValue(Stats.AoiX0); w.WriteNumberValue(Stats.AoiX1); w.WriteEndArray();
            w.WriteStartArray("aoi_y"); w.WriteNumberValue(Stats.AoiY0); w.WriteNumberValue(Stats.AoiY1); w.WriteEndArray();
            w.WriteNumber("triangles", st.AoiTris);
            w.WriteNumber("z_min", JsonExt.R(st.AoiZMin));
            w.WriteNumber("z_max", JsonExt.R(st.AoiZMax));
            w.Vec3("lowest_point", st.AoiLowest.X, st.AoiLowest.Y, st.AoiLowest.Z);
            w.WriteString("lowest_ybn", st.AoiLowestYbn);
            w.Vec3("highest_point", st.AoiHighest.X, st.AoiHighest.Y, st.AoiHighest.Z);
            w.WriteString("highest_ybn", st.AoiHighestYbn);
            w.WriteStartArray("z_histogram_20m_from_minus300");
            foreach (var c in st.AoiZHist) w.WriteNumberValue(c);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("totals");
            w.WriteNumber("ybns", src.Ybns.Count);
            w.WriteNumber("ybns_failed", src.Ybns.Count(y => !y.Ok));
            w.WriteNumber("unique_triangles", totalTris + totalPrimTris);
            w.WriteNumber("unique_real_triangles", totalTris);
            w.WriteNumber("unique_primitive_triangles", totalPrimTris);
            w.WriteNumber("mesh_bytes", meshBytes);
            w.WriteNumber("tiles", tiles.Count);
            w.WriteNumber("tile_triangles", tiles.Sum(t => (long)t.Header.NTris));
            w.WriteNumber("tile_vertices", tiles.Sum(t => (long)t.Header.NVerts));
            w.WriteNumber("tile_primitives", tiles.Sum(t => (long)t.Header.NPrims));
            w.WriteNumber("tile_bytes", tileBytes);
            w.WriteString("timing_note", "seconds of the most recent run; *_reused counts files that were up to date and skipped (0 = full rebuild)");
            w.WriteNumber("tiling_seconds", Math.Round(tileSecs, 1));
            w.WriteNumber("tiles_reused", tiles.Count(t => t.Reused));
            if (src.Root.TryGetProperty("phase1_seconds", out var p1)) w.WriteNumber("phase1_seconds", p1.GetDouble());
            if (src.Root.TryGetProperty("phase1_reused", out var p1r)) w.WriteNumber("meshes_reused", p1r.GetInt32());
            w.WriteEndObject();

            w.WriteStartArray("layers");
            string[] lnames = { "mover (plain name): vehicle/ped collision, often also weapon", "hi@: high detail weapon/camera collision", "ma@: material/procedural surface patches (no collision flags)" };
            string[] kinds = { "triangle", "sphere", "capsule", "box", "cylinder" };
            for (int l = 0; l < Stats.Layers; l++)
            {
                w.WriteStartObject();
                w.WriteNumber("layer", l);
                w.WriteString("meaning", lnames[l]);
                w.WriteNumber("ybns", st.LayerYbns[l]);
                w.WriteNumber("groups", st.LayerGroups[l]);
                w.WriteNumber("real_triangles", st.LayerTris[l]);
                w.WriteNumber("primitive_triangles", st.LayerPrimTris[l]);
                w.WriteStartObject("primitives");
                for (int k = 1; k < 5; k++) w.WriteNumber(kinds[k], st.LayerPrims[l, k]);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("child_flag_combos");
            foreach (var kv in st.FlagCombos.OrderByDescending(k => k.Value.tris))
            {
                w.WriteStartObject();
                w.WriteNumber("type_flags", kv.Key.Item1);
                w.WriteNumber("include_flags", kv.Key.Item2);
                w.WriteString("type", BoundFlags.Describe(kv.Key.Item1));
                w.WriteString("include", BoundFlags.Describe(kv.Key.Item2));
                w.WriteNumber("groups", kv.Value.groups);
                w.WriteNumber("triangles", kv.Value.tris);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("material_usage");
            for (int m = 0; m < 256; m++)
            {
                long n = 0; for (int l = 0; l < Stats.Layers; l++) n += st.MatTris[l, m];
                if (n == 0) continue;
                w.WriteStartObject();
                w.WriteNumber("id", m);
                w.WriteString("name", m < matNames.Length ? matNames[m] : "");
                w.WriteStartArray("triangles_by_layer"); for (int l = 0; l < Stats.Layers; l++) w.WriteNumberValue(st.MatTris[l, m]); w.WriteEndArray();
                w.WriteStartArray("area_m2_by_layer"); for (int l = 0; l < Stats.Layers; l++) w.WriteNumberValue(Math.Round(st.MatArea[l, m])); w.WriteEndArray();
                w.WriteStartArray("plan_area_m2_by_layer"); for (int l = 0; l < Stats.Layers; l++) w.WriteNumberValue(Math.Round(st.MatPlanArea[l, m])); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("material_flag_triangles");
            for (int i = 0; i < 16; i++) w.WriteNumber(MatFlags.Names[i], st.MatFlagTris[i]);
            w.WriteEndObject();

            if (src.Root.TryGetProperty("flatten_totals", out var ft)) { w.WritePropertyName("flatten_totals"); ft.WriteTo(w); }
            if (src.Root.TryGetProperty("map_state", out var mst)) { w.WritePropertyName("map_state"); mst.WriteTo(w); }
            if (src.Root.TryGetProperty("map_groups", out var mg)) { w.WritePropertyName("map_groups"); mg.WriteTo(w); }
            if (src.Root.TryGetProperty("time_groups", out var tg)) { w.WritePropertyName("time_groups"); tg.WriteTo(w); }
            if (src.Root.TryGetProperty("story_only_groups", out var sog)) { w.WritePropertyName("story_only_groups"); sog.WriteTo(w); }
            if (src.Root.TryGetProperty("skipped", out var sk)) { w.WritePropertyName("skipped"); sk.WriteTo(w); }

            w.WritePropertyName("ybns");
            src.Root.GetProperty("ybns").WriteTo(w);

            w.WriteStartArray("tiles");
            foreach (var t in tiles)
            {
                w.WriteStartObject();
                w.WriteNumber("x", t.X);
                w.WriteNumber("y", t.Y);
                w.WriteString("file", "tiles/" + Tiler.TileFileName(t.X, t.Y));
                w.WriteNumber("tris", t.Header.NTris);
                w.WriteNumber("verts", t.Header.NVerts);
                w.WriteNumber("prims", t.Header.NPrims);
                w.WriteNumber("groups", t.Header.NGroups);
                w.WriteNumber("z_min", JsonExt.R(t.Header.Min.Z));
                w.WriteNumber("z_max", JsonExt.R(t.Header.Max.Z));
                w.WriteNumber("bytes", t.Bytes);
                w.WriteStartArray("ybns");
                foreach (var id in t.Ybns) w.WriteNumberValue(id);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        Log.Info($"index written: {Path.Combine(colDir, "index.json")}");
        Log.Info($"  unique triangles {totalTris + totalPrimTris:N0} (real {totalTris:N0}, primitive {totalPrimTris:N0}); world z {st.Min.Z:F1}..{st.Max.Z:F1}; main map z {st.AoiZMin:F1}..{st.AoiZMax:F1}");
        Log.Info($"  main map highest point ({st.AoiHighest.X:F0},{st.AoiHighest.Y:F0},{st.AoiHighest.Z:F1}) in {st.AoiHighestYbn}; lowest ({st.AoiLowest.X:F0},{st.AoiLowest.Y:F0},{st.AoiLowest.Z:F1}) in {st.AoiLowestYbn}");
    }

    public static string[] LoadMaterialNames(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("materials").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
