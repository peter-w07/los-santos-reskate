using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaExport;

/// <summary>One MLO archetype (an interior definition from a .ytyp) after DLC resolution.</summary>
public sealed class MloArchInfo
{
    public uint Hash;
    public string Name;
    public MloArchetype Arch;
    public string YtypPath;
    public string Dlc;
    public int Order;                       // load order of the defining pack (base -1)
    public bool Active;                     // the ytyp's rpf is part of the DLC-resolved active map set
    public List<string> AlsoIn = new();     // other ytyp files that define the same archetype (lost the override)
    public Vector3 BBMin, BBMax;            // archetype bounding box (interior-local)
}

/// <summary>One placed interior: a CMloInstanceDef entity of an active .ymap.</summary>
public sealed class MloInstInfo
{
    public int Id = -1;
    public uint ArchHash;
    public string ArchName;
    public MloArchInfo Arch;                // null when no active ytyp defines the archetype
    public string Ymap, YmapPath, Dlc, Origin;
    public uint YmapFlags, YmapContentFlags;
    public bool ScriptManaged => (YmapFlags & 1) != 0;
    public Vector3 Pos;
    public Quaternion Rot;                  // interior-local -> world (CMloInstanceDef rotations are not inverted)
    public float ScaleXY = 1, ScaleZ = 1;
    public uint EntityFlags, Guid, GroupId, FloorId, NumExitPortals, MloInstFlags;
    public string[] DefaultEntitySets = Array.Empty<string>();
    public List<string> DuplicateYmaps = new(); // other ymaps that place the same archetype at the same spot
    public bool Active = true;              // placed by a ymap of the DLC-resolved active set
    public bool StoryOnly;                  // only placed by an overlaid base/patch ymap (single-player content)
    public int Order;                       // DLC load order of the ymap's pack
    public List<string> Supersedes = new(); // inactive placements at the same spot that this one stands in for

    public Matrix Xf => Matrix.RotationQuaternion(Rot) * Matrix.Translation(Pos);
    public Vector3 ToWorld(Vector3 local) => Vector3.TransformCoordinate(local, Xf);
}

public sealed class ScanCounts
{
    public int YmapsAll, YmapsActive, ActiveNonMeta, ActiveFailed, ActiveInstances, StoryOnlyInstances, Superseded;
}

public sealed class TrackNode { public Vector3 Pos; public int Type; }

public sealed class TrainTrackInfo
{
    public string File, Config, Name;
    public bool PingPong, StopsAtStations, MpStopsAtStations;
    public float Speed, BrakingDist;
    public List<TrackNode> Nodes = new();
    public bool IsMetro => (Config ?? "").Contains("metro", StringComparison.OrdinalIgnoreCase);
}

public static class InteriorScan
{
    public static int DlcOrder(GameContext ctx, string dlc)
    {
        if (string.IsNullOrEmpty(dlc)) return -1;
        int i = ctx.Gfc.DlcNameList.FindIndex(d => string.Equals(d, dlc, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? ctx.Gfc.DlcNameList.Count : i;
    }

    /// <summary>
    /// All MLO archetypes, from every .ytyp of every archive. The DLC-resolved "active" set is not enough: the
    /// multiplayer map packs (mpheist, mplowrider, ...) replace a district's *_metadata.rpf with one that only
    /// carries renamed ymaps, while the interior archetypes stay in the base game's (now overlaid) rpf - the same
    /// reason CodeWalker builds its archetype dictionary from all rpfs. Priority when several files define the
    /// same archetype: a file of the active set, then the later pack in DLC load order.
    /// </summary>
    public static Dictionary<uint, MloArchInfo> LoadMloArchetypes(GameContext ctx, int threads, out int ytypCount, out int ytypFailed)
    {
        var activeRpfs = ctx.Map.Rpfs;
        var entries = new List<(RpfFileEntry e, bool active)>();
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            bool act = activeRpfs.Contains(rpf);
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && e.NameLower.EndsWith(".ytyp")) entries.Add((fe, act));
        }
        ytypCount = entries.Count;
        var found = new ConcurrentBag<MloArchInfo>();
        int failed = 0;
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = threads }, it =>
        {
            var e = it.e;
            try
            {
                var ytyp = ctx.Rpf.GetFile<YtypFile>(e);
                if (ytyp?.AllArchetypes == null) { Interlocked.Increment(ref failed); return; }
                foreach (var a in ytyp.AllArchetypes)
                {
                    if (a is not MloArchetype m) continue;
                    var dlc = GameContext.DlcOf(e.Path);
                    found.Add(new MloArchInfo
                    {
                        Hash = m.Hash,
                        Name = GameContext.HashName(m.Hash),
                        Arch = m,
                        YtypPath = e.Path,
                        Dlc = dlc,
                        Order = DlcOrder(ctx, dlc),
                        Active = it.active,
                        BBMin = m.BBMin,
                        BBMax = m.BBMax,
                    });
                }
            }
            catch (Exception) { Interlocked.Increment(ref failed); }
        });
        ytypFailed = failed;
        var dict = new Dictionary<uint, MloArchInfo>();
        foreach (var a in found.OrderBy(a => a.Active ? 1 : 0).ThenBy(a => a.Order).ThenBy(a => a.YtypPath, StringComparer.OrdinalIgnoreCase))
        {
            if (dict.TryGetValue(a.Hash, out var old)) { a.AlsoIn.AddRange(old.AlsoIn); a.AlsoIn.Add(old.YtypPath); }
            dict[a.Hash] = a;
        }
        return dict;
    }

    static List<MloInstInfo> ReadYmap(GameContext ctx, RpfResourceFileEntry e, bool active)
    {
        var res = new List<MloInstInfo>();
        var data = e.File.ExtractFile(e);
        if (data == null) return null;
        var rd = new ResourceDataReader(e, data);
        var meta = rd.ReadBlock<Meta>();
        var cmap = MetaTypes.GetTypedData<CMapData>(meta, MetaName.CMapData);
        var eptrs = MetaTypes.GetPointerArray(meta, cmap.entities);
        if (eptrs == null) return res;
        var mlos = MetaTypes.GetTypedPointerArray<CMloInstanceDef>(meta, MetaName.CMloInstanceDef, eptrs);
        if (mlos == null || mlos.Length == 0) return res;
        var name = e.NameLower.EndsWith(".ymap") ? e.NameLower.Substring(0, e.NameLower.Length - 5) : e.NameLower;
        var dlc = GameContext.DlcOf(e.Path);
        string origin = e.Path.StartsWith("update\\", StringComparison.OrdinalIgnoreCase)
            ? (e.Path.Contains("\\dlcpacks\\", StringComparison.OrdinalIgnoreCase) ? "dlc" : "update")
            : (dlc.Length > 0 ? "dlc" : "base");
        foreach (var m in mlos)
        {
            var ce = m.CEntityDef;
            var q = new Quaternion(ce.rotation);
            if (q.LengthSquared() < 1e-12f) q = Quaternion.Identity; else q.Normalize();
            var sets = MetaTypes.GetHashArray(meta, m.defaultEntitySets);
            res.Add(new MloInstInfo
            {
                ArchHash = ce.archetypeName,
                ArchName = GameContext.HashName(ce.archetypeName),
                Ymap = name,
                YmapPath = e.Path,
                Dlc = dlc,
                Origin = origin,
                Active = active,
                Order = DlcOrder(ctx, dlc),
                YmapFlags = cmap.flags,
                YmapContentFlags = cmap.contentFlags,
                Pos = ce.position,
                Rot = q,
                ScaleXY = ce.scaleXY,
                ScaleZ = ce.scaleZ,
                EntityFlags = ce.flags,
                Guid = ce.guid,
                GroupId = m.groupId,
                FloorId = m.floorId,
                NumExitPortals = m.numExitPortals,
                MloInstFlags = m.MLOInstflags,
                DefaultEntitySets = sets?.Select(h => GameContext.HashName(h)).ToArray() ?? Array.Empty<string>(),
            });
        }
        return res;
    }

    /// <summary>
    /// Every CMloInstanceDef of every active .ymap, plus "story only" placements: interiors that only an
    /// overlaid (inactive) base/patch ymap places and that no active interior stands in for. The active set is
    /// CodeWalker's all-DLC world, i.e. the multiplayer map, where the heist-era packs swapped each district's
    /// metadata rpf for one without the single-player script interiors (hospital, car showroom, trailer...).
    /// Only the Meta block of each ymap is parsed (no entity objects are built).
    /// </summary>
    public static List<MloInstInfo> ScanInstances(GameContext ctx, int threads, out ScanCounts counts)
    {
        var activeSet = new HashSet<RpfFileEntry>(ctx.Map.YmapDict.Values);
        var entries = new List<RpfFileEntry>();
        foreach (var rpf in ctx.Gfc.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
                if (e is RpfFileEntry fe && e.NameLower.EndsWith(".ymap")) entries.Add(fe);
        }
        var bag = new ConcurrentBag<MloInstInfo>();
        int nonMeta = 0, failed = 0;
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = threads }, e =>
        {
            bool act = activeSet.Contains(e);
            try
            {
                if (e is not RpfResourceFileEntry res) { if (act) Interlocked.Increment(ref nonMeta); return; }
                var l = ReadYmap(ctx, res, act);
                if (l == null) { if (act) Interlocked.Increment(ref failed); return; }
                foreach (var i in l) bag.Add(i);
            }
            catch (Exception) { if (act) Interlocked.Increment(ref failed); }
        });
        counts = new ScanCounts { YmapsAll = entries.Count, YmapsActive = activeSet.Count, ActiveNonMeta = nonMeta, ActiveFailed = failed };

        // deterministic order, then drop exact duplicates (same archetype at the same place from two ymaps)
        IEnumerable<MloInstInfo> Sorted(IEnumerable<MloInstInfo> src) => src.OrderBy(i => i.ArchName, StringComparer.Ordinal)
            .ThenBy(i => i.Pos.X).ThenBy(i => i.Pos.Y).ThenBy(i => i.Pos.Z)
            .ThenBy(i => i.ScriptManaged ? 1 : 0).ThenBy(i => i.Ymap, StringComparer.Ordinal).ThenBy(i => i.YmapPath, StringComparer.OrdinalIgnoreCase);
        var res = new List<MloInstInfo>();
        foreach (var i in Sorted(bag.Where(i => i.Active)))
        {
            var prev = res.Count > 0 ? res[^1] : null;
            if (prev != null && SamePlacement(prev, i)) { prev.DuplicateYmaps.Add(i.Ymap); continue; }
            res.Add(i);
        }
        counts.ActiveInstances = res.Count;

        // inactive ymaps: newest version of each ymap name that has no active version
        var activeNames = new HashSet<uint>(ctx.Map.YmapDict.Keys);
        var story = new List<MloInstInfo>();
        foreach (var g in bag.Where(i => !i.Active && !activeNames.Contains(JenkHash.GenHash(i.Ymap))).GroupBy(i => i.Ymap).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var bestPath = g.OrderBy(i => i.Order).ThenBy(i => i.YmapPath, StringComparer.OrdinalIgnoreCase).Last().YmapPath;
            foreach (var i in g.Where(i => i.YmapPath == bestPath))
            {
                // an active interior at the same spot stands in for it (e.g. hei_heist_police_dlc for v_policehub)
                var by = res.FirstOrDefault(a => Vector3.DistanceSquared(a.Pos, i.Pos) < 2f * 2f);
                if (by != null) { by.Supersedes.Add($"{i.ArchName}@{i.Ymap}"); counts.Superseded++; continue; }
                i.StoryOnly = true;
                story.Add(i);
            }
        }
        var storyKept = new List<MloInstInfo>();
        foreach (var i in Sorted(story))
        {
            var prev = storyKept.Count > 0 ? storyKept[^1] : null;
            if (prev != null && SamePlacement(prev, i)) { prev.DuplicateYmaps.Add(i.Ymap); continue; }
            storyKept.Add(i);
        }
        counts.StoryOnlyInstances = storyKept.Count;
        res.AddRange(storyKept);
        for (int k = 0; k < res.Count; k++) res[k].Id = k;
        return res;
    }

    static bool SamePlacement(MloInstInfo a, MloInstInfo b) =>
        a.ArchHash == b.ArchHash && Vector3.DistanceSquared(a.Pos, b.Pos) < 0.05f * 0.05f && Math.Abs(Quaternion.Dot(a.Rot, b.Rot)) > 0.9999f;

    /// <summary>traintracks.xml + the node files it names (patch priority: update.rpf first).</summary>
    public static List<TrainTrackInfo> LoadTrainTracks(GameContext ctx)
    {
        var res = new List<TrainTrackInfo>();
        var xe = ctx.FindCommon(@"levels\gta5\traintracks.xml");
        if (xe == null) return res;
        var doc = new XmlDocument();
        doc.LoadXml(StripBom(System.Text.Encoding.UTF8.GetString(ctx.ReadEntry(xe))));
        foreach (XmlNode n in doc.DocumentElement.SelectNodes("train_track"))
        {
            string A(string k) => n.Attributes?[k]?.Value ?? "";
            var t = new TrainTrackInfo
            {
                File = A("filename"),
                Config = A("trainConfigName"),
                PingPong = A("isPingPongTrack") == "true",
                StopsAtStations = A("stopsAtStations") == "true",
                MpStopsAtStations = A("MPstopsAtStations") == "true",
                Speed = float.TryParse(A("speed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) ? sp : 0,
                BrakingDist = float.TryParse(A("brakingDist"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bd) ? bd : 0,
            };
            // filename looks like "common:/data/levels/gta5/trains1.dat"
            var rel = t.File.Replace('/', '\\');
            int di = rel.IndexOf(@"data\", StringComparison.OrdinalIgnoreCase);
            if (di >= 0) rel = rel.Substring(di + 5);
            t.Name = Path.GetFileNameWithoutExtension(rel);
            var fe = ctx.FindCommon(rel);
            if (fe == null) { res.Add(t); continue; }
            var text = StripBom(System.Text.Encoding.UTF8.GetString(ctx.ReadEntry(fe)));
            foreach (var line in text.Split('\n'))
            {
                var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 4) continue;
                if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
                if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
                if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
                int.TryParse(p[3], out var ty);
                t.Nodes.Add(new TrackNode { Pos = new Vector3(x, y, z), Type = ty });
            }
            res.Add(t);
        }
        return res;
    }

    static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
}
