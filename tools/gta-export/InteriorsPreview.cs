using System.Text.Json;

namespace GtaExport;

/// <summary>
/// Verification renders of the interior layer, made ONLY from exported files (collision/ and interiors/ tiles,
/// index.json, portals.json, tracks.json): plan views of the transit / tunnel / parking interiors over a faint
/// street map, and vertical cross-sections along a train track showing the street above and the tunnel below.
/// </summary>
public static class InteriorsPreview
{
    // ------------------------------------------------------------------ canvas with a 5x7 bitmap font

    public sealed class Canvas
    {
        public readonly int W, H;
        public readonly byte[] Rgb;
        public Canvas(int w, int h) { W = w; H = h; Rgb = new byte[w * h * 3]; }

        public void Set(int x, int y, (int r, int g, int b) c)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            int o = (y * W + x) * 3;
            Rgb[o] = (byte)c.r; Rgb[o + 1] = (byte)c.g; Rgb[o + 2] = (byte)c.b;
        }

        public void Blend(int x, int y, (int r, int g, int b) c, double a)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
            int o = (y * W + x) * 3;
            Rgb[o] = (byte)(Rgb[o] * (1 - a) + c.r * a); Rgb[o + 1] = (byte)(Rgb[o + 1] * (1 - a) + c.g * a); Rgb[o + 2] = (byte)(Rgb[o + 2] * (1 - a) + c.b * a);
        }

        public void Fill(int x0, int y0, int x1, int y1, (int r, int g, int b) c, double a = 1)
        {
            for (int y = Math.Max(0, y0); y <= Math.Min(H - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(W - 1, x1); x++)
                    if (a >= 1) Set(x, y, c); else Blend(x, y, c, a);
        }

        public void Line(double x0, double y0, double x1, double y1, (int r, int g, int b) c, double a = 1)
        {
            // clip coarsely, then DDA
            if (Math.Max(x0, x1) < 0 || Math.Min(x0, x1) > W - 1 || Math.Max(y0, y1) < 0 || Math.Min(y0, y1) > H - 1) return;
            double dx = x1 - x0, dy = y1 - y0;
            int n = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (n > 200000) return;
            if (n == 0) { Put((int)Math.Round(x0), (int)Math.Round(y0)); return; }
            for (int i = 0; i <= n; i++) Put((int)Math.Round(x0 + dx * i / n), (int)Math.Round(y0 + dy * i / n));
            void Put(int x, int y) { if (a >= 1) Set(x, y, c); else Blend(x, y, c, a); }
        }

        // classic 5x7 column font, ASCII 32..95 (lower case is drawn as upper case)
        static readonly byte[] Font =
        {
            0x00,0x00,0x00,0x00,0x00, 0x00,0x00,0x5F,0x00,0x00, 0x00,0x07,0x00,0x07,0x00, 0x14,0x7F,0x14,0x7F,0x14,
            0x24,0x2A,0x7F,0x2A,0x12, 0x23,0x13,0x08,0x64,0x62, 0x36,0x49,0x55,0x22,0x50, 0x00,0x05,0x03,0x00,0x00,
            0x00,0x1C,0x22,0x41,0x00, 0x00,0x41,0x22,0x1C,0x00, 0x08,0x2A,0x1C,0x2A,0x08, 0x08,0x08,0x3E,0x08,0x08,
            0x00,0x50,0x30,0x00,0x00, 0x08,0x08,0x08,0x08,0x08, 0x00,0x60,0x60,0x00,0x00, 0x20,0x10,0x08,0x04,0x02,
            0x3E,0x51,0x49,0x45,0x3E, 0x00,0x42,0x7F,0x40,0x00, 0x42,0x61,0x51,0x49,0x46, 0x21,0x41,0x45,0x4B,0x31,
            0x18,0x14,0x12,0x7F,0x10, 0x27,0x45,0x45,0x45,0x39, 0x3C,0x4A,0x49,0x49,0x30, 0x01,0x71,0x09,0x05,0x03,
            0x36,0x49,0x49,0x49,0x36, 0x06,0x49,0x49,0x29,0x1E, 0x00,0x36,0x36,0x00,0x00, 0x00,0x56,0x36,0x00,0x00,
            0x00,0x08,0x14,0x22,0x41, 0x14,0x14,0x14,0x14,0x14, 0x41,0x22,0x14,0x08,0x00, 0x02,0x01,0x51,0x09,0x06,
            0x32,0x49,0x79,0x41,0x3E, 0x7E,0x11,0x11,0x11,0x7E, 0x7F,0x49,0x49,0x49,0x36, 0x3E,0x41,0x41,0x41,0x22,
            0x7F,0x41,0x41,0x22,0x1C, 0x7F,0x49,0x49,0x49,0x41, 0x7F,0x09,0x09,0x01,0x01, 0x3E,0x41,0x41,0x51,0x32,
            0x7F,0x08,0x08,0x08,0x7F, 0x00,0x41,0x7F,0x41,0x00, 0x20,0x40,0x41,0x3F,0x01, 0x7F,0x08,0x14,0x22,0x41,
            0x7F,0x40,0x40,0x40,0x40, 0x7F,0x02,0x04,0x02,0x7F, 0x7F,0x04,0x08,0x10,0x7F, 0x3E,0x41,0x41,0x41,0x3E,
            0x7F,0x09,0x09,0x09,0x06, 0x3E,0x41,0x51,0x21,0x5E, 0x7F,0x09,0x19,0x29,0x46, 0x46,0x49,0x49,0x49,0x31,
            0x01,0x01,0x7F,0x01,0x01, 0x3F,0x40,0x40,0x40,0x3F, 0x1F,0x20,0x40,0x20,0x1F, 0x7F,0x20,0x18,0x20,0x7F,
            0x63,0x14,0x08,0x14,0x63, 0x03,0x04,0x78,0x04,0x03, 0x61,0x51,0x49,0x45,0x43, 0x00,0x00,0x7F,0x41,0x41,
            0x02,0x04,0x08,0x10,0x20, 0x41,0x41,0x7F,0x00,0x00, 0x04,0x02,0x01,0x02,0x04, 0x40,0x40,0x40,0x40,0x40,
        };

        public static int TextWidth(string s, int scale = 1) => s.Length * 6 * scale;

        /// <summary>Text with a dark halo so it stays readable on any background.</summary>
        public void Text(int x, int y, string s, (int r, int g, int b) c, int scale = 1, bool halo = true)
        {
            if (halo)
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (dx != 0 || dy != 0) Glyphs(x + dx, y + dy, s, (0, 0, 0), scale);
            Glyphs(x, y, s, c, scale);
        }

        void Glyphs(int x, int y, string s, (int r, int g, int b) c, int scale)
        {
            foreach (var ch0 in s)
            {
                char ch = char.ToUpperInvariant(ch0);
                if (ch < 32 || ch > 95) ch = '?';
                int o = (ch - 32) * 5;
                for (int col = 0; col < 5; col++)
                {
                    byte bits = Font[o + col];
                    for (int row = 0; row < 7; row++)
                        if ((bits & (1 << row)) != 0)
                            for (int sy = 0; sy < scale; sy++) for (int sx = 0; sx < scale; sx++) Set(x + col * scale + sx, y + row * scale + sy, c);
                }
                x += 6 * scale;
            }
        }
    }

    // ------------------------------------------------------------------ shared bits

    public static readonly (int r, int g, int b)[] CatColour =
    {
        (170, 170, 190),   // other
        (0, 215, 255),     // transit
        (255, 150, 30),    // road tunnel
        (90, 235, 90),     // parking
        (190, 110, 235),   // building interior
    };

    sealed class Layer
    {
        public string Dir;
        public float TileSize;
        public Dictionary<(int, int), string> Tiles = new();
        public bool[] YbnSkip;       // layer >= 2 or default off
        public bool[] YbnDetached;
        public byte[] YbnCat;
        public bool[] RoadMat = new bool[256];

        public static Layer Load(string outDir, string name, bool includeOff)
        {
            var l = new Layer { Dir = Path.Combine(outDir, name) };
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(l.Dir, "index.json")));
            var root = doc.RootElement;
            l.TileSize = root.GetProperty("format").GetProperty("tile_size").GetSingle();
            var cats = root.GetProperty("format").TryGetProperty("categories", out var ce) ? ce.EnumerateArray().Select(e => e.GetString()).ToList() : new List<string>();
            var ye = root.GetProperty("ybns");
            int n = ye.GetArrayLength();
            l.YbnSkip = new bool[n]; l.YbnDetached = new bool[n]; l.YbnCat = new byte[n];
            int i = 0;
            foreach (var e in ye.EnumerateArray())
            {
                l.YbnSkip[i] = e.GetProperty("layer").GetInt32() >= 2 || (!includeOff && !e.GetProperty("default_on").GetBoolean());
                if (e.TryGetProperty("detached", out var de)) l.YbnDetached[i] = de.GetBoolean();
                if (e.TryGetProperty("category", out var c)) l.YbnCat[i] = (byte)Math.Max(0, cats.IndexOf(c.GetString()));
                i++;
            }
            foreach (var e in root.GetProperty("tiles").EnumerateArray())
                l.Tiles[(e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32())] = e.GetProperty("file").GetString();
            var mp = Path.Combine(outDir, "materials.json");
            if (File.Exists(mp))
            {
                using var md = JsonDocument.Parse(File.ReadAllText(mp));
                foreach (var e in md.RootElement.GetProperty("materials").EnumerateArray())
                {
                    int id = e.GetProperty("id").GetInt32();
                    if (id >= 0 && id < 256) l.RoadMat[id] = e.GetProperty("class").GetString() == "road";
                }
            }
            return l;
        }

        public IEnumerable<(int x, int y, string file)> TilesIn(double x0, double y0, double x1, double y1)
        {
            foreach (var kv in Tiles)
            {
                var (tx, ty) = kv.Key;
                if ((tx + 1) * TileSize <= x0 || tx * TileSize >= x1 || (ty + 1) * TileSize <= y0 || ty * TileSize >= y1) continue;
                yield return (tx, ty, Path.Combine(Dir, kv.Value.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        public static bool Solid(in GroupRec g) =>
            (g.TypeFlags & (BoundFlags.MAP_RIVER | BoundFlags.FOLIAGE)) == 0 && g.TypeFlags != BoundFlags.MAP_STAIRS &&
            ((g.TypeFlags & BoundFlags.MAP_WEAPON) != 0 || (g.TypeFlags & (BoundFlags.MAP_DYNAMIC | BoundFlags.MAP_VEHICLE)) == (BoundFlags.MAP_DYNAMIC | BoundFlags.MAP_VEHICLE));
    }

    sealed class Track { public string Name, Config; public bool Metro; public double[][] Nodes; }

    static List<Track> LoadTracks(string intDir)
    {
        var res = new List<Track>();
        var p = Path.Combine(intDir, "tracks.json");
        if (!File.Exists(p)) return res;
        using var doc = JsonDocument.Parse(File.ReadAllText(p));
        foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateArray())
            res.Add(new Track
            {
                Name = t.GetProperty("name").GetString(), Config = t.GetProperty("train_config").GetString(), Metro = t.GetProperty("is_metro").GetBoolean(),
                Nodes = t.GetProperty("nodes").EnumerateArray().Select(n => new[] { n[0].GetDouble(), n[1].GetDouble(), n[2].GetDouble(), n[3].GetDouble() }).ToArray(),
            });
        return res;
    }

    // ------------------------------------------------------------------ plan view

    public sealed class PlanOptions
    {
        public string Name = "plan";
        public double X0, Y0, X1, Y1, Mpp = 1;
        public bool Buildings;      // also draw building_interior / other
        public bool Detached;       // also draw detached interiors (where they are placed)
        public bool Labels = true;
    }

    public static void RenderPlan(string outDir, PlanOptions o, int threads)
    {
        double t0 = Log.Seconds;
        var intDir = Path.Combine(outDir, "interiors");
        var st = Layer.Load(outDir, "collision", false);
        var it = Layer.Load(outDir, "interiors", false);
        int W = (int)Math.Ceiling((o.X1 - o.X0) / o.Mpp), H = (int)Math.Ceiling((o.Y1 - o.Y0) / o.Mpp);

        // ---- background: highest default-on solid surface per pixel, roads brighter
        var zb = new float[W * H]; Array.Fill(zb, float.NegativeInfinity);
        var road = new bool[W * H];
        Parallel.ForEach(st.TilesIn(o.X0, o.Y0, o.X1, o.Y1), new ParallelOptions { MaxDegreeOfParallelism = threads }, tr =>
        {
            var m = Mesh.Read(tr.file);
            Clip(tr.x, tr.y, st.TileSize, o, W, H, out int pxMin, out int pxMax, out int pyMin, out int pyMax);
            if (pxMin > pxMax || pyMin > pyMax) return;
            var v = m.Verts; var t = m.Tris; var at = m.Attrs;
            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                if (st.YbnSkip[g.YbnId] || !Layer.Solid(g)) continue;
                var sink = new GroundSink { Zb = zb, Road = road };
                for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                {
                    sink.IsRoad = st.RoadMat[at[i].Material];
                    Raster(v[t[i * 3]], v[t[i * 3 + 1]], v[t[i * 3 + 2]], o, W, pxMin, pxMax, pyMin, pyMax, ref sink);
                }
            }
        });

        // ---- interior coverage per pixel: category of the topmost drawn category by priority, and lowest z
        var cat = new byte[W * H];           // 0 none, else category index + 1
        var det = new bool[W * H];
        long trisDrawn = 0, trisDetached = 0;
        var perCat = new long[5];
        int[] prio = { 1, 5, 4, 3, 2 }; // other, transit, road tunnel, parking, building
        Parallel.ForEach(it.TilesIn(o.X0, o.Y0, o.X1, o.Y1), new ParallelOptions { MaxDegreeOfParallelism = threads }, tr =>
        {
            var m = Mesh.Read(tr.file);
            Clip(tr.x, tr.y, it.TileSize, o, W, H, out int pxMin, out int pxMax, out int pyMin, out int pyMax);
            if (pxMin > pxMax || pyMin > pyMax) return;
            var v = m.Verts; var t = m.Tris;
            long drawn = 0, detached = 0;
            var pc = new long[5];
            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                if (it.YbnSkip[g.YbnId] || !Layer.Solid(g)) continue;
                int c = (g.Info >> 4) & 7;
                bool d = (g.Info & 0x80) != 0;
                if (d) { detached += g.NTris; if (!o.Detached) continue; }
                if (!o.Buildings && (c == InteriorsExport.CatBuilding || c == InteriorsExport.CatOther)) continue;
                var sink = new InteriorSink { Cat = cat, Det = det, Prio = prio, C = c, Code = (byte)(c + 1), D = d };
                for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                {
                    Raster(v[t[i * 3]], v[t[i * 3 + 1]], v[t[i * 3 + 2]], o, W, pxMin, pxMax, pyMin, pyMax, ref sink);
                    drawn++;
                }
                pc[c] += g.NTris;
            }
            Interlocked.Add(ref trisDrawn, drawn); Interlocked.Add(ref trisDetached, detached);
            for (int c = 0; c < 5; c++) Interlocked.Add(ref perCat[c], pc[c]);
        });

        // ---- compose
        var cv = new Canvas(W, H);
        Parallel.For(0, H, py =>
        {
            for (int px = 0; px < W; px++)
            {
                int k = py * W + px;
                float z = zb[k];
                (int r, int g, int b) c;
                if (float.IsNegativeInfinity(z)) c = (8, 10, 18);
                else
                {
                    float zl = px > 0 && !float.IsNegativeInfinity(zb[k - 1]) ? zb[k - 1] : z;
                    float zr = px < W - 1 && !float.IsNegativeInfinity(zb[k + 1]) ? zb[k + 1] : z;
                    float zu = py > 0 && !float.IsNegativeInfinity(zb[k - W]) ? zb[k - W] : z;
                    float zd = py < H - 1 && !float.IsNegativeInfinity(zb[k + W]) ? zb[k + W] : z;
                    double nx = -(zr - zl) / (2 * o.Mpp), ny = -(zu - zd) / (2 * o.Mpp);
                    double lam = (nx * -0.5 + ny * 0.5 + 0.7071) / Math.Sqrt(nx * nx + ny * ny + 1);
                    double sh = Math.Clamp(0.55 + 0.75 * lam, 0.45, 1.25);
                    c = z < 0.3f && !road[k] ? (14, 26, 48) : road[k] ? ((int)(86 * sh), (int)(88 * sh), (int)(96 * sh)) : ((int)(40 * sh), (int)(43 * sh), (int)(50 * sh));
                }
                cv.Set(px, py, c);
            }
        });

        // train tracks under the interior overlay: where the line stays bright the track is outside every interior
        var tracks = LoadTracks(intDir);
        foreach (var tk in tracks)
        {
            var col = tk.Metro ? (245, 245, 245) : (170, 140, 90);
            for (int i = 1; i < tk.Nodes.Length; i++)
            {
                var a = tk.Nodes[i - 1]; var b = tk.Nodes[i];
                cv.Line((a[0] - o.X0) / o.Mpp - 0.5, (o.Y1 - a[1]) / o.Mpp - 0.5, (b[0] - o.X0) / o.Mpp - 0.5, (o.Y1 - b[1]) / o.Mpp - 0.5, col);
            }
        }
        for (int k = 0; k < cat.Length; k++)
        {
            if (cat[k] == 0) continue;
            var c = det[k] ? (150, 40, 40) : CatColour[cat[k] - 1];
            cv.Blend(k % W, k / W, c, 0.78);
        }

        // entrances that meet the outside world (priority categories)
        int entrances = 0;
        var labels = new List<(double x, double y, string text)>();
        var pp = Path.Combine(intDir, "portals.json");
        if (File.Exists(pp))
        {
            using var pd = JsonDocument.Parse(File.ReadAllText(pp));
            foreach (var p in pd.RootElement.GetProperty("list").EnumerateArray())
            {
                if (p.GetProperty("kind").GetString() != "entrance" || p.GetProperty("detached").GetBoolean()) continue;
                if (p.GetProperty("opens_to").GetString() != "outside") continue;
                var c = p.GetProperty("category").GetString();
                if (!o.Buildings && c != "transit" && c != "road_tunnel" && c != "parking") continue;
                var ce = p.GetProperty("centre");
                double x = (ce[0].GetDouble() - o.X0) / o.Mpp - 0.5, y = (o.Y1 - ce[1].GetDouble()) / o.Mpp - 0.5;
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                entrances++;
                int r = o.Mpp <= 1.5 ? 3 : 2;
                cv.Fill((int)x - r, (int)y - r, (int)x + r, (int)y + r, (0, 0, 0));
                cv.Fill((int)x - r + 1, (int)y - r + 1, (int)x + r - 1, (int)y + r - 1, (255, 240, 60));
            }
        }
        // station names: the metro station entrance interiors
        using (var idx = JsonDocument.Parse(File.ReadAllText(Path.Combine(intDir, "index.json"))))
        {
            foreach (var e in idx.RootElement.GetProperty("interiors").EnumerateArray())
            {
                if (e.GetProperty("kind").GetString() != "metro_station_entrance") continue;
                var pos = e.GetProperty("position");
                labels.Add((pos[0].GetDouble(), pos[1].GetDouble(), e.GetProperty("name").GetString()));
            }
        }
        if (o.Labels)
            foreach (var (x, y, text) in labels)
            {
                int px = (int)((x - o.X0) / o.Mpp), py = (int)((o.Y1 - y) / o.Mpp);
                if (px < 0 || py < 0 || px >= W || py >= H) continue;
                cv.Text(Math.Min(px + 8, W - Canvas.TextWidth(text) - 2), py - 12, text, (255, 255, 255));
            }

        // legend
        int lx = 8, ly = 8, sc = W >= 1600 ? 2 : 1, lh = 10 * sc;
        string title = $"{o.Name}: GTA x {o.X0:F0}..{o.X1:F0}, y {o.Y0:F0}..{o.Y1:F0}, {o.Mpp:0.##} m/px, north up";
        var rows = new List<((int, int, int) c, string s)>
        {
            (CatColour[1], "transit: metro stations, metro and rail tunnels (interior layer)"),
            (CatColour[2], "road tunnels (interior layer)"),
            (CatColour[3], "underground / multi-storey car parks, garages (interior layer)"),
        };
        if (o.Buildings) { rows.Add((CatColour[4], "building interiors")); rows.Add((CatColour[0], "other interiors")); }
        if (o.Detached) rows.Add(((150, 40, 40), "detached interiors (teleport / orphaned)"));
        rows.Add(((255, 240, 60), "entrance portal that meets the static world"));
        rows.Add(((245, 245, 245), "metro track (traintracks.xml); bright = not inside an interior"));
        rows.Add(((170, 140, 90), "freight track"));
        rows.Add(((86, 88, 96), "static layer: tarmac (streets); darker = other surfaces"));
        int boxW = Math.Max(Canvas.TextWidth(title, sc), rows.Max(r => Canvas.TextWidth(r.s, sc)) + 16 * sc) + 12, boxH = (rows.Count + 1) * lh + 10;
        cv.Fill(lx - 4, ly - 4, lx + boxW, ly + boxH, (0, 0, 0), 0.72);
        cv.Text(lx, ly, title, (255, 255, 255), sc, false);
        for (int i = 0; i < rows.Count; i++)
        {
            int y = ly + (i + 1) * lh + 2;
            cv.Fill(lx, y, lx + 10 * sc, y + 7 * sc - 1, rows[i].c);
            cv.Text(lx + 14 * sc, y, rows[i].s, (225, 225, 225), sc, false);
        }
        // scale bar
        double barM = o.Mpp <= 1.5 ? 200 : o.Mpp <= 3 ? 500 : 2000;
        int barPx = (int)(barM / o.Mpp), bx = W - barPx - 20, by = H - 24;
        cv.Fill(bx - 6, by - 14 * sc, bx + barPx + 6, by + 8, (0, 0, 0), 0.72);
        cv.Fill(bx, by, bx + barPx, by + 3, (255, 255, 255));
        cv.Text(bx, by - 10 * sc, $"{barM:F0} m", (255, 255, 255), sc, false);

        var pdir = Path.Combine(intDir, "preview");
        Directory.CreateDirectory(pdir);
        Png.WriteRgb(Path.Combine(pdir, o.Name + ".png"), W, H, cv.Rgb);
        long covered = cat.LongCount(c => c != 0);
        double secs = Log.Seconds - t0;
        Paths.WriteJson(Path.Combine(pdir, o.Name + ".json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("name", o.Name);
            w.WriteString("kind", "plan");
            w.WriteStartArray("region_gta_x0_y0_x1_y1"); w.WriteNumberValue(o.X0); w.WriteNumberValue(o.Y0); w.WriteNumberValue(o.X1); w.WriteNumberValue(o.Y1); w.WriteEndArray();
            w.WriteNumber("metres_per_pixel", o.Mpp);
            w.WriteNumber("width", W); w.WriteNumber("height", H);
            w.WriteString("orientation", "north up; pixel (px,py) centre = gta (x0 + (px+0.5)*mpp, y1 - (py+0.5)*mpp)");
            w.WriteBoolean("buildings_drawn", o.Buildings);
            w.WriteBoolean("detached_drawn", o.Detached);
            w.WriteNumber("interior_triangles_rasterised", trisDrawn);
            w.WriteNumber("detached_tile_triangles_in_region", trisDetached);
            w.WriteStartObject("tile_triangles_drawn_by_category");
            for (int c = 0; c < 5; c++) w.WriteNumber(InteriorsExport.CategoryNames[c], perCat[c]);
            w.WriteEndObject();
            w.WriteNumber("interior_covered_m2", covered * o.Mpp * o.Mpp);
            w.WriteNumber("entrances_drawn", entrances);
            w.WriteNumber("seconds", Math.Round(secs, 1));
            w.WriteString("png", o.Name + ".png");
            w.WriteEndObject();
        });
        Log.Info($"interior plan '{o.Name}': {W}x{H} px at {o.Mpp} m/px, {trisDrawn:N0} interior triangles, {covered * o.Mpp * o.Mpp / 1e6:F3} km2 covered, {entrances} entrances, {secs:F1}s");
    }

    static void Clip(int tx, int ty, float ts, PlanOptions o, int W, int H, out int pxMin, out int pxMax, out int pyMin, out int pyMax)
    {
        pxMin = Math.Max(0, (int)Math.Ceiling((tx * ts - o.X0) / o.Mpp - 0.5));
        pxMax = Math.Min(W - 1, (int)Math.Ceiling(((tx + 1) * ts - o.X0) / o.Mpp - 0.5) - 1);
        pyMin = Math.Max(0, (int)Math.Floor((o.Y1 - (ty + 1) * ts) / o.Mpp - 0.5) + 1);
        pyMax = Math.Min(H - 1, (int)Math.Floor((o.Y1 - ty * ts) / o.Mpp - 0.5));
    }

    interface IPixelSink { void Put(int k, float z); }

    struct GroundSink : IPixelSink
    {
        public float[] Zb; public bool[] Road; public bool IsRoad;
        public void Put(int k, float z) { if (z > Zb[k]) { Zb[k] = z; Road[k] = IsRoad; } }
    }

    struct InteriorSink : IPixelSink
    {
        public byte[] Cat; public bool[] Det; public int[] Prio; public int C; public byte Code; public bool D;
        public void Put(int k, float z) { if (Cat[k] == 0 || Prio[C] > Prio[Cat[k] - 1]) { Cat[k] = Code; Det[k] = D; } }
    }

    /// <summary>Rasterise one triangle top-down (pixel centres) into a sink; tiny triangles splat their centroid.</summary>
    static void Raster<T>(in V3 a, in V3 b, in V3 d, PlanOptions o, int W, int pxMin, int pxMax, int pyMin, int pyMax, ref T put) where T : struct, IPixelSink
    {
        double ax = (a.X - o.X0) / o.Mpp - 0.5, ay = (o.Y1 - a.Y) / o.Mpp - 0.5;
        double bx = (b.X - o.X0) / o.Mpp - 0.5, by = (o.Y1 - b.Y) / o.Mpp - 0.5;
        double cx = (d.X - o.X0) / o.Mpp - 0.5, cy = (o.Y1 - d.Y) / o.Mpp - 0.5;
        double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (Math.Abs(area) < 1e-12) return;
        int x0 = Math.Max(pxMin, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(pxMax, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx))));
        int y0 = Math.Max(pyMin, (int)Math.Ceiling(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(pyMax, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy))));
        bool hit = false;
        double inv = 1.0 / area;
        for (int py = y0; py <= y1; py++)
            for (int px = x0; px <= x1; px++)
            {
                double w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) * inv;
                double w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) * inv;
                double w2 = 1.0 - w0 - w1;
                if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                hit = true;
                put.Put(py * W + px, (float)(w0 * a.Z + w1 * b.Z + w2 * d.Z));
            }
        if (!hit)
        {
            int px = (int)Math.Round((ax + bx + cx) / 3), py = (int)Math.Round((ay + by + cy) / 3);
            if (px >= pxMin && px <= pxMax && py >= pyMin && py <= pyMax) put.Put(py * W + px, (a.Z + b.Z + d.Z) / 3f);
        }
    }

    // ------------------------------------------------------------------ vertical section along a track

    public sealed class SectionOptions
    {
        public string Name = "section";
        public string Track = "trains4";
        public int FirstNode, LastNode;
        public double Hmpp = 1.0, Vmpp = 0.5;   // metres per pixel along the track / vertically
        public double Z0 = -30, Z1 = 110;
        public double Halo = 45;                // interior geometry this far beside the track is projected onto the section (dim)
    }

    public static void RenderSection(string outDir, SectionOptions o, int threads)
    {
        double t0 = Log.Seconds;
        var intDir = Path.Combine(outDir, "interiors");
        var st = Layer.Load(outDir, "collision", false);
        var it = Layer.Load(outDir, "interiors", false);
        var tk = LoadTracks(intDir).FirstOrDefault(t => t.Name == o.Track) ?? throw new ArgumentException("no such track: " + o.Track);
        int n0 = Math.Clamp(o.FirstNode, 0, tk.Nodes.Length - 1), n1 = Math.Clamp(o.LastNode, 0, tk.Nodes.Length - 1);
        if (n1 <= n0) throw new ArgumentException("section needs first < last node");
        var path = tk.Nodes.Skip(n0).Take(n1 - n0 + 1).ToArray();
        int ns = path.Length - 1;
        var cum = new double[path.Length];
        for (int i = 1; i < path.Length; i++) cum[i] = cum[i - 1] + Math.Sqrt(Sq(path[i][0] - path[i - 1][0]) + Sq(path[i][1] - path[i - 1][1]));
        double total = cum[^1];
        int left = 46, top = 30, bottom = 30;
        int PW = (int)Math.Ceiling(total / o.Hmpp), PH = (int)Math.Ceiling((o.Z1 - o.Z0) / o.Vmpp);
        var cv = new Canvas(PW + left + 8, PH + top + bottom);
        cv.Fill(0, 0, cv.W - 1, cv.H - 1, (10, 12, 18));
        double X(double s) => left + s / o.Hmpp;
        double Y(double z) => top + (o.Z1 - z) / o.Vmpp;

        // grid
        for (double z = Math.Ceiling(o.Z0 / 10) * 10; z <= o.Z1; z += 10)
        {
            var c = Math.Abs(z) < 1e-6 ? (40, 70, 120) : (28, 31, 40);
            cv.Line(left, Y(z), left + PW, Y(z), c);
            cv.Text(4, (int)Y(z) - 3, $"{z:F0}", (150, 150, 160), 1, false);
        }
        for (double s = 0; s <= total; s += 100)
        {
            cv.Line(X(s), top, X(s), top + PH, (24, 27, 36));
            if (((int)(s / 100)) % 5 == 0) cv.Text((int)X(s) + 2, top + PH + 6, $"{s:F0} m", (150, 150, 160), 1, false);
        }

        // segments: plan bbox for quick rejection
        var segs = new (double x0, double y0, double dx, double dy, double len, double s0, double bx0, double by0, double bx1, double by1)[ns];
        double pad = Math.Max(o.Halo, 1);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < ns; i++)
        {
            double ax = path[i][0], ay = path[i][1], bx = path[i + 1][0], by = path[i + 1][1];
            double len = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
            segs[i] = (ax, ay, len > 0 ? (bx - ax) / len : 1, len > 0 ? (by - ay) / len : 0, len, cum[i], Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
            minX = Math.Min(minX, Math.Min(ax, bx)); minY = Math.Min(minY, Math.Min(ay, by)); maxX = Math.Max(maxX, Math.Max(ax, bx)); maxY = Math.Max(maxY, Math.Max(ay, by));
        }

        var gate = new object();
        long cutStatic = 0, cutInterior = 0;

        // exact cut of one triangle with the vertical curtain along the path
        void Cut(in V3 a, in V3 b, in V3 c, (int, int, int) col, ref long count)
        {
            double tx0 = Math.Min(a.X, Math.Min(b.X, c.X)), tx1 = Math.Max(a.X, Math.Max(b.X, c.X));
            double ty0 = Math.Min(a.Y, Math.Min(b.Y, c.Y)), ty1 = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            if (tx1 < minX || tx0 > maxX || ty1 < minY || ty0 > maxY) return;
            Span<double> ts = stackalloc double[3]; Span<double> zs = stackalloc double[3];
            for (int i = 0; i < ns; i++)
            {
                ref var sg = ref segs[i];
                if (sg.len <= 0 || tx1 < sg.bx0 || tx0 > sg.bx1 || ty1 < sg.by0 || ty0 > sg.by1) continue;
                // signed lateral distance of each vertex from the segment's vertical plane
                double da = (a.X - sg.x0) * -sg.dy + (a.Y - sg.y0) * sg.dx;
                double db = (b.X - sg.x0) * -sg.dy + (b.Y - sg.y0) * sg.dx;
                double dc = (c.X - sg.x0) * -sg.dy + (c.Y - sg.y0) * sg.dx;
                if ((da > 0 && db > 0 && dc > 0) || (da < 0 && db < 0 && dc < 0)) continue;
                int n = 0;
                Edge(a, b, da, db, ref sg, ts, zs, ref n);
                Edge(b, c, db, dc, ref sg, ts, zs, ref n);
                Edge(c, a, dc, da, ref sg, ts, zs, ref n);
                if (n < 2) continue;
                double tA = ts[0], zA = zs[0], tB = ts[1], zB = zs[1];
                if (n == 3 && Math.Abs(tB - tA) + Math.Abs(zB - zA) < 1e-9) { tB = ts[2]; zB = zs[2]; }
                if (tA > tB) { (tA, tB) = (tB, tA); (zA, zB) = (zB, zA); }
                if (tB < 0 || tA > sg.len) continue;
                if (tA < 0) { zA += (zB - zA) * (0 - tA) / Math.Max(1e-12, tB - tA); tA = 0; }
                if (tB > sg.len) { zB = zA + (zB - zA) * (sg.len - tA) / Math.Max(1e-12, tB - tA); tB = sg.len; }
                lock (gate) { cv.Line(X(sg.s0 + tA), Y(zA), X(sg.s0 + tB), Y(zB), col); count++; }
            }
        }

        static void Edge(in V3 p, in V3 q, double dp, double dq, ref (double x0, double y0, double dx, double dy, double len, double s0, double bx0, double by0, double bx1, double by1) sg, Span<double> ts, Span<double> zs, ref int n)
        {
            if ((dp > 0 && dq > 0) || (dp < 0 && dq < 0) || n >= 3) return;
            double den = dp - dq;
            double f = Math.Abs(den) < 1e-12 ? 0 : dp / den;
            double x = p.X + (q.X - p.X) * f, y = p.Y + (q.Y - p.Y) * f, z = p.Z + (q.Z - p.Z) * f;
            ts[n] = (x - sg.x0) * sg.dx + (y - sg.y0) * sg.dy; zs[n] = z; n++;
        }

        // nearest-segment projection of a point: (distance along the track, lateral offset)
        bool Project(in V3 p, out double s, out double lat)
        {
            s = 0; lat = double.MaxValue;
            for (int i = 0; i < ns; i++)
            {
                ref var sg = ref segs[i];
                if (p.X < sg.bx0 - pad || p.X > sg.bx1 + pad || p.Y < sg.by0 - pad || p.Y > sg.by1 + pad) continue;
                double t = Math.Clamp((p.X - sg.x0) * sg.dx + (p.Y - sg.y0) * sg.dy, 0, sg.len);
                double qx = sg.x0 + sg.dx * t, qy = sg.y0 + sg.dy * t;
                double d = Math.Sqrt(Sq(p.X - qx) + Sq(p.Y - qy));
                if (d < lat) { lat = d; s = sg.s0 + t; }
            }
            return lat <= o.Halo;
        }

        // pass 1: dim projection of the interior geometry beside the track (stations, stairs, walkways)
        var halo = new Canvas(cv.W, cv.H);
        var haloCat = new byte[cv.W * cv.H];
        Parallel.ForEach(it.TilesIn(minX - pad, minY - pad, maxX + pad, maxY + pad), new ParallelOptions { MaxDegreeOfParallelism = threads }, tr =>
        {
            var m = Mesh.Read(tr.file);
            var v = m.Verts; var t = m.Tris;
            float ox0 = tr.x * it.TileSize, ox1 = ox0 + it.TileSize, oy0 = tr.y * it.TileSize, oy1 = oy0 + it.TileSize;
            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                if (it.YbnSkip[g.YbnId] || !Layer.Solid(g) || (g.Info & 3) != 0) continue; // mover layer is enough for the dim projection
                if (g.Max.X < minX - pad || g.Min.X > maxX + pad || g.Max.Y < minY - pad || g.Min.Y > maxY + pad) continue;
                int c = (g.Info >> 4) & 7;
                bool d = (g.Info & 0x80) != 0;
                for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                {
                    var a = v[t[i * 3]]; var b = v[t[i * 3 + 1]]; var e = v[t[i * 3 + 2]];
                    float gx = (a.X + b.X + e.X) / 3f, gy = (a.Y + b.Y + e.Y) / 3f;
                    if (gx < ox0 || gx >= ox1 || gy < oy0 || gy >= oy1) continue;
                    if (!Project(a, out double sa, out _) || !Project(b, out double sb, out _) || !Project(e, out double se, out _)) continue;
                    if (Math.Max(sa, Math.Max(sb, se)) - Math.Min(sa, Math.Min(sb, se)) > 60) continue; // wrapped around a bend
                    byte code = (byte)(d ? 6 : c + 1);
                    lock (gate)
                    {
                        Mark(sa, a.Z, sb, b.Z); Mark(sb, b.Z, se, e.Z); Mark(se, e.Z, sa, a.Z);
                        void Mark(double s0, double z0, double s1, double z1)
                        {
                            double x0 = X(s0), y0 = Y(z0), x1 = X(s1), y1 = Y(z1);
                            int n = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
                            for (int k = 0; k <= n; k++)
                            {
                                int px = (int)Math.Round(x0 + (x1 - x0) * (n == 0 ? 0 : (double)k / n)), py = (int)Math.Round(y0 + (y1 - y0) * (n == 0 ? 0 : (double)k / n));
                                if (px >= left && px < left + PW && py >= top && py < top + PH) haloCat[py * cv.W + px] = code;
                            }
                        }
                    }
                }
            }
        });
        for (int k = 0; k < haloCat.Length; k++)
        {
            if (haloCat[k] == 0) continue;
            var c = haloCat[k] == 6 ? (150, 40, 40) : CatColour[haloCat[k] - 1];
            cv.Blend(k % cv.W, k / cv.W, c, 0.22);
        }

        // pass 2: exact cuts, static layer first then interiors on top
        Parallel.ForEach(st.TilesIn(minX - 1, minY - 1, maxX + 1, maxY + 1), new ParallelOptions { MaxDegreeOfParallelism = threads }, tr =>
        {
            var m = Mesh.Read(tr.file);
            var v = m.Verts; var t = m.Tris; var at = m.Attrs;
            float ox0 = tr.x * st.TileSize, ox1 = ox0 + st.TileSize, oy0 = tr.y * st.TileSize, oy1 = oy0 + st.TileSize;
            long cnt = 0;
            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                if (st.YbnSkip[g.YbnId] || !Layer.Solid(g)) continue;
                if (g.Max.X < minX || g.Min.X > maxX || g.Max.Y < minY || g.Min.Y > maxY) continue;
                for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                {
                    var a = v[t[i * 3]]; var b = v[t[i * 3 + 1]]; var e = v[t[i * 3 + 2]];
                    float gx = (a.X + b.X + e.X) / 3f, gy = (a.Y + b.Y + e.Y) / 3f;
                    if (gx < ox0 || gx >= ox1 || gy < oy0 || gy >= oy1) continue;
                    Cut(a, b, e, st.RoadMat[at[i].Material] ? (235, 235, 235) : (125, 128, 138), ref cnt);
                }
            }
            Interlocked.Add(ref cutStatic, cnt);
        });
        Parallel.ForEach(it.TilesIn(minX - 1, minY - 1, maxX + 1, maxY + 1), new ParallelOptions { MaxDegreeOfParallelism = threads }, tr =>
        {
            var m = Mesh.Read(tr.file);
            var v = m.Verts; var t = m.Tris;
            float ox0 = tr.x * it.TileSize, ox1 = ox0 + it.TileSize, oy0 = tr.y * it.TileSize, oy1 = oy0 + it.TileSize;
            long cnt = 0;
            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                if (it.YbnSkip[g.YbnId] || !Layer.Solid(g)) continue;
                if (g.Max.X < minX || g.Min.X > maxX || g.Max.Y < minY || g.Min.Y > maxY) continue;
                int c = (g.Info >> 4) & 7;
                var col = (g.Info & 0x80) != 0 ? (200, 60, 60) : CatColour[c];
                for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                {
                    var a = v[t[i * 3]]; var b = v[t[i * 3 + 1]]; var e = v[t[i * 3 + 2]];
                    float gx = (a.X + b.X + e.X) / 3f, gy = (a.Y + b.Y + e.Y) / 3f;
                    if (gx < ox0 || gx >= ox1 || gy < oy0 || gy >= oy1) continue;
                    Cut(a, b, e, col, ref cnt);
                }
            }
            Interlocked.Add(ref cutInterior, cnt);
        });

        // the rail itself and the station stops
        for (double s = 0; s < total; s += 12 * o.Hmpp) // dashed, so the floor under the rail stays visible
        {
            double s1 = Math.Min(total, s + 6 * o.Hmpp);
            cv.Line(X(s), Y(RailZ(s)), X(s1), Y(RailZ(s1)), (255, 230, 60));
        }
        double RailZ(double s)
        {
            int i = Array.BinarySearch(cum, s);
            if (i < 0) i = ~i;
            i = Math.Clamp(i, 1, path.Length - 1);
            double f = cum[i] > cum[i - 1] ? (s - cum[i - 1]) / (cum[i] - cum[i - 1]) : 0;
            return path[i - 1][2] + (path[i][2] - path[i - 1][2]) * f;
        }
        int stops = 0;
        double lastStop = -1e9;
        for (int i = 0; i < path.Length; i++)
        {
            if (((int)path[i][3] & 1) == 0) continue;
            stops++;
            cv.Line(X(cum[i]), Y(path[i][2]) - 14, X(cum[i]), Y(path[i][2]) + 2, (255, 80, 80));
            if (cum[i] - lastStop > 80) cv.Text((int)X(cum[i]) - 30, (int)Y(path[i][2]) - 26, "station stop", (255, 120, 120));
            lastStop = cum[i];
        }
        // where the game flags the track as "in tunnel": bar under the plot
        for (int i = 1; i < path.Length; i++)
            if (((int)path[i][3] & 4) != 0 && ((int)path[i - 1][3] & 4) != 0)
                cv.Fill((int)X(cum[i - 1]), top + PH + 1, (int)X(cum[i]), top + PH + 3, (0, 215, 255));

        string title = $"{o.Name}: vertical section along {tk.Name} ({tk.Config}) nodes {n0}..{n1}, {total:F0} m; {o.Hmpp:0.##} m/px along, {o.Vmpp:0.##} m/px up (z in m, 0 = sea level)";
        cv.Text(left, 4, title, (255, 255, 255), 1, false);
        cv.Text(left, 16, "white/grey: static layer cut (white = tarmac)   cyan: transit interior   orange: road tunnel   green: parking   violet: building interior   yellow dashes: rail   red: detached interior   dim: interiors within " + $"{o.Halo:F0} m beside the track   cyan bar below: track flagged 'tunnel'", (190, 190, 200), 1, false);
        cv.Text(left, top + PH + 18, $"from gta ({path[0][0]:F0}, {path[0][1]:F0}) to ({path[^1][0]:F0}, {path[^1][1]:F0})", (150, 150, 160), 1, false);

        var pdir = Path.Combine(intDir, "preview");
        Directory.CreateDirectory(pdir);
        Png.WriteRgb(Path.Combine(pdir, o.Name + ".png"), cv.W, cv.H, cv.Rgb);
        double secs = Log.Seconds - t0;
        Paths.WriteJson(Path.Combine(pdir, o.Name + ".json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("name", o.Name);
            w.WriteString("kind", "section");
            w.WriteString("track", tk.Name);
            w.WriteNumber("first_node", n0); w.WriteNumber("last_node", n1);
            w.WriteNumber("length_m", Math.Round(total, 1));
            w.WriteNumber("metres_per_pixel_along", o.Hmpp);
            w.WriteNumber("metres_per_pixel_up", o.Vmpp);
            w.WriteNumber("z0", o.Z0); w.WriteNumber("z1", o.Z1);
            w.WriteNumber("plot_left_px", left); w.WriteNumber("plot_top_px", top);
            w.WriteNumber("halo_m", o.Halo);
            w.WriteNumber("static_triangles_cut", cutStatic);
            w.WriteNumber("interior_triangles_cut", cutInterior);
            w.WriteNumber("station_stop_nodes", stops);
            w.WriteNumber("seconds", Math.Round(secs, 1));
            w.WriteString("png", o.Name + ".png");
            w.WriteEndObject();
        });
        Log.Info($"interior section '{o.Name}': {cv.W}x{cv.H} px, {total:F0} m of {tk.Name}, {cutStatic:N0} static and {cutInterior:N0} interior triangles cut, {secs:F1}s");
    }

    static double Sq(double v) => v * v;
}
