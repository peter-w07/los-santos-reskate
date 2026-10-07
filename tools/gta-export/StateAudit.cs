using System.Text.Json;
using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>
/// Map-state audit (work item E2): read-only reports about which map data the game would have loaded at the
/// single-player story start, written under an audit folder. Nothing here changes the export itself.
/// </summary>
public static class StateAudit
{
    public static void Run(GameContext ctx, Opts o, string outDir, int threads)
    {
        var what = o.Str("what", "dlc");
        var auditDir = Path.GetFullPath(o.Str("audit", Path.Combine(outDir, "audit")));
        Directory.CreateDirectory(auditDir);
        switch (what)
        {
            case "dlc": DlcChangeSets(ctx, auditDir); break;
            case "scan": Scan(ctx, auditDir, threads); break;
            case "scripts": ScriptScan.Run(ctx, auditDir, threads, o.Str("scripts")?.Replace('/', Path.DirectorySeparatorChar)); break;
            case "ysc-debug": ScriptScan.Debug(ctx, o.Str("scripts")?.Replace('/', Path.DirectorySeparatorChar), o.Str("names", "initial")); break;
            case "ysc-dump": ScriptScan.Dump(ctx, auditDir, o.Str("scripts")?.Replace('/', Path.DirectorySeparatorChar), o.Str("names", "building_controller").Split(',')); break;
            case "building-states": BuildingStateScan.Run(ctx, auditDir, threads, o.Str("scripts")?.Replace('/', Path.DirectorySeparatorChar), o.Int("global", 41857)); break;
            default: throw new ArgumentException("audit --what dlc|scan|scripts");
        }
    }

    /// <summary>
    /// Every DLC pack's setup2.xml change-set groups and content.xml change sets, as the game data states them.
    /// CodeWalker applies every change set of every pack; the game applies a group only in the mode it names
    /// (GROUP_MAP: the multiplayer map, executed by ON_ENTER_MP; the other groups at startup / title update).
    /// </summary>
    static void DlcChangeSets(GameContext ctx, string auditDir)
    {
        var gfc = ctx.Gfc;
        Paths.WriteJson(Path.Combine(auditDir, "dlc_changesets.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "setup2.xml contentChangeSetGroups and content.xml contentChangeSets of every DLC pack in load order (game data, read-only).");
            w.WriteStartArray("packs");
            foreach (var s in gfc.DlcSetupFiles)
            {
                if (s.DlcFile == null) continue;
                var c = s.ContentFile;
                w.WriteStartObject();
                w.WriteString("name", GameContext.DlcOf(s.DlcFile.Path + "\\"));
                w.WriteString("rpf", Paths.Fwd(s.DlcFile.Path));
                w.WriteString("device", s.deviceName);
                w.WriteString("type", s.type);
                w.WriteNumber("order", s.order);
                w.WriteBoolean("is_level_pack", s.isLevelPack);
                w.WriteStartArray("groups");
                foreach (var g in s.contentChangeSetGroups ?? new())
                {
                    w.WriteStartObject();
                    w.WriteString("group", g.NameHash);
                    w.WriteStartArray("change_sets");
                    foreach (var n in g.ContentChangeSets) w.WriteStringValue(n);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("data_files");
                foreach (var kv in c?.RpfDataFiles ?? new())
                {
                    w.WriteStartObject();
                    w.WriteString("key", kv.Key);
                    w.WriteString("filename", kv.Value.filename);
                    w.WriteString("file_type", kv.Value.fileType);
                    w.WriteString("contents", kv.Value.contents);
                    w.WriteBoolean("overlay", kv.Value.overlay);
                    w.WriteBoolean("disabled", kv.Value.disabled);
                    w.WriteBoolean("persistent", kv.Value.persistent);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("change_sets");
                foreach (var cs in c?.contentChangeSets ?? new())
                {
                    w.WriteStartObject();
                    w.WriteString("name", cs.changeSetName);
                    w.WriteBoolean("use_cache_loader", cs.useCacheLoader);
                    if (cs.executionConditions != null)
                    {
                        w.WriteString("active_changeset_conditions", cs.executionConditions.activeChangesetConditions ?? "");
                        w.WriteString("generic_conditions", cs.executionConditions.genericConditions ?? "");
                    }
                    StrArr(w, "files_to_enable", cs.filesToEnable);
                    StrArr(w, "files_to_disable", cs.filesToDisable);
                    StrArr(w, "files_to_invalidate", cs.filesToInvalidate);
                    w.WriteStartArray("map_change_sets");
                    foreach (var m in cs.mapChangeSetData ?? new())
                    {
                        w.WriteStartObject();
                        w.WriteString("associated_map", m.associatedMap ?? "");
                        StrArr(w, "files_to_invalidate", m.filesToInvalidate);
                        StrArr(w, "files_to_disable", m.filesToDisable);
                        StrArr(w, "files_to_enable", m.filesToEnable);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        Log.Info("wrote " + Path.Combine(auditDir, "dlc_changesets.json"));
    }

    sealed class YmapRow
    {
        public string Path, Name;
        public bool Active, Ok;
        public uint NameHash, Parent, Flags, ContentFlags;
        public int Entities, Parented, Mlo;
        public int[] Lod = new int[7];
        public float[] Ext = new float[6];
        public uint[] Phys = Array.Empty<uint>();
    }

    /// <summary>
    /// Inventory of every .ymap, _manifest.ymf and .ybn in every archive of the install (active or not), so the
    /// analysis can ask what the story-mode map would have had where CodeWalker's all-DLC view replaced an archive.
    /// </summary>
    static void Scan(GameContext ctx, string auditDir, int threads)
    {
        var gfc = ctx.Gfc;
        var activeRpf = new Dictionary<RpfFile, string>();
        foreach (var kv in gfc.ActiveMapRpfFiles) activeRpf[kv.Value] = kv.Key;
        var activeYmap = new HashSet<RpfFileEntry>(gfc.YmapDict.Values);
        var activeYbn = new HashSet<RpfFileEntry>(gfc.YbnDict.Values);

        var ymapEntries = new List<RpfFileEntry>();
        var ymfEntries = new List<RpfFileEntry>();
        var ybnEntries = new List<RpfFileEntry>();
        foreach (var rpf in gfc.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries)
            {
                if (e is not RpfFileEntry fe) continue;
                if (e.NameLower.EndsWith(".ymap")) ymapEntries.Add(fe);
                else if (e.NameLower.EndsWith(".ymf")) ymfEntries.Add(fe);
                else if (e.NameLower.EndsWith(".ybn")) ybnEntries.Add(fe);
            }
        }

        // rpf list with the virtual path CodeWalker mounted it under (null = not part of the active set)
        Paths.WriteJson(Path.Combine(auditDir, "rpfs_all.json"), w =>
        {
            w.WriteStartArray();
            foreach (var rpf in gfc.AllRpfs.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                w.WriteStartObject();
                w.WriteString("path", Paths.Fwd(rpf.Path));
                if (activeRpf.TryGetValue(rpf, out var vp)) w.WriteString("active_as", vp); else w.WriteNull("active_as");
                w.WriteNumber("entries", rpf.AllEntries?.Count ?? 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        var rows = new YmapRow[ymapEntries.Count];
        Parallel.For(0, ymapEntries.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var e = ymapEntries[i];
            var r = new YmapRow { Path = e.Path, Name = e.GetShortNameLower(), Active = activeYmap.Contains(e), NameHash = e.ShortNameHash };
            rows[i] = r;
            try
            {
                if (e is not RpfResourceFileEntry res) return;
                var data = res.File.ExtractFile(res);
                if (data == null) return;
                var rd = new ResourceDataReader(res, data);
                var meta = rd.ReadBlock<Meta>();
                var cmap = MetaTypes.GetTypedData<CMapData>(meta, MetaName.CMapData);
                r.Parent = cmap.parent; r.Flags = cmap.flags; r.ContentFlags = cmap.contentFlags;
                var a = cmap.streamingExtentsMin; var b = cmap.streamingExtentsMax;
                r.Ext = new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z };
                r.Phys = MetaTypes.GetHashArray(meta, cmap.physicsDictionaries)?.Select(h => h.Hash).ToArray() ?? Array.Empty<uint>();
                var eptrs = MetaTypes.GetPointerArray(meta, cmap.entities);
                if (eptrs != null)
                {
                    var ents = MetaTypes.GetTypedPointerArray<CEntityDef>(meta, MetaName.CEntityDef, eptrs);
                    if (ents != null)
                        foreach (var en in ents)
                        {
                            r.Entities++;
                            int l = (int)en.lodLevel; if (l >= 0 && l < 7) r.Lod[l]++;
                            if (en.parentIndex >= 0) r.Parented++;
                        }
                    var mlos = MetaTypes.GetTypedPointerArray<CMloInstanceDef>(meta, MetaName.CMloInstanceDef, eptrs);
                    if (mlos != null) r.Mlo = mlos.Length;
                }
                r.Ok = true;
            }
            catch (Exception) { }
        });
        Paths.WriteJson(Path.Combine(auditDir, "ymaps_all.json"), w =>
        {
            w.WriteStartArray();
            foreach (var r in rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                w.WriteStartObject();
                w.WriteString("path", Paths.Fwd(r.Path));
                w.WriteString("name", r.Name);
                w.WriteNumber("hash", r.NameHash);
                w.WriteBoolean("active", r.Active);
                w.WriteBoolean("ok", r.Ok);
                w.WriteNumber("flags", r.Flags);
                w.WriteNumber("content_flags", r.ContentFlags);
                w.WriteNumber("parent", r.Parent);
                w.WriteString("parent_name", GameContext.HashName(r.Parent));
                w.WriteNumber("entities", r.Entities);
                w.WriteNumber("parented", r.Parented);
                w.WriteNumber("mlo", r.Mlo);
                w.WriteStartArray("lod"); foreach (var n in r.Lod) w.WriteNumberValue(n); w.WriteEndArray();
                w.WriteStartArray("ext"); foreach (var f in r.Ext) w.WriteNumberValue(JsonExt.R(f)); w.WriteEndArray();
                w.WriteStartArray("phys"); foreach (var h in r.Phys) w.WriteStringValue(GameContext.HashName(h)); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        Paths.WriteJson(Path.Combine(auditDir, "manifests_all.json"), w =>
        {
            w.WriteStartArray();
            foreach (var e in ymfEntries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                YmfFile y = null;
                try { y = ctx.Rpf.GetFile<YmfFile>(e); } catch (Exception) { }
                w.WriteStartObject();
                w.WriteString("path", Paths.Fwd(e.Path));
                w.WriteBoolean("active_rpf", activeRpf.ContainsKey(e.File));
                w.WriteBoolean("ok", y != null && (y.Pso != null || y.Rbf != null || y.Meta != null));
                w.WriteStartArray("groups");
                foreach (var g in y?.MapDataGroups ?? Array.Empty<YmfMapDataGroup>())
                {
                    w.WriteStartObject();
                    w.WriteString("name", GameContext.HashName(g.Name));
                    w.WriteNumber("hash", g.Name.Hash);
                    w.WriteNumber("flags", g.Flags);
                    w.WriteNumber("hours_on_off", g.HoursOnOff);
                    w.WriteStartArray("weather"); foreach (var x in g.WeatherTypes ?? Array.Empty<MetaHash>()) w.WriteStringValue(GameContext.HashName(x)); w.WriteEndArray();
                    if (g.Bounds == null) w.WriteNull("bounds");
                    else { w.WriteStartArray("bounds"); foreach (var x in g.Bounds) w.WriteStringValue(GameContext.HashName(x)); w.WriteEndArray(); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("interiors");
                foreach (var it in y?.Interiors ?? Array.Empty<YmfInterior>())
                {
                    w.WriteStartObject();
                    w.WriteString("name", GameContext.HashName(it.Interior.Name));
                    w.WriteStartArray("bounds"); foreach (var x in it.Bounds ?? Array.Empty<MetaHash>()) w.WriteStringValue(GameContext.HashName(x)); w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });

        Paths.WriteJson(Path.Combine(auditDir, "ybn_all.json"), w =>
        {
            w.WriteStartArray();
            foreach (var e in ybnEntries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                w.WriteStartObject();
                w.WriteString("path", Paths.Fwd(e.Path));
                w.WriteString("name", e.GetShortNameLower());
                w.WriteBoolean("active", activeYbn.Contains(e));
                w.WriteBoolean("active_rpf", activeRpf.ContainsKey(e.File));
                w.WriteNumber("offset", e.FileOffset);
                w.WriteNumber("size", e.GetFileSize());
                w.WriteNumber("stored", e.FileSize);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        Log.Info($"scan: {gfc.AllRpfs.Count} rpfs ({activeRpf.Count} active), {ymapEntries.Count} ymap ({rows.Count(r => r.Ok)} read, {activeYmap.Count} active), {ymfEntries.Count} manifests, {ybnEntries.Count} ybn ({activeYbn.Count} active) -> {auditDir}");
    }

    static void StrArr(Utf8JsonWriter w, string name, List<string> l)
    {
        w.WriteStartArray(name);
        if (l != null) foreach (var s in l) w.WriteStringValue(s);
        w.WriteEndArray();
    }
}
