using CodeWalker.GameFiles;
using SharpDX;

namespace GtaExport;

/// <summary>Ad-hoc inspection helpers used while developing the interiors export (read-only).</summary>
public static class Probe
{
    public static void Run(GameContext ctx, Opts o)
    {
        var what = o.Str("what", "ytyp");
        var gfc = ctx.Gfc;
        if (what == "ytyp")
        {
            // where is each MLO archetype defined, over ALL rpfs (not only the active map set)?
            var active = new HashSet<string>(ctx.Map.Rpfs.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var names = (o.Str("names", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            int total = 0, mlo = 0, bad = 0;
            foreach (var rpf in gfc.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not RpfFileEntry fe || !e.NameLower.EndsWith(".ytyp")) continue;
                    total++;
                    YtypFile y = null;
                    try { y = ctx.Rpf.GetFile<YtypFile>(fe); } catch (Exception ex) { Console.WriteLine($"EXC {fe.Path}: {ex.Message}"); }
                    if (y?.AllArchetypes == null)
                    {
                        bad++;
                        Console.WriteLine($"UNREADABLE {fe.Path} active={active.Contains(rpf.Path)} res={(fe is RpfResourceFileEntry)} pso={(y?.Pso != null)} rbf={(y?.Rbf != null)}");
                        continue;
                    }
                    foreach (var a in y.AllArchetypes)
                    {
                        if (a is not MloArchetype m) continue;
                        mlo++;
                        var n = GameContext.HashName(m.Hash);
                        if (names.Length == 0 || names.Any(k => n.Contains(k)))
                            Console.WriteLine($"MLO {n,-36} active={(active.Contains(rpf.Path) ? 1 : 0)} rooms={m.rooms?.Length ?? 0} portals={m.portals?.Length ?? 0} {fe.Path}");
                    }
                }
            }
            Console.WriteLine($"{total} ytyp entries in all rpfs, {bad} unreadable, {mlo} MLO archetype definitions");
        }
        else if (what == "ymap")
        {
            var names = (o.Str("names", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var activeE = new HashSet<RpfFileEntry>(ctx.Map.YmapDict.Values);
            foreach (var rpf in gfc.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not RpfResourceFileEntry fe || !e.NameLower.EndsWith(".ymap")) continue;
                    try
                    {
                        var data = fe.File.ExtractFile(fe);
                        var rd = new ResourceDataReader(fe, data);
                        var meta = rd.ReadBlock<Meta>();
                        var cmap = MetaTypes.GetTypedData<CMapData>(meta, MetaName.CMapData);
                        var eptrs = MetaTypes.GetPointerArray(meta, cmap.entities);
                        if (eptrs == null) continue;
                        var mlos = MetaTypes.GetTypedPointerArray<CMloInstanceDef>(meta, MetaName.CMloInstanceDef, eptrs);
                        if (mlos == null) continue;
                        foreach (var m in mlos)
                        {
                            var n = GameContext.HashName(m.CEntityDef.archetypeName);
                            if (names.Length > 0 && !names.Any(k => n.Contains(k))) continue;
                            var p = m.CEntityDef.position;
                            Console.WriteLine($"INST {n,-34} active={(activeE.Contains(fe) ? 1 : 0)} flags={cmap.flags} pos=({p.X:F1},{p.Y:F1},{p.Z:F1}) {fe.Path}");
                        }
                    }
                    catch (Exception) { }
                }
            }
        }
        else if (what == "ls")
        {
            var key = o.Str("names", "");
            foreach (var rpf in gfc.AllRpfs)
            {
                if (!rpf.Path.Contains(key, StringComparison.OrdinalIgnoreCase) || rpf.AllEntries == null) continue;
                Console.WriteLine($"== {rpf.Path} ({rpf.AllEntries.Count} entries)");
                foreach (var e in rpf.AllEntries) if (e is RpfFileEntry) Console.WriteLine("   " + e.Name);
            }
        }
        else if (what == "find")
        {
            // every file entry (any rpf) whose name contains one of the keys, with active flag for ybn
            var names = (o.Str("names", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var activeYbn = new HashSet<RpfFileEntry>(ctx.Map.YbnDict.Values);
            var activeRpf = ctx.Map.Rpfs;
            foreach (var rpf in gfc.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not RpfFileEntry fe) continue;
                    if (!names.Any(k => e.NameLower.Contains(k))) continue;
                    Console.WriteLine($"{(activeYbn.Contains(fe) ? "ACTIVE-YBN" : activeRpf.Contains(rpf) ? "active-rpf" : "inactive  ")} {fe.GetFileSize(),9} {fe.Path}");
                }
            }
        }
        else if (what == "mlo")
        {
            // entities of one MLO archetype with the physics dictionary of each entity archetype (needs all archetypes)
            var names = (o.Str("names", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var archs = new Dictionary<uint, Archetype>();
            var mlos = new List<MloArchetype>();
            foreach (var rpf in gfc.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not RpfFileEntry fe || !e.NameLower.EndsWith(".ytyp")) continue;
                    YtypFile y = null;
                    try { y = ctx.Rpf.GetFile<YtypFile>(fe); } catch (Exception) { }
                    if (y?.AllArchetypes == null) continue;
                    foreach (var a in y.AllArchetypes) { archs[a.Hash] = a; if (a is MloArchetype m && names.Any(k => GameContext.HashName(m.Hash) == k)) mlos.Add(m); }
                }
            }
            foreach (var m in mlos.GroupBy(m => m.Hash).Select(g => g.Last()))
            {
                Console.WriteLine($"== {GameContext.HashName(m.Hash)} ({m.Ytyp?.RpfFileEntry?.Path}) entities={m.entities?.Length ?? 0} rooms={m.rooms?.Length ?? 0} bb={m.BBMin}..{m.BBMax} flags={m._BaseArchetypeDef.flags} physDict={GameContext.HashName(m._BaseArchetypeDef.physicsDictionary)}");
                if (m.rooms != null) foreach (var r in m.rooms) Console.WriteLine($"   room {r.RoomName} {r._Data.bbMin}..{r._Data.bbMax} objs={r.AttachedObjects?.Length ?? 0}");
                if (m.entities == null) continue;
                foreach (var en in m.entities)
                {
                    var h = en._Data.archetypeName;
                    archs.TryGetValue(h, out var ea);
                    Console.WriteLine($"   ent {GameContext.HashName(h),-40} pos={en._Data.position} flags={en._Data.flags} arch={(ea == null ? "?" : ea.GetType().Name)} physDict={(ea == null ? "" : GameContext.HashName(ea._BaseArchetypeDef.physicsDictionary))} asset={(ea == null ? "" : ea._BaseArchetypeDef.assetType.ToString())} lod={(ea == null ? 0 : ea._BaseArchetypeDef.lodDist)}");
                }
            }
        }
        else if (what == "overlaid")
        {
            // .ybn that exist only in archives a DLC map pack overlays (not in the active set), split into
            // interior bounds (name of an MLO archetype) and others; for the others, is there an active .ybn
            // whose name ends with the same base name (the DLC's renamed copy, e.g. hei_dt1_03_0 for dt1_03_0)?
            var active = new HashSet<string>(ctx.Map.YbnDict.Values.Select(e => e.GetShortNameLower()));
            var mloNames = new HashSet<string>();
            foreach (var rpf in gfc.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not RpfFileEntry fe || !e.NameLower.EndsWith(".ytyp")) continue;
                    try { var y = ctx.Rpf.GetFile<YtypFile>(fe); if (y?.AllArchetypes != null) foreach (var a in y.AllArchetypes) if (a is MloArchetype) mloNames.Add(GameContext.HashName(a.Hash)); } catch (Exception) { }
                }
            }
            var seen = new Dictionary<string, string>();
            foreach (var rpf in gfc.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                    if (e is RpfFileEntry fe && e.NameLower.EndsWith(".ybn") && !ctx.Map.YbnDict.ContainsKey(e.ShortNameHash)) seen[e.GetShortNameLower()] = fe.Path;
            }
            int interior = 0, renamed = 0, lost = 0;
            var lostList = new List<string>();
            foreach (var kv in seen.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var bare = InteriorsExport.BareName(kv.Key);
                var prefix = kv.Key.Substring(0, kv.Key.Length - bare.Length);
                if (mloNames.Contains(bare)) { interior++; continue; }
                bool has = active.Any(a => a.StartsWith(prefix, StringComparison.Ordinal) && InteriorsExport.BareName(a).EndsWith("_" + bare, StringComparison.Ordinal));
                if (has) renamed++; else { lost++; lostList.Add(kv.Key + "  <- " + kv.Value); }
            }
            Console.WriteLine($"{seen.Count} ybn names exist only outside the active set: {interior} interior bounds, {renamed} with a renamed active copy (prefix_name), {lost} without");
            foreach (var l in lostList) Console.WriteLine("  NO ACTIVE COPY " + l);
        }
        else if (what == "meshcheck")
        {
            // read-only: flatten static ybn with today's code and compare byte for byte with collision/ybn/*.lsct
            var outDir = Path.GetFullPath(o.Str("out", Paths.DefaultOut));
            var names = (o.Str("names", "") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var all = ctx.ClassifyBounds().Where(y => !y.Interior).ToList();
            var pick = names.Length > 0 ? all.Where(y => names.Contains(y.Name)).ToList() : all.Where((y, i) => i % 97 == 0).ToList();
            int same = 0, differ = 0, missing = 0;
            foreach (var y in pick)
            {
                var path = Path.Combine(outDir, "collision", "ybn", CollisionExport.MeshFileName(y.Name));
                if (!File.Exists(path)) { missing++; continue; }
                var ybn = ctx.Rpf.GetFile<YbnFile>(y.Entry);
                var mesh = YbnFlattener.Flatten(ybn.Bounds, (byte)y.Layer, y.Hash, out _);
                Mesh.TryReadHeader(path, out var eh);
                mesh.Header.Signature = eh.Signature;
                using var ms = new MemoryStream();
                mesh.Write(ms);
                if (ms.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(path))) same++;
                else { differ++; Console.WriteLine("DIFFERS " + y.Name); }
            }
            Console.WriteLine($"static meshes re-flattened in memory: {same} byte-identical to collision/ybn, {differ} differ, {missing} not on disk (of {pick.Count} sampled)");
        }
        else if (what == "rpfs")
        {
            foreach (var img in ctx.Map.Images) Console.WriteLine($"{img.VPath} -> {img.Rpf.Path}  [{img.Source}{(img.Overlay ? ", overlay" : "")}]");
        }
    }

    /// <summary>
    /// Read-only check that the static layer on disk is still what the current code would produce: every tile
    /// header signature under collision/tiles must equal the signature the tiler computes today (so a 'tiles' run
    /// would reuse every file). Writes nothing.
    /// </summary>
    public static int TileSignatures(string outDir, float margin)
    {
        var colDir = Path.Combine(outDir, "collision");
        var src = Sources.Load(colDir);
        var cand = new Dictionary<(int, int), List<SrcYbn>>();
        foreach (var y in src.Ybns.Where(y => y.Ok && y.Tris > 0))
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
        int same = 0, differ = 0, missing = 0;
        foreach (var kv in cand)
        {
            kv.Value.Sort((a, b) => a.Id.CompareTo(b.Id));
            var path = Path.Combine(colDir, "tiles", Tiler.TileFileName(kv.Key.Item1, kv.Key.Item2));
            if (!Mesh.TryReadHeader(path, out var h)) { missing++; continue; }
            if (h.Signature == Tiler.TileSignature(kv.Key.Item1, kv.Key.Item2, margin, kv.Value)) same++; else differ++;
        }
        Console.WriteLine($"static tiles: {same} signatures match the current tiler, {differ} differ, {missing} candidate tiles have no file (empty tiles are not written)");
        return differ == 0 ? 0 : 1;
    }
}
