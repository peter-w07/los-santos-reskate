using System.Collections.Concurrent;
using System.Text.Json;

namespace GtaExport;

public sealed class SrcYbn
{
    public int Id;
    public string Name, Origin, Dlc, MapGroup, RpfPath, File, Error;
    public uint Hash;
    public int Layer;
    public bool InCache;
    public bool DefaultOn = true;
    public byte ExtraInfo;       // OR-ed into the group info byte of the tiles (bits 4-7; 0 for the static world)
    public ulong Signature;
    public uint Groups, Verts, Tris, Prims;
    public long Bytes;
    public V3 Min, Max;
    public bool Ok => Error == null;
}

public sealed class Sources
{
    public List<SrcYbn> Ybns = new();
    public JsonElement Root;

    public static Sources Load(string colDir)
    {
        var path = Path.Combine(colDir, "sources.json");
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException("run the 'collision' command first; missing " + path);
        var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var s = new Sources { Root = doc.RootElement.Clone() };
        foreach (var e in s.Root.GetProperty("ybns").EnumerateArray())
        {
            var y = new SrcYbn
            {
                Id = e.GetProperty("id").GetInt32(),
                Name = e.GetProperty("name").GetString(),
                Hash = e.GetProperty("hash").GetUInt32(),
                Layer = e.GetProperty("layer").GetInt32(),
                InCache = e.GetProperty("in_cache").GetBoolean(),
                Origin = e.GetProperty("origin").GetString(),
                Dlc = e.GetProperty("dlc").GetString(),
                MapGroup = e.GetProperty("map_group").ValueKind == JsonValueKind.Null ? null : e.GetProperty("map_group").GetString(),
                RpfPath = e.GetProperty("rpf_path").GetString(),
                DefaultOn = e.GetProperty("default_on").GetBoolean(),
                File = e.GetProperty("file").GetString(),
            };
            if (e.TryGetProperty("error", out var err)) y.Error = err.GetString();
            else
            {
                y.Signature = e.GetProperty("signature").GetUInt64();
                y.Groups = e.GetProperty("groups").GetUInt32();
                y.Verts = e.GetProperty("verts").GetUInt32();
                y.Tris = e.GetProperty("tris").GetUInt32();
                y.Prims = e.GetProperty("prims").GetUInt32();
                y.Bytes = e.GetProperty("bytes").GetInt64();
                var mn = e.GetProperty("min"); var mx = e.GetProperty("max");
                y.Min = new V3(mn[0].GetSingle(), mn[1].GetSingle(), mn[2].GetSingle());
                y.Max = new V3(mx[0].GetSingle(), mx[1].GetSingle(), mx[2].GetSingle());
            }
            if (y.Id != s.Ybns.Count) throw new InvalidDataException("sources.json ybn ids are not dense");
            s.Ybns.Add(y);
        }
        return s;
    }
}

/// <summary>
/// Phase 2: bin the per-ybn meshes into 256 m x 256 m tiles (a polygon goes into every tile it overlaps),
/// then gather statistics and write collision/index.json. Needs only phase 1 output, not the game.
/// </summary>
public static class Tiler
{
    public const float TileSize = 256f;

    public sealed class TileResult
    {
        public int X, Y;
        public LsctHeader Header;
        public long Bytes;
        public bool Reused;
        public int[] Ybns;
    }

    public static string TileFileName(int tx, int ty) => $"tile_{tx}_{ty}.lsct";

    public static void Run(string outDir, int threads, bool force, float margin)
    {
        Mesh.SelfCheck();
        double t0 = Log.Seconds;
        var colDir = Path.Combine(outDir, "collision");
        var tileDir = Path.Combine(colDir, "tiles");
        Directory.CreateDirectory(tileDir);
        var src = Sources.Load(colDir);
        var ok = src.Ybns.Where(y => y.Ok && (y.Tris > 0)).ToList();

        // candidate lists per tile from the mesh bounding boxes
        var cand = new Dictionary<(int, int), List<SrcYbn>>();
        foreach (var y in ok)
        {
            int x0 = (int)Math.Floor((y.Min.X - margin) / TileSize), x1 = (int)Math.Floor((y.Max.X + margin) / TileSize);
            int y0 = (int)Math.Floor((y.Min.Y - margin) / TileSize), y1 = (int)Math.Floor((y.Max.Y + margin) / TileSize);
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!cand.TryGetValue((tx, ty), out var l)) cand[(tx, ty)] = l = new List<SrcYbn>();
                    l.Add(y);
                }
        }
        var keys = cand.Keys.OrderBy(k => k.Item2).ThenBy(k => k.Item1).ToArray();
        Log.Info($"phase 2: {ok.Count} meshes -> {keys.Length} candidate tiles (tile {TileSize} m, margin {margin} m)");

        var results = new ConcurrentBag<TileResult>();
        int done = 0, reused = 0;
        double lastLog = Log.Seconds;
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = threads }, key =>
        {
            var r = BuildTile(colDir, tileDir, key.Item1, key.Item2, cand[key], margin, force);
            if (r != null) { results.Add(r); if (r.Reused) Interlocked.Increment(ref reused); }
            int d = Interlocked.Increment(ref done);
            if (Log.Seconds - Volatile.Read(ref lastLog) > 5)
            {
                Volatile.Write(ref lastLog, Log.Seconds);
                Log.Info($"  phase 2: {d}/{keys.Length} tiles ({reused} reused)");
            }
        });

        var tiles = results.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
        // drop stale tile files from earlier runs
        var keep = new HashSet<string>(tiles.Select(t => TileFileName(t.X, t.Y)), StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var f in Directory.GetFiles(tileDir))
        {
            if (!keep.Contains(Path.GetFileName(f))) { System.IO.File.Delete(f); removed++; }
        }
        double tileSecs = Log.Seconds - t0;
        long tileBytes = tiles.Sum(t => t.Bytes);
        Log.Info($"phase 2 done: {tiles.Count} tiles ({reused} reused, {removed} stale removed), {tiles.Sum(t => (long)t.Header.NTris):N0} tile triangles, {tileBytes / 1048576.0:F0} MiB in {tileSecs:F1}s");

        double t1 = Log.Seconds;
        var stats = Stats.Gather(colDir, src, threads);
        Log.Info($"stats gathered in {Log.Seconds - t1:F1}s");
        IndexWriter.Write(colDir, src, tiles, stats, margin, tileSecs);
    }

    public static ulong TileSignature(int tx, int ty, float margin, List<SrcYbn> cands)
    {
        var h = Fnv64.Create();
        h.Add("lsct-tile");
        h.Add(LsctHeader.CurrentVersion);
        h.Add((ulong)(uint)tx); h.Add((ulong)(uint)ty);
        h.Add((ulong)BitConverter.SingleToUInt32Bits(margin));
        h.Add((ulong)BitConverter.SingleToUInt32Bits(TileSize));
        foreach (var y in cands)
        {
            h.Add((ulong)y.Id); h.Add(y.Name); h.Add(y.Signature); h.Add(y.Tris); h.Add((ulong)((y.MapGroup != null ? 4 : 0) | (y.DefaultOn ? 0 : 8)));
            if (y.ExtraInfo != 0) h.Add(0x1000UL | y.ExtraInfo); // only interior layers set this, so static tile signatures are unchanged
        }
        return h.Value;
    }

    /// <summary>Build (or reuse) one tile from the meshes in <paramref name="cands"/>; mesh paths are relative to <paramref name="colDir"/>.</summary>
    public static TileResult BuildTile(string colDir, string tileDir, int tx, int ty, List<SrcYbn> cands, float margin, bool force)
    {
        cands.Sort((a, b) => a.Id.CompareTo(b.Id));
        var path = Path.Combine(tileDir, TileFileName(tx, ty));
        ulong sig = TileSignature(tx, ty, margin, cands);
        if (!force && Mesh.TryReadHeader(path, out var eh) && eh.Signature == sig && eh.Kind == 0)
        {
            return new TileResult { X = tx, Y = ty, Header = eh, Bytes = new FileInfo(path).Length, Reused = true, Ybns = ReadTileYbns(path) };
        }

        float rx0 = tx * TileSize - margin, rx1 = (tx + 1) * TileSize + margin;
        float ry0 = ty * TileSize - margin, ry1 = (ty + 1) * TileSize + margin;

        var groups = new List<GroupRec>();
        var verts = new List<V3>();
        var tris = new List<uint>();
        var attrs = new List<TriAttr>();
        var prims = new List<PrimRec>();
        var ybnIds = new List<int>();

        foreach (var y in cands)
        {
            var m = Mesh.Read(Path.Combine(colDir, y.File.Replace('/', Path.DirectorySeparatorChar)));
            var remap = new int[m.Verts.Length];
            Array.Fill(remap, -1);
            bool any = false;
            var mv = m.Verts; var mt = m.Tris; var ma = m.Attrs;

            uint MapV(uint i)
            {
                int r = remap[i];
                if (r < 0) { r = verts.Count; remap[i] = r; verts.Add(mv[i]); }
                return (uint)r;
            }

            foreach (ref readonly var g in m.Groups.AsSpan())
            {
                if (g.Max.X < rx0 || g.Min.X > rx1 || g.Max.Y < ry0 || g.Min.Y > ry1) continue;
                if (groups.Count >= ushort.MaxValue) throw new InvalidDataException($"tile {tx},{ty}: more than 65535 groups");
                ushort gi = (ushort)groups.Count;
                var og = g;
                og.YbnId = (uint)y.Id;
                og.Info = (byte)((og.Info & 3) | (y.MapGroup != null ? 4 : 0) | (y.DefaultOn ? 0 : 8) | (y.ExtraInfo & 0xF0));
                og.FirstTri = (uint)attrs.Count;
                og.FirstPrim = (uint)prims.Count;
                int vertStart = verts.Count;
                var bmin = new V3(float.MaxValue, float.MaxValue, float.MaxValue);
                var bmax = new V3(float.MinValue, float.MinValue, float.MinValue);

                void Grow(in V3 v)
                {
                    if (v.X < bmin.X) bmin.X = v.X; if (v.Y < bmin.Y) bmin.Y = v.Y; if (v.Z < bmin.Z) bmin.Z = v.Z;
                    if (v.X > bmax.X) bmax.X = v.X; if (v.Y > bmax.Y) bmax.Y = v.Y; if (v.Z > bmax.Z) bmax.Z = v.Z;
                }

                uint plainEnd = g.NPrims > 0 ? m.Prims[g.FirstPrim].FirstTri : g.FirstTri + g.NTris;
                for (uint t = g.FirstTri; t < plainEnd; t++)
                {
                    uint ia = mt[t * 3], ib = mt[t * 3 + 1], ic = mt[t * 3 + 2];
                    ref readonly var a = ref mv[ia]; ref readonly var b = ref mv[ib]; ref readonly var c = ref mv[ic];
                    if (!TriOverlapsRect(a, b, c, rx0, ry0, rx1, ry1)) continue;
                    tris.Add(MapV(ia)); tris.Add(MapV(ib)); tris.Add(MapV(ic));
                    var at = ma[t]; at.Group = gi; attrs.Add(at);
                    Grow(a); Grow(b); Grow(c);
                }
                for (uint p = g.FirstPrim; p < g.FirstPrim + g.NPrims; p++)
                {
                    var pr = m.Prims[p];
                    // bounding rectangle of the primitive's tessellation
                    float px0 = float.MaxValue, py0 = float.MaxValue, px1 = float.MinValue, py1 = float.MinValue;
                    for (uint t = pr.FirstTri; t < pr.FirstTri + pr.NTris; t++)
                        for (int k = 0; k < 3; k++)
                        {
                            ref readonly var v = ref mv[mt[t * 3 + k]];
                            if (v.X < px0) px0 = v.X; if (v.X > px1) px1 = v.X; if (v.Y < py0) py0 = v.Y; if (v.Y > py1) py1 = v.Y;
                        }
                    if (px1 < rx0 || px0 > rx1 || py1 < ry0 || py0 > ry1) continue;
                    uint first = (uint)attrs.Count;
                    for (uint t = pr.FirstTri; t < pr.FirstTri + pr.NTris; t++)
                    {
                        uint ia = mt[t * 3], ib = mt[t * 3 + 1], ic = mt[t * 3 + 2];
                        tris.Add(MapV(ia)); tris.Add(MapV(ib)); tris.Add(MapV(ic));
                        var at = ma[t]; at.Group = gi; attrs.Add(at);
                        Grow(mv[ia]); Grow(mv[ib]); Grow(mv[ic]);
                    }
                    pr.FirstTri = first;
                    pr.Group = gi;
                    prims.Add(pr);
                }

                og.NTris = (uint)attrs.Count - og.FirstTri;
                og.NPrims = (uint)prims.Count - og.FirstPrim;
                if (og.NTris == 0) continue;
                og.Min = bmin; og.Max = bmax;
                groups.Add(og);
                any = true;
            }
            if (any) ybnIds.Add(y.Id);
        }

        if (attrs.Count == 0)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            return null;
        }

        var tile = new Mesh
        {
            Groups = groups.ToArray(), Verts = verts.ToArray(), Tris = tris.ToArray(), Attrs = attrs.ToArray(), Prims = prims.ToArray(),
        };
        tile.Header.Kind = 0;
        tile.Header.TileX = tx;
        tile.Header.TileY = ty;
        tile.Header.TileSize = TileSize;
        tile.Header.Margin = margin;
        tile.Header.Signature = sig;
        tile.RecomputeBounds();
        Paths.AtomicWrite(path, tile.Write);
        return new TileResult { X = tx, Y = ty, Header = tile.Header, Bytes = tile.ByteSize, Ybns = ybnIds.ToArray() };
    }

    static int[] ReadTileYbns(string path)
    {
        var set = new SortedSet<int>();
        foreach (var g in Mesh.ReadGroups(path)) set.Add((int)g.YbnId);
        return set.ToArray();
    }

    /// <summary>Exact 2D triangle / axis-aligned rectangle overlap (closed sets) by separating axes.</summary>
    public static bool TriOverlapsRect(in V3 a, in V3 b, in V3 c, float x0, float y0, float x1, float y1)
    {
        float minx = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxx = MathF.Max(a.X, MathF.Max(b.X, c.X));
        if (maxx < x0 || minx > x1) return false;
        float miny = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        if (maxy < y0 || miny > y1) return false;
        return !EdgeSeparates(a, b, c, x0, y0, x1, y1) && !EdgeSeparates(b, c, a, x0, y0, x1, y1) && !EdgeSeparates(c, a, b, x0, y0, x1, y1);
    }

    static bool EdgeSeparates(in V3 p, in V3 q, in V3 r, float x0, float y0, float x1, float y1)
    {
        // line through p,q; the triangle lies on the side of r. Separated if all four rectangle corners are strictly on the other side.
        double nx = -(double)(q.Y - p.Y), ny = (double)(q.X - p.X);
        double sr = nx * (r.X - p.X) + ny * (r.Y - p.Y);
        if (sr == 0) return false; // degenerate in plan view (vertical wall): the box test above decides
        if (sr < 0) { nx = -nx; ny = -ny; }
        // most positive corner along n
        double cx = nx > 0 ? x1 : x0, cy = ny > 0 ? y1 : y0;
        return nx * (cx - p.X) + ny * (cy - p.Y) < 0;
    }
}
