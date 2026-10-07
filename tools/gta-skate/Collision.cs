using System.Collections.Concurrent;
using System.Text.Json;
using SharpDX;

namespace GtaSkate;

/// <summary>
/// Collision for ReSkate Studio from the static collision tiles tools/gta-export writes
/// (work/export/collision/tiles/*.lsct, 256 m, world space, a GTA material per triangle).
///
/// Skate finds grindable edges itself, on creases of collision pieces, but drops that analysis for a piece
/// with more than about 1000 half-edges per metre of its bounds (Studio's exporter warns at 900). So every
/// tile is cut into pieces small enough to stay under that, one piece per surface type.
/// </summary>
public static class Collision
{
    const uint MAP_DYNAMIC = 1u << 2, FOLIAGE = 1u << 19, MAP_RIVER = 1u << 27;
    public const float SrcTile = 256f;
    public const float MaxHalfEdgesPerMetre = 600f;
    public const float MinCell = 8f;
    public const int MaxPieceTris = 20000;   // Studio stores a collision piece with 16-bit indices

    /// <summary>Skate surfaces used (MaterialDecl.Packed of Studio's native collision materials).</summary>
    public static readonly (string name, int packed)[] Surfaces =
    {
        ("default", 32), ("concrete", 96), ("asphalt", 160), ("metal", 352), ("wood", 416), ("marble", 480), ("grass", 608),
        ("brick", 2016), ("glass", 2144), ("earth", 3488), ("sand", 4896), ("gravel", 4960), ("plastic", 3296), ("rubber", 4128),
        ("metal_grate", 1632), ("metal_thin", 1696), ("roofing", 2272),
    };

    /// <summary>Surfaces that switch skating off: grass (material slot 9: no grind edges, no alignment) and sand (slot 76: no alignment).</summary>
    static bool Soft(int s) => Surfaces[s].name is "grass" or "sand";

    static int Surface(string gta)
    {
        string n = gta;
        int S(string s) { for (int i = 0; i < Surfaces.Length; i++) if (Surfaces[i].name == s) return i; return 0; }
        if (n.StartsWith("CONCRETE") || n is "BREEZE_BLOCK" or "PAVING_SLAB" or "STONE" or "COBBLESTONE" or "SANDSTONE_SOLID" or "SANDSTONE_BRITTLE" or "PLASTER_SOLID" or "PLASTER_BRITTLE" or "STUNT_RAMP_SURFACE") return S("concrete");
        if (n.StartsWith("TARMAC") || n is "RUMBLE_STRIP" or "ICE_TARMAC" or "SNOW_TARMAC") return S("asphalt");
        if (n is "MARBLE" or "CERAMIC" or "LINOLEUM" or "LAMINATE") return S("marble");
        if (n.StartsWith("BRICK") || n is "ROOF_TILE") return S("brick");
        if (n.StartsWith("ROCK")) return S("concrete");
        if (n.StartsWith("SAND")) return S("sand");
        if (n.StartsWith("GRAVEL")) return S("gravel");
        if (n.StartsWith("GRASS") || n is "HAY" or "BUSHES" or "BUSHES_NOINST" or "LEAVES" or "TWIGS" or "MARSH" or "MARSH_DEEP") return S("grass");
        if (n.StartsWith("MUD") || n.StartsWith("CLAY") || n.StartsWith("SNOW") || n is "DIRT_TRACK" or "SOIL" or "WOODCHIPS" or "ICE") return S("earth");
        if (n is "METAL_GRILLE" or "METAL_CHAINLINK_SMALL" or "METAL_CHAINLINK_LARGE" or "METAL_MANHOLE") return S("metal_grate");
        if (n is "METAL_CORRUGATED_IRON" or "METAL_DUCT" or "METAL_GARAGE_DOOR" || n.StartsWith("METAL_HOLLOW")) return S("metal_thin");
        if (n.StartsWith("METAL") || n.StartsWith("CAR_METAL") || n.StartsWith("VFX_METAL") || n.StartsWith("PHYS_ELECTRIC")) return S("metal");
        if (n.StartsWith("WOOD") || n is "TREE_BARK" or "CARDBOARD_SHEET" or "CARDBOARD_BOX") return S("wood");
        if (n == "ROOF_FELT") return S("roofing");
        if (n.StartsWith("GLASS") || n is "PERSPEX" or "EMISSIVE_GLASS" or "TVSCREEN" || n.StartsWith("CAR_GLASS")) return S("glass");
        if (n.StartsWith("PLASTIC") || n.StartsWith("FIBREGLASS") || n is "TARPAULIN" or "EMISSIVE_PLASTIC" or "POLYSTYRENE") return S("plastic");
        if (n.StartsWith("RUBBER")) return S("rubber");
        return S("default");
    }

    /// <summary>
    /// Natural rock. It skates like concrete (<see cref="Surface"/>), but a flat patch of it on a hillside is not a
    /// pavement: Place takes it only where there is no road or pavement in reach.
    /// </summary>
    static bool Rock(string gta) => gta.StartsWith("ROCK") || gta.StartsWith("SANDSTONE");

    /// <summary>GTA collision material index -> index into <see cref="Surfaces"/>.</summary>
    public static int[] LoadSurfaceTable(string exportDir) => LoadSurfaceTable(exportDir, out _);

    /// <param name="rock">GTA collision material index -> natural rock (see <see cref="Rock"/>)</param>
    public static int[] LoadSurfaceTable(string exportDir, out bool[] rock)
    {
        var surfaceOf = new int[256];
        rock = new bool[256];
        var p = Path.Combine(exportDir, "materials.json");
        if (!File.Exists(p)) return surfaceOf;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
        foreach (var m in doc.RootElement.GetProperty("materials").EnumerateArray())
        {
            int id = m.GetProperty("id").GetInt32();
            if ((uint)id >= 256) continue;
            string name = m.GetProperty("name").GetString() ?? "";
            surfaceOf[id] = Surface(name);
            rock[id] = Rock(name);
        }
        return surfaceOf;
    }

    /// <summary>Collision of an archetype's own bound, in model space. Foliage volumes are left out.</summary>
    static readonly string[] NoCollisionNames =
    {
        "fireescape", "aircon", "roofvent", "roofpipe", "billb_", "_bmu_", "solarpanel", "satdish", "antenna", "skylight", "rooftop", "watertower",
        "_wires", "telegraph", "powerline", "elecbox", "_flag", "neon", "sign_", "_sign", "litter", "rub_", "weed", "plant_", "bush", "grass", "fern", "cactus",
    };

    public static void FromBound(CodeWalker.GameFiles.Bounds bound, int[] surfaceOf, SrcMesh into)
    {
        if (bound == null) return;
        foreach (var n in NoCollisionNames) if (into.Name.Contains(n)) return;
        GtaExport.Mesh m;
        try { m = GtaExport.YbnFlattener.Flatten(bound, 0, 0, out _); } catch (Exception) { return; }
        if (m.Tris.Length == 0 || m.Tris.Length / 3 > 6000) return;      // a prop this detailed is roof plant or scaffolding
        var map = new Dictionary<uint, int>();
        var pos = new List<float>(); var idx = new List<int>(); var surf = new List<byte>();
        int nt = m.Tris.Length / 3;
        for (int t = 0; t < nt; t++)
        {
            var at = m.Attrs[t];
            if (at.Group < m.Groups.Length && (m.Groups[at.Group].TypeFlags & FOLIAGE) != 0) continue;
            for (int k = 0; k < 3; k++)
            {
                uint vi = m.Tris[t * 3 + k];
                if (!map.TryGetValue(vi, out var ni))
                {
                    ni = pos.Count / 3; map[vi] = ni;
                    pos.Add(m.Verts[vi].X); pos.Add(m.Verts[vi].Y); pos.Add(m.Verts[vi].Z);
                }
                idx.Add(ni);
            }
            surf.Add((byte)surfaceOf[at.Material]);
        }
        if (idx.Count == 0) return;
        into.ColPos = pos.ToArray(); into.ColIdx = idx.ToArray(); into.ColSurf = surf.ToArray();
    }

    /// <summary>World-space collision triangles of placed props, per 256 m source tile.</summary>
    public sealed class PropSoup
    {
        public readonly List<float> Pos = new();   // 9 per triangle
        public readonly List<byte> Surf = new();
    }

    /// <summary>
    /// <paramref name="soups"/> belongs to one caller (one map tile): no locking, and the order is the caller's.
    /// With <paramref name="box"/> (x0, y0, x1, y1), only the triangles whose outline box reaches into it: the
    /// rule Pack cuts the drawn mesh of the same piece with. Returns the number of triangles added.
    /// </summary>
    public static int AddProp(Dictionary<(int, int), PropSoup> soups, SrcMesh m, Vector3 p, Vector3 ex, Vector3 ey, Vector3 ez, float[] box = null)
    {
        if (m.ColIdx == null) return 0;
        var key = ((int)MathF.Floor(p.X / SrcTile), (int)MathF.Floor(p.Y / SrcTile));
        soups.TryGetValue(key, out var soup);
        int added = 0;
        Span<float> w = stackalloc float[9];
        for (int t = 0; t + 2 < m.ColIdx.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = m.ColIdx[t + k] * 3;
                float x = m.ColPos[v], y = m.ColPos[v + 1], z = m.ColPos[v + 2];
                w[k * 3] = p.X + ex.X * x + ey.X * y + ez.X * z; w[k * 3 + 1] = p.Y + ex.Y * x + ey.Y * y + ez.Y * z; w[k * 3 + 2] = p.Z + ex.Z * x + ey.Z * y + ez.Z * z;
            }
            if (box != null && !(MathF.Max(w[0], MathF.Max(w[3], w[6])) >= box[0] && MathF.Min(w[0], MathF.Min(w[3], w[6])) <= box[2] &&
                                 MathF.Max(w[1], MathF.Max(w[4], w[7])) >= box[1] && MathF.Min(w[1], MathF.Min(w[4], w[7])) <= box[3])) continue;
            if (soup == null) soups[key] = soup = new PropSoup();
            for (int k = 0; k < 9; k++) soup.Pos.Add(w[k]);
            soup.Surf.Add(m.ColSurf[t / 3]);
            added++;
        }
        return added;
    }

    /// <summary>
    /// An interior of gta-export's interior table (interiors/index.json): its collision sources ("ybn_ids", the
    /// YbnId of the groups in interiors/tiles) and the box they lie in. Pack places an interior whole when its
    /// origin is in the region, so its own collision is kept whole too.
    /// </summary>
    public sealed class PlacedInterior
    {
        public uint[] Ybns = Array.Empty<uint>();
        public float X0, Y0, X1, Y1;
    }

    static float _limit = float.MaxValue, _offX, _offY;
    static bool _world;   // portable model: pieces are written in scene coordinates, there is no map.json to place them

    sealed class TileData
    {
        public float[] V;          // x, y, z
        public int[] T;            // kept triangles, 3 indices each
        public byte[] S;           // surface per kept triangle
        public bool[] Rock = Array.Empty<bool>();   // natural rock, for the static triangles only: they come first (Place)
    }

    public sealed class Result
    {
        public readonly ConcurrentBag<MapObject> Objects = new();
        public long Triangles, PropTriangles, InteriorTriangles, Pieces, SplitCells, DenseLeaves, UpTris, DownTris;
        public (float x, float y)[] Placed = Array.Empty<(float, float)>();   // where each query point ended up
        public float[] Ground = Array.Empty<float>();   // highest upward-facing surface under each query point (NaN: none)
    }

    /// <summary>Writes collision GLBs under <paramref name="meshDir"/> and returns their map.json records.</summary>
    /// <param name="placedInteriors">interiors Pack placed whole (origin in the region): their own collision is not cut at the region line</param>
    /// <param name="world">portable model: write the pieces in scene coordinates instead of about their cell centre</param>
    public static Result Build(string exportDir, double[] region, string meshDir, int threads, IReadOnlyList<(float x, float y)> queries,
        ConcurrentDictionary<(int, int), PropSoup> props = null, float offX = 0, float offY = 0, bool interiors = false, float limit = float.MaxValue,
        IReadOnlyCollection<PlacedInterior> placedInteriors = null, bool world = false)
    {
        _limit = limit; _offX = offX; _offY = offY; _world = world;
        var res = new Result();
        // one entry per query even when there is no collision: Pack.Run indexes these ([0] is the spawn)
        res.Placed = queries.ToArray();
        res.Ground = new float[queries.Count];
        Array.Fill(res.Ground, float.NaN);
        var tilesDir = Path.Combine(exportDir, "collision", "tiles");
        if (!Directory.Exists(tilesDir)) { Log.Warn("no collision tiles under " + tilesDir + "; the map will have no collision"); return res; }
        var surfaceOf = LoadSurfaceTable(exportDir, out var rock);

        int tx0 = (int)Math.Floor(region[0] / SrcTile), tx1 = (int)Math.Floor((region[2] - 1e-6) / SrcTile);
        int ty0 = (int)Math.Floor(region[1] / SrcTile), ty1 = (int)Math.Floor((region[3] - 1e-6) / SrcTile);
        var jobSet = new HashSet<(int, int)>();
        for (int ty = ty0; ty <= ty1; ty++) for (int tx = tx0; tx <= tx1; tx++) jobSet.Add((tx, ty));
        // A prop is drawn whole when any of it lies in the region, and an interior is placed whole when its origin
        // is in the region, so the pivot that files a prop's collision under a tile, and an interior's own collision,
        // can lie in tiles the region does not touch. Those tiles are built too: prop soups and placed interiors only.
        if (props != null) foreach (var key in props.Keys) jobSet.Add(key);
        var whole = new HashSet<uint>();
        if (interiors && placedInteriors != null)
            foreach (var pi in placedInteriors)
            {
                if (pi.Ybns.Length == 0) continue;
                foreach (var id in pi.Ybns) whole.Add(id);
                int ix0 = (int)MathF.Floor(pi.X0 / SrcTile), ix1 = (int)MathF.Floor(pi.X1 / SrcTile);
                int iy0 = (int)MathF.Floor(pi.Y0 / SrcTile), iy1 = (int)MathF.Floor(pi.Y1 / SrcTile);
                for (int ty = iy0; ty <= iy1; ty++) for (int tx = ix0; tx <= ix1; tx++) jobSet.Add((tx, ty));
            }
        var jobs = jobSet.OrderBy(j => j.Item2).ThenBy(j => j.Item1).ToList();

        // The spawn and bus-stop queries run after the tiles are built, over the tiles around each point. A tile holds
        // only the triangles whose centre is in it, so the road under a point near a tile border, or a bridge or
        // roof over it, can belong to the tile next door, and the 120 m search must not stop at the border.
        var wanted = new HashSet<(int, int)>();
        foreach (var q in queries)
        {
            var (ax0, ay0, ax1, ay1) = PlaceTiles(q.x, q.y);
            for (int ay = ay0; ay <= ay1; ay++) for (int ax = ax0; ax <= ax1; ax++) wanted.Add((ax, ay));
        }
        var kept = new ConcurrentDictionary<(int, int), (TileData td, int nStatic)>();

        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = threads }, job =>
        {
            var (tx, ty) = job;
            // a tile beyond the region holds only what was placed there from inside it: props and whole interiors
            bool inRegion = tx >= tx0 && tx <= tx1 && ty >= ty0 && ty <= ty1;
            var path = Path.Combine(tilesDir, $"tile_{tx}_{ty}.lsct");
            var td = inRegion && File.Exists(path) ? Read(path, tx, ty, region, surfaceOf, rock, res, false, null) : null;
            td ??= new TileData { V = Array.Empty<float>(), T = Array.Empty<int>(), S = Array.Empty<byte>() };
            int nStatic = td.T.Length / 3;      // the static world comes first; interiors and props follow
            var ipath = Path.Combine(exportDir, "interiors", "tiles", $"tile_{tx}_{ty}.lsct");
            if (interiors && (inRegion || whole.Count > 0) && File.Exists(ipath))
            {
                // collision of the placed interiors, same format on the same grid
                var it = Read(ipath, tx, ty, region, surfaceOf, rock, res, true, whole);
                if (it != null && it.T.Length > 0)
                {
                    int vb = td.V.Length / 3;
                    var v2 = new float[td.V.Length + it.V.Length];
                    Array.Copy(td.V, v2, td.V.Length); Array.Copy(it.V, 0, v2, td.V.Length, it.V.Length);
                    var t2 = new int[td.T.Length + it.T.Length];
                    Array.Copy(td.T, t2, td.T.Length);
                    for (int i = 0; i < it.T.Length; i++) t2[td.T.Length + i] = vb + it.T[i];
                    var s2 = new byte[td.S.Length + it.S.Length];
                    Array.Copy(td.S, s2, td.S.Length); Array.Copy(it.S, 0, s2, td.S.Length, it.S.Length);
                    td = new TileData { V = v2, T = t2, S = s2, Rock = td.Rock };
                    Interlocked.Add(ref res.InteriorTriangles, it.T.Length / 3);
                }
            }
            if (props != null && props.TryGetValue((tx, ty), out var soup) && soup.Surf.Count > 0)
            {
                // the placed props' own collision joins the tile's triangles; as for the static ones (Read), a
                // triangle whose centre lies beyond the limit is left out
                int v0 = td.V.Length / 3, nAll = soup.Surf.Count;
                var keptTris = new List<int>(nAll);
                for (int i = 0; i < nAll; i++)
                {
                    int p = i * 9;
                    float cx = (soup.Pos[p] + soup.Pos[p + 3] + soup.Pos[p + 6]) / 3f, cy = (soup.Pos[p + 1] + soup.Pos[p + 4] + soup.Pos[p + 7]) / 3f;
                    if (MathF.Abs(cx + offX) > limit - 20f || MathF.Abs(cy + offY) > limit - 20f) continue;
                    keptTris.Add(i);
                }
                int nt = keptTris.Count;
                if (nt > 0)
                {
                    var v = new float[td.V.Length + nt * 9];
                    Array.Copy(td.V, v, td.V.Length);
                    var t = new int[td.T.Length + nt * 3];
                    Array.Copy(td.T, t, td.T.Length);
                    var s = new byte[td.S.Length + nt];
                    Array.Copy(td.S, s, td.S.Length);
                    for (int k = 0; k < nt; k++)
                    {
                        soup.Pos.CopyTo(keptTris[k] * 9, v, td.V.Length + k * 9, 9);
                        t[td.T.Length + k * 3] = v0 + k * 3; t[td.T.Length + k * 3 + 1] = v0 + k * 3 + 1; t[td.T.Length + k * 3 + 2] = v0 + k * 3 + 2;
                        s[td.S.Length + k] = soup.Surf[keptTris[k]];
                    }
                    td = new TileData { V = v, T = t, S = s, Rock = td.Rock };
                    Interlocked.Add(ref res.PropTriangles, nt);
                }
            }
            if (td.T.Length == 0) return;
            if (wanted.Contains((tx, ty))) kept[(tx, ty)] = (td, nStatic);

            var all = new int[td.T.Length / 3];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            Split(td, all, tx * SrcTile, ty * SrcTile, SrcTile, $"c_{tx}_{ty}", meshDir, res, offX, offY);
        });

        // Spawn and bus stops: each query point goes onto ground that is joined to the streets (see Place), so
        // nobody starts on a roof, on a prop, inside a building or under water. The game's water surfaces (the
        // quads Pack.Water draws) tell which ground is sea or lake bed. Every quad is taken, not only those that
        // touch the region: the search reaches 120 m beyond the point, and a sea-bed triangle filed inside the
        // region reaches further out than that, under quads the region itself does not touch.
        if (queries.Count > 0)
        {
            var water = new List<WaterQuad>();
            var waterPath = Path.Combine(exportDir, "water", "water.json");
            if (File.Exists(waterPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(waterPath));
                foreach (var q in doc.RootElement.GetProperty("main").GetProperty("quads").EnumerateArray())
                {
                    if (q.TryGetProperty("IsInvisible", out var inv) && inv.ValueKind == JsonValueKind.True) continue;
                    var cs = q.GetProperty("corners").EnumerateArray().Select(c => ((float)c[0].GetDouble(), (float)c[1].GetDouble())).ToArray();
                    if (cs.Length < 3) continue;
                    water.Add(new WaterQuad
                    {
                        X0 = (float)q.GetProperty("minX").GetDouble(), X1 = (float)q.GetProperty("maxX").GetDouble(),
                        Y0 = (float)q.GetProperty("minY").GetDouble(), Y1 = (float)q.GetProperty("maxY").GetDouble(),
                        Z = (float)q.GetProperty("z").GetDouble(), Corners = cs,
                    });
                }
            }
            for (int qi = 0; qi < queries.Count; qi++)
            {
                float qx = queries[qi].x, qy = queries[qi].y;
                var near = Around(qx, qy, kept, out int nStatic);
                if (Place(near, nStatic, water, qx, qy, out float px, out float py, out float pz)) { res.Ground[qi] = pz; res.Placed[qi] = (px, py); }
            }
        }
        return res;
    }

    static Vector3 P(float[] v, int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

    /// <summary>A water surface of the game: a rectangle or, for the quads cut along a coast, a triangle.</summary>
    sealed class WaterQuad
    {
        public float X0, Y0, X1, Y1, Z;
        public (float x, float y)[] Corners;

        /// <summary>True when the point lies below this surface: inside its outline (which is convex) and lower.</summary>
        public bool Over(float x, float y, float z)
        {
            if (x < X0 || x > X1 || y < Y0 || y > Y1 || !(z < Z)) return false;
            bool left = false, right = false;
            for (int i = 0; i < Corners.Length; i++)
            {
                var a = Corners[i]; var b = Corners[(i + 1) % Corners.Length];
                float side = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x);
                if (side > 0) left = true; else if (side < 0) right = true;
            }
            return !(left && right);
        }
    }

    const float PlaceStep = 4f;      // the grid Place works on: a point every 4 m ...
    const int PlaceCells = 30;       // ... 30 points (120 m) each way from the point asked for
    const float PlaceReach = PlaceStep * PlaceCells;

    /// <summary>
    /// The tiles whose triangles can reach into the search square of a query. A triangle is filed under the tile
    /// its centre is in, and the centre of the largest collision triangles is about 230 m from their far corner,
    /// so one tile size beyond the square covers them all.
    /// </summary>
    static (int x0, int y0, int x1, int y1) PlaceTiles(float qx, float qy)
    {
        const float span = PlaceReach + SrcTile;
        return ((int)MathF.Floor((qx - span) / SrcTile), (int)MathF.Floor((qy - span) / SrcTile), (int)MathF.Floor((qx + span) / SrcTile), (int)MathF.Floor((qy + span) / SrcTile));
    }

    /// <summary>
    /// The collision triangles that reach into the search square of Place around a point, as one soup of loose
    /// triangles: the static world first (<paramref name="nStatic"/> triangles), then the placed interiors and the
    /// props. They come from the finished tiles Build kept (region, limit, interiors and props already applied),
    /// whichever of the surrounding tiles owns them, so each triangle is there once.
    /// </summary>
    static TileData Around(float qx, float qy, ConcurrentDictionary<(int, int), (TileData td, int nStatic)> tiles, out int nStatic)
    {
        var (ax0, ay0, ax1, ay1) = PlaceTiles(qx, qy);
        var v = new List<float>(); var s = new List<byte>(); var rock = new List<bool>();
        nStatic = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int ay = ay0; ay <= ay1; ay++)
                for (int ax = ax0; ax <= ax1; ax++)
                {
                    if (!tiles.TryGetValue((ax, ay), out var k)) continue;
                    var td = k.td;
                    int from = pass == 0 ? 0 : k.nStatic * 3, to = pass == 0 ? k.nStatic * 3 : td.T.Length;
                    for (int t = from; t < to; t += 3)
                    {
                        var a = P(td.V, td.T[t]); var b = P(td.V, td.T[t + 1]); var c = P(td.V, td.T[t + 2]);
                        if (MathF.Max(a.X, MathF.Max(b.X, c.X)) < qx - PlaceReach || MathF.Min(a.X, MathF.Min(b.X, c.X)) > qx + PlaceReach ||
                            MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) < qy - PlaceReach || MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) > qy + PlaceReach) continue;
                        v.Add(a.X); v.Add(a.Y); v.Add(a.Z); v.Add(b.X); v.Add(b.Y); v.Add(b.Z); v.Add(c.X); v.Add(c.Y); v.Add(c.Z);
                        s.Add(td.S[t / 3]);
                        if (pass == 0) rock.Add(td.Rock[t / 3]);
                    }
                }
            if (pass == 0) nStatic = s.Count;
        }
        var idx = new int[s.Count * 3];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        return new TileData { V = v.ToArray(), T = idx, S = s.ToArray(), Rock = rock.ToArray() };
    }

    /// <summary>
    /// Where a spawn or bus stop asked for at (qx, qy) goes: onto ground that is joined to the streets.
    ///
    /// The near-horizontal triangles around the point (<paramref name="td"/>, from Around) are rasterized to a grid,
    /// a point every 4 m, 120 m each way: the top surface of everything, and the lowest upward-facing surface of
    /// the static world. A grid point is ground when its top surface is an upward-facing triangle of the static
    /// world (the first <paramref name="nStatic"/> triangles: an interior or a prop on top hides the ground) and is
    /// not under water, and it is solid ground when nothing lies beneath it (a bridge, a deck or a roof over a
    /// floor is not). Neighbouring ground points are joined when the step between them is no steeper than a 30 %
    /// slope. The area holding the most asphalt on solid ground is the street network with its pavements, squares
    /// and ramps; a roof, a car-park deck or a podium is an island cut off by its walls, whatever its height and
    /// material, so nothing is placed there. The point itself is kept when it is already flat asphalt or concrete
    /// of that area; otherwise it moves to the nearest such grid point (asphalt and solid ground first, clear of
    /// walls and kerbs when there is a choice). Natural rock has the concrete surface too (TileData.Rock tells it
    /// apart): a flat ledge of it is taken only when no road or pavement is in reach, before any other ground.
    /// </summary>
    static bool Place(TileData td, int nStatic, List<WaterQuad> water, float qx, float qy, out float px, out float py, out float pz)
    {
        const float Step = PlaceStep, MaxSlope = 0.3f;
        const int R = PlaceCells, W = 2 * R + 1;
        px = qx; py = qy; pz = float.NaN;

        var top = new float[W * W]; var low = new float[W * W]; var tri = new int[W * W];
        Array.Fill(top, float.NaN); Array.Fill(low, float.NaN); Array.Fill(tri, -1);
        for (int t = 0; t < td.T.Length; t += 3)
        {
            var a = P(td.V, td.T[t]); var b = P(td.V, td.T[t + 1]); var c = P(td.V, td.T[t + 2]);
            int i0 = Math.Max(-R, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - qx) / Step)), i1 = Math.Min(R, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - qx) / Step));
            if (i0 > i1) continue;
            int j0 = Math.Max(-R, (int)MathF.Ceiling((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - qy) / Step)), j1 = Math.Min(R, (int)MathF.Floor((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - qy) / Step));
            if (j0 > j1) continue;
            var n = Vector3.Cross(b - a, c - a);
            if (MathF.Abs(n.Z) < 0.7f * n.Length()) continue;
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-9f) continue;
            bool floor = t < nStatic * 3 && n.Z > 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float x = qx + i * Step, y = qy + j * Step;
                    float w0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d, w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d, w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                    int k = (j + R) * W + i + R;
                    if (float.IsNaN(top[k]) || z > top[k]) { top[k] = z; tri[k] = t; }
                    if (floor && (float.IsNaN(low[k]) || z < low[k])) low[k] = z;
                }
        }

        // what each top surface is: 0 nothing to stand on, 1 ground, 2 flat concrete, 3 flat asphalt; rock: the
        // flat concrete is natural rock (it has the concrete surface, but a rock ledge on a hillside is no pavement)
        int asphalt = Array.FindIndex(Surfaces, x => x.name == "asphalt"), concrete = Array.FindIndex(Surfaces, x => x.name == "concrete");
        var kind = new byte[W * W]; var solid = new bool[W * W]; var rock = new bool[W * W];
        for (int k = 0; k < kind.Length; k++)
        {
            int t = tri[k];
            if (t < 0) continue;
            float x = qx + (k % W - R) * Step, y = qy + (k / W - R) * Step;
            bool wet = false;
            foreach (var q in water) if (q.Over(x, y, top[k])) { wet = true; break; }
            if (wet) { top[k] = float.NaN; continue; }                        // under water: sea bed, lake bed
            if (t >= nStatic * 3) continue;                                   // an interior or a prop is on top
            var a = P(td.V, td.T[t]);
            var n = Vector3.Cross(P(td.V, td.T[t + 1]) - a, P(td.V, td.T[t + 2]) - a);
            float nl = n.Length();
            if (n.Z < 0.7f * nl) continue;                                    // faces down
            int sf = td.S[t / 3];
            kind[k] = (byte)(n.Z < 0.97f * nl ? 1 : sf == asphalt ? 3 : sf == concrete ? 2 : 1);
            rock[k] = kind[k] == 2 && td.Rock[t / 3];
            solid[k] = top[k] - low[k] <= 2f;
        }

        // joined areas of ground, and how much street and how much solid ground each one holds
        var comp = new int[W * W]; Array.Fill(comp, -1);
        var street = new List<int>(); var ground = new List<int>(); var queue = new int[W * W];
        for (int s = 0; s < kind.Length; s++)
        {
            if (kind[s] == 0 || comp[s] >= 0) continue;
            int id = street.Count, head = 0, tail = 0, nStreet = 0, nGround = 0;
            comp[s] = id; queue[tail++] = s;
            while (head < tail)
            {
                int k = queue[head++], i = k % W, j = k / W;
                if (solid[k]) { nGround++; if (kind[k] == 3) nStreet++; }
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        int i2 = i + di, j2 = j + dj;
                        if ((di == 0 && dj == 0) || i2 < 0 || i2 >= W || j2 < 0 || j2 >= W) continue;
                        int k2 = j2 * W + i2;
                        if (kind[k2] == 0 || comp[k2] >= 0) continue;
                        if (MathF.Abs(top[k2] - top[k]) > MaxSlope * Step * (di != 0 && dj != 0 ? 1.4142f : 1f)) continue;
                        comp[k2] = id; queue[tail++] = k2;
                    }
            }
            street.Add(nStreet); ground.Add(nGround);
        }
        // with no street in reach (open country, a beach) the largest piece of solid ground stands in for it
        var score = street.Count > 0 && street.Max() >= 40 ? street : ground;
        int most = score.Count > 0 ? score.Max() : 0;
        bool Joined(int k) => kind[k] != 0 && score[comp[k]] > 0 && score[comp[k]] * 2 >= most;   // half as much counts too: the far side of a river or a freeway

        // where to stand
        int centre = R * W + R, best = -1;
        if (kind[centre] >= 2 && !rock[centre] && solid[centre] && Joined(centre)) best = centre;   // the point asked for is open road or pavement already
        // road or pavement; where there is none in reach, a flat rock ledge; where there is none either, any ground
        for (int pass = 0; pass < 3 && best < 0; pass++)
        {
            int want = pass < 2 ? 2 : 1;
            float bestCost = float.MaxValue;
            for (int k = 0; k < kind.Length; k++)
            {
                if (kind[k] < want || (pass == 0 && rock[k]) || !Joined(k)) continue;
                int i = k % W - R, j = k / W - R;
                float cost = (i * i + j * j) * Step * Step;
                if (want == 2 && kind[k] != 3) cost += 3600f;                         // prefer the road itself
                if (!solid[k]) cost += 3600f;                                         // street level beats a bridge or a flyover
                bool clear = i > -R && i < R && j > -R && j < R;
                for (int dj = -1; clear && dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                        if (comp[k + dj * W + di] != comp[k]) { clear = false; break; }
                if (!clear) cost += 400f;                                             // next to a wall, a kerb drop or a prop
                if (cost < bestCost) { bestCost = cost; best = k; }
            }
        }
        if (best < 0 && !float.IsNaN(top[centre])) best = centre;                     // no ground in reach: whatever is highest at the point
        if (best < 0) return false;
        px = qx + (best % W - R) * Step; py = qy + (best / W - R) * Step; pz = top[best];
        return true;
    }

    static TileData Read(string path, int tx, int ty, double[] region, int[] surfaceOf, bool[] rockOf, Result res, bool interior, HashSet<uint> whole)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 96 || b[0] != 'L' || b[1] != 'S' || b[2] != 'C' || b[3] != 'T') return null;
        uint nGroups = BitConverter.ToUInt32(b, 28), nVerts = BitConverter.ToUInt32(b, 32), nTris = BitConverter.ToUInt32(b, 36);
        int offGroups = (int)BitConverter.ToUInt32(b, 68), offVerts = (int)BitConverter.ToUInt32(b, 72), offTris = (int)BitConverter.ToUInt32(b, 76), offAttrs = (int)BitConverter.ToUInt32(b, 80);
        var keepGroup = new bool[nGroups];
        var wholeGroup = new bool[nGroups];   // interior tiles: the group belongs to an interior that was placed whole
        for (int g = 0; g < nGroups; g++)
        {
            int o = offGroups + g * 56;
            byte info = b[o + 7];
            uint type = BitConverter.ToUInt32(b, o + 8);
            // what peds and objects collide with: the mover collision, not the weapon-only detail layer
            // interior tiles: bit 7 = detached from the world (teleport interiors), bit 3 = off at story start
            keepGroup[g] = (info & 3) < 2 && (info & (interior ? 0x88 : 8)) == 0 && (type & (MAP_RIVER | FOLIAGE)) == 0 && (type & MAP_DYNAMIC) != 0;
            wholeGroup[g] = interior && whole != null && whole.Contains(BitConverter.ToUInt32(b, o));   // GroupRec.YbnId
        }
        var v = new float[nVerts * 3];
        Buffer.BlockCopy(b, offVerts, v, 0, v.Length * 4);
        var tris = new List<int>((int)nTris); var surf = new List<byte>((int)nTris); var rock = new List<bool>((int)nTris);
        float x0 = tx * SrcTile, y0 = ty * SrcTile;
        long up = 0, down = 0;
        for (int t = 0; t < nTris; t++)
        {
            int oa = offAttrs + t * 8;
            int group = BitConverter.ToUInt16(b, oa + 4);
            if (group >= nGroups || !keepGroup[group]) continue;
            int i0 = (int)BitConverter.ToUInt32(b, offTris + t * 12), i1 = (int)BitConverter.ToUInt32(b, offTris + t * 12 + 4), i2 = (int)BitConverter.ToUInt32(b, offTris + t * 12 + 8);
            float cx = (v[i0 * 3] + v[i1 * 3] + v[i2 * 3]) / 3f, cy = (v[i0 * 3 + 1] + v[i1 * 3 + 1] + v[i2 * 3 + 1]) / 3f;
            // a triangle belongs to the tile its centre is in (tiles overlap by a margin)
            if (cx < x0 || cx >= x0 + SrcTile || cy < y0 || cy >= y0 + SrcTile) continue;
            // the region cuts the world, but not an interior that was placed whole: it is drawn beyond the line too
            if (!wholeGroup[group] && (cx < region[0] || cx >= region[2] || cy < region[1] || cy >= region[3])) continue;
            if (MathF.Abs(cx + _offX) > _limit - 20f || MathF.Abs(cy + _offY) > _limit - 20f) continue;
            var n = Vector3.Cross(P(v, i1) - P(v, i0), P(v, i2) - P(v, i0));
            float len = n.Length();
            if (!(len > 1e-9f)) continue;
            if (n.Z > 0.9f * len) up++; else if (n.Z < -0.9f * len) down++;
            tris.Add(i0); tris.Add(i1); tris.Add(i2);
            surf.Add((byte)surfaceOf[b[oa]]);
            rock.Add(rockOf[b[oa]]);
        }
        Interlocked.Add(ref res.UpTris, up); Interlocked.Add(ref res.DownTris, down);
        return new TileData { V = v, T = tris.ToArray(), S = surf.ToArray(), Rock = rock.ToArray() };
    }

    static void Split(TileData td, int[] tris, float x0, float y0, float size, string name, string meshDir, Result res, float offX, float offY)
    {
        if (tris.Length == 0) return;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var t in tris)
            for (int k = 0; k < 3; k++) { var p = P(td.V, td.T[t * 3 + k]); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        float diag = (mx - mn).Length();
        bool dense = tris.Length * 3 > MaxHalfEdgesPerMetre * MathF.Max(diag, 1f) || tris.Length > MaxPieceTris;
        if (dense && size > MinCell)
        {
            Interlocked.Increment(ref res.SplitCells);
            float h = size / 2;
            var q = new List<int>[4];
            for (int i = 0; i < 4; i++) q[i] = new List<int>();
            foreach (var t in tris)
            {
                float cx = (td.V[td.T[t * 3] * 3] + td.V[td.T[t * 3 + 1] * 3] + td.V[td.T[t * 3 + 2] * 3]) / 3f;
                float cy = (td.V[td.T[t * 3] * 3 + 1] + td.V[td.T[t * 3 + 1] * 3 + 1] + td.V[td.T[t * 3 + 2] * 3 + 1]) / 3f;
                q[(cx >= x0 + h ? 1 : 0) + (cy >= y0 + h ? 2 : 0)].Add(t);
            }
            for (int i = 0; i < 4; i++) Split(td, q[i].ToArray(), x0 + (i & 1) * h, y0 + (i >> 1) * h, h, name + "_" + i, meshDir, res, offX, offY);
            return;
        }
        if (dense) Interlocked.Increment(ref res.DenseLeaves);

        // One piece per surface; a surface with only a few triangles here joins a bigger one, but never across
        // the divide between what can be skated and what cannot: grass (no grind edges, no alignment) and sand (no
        // alignment) switch skating off for the whole piece. So the few triangles of rails, benches and kerbs in a
        // lawn join the largest of the other hard surfaces, a small patch of grass in a square joins the largest
        // soft surface, and the largest surface of each kind is written however small it is.
        var count = new int[Surfaces.Length];
        foreach (var t in tris) count[td.S[t]]++;
        int hardHost = -1, softHost = -1;
        for (int s = 0; s < count.Length; s++)
        {
            if (count[s] == 0) continue;
            if (Soft(s)) { if (softHost < 0 || count[s] > count[softHost]) softHost = s; }
            else if (hardHost < 0 || count[s] > count[hardHost]) hardHost = s;
        }
        int minor = Math.Max(8, tris.Length / 20);
        var into = new int[count.Length];      // the piece each surface is written into
        for (int s = 0; s < count.Length; s++) into[s] = count[s] >= minor ? s : Soft(s) ? softHost : hardHost;
        // Studio map: vertices about the cell centre, placed by the record's transform in map.json.
        // Portable model: nothing carries that transform, so the piece is written where it stands in the
        // scene (map = gta + offset) and its transform below comes out as the identity.
        var origin = _world ? new Vector3(-offX, -offY, 0) : new Vector3(x0 + size / 2, y0 + size / 2, 0);
        Span<int> tri = stackalloc int[3];
        for (int s = 0; s < count.Length; s++)
        {
            if (count[s] == 0 || into[s] != s) continue;
            // vertices are welded by position (1 mm): neighbouring triangles must share their edges, both for
            // Skate's edge analysis and because props arrive as loose triangles
            var map = new Dictionary<(int, int, int), int>();
            var pos = new List<float>(); var idx = new List<int>();
            foreach (var t in tris)
            {
                if (into[td.S[t]] != s) continue;
                for (int k = 0; k < 3; k++)
                {
                    int vi = td.T[t * 3 + k];
                    float x = td.V[vi * 3] - origin.X, y = td.V[vi * 3 + 1] - origin.Y, z = td.V[vi * 3 + 2];
                    var key = ((int)MathF.Round(x * 1000f), (int)MathF.Round(y * 1000f), (int)MathF.Round(z * 1000f));
                    if (!map.TryGetValue(key, out var ni))
                    {
                        ni = pos.Count / 3; map[key] = ni;
                        pos.Add(x); pos.Add(y); pos.Add(z);
                    }
                    tri[k] = ni;
                }
                if (tri[0] == tri[1] || tri[1] == tri[2] || tri[0] == tri[2]) continue;      // collapsed by the weld
                idx.Add(tri[0]); idx.Add(tri[1]); idx.Add(tri[2]);
            }
            if (idx.Count == 0) continue;
            string file = $"{name}_{Surfaces[s].name}.glb";
            Glb.Write(Path.Combine(meshDir, file), Path.GetFileNameWithoutExtension(file), new[] { new GlbPrim { Pos = pos.ToArray(), Idx = idx.ToArray() } });
            res.Objects.Add(new MapObject
            {
                Name = Path.GetFileNameWithoutExtension(file), CollisionMesh = "meshes/" + file, Render = false, CollisionMode = "triangle_mesh",
                CollisionPacked = Surfaces[s].packed, Transform = MapObject.Translation(origin.X + offX, origin.Y + offY, origin.Z),
            });
            Interlocked.Add(ref res.Triangles, idx.Count / 3);
            Interlocked.Increment(ref res.Pieces);
        }
    }
}
