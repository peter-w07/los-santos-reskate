using System.Collections.Concurrent;
using System.Text.Json;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaExport;

/// <summary>
/// Interior (MLO) collision layer. The static export skips every .ybn a manifest lists under an interior,
/// because those files are stored in interior-local space. Here every placed interior instance
/// (CMloInstanceDef in an active .ymap) is resolved to its archetype (.ytyp) and its bounds, the bounds are
/// transformed to world space with the instance position/rotation, and the result is written in the same
/// LSCT tile format as the static world into interiors/, with its own index, plus rooms and portals as JSON.
/// </summary>
public static class InteriorsExport
{
    public static readonly string[] CategoryNames = { "other", "transit", "road_tunnel", "parking", "building_interior" };
    public const int CatOther = 0, CatTransit = 1, CatRoadTunnel = 2, CatParking = 3, CatBuilding = 4;
    public const byte InfoDetached = 0x80;

    public sealed class Inst
    {
        public MloInstInfo I;
        public List<YbnInfo> Bounds = new();
        public string BoundsFrom = "";          // manifest | name | none
        public int Category;
        public string Kind = "", CategoryReason = "";
        public bool DefaultOn = true;
        public string DefaultReason = "";
        public List<Src> Sources = new();
        public long Tris, Prims;
        public V3 Min = new(float.MaxValue, float.MaxValue, float.MaxValue), Max = new(float.MinValue, float.MinValue, float.MinValue);
        public Vector3 LocalMin = new(float.MaxValue), LocalMax = new(float.MinValue);
        public bool HasGeometry => Tris > 0;
        public long ExteriorTrisNear;           // default-on static solid triangles that reach into the interior's box
        public long ExteriorOffTrisNear;        // same for switched-off map groups
        public long PortalStaticOn, PortalStaticOff; // static solid triangles at its entrance portals (sum over portals)
        public bool Detached;
        public string AttachedVia = "";
        public int Component = -1;
        public List<PortalW> Portals = new();
        public int MetroNodes, MetroStationNodes, RailNodes; // train track nodes inside the interior's box
        public int SameArchCount;
        public Matrix Xf, Inv;
    }

    public sealed class Src
    {
        public int Id;
        public Inst Owner;
        public YbnInfo Ybn;
        public string File;      // relative to interiors/
        public LsctHeader Header;
        public long Bytes;
        public bool Reused;
        public string Error;
        public ulong Signature;
        public FlattenStats Stats;
    }

    public sealed class PortalW
    {
        public int Index;
        public uint RoomFrom, RoomTo, Flags, MirrorPriority, Opacity, AudioOcclusion;
        public Vector3[] Corners = Array.Empty<Vector3>();   // world
        public Vector3 Centre, Normal;
        public float Width, Height;
        public bool Exterior, Mirror;
        public int LinkInst = -1, LinkPortal = -1;           // matching exterior portal of another interior
        public float LinkDist;
        public long StaticOn, StaticOff;                     // static solid triangles within PortalReach of the opening
        // oriented box around the opening used for that test (world): centre, unit axes, half extents
        public Vector3 BoxU, BoxV, BoxN, BoxHalf;
        public Vector3 BoxMin, BoxMax;
    }

    // ------------------------------------------------------------------ category rules

    /// <summary>
    /// Category from the archetype name, refined by evidence (train track nodes inside the interior's box).
    /// The name rules are the exporter's reading of Rockstar's naming, not game data.
    /// </summary>
    static (int cat, string kind, string why) Categorise(Inst it)
    {
        var n = it.I.ArchName;
        bool Has(params string[] keys) => keys.Any(k => n.Contains(k, StringComparison.Ordinal));
        bool tunnelName = (Has("tunnel", "_tun", "inttun", "newtun", "undpass") || n.Contains("tun", StringComparison.Ordinal) && char.IsDigit(n[^1])) && !Has("tuner");
        string ev = it.MetroNodes > 0 ? $"; {it.MetroNodes} metro track nodes inside ({it.MetroStationNodes} station stops)" : it.RailNodes > 0 ? $"; {it.RailNodes} freight track nodes inside" : "";

        if (Has("metro", "subway"))
        {
            string kind = n.EndsWith("_subway", StringComparison.Ordinal) ? "metro_station_entrance"
                : Has("newwalk") ? "metro_walkway"
                : it.MetroStationNodes > 0 || Has("station") ? "metro_station"
                : "metro_tunnel";
            return (CatTransit, kind, "name: metro/subway" + ev);
        }
        if (n.StartsWith("v_31_", StringComparison.Ordinal))
            return (CatTransit, "metro_construction_tunnel", "name: v_31 tunnels, the metro extension under construction (shaft at the downtown building site)" + ev);
        if (Has("railway", "railtunnel", "rwayb_tunnel")) return (CatTransit, "rail_tunnel", "name: rail tunnel" + ev);
        if (tunnelName)
        {
            if (it.MetroNodes > 0) return (CatTransit, "metro_tunnel", "tunnel with the metro track inside" + ev);
            if (it.RailNodes > 0) return (CatTransit, "rail_tunnel", "tunnel with a freight track inside" + ev);
            bool dlc = it.I.Arch != null && it.I.Arch.Dlc.StartsWith("mp", StringComparison.Ordinal);
            return (CatRoadTunnel, dlc ? "dlc_tunnel" : Has("undpass") ? "underpass" : "road_tunnel", "name: tunnel, no train track inside");
        }
        if (Has("carpark")) return (CatParking, "car_park", "name: carpark");
        if (Has("garage") || n.StartsWith("v_garage", StringComparison.Ordinal)) return (CatParking, "garage", "name: garage");
        if (Has("yacht", "carrier", "int_sub", "milo_bar", "milo_bedrm", "milo_bridge", "milo_enginrm", "milo_lounge") || n.EndsWith("_sub", StringComparison.Ordinal))
            return (CatOther, "vessel", "name: yacht / carrier / submarine interior");
        if (Has("mine_int")) return (CatOther, "mine", "name: mine");
        if (Has("milo_replay")) return (CatOther, "editor", "name: replay editor room");
        return (CatBuilding, "building", "default: shop / apartment / office / mission interior" + ev);
    }

    static (bool on, string why) DefaultState(MloInstInfo r, ScriptEvidence ev)
    {
        if (!r.ScriptManaged) return (true, r.StoryOnly ? "always-streamed ymap of the single-player map" : "ymap is always streamed");
        // same decision procedure as the static map groups (GroupDefaults): the building controller's state
        // table decides where it names the ymap, then the built-in table / rules
        var d = GroupDefaults.Decide(r.Ymap, r.Dlc, 0, null, ev);
        return (d.On, $"script-managed ymap; {d.Source} ({d.Confidence}): {d.Reason}");
    }

    static ulong InstSignature(Inst it, YbnInfo y)
    {
        var h = Fnv64.Create();
        h.Add("lsct-interior-mesh");
        h.Add(LsctHeader.CurrentVersion);
        h.Add((ulong)YbnFlattener.TessellationVersion);
        h.Add(y.RpfPath.ToLowerInvariant());
        h.Add((ulong)y.Entry.FileOffset);
        h.Add((ulong)y.Entry.FileSize);
        h.Add((ulong)y.Entry.GetFileSize());
        h.Add((ulong)y.Layer);
        var p = it.I.Pos; var q = it.I.Rot;
        foreach (var f in new[] { p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W }) h.Add((ulong)BitConverter.SingleToUInt32Bits(f));
        return h.Value;
    }

    public static string SafeName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '@' ? c : '_');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ run

    public static void Run(GameContext ctx, string outDir, int threads, bool force, float margin, bool listOnly, string only)
    {
        Mesh.SelfCheck();
        double t0 = Log.Seconds;
        var intDir = Path.Combine(outDir, "interiors");
        var meshDir = Path.Combine(intDir, "mesh");
        var colDir = Path.Combine(outDir, "collision");

        // 1. classification of every active ybn (which are interior bounds, and of which interior)
        var all = ctx.ClassifyBounds();
        var byHash = all.ToDictionary(y => y.Hash);
        var interiorYbns = all.Where(y => y.Interior).ToList();
        var boundsOf = new Dictionary<uint, List<YbnInfo>>();   // interior (archetype) name hash -> its bounds
        foreach (var y in interiorYbns)
        {
            uint ih = JenkHash.GenHash(y.InteriorName);
            if (!boundsOf.TryGetValue(ih, out var l)) boundsOf[ih] = l = new List<YbnInfo>();
            l.Add(y);
        }

        // 2. archetypes and instances
        double t1 = Log.Seconds;
        var archs = InteriorScan.LoadMloArchetypes(ctx, threads, out int ytypCount, out int ytypFailed);
        Log.Info($"interiors: {archs.Count} MLO archetypes ({archs.Values.Count(a => a.Active)} from the active set) in {ytypCount} ytyp files ({ytypFailed} without archetypes/unreadable), {Log.Seconds - t1:F1}s");
        t1 = Log.Seconds;
        var raw = InteriorScan.ScanInstances(ctx, threads, out var sc);
        Log.Info($"interiors: {sc.ActiveInstances} MLO instances in {sc.YmapsActive} active ymap files ({sc.ActiveNonMeta} not in meta format, {sc.ActiveFailed} unreadable) + {sc.StoryOnlyInstances} story-only placements from overlaid ymaps ({sc.Superseded} superseded by an active interior; {sc.YmapsAll} ymap files in all archives), {Log.Seconds - t1:F1}s");
        var tracks = InteriorScan.LoadTrainTracks(ctx);
        Log.Info("train tracks: " + string.Join(", ", tracks.Select(t => $"{t.Name}[{t.Config}] {t.Nodes.Count} nodes")));

        // .ybn files that are not in the active set (their rpf is overlaid by a DLC map pack); newest pack wins.
        // The multiplayer map packs replace e.g. sc1_rd.rpf by lr_sc1_rd.rpf, which re-places the tunnel interiors
        // but does not carry their bounds: those only exist in the overlaid base rpf.
        var overlaid = new Dictionary<uint, RpfFileEntry>();
        var overlaidOrder = new Dictionary<uint, (int, string)>();
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe || !e.NameLower.EndsWith(".ybn") || ctx.Map.YbnDict.ContainsKey(e.ShortNameHash)) continue;
                var key = (InteriorScan.DlcOrder(ctx, GameContext.DlcOf(fe.Path)), fe.Path.ToLowerInvariant());
                if (overlaidOrder.TryGetValue(e.ShortNameHash, out var old) && (old.Item1 > key.Item1 || old.Item1 == key.Item1 && string.CompareOrdinal(old.Item2, key.Item2) >= 0)) continue;
                overlaid[e.ShortNameHash] = fe; overlaidOrder[e.ShortNameHash] = key;
            }
        }
        var overlaidInfo = new Dictionary<uint, YbnInfo>();

        var insts = new List<Inst>();
        foreach (var r in raw)
        {
            var it = new Inst { I = r };
            archs.TryGetValue(r.ArchHash, out r.Arch);
            if (boundsOf.TryGetValue(r.ArchHash, out var bl)) { it.Bounds.AddRange(bl); it.BoundsFrom = "manifest"; }
            else
            {
                // no manifest entry: fall back to a ybn named like the archetype (how CodeWalker looks interiors up),
                // first in the active set, then in rpfs that a DLC map pack overlays
                foreach (var bn in new[] { r.ArchName, "hi@" + r.ArchName })
                    if (byHash.TryGetValue(JenkHash.GenHash(bn), out var y)) it.Bounds.Add(y);
                it.BoundsFrom = it.Bounds.Count > 0 ? "name" : "none";
                if (it.Bounds.Count == 0)
                {
                    foreach (var bn in new[] { r.ArchName, "hi@" + r.ArchName })
                    {
                        uint bh = JenkHash.GenHash(bn);
                        if (!overlaid.TryGetValue(bh, out var oe)) continue;
                        if (!overlaidInfo.TryGetValue(bh, out var oy))
                        {
                            var dlc = GameContext.DlcOf(oe.Path);
                            overlaidInfo[bh] = oy = new YbnInfo
                            {
                                Hash = bh, Name = bn, Entry = oe, RpfPath = oe.Path, Dlc = dlc, Origin = dlc.Length > 0 ? "dlc" : "base",
                                NameLayer = bn.StartsWith("hi@", StringComparison.Ordinal) ? 1 : 0, Interior = true, InteriorName = r.ArchName, Overlaid = true,
                            };
                        }
                        it.Bounds.Add(oy);
                    }
                    if (it.Bounds.Count > 0) it.BoundsFrom = "name, overlaid rpf";
                }
            }
            it.Bounds.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            it.Xf = r.Xf;
            it.Inv = Matrix.Invert(it.Xf);
            insts.Add(it);
        }
        foreach (var g in insts.GroupBy(i => i.I.ArchHash)) foreach (var it in g) it.SameArchCount = g.Count();

        // default state: an interior in a script-managed ymap only exists after a script requests that IPL
        foreach (var it in insts) (it.DefaultOn, it.DefaultReason) = DefaultState(it.I, ctx.Evidence);

        if (only != null) insts = insts.Where(i => i.I.ArchName.Contains(only)).ToList();

        // 3. meshes: one LSCT (kind 1) per instance and bound, in world space
        var srcs = new List<Src>();
        foreach (var it in insts)
            foreach (var y in it.Bounds)
            {
                var s = new Src { Id = srcs.Count, Owner = it, Ybn = y, File = $"mesh/i{it.I.Id:D4}_{SafeName(y.Name)}.lsct" };
                it.Sources.Add(s);
                srcs.Add(s);
            }
        if (listOnly || only != null) { PrintList(insts, archs, all); return; } // --only never writes a partial layer

        Directory.CreateDirectory(meshDir);
        var statsPath = Path.Combine(meshDir, "_flatten_stats.json");
        var oldStats = LoadStats(statsPath);
        t1 = Log.Seconds;
        int reused = 0, failed = 0;
        // parse each source ybn once, even when it is instanced many times
        var perYbn = srcs.GroupBy(s => s.Ybn.Hash).ToList();
        Parallel.ForEach(perYbn, new ParallelOptions { MaxDegreeOfParallelism = threads }, grp =>
        {
            YbnFile ybn = null;
            foreach (var s in grp)
            {
                var path = Path.Combine(intDir, s.File.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    s.Signature = InstSignature(s.Owner, s.Ybn);
                    if (!force && Mesh.TryReadHeader(path, out var eh) && eh.Signature == s.Signature && eh.Kind == 1)
                    {
                        s.Header = eh; s.Reused = true; s.Bytes = new FileInfo(path).Length;
                        oldStats.TryGetValue(s.File, out s.Stats);
                        Interlocked.Increment(ref reused);
                        continue;
                    }
                    ybn ??= ctx.Rpf.GetFile<YbnFile>(s.Ybn.Entry);
                    if (ybn?.Bounds == null) throw new InvalidDataException("ybn has no bounds / could not be read");
                    var mesh = YbnFlattener.Flatten(ybn.Bounds, (byte)s.Ybn.Layer, s.Ybn.Hash, s.Owner.Xf, out var st);
                    mesh.Header.Signature = s.Signature;
                    Paths.AtomicWrite(path, mesh.Write);
                    s.Header = mesh.Header; s.Bytes = mesh.ByteSize; s.Stats = st;
                }
                catch (Exception ex)
                {
                    s.Error = ex.GetType().Name + ": " + ex.Message;
                    Interlocked.Increment(ref failed);
                }
            }
        });
        var newStats = new Dictionary<string, FlattenStats>();
        foreach (var s in srcs) if (s.Stats != null) newStats[s.File] = s.Stats;
        Paths.AtomicWriteText(statsPath, JsonSerializer.Serialize(newStats, new JsonSerializerOptions { IncludeFields = true }));
        {
            var keep = new HashSet<string>(srcs.Select(s => Path.GetFileName(s.File)), StringComparer.OrdinalIgnoreCase);
            int stale = 0;
            foreach (var f in Directory.GetFiles(meshDir, "*.lsct"))
                if (!keep.Contains(Path.GetFileName(f))) { File.Delete(f); stale++; }
            if (stale > 0) Log.Info($"removed {stale} stale interior mesh files");
        }
        double meshSecs = Log.Seconds - t1;
        Log.Info($"interior meshes: {srcs.Count} ({reused} reused, {failed} failed), {srcs.Where(s => s.Error == null).Sum(s => (long)s.Header.NTris):N0} triangles, {srcs.Sum(s => s.Bytes) / 1048576.0:F0} MiB in {meshSecs:F1}s");

        // 4. per-instance bounds (world and interior-local)
        Parallel.ForEach(insts, new ParallelOptions { MaxDegreeOfParallelism = threads }, it =>
        {
            foreach (var s in it.Sources)
            {
                if (s.Error != null || s.Header.NTris == 0) continue;
                it.Tris += s.Header.NTris; it.Prims += s.Header.NPrims;
                var m = Mesh.Read(Path.Combine(intDir, s.File.Replace('/', Path.DirectorySeparatorChar)));
                foreach (ref readonly var v in m.Verts.AsSpan())
                {
                    if (v.X < it.Min.X) it.Min.X = v.X; if (v.Y < it.Min.Y) it.Min.Y = v.Y; if (v.Z < it.Min.Z) it.Min.Z = v.Z;
                    if (v.X > it.Max.X) it.Max.X = v.X; if (v.Y > it.Max.Y) it.Max.Y = v.Y; if (v.Z > it.Max.Z) it.Max.Z = v.Z;
                    var l = Vector3.TransformCoordinate(new Vector3(v.X, v.Y, v.Z), it.Inv);
                    it.LocalMin = Vector3.Min(it.LocalMin, l); it.LocalMax = Vector3.Max(it.LocalMax, l);
                }
            }
            if (!it.HasGeometry)
            {
                // no bounds of its own: take the extent from the room boxes and portal corners (the archetype box is empty)
                var a = it.I.Arch?.Arch;
                var lmn = new Vector3(float.MaxValue); var lmx = new Vector3(float.MinValue);
                if (a?.rooms != null)
                    foreach (var rm in a.rooms)
                    {
                        if ((rm._Data.bbMax - rm._Data.bbMin).LengthSquared() < 1e-6f) continue; // limbo
                        lmn = Vector3.Min(lmn, rm._Data.bbMin); lmx = Vector3.Max(lmx, rm._Data.bbMax);
                    }
                if (a?.portals != null)
                    foreach (var po in a.portals)
                        if (po.Corners != null) foreach (var c in po.Corners) { var c3 = new Vector3(c.X, c.Y, c.Z); lmn = Vector3.Min(lmn, c3); lmx = Vector3.Max(lmx, c3); }
                if (lmn.X > lmx.X) { lmn = Vector3.Zero; lmx = Vector3.Zero; }
                it.LocalMin = lmn; it.LocalMax = lmx;
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var c in BoxCorners(it.LocalMin, it.LocalMax)) { var w = Vector3.TransformCoordinate(c, it.Xf); mn = Vector3.Min(mn, w); mx = Vector3.Max(mx, w); }
                it.Min = new V3(mn); it.Max = new V3(mx);
            }
        });

        // 5. portals in world space, train track evidence, categories
        foreach (var it in insts) BuildPortals(it);
        foreach (var t in tracks)
        {
            foreach (var nd in t.Nodes)
            {
                foreach (var it in insts)
                {
                    if (nd.Pos.X < it.Min.X - 3 || nd.Pos.X > it.Max.X + 3 || nd.Pos.Y < it.Min.Y - 3 || nd.Pos.Y > it.Max.Y + 3 || nd.Pos.Z < it.Min.Z - 3 || nd.Pos.Z > it.Max.Z + 3) continue;
                    var l = Vector3.TransformCoordinate(nd.Pos, it.Inv);
                    const float m = 1.0f, mz = 0.3f; // rail nodes sit at floor level: keep the vertical slack small so a tunnel below is not counted
                    if (l.X < it.LocalMin.X - m || l.X > it.LocalMax.X + m || l.Y < it.LocalMin.Y - m || l.Y > it.LocalMax.Y + m || l.Z < it.LocalMin.Z - mz || l.Z > it.LocalMax.Z + mz) continue;
                    if (t.IsMetro) { it.MetroNodes++; if ((nd.Type & 1) != 0) it.MetroStationNodes++; } else it.RailNodes++;
                }
            }
        }
        foreach (var it in insts) { var (c, k, w) = Categorise(it); it.Category = c; it.Kind = k; it.CategoryReason = w; }

        // 6. which interiors touch the outside world, and which only hang off other interiors
        t1 = Log.Seconds;
        LinkPortals(insts);
        ExteriorContact(colDir, insts, threads);
        Attach(insts);
        Log.Info($"interior connectivity: {insts.Count(i => !i.Detached)} attached, {insts.Count(i => i.Detached)} detached, {Log.Seconds - t1:F1}s");

        // 7. tiles
        t1 = Log.Seconds;
        var tileDir = Path.Combine(intDir, "tiles");
        Directory.CreateDirectory(tileDir);
        var srcY = new List<SrcYbn>();
        foreach (var s in srcs)
        {
            var it = s.Owner;
            srcY.Add(new SrcYbn
            {
                Id = s.Id, Name = s.Ybn.Name, Hash = s.Ybn.Hash, Layer = s.Ybn.Layer, InCache = s.Ybn.InCache, Origin = s.Ybn.Origin, Dlc = s.Ybn.Dlc,
                MapGroup = it.I.ScriptManaged ? it.I.Ymap : null, DefaultOn = it.DefaultOn, RpfPath = s.Ybn.RpfPath, File = s.File, Error = s.Error,
                ExtraInfo = (byte)((it.Category << 4) | (it.Detached ? InfoDetached : 0)),
                Signature = s.Signature, Groups = s.Header.NGroups, Verts = s.Header.NVerts, Tris = s.Header.NTris, Prims = s.Header.NPrims, Bytes = s.Bytes,
                Min = s.Header.Min, Max = s.Header.Max,
            });
        }
        var cand = new Dictionary<(int, int), List<SrcYbn>>();
        foreach (var y in srcY.Where(y => y.Ok && y.Tris > 0))
        {
            int x0 = (int)Math.Floor((y.Min.X - margin) / Tiler.TileSize), x1 = (int)Math.Floor((y.Max.X + margin) / Tiler.TileSize);
            int y0 = (int)Math.Floor((y.Min.Y - margin) / Tiler.TileSize), y1 = (int)Math.Floor((y.Max.Y + margin) / Tiler.TileSize);
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!cand.TryGetValue((tx, ty), out var l)) cand[(tx, ty)] = l = new List<SrcYbn>();
                    l.Add(y);
                }
        }
        var results = new ConcurrentBag<Tiler.TileResult>();
        Parallel.ForEach(cand.Keys.ToArray(), new ParallelOptions { MaxDegreeOfParallelism = threads }, key =>
        {
            var r = Tiler.BuildTile(intDir, tileDir, key.Item1, key.Item2, cand[key], margin, force);
            if (r != null) results.Add(r);
        });
        var tiles = results.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
        {
            var keepT = new HashSet<string>(tiles.Select(t => Tiler.TileFileName(t.X, t.Y)), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(tileDir)) if (!keepT.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        double tileSecs = Log.Seconds - t1;
        Log.Info($"interior tiles: {tiles.Count} ({tiles.Count(t => t.Reused)} reused), {tiles.Sum(t => (long)t.Header.NTris):N0} tile triangles, {tiles.Sum(t => t.Bytes) / 1048576.0:F0} MiB in {tileSecs:F1}s");

        // 8. JSON
        var staticAsInterior = all.Where(y => !y.Interior && archs.ContainsKey(JenkHash.GenHash(BareName(y.Name)))).ToList();
        InteriorsJson.Write(ctx, intDir, insts, srcs, tiles, archs, tracks, interiorYbns, staticAsInterior, margin,
            new InteriorsJson.RunInfo
            {
                YtypCount = ytypCount, YtypFailed = ytypFailed, Scan = sc,
                MeshSeconds = meshSecs, TileSeconds = tileSecs, TotalSeconds = Log.Seconds - t0, OpenSeconds = ctx.InitSeconds, Reused = reused, Failed = failed,
            });
        PrintSummary(insts);
    }

    public static string BareName(string n) => n.StartsWith("hi@", StringComparison.Ordinal) || n.StartsWith("ma@", StringComparison.Ordinal) ? n.Substring(3) : n;

    static Dictionary<string, FlattenStats> LoadStats(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, FlattenStats>>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true }) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    static IEnumerable<Vector3> BoxCorners(Vector3 mn, Vector3 mx)
    {
        for (int i = 0; i < 8; i++) yield return new Vector3((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z);
    }

    // ------------------------------------------------------------------ portals

    static void BuildPortals(Inst it)
    {
        var ps = it.I.Arch?.Arch.portals;
        if (ps == null) return;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var d = p._Data;
            var pw = new PortalW
            {
                Index = i, RoomFrom = d.roomFrom, RoomTo = d.roomTo, Flags = d.flags, MirrorPriority = d.mirrorPriority, Opacity = d.opacity, AudioOcclusion = d.audioOcclusion,
            };
            var cs = p.Corners ?? Array.Empty<Vector4>();
            pw.Corners = cs.Select(c => Vector3.TransformCoordinate(new Vector3(c.X, c.Y, c.Z), it.Xf)).ToArray();
            if (pw.Corners.Length > 0)
            {
                var c = Vector3.Zero;
                foreach (var v in pw.Corners) c += v;
                pw.Centre = c / pw.Corners.Length;
            }
            if (pw.Corners.Length >= 3)
            {
                // Newell normal; edge lengths give the opening size
                var nrm = Vector3.Zero;
                for (int k = 0; k < pw.Corners.Length; k++)
                {
                    var a = pw.Corners[k]; var b = pw.Corners[(k + 1) % pw.Corners.Length];
                    nrm.X += (a.Y - b.Y) * (a.Z + b.Z); nrm.Y += (a.Z - b.Z) * (a.X + b.X); nrm.Z += (a.X - b.X) * (a.Y + b.Y);
                }
                if (nrm.LengthSquared() > 1e-12f) nrm.Normalize();
                pw.Normal = nrm;
                float e0 = Vector3.Distance(pw.Corners[0], pw.Corners[1]), e1 = Vector3.Distance(pw.Corners[1], pw.Corners[2]);
                // width = the more horizontal edge
                float h0 = Math.Abs(pw.Corners[0].Z - pw.Corners[1].Z), h1 = Math.Abs(pw.Corners[1].Z - pw.Corners[2].Z);
                if (h0 <= h1) { pw.Width = e0; pw.Height = e1; } else { pw.Width = e1; pw.Height = e0; }
            }
            if (pw.Corners.Length >= 3 && pw.Normal.LengthSquared() > 0.5f)
            {
                var u = pw.Corners[1] - pw.Corners[0];
                u -= pw.Normal * Vector3.Dot(u, pw.Normal);
                if (u.LengthSquared() < 1e-8f) u = Vector3.Cross(pw.Normal, Math.Abs(pw.Normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
                u.Normalize();
                var v = Vector3.Cross(pw.Normal, u);
                float hu = 0, hv = 0;
                foreach (var c in pw.Corners) { var r = c - pw.Centre; hu = Math.Max(hu, Math.Abs(Vector3.Dot(r, u))); hv = Math.Max(hv, Math.Abs(Vector3.Dot(r, v))); }
                pw.BoxU = u; pw.BoxV = v; pw.BoxN = pw.Normal;
                pw.BoxHalf = new Vector3(hu + PortalReach, hv + PortalReach, PortalReach);
                var ext = new Vector3(
                    Math.Abs(u.X) * pw.BoxHalf.X + Math.Abs(v.X) * pw.BoxHalf.Y + Math.Abs(pw.Normal.X) * pw.BoxHalf.Z,
                    Math.Abs(u.Y) * pw.BoxHalf.X + Math.Abs(v.Y) * pw.BoxHalf.Y + Math.Abs(pw.Normal.Y) * pw.BoxHalf.Z,
                    Math.Abs(u.Z) * pw.BoxHalf.X + Math.Abs(v.Z) * pw.BoxHalf.Y + Math.Abs(pw.Normal.Z) * pw.BoxHalf.Z);
                pw.BoxMin = pw.Centre - ext; pw.BoxMax = pw.Centre + ext;
            }
            // flag 4 = mirror portal (room renders its own reflection): not an opening
            pw.Mirror = (d.flags & 4) != 0;
            pw.Exterior = !pw.Mirror && (d.roomFrom == 0 || d.roomTo == 0) && d.roomFrom != d.roomTo;
            it.Portals.Add(pw);
        }
    }

    /// <summary>Exterior portals of two interiors that coincide: the interiors continue into each other (tunnel segments).</summary>
    static void LinkPortals(List<Inst> insts)
    {
        const float cell = 4f, maxDist = 2.5f;
        var grid = new Dictionary<(int, int, int), List<(Inst it, PortalW p)>>();
        foreach (var it in insts)
            foreach (var p in it.Portals)
            {
                if (!p.Exterior) continue;
                var k = ((int)Math.Floor(p.Centre.X / cell), (int)Math.Floor(p.Centre.Y / cell), (int)Math.Floor(p.Centre.Z / cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                l.Add((it, p));
            }
        foreach (var it in insts)
            foreach (var p in it.Portals)
            {
                if (!p.Exterior) continue;
                int cx = (int)Math.Floor(p.Centre.X / cell), cy = (int)Math.Floor(p.Centre.Y / cell), cz = (int)Math.Floor(p.Centre.Z / cell);
                float best = maxDist; Inst bi = null; PortalW bp = null;
                for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                    foreach (var (oi, op) in l)
                    {
                        if (ReferenceEquals(oi, it)) continue;
                        float d = Vector3.Distance(op.Centre, p.Centre);
                        if (d < best) { best = d; bi = oi; bp = op; }
                    }
                }
                if (bi != null) { p.LinkInst = bi.I.Id; p.LinkPortal = bp.Index; p.LinkDist = best; }
            }
    }

    // ------------------------------------------------------------------ contact with the static world

    const float ContactMargin = 1.5f;   // interior collision box is grown by this for the "static geometry in the box" count
    const float PortalReach = 2.0f;     // static geometry this close to an entrance portal means the opening meets the outside world

    /// <summary>
    /// Count the static-world solid triangles (a) that reach into each interior's oriented collision box grown by
    /// ContactMargin and (b) that lie within PortalReach of each entrance portal. Reads the static tiles only;
    /// nothing under collision/ is written.
    /// </summary>
    static void ExteriorContact(string colDir, List<Inst> insts, int threads)
    {
        var idxPath = Path.Combine(colDir, "index.json");
        if (!File.Exists(idxPath)) { Log.Warn("collision/index.json missing: cannot test contact with the static world; no interior is flagged detached"); foreach (var it in insts) it.ExteriorTrisNear = -1; return; }
        using var doc = JsonDocument.Parse(File.ReadAllText(idxPath));
        var root = doc.RootElement;
        var ybnEls = root.GetProperty("ybns");
        int ny = ybnEls.GetArrayLength();
        var ybnLayer = new byte[ny]; var ybnOn = new bool[ny];
        int k = 0;
        foreach (var e in ybnEls.EnumerateArray()) { ybnLayer[k] = (byte)e.GetProperty("layer").GetInt32(); ybnOn[k] = e.GetProperty("default_on").GetBoolean(); k++; }
        var tileFiles = new Dictionary<(int, int), string>();
        foreach (var e in root.GetProperty("tiles").EnumerateArray())
            tileFiles[(e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32())] = e.GetProperty("file").GetString();

        // search box per instance: collision box and entrance portal boxes
        var smin = new Dictionary<Inst, Vector3>(); var smax = new Dictionary<Inst, Vector3>();
        foreach (var it in insts)
        {
            var mn = new Vector3(it.Min.X, it.Min.Y, it.Min.Z) - new Vector3(ContactMargin);
            var mx = new Vector3(it.Max.X, it.Max.Y, it.Max.Z) + new Vector3(ContactMargin);
            foreach (var p in it.Portals) if (p.Exterior && p.BoxHalf.X > 0) { mn = Vector3.Min(mn, p.BoxMin); mx = Vector3.Max(mx, p.BoxMax); }
            smin[it] = mn; smax[it] = mx;
        }
        var perTile = new Dictionary<(int, int), List<Inst>>();
        foreach (var it in insts)
        {
            int x0 = (int)Math.Floor(smin[it].X / Tiler.TileSize), x1 = (int)Math.Floor(smax[it].X / Tiler.TileSize);
            int y0 = (int)Math.Floor(smin[it].Y / Tiler.TileSize), y1 = (int)Math.Floor(smax[it].Y / Tiler.TileSize);
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (!tileFiles.ContainsKey((tx, ty))) continue;
                    if (!perTile.TryGetValue((tx, ty), out var l)) perTile[(tx, ty)] = l = new();
                    l.Add(it);
                }
        }
        var gate = new object();
        Parallel.ForEach(perTile, new ParallelOptions { MaxDegreeOfParallelism = threads }, kv =>
        {
            var (tx, ty) = kv.Key;
            var m = Mesh.Read(Path.Combine(colDir, tileFiles[kv.Key].Replace('/', Path.DirectorySeparatorChar)));
            // a triangle is stored in every tile it overlaps: count it only in the tile that owns its centroid
            float ox0 = tx * Tiler.TileSize, ox1 = ox0 + Tiler.TileSize, oy0 = ty * Tiler.TileSize, oy1 = oy0 + Tiler.TileSize;
            var v = m.Verts; var t = m.Tris;
            foreach (var it in kv.Value)
            {
                var c = (it.LocalMin + it.LocalMax) * 0.5f;
                var h = (it.LocalMax - it.LocalMin) * 0.5f + new Vector3(ContactMargin);
                var bmn = smin[it]; var bmx = smax[it];
                var ports = it.Portals.Where(p => p.Exterior && p.BoxHalf.X > 0).ToArray();
                var pOn = new long[ports.Length]; var pOff = new long[ports.Length];
                long nOn = 0, nOff = 0;
                foreach (ref readonly var g in m.Groups.AsSpan())
                {
                    if (g.Max.X < bmn.X || g.Min.X > bmx.X || g.Max.Y < bmn.Y || g.Min.Y > bmx.Y || g.Max.Z < bmn.Z || g.Min.Z > bmx.Z) continue;
                    if (ybnLayer[g.YbnId] >= 2) continue;
                    if ((g.TypeFlags & (BoundFlags.MAP_RIVER | BoundFlags.FOLIAGE)) != 0) continue;
                    bool solid = (g.TypeFlags & BoundFlags.MAP_WEAPON) != 0 || (g.TypeFlags & (BoundFlags.MAP_DYNAMIC | BoundFlags.MAP_VEHICLE)) == (BoundFlags.MAP_DYNAMIC | BoundFlags.MAP_VEHICLE);
                    if (!solid) continue;
                    bool isOn = ybnOn[g.YbnId];
                    for (uint i = g.FirstTri; i < g.FirstTri + g.NTris; i++)
                    {
                        ref readonly var a = ref v[t[i * 3]]; ref readonly var b = ref v[t[i * 3 + 1]]; ref readonly var d = ref v[t[i * 3 + 2]];
                        float mnx = MathF.Min(a.X, MathF.Min(b.X, d.X)), mxx = MathF.Max(a.X, MathF.Max(b.X, d.X));
                        if (mxx < bmn.X || mnx > bmx.X) continue;
                        float mny = MathF.Min(a.Y, MathF.Min(b.Y, d.Y)), mxy = MathF.Max(a.Y, MathF.Max(b.Y, d.Y));
                        if (mxy < bmn.Y || mny > bmx.Y) continue;
                        float mnz = MathF.Min(a.Z, MathF.Min(b.Z, d.Z)), mxz = MathF.Max(a.Z, MathF.Max(b.Z, d.Z));
                        if (mxz < bmn.Z || mnz > bmx.Z) continue;
                        float gx = (a.X + b.X + d.X) / 3f, gy = (a.Y + b.Y + d.Y) / 3f;
                        if (gx < ox0 || gx >= ox1 || gy < oy0 || gy >= oy1) continue;
                        var wa = new Vector3(a.X, a.Y, a.Z); var wb = new Vector3(b.X, b.Y, b.Z); var wd = new Vector3(d.X, d.Y, d.Z);
                        var la = Vector3.TransformCoordinate(wa, it.Inv) - c;
                        var lb = Vector3.TransformCoordinate(wb, it.Inv) - c;
                        var ld = Vector3.TransformCoordinate(wd, it.Inv) - c;
                        if (TriBoxOverlap(la, lb, ld, h)) { if (isOn) nOn++; else nOff++; }
                        for (int pi = 0; pi < ports.Length; pi++)
                        {
                            var p = ports[pi];
                            if (mxx < p.BoxMin.X || mnx > p.BoxMax.X || mxy < p.BoxMin.Y || mny > p.BoxMax.Y || mxz < p.BoxMin.Z || mnz > p.BoxMax.Z) continue;
                            Vector3 ra = wa - p.Centre, rb = wb - p.Centre, rd = wd - p.Centre;
                            var qa = new Vector3(Vector3.Dot(ra, p.BoxU), Vector3.Dot(ra, p.BoxV), Vector3.Dot(ra, p.BoxN));
                            var qb = new Vector3(Vector3.Dot(rb, p.BoxU), Vector3.Dot(rb, p.BoxV), Vector3.Dot(rb, p.BoxN));
                            var qd = new Vector3(Vector3.Dot(rd, p.BoxU), Vector3.Dot(rd, p.BoxV), Vector3.Dot(rd, p.BoxN));
                            if (TriBoxOverlap(qa, qb, qd, p.BoxHalf)) { if (isOn) pOn[pi]++; else pOff[pi]++; }
                        }
                    }
                }
                lock (gate)
                {
                    it.ExteriorTrisNear += nOn; it.ExteriorOffTrisNear += nOff;
                    for (int pi = 0; pi < ports.Length; pi++)
                    {
                        ports[pi].StaticOn += pOn[pi]; ports[pi].StaticOff += pOff[pi];
                        it.PortalStaticOn += pOn[pi]; it.PortalStaticOff += pOff[pi];
                    }
                }
            }
        });
    }

    /// <summary>
    /// An interior is attached when default-on static geometry lies at one of its entrance portals (or, for an
    /// interior without bounds of its own, inside its room boxes), or when it is linked through coincident
    /// entrance portals to an attached interior (tunnel segments). Everything else is "detached": a teleport
    /// interior under/away from the map, an orphaned piece, or one whose outside world is a switched-off map
    /// group (North Yankton, Cayo Perico, yachts).
    /// </summary>
    static void Attach(List<Inst> insts)
    {
        var byId = insts.ToDictionary(i => i.I.Id);
        var parent = insts.ToDictionary(i => i.I.Id, i => i.I.Id);
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
        foreach (var it in insts)
            foreach (var p in it.Portals)
                if (p.LinkInst >= 0 && byId.ContainsKey(p.LinkInst)) Union(it.I.Id, p.LinkInst);
        bool unknown = insts.Any(i => i.ExteriorTrisNear < 0);
        bool Touches(Inst i) => i.PortalStaticOn > 0 || (!i.HasGeometry && i.ExteriorTrisNear > 0);
        var compTouch = new Dictionary<int, Inst>();
        foreach (var it in insts)
        {
            it.Component = Find(it.I.Id);
            if (Touches(it) && !compTouch.ContainsKey(it.Component)) compTouch[it.Component] = it;
        }
        foreach (var it in insts)
        {
            if (unknown) { it.Detached = false; it.AttachedVia = "unknown (static index missing)"; continue; }
            if (it.PortalStaticOn > 0) { it.Detached = false; it.AttachedVia = "exterior"; }
            else if (Touches(it)) { it.Detached = false; it.AttachedVia = "exterior (no bounds of its own: the static world fills its rooms)"; }
            else if (compTouch.TryGetValue(it.Component, out var o)) { it.Detached = false; it.AttachedVia = "interior chain (component of #" + o.I.Id + " " + o.I.ArchName + ")"; }
            else
            {
                it.Detached = true;
                it.AttachedVia = !it.Portals.Any(p => p.Exterior) ? "none: no entrance portal"
                    : it.PortalStaticOff > 0 ? "none: only switched-off map groups at its entrances"
                    : "none: no static geometry within " + PortalReach.ToString("0.#") + " m of an entrance";
            }
        }
    }

    /// <summary>Triangle / origin-centred box overlap (separating axis test, Akenine-Moller).</summary>
    public static bool TriBoxOverlap(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 h)
    {
        if (MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > h.X || MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < -h.X) return false;
        if (MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > h.Y || MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < -h.Y) return false;
        if (MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)) > h.Z || MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)) < -h.Z) return false;
        Vector3 e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;
        Span<Vector3> edges = stackalloc Vector3[3] { e0, e1, e2 };
        for (int i = 0; i < 3; i++)
        {
            var e = edges[i];
            // axes = unit axis x edge
            if (AxisSeparates(new Vector3(0, -e.Z, e.Y), v0, v1, v2, h)) return false;
            if (AxisSeparates(new Vector3(e.Z, 0, -e.X), v0, v1, v2, h)) return false;
            if (AxisSeparates(new Vector3(-e.Y, e.X, 0), v0, v1, v2, h)) return false;
        }
        var n = Vector3.Cross(e0, e1);
        float dn = Vector3.Dot(n, v0);
        float r = h.X * MathF.Abs(n.X) + h.Y * MathF.Abs(n.Y) + h.Z * MathF.Abs(n.Z);
        return MathF.Abs(dn) <= r;
    }

    static bool AxisSeparates(Vector3 ax, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 h)
    {
        float p0 = Vector3.Dot(ax, v0), p1 = Vector3.Dot(ax, v1), p2 = Vector3.Dot(ax, v2);
        float r = h.X * MathF.Abs(ax.X) + h.Y * MathF.Abs(ax.Y) + h.Z * MathF.Abs(ax.Z);
        return MathF.Min(p0, MathF.Min(p1, p2)) > r || MathF.Max(p0, MathF.Max(p1, p2)) < -r;
    }

    // ------------------------------------------------------------------ reports

    static void PrintList(List<Inst> insts, Dictionary<uint, MloArchInfo> archs, List<YbnInfo> all)
    {
        Console.WriteLine($"{insts.Count} MLO instances, {insts.Select(i => i.I.ArchHash).Distinct().Count()} distinct archetypes; {archs.Count} MLO archetypes defined");
        foreach (var g in insts.GroupBy(i => i.I.ArchName).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var f = g.First();
            var a = f.I.Arch;
            Console.WriteLine($"{g.Key,-34} x{g.Count(),-3} bounds={string.Join("+", f.Bounds.Select(b => b.Name)),-50} from={f.BoundsFrom,-8} arch={(a == null ? "MISSING" : GameContext.DlcOf(a.YtypPath) is { Length: > 0 } d ? d : "base")} rooms={a?.Arch.rooms?.Length ?? 0} portals={a?.Arch.portals?.Length ?? 0} story={g.Count(i => i.I.StoryOnly)} " +
                              $"pos0=({f.I.Pos.X:F0},{f.I.Pos.Y:F0},{f.I.Pos.Z:F0}) ymap0={f.I.Ymap} sm={(f.I.ScriptManaged ? 1 : 0)} dlc={f.I.Dlc}");
        }
        var used = new HashSet<uint>(insts.Select(i => i.I.ArchHash));
        Console.WriteLine("archetypes never instanced: " + string.Join(", ", archs.Values.Where(a => !used.Contains(a.Hash)).Select(a => a.Name).OrderBy(s => s, StringComparer.Ordinal)));
        var usedY = new HashSet<uint>(insts.SelectMany(i => i.Bounds).Select(b => b.Hash));
        Console.WriteLine("interior ybn never placed: " + string.Join(", ", all.Where(y => y.Interior && !usedY.Contains(y.Hash)).Select(y => y.Name)));
        Console.WriteLine("static ybn named like an MLO archetype: " + string.Join(", ", all.Where(y => !y.Interior && archs.ContainsKey(JenkHash.GenHash(BareName(y.Name)))).Select(y => y.Name)));
    }

    static void PrintSummary(List<Inst> insts)
    {
        foreach (var g in insts.GroupBy(i => i.Category).OrderBy(g => g.Key))
            Log.Info($"  {CategoryNames[g.Key],-18} {g.Count(),5} instances ({g.Count(i => i.Detached),4} detached, {g.Count(i => !i.HasGeometry),3} without collision), {g.Sum(i => i.Tris),10:N0} triangles, attached z {ZRange(g.Where(i => !i.Detached && i.HasGeometry))}");
    }

    static string ZRange(IEnumerable<Inst> e)
    {
        var l = e.ToList();
        return l.Count == 0 ? "-" : $"{l.Min(i => i.Min.Z):F1}..{l.Max(i => i.Max.Z):F1}";
    }
}
