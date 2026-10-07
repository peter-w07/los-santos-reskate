using System.Collections.Concurrent;
using CodeWalker;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaSkate;

public sealed class PackOptions
{
    public string Name = "test";
    public double[] Region = { -100, -1200, 500, -600 };
    public float Tile = 200f;
    public int BandRows = 8;
    public float MinSize = 0.5f;       // skip entities whose bounding radius is below this (metres)
    public int Level = 1, TreeLevel = 1;
    public int TexCap = 256;
    public int MinInstances = 3;       // an archetype placed at least this often ...
    public int MinInstanceTris = 150;  // ... with at least this many triangles is instanced instead of merged
    public long TexBudget = 1500L << 20;
    public bool PropCollision = true;  // placed props collide with their own bound
    public double[] Offset = { 0, 0 };  // added to every position: map = gta + offset (Studio's world is 9600 m square about 0,0; use multiples of 200)
    public float Limit = 4780f;        // Studio map only: geometry beyond +-Limit (after the offset) is dropped
    public bool Model;                 // write one portable glTF scene (.gltf + .bin + .png) instead of Studio's normalized map
    public int MapHeight = 2160;       // pixel height of ui/map_image.png (16:9); Studio expects 2160
    public bool NoProps;               // experiment: leave out every prop (entities without a LOD parent)
    public bool Interiors = true;      // objects inside placed interiors (MLO): metro, tunnels, car parks, shops
    public bool Water = true;          // sea and lakes from the game's water quads, as Studio's native ocean
    public string DisplayName;         // level name shown in ReSkate (default: the map name)
    public List<(string name, float x, float y, float yaw, float z)> Stops = new();   // z NaN: put on the nearest open road   // fast-travel bus stops, gta coordinates
    public double[] Spawn;             // gta x, y[, z[, yaw]]; default: the middle of the region, on the ground
}

/// <summary>One placed entity selected for the map.</summary>
public sealed class Sel
{
    public YmapInfo Y;
    public Archetype Arch;
    public uint Tint;
    public Vector3 Pos, Ex, Ey, Ez;
    public float Radius;
    public int Tx, Ty;
    public bool Outside;   // taken because its box reaches into the region; its pivot (or its interior's origin) lies outside
}

public sealed class ProtoInfo
{
    public int Id;
    public string Name, File;
    public int Tris, Verts, Parts;
    public Vector3 Min, Max;
    public long Count;
    public bool Tree;
}

public sealed class TileOut
{
    public int X, Y, Tris, Verts, Parts, Instances, Entities;
    public string Mesh;
    public float ZMin = float.MaxValue, ZMax = float.MinValue;
}

/// <summary>
/// YMAP placements -> archetypes -> drawables -> ReSkate Studio's "normalized map" folder (what its Blender
/// add-on exports and `reskate_cli compile-map` compiles): map.json, meshes/*.glb, textures/*.
/// Static geometry is merged into one mesh per map tile (one primitive per material); archetypes that repeat
/// keep one mesh and are placed many times. Coordinates in the meshes are y up, converted from GTA's z up.
/// </summary>
public static class Pack
{
    public static readonly string[] LodNames = { "HD", "LOD", "SLOD1", "SLOD2", "SLOD3", "ORPHANHD", "SLOD4" };

    public static RpfFileEntry DrawableEntry(GameFileCache gfc, Archetype a, out string kind)
    {
        kind = "none";
        if (a == null) return null;
        if (a.DrawableDict != 0 && gfc.YddDict.TryGetValue(a.DrawableDict, out var ydd)) { kind = "ydd"; return ydd; }
        if (gfc.YdrDict.TryGetValue(a.Hash, out var ydr)) { kind = "ydr"; return ydr; }
        if (gfc.YftDict.TryGetValue(a.Hash, out var yft)) { kind = "yft"; return yft; }
        return null;
    }

    static string SkipReason(Archetype arch, uint entityFlags)
    {
        if (arch is TimeArchetype ta && !ta.IsActive(12f)) return "skip: time archetype not active at 12:00";
        if ((arch._BaseArchetypeDef.flags & 2048) != 0) return "skip: shadow proxy archetype";
        switch (entityFlags)
        {
            case 135790592: case 135790593: case 672661504: case 536870912: case 35127296: case 39321602:
                return "skip: reflection proxy entity";
        }
        return null;
    }

    /// <summary>
    /// Interiors that are part of the story-start world, as "ymap|archetype": placed, on by default and
    /// physically attached to the outside (the interior table gta-export wrote). Teleport interiors under the
    /// map and mission-only ones are not in it.
    /// </summary>
    static Dictionary<string, Collision.PlacedInterior> InteriorSet(string exportDir)
    {
        var set = new Dictionary<string, Collision.PlacedInterior>(StringComparer.OrdinalIgnoreCase);
        var p = Path.Combine(exportDir, "interiors", "index.json");
        if (!File.Exists(p)) return set;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(p));
        foreach (var it in doc.RootElement.GetProperty("interiors").EnumerateArray())
        {
            if (it.GetProperty("detached").GetBoolean() || !it.GetProperty("default_on").GetBoolean()) continue;
            var pi = new Collision.PlacedInterior();
            if (it.TryGetProperty("ybn_ids", out var ids) && it.TryGetProperty("min", out var mn) && it.TryGetProperty("max", out var mx))
            {
                pi.Ybns = ids.EnumerateArray().Select(v => v.GetUInt32()).ToArray();
                pi.X0 = mn[0].GetSingle(); pi.Y0 = mn[1].GetSingle(); pi.X1 = mx[0].GetSingle(); pi.Y1 = mx[1].GetSingle();
            }
            set[it.GetProperty("ymap").GetString() + "|" + it.GetProperty("name").GetString()] = pi;
        }
        return set;
    }

    /// <summary>How far outside the region a pivot may lie and its mesh still be looked at; ymaps are loaded with the same margin.</summary>
    const float Reach = 600f;

    /// <summary>
    /// A piece whose pivot lies outside the region (<see cref="Sel.Outside"/>) is drawn only when a triangle of it
    /// reaches into the region, and then only its triangles that reach into the region grown by this many metres.
    /// Such a piece is 100 to 900 m long and only its near end is wanted: the rest is ground with no collision
    /// under it, textures nothing in the level shows, and decal strips with thousands of texture repeats that
    /// belong to the neighbouring section. The apron is there because collision is kept by triangle centre
    /// (Collision.Read): a collision triangle of the piece hangs over the region line by up to two thirds of
    /// its size, and what can be stood on should be drawn.
    /// </summary>
    const float Apron = 30f;

    /// <summary>
    /// True when the archetype's box, placed at <paramref name="p"/> with the basis ex, ey, ez, reaches into the
    /// region. Collision is kept per triangle inside the region (Collision.Read), so what is drawn there must not
    /// depend on where the pivot of a 100 to 300 m wide road or terrain piece happens to lie.
    /// </summary>
    static bool Touches(double[] r, Archetype a, Vector3 p, Vector3 ex, Vector3 ey, Vector3 ez)
    {
        if (p.X >= r[0] && p.X < r[2] && p.Y >= r[1] && p.Y < r[3]) return true;
        var c = (a.BBMin + a.BBMax) * 0.5f; var h = (a.BBMax - a.BBMin) * 0.5f;
        float cx = p.X + ex.X * c.X + ey.X * c.Y + ez.X * c.Z, cy = p.Y + ex.Y * c.X + ey.Y * c.Y + ez.Y * c.Z;
        float hx = MathF.Abs(ex.X * h.X) + MathF.Abs(ey.X * h.Y) + MathF.Abs(ez.X * h.Z), hy = MathF.Abs(ex.Y * h.X) + MathF.Abs(ey.Y * h.Y) + MathF.Abs(ez.Y * h.Z);
        if (!(hx <= Reach && hy <= Reach)) return false;      // a box that is not a number, or not a real size: the pivot decides, as before
        return cx + hx > r[0] && cx - hx < r[2] && cy + hy > r[1] && cy - hy < r[3];
    }

    /// <param name="placedInteriors">receives the interiors that were placed whole (their own collision is then kept whole too)</param>
    static List<Sel> Select(Game game, World world, PackOptions o, Counter counts, Dictionary<string, Collision.PlacedInterior> interiors, List<Collision.PlacedInterior> placedInteriors)
    {
        var gfc = game.Gfc;
        var r = o.Region;
        // entities with at least one child in a loaded, switched-on ymap are replaced by their children
        var live = new HashSet<YmapEntityDef>();
        foreach (var y in world.Ymaps)
        {
            if (!y.On || y.File?.AllEntities == null) continue;
            var ents = y.File.AllEntities;
            YmapEntityDef[] pents = null;
            if (y.ParentHash != 0 && world.ByHash.TryGetValue(y.ParentHash, out var py)) pents = py.File?.AllEntities;
            foreach (var ent in ents)
            {
                int pind = ent._CEntityDef.parentIndex;
                if (pind < 0) continue;
                var arr = ent.LodInParentYmap ? pents : ents;
                if (arr != null && pind < arr.Length) live.Add(arr[pind]);
            }
        }
        var sels = new List<Sel>();
        foreach (var y in world.Ymaps)
        {
            if (!y.On || y.File?.AllEntities == null) continue;
            foreach (var ent in y.File.AllEntities)
            {
                var p = ent.Position;
                bool pivotIn = p.X >= r[0] && p.X < r[2] && p.Y >= r[1] && p.Y < r[3];
                if (!pivotIn && (p.X < r[0] - Reach || p.X >= r[2] + Reach || p.Y < r[1] - Reach || p.Y >= r[3] + Reach)) continue;
                var arch = gfc.GetArchetype(ent._CEntityDef.archetypeName);
                if (arch == null) { if (pivotIn) counts.Add("skip: archetype not found"); continue; }
                if (pivotIn && ent.Archetype == null) ent.SetArchetype(arch);
                if (ent.IsMlo || arch is MloArchetype)
                {
                    var mlo = arch as MloArchetype;
                    string aname = (arch.Name ?? "").ToLowerInvariant();
                    Collision.PlacedInterior irec = null;
                    if (mlo?.entities == null || interiors == null || !interiors.TryGetValue(y.Name + "|" + aname, out irec)) { if (pivotIn) counts.Add("interiors skipped (detached, off by default or not exported)"); continue; }
                    // An interior whose origin is in the region is placed whole, with all of its own collision
                    // (Collision.Build gets it through placedInteriors), so a tunnel or car park that crosses the
                    // region line keeps its floor. One whose origin is outside is not: only its objects that reach
                    // into the region are taken, like any other object, and its collision is cut at the region
                    // line like the rest of the world.
                    if (pivotIn) { counts.Add("interiors exported"); placedInteriors.Add(irec); }
                    var mq = ent.Orientation; var mp = ent.Position;
                    int taken = 0;
                    foreach (var me in mlo.entities)
                    {
                        var ce = me._Data;
                        var ca = gfc.GetArchetype(ce.archetypeName);
                        if (ca == null || ca is MloArchetype || ce.numChildren > 0) continue;
                        float sxy = ce.scaleXY, sz = ce.scaleZ;
                        var lq = new Quaternion(ce.rotation);
                        if (lq.LengthSquared() < 1e-12f) lq = Quaternion.Identity;
                        else if (lq != Quaternion.Identity) lq = Quaternion.Invert(lq);       // as CodeWalker places MLO children
                        var cpos = mp + mq.Multiply(ce.position);
                        var cq = Quaternion.Multiply(mq, lq);
                        Vector3 cex = cq.Multiply(new Vector3(sxy, 0, 0)), cey = cq.Multiply(new Vector3(0, sxy, 0)), cez = cq.Multiply(new Vector3(0, 0, sz));
                        if (!pivotIn && !Touches(r, ca, cpos, cex, cey, cez)) continue;
                        // helper meshes drawn only in a shadow or reflection pass
                        if ((ce.flags & 0x0A800000u) != 0 || (ce.flags & 0x21000000u) == 0x20000000u) { counts.Add("skip: interior helper mesh"); continue; }
                        if (SkipReason(ca, ce.flags) != null) continue;
                        float crad = ca.BSRadius * MathF.Max(sxy, sz);
                        if (crad < o.MinSize) { counts.Add("skip: smaller than --min-size"); continue; }
                        sels.Add(new Sel
                        {
                            Y = y, Arch = ca, Pos = cpos, Radius = crad, Tint = ce.tintValue,
                            Ex = cex, Ey = cey, Ez = cez,
                            Tx = (int)MathF.Floor(cpos.X / o.Tile), Ty = (int)MathF.Floor(cpos.Y / o.Tile),
                            Outside = !pivotIn,
                        });
                        counts.Add(pivotIn ? "selected interior object" : "selected interior object (interior origin outside the region)");
                        taken++;
                    }
                    if (!pivotIn && taken > 0) counts.Add("interiors exported in part (origin outside the region)");
                    continue;
                }
                bool leaf = ent._CEntityDef.numChildren == 0;
                int lod = (int)ent._CEntityDef.lodLevel;
                bool hd = lod == 0 || lod == 5;      // HD or ORPHANHD: a real piece of the world; every other level stands in for such pieces
                // A far-view shell that has no children linked to it but says from how far it replaces them
                // (childLodDist): the game never shows it near, and here it would be drawn over the real buildings.
                if (leaf && !hd && ent._CEntityDef.childLodDist > 0) { if (pivotIn) counts.Add("skip: far-view stand-in without linked children"); continue; }
                var q = ent.Orientation; var s = ent.Scale;
                Vector3 ex = q.Multiply(new Vector3(s.X, 0, 0)), ey = q.Multiply(new Vector3(0, s.Y, 0)), ez = q.Multiply(new Vector3(0, 0, s.Z));
                // Drawn when any of it lies in the region, wherever its pivot is. A LOD stand-in is still taken by
                // its pivot alone, whether it has children or not (the level says what it is, not the child count):
                // with the pivot outside, the pieces it stands for may be in ymaps that were not loaded, and the
                // stand-in would be drawn across the region on top of the real thing.
                if (!pivotIn && (!leaf || !hd || !Touches(r, arch, p, ex, ey, ez))) continue;
                if (!leaf && live.Contains(ent)) continue;
                var skip = SkipReason(arch, ent._CEntityDef.flags);
                if (skip != null) { counts.Add(skip); continue; }
                float rad = arch.BSRadius * MathF.Max(s.X, s.Z);
                if (rad < o.MinSize) { counts.Add("skip: smaller than --min-size"); continue; }
                // props: objects with no LOD parent (street furniture, clutter, most plants); buildings, roads and terrain have one
                if (o.NoProps && lod == 5) { counts.Add("skip: prop (--no-props)"); continue; }
                if (!leaf) counts.Add("LOD fallback (children not loaded)");
                if (!pivotIn) counts.Add("selected with the pivot outside the region");
                sels.Add(new Sel
                {
                    Y = y, Arch = arch, Pos = p, Radius = rad, Tint = ent._CEntityDef.tintValue,
                    Ex = ex, Ey = ey, Ez = ez,
                    Tx = (int)MathF.Floor(p.X / o.Tile), Ty = (int)MathF.Floor(p.Y / o.Tile),
                    Outside = !pivotIn,
                });
                counts.Add("selected " + ((uint)lod < 7 ? LodNames[lod] : "?"));
            }
        }
        live.Clear();
        foreach (var y in world.Ymaps) y.File = null;
        return sels;
    }

    sealed class Acc
    {
        public readonly List<float> Pos = new(), Nrm = new(), Uv = new();
        public readonly List<int> Idx = new();
    }

    /// <summary>Studio stores a mesh section with 16-bit vertex indices; larger ones are cut here, in triangle order.</summary>
    public const int MaxSectionVerts = 60000;

    static List<GlbPrim> Prims(SortedDictionary<int, Acc> parts, Material[] mats)
    {
        var list = new List<GlbPrim>();
        // primitives in the order of their material names: material ids follow thread timing
        foreach (var kv in parts.OrderBy(p => mats[p.Key].Name, StringComparer.Ordinal))
        {
            var a = kv.Value;
            string name = mats[kv.Key].Name;
            if (a.Pos.Count / 3 <= MaxSectionVerts)
            {
                list.Add(new GlbPrim { Material = name, Pos = a.Pos.ToArray(), Nrm = a.Nrm.ToArray(), Uv = a.Uv.ToArray(), Idx = a.Idx.ToArray() });
                continue;
            }
            var map = new Dictionary<int, int>();
            List<float> pos = new(), nrm = new(), uv = new(); List<int> idx = new();
            void Flush()
            {
                if (idx.Count > 0) list.Add(new GlbPrim { Material = name, Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv = uv.ToArray(), Idx = idx.ToArray() });
                map = new Dictionary<int, int>(); pos = new(); nrm = new(); uv = new(); idx = new();
            }
            for (int t = 0; t + 2 < a.Idx.Count; t += 3)
            {
                if (pos.Count / 3 + 3 > MaxSectionVerts) Flush();
                for (int k = 0; k < 3; k++)
                {
                    int v = a.Idx[t + k];
                    if (!map.TryGetValue(v, out var ni))
                    {
                        ni = pos.Count / 3; map[v] = ni;
                        pos.Add(a.Pos[v * 3]); pos.Add(a.Pos[v * 3 + 1]); pos.Add(a.Pos[v * 3 + 2]);
                        nrm.Add(a.Nrm[v * 3]); nrm.Add(a.Nrm[v * 3 + 1]); nrm.Add(a.Nrm[v * 3 + 2]);
                        uv.Add(a.Uv[v * 2]); uv.Add(a.Uv[v * 2 + 1]);
                    }
                    idx.Add(ni);
                }
            }
            Flush();
        }
        return list;
    }

    /// <summary>
    /// The triangles of a part, placed by <paramref name="s"/>, whose outline box reaches into <paramref name="box"/>
    /// (x0, y0, x1, y1, GTA metres): their indices, three per triangle, in the part's order. The part's own index
    /// array when every triangle does. The box of the triangle is tested, not its corners: a large triangle can
    /// cross the region with every corner outside it. <paramref name="reaches"/> is set when one of them reaches
    /// into <paramref name="region"/> itself.
    /// </summary>
    static int[] Kept(SrcPart p, Sel s, float[] box, double[] region, ref bool reaches)
    {
        int nv = p.NVerts;
        var wx = new float[nv]; var wy = new float[nv];
        for (int i = 0; i < nv; i++)
        {
            float x = p.Pos[i * 3], y = p.Pos[i * 3 + 1], z = p.Pos[i * 3 + 2];
            wx[i] = s.Pos.X + s.Ex.X * x + s.Ey.X * y + s.Ez.X * z; wy[i] = s.Pos.Y + s.Ex.Y * x + s.Ey.Y * y + s.Ez.Y * z;
        }
        List<int> keep = null;      // made at the first triangle that is left out
        var idx = p.Idx;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
            float x0 = MathF.Min(wx[i0], MathF.Min(wx[i1], wx[i2])), x1 = MathF.Max(wx[i0], MathF.Max(wx[i1], wx[i2]));
            float y0 = MathF.Min(wy[i0], MathF.Min(wy[i1], wy[i2])), y1 = MathF.Max(wy[i0], MathF.Max(wy[i1], wy[i2]));
            if (x1 >= box[0] && x0 <= box[2] && y1 >= box[1] && y0 <= box[3])
            {
                if (keep != null) { keep.Add(i0); keep.Add(i1); keep.Add(i2); }
                if (!reaches && x1 >= region[0] && x0 < region[2] && y1 >= region[1] && y0 < region[3]) reaches = true;
            }
            else if (keep == null) { keep = new List<int>(idx.Length); for (int k = 0; k < t; k++) keep.Add(idx[k]); }
        }
        return keep == null ? idx : keep.ToArray();
    }

    /// <summary>
    /// Append a part, moved by pos + basis (or as it is when <paramref name="s"/> is null). With
    /// <paramref name="only"/> (from <see cref="Kept"/>), just those triangles and the vertices they use.
    /// </summary>
    static void Append(Acc a, SrcPart p, Sel s, Vector3 origin, ref float zmin, ref float zmax, int[] only = null)
    {
        int vbase = a.Pos.Count / 3;
        int nv = p.NVerts;
        if (s == null)
        {
            a.Pos.AddRange(p.Pos); a.Nrm.AddRange(p.Nrm);
            a.Uv.AddRange(p.Uv);
            foreach (var i in p.Idx) a.Idx.Add(vbase + i);
            return;
        }
        var ex = s.Ex; var ey = s.Ey; var ez = s.Ez;
        var nx = ex / MathF.Max(ex.LengthSquared(), 1e-12f); var ny = ey / MathF.Max(ey.LengthSquared(), 1e-12f); var nz = ez / MathF.Max(ez.LengthSquared(), 1e-12f);
        var t = s.Pos - origin;
        float lo = zmin, hi = zmax;
        void Vertex(int i)
        {
            float x = p.Pos[i * 3], y = p.Pos[i * 3 + 1], z = p.Pos[i * 3 + 2];
            float wz = t.Z + ex.Z * x + ey.Z * y + ez.Z * z;
            a.Pos.Add(t.X + ex.X * x + ey.X * y + ez.X * z); a.Pos.Add(t.Y + ex.Y * x + ey.Y * y + ez.Y * z); a.Pos.Add(wz);
            if (wz < lo) lo = wz; if (wz > hi) hi = wz;
            float a0 = p.Nrm[i * 3], a1 = p.Nrm[i * 3 + 1], a2 = p.Nrm[i * 3 + 2];
            var n = nx * a0 + ny * a1 + nz * a2;
            float len = n.Length();
            if (len > 1e-8f) n /= len; else n = Vector3.UnitZ;
            a.Nrm.Add(n.X); a.Nrm.Add(n.Y); a.Nrm.Add(n.Z);
        }
        if (only == null || ReferenceEquals(only, p.Idx))
        {
            for (int i = 0; i < nv; i++) Vertex(i);
            a.Uv.AddRange(p.Uv);
            foreach (var i in p.Idx) a.Idx.Add(vbase + i);
        }
        else
        {
            var remap = new int[nv];
            Array.Fill(remap, -1);
            foreach (var i in only)
            {
                if (remap[i] < 0)
                {
                    remap[i] = a.Pos.Count / 3;
                    Vertex(i);
                    a.Uv.Add(p.Uv[i * 2]); a.Uv.Add(p.Uv[i * 2 + 1]);
                }
                a.Idx.Add(remap[i]);
            }
        }
        zmin = lo; zmax = hi;
    }

    /// <summary>Sutherland-Hodgman, one step: the part of a convex outline on one side of the line x = k or y = k.</summary>
    static List<(float x, float y)> ClipHalf(List<(float x, float y)> p, bool alongY, float k, bool keepAbove)
    {
        var o = new List<(float x, float y)>(p.Count + 1);
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            float da = (alongY ? a.y : a.x) - k, db = (alongY ? b.y : b.x) - k;
            if (!keepAbove) { da = -da; db = -db; }
            if (da >= 0) o.Add(a);
            if ((da > 0 && db < 0) || (da < 0 && db > 0))   // strict: a corner on the line is not added twice
            {
                float t = da / (da - db);
                o.Add(alongY ? (a.x + (b.x - a.x) * t, k) : (k, a.y + (b.y - a.y) * t));
            }
        }
        return o;
    }

    /// <summary>
    /// The game's water quads (water.xml, already exported by gta-export as water/water.json: sea at z 0, Alamo
    /// Sea, reservoirs) that touch the region, as one mesh with Studio's ocean material and water collision.
    /// </summary>
    static MapObject Water(string exportDir, double[] r, string meshDir, string material, Vector3 off, float limit)
    {
        var path = Path.Combine(exportDir, "water", "water.json");
        if (!File.Exists(path)) return null;
        var origin = new Vector3((float)(r[0] + r[2]) / 2, (float)(r[1] + r[3]) / 2, 0f);
        var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var q in doc.RootElement.GetProperty("main").GetProperty("quads").EnumerateArray())
        {
            if (q.TryGetProperty("IsInvisible", out var inv) && inv.ValueKind == System.Text.Json.JsonValueKind.True) continue;
            double minX = q.GetProperty("minX").GetDouble(), maxX = q.GetProperty("maxX").GetDouble(), minY = q.GetProperty("minY").GetDouble(), maxY = q.GetProperty("maxY").GetDouble();
            if (maxX <= r[0] || minX >= r[2] || maxY <= r[1] || minY >= r[3]) continue;
            float z = (float)q.GetProperty("z").GetDouble();
            // Clip the outline to the box (region + 400 m, inside the world limit). Clamping each corner instead
            // swings the sloping edge of a triangular quad (Type 1-4), which loses or adds a wedge of water.
            float bx0 = Math.Max((float)r[0] - 400f, -limit - off.X), bx1 = Math.Min((float)r[2] + 400f, limit - off.X);
            float by0 = Math.Max((float)r[1] - 400f, -limit - off.Y), by1 = Math.Min((float)r[3] + 400f, limit - off.Y);
            var cs = q.GetProperty("corners").EnumerateArray().Select(c => (x: (float)c[0].GetDouble(), y: (float)c[1].GetDouble())).ToList();
            cs = ClipHalf(ClipHalf(ClipHalf(ClipHalf(cs, false, bx0, true), false, bx1, false), true, by0, true), true, by1, false);
            if (cs.Count < 3) continue;
            float area = 0;
            for (int i = 0; i < cs.Count; i++) { var a = cs[i]; var b = cs[(i + 1) % cs.Count]; area += a.x * b.y - b.x * a.y; }
            if (MathF.Abs(area) < 1f) continue;   // clipped away
            if (area < 0) cs.Reverse();         // counter-clockwise seen from above
            int v0 = pos.Count / 3;
            foreach (var c in cs)
            {
                pos.Add(c.x - origin.X); pos.Add(c.y - origin.Y); pos.Add(z);
                nrm.Add(0); nrm.Add(0); nrm.Add(1);
                uv.Add(c.x * 0.05f); uv.Add(-c.y * 0.05f);
            }
            for (int i = 1; i + 1 < cs.Count; i++) { idx.Add(v0); idx.Add(v0 + i); idx.Add(v0 + i + 1); }
        }
        if (idx.Count == 0) return null;
        Glb.Write(Path.Combine(meshDir, "water.glb"), "water", new[] { new GlbPrim { Material = material, Pos = pos.ToArray(), Nrm = nrm.ToArray(), Uv = uv.ToArray(), Idx = idx.ToArray() } });
        Log.Info($"water: {idx.Count / 3} triangles from the game's water quads");
        return new MapObject
        {
            Name = "water", Mesh = "meshes/water.glb", Primitive = 0, Material = material, Transform = MapObject.Translation(origin.X + off.X, origin.Y + off.Y, 0),
            CollisionMode = "water", CollisionPacked = 0x00F007A0,
        };
    }

    sealed class Proto
    {
        public ProtoInfo Info;
        public string[] Materials;     // material name per primitive
        public float[][] Origins;      // per primitive: bounding-box centre (y up) when it may be shared, else null
        public int Mesh;               // model mode: mesh index in the glTF scene
    }

    public static void Run(Game game, string outRoot, string collisionDir, int threads, PackOptions o)
    {
        double tStart = Log.Seconds;
        var gfc = game.Gfc;
        var outDir = Path.Combine(outRoot, o.Name);
        string meshDir = Path.Combine(outDir, o.Model ? "geometry" : "meshes"), texDir = Path.Combine(outDir, "textures");
        var doc = o.Model ? new GltfDoc() : null;
        foreach (var d in new[] { meshDir, texDir }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
        ShaderTable.LoadNames(Path.Combine(AppContext.BaseDirectory, "ShadersGen9Conversion.xml"));

        var r = o.Region;
        var off = new Vector3((float)o.Offset[0], (float)o.Offset[1], 0f);
        var pm = o.Model ? null : new PauseMap(r, off, o.MapHeight);
        var counts = new Counter();
        var rules = MapStateRules.Load(collisionDir);
        var keep = new BoundingBox(new Vector3((float)r[0] - Reach, (float)r[1] - Reach, -1e6f), new Vector3((float)r[2] + Reach, (float)r[3] + Reach, 1e6f));
        var world = World.Load(game, rules, threads, keep);
        double t0 = Log.Seconds;
        var placedInteriors = new List<Collision.PlacedInterior>();
        var sels = Select(game, world, o, counts, o.Interiors ? InteriorSet(collisionDir) : null, placedInteriors);
        GC.Collect();

        var archCount = new Dictionary<uint, int>();
        foreach (var s in sels) { archCount.TryGetValue(s.Arch.Hash.Hash, out var c); archCount[s.Arch.Hash.Hash] = c + 1; }
        int rowMin = sels.Count > 0 ? sels.Min(s => s.Ty) : 0, rowMax = sels.Count > 0 ? sels.Max(s => s.Ty) : 0;
        int nBands = (rowMax - rowMin) / o.BandRows + 1;
        var perBand = new List<Sel>[nBands];
        for (int b = 0; b < nBands; b++) perBand[b] = new List<Sel>();
        var archLast = new Dictionary<uint, int>();
        foreach (var s in sels)
        {
            int b = (s.Ty - rowMin) / o.BandRows;
            perBand[b].Add(s);
            if (!archLast.TryGetValue(s.Arch.Hash.Hash, out var last) || last < b) archLast[s.Arch.Hash.Hash] = b;
        }
        Log.Info($"selected {sels.Count} entities, {archCount.Count} archetypes, {nBands} band(s) of {o.BandRows} tile rows ({o.Tile} m tiles), in {Log.Seconds - t0:F1}s");

        var tex = new TexLib(game, o.TexBudget, texDir, o.TexCap) { PngOnly = o.Model };
        var mats = new MatLib(tex);
        var shaders = new ShaderTable();
        var builder = new MeshBuilder(tex, mats, shaders) { Level = o.Level, TreeLevel = o.TreeLevel };
        var meshes = new ConcurrentDictionary<uint, SrcMesh>();
        var failed = new ConcurrentDictionary<uint, byte>();
        var protos = new ConcurrentDictionary<(uint arch, uint row), Lazy<Proto>>();
        var protoList = new List<ProtoInfo>();
        var tiles = new ConcurrentBag<TileOut>();
        var objects = new ConcurrentBag<MapObject>();
        var matTris = new ConcurrentDictionary<int, long>();
        // prop collision per 256 m collision tile, as one piece per map tile that has props there (joined before Collision.Build)
        var propParts = new ConcurrentDictionary<(int, int), ConcurrentBag<(int tx, int ty, Collision.PropSoup soup)>>();
        var propColTris = new ConcurrentDictionary<string, long>();
        var surfaceOf = Collision.LoadSurfaceTable(collisionDir);
        long clipped = 0, loadFail = 0, noFile = 0, batchTris = 0, instTris = 0, instCount = 0, batchEntities = 0, emptyMesh = 0, peakMeshBytes = 0, farDropped = 0;
        // what is kept of the pieces whose pivot lies outside the region: the region grown by the apron (GTA metres)
        var edge = new[] { (float)r[0] - Apron, (float)r[1] - Apron, (float)r[2] + Apron, (float)r[3] + Apron };
        // materials some written mesh uses. A texture is written when its drawable is built, before it is known
        // which placements are drawn; those of materials not in here are removed again at the end.
        var matUsed = new ConcurrentDictionary<int, byte>();

        // colour of a material on the pause-map picture; 0 = not drawn (decals, glass and other see-through layers)
        var mapColour = new ConcurrentDictionary<int, uint>();
        uint MapColour(int mat) => mapColour.GetOrAdd(mat, id =>
        {
            var mm = mats.ById(id);
            if (mm.Class is VClass.Decal or VClass.Glass or VClass.Alpha) return 0u;
            uint c = tex.MeanOf(mm.Tex);
            return c == 0 ? 0x010101u : c;
        });

        Proto MakeProto(SrcMesh m, uint row)
        {
            var parts = new SortedDictionary<int, Acc>();
            float zmin = 0, zmax = 0;
            foreach (var p in m.Parts)
            {
                int mat = builder.MaterialOf(p, row);
                if (!parts.TryGetValue(mat, out var a)) parts[mat] = a = new Acc();
                Append(a, p, null, Vector3.Zero, ref zmin, ref zmax);
            }
            foreach (var k in parts.Keys) matUsed[k] = 1;
            ProtoInfo pi;
            lock (protoList)
            {
                pi = new ProtoInfo { Id = protoList.Count, Name = m.Name, Tris = m.Tris, Verts = m.Verts, Parts = parts.Count, Min = m.Min, Max = m.Max, Tree = m.IsTree };
                protoList.Add(pi);
            }
            // named after the archetype and its tint row, which are what make a prototype: the same prop is the same
            // file in every run and in every export (pi.Id follows thread timing)
            string stem = $"p_{m.Hash:x8}_{row}";
            pi.File = stem + ".glb";
            var prims = Prims(parts, mats.All);
            if (doc != null)
                return new Proto { Info = pi, Materials = Array.Empty<string>(), Mesh = doc.AddMeshes(outDir, $"geometry/{stem}.bin", new[] { (m.Name, prims) }) };
            Glb.Write(Path.Combine(meshDir, pi.File), stem, prims);
            // Studio only instances a record flagged shared_geometry, and its own importer flags a prototype whose
            // half-extent is at most 32 m (MAX_RENDER_LOCAL_REACH); anything else is baked per placement.
            var origins = new float[prims.Count][];
            for (int i = 0; i < prims.Count; i++)
            {
                var pp = prims[i].Pos;
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                for (int v = 0; v + 2 < pp.Length; v += 3) { var q = new Vector3(pp[v], pp[v + 1], pp[v + 2]); lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                var half = (hi - lo) * 0.5f; var c = (hi + lo) * 0.5f;
                if (pp.Length > 0 && MathF.Max(half.X, MathF.Max(half.Y, half.Z)) <= 32f) origins[i] = new[] { c.X, c.Z, -c.Y };
            }
            return new Proto { Info = pi, Materials = prims.Select(p => p.Material).ToArray(), Origins = origins };
        }

        for (int band = 0; band < nBands; band++)
        {
            var bandSels = perBand[band];
            if (bandSels.Count == 0) continue;
            double tb = Log.Seconds;

            // --- load and build the drawables this band needs
            var jobs = new Dictionary<RpfFileEntry, (string kind, List<Archetype> archs)>();
            var seen = new HashSet<uint>();
            foreach (var s in bandSels)
            {
                uint h = s.Arch.Hash.Hash;
                if (!seen.Add(h) || meshes.ContainsKey(h) || failed.ContainsKey(h)) continue;
                var fe = DrawableEntry(gfc, s.Arch, out var kind);
                if (fe == null) { failed[h] = 1; noFile++; continue; }
                if (!jobs.TryGetValue(fe, out var j)) jobs[fe] = j = (kind, new List<Archetype>());
                j.archs.Add(s.Arch);
            }
            Parallel.ForEach(jobs.OrderByDescending(j => j.Key.FileSize), new ParallelOptions { MaxDegreeOfParallelism = threads }, job =>
            {
                var fe = job.Key;
                Dictionary<uint, Drawable> dict = null;
                DrawableBase single = null;
                Bounds fragBound = null;
                try
                {
                    switch (job.Value.kind)
                    {
                        case "ydr": single = game.Load<YdrFile>(fe)?.Drawable; break;
                        case "ydd": dict = game.Load<YddFile>(fe)?.Dict; break;
                        case "yft":
                        {
                            var frag = game.Load<YftFile>(fe)?.Fragment;
                            single = frag?.Drawable;
                            fragBound = frag?.PhysicsLODGroup?.PhysicsLOD1?.Bound;
                            break;
                        }
                    }
                }
                catch (Exception) { }
                if (single == null && dict == null)
                {
                    Interlocked.Increment(ref loadFail);
                    foreach (var arch in job.Value.archs) failed[arch.Hash.Hash] = 1;
                    return;
                }
                foreach (var arch in job.Value.archs)
                {
                    DrawableBase d = single;
                    if (dict != null) { dict.TryGetValue(arch.Hash.Hash, out var dd); d = dd; }
                    if (d == null) { failed[arch.Hash.Hash] = 1; continue; }
                    try
                    {
                        var built = builder.Build(d, arch);
                        if (o.PropCollision) Collision.FromBound(fragBound ?? (d as Drawable)?.Bound, surfaceOf, built);
                        meshes[arch.Hash.Hash] = built;
                    }
                    catch (Exception ex) { failed[arch.Hash.Hash] = 1; Log.Warn($"build failed for {arch.Name}: {ex.GetType().Name} {ex.Message}"); }
                }
            });
            peakMeshBytes = Math.Max(peakMeshBytes, meshes.Values.Sum(m => m.Bytes));
            double tBuilt = Log.Seconds;

            // --- tiles of this band
            var byTile = bandSels.GroupBy(s => (s.Tx, s.Ty)).ToArray();
            Parallel.ForEach(byTile, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                var (tx, ty) = g.Key;
                var origin = new Vector3((tx + 0.5f) * o.Tile, (ty + 0.5f) * o.Tile, 0f);
                var to = new TileOut { X = tx, Y = ty };
                var parts = new SortedDictionary<int, Acc>();
                var soups = new Dictionary<(int, int), Collision.PropSoup>();   // this tile's prop collision, in the order of the loop below
                foreach (var s in g.OrderBy(s => s.Arch.Hash.Hash).ThenBy(s => s.Pos.X).ThenBy(s => s.Pos.Y).ThenBy(s => s.Pos.Z))
                {
                    if (!meshes.TryGetValue(s.Arch.Hash.Hash, out var m) || m.Tris == 0) { Interlocked.Increment(ref emptyMesh); continue; }
                    bool inst = archCount[m.Hash] >= o.MinInstances && m.Tris >= o.MinInstanceTris;
                    // A piece taken for its box, with the pivot outside the region. The box (an upright one around a
                    // turned one) only says the piece may reach in; the mesh says what does. No triangle in the
                    // region: the piece is left out. Otherwise only its triangles near the region are merged (see
                    // Apron). A repeated piece is placed whole, as every instance is, unless it is too large for
                    // Studio to share (half-extent above 32 m, see MakeProto): then it is merged, so that it can be cut.
                    int[][] kept = null;
                    if (s.Outside)
                    {
                        kept = new int[m.Parts.Count][];
                        bool reaches = false;
                        for (int i = 0; i < kept.Length; i++) kept[i] = Kept(m.Parts[i], s, edge, r, ref reaches);
                        if (!reaches) { Interlocked.Increment(ref farDropped); continue; }
                        var half = (m.Max - m.Min) * 0.5f;
                        if (inst && MathF.Max(half.X, MathF.Max(half.Y, half.Z)) > 32f) inst = false;
                        if (inst) kept = null;
                    }
                    to.Entities++;
                    uint row = m.HasTint ? s.Tint : 0u;
                    if (!o.Model && (MathF.Abs(s.Pos.X + off.X) + s.Radius > o.Limit || MathF.Abs(s.Pos.Y + off.Y) + s.Radius > o.Limit) && inst)
                    { Interlocked.Increment(ref clipped); continue; }
                    // after the limit test: an instance that is not drawn must not collide either; of a piece that is
                    // cut, the collision is cut the same way
                    int colTris = Collision.AddProp(soups, m, s.Pos, s.Ex, s.Ey, s.Ez, kept != null ? edge : null);
                    if (colTris > 0) propColTris.AddOrUpdate(m.Name, colTris, (_, c) => c + colTris);
                    if (inst)
                    {
                        var pr = protos.GetOrAdd((m.Hash, row), k => new Lazy<Proto>(() => MakeProto(m, k.row))).Value;
                        Interlocked.Increment(ref pr.Info.Count);
                        // tile and number within the tile (this loop runs in a fixed order), not a counter shared by the threads
                        string iname = $"i_{tx}_{ty}_{to.Instances}_{pr.Info.Name}";
                        var rows = MapObject.Rows(s.Ex, s.Ey, s.Ez, s.Pos + off);
                        if (doc != null) doc.AddNode(iname, pr.Mesh, rows);
                        if (pm != null)
                            foreach (var p in m.Parts)
                            {
                                uint pc = MapColour(builder.MaterialOf(p, row));
                                if (pc != 0) pm.Draw(p.Pos, p.Idx, s.Pos + off, s.Ex, s.Ey, s.Ez, pc);
                            }
                        for (int i = 0; i < pr.Materials.Length; i++)
                            objects.Add(new MapObject
                            {
                                Name = pr.Materials.Length == 1 ? iname : $"{iname} / section {i + 1}",
                                Mesh = "meshes/" + pr.Info.File, Primitive = i, Material = pr.Materials[i], Transform = rows,
                                SharedOrigin = pr.Origins?[i],
                            });
                        to.Instances++;
                        Interlocked.Increment(ref instCount);
                        Interlocked.Add(ref instTris, m.Tris);
                        continue;
                    }
                    Interlocked.Increment(ref batchEntities);
                    for (int pi = 0; pi < m.Parts.Count; pi++)
                    {
                        var p = m.Parts[pi];
                        if (kept != null && kept[pi].Length == 0) continue;      // before MaterialOf: a tinted part writes its texture there
                        int mat = builder.MaterialOf(p, row);
                        if (!parts.TryGetValue(mat, out var a)) parts[mat] = a = new Acc();
                        Append(a, p, s, origin, ref to.ZMin, ref to.ZMax, kept?[pi]);
                    }
                }
                if (parts.Count > 0)
                {
                    to.Mesh = $"t_{tx}_{ty}.glb";
                    if (pm != null)
                        foreach (var kv in parts)
                        {
                            uint pc = MapColour(kv.Key);
                            if (pc != 0) pm.Draw(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(kv.Value.Pos), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(kv.Value.Idx),
                                new Vector3(origin.X + off.X, origin.Y + off.Y, 0f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, pc);
                        }
                    float lim = o.Model ? float.MaxValue : o.Limit;
                    float ox = origin.X + off.X, oy = origin.Y + off.Y;
                    if (MathF.Abs(ox) + 600f > lim || MathF.Abs(oy) + 600f > lim)
                        foreach (var a in parts.Values)
                        {
                            var keepIdx = new List<int>(a.Idx.Count);
                            for (int t = 0; t + 2 < a.Idx.Count; t += 3)
                            {
                                bool inside = true;
                                for (int k = 0; k < 3 && inside; k++)
                                {
                                    int v = a.Idx[t + k] * 3;
                                    inside = MathF.Abs(a.Pos[v] + ox) <= lim && MathF.Abs(a.Pos[v + 1] + oy) <= lim;
                                }
                                if (inside) { keepIdx.Add(a.Idx[t]); keepIdx.Add(a.Idx[t + 1]); keepIdx.Add(a.Idx[t + 2]); }
                            }
                            if (keepIdx.Count != a.Idx.Count)
                            {
                                Interlocked.Add(ref clipped, (a.Idx.Count - keepIdx.Count) / 3);
                                // the vertices only the dropped triangles used go too: the GLB's POSITION accessor and
                                // its min / max (the object's bounds) cover every vertex, referenced or not
                                var remap = new int[a.Pos.Count / 3];
                                Array.Fill(remap, -1);
                                var cPos = new List<float>(); var cNrm = new List<float>(); var cUv = new List<float>();
                                for (int i = 0; i < keepIdx.Count; i++)
                                {
                                    int vi = keepIdx[i];
                                    if (remap[vi] < 0)
                                    {
                                        remap[vi] = cPos.Count / 3;
                                        cPos.Add(a.Pos[vi * 3]); cPos.Add(a.Pos[vi * 3 + 1]); cPos.Add(a.Pos[vi * 3 + 2]);
                                        cNrm.Add(a.Nrm[vi * 3]); cNrm.Add(a.Nrm[vi * 3 + 1]); cNrm.Add(a.Nrm[vi * 3 + 2]);
                                        cUv.Add(a.Uv[vi * 2]); cUv.Add(a.Uv[vi * 2 + 1]);
                                    }
                                    keepIdx[i] = remap[vi];
                                }
                                a.Pos.Clear(); a.Pos.AddRange(cPos); a.Nrm.Clear(); a.Nrm.AddRange(cNrm); a.Uv.Clear(); a.Uv.AddRange(cUv);
                                a.Idx.Clear(); a.Idx.AddRange(keepIdx);
                            }
                        }
                    foreach (var k in parts.Where(kv => kv.Value.Idx.Count == 0).Select(kv => kv.Key).ToList()) parts.Remove(k);
                    var prims = Prims(parts, mats.All);
                    var rows = MapObject.Translation(origin.X + off.X, origin.Y + off.Y, origin.Z);
                    if (doc != null)
                    {
                        doc.AddNode($"t_{tx}_{ty}", doc.AddMeshes(outDir, $"geometry/t_{tx}_{ty}.bin", new[] { ($"t_{tx}_{ty}", prims) }), rows);
                        prims = new List<GlbPrim>();
                    }
                    else Glb.Write(Path.Combine(meshDir, to.Mesh), $"t_{tx}_{ty}", prims);
                    for (int i = 0; i < prims.Count; i++)
                        objects.Add(new MapObject
                        {
                            Name = prims.Count == 1 ? $"t_{tx}_{ty}" : $"t_{tx}_{ty} / section {i + 1}",
                            Mesh = "meshes/" + to.Mesh, Primitive = i, Material = prims[i].Material, Transform = rows,
                        });
                    to.Parts = prims.Count;
                    foreach (var kv in parts)
                    {
                        to.Tris += kv.Value.Idx.Count / 3; to.Verts += kv.Value.Pos.Count / 3;
                        matTris.AddOrUpdate(kv.Key, kv.Value.Idx.Count / 3, (_, c) => c + kv.Value.Idx.Count / 3);
                        matUsed[kv.Key] = 1;
                    }
                    Interlocked.Add(ref batchTris, to.Tris);
                }
                if (to.Mesh != null || to.Instances > 0) tiles.Add(to);
                foreach (var kv in soups) propParts.GetOrAdd(kv.Key, _ => new()).Add((tx, ty, kv.Value));
            });

            // --- drop drawables no later band uses
            foreach (var kv in archLast) if (kv.Value == band) meshes.TryRemove(kv.Key, out _);
            Log.Info($"band {band + 1}/{nBands}: {bandSels.Count} entities, {jobs.Count} files, build {tBuilt - tb:F1}s, tiles {Log.Seconds - tBuilt:F1}s; textures {tex.All.Length}, protos {protoList.Count}");
            GC.Collect();
        }

        if (farDropped > 0) counts.Add("left out again: pivot outside the region and no triangle in it", farDropped);
        if (pm != null) { pm.Save(outDir); Log.Info($"pause map: ui/map_image.png from {pm.Triangles / 1e6:F1} M triangles"); }

        // ---------------------------------------------------------------- collision
        double tc = Log.Seconds;
        float sx = o.Spawn != null ? (float)o.Spawn[0] : (float)(r[0] + r[2]) / 2, sy = o.Spawn != null ? (float)o.Spawn[1] : (float)(r[1] + r[3]) / 2;
        string colDir = meshDir;
        if (o.Model) { colDir = Path.Combine(outDir, "collision"); if (Directory.Exists(colDir)) Directory.Delete(colDir, true); Directory.CreateDirectory(colDir); }
        var queries = new List<(float, float)> { (sx, sy) };
        queries.AddRange(o.Stops.Select(s => (s.x, s.y)));
        // The props of a collision tile come from several map tiles that were built in parallel. They are joined in
        // tile order (row, then column): the order decides the vertex order of the collision meshes and which of two
        // vertices within a millimetre the weld keeps.
        var propCol = new ConcurrentDictionary<(int, int), Collision.PropSoup>();
        foreach (var key in propParts.Keys.ToArray())
        {
            if (!propParts.TryRemove(key, out var bag)) continue;
            var soup = new Collision.PropSoup();
            foreach (var part in bag.OrderBy(p => p.ty).ThenBy(p => p.tx)) { soup.Pos.AddRange(part.soup.Pos); soup.Surf.AddRange(part.soup.Surf); }
            propCol[key] = soup;
        }
        var col = Collision.Build(collisionDir, r, colDir, threads, queries, propCol, off.X, off.Y, o.Interiors, o.Model ? float.MaxValue : o.Limit, placedInteriors, world: o.Model);
        Log.Info($"collision: {col.Pieces} pieces, {col.Triangles / 1e6:F2} M triangles ({col.PropTriangles / 1e6:F2} M from props, {col.InteriorTriangles / 1e6:F2} M interiors), {col.SplitCells} cells split, {col.DenseLeaves} still dense at {Collision.MinCell} m, " +
                 $"flat triangles facing up / down {col.UpTris} / {col.DownTris}, in {Log.Seconds - tc:F1}s");
        if (o.Spawn == null || o.Spawn.Length < 3) { sx = col.Placed[0].x; sy = col.Placed[0].y; }
        float sz = o.Spawn != null && o.Spawn.Length > 2 ? (float)o.Spawn[2] : (float.IsNaN(col.Ground[0]) ? 50f : col.Ground[0] + 0.3f);

        // ---------------------------------------------------------------- sea and lakes (the game's water quads)
        const string OceanMaterial = "ocean_water";
        MapObject water = o.Water && !o.Model ? Water(collisionDir, r, meshDir, OceanMaterial, off, o.Limit) : null;

        // ---------------------------------------------------------------- map.json
        tex.Finish();
        var allMat = mats.All;
        // textures only the left-out geometry used (pieces beyond the apron, instances beyond the limit) go again:
        // every texture file of a level costs one of the game's streaming-texture handles
        int texRemoved = tex.Prune(new HashSet<int>(matUsed.Keys.Select(id => allMat[id].Tex).Where(t => t >= 0)));
        var allTex = tex.All;                                   // by id, as the materials refer to them
        var outTex = allTex.Where(t => !t.Removed).ToArray();   // the files of this export
        foreach (var m in allMat) m.Tris = matTris.TryGetValue(m.Id, out var c) ? c : 0;
        var usedMats = new HashSet<string>(objects.Where(ob => ob.Material != null).Select(ob => ob.Material));
        var objArr = objects.OrderBy(ob => ob.Name, StringComparer.Ordinal).Concat(col.Objects.OrderBy(ob => ob.Name, StringComparer.Ordinal))
            .Concat(water != null ? new[] { water } : Array.Empty<MapObject>()).ToArray();
        if (doc != null)
        {
            var gm = new Dictionary<string, GltfMaterial>();
            foreach (var m in allMat)
                gm[m.Name] = new GltfMaterial { Name = m.Name, Alpha = m.Alpha, Cutoff = m.Cutoff, TwoSided = m.TwoSided, TextureUri = m.Tex >= 0 ? "textures/" + allTex[m.Tex].File : null };
            doc.Save(Path.Combine(outDir, o.Name + ".gltf"), gm);
            Log.Info($"model: {o.Name}.gltf with {doc.Nodes} nodes, {doc.Meshes} meshes, {doc.Triangles / 1e6:F1} M unique triangles, {doc.Bytes / 1073741824.0:F2} GiB of geometry buffers");
        }
        else Paths.WriteJson(Path.Combine(outDir, "map.json"), w =>
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("name", o.Name); w.WriteString("display_name", o.DisplayName ?? o.Name);
            w.WriteString("units", "meters"); w.WriteString("up", "y"); w.WriteString("forward", "-z");
            w.WriteString("generator", "gta-skate 1 (normalized map, written without Blender)");
            w.WriteStartObject("materials");
            foreach (var m in allMat.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (!usedMats.Contains(m.Name)) continue;
                string domain = m.Domain switch { 1 => "decal", 2 => "foliage", _ => "surface" };
                string alpha = m.Alpha switch { 2 => "mask", 3 => "blend", _ => "opaque" };
                w.WriteStartObject(m.Name);
                w.WriteString("surface", "default"); w.WriteBoolean("srgb", true);
                w.WriteString("domain", domain); w.WriteString("shader_override", domain);
                w.WriteString("alpha_source", m.Alpha == 1 ? "constant" : "base_color_texture");
                w.WriteNumber("alpha_cutoff", m.Cutoff);
                w.WriteBoolean("cast_shadows", m.Class != VClass.Decal && m.Class != VClass.Water);
                w.WriteBoolean("double_sided", m.TwoSided);
                w.WriteBoolean("transparent_shadow", m.Alpha == 2);
                w.WriteString("alpha", alpha);
                w.WriteString("blend_method", m.Alpha == 3 ? "blend" : "clip");
                w.WriteNumber("roughness", m.Class is VClass.Glass or VClass.Water ? 0.15 : 0.85); w.WriteNumber("metallic", 0.0);
                if (m.Tex >= 0) w.WriteString("texture", "textures/" + allTex[m.Tex].File);
                w.WriteEndObject();
            }
            if (water != null)
            {
                // Studio's native ocean: no texture, the game's own water shader
                w.WriteStartObject(OceanMaterial);
                w.WriteString("surface", "water"); w.WriteBoolean("srgb", true);
                w.WriteString("domain", "ocean"); w.WriteString("shader_override", "ocean"); w.WriteString("alpha", "opaque");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("surface_profiles"); w.WriteEndObject();
            w.WriteStartObject("streaming_distances"); w.WriteNumber("near", 50.0); w.WriteNumber("medium", 250.0); w.WriteNumber("far", 500.0); w.WriteEndObject();
            w.WriteStartArray("objects");
            foreach (var ob in objArr)
            {
                w.WriteStartObject();
                w.WriteString("name", ob.Name);
                w.WriteString("placement_mode", "authored_mesh");
                if (ob.Render)
                {
                    w.WriteString("mesh", ob.Mesh); w.WriteNumber("primitive", ob.Primitive); w.WriteString("material", ob.Material);
                    if (ob.SharedOrigin != null)
                    {
                        w.WriteBoolean("world_transform_baked", false); w.WriteBoolean("shared_geometry", true);
                        w.WriteStartArray("native_render_origin"); foreach (var v in ob.SharedOrigin) w.WriteNumberValue(v); w.WriteEndArray();
                    }
                }
                else
                {
                    w.WriteBoolean("render", false); w.WriteString("collision_mesh", ob.CollisionMesh);
                }
                w.WriteStartArray("transform"); foreach (var v in ob.Transform) w.WriteNumberValue(v); w.WriteEndArray();
                w.WriteBoolean("static", true);
                w.WriteString("collision_mode", ob.CollisionMode);
                w.WriteString("collision_material", $"material_{ob.CollisionPacked:D4}");
                w.WriteNumber("collision_material_packed", ob.CollisionPacked);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            foreach (var k in new[] { "lights", "audio_volumes", "audio_emitters", "interaction_prefabs", "trigger_effects", "vfx_prefabs", "npc_routes", "warnings" })
            { w.WriteStartArray(k); w.WriteEndArray(); }
            w.WriteStartArray("travel_points");
            for (int i = 0; i < o.Stops.Count; i++)
            {
                var st = o.Stops[i];
                float gz = st.z;
                if (float.IsNaN(gz)) { st.x = col.Placed[i + 1].x; st.y = col.Placed[i + 1].y; gz = col.Ground[i + 1]; }
                if (float.IsNaN(gz)) { Log.Warn($"bus stop '{st.name}' has no ground under it and is left out"); continue; }
                string id = i == 0 ? "TravelPoint" : $"TravelPoint.{i:D3}";
                w.WriteStartObject();
                w.WriteString("name", id); w.WriteString("source_name", id); w.WriteString("source_path", id);
                w.WriteStartArray("source_collections"); w.WriteStringValue("Markers"); w.WriteEndArray();
                w.WriteStartArray("position"); w.WriteNumberValue(JsonExt.R(st.x + off.X)); w.WriteNumberValue(JsonExt.R(gz + 0.05)); w.WriteNumberValue(JsonExt.R(-(st.y + off.Y))); w.WriteEndArray();
                w.WriteNumber("yaw", st.yaw);
                w.WriteString("stop_name", st.name); w.WriteBoolean("shelter", false);
                w.WriteEndObject();
                Log.Info($"bus stop '{st.name}' at gta ({st.x:F0}, {st.y:F0}, {gz:F1})");
            }
            w.WriteEndArray();
            w.WriteStartObject("spawn");
            w.WriteString("name", "spawn");
            w.WriteStartArray("position"); w.WriteNumberValue(JsonExt.R(sx + off.X)); w.WriteNumberValue(JsonExt.R(sz)); w.WriteNumberValue(JsonExt.R(-(sy + off.Y))); w.WriteEndArray();
            w.WriteNumber("yaw", o.Spawn != null && o.Spawn.Length > 3 ? o.Spawn[3] : 0.0);
            w.WriteEndObject();
            w.WriteEndObject();
        });

        // ---------------------------------------------------------------- stats
        var tileArr = tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToArray();
        long texBytes = outTex.Sum(t => t.Bytes), texPixels = outTex.Sum(t => (long)t.W * t.H), meshBytes = new DirectoryInfo(meshDir).EnumerateFiles().Sum(f => f.Length);
        long protoTris = protoList.Sum(p => (long)p.Tris);
        Paths.WriteJson(Path.Combine(outDir, "stats.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("name", o.Name);
            w.WriteStartArray("region"); foreach (var v in r) w.WriteNumberValue(v); w.WriteEndArray();
            w.WriteNumber("tile_size", o.Tile);
            w.WriteStartObject("options");
            w.WriteNumber("min_size", o.MinSize); w.WriteNumber("level", o.Level); w.WriteNumber("tree_level", o.TreeLevel); w.WriteNumber("tex_cap", o.TexCap);
            w.WriteNumber("min_instances", o.MinInstances); w.WriteNumber("min_instance_tris", o.MinInstanceTris);
            w.WriteEndObject();
            w.WriteStartObject("totals");
            w.WriteNumber("entities", sels.Count - farDropped); w.WriteNumber("archetypes", archCount.Count);
            w.WriteNumber("tiles", tileArr.Length); w.WriteNumber("object_records", objArr.Length);
            w.WriteNumber("merged_entities", batchEntities); w.WriteNumber("merged_triangles", batchTris);
            w.WriteNumber("instances", instCount); w.WriteNumber("instanced_triangles_drawn", instTris);
            w.WriteNumber("prototypes", protoList.Count); w.WriteNumber("prototype_triangles", protoTris);
            w.WriteNumber("unique_triangles", batchTris + protoTris);
            w.WriteNumber("collision_pieces", col.Pieces); w.WriteNumber("collision_triangles", col.Triangles);
            w.WriteNumber("materials", usedMats.Count); w.WriteNumber("textures", outTex.Length);
            w.WriteNumber("texture_file_bytes", texBytes); w.WriteNumber("texture_pixels", texPixels); w.WriteNumber("mesh_file_bytes", meshBytes);
            w.WriteNumber("texture_refs", tex.Refs); w.WriteNumber("texture_refs_missing", tex.Missing); w.WriteNumber("textures_unsupported", tex.Unsupported);
            w.WriteNumber("drawables_failed", loadFail); w.WriteNumber("archetypes_without_file", noFile); w.WriteNumber("entities_with_empty_mesh", emptyMesh);
            w.WriteNumber("peak_mesh_bytes", peakMeshBytes); w.WriteNumber("seconds", JsonExt.R(Log.Seconds - tStart));
            w.WriteEndObject();
            w.WriteStartObject("selection"); foreach (var kv in counts.Counts) w.WriteNumber(kv.Key, kv.Value); w.WriteEndObject();
            w.WriteStartObject("build");
            var bs = builder.Stats;
            w.WriteNumber("drawables", bs.Drawables); w.WriteNumber("geometries", bs.Geometries); w.WriteNumber("skipped_shader", bs.SkippedShader);
            w.WriteNumber("skipped_proxy_model", bs.SkippedProxy); w.WriteNumber("bad_layout", bs.BadLayout); w.WriteNumber("no_diffuse", bs.NoDiffuse);
            w.WriteNumber("source_triangles", bs.SourceTris); w.WriteNumber("out_triangles", bs.OutTris);
            w.WriteNumber("geometries_winding_flipped", bs.FlippedGeoms); w.WriteNumber("geometries_without_normals", bs.NoNormals);
            w.WriteNumber("decal_triangles_over_256_texture_repeats_left_out", bs.RepeatTris); w.WriteNumber("blended_geometries_alpha_tested_for_texture_repeats", bs.RepeatMasked);
            w.WriteEndObject();
            w.WriteStartObject("texture_formats");
            foreach (var g in outTex.GroupBy(t => t.Format).OrderBy(g => g.Key)) w.WriteNumber(g.Key, g.Count());
            w.WriteEndObject();
            w.WriteStartArray("missing_textures_sample"); foreach (var kv in tex.MissingNames.OrderByDescending(k => k.Value).Take(30)) w.WriteStringValue(kv.Key); w.WriteEndArray();
            w.WriteStartObject("prop_collision_top");
            foreach (var kv in propColTris.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(40)) w.WriteNumber(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteStartArray("protos_top");
            foreach (var p in protoList.OrderByDescending(p => p.Count * (long)p.Tris).ThenBy(p => p.File, StringComparer.Ordinal).Take(40))
            {
                w.WriteStartObject(); w.WriteString("name", p.Name); w.WriteNumber("tris", p.Tris); w.WriteNumber("count", p.Count); w.WriteNumber("parts", p.Parts); w.WriteBoolean("tree", p.Tree); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("tiles");
            foreach (var t in tileArr)
            {
                w.WriteStartObject();
                w.WriteNumber("x", t.X); w.WriteNumber("y", t.Y); w.WriteNumber("tris", t.Tris); w.WriteNumber("parts", t.Parts); w.WriteNumber("instances", t.Instances); w.WriteNumber("entities", t.Entities);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        Log.Info($"map '{o.Name}': {tileArr.Length} tiles, merged {batchEntities} entities / {batchTris / 1e6:F2} M tris, " +
                 $"{instCount} instances of {protoList.Count} prototypes ({protoTris / 1e6:F2} M unique tris, {instTris / 1e6:F2} M drawn), {objArr.Length} object records");
        Log.Info($"materials {usedMats.Count}, textures {outTex.Length} ({texBytes / 1048576.0:F0} MiB files, {texPixels / 1e6:F0} Mpx; {texRemoved} more were only used by geometry that was left out), meshes {meshBytes / 1048576.0:F0} MiB, missing refs {tex.Missing}, unsupported {tex.Unsupported}");
        Log.Info($"spawn at gta ({sx:F1}, {sy:F1}, {sz:F1}); {clipped} triangles / instances dropped beyond the +-{o.Limit} m limit");
        foreach (var kv in counts.Counts) Log.Info($"  {kv.Key}: {kv.Value}");
        Log.Info($"done in {Log.Seconds - tStart:F1}s -> {outDir}");
    }
}

public sealed class Counter
{
    public readonly SortedDictionary<string, long> Counts = new(StringComparer.Ordinal);
    public void Add(string k, long n = 1) { lock (Counts) Counts[k] = Counts.TryGetValue(k, out var v) ? v + n : n; }
}
