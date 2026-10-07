using CodeWalker.GameFiles;
using SharpDX;

namespace GtaExport;

/// <summary>One static-collision candidate file (a .ybn resolved through the DLC/patch overlay).</summary>
public sealed class YbnInfo
{
    public uint Hash;            // jenkins hash of the name without extension (how the game refers to it)
    public string Name;          // lower case, no extension, e.g. "hi@dt1_00_0"
    public RpfFileEntry Entry;   // winning entry after DLC/update overrides
    public string RpfPath;       // path inside the install, e.g. x64i.rpf\levels\gta5\...\hi@dt1_00_0.ybn
    public string Dlc;           // "" for base game, else dlc pack name (also for dlc_patch overrides in update.rpf)
    public string Origin;        // base | dlc | update
    public bool InCache;         // listed in a *_cache_y.dat static bounds store
    public int CacheLayer = -1;  // layer from the cache (0 mover, 1 hi-detail weapon, 2 material), -1 unknown
    public Vector3 CacheMin, CacheMax;
    public bool Interior;        // listed as an interior (MLO) bound in a manifest
    public string InteriorName;
    public string Group;         // map data group (IPL group) the manifest ties this bound to, or null
    public ushort GroupFlags;
    public uint GroupHours;
    public int NameLayer;        // layer guessed from the file name prefix (hi@ = 1, ma@ = 2, else 0)
    public bool DefaultOn = true; // false when the bound belongs to a map group guessed to be off at story start
    public bool Overlaid;         // not in the active set: only found in an rpf that a DLC map pack overlays (interiors export only)
    public bool StoryOnly;        // profile online: file of a story-mode map group whose archive the multiplayer map unloads

    public int Layer => CacheLayer >= 0 ? CacheLayer : NameLayer;
}

public sealed class MapGroupInfo
{
    public uint Hash;
    public string Name;
    public ushort Flags;
    public uint HoursOnOff;
    public string[] WeatherTypes = Array.Empty<string>();
    public uint[] WeatherHashes = Array.Empty<uint>();
    public string Manifest;
    public string TimeManifest = "";   // manifest that carries the hours / weather entry (often the metadata archive's)
    public bool TimeFromUnmounted;     // that manifest is in an archive the profile does not mount (online: base metadata archives)
    public bool StoryOnly;             // profile online: a story-mode group whose bounds archive the multiplayer map unloads
    public bool StoryIncluded;         // ... and whose files were added to the export (on at story start, no multiplayer replacement)
    public string StoryNote = "";
    public List<string> StoryFiles = new();
    public string DefaultSource = "";  // which kind of evidence decided default_on (see GroupDefaults)
    public List<string> Bounds = new();
    public bool YmapFound;        // a .ymap with the group's name exists in the active map data
    public uint YmapFlags;        // CMapData.flags (bit 0 = script managed, bit 1 = LOD in parent)
    public uint YmapContentFlags;
    public string YmapPath = "";
    public bool YmapMounted;      // the ymap is part of the mounted map set (false: only found in an archive that is not mounted)
    public bool ScriptManaged => YmapFound && (YmapFlags & 1) != 0;
    public bool DefaultOn;        // exporter's guess of the story-start state (see GroupDefaults)
    public string DefaultConfidence = "", DefaultReason = "";
}

public sealed class GameContext
{
    public string GameDir;
    public GameFileCache Gfc;
    public RpfManager Rpf => Gfc.RpfMan;
    public readonly List<string> Errors = new();
    public double InitSeconds;
    public MapSet Map;               // which level archives count as mounted (see MapSet)
    public ScriptEvidence Evidence;  // what the game scripts say about IPL names (loaded on first classification)
    public static bool UseScripts = true;
    /// <summary>
    /// Profile online: also export the story-mode map groups that are on at story start but whose archives the
    /// multiplayer map unloads without putting anything in their place (the same policy the interior layer applies
    /// to story-only interior placements). Off: the plain multiplayer map.
    /// </summary>
    public static bool IncludeStoryGroups = true;
    public List<MapGroupInfo> StoryOnlyGroups = new();

    /// <summary>
    /// Story-only groups that are on at story start but must not be added. Empty: for every group checked, the
    /// multiplayer static world covers exactly the same share of the group's surfaces as the story static world
    /// does (it is a renamed copy with the same gaps), so nothing collides.
    /// </summary>
    static readonly Dictionary<string, string> StoryOnlyExclude = new(StringComparer.OrdinalIgnoreCase);
    public static string DefaultProfile = "online";

    public static GameContext Open(string gameDir) => Open(gameDir, DefaultProfile);

    public static GameContext Open(string gameDir, string profile)
    {
        if (!File.Exists(Path.Combine(gameDir, "GTA5.exe")))
            throw new FileNotFoundException("GTA5.exe not found in " + gameDir);

        double t0 = Log.Seconds;
        var ctx = new GameContext { GameDir = gameDir };

        // Keys are derived in memory from the user's own GTA5.exe on every run (about a second);
        // they are never written to disk and never logged.
        Log.Info("deriving archive keys from GTA5.exe (in memory only)...");
        GTA5Keys.LoadFromPath(gameDir, false, null);
        if (GTA5Keys.PC_AES_KEY == null || GTA5Keys.PC_NG_KEYS == null)
            throw new InvalidOperationException("could not derive archive keys from GTA5.exe (unsupported build?)");

        Log.Info("scanning RPF archives (read-only)...");
        var gfc = new GameFileCache(1L << 30, 10.0, gameDir, false, "", false, "Installers;_CommonRedist");
        gfc.EnableDlc = true;               // update.rpf + every DLC in dlclist.xml, in setup2.xml order
        gfc.EnableMods = false;             // never read a mods folder
        gfc.LoadArchetypes = false;
        gfc.LoadVehicles = false;
        gfc.LoadPeds = false;
        gfc.LoadAudio = false;
        gfc.BuildExtendedJenkIndex = false;
        gfc.Init(_ => { }, e =>
        {
            lock (ctx.Errors) ctx.Errors.Add(e.Split('\n')[0]);
        });
        ctx.Gfc = gfc;
        ctx.Map = MapSet.Build(ctx, profile);
        ctx.InitSeconds = Log.Seconds - t0;
        Log.Info($"game opened: {gfc.AllRpfs.Count} rpfs, {gfc.DlcNameList.Count} dlc packs (last: {gfc.SelectedDlc}), " +
                 $"{gfc.YbnDict.Count} ybn in CodeWalker's active set, {gfc.AllCacheFiles.Count} cache files, {gfc.AllManifests.Count} manifests, {ctx.Errors.Count} errors");
        Log.Info(ctx.Map.Summary());
        return ctx;
    }

    /// <summary>Resolve a common data file with patch priority: update.rpf, then update2.rpf, then common.rpf.</summary>
    public RpfFileEntry FindCommon(string relUnderData)
    {
        relUnderData = relUnderData.Replace('/', '\\').TrimStart('\\');
        foreach (var root in new[] { @"update\update.rpf\common\data\", @"update\update2.rpf\common\data\", @"common.rpf\data\" })
        {
            if (Rpf.GetEntry(root + relUnderData) is RpfFileEntry fe) return fe;
        }
        return null;
    }

    public byte[] ReadEntry(RpfFileEntry e) => e?.File.ExtractFile(e);

    public static string HashName(uint h)
    {
        if (h == 0) return "";
        var s = JenkIndex.TryGetString(h);
        return string.IsNullOrEmpty(s) ? "hash_" + h.ToString("x8") : s.ToLowerInvariant();
    }

    public static string DlcOf(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        foreach (var marker in new[] { @"\dlcpacks\", @"\dlc_patch\" })
        {
            int i = p.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0)
            {
                int s = i + marker.Length;
                int e = p.IndexOf('\\', s);
                if (e > s) return p.Substring(s, e - s);
            }
        }
        return "";
    }

    public Dictionary<uint, MapGroupInfo> Groups = new();
    public Dictionary<uint, List<string>> InteriorBounds = new(); // interior name hash -> bound names

    /// <summary>
    /// Classify every active .ybn the way CodeWalker's world loader (Space.InitManifestData/InitCacheData) does:
    /// bounds listed under an interior in a manifest are MLO-local interior collision, everything else is
    /// world-space static collision; the cache_y.dat bounds store supplies the layer for cached files.
    /// </summary>
    /// <summary>
    /// Profile online. The multiplayer map change sets unload ("invalidate") whole base archives and mount renamed
    /// copies (hei_, lr_, apa_ ...). Script-managed groups of the single-player map that lived in those archives are
    /// not copied, so the multiplayer map simply lacks them: the trailers of the trailer park, the dock crane, a
    /// stretch of river surface, the ground plate over the construction shaft ... The export aims at the story-start
    /// state on top of the multiplayer map, so the groups that are ON at story start are added from the story-mode
    /// archives (origin "story_only"); everything else of them is only listed (story_only_groups in the index).
    /// </summary>
    void AddStoryOnlyGroups(List<YbnInfo> list, Dictionary<uint, BoundsStoreItem> cached)
    {
        StoryOnlyGroups.Clear();
        var story = new Dictionary<uint, MapGroupInfo>();
        var boundsOf = new Dictionary<uint, List<uint>>();
        foreach (var e in Map.StoryManifestEntries)
        {
            YmfFile ymf = null;
            try { ymf = Rpf.GetFile<YmfFile>(e); } catch (Exception) { }
            if (ymf?.MapDataGroups == null) continue;
            foreach (var g in ymf.MapDataGroups)
            {
                uint gh = g.Name;
                if (!story.TryGetValue(gh, out var gi)) story[gh] = gi = new MapGroupInfo { Hash = gh, Name = HashName(gh), Flags = g.Flags, Manifest = e.Path };
                if (g.Bounds != null)
                {
                    gi.Manifest = e.Path; gi.Flags = g.Flags;
                    boundsOf[gh] = g.Bounds.Select(b => b.Hash).ToList();
                }
                if (g.HoursOnOff != 0 || (g.WeatherTypes?.Length ?? 0) > 0)
                {
                    gi.HoursOnOff = g.HoursOnOff;
                    gi.WeatherHashes = g.WeatherTypes?.Select(w => w.Hash).ToArray() ?? Array.Empty<uint>();
                    gi.WeatherTypes = g.WeatherTypes?.Select(w => HashName(w)).ToArray() ?? Array.Empty<string>();
                    gi.TimeManifest = e.Path; gi.TimeFromUnmounted = true;
                }
            }
        }
        foreach (var gi in story.Values.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            if (!boundsOf.TryGetValue(gi.Hash, out var bl)) continue;
            if (Groups.TryGetValue(gi.Hash, out var have) && have.Bounds.Count > 0) continue;       // the multiplayer map still has it
            var files = bl.Where(b => !Map.YbnDict.ContainsKey(b) && Map.StoryYbnDict.ContainsKey(b)).ToList();
            if (files.Count == 0) continue;
            gi.StoryOnly = true;
            if (Gfc.AllYmapsDict.TryGetValue(gi.Hash, out var ye))
            {
                try
                {
                    var ymap = Rpf.GetFile<YmapFile>(ye);
                    if (ymap != null) { gi.YmapFound = true; gi.YmapFlags = ymap._CMapData.flags; gi.YmapContentFlags = ymap._CMapData.contentFlags; gi.YmapPath = ye.Path; }
                }
                catch (Exception) { }
            }
            var d = GroupDefaults.Decide(gi.Name, DlcOf(gi.Manifest), gi.HoursOnOff, gi.WeatherHashes, Evidence);
            gi.DefaultOn = d.On; gi.DefaultConfidence = d.Confidence; gi.DefaultReason = d.Reason; gi.DefaultSource = d.Source;
            foreach (var b in files) gi.StoryFiles.Add(Map.StoryYbnDict[b].GetShortNameLower());
            gi.StoryFiles.Sort(StringComparer.Ordinal);
            StoryOnlyGroups.Add(gi);

            if (!IncludeStoryGroups) { gi.StoryNote = "not added (--no-story-groups)"; continue; }
            if (!gi.DefaultOn) { gi.StoryNote = "not added: off at story start"; continue; }
            if (StoryOnlyExclude.TryGetValue(gi.Name, out var why)) { gi.StoryNote = "not added: " + why; continue; }
            gi.StoryIncluded = true;
            gi.StoryNote = "added from the story-mode archives";
            Groups[gi.Hash] = gi;
            foreach (var b in files)
            {
                var e = Map.StoryYbnDict[b];
                var name = e.GetShortNameLower();
                var info = new YbnInfo
                {
                    Hash = b, Name = name, Entry = e, RpfPath = e.Path, Dlc = DlcOf(e.Path), Origin = "story_only", StoryOnly = true,
                    NameLayer = name.StartsWith("hi@") ? 1 : name.StartsWith("ma@") ? 2 : 0,
                    Group = gi.Name, GroupFlags = gi.Flags, GroupHours = gi.HoursOnOff, DefaultOn = true,
                };
                if (cached.TryGetValue(b, out var item)) { info.InCache = true; info.CacheLayer = (int)(item.Layer & 0xFF); info.CacheMin = item.Min; info.CacheMax = item.Max; }
                gi.Bounds.Add(name);
                list.Add(info);
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        Log.Info($"story-only map groups (archives unloaded by the multiplayer map): {StoryOnlyGroups.Count}, added {StoryOnlyGroups.Count(g => g.StoryIncluded)} with {StoryOnlyGroups.Where(g => g.StoryIncluded).Sum(g => g.Bounds.Count)} files");
    }

    public List<YbnInfo> ClassifyBounds()
    {
        var interiorOf = new Dictionary<uint, uint>();
        var groupOf = new Dictionary<uint, MapGroupInfo>();
        foreach (var m in Map.Manifests)
        {
            if (m.Interiors != null)
            {
                foreach (var it in m.Interiors)
                {
                    if (it.Bounds == null) continue;
                    uint iname = it.Interior.Name;
                    if (!InteriorBounds.TryGetValue(iname, out var l)) InteriorBounds[iname] = l = new List<string>();
                    foreach (var b in it.Bounds)
                    {
                        interiorOf[b] = iname;
                        var bn = HashName(b);
                        if (!l.Contains(bn)) l.Add(bn);
                    }
                }
            }
            if (m.MapDataGroups != null)
            {
                foreach (var g in m.MapDataGroups)
                {
                    // A group can have two manifest entries: one in the archive that holds its bounds (bounds list,
                    // no hours) and one in the metadata archive of the district (hours mask + weather list, no
                    // bounds). CodeWalker keeps only the first kind; both are merged here, so time-dependent groups
                    // keep their hours. A later manifest with bounds takes the group over, as before.
                    uint gh = g.Name;
                    var path = m.FileEntry?.Path ?? "";
                    if (!Groups.TryGetValue(gh, out var gi))
                        Groups[gh] = gi = new MapGroupInfo { Hash = gh, Name = HashName(gh), Flags = g.Flags, Manifest = path };
                    if (g.Bounds != null)
                    {
                        gi.Manifest = path; gi.Flags = g.Flags;
                        foreach (var b in g.Bounds) groupOf[b] = gi;
                    }
                    if (g.HoursOnOff != 0 || (g.WeatherTypes?.Length ?? 0) > 0)
                    {
                        gi.HoursOnOff = g.HoursOnOff;
                        gi.WeatherHashes = g.WeatherTypes?.Select(w => w.Hash).ToArray() ?? Array.Empty<uint>();
                        gi.WeatherTypes = g.WeatherTypes?.Select(w => HashName(w)).ToArray() ?? Array.Empty<string>();
                        gi.TimeManifest = path;
                    }
                }
            }
        }

        // Hours of groups whose bounds are mounted but whose hours entry is not: the multiplayer map swaps the
        // metadata archives of the districts (which carry the hours / weather entries of the base groups) for
        // its own, while the archives with the bounds stay. Take the hours from the unmounted manifest then.
        var orphans = groupOf.Values.Distinct().Where(g => g.HoursOnOff == 0).ToDictionary(g => g.Hash);
        if (orphans.Count > 0 && Map.Profile != "story")
        {
            foreach (var rpf in Gfc.AllRpfs)
            {
                if (rpf.AllEntries == null || Map.Rpfs.Contains(rpf)) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (!e.Name.EndsWith(".ymf")) continue;
                    YmfFile ymf = null;
                    try { ymf = Rpf.GetFile<YmfFile>(e); } catch (Exception) { }
                    if (ymf?.MapDataGroups == null) continue;
                    foreach (var g in ymf.MapDataGroups)
                    {
                        if (g.HoursOnOff == 0 || !orphans.TryGetValue(g.Name, out var gi)) continue;
                        gi.HoursOnOff = g.HoursOnOff;
                        gi.WeatherHashes = g.WeatherTypes?.Select(w => w.Hash).ToArray() ?? Array.Empty<uint>();
                        gi.WeatherTypes = g.WeatherTypes?.Select(w => HashName(w)).ToArray() ?? Array.Empty<string>();
                        gi.TimeManifest = e.Path;
                        gi.TimeFromUnmounted = true;
                    }
                }
            }
        }

        var cached = new Dictionary<uint, BoundsStoreItem>();
        foreach (var c in Map.CacheFiles)
        {
            if (c.AllBoundsStoreItems == null) continue;
            foreach (var b in c.AllBoundsStoreItems) cached[b.Name] = b; // later caches (DLC) override earlier
        }

        var list = new List<YbnInfo>(Map.YbnDict.Count);
        foreach (var kv in Map.YbnDict)
        {
            var e = kv.Value;
            var name = e.NameLower;
            if (name.EndsWith(".ybn")) name = name.Substring(0, name.Length - 4);
            var path = e.Path;
            var info = new YbnInfo
            {
                Hash = kv.Key,
                Name = name,
                Entry = e,
                RpfPath = path,
                Dlc = DlcOf(path),
                NameLayer = name.StartsWith("hi@") ? 1 : name.StartsWith("ma@") ? 2 : 0,
            };
            info.Origin = path.StartsWith("update\\", StringComparison.OrdinalIgnoreCase)
                ? (path.Contains("\\dlcpacks\\", StringComparison.OrdinalIgnoreCase) ? "dlc" : "update")
                : (info.Dlc.Length > 0 ? "dlc" : "base");
            if (cached.TryGetValue(kv.Key, out var item))
            {
                info.InCache = true;
                info.CacheLayer = (int)(item.Layer & 0xFF); // main cache stores junk in the upper bytes (0x7F00)
                info.CacheMin = item.Min;
                info.CacheMax = item.Max;
            }
            if (interiorOf.TryGetValue(kv.Key, out var iname))
            {
                info.Interior = true;
                info.InteriorName = HashName(iname);
            }
            if (groupOf.TryGetValue(kv.Key, out var gi))
            {
                info.Group = gi.Name;
                info.GroupFlags = gi.Flags;
                info.GroupHours = gi.HoursOnOff;
                gi.Bounds.Add(name);
            }
            list.Add(info);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        // The group's own ymap tells whether the group is script managed (REQUEST_IPL) or always streamed.
        Parallel.ForEach(Groups.Values.Where(g => g.Bounds.Count > 0), g =>
        {
            try
            {
                if (!Map.YmapDict.TryGetValue(g.Hash, out var ye) && !Gfc.AllYmapsDict.TryGetValue(g.Hash, out ye)) return;
                g.YmapMounted = Map.YmapDict.ContainsKey(g.Hash);
                var ymap = Rpf.GetFile<YmapFile>(ye);
                if (ymap == null) return;
                g.YmapFound = true;
                g.YmapFlags = ymap._CMapData.flags;
                g.YmapContentFlags = ymap._CMapData.contentFlags;
                g.YmapPath = ye.Path;
            }
            catch (Exception) { }
        });
        Evidence ??= UseScripts ? ScriptEvidence.Load(this, Environment.ProcessorCount) : new ScriptEvidence { Note = "--no-scripts" };
        Log.Info(Evidence.Summary());
        foreach (var g in Groups.Values)
        {
            var d = GroupDefaults.Decide(g.Name, DlcOf(g.Manifest), g.HoursOnOff, g.WeatherHashes, Evidence);
            g.DefaultOn = d.On; g.DefaultConfidence = d.Confidence; g.DefaultReason = d.Reason; g.DefaultSource = d.Source;
        }
        foreach (var y in list)
        {
            if (y.Group == null) continue;
            y.DefaultOn = Groups.Values.First(g => g.Name == y.Group).DefaultOn;
        }
        if (Map.Profile == "online") AddStoryOnlyGroups(list, cached);
        return list;
    }
}
