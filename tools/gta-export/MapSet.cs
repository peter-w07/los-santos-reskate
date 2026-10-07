using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>
/// Which level archives are mounted, resolved from the game's own DLC data instead of CodeWalker's approximation.
///
/// Every map archive of a DLC pack is a data file that starts disabled; a content change set enables it, and
/// setup2.xml puts each change set into a group. The game executes GROUP_MAP only for the multiplayer map
/// (ON_ENTER_MP); the title-update groups (GROUP_UPDATE_STREAMING, GROUP_STARTUP, GROUP_EARLY_ON ...) always.
///
///   story   base archives + every always-executed change set: the single-player map
///   online  story + every GROUP_MAP change set: the multiplayer map (what CodeWalker shows with DLC on)
///   codewalker  CodeWalker's own active set, unchanged (kept for comparison with exports made before this class)
///
/// Two details CodeWalker gets wrong and this class does not:
///  - an archive enabled on the platform path of one that is already mounted overlays it file by file (the patch
///    packs ship partial archives, overlay=true); CodeWalker swaps the whole archive and loses the other files;
///  - CodeWalker skips the pack patchday27ng altogether because, applied in pack order, it would undo the
///    multiplayer replacements. Run in two phases (startup groups of all packs, then GROUP_MAP of all packs) it
///    is harmless, as in the game.
/// filesToInvalidate removes the archive at that platform path together with everything overlaid on it.
/// </summary>
public sealed class MapSet
{
    public sealed class Image
    {
        public string VPath;        // platform-relative path, e.g. x64/levels/gta5/_citye/downtown_01/dt1_03.rpf
        public RpfFile Rpf;
        public string Source;       // "base" or "pack:changeset"
        public bool Overlay;        // mounted on top of another archive at the same path
        public int Seq;             // mount sequence
    }

    public string Profile;
    public List<Image> Images = new();                         // mount order; a later archive wins per file name
    public Dictionary<uint, RpfFileEntry> YbnDict = new();
    public Dictionary<uint, RpfFileEntry> YmapDict = new();
    public HashSet<RpfFile> Rpfs = new();
    public List<YmfFile> Manifests = new();
    public List<CacheDatFile> CacheFiles = new();
    public List<string> Log = new();
    public int Invalidated, InvalidateMisses, Unresolved, StartupArchives, MapArchives, OverlayArchives;
    /// <summary>ybn names of CodeWalker's active set, for stable ids (see CollisionExport).</summary>
    public HashSet<uint> LegacyYbn = new();
    /// <summary>
    /// Profile online only: the story-mode state of the same install (after the startup groups, before GROUP_MAP).
    /// Used to find single-player map groups whose archives the multiplayer map unloads without replacing them.
    /// </summary>
    public Dictionary<uint, RpfFileEntry> StoryYbnDict = new();
    public List<RpfFileEntry> StoryManifestEntries = new();

    static readonly HashSet<string> NotAtStartup = new(StringComparer.OrdinalIgnoreCase) { "GROUP_MAP", "GROUP_MAP_SP", "GROUP_ON_DEMAND" };
    static readonly Regex BaseRx = new(@"^x64[a-w]\.rpf\\(.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly string[] Profiles = { "online", "story", "codewalker" };

    public static MapSet Build(GameContext ctx, string profile)
    {
        profile = (profile ?? "online").ToLowerInvariant();
        if (!Profiles.Contains(profile)) throw new ArgumentException("--profile must be one of " + string.Join(", ", Profiles));
        var gfc = ctx.Gfc;
        var ms = new MapSet { Profile = profile };
        foreach (var k in gfc.YbnDict.Keys) ms.LegacyYbn.Add(k);

        if (profile == "codewalker")
        {
            foreach (var kv in gfc.ActiveMapRpfFiles) ms.Images.Add(new Image { VPath = kv.Key, Rpf = kv.Value, Source = "codewalker" });
            ms.YbnDict = gfc.YbnDict;
            ms.YmapDict = gfc.YmapDict;
            ms.Manifests = gfc.AllManifests;
            ms.CacheFiles = gfc.AllCacheFiles;
            foreach (var i in ms.Images) ms.Rpfs.Add(i.Rpf);
            return ms;
        }

        var rpfByPath = new Dictionary<string, RpfFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in gfc.AllRpfs) rpfByPath[r.Path] = r;

        // mounted archives per platform path, in the order the paths first appeared
        var stack = new Dictionary<string, List<Image>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        int seq = 0;
        void Mount(string vpath, RpfFile rpf, string source)
        {
            if (!stack.TryGetValue(vpath, out var l)) { stack[vpath] = l = new List<Image>(); order.Add(vpath); }
            l.Add(new Image { VPath = vpath, Rpf = rpf, Source = source, Overlay = l.Count > 0, Seq = seq++ });
        }

        // 1. the base game: every archive inside x64a.rpf .. x64w.rpf
        foreach (var r in gfc.AllRpfs.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var m = BaseRx.Match(r.Path);
            if (m.Success) Mount("x64/" + m.Groups[1].Value.Replace('\\', '/').ToLowerInvariant(), r, "base");
        }
        // update.rpf itself (loose patched files; no level archive lives directly in it on this install)
        if (rpfByPath.TryGetValue("update\\update.rpf", out var upd)) Mount("update/update.rpf", upd, "update");

        // 2. DLC packs
        var devices = new Dictionary<string, DlcSetupFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in gfc.DlcSetupFiles) if (s.DlcFile != null && s.deviceName != null) devices[s.deviceName.TrimEnd(':')] = s;

        RpfFile Physical(string reference, out string vpath)
        {
            vpath = null;
            var f = reference.Replace('\\', '/');
            int c = f.IndexOf(":/", StringComparison.Ordinal);
            if (c <= 0) return null;
            var dev = f.Substring(0, c);
            var rel = f.Substring(c + 2).Replace("%PLATFORM%", "x64", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
            vpath = dev.Equals("platform", StringComparison.OrdinalIgnoreCase) ? "x64/" + rel : rel;
            if (!devices.TryGetValue(dev, out var setup)) return null;
            var dlcPath = setup.DlcFile.Path;                                   // update\x64\dlcpacks\<name>\dlc.rpf
            var name = GameContext.DlcOf(dlcPath + "\\");
            var relBs = rel.Replace('/', '\\');
            if (rpfByPath.TryGetValue("update\\update.rpf\\dlc_patch\\" + name + "\\" + relBs, out var patched)) return patched;
            if (rpfByPath.TryGetValue(dlcPath + "\\" + relBs, out var phys)) return phys;
            for (int i = 1; i <= Math.Max(setup.subPackCount, 5); i++)
                if (rpfByPath.TryGetValue(dlcPath.Replace("\\dlc.rpf", "\\dlc" + i + ".rpf", StringComparison.OrdinalIgnoreCase) + "\\" + relBs, out var sub)) return sub;
            return null;
        }

        void Enable(string pack, string cs, string reference, ref int counter)
        {
            if (!reference.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) return;
            var rpf = Physical(reference, out var vpath);
            if (rpf == null) { ms.Unresolved++; ms.Log.Add($"unresolved {pack}:{cs} {reference}"); return; }
            Mount(vpath, rpf, pack + ":" + cs);
            counter++;
        }

        void Invalidate(string pack, string cs, string reference)
        {
            Physical(reference, out var vpath);
            if (vpath == null) vpath = reference.ToLowerInvariant();
            if (stack.TryGetValue(vpath, out var l) && l.Count > 0)
            {
                ms.Invalidated++;
                ms.Log.Add($"invalidated {pack}:{cs} {vpath} ({string.Join(" + ", l.Select(i => i.Source))})");
                l.Clear();
            }
            else { ms.InvalidateMisses++; ms.Log.Add($"invalidate-miss {pack}:{cs} {vpath}"); }
        }

        void RunGroups(Func<List<string>, bool> pick, ref int counter)
        {
            foreach (var s in gfc.DlcSetupFiles)
            {
                if (s.DlcFile == null || s.ContentFile == null) continue;
                var pack = GameContext.DlcOf(s.DlcFile.Path + "\\");
                var groupsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in s.contentChangeSetGroups ?? new())
                    foreach (var n in g.ContentChangeSets)
                    {
                        if (!groupsOf.TryGetValue(n, out var gl)) groupsOf[n] = gl = new List<string>();
                        gl.Add(g.NameHash);
                    }
                foreach (var cs in s.ContentFile.contentChangeSets)
                {
                    if (cs.changeSetName == null || !groupsOf.TryGetValue(cs.changeSetName, out var groups) || !pick(groups)) continue;
                    foreach (var f in cs.filesToInvalidate ?? new()) Invalidate(pack, cs.changeSetName, f);
                    foreach (var f in cs.filesToEnable ?? new()) Enable(pack, cs.changeSetName, f, ref counter);
                    foreach (var m in cs.mapChangeSetData ?? new())
                    {
                        foreach (var f in m.filesToInvalidate ?? new()) Invalidate(pack, cs.changeSetName, f);
                        foreach (var f in m.filesToEnable ?? new()) Enable(pack, cs.changeSetName, f, ref counter);
                    }
                }
            }
        }

        RunGroups(groups => !groups.Any(g => NotAtStartup.Contains(g)), ref ms.StartupArchives);
        if (profile == "online")
        {
            // remember what story mode has before the multiplayer map change sets run
            foreach (var img in order.SelectMany(vp => stack[vp]).OrderBy(i => i.Seq))
            {
                if (img.Rpf.AllEntries == null) continue;
                foreach (var e in img.Rpf.AllEntries)
                {
                    if (e is not RpfFileEntry fe) continue;
                    if (e.NameLower.EndsWith(".ybn")) ms.StoryYbnDict[e.ShortNameHash] = fe;
                    else if (e.Name.EndsWith(".ymf")) ms.StoryManifestEntries.Add(fe);
                }
            }
            RunGroups(groups => groups.Contains("GROUP_MAP", StringComparer.OrdinalIgnoreCase), ref ms.MapArchives);
        }

        // 3. per-name resolution in mount order: the archive mounted last wins
        foreach (var vp in order)
            foreach (var img in stack[vp]) { ms.Images.Add(img); ms.Rpfs.Add(img.Rpf); if (img.Overlay) ms.OverlayArchives++; }
        ms.Images.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        foreach (var img in ms.Images)
        {
            if (img.Rpf.AllEntries == null) continue;
            foreach (var e in img.Rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe) continue;
                if (e.NameLower.EndsWith(".ybn")) ms.YbnDict[e.ShortNameHash] = fe;
                else if (e.NameLower.EndsWith(".ymap")) ms.YmapDict[e.ShortNameHash] = fe;
            }
        }

        // 4. manifests of every mounted archive, in mount order (a later manifest replaces a group of the same name)
        foreach (var img in ms.Images)
        {
            if (img.Rpf.AllEntries == null) continue;
            foreach (var e in img.Rpf.AllEntries)
            {
                if (!e.Name.EndsWith(".ymf")) continue;
                try { var y = gfc.RpfMan.GetFile<YmfFile>(e); if (y != null) ms.Manifests.Add(y); }
                catch (Exception) { }
            }
        }

        // 5. bounds caches: the base cache always; a DLC cache only when the change set it belongs to was executed
        var executed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var img in ms.Images) executed.Add(img.Source);
        foreach (var c in gfc.AllCacheFiles)
        {
            var p = c.FileEntry?.Path ?? "";
            if (!p.Contains("cacheloaderdata_dlc", StringComparison.OrdinalIgnoreCase)) { ms.CacheFiles.Add(c); continue; }
            if (profile == "online") { ms.CacheFiles.Add(c); continue; }
            // story: keep a DLC cache only if its change set belongs to an always-executed group
            var file = Path.GetFileName(p);
            bool keep = false;
            foreach (var s in gfc.DlcSetupFiles)
            {
                if (s.DlcFile == null || s.ContentFile == null) continue;
                var pack = GameContext.DlcOf(s.DlcFile.Path + "\\");
                foreach (var cs in s.ContentFile.contentChangeSets)
                {
                    if (!cs.useCacheLoader || cs.changeSetName == null) continue;
                    if (!file.Equals(pack + "_" + JenkHash.GenHash(cs.changeSetName.ToLowerInvariant()) + "_cache_y.dat", StringComparison.OrdinalIgnoreCase)) continue;
                    var groups = (s.contentChangeSetGroups ?? new()).Where(g => g.ContentChangeSets.Contains(cs.changeSetName, StringComparer.OrdinalIgnoreCase)).Select(g => g.NameHash).ToList();
                    if (groups.Count > 0 && !groups.Any(g => NotAtStartup.Contains(g))) keep = true;
                }
            }
            if (keep) ms.CacheFiles.Add(c);
        }
        return ms;
    }

    public string Summary() =>
        $"map set '{Profile}': {Images.Count} archives ({OverlayArchives} overlays; {StartupArchives} enabled by startup groups, {MapArchives} by GROUP_MAP; " +
        $"{Invalidated} invalidated, {InvalidateMisses} invalidate misses, {Unresolved} unresolved), {YbnDict.Count} ybn, {YmapDict.Count} ymap, {Manifests.Count} manifests, {CacheFiles.Count} cache files";
}
