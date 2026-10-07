using System.Text.Json;

namespace GtaExport;

public sealed class PreviewOptions
{
    public string Name = "preview";
    public double X0, Y0, X1, Y1;     // GTA world rectangle
    public double Mpp = 4;            // metres per pixel
    public string Mode = "default";   // default = ungrouped + default_on groups, all = everything, ungrouped = no script groups
    public bool AutoRamp = false;     // colour the height image relative to the region's own z range
    public bool Holes = false;        // also report pixels only covered by switched-off groups (<name>_holes.json)
    public bool DumpZ = false;        // also write the max-height buffer as raw little-endian float32 (<name>_zmax.f32)
    public bool Foliage = true;
    public bool Water = true;
    public string[] ExcludeNamePrefixes = Array.Empty<string>();
}

/// <summary>
/// Top-down verification renders made ONLY from the exported files (index.json, tiles, materials.json, water.json):
/// a max-height image with hill shading and a material-coloured image. North is up, +x is right.
/// </summary>
public static class Preview
{
    sealed class TileRef { public int X, Y; public string File; }

    public static void Render(string outDir, PreviewOptions o, int threads)
    {
        double t0 = Log.Seconds;
        var colDir = Path.Combine(outDir, "collision");
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(colDir, "index.json")));
        var root = index.RootElement;
        float tileSize = root.GetProperty("format").GetProperty("tile_size").GetSingle();

        // per-ybn filter table
        var ybnEls = root.GetProperty("ybns");
        int ny = ybnEls.GetArrayLength();
        var ybnSkip = new bool[ny];
        var ybnOff = new bool[ny];       // not drawn, but tracked for the hole report
        var ybnGroup = new string[ny];
        int yi = 0;
        foreach (var e in ybnEls.EnumerateArray())
        {
            bool skip = e.GetProperty("layer").GetInt32() >= 2;
            bool grouped = e.GetProperty("map_group").ValueKind != JsonValueKind.Null;
            ybnGroup[yi] = grouped ? e.GetProperty("map_group").GetString() : null;
            bool off = o.Mode == "ungrouped" ? grouped : o.Mode == "all" ? false : !e.GetProperty("default_on").GetBoolean();
            if (off) { ybnOff[yi] = !skip; skip = true; }
            var name = e.GetProperty("name").GetString();
            var bare = name.StartsWith("hi@") || name.StartsWith("ma@") ? name.Substring(3) : name;
            foreach (var p in o.ExcludeNamePrefixes) if (bare.StartsWith(p, StringComparison.Ordinal)) { skip = true; ybnOff[yi] = false; }
            ybnSkip[yi++] = skip;
        }

        var tiles = new List<TileRef>();
        foreach (var e in root.GetProperty("tiles").EnumerateArray())
        {
            int tx = e.GetProperty("x").GetInt32(), ty = e.GetProperty("y").GetInt32();
            if ((tx + 1) * tileSize <= o.X0 || tx * tileSize >= o.X1 || (ty + 1) * tileSize <= o.Y0 || ty * tileSize >= o.Y1) continue;
            tiles.Add(new TileRef { X = tx, Y = ty, File = e.GetProperty("file").GetString() });
        }

        int W = (int)Math.Ceiling((o.X1 - o.X0) / o.Mpp), H = (int)Math.Ceiling((o.Y1 - o.Y0) / o.Mpp);
        var zb = new float[W * H];
        Array.Fill(zb, float.NegativeInfinity);
        var mat = new byte[W * H];
        var cls = new byte[W * H]; // 0 none, 1 solid, 2 river water, 3 foliage
        var offYbn = o.Holes ? new int[W * H] : null; // a switched-off ybn that covers the pixel
        if (offYbn != null) Array.Fill(offYbn, -1);
        long trisDrawn = 0;

        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = threads }, tr =>
        {
            var m = Mesh.Read(Path.Combine(colDir, tr.File.Replace('/', Path.DirectorySeparatorChar)));
            // pixel centres owned by this tile: world x in [tx*S,(tx+1)*S), y in [ty*S,(ty+1)*S)
            int pxMin = Math.Max(0, (int)Math.Ceiling((tr.X * tileSize - o.X0) / o.Mpp - 0.5));
            int pxMax = Math.Min(W - 1, (int)Math.Ceiling(((tr.X + 1) * tileSize - o.X0) / o.Mpp - 0.5) - 1);
            int pyMin = Math.Max(0, (int)Math.Floor((o.Y1 - (tr.Y + 1) * tileSize) / o.Mpp - 0.5) + 1);
            int pyMax = Math.Min(H - 1, (int)Math.Floor((o.Y1 - tr.Y * tileSize) / o.Mpp - 0.5));
            if (pxMin > pxMax || pyMin > pyMax) return;
            long drawn = 0;
            var v = m.Verts; var t = m.Tris; var at = m.Attrs;
            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                bool offOnly = false;
                if (ybnSkip[g.YbnId])
                {
                    if (offYbn == null || !ybnOff[g.YbnId]) continue;
                    offOnly = true;
                }
                if (g.TypeFlags == BoundFlags.MAP_STAIRS) continue;
                byte c = 1;
                if ((g.TypeFlags & BoundFlags.MAP_RIVER) != 0) c = 2;
                else if ((g.TypeFlags & BoundFlags.FOLIAGE) != 0) { if (!o.Foliage) continue; c = 3; }
                int ybnId = (int)g.YbnId;
                for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                {
                    ref readonly var a = ref v[t[i * 3]]; ref readonly var b = ref v[t[i * 3 + 1]]; ref readonly var d = ref v[t[i * 3 + 2]];
                    // pixel space: pixel centres at integer coordinates
                    double ax = (a.X - o.X0) / o.Mpp - 0.5, ay = (o.Y1 - a.Y) / o.Mpp - 0.5;
                    double bx = (b.X - o.X0) / o.Mpp - 0.5, by = (o.Y1 - b.Y) / o.Mpp - 0.5;
                    double cx = (d.X - o.X0) / o.Mpp - 0.5, cy = (o.Y1 - d.Y) / o.Mpp - 0.5;
                    double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                    if (Math.Abs(area) < 1e-12) continue; // vertical in plan view
                    int x0 = Math.Max(pxMin, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx))));
                    int x1 = Math.Min(pxMax, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx))));
                    int y0 = Math.Max(pyMin, (int)Math.Ceiling(Math.Min(ay, Math.Min(by, cy))));
                    int y1 = Math.Min(pyMax, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy))));
                    byte mid = at[i].Material;
                    bool hit = false;
                    double inv = 1.0 / area;
                    for (int py = y0; py <= y1; py++)
                    {
                        for (int px = x0; px <= x1; px++)
                        {
                            double w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                            double w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                            double w2 = 1.0 - w0 - w1;
                            if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                            hit = true;
                            int k = py * W + px;
                            if (offOnly) { offYbn[k] = ybnId; continue; }
                            float z = (float)(w0 * a.Z + w1 * b.Z + w2 * d.Z);
                            if (z > zb[k]) { zb[k] = z; mat[k] = mid; cls[k] = c; }
                        }
                    }
                    if (!hit && !offOnly)
                    {
                        // small triangle that covers no pixel centre: splat its centroid so fine detail still shows
                        int px = (int)Math.Round((ax + bx + cx) / 3), py = (int)Math.Round((ay + by + cy) / 3);
                        if (px >= pxMin && px <= pxMax && py >= pyMin && py <= pyMax)
                        {
                            float z = (a.Z + b.Z + d.Z) / 3f;
                            int k = py * W + px;
                            if (z > zb[k]) { zb[k] = z; mat[k] = mid; cls[k] = c; }
                        }
                    }
                    if (!offOnly) drawn++;
                }
            }
            Interlocked.Add(ref trisDrawn, drawn);
        });

        // water quads (ocean, lakes) from the exported water.json
        var waterZ = new float[W * H];
        Array.Fill(waterZ, float.NaN);
        int waterQuads = 0;
        var waterPath = Path.Combine(outDir, "water", "water.json");
        if (o.Water && File.Exists(waterPath))
        {
            using var wd = JsonDocument.Parse(File.ReadAllText(waterPath));
            if (wd.RootElement.TryGetProperty("main", out var mainW))
            {
                foreach (var q in mainW.GetProperty("quads").EnumerateArray())
                {
                    float z = q.TryGetProperty("z", out var ze) ? ze.GetSingle() : 0f;
                    var pts = q.GetProperty("corners").EnumerateArray().Select(p => (x: (p[0].GetDouble() - o.X0) / o.Mpp - 0.5, y: (o.Y1 - p[1].GetDouble()) / o.Mpp - 0.5)).ToArray();
                    int x0 = Math.Max(0, (int)Math.Ceiling(pts.Min(p => p.x))), x1 = Math.Min(W - 1, (int)Math.Floor(pts.Max(p => p.x)));
                    int y0 = Math.Max(0, (int)Math.Ceiling(pts.Min(p => p.y))), y1 = Math.Min(H - 1, (int)Math.Floor(pts.Max(p => p.y)));
                    if (x0 > x1 || y0 > y1) continue;
                    waterQuads++;
                    for (int py = y0; py <= y1; py++)
                        for (int px = x0; px <= x1; px++)
                        {
                            if (pts.Length == 3 && !InTri(px, py, pts)) continue;
                            int k = py * W + px;
                            if (float.IsNaN(waterZ[k]) || z > waterZ[k]) waterZ[k] = z;
                        }
                }
            }
        }

        // range for the relative height ramp (ignore the extremes)
        float autoLo = 0, autoHi = 1;
        if (o.AutoRamp)
        {
            var vals = zb.Where(z => !float.IsNegativeInfinity(z)).ToArray();
            if (vals.Length > 0)
            {
                Array.Sort(vals);
                autoLo = vals[(int)(vals.Length * 0.005)];
                autoHi = vals[Math.Min(vals.Length - 1, (int)(vals.Length * 0.9995))];
                if (autoHi <= autoLo) autoHi = autoLo + 1;
            }
        }

        // colours
        var matRgb = LoadMaterialColours(Path.Combine(outDir, "materials.json"));
        var himg = new byte[W * H * 3];
        var mimg = new byte[W * H * 3];
        long covered = 0, waterPx = 0;
        float zmin = float.MaxValue, zmax = float.MinValue;
        Parallel.For(0, H, py =>
        {
            for (int px = 0; px < W; px++)
            {
                int k = py * W + px;
                float z = zb[k];
                bool has = !float.IsNegativeInfinity(z);
                double shade = 1.0;
                if (has)
                {
                    // hill shading from the height buffer (light from the north-west, 45 degrees up)
                    float zl = px > 0 && !float.IsNegativeInfinity(zb[k - 1]) ? zb[k - 1] : z;
                    float zr = px < W - 1 && !float.IsNegativeInfinity(zb[k + 1]) ? zb[k + 1] : z;
                    float zu = py > 0 && !float.IsNegativeInfinity(zb[k - W]) ? zb[k - W] : z;
                    float zd = py < H - 1 && !float.IsNegativeInfinity(zb[k + W]) ? zb[k + W] : z;
                    double dzdx = (zr - zl) / (2 * o.Mpp);       // +x east
                    double dzdy = (zu - zd) / (2 * o.Mpp);       // +y north (row above is further north)
                    double nx = -dzdx, nyv = -dzdy, nz = 1.0;
                    double nl = Math.Sqrt(nx * nx + nyv * nyv + nz * nz);
                    const double lx = -0.5, ly = 0.5, lz = 0.7071; // direction towards the light
                    double lam = (nx * lx + nyv * ly + nz * lz) / nl;
                    shade = Math.Clamp(0.25 + 1.05 * lam, 0.2, 1.3);
                }
                int o3 = k * 3;
                if (has)
                {
                    var hc = o.AutoRamp ? AutoColour(z, autoLo, autoHi) : HeightColour(z);
                    himg[o3] = Sh(hc.r, shade); himg[o3 + 1] = Sh(hc.g, shade); himg[o3 + 2] = Sh(hc.b, shade);
                }
                else { himg[o3] = 8; himg[o3 + 1] = 8; himg[o3 + 2] = 14; }

                // material view with water overlay
                float wz = waterZ[k];
                bool underWater = !float.IsNaN(wz) && (!has || z < wz);
                (int r, int g, int b) mc = has ? matRgb[mat[k]] : (0, 0, 0);
                double ms = shade;
                if (has && cls[k] == 3) mc = (mc.r * 3 / 4, mc.g * 3 / 4 + 20, mc.b * 3 / 4);
                if (underWater)
                {
                    double depth = has ? wz - z : 1000;
                    double al = has ? Math.Clamp(0.55 + depth / 60.0, 0.55, 0.95) : 1.0;
                    (double r, double g, double b) wc = depth > 60 ? (18, 52, 120) : (40, 110, 200);
                    mc = ((int)(mc.r * ms * (1 - al) + wc.r * al), (int)(mc.g * ms * (1 - al) + wc.g * al), (int)(mc.b * ms * (1 - al) + wc.b * al));
                    ms = 1.0;
                }
                else if (has && cls[k] == 2) { mc = (70, 150, 225); }
                mimg[o3] = Sh(mc.r, ms); mimg[o3 + 1] = Sh(mc.g, ms); mimg[o3 + 2] = Sh(mc.b, ms);
            }
        });
        for (int k = 0; k < zb.Length; k++)
        {
            if (float.IsNegativeInfinity(zb[k])) { if (!float.IsNaN(waterZ[k])) waterPx++; continue; }
            covered++;
            if (zb[k] < zmin) zmin = zb[k];
            if (zb[k] > zmax) zmax = zb[k];
            if (!float.IsNaN(waterZ[k]) && zb[k] < waterZ[k]) waterPx++;
        }

        var pdir = Path.Combine(outDir, "preview");
        Directory.CreateDirectory(pdir);

        if (offYbn != null)
        {
            // pixels that nothing in the drawn set covers but a switched-off ybn would: candidates for wrong defaults
            var perGroup = new Dictionary<string, long>();
            for (int k = 0; k < zb.Length; k++)
            {
                if (!float.IsNegativeInfinity(zb[k]) || offYbn[k] < 0) continue;
                var gname = ybnGroup[offYbn[k]] ?? "(ungrouped)";
                perGroup[gname] = perGroup.GetValueOrDefault(gname) + 1;
            }
            double px2 = o.Mpp * o.Mpp;
            Paths.WriteJson(Path.Combine(pdir, o.Name + "_holes.json"), w =>
            {
                w.WriteStartObject();
                w.WriteString("about", "plan-view area (m2) not covered by any drawn bound that a switched-off map group would cover; large values suggest the group should be on by default");
                w.WriteString("mode", o.Mode);
                w.WriteNumber("metres_per_pixel", o.Mpp);
                w.WriteStartArray("groups");
                foreach (var kv in perGroup.OrderByDescending(k => k.Value))
                {
                    w.WriteStartObject();
                    w.WriteString("map_group", kv.Key);
                    w.WriteNumber("uncovered_m2", kv.Value * px2);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            Log.Info($"holes: {perGroup.Count} switched-off groups would cover otherwise empty pixels; top: " +
                     string.Join(", ", perGroup.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}={k.Value * px2:F0}m2")));
        }

        if (o.DumpZ)
        {
            Paths.AtomicWrite(Path.Combine(pdir, o.Name + "_zmax.f32"), s => s.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(zb.AsSpan())));
        }

        var hp = Path.Combine(pdir, o.Name + "_height.png");
        var mp = Path.Combine(pdir, o.Name + "_material.png");
        Png.WriteRgb(hp, W, H, himg);
        Png.WriteRgb(mp, W, H, mimg);
        double secs = Log.Seconds - t0;
        Paths.WriteJson(Path.Combine(pdir, o.Name + ".json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("name", o.Name);
            w.WriteStartArray("region_gta_x0_y0_x1_y1"); w.WriteNumberValue(o.X0); w.WriteNumberValue(o.Y0); w.WriteNumberValue(o.X1); w.WriteNumberValue(o.Y1); w.WriteEndArray();
            w.WriteNumber("metres_per_pixel", o.Mpp);
            w.WriteNumber("width", W);
            w.WriteNumber("height", H);
            w.WriteString("orientation", "north up; pixel (px,py) centre = gta (x0 + (px+0.5)*mpp, y1 - (py+0.5)*mpp)");
            w.WriteString("mode", o.Mode);
            w.WriteBoolean("auto_height_ramp", o.AutoRamp);
            if (o.AutoRamp) { w.WriteNumber("ramp_z_low", JsonExt.R(autoLo)); w.WriteNumber("ramp_z_high", JsonExt.R(autoHi)); }
            w.WriteBoolean("foliage", o.Foliage);
            w.WriteNumber("tiles_read", tiles.Count);
            w.WriteNumber("triangles_rasterised", trisDrawn);
            w.WriteNumber("covered_pixels", covered);
            w.WriteNumber("covered_fraction", Math.Round((double)covered / (W * (double)H), 4));
            w.WriteNumber("water_pixels", waterPx);
            w.WriteNumber("water_quads_drawn", waterQuads);
            w.WriteNumber("z_min", JsonExt.R(zmin));
            w.WriteNumber("z_max", JsonExt.R(zmax));
            w.WriteNumber("seconds", Math.Round(secs, 1));
            if (o.DumpZ)
            {
                w.WriteString("zmax_f32", o.Name + "_zmax.f32");
                w.WriteString("zmax_f32_note", "width*height little-endian float32, row 0 = north, -inf where nothing was hit; highest default_on surface per pixel centre");
            }
            w.WriteString("height_png", o.Name + "_height.png");
            w.WriteString("material_png", o.Name + "_material.png");
            w.WriteEndObject();
        });
        Log.Info($"preview '{o.Name}': {W}x{H} px at {o.Mpp} m/px, {tiles.Count} tiles, {trisDrawn:N0} triangles, coverage {100.0 * covered / (W * (double)H):F1}%, z {zmin:F1}..{zmax:F1}, {secs:F1}s");
    }

    /// <summary>Relative ramp for city-scale renders: dark blue (low) to yellow to white (high), square-root spaced.</summary>
    static (int r, int g, int b) AutoColour(float z, float lo, float hi)
    {
        double t = Math.Sqrt(Math.Clamp((z - lo) / (hi - lo), 0, 1));
        (double t, int r, int g, int b)[] ramp = { (0, 40, 60, 110), (0.2, 60, 120, 150), (0.4, 90, 170, 120), (0.6, 210, 200, 100), (0.8, 230, 130, 70), (1, 255, 250, 245) };
        for (int i = 1; i < ramp.Length; i++)
        {
            if (t <= ramp[i].t)
            {
                double f = (t - ramp[i - 1].t) / (ramp[i].t - ramp[i - 1].t);
                return ((int)(ramp[i - 1].r + f * (ramp[i].r - ramp[i - 1].r)), (int)(ramp[i - 1].g + f * (ramp[i].g - ramp[i - 1].g)), (int)(ramp[i - 1].b + f * (ramp[i].b - ramp[i - 1].b)));
            }
        }
        return (255, 250, 245);
    }

    static bool InTri(int px, int py, (double x, double y)[] p)
    {
        double d1 = (px - p[1].x) * (p[0].y - p[1].y) - (p[0].x - p[1].x) * (py - p[1].y);
        double d2 = (px - p[2].x) * (p[1].y - p[2].y) - (p[1].x - p[2].x) * (py - p[2].y);
        double d3 = (px - p[0].x) * (p[2].y - p[0].y) - (p[2].x - p[0].x) * (py - p[0].y);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    static byte Sh(int c, double s) => (byte)Math.Clamp((int)(c * s), 0, 255);

    static readonly (float z, int r, int g, int b)[] Ramp =
    {
        (-250, 6, 10, 40), (-100, 14, 40, 110), (-20, 40, 110, 190), (-0.5f, 120, 190, 235),
        (0.5f, 215, 205, 160), (10, 110, 160, 90), (60, 70, 130, 70), (150, 150, 165, 95), (300, 190, 165, 110),
        (450, 160, 120, 90), (600, 140, 115, 110), (750, 215, 215, 215), (850, 255, 255, 255),
    };

    static (int r, int g, int b) HeightColour(float z)
    {
        if (z <= Ramp[0].z) return (Ramp[0].r, Ramp[0].g, Ramp[0].b);
        for (int i = 1; i < Ramp.Length; i++)
        {
            if (z <= Ramp[i].z)
            {
                float f = (z - Ramp[i - 1].z) / (Ramp[i].z - Ramp[i - 1].z);
                return ((int)(Ramp[i - 1].r + f * (Ramp[i].r - Ramp[i - 1].r)), (int)(Ramp[i - 1].g + f * (Ramp[i].g - Ramp[i - 1].g)), (int)(Ramp[i - 1].b + f * (Ramp[i].b - Ramp[i - 1].b)));
            }
        }
        var l = Ramp[^1];
        return (l.r, l.g, l.b);
    }

    static (int r, int g, int b)[] LoadMaterialColours(string path)
    {
        var res = new (int, int, int)[256];
        for (int i = 0; i < 256; i++) res[i] = (150, 150, 155);
        if (!File.Exists(path)) return res;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.GetProperty("materials").EnumerateArray())
        {
            int id = e.GetProperty("id").GetInt32();
            var c = e.GetProperty("preview_rgb");
            if (id >= 0 && id < 256) res[id] = (c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32());
        }
        return res;
    }
}
