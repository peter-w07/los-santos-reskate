using System.Collections.Concurrent;
using System.Text.Json;
using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>
/// Phase 1: every static (non-interior) YBN of the DLC-resolved world -> one flat world-space LSCT mesh
/// under collision/ybn/, plus collision/sources.json describing what was exported and what was skipped.
/// Resumable: a mesh whose header signature matches the source entry is not rebuilt.
/// </summary>
public static class CollisionExport
{
    public sealed class YbnResult
    {
        public YbnInfo Info;
        public int Id;
        public string File;      // relative to collision/
        public string Error;
        public bool Reused;
        public FlattenStats Stats;
        public LsctHeader Header;
        public long Bytes;
    }

    public static string MeshFileName(string ybnName) => ybnName + ".lsct";

    static ulong SourceSignature(YbnInfo y)
    {
        var h = Fnv64.Create();
        h.Add("lsct-mesh");
        h.Add(LsctHeader.CurrentVersion);
        h.Add((ulong)YbnFlattener.TessellationVersion);
        h.Add(y.RpfPath.ToLowerInvariant());
        h.Add((ulong)y.Entry.FileOffset);
        h.Add((ulong)y.Entry.FileSize);
        h.Add((ulong)y.Entry.GetFileSize());
        h.Add((ulong)y.Layer);
        return h.Value;
    }

    public static void Run(GameContext ctx, string outDir, int threads, bool force, string onlyPrefix = null)
    {
        Mesh.SelfCheck();
        double t0 = Log.Seconds;
        var colDir = Path.Combine(outDir, "collision");
        var ybnDir = Path.Combine(colDir, "ybn");
        Directory.CreateDirectory(ybnDir);

        var all = ctx.ClassifyBounds();
        var statics = all.Where(y => !y.Interior).ToList();
        var interiors = all.Where(y => y.Interior).ToList();
        // Ids: files that CodeWalker's active set already had keep the id they had before MapSet existed (name
        // order); files only the corrected map set mounts follow in name order. Tiles that do not contain a new
        // or changed source therefore stay byte-identical. (Any other profile: plain name order.)
        if (ctx.Map.Profile == "online")
            statics = statics.Where(y => ctx.Map.LegacyYbn.Contains(y.Hash)).Concat(statics.Where(y => !ctx.Map.LegacyYbn.Contains(y.Hash))).ToList();
        if (onlyPrefix != null) statics = statics.Where(y => y.Name.Contains(onlyPrefix)).ToList();
        Log.Info($"ybn: {all.Count} active, {statics.Count} static world bounds to export, {interiors.Count} interior (MLO) bounds skipped");

        var statsPath = Path.Combine(ybnDir, "_flatten_stats.json");
        var oldStats = LoadFlattenStats(statsPath);

        var results = new YbnResult[statics.Count];
        int done = 0, reused = 0, failed = 0;
        long bytes = 0;
        double lastLog = Log.Seconds;
        Parallel.For(0, statics.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var y = statics[i];
            var r = new YbnResult { Info = y, Id = i, File = "ybn/" + MeshFileName(y.Name) };
            results[i] = r;
            var path = Path.Combine(ybnDir, MeshFileName(y.Name));
            try
            {
                ulong sig = SourceSignature(y);
                if (!force && Mesh.TryReadHeader(path, out var eh) && eh.Signature == sig && eh.Kind == 1)
                {
                    r.Header = eh;
                    r.Reused = true;
                    r.Bytes = new FileInfo(path).Length;
                    oldStats.TryGetValue(y.Name, out r.Stats);
                    Interlocked.Increment(ref reused);
                }
                else
                {
                    var ybn = ctx.Rpf.GetFile<YbnFile>(y.Entry);
                    if (ybn?.Bounds == null) throw new InvalidDataException("ybn has no bounds / could not be read");
                    var mesh = YbnFlattener.Flatten(ybn.Bounds, (byte)y.Layer, y.Hash, out var st);
                    mesh.Header.Signature = sig;
                    Paths.AtomicWrite(path, mesh.Write);
                    r.Header = mesh.Header;
                    r.Stats = st;
                    r.Bytes = mesh.ByteSize;
                }
                Interlocked.Add(ref bytes, r.Bytes);
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
                Interlocked.Increment(ref failed);
            }
            int d = Interlocked.Increment(ref done);
            if (Log.Seconds - Volatile.Read(ref lastLog) > 5)
            {
                Volatile.Write(ref lastLog, Log.Seconds);
                Log.Info($"  phase 1: {d}/{statics.Count} ybn ({reused} reused, {failed} failed)");
            }
        });

        double secs = Log.Seconds - t0;
        long tris = results.Where(r => r.Error == null).Sum(r => (long)r.Header.NTris);
        Log.Info($"phase 1 done: {statics.Count} ybn ({reused} reused, {failed} failed), {tris:N0} triangles, {bytes / 1048576.0:F0} MiB in {secs:F1}s");

        // remove meshes of sources that no longer exist (game update), unless this was a filtered debug run
        if (onlyPrefix == null)
        {
            var keep = new HashSet<string>(results.Select(r => MeshFileName(r.Info.Name)), StringComparer.OrdinalIgnoreCase);
            int stale = 0;
            foreach (var f in Directory.GetFiles(ybnDir, "*.lsct"))
                if (!keep.Contains(Path.GetFileName(f))) { File.Delete(f); stale++; }
            if (stale > 0) Log.Info($"removed {stale} stale mesh files");
        }

        // persist flatten stats across resumed runs
        var newStats = new Dictionary<string, FlattenStats>();
        foreach (var r in results) if (r.Stats != null) newStats[r.Info.Name] = r.Stats;
        SaveFlattenStats(statsPath, newStats);

        WriteSources(ctx, colDir, results, interiors, secs);
        WriteInteriors(ctx, colDir, interiors);
    }

    static Dictionary<string, FlattenStats> LoadFlattenStats(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, FlattenStats>>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true }) ?? new();
        }
        catch (Exception) { }
        return new();
    }

    static void SaveFlattenStats(string path, Dictionary<string, FlattenStats> stats)
    {
        Paths.AtomicWriteText(path, JsonSerializer.Serialize(stats, new JsonSerializerOptions { IncludeFields = true }));
    }

    static void WriteSources(GameContext ctx, string colDir, YbnResult[] results, List<YbnInfo> interiors, double secs)
    {
        var gfc = ctx.Gfc;
        Paths.WriteJson(Path.Combine(colDir, "sources.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "Static world collision sources (phase 1 of gta-export). Ids index the 'ybns' array.");
            w.WriteStartObject("game");
            w.WriteString("install", Paths.Fwd(ctx.GameDir));
            w.WriteString("exe_version", ExeVersion(ctx.GameDir));
            w.WriteNumber("rpf_count", gfc.AllRpfs.Count);
            w.WriteNumber("dlc_count", gfc.DlcNameList.Count);
            w.WriteString("last_dlc", gfc.SelectedDlc);
            w.WriteBoolean("dlc_enabled", gfc.EnableDlc);
            w.WriteBoolean("mods_enabled", gfc.EnableMods);
            w.WriteStartArray("dlc_order");
            foreach (var d in gfc.DlcNameList) w.WriteStringValue(d);
            w.WriteEndArray();
            w.WriteStartArray("cache_files");
            foreach (var c in ctx.Map.CacheFiles) w.WriteStringValue(Paths.Fwd(c.FileEntry?.Path));
            w.WriteEndArray();
            w.WriteNumber("init_errors", ctx.Errors.Count);
            w.WriteString("map_profile", ctx.Map.Profile);
            w.WriteStartObject("map_set");
            w.WriteString("note", "which level archives count as mounted (tools/gta-export MapSet.cs): base archives, then the change sets of the always-executed groups, then (profile online) the GROUP_MAP change sets of the multiplayer map");
            w.WriteNumber("archives", ctx.Map.Images.Count);
            w.WriteNumber("overlay_archives", ctx.Map.OverlayArchives);
            w.WriteNumber("enabled_by_startup_groups", ctx.Map.StartupArchives);
            w.WriteNumber("enabled_by_group_map", ctx.Map.MapArchives);
            w.WriteNumber("invalidated", ctx.Map.Invalidated);
            w.WriteNumber("ybn", ctx.Map.YbnDict.Count);
            w.WriteNumber("ybn_not_in_codewalker_active_set", ctx.Map.YbnDict.Keys.Count(k => !ctx.Map.LegacyYbn.Contains(k)));
            w.WriteNumber("ybn_from_another_archive_than_codewalker", ctx.Map.YbnDict.Count(kv => gfc.YbnDict.TryGetValue(kv.Key, out var ce) && ce != kv.Value));
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteNumber("phase1_seconds", Math.Round(secs, 1));
            w.WriteNumber("phase1_reused", results.Count(r => r.Reused));
            w.WriteNumber("tessellation_version", YbnFlattener.TessellationVersion);

            // totals of things that the flat mesh no longer shows
            var tot = new FlattenStats();
            int withStats = 0;
            foreach (var r in results)
            {
                var s = r.Stats; if (s == null) continue; withStats++;
                tot.Groups += s.Groups; tot.Triangles += s.Triangles; tot.DegenerateTriangles += s.DegenerateTriangles;
                tot.BadIndexPolys += s.BadIndexPolys; tot.Boxes += s.Boxes; tot.Spheres += s.Spheres; tot.Capsules += s.Capsules;
                tot.Cylinders += s.Cylinders; tot.StandaloneBounds += s.StandaloneBounds; tot.ClothSkipped += s.ClothSkipped;
                tot.UnknownBounds += s.UnknownBounds; tot.NonRigidTransforms += s.NonRigidTransforms;
                tot.NonCompositeRoot += s.NonCompositeRoot; tot.NullChildren += s.NullChildren; tot.PrimTriangles += s.PrimTriangles;
            }
            w.WriteStartObject("flatten_totals");
            w.WriteNumber("ybns_with_stats", withStats);
            w.WriteNumber("groups", tot.Groups);
            w.WriteNumber("triangles", tot.Triangles);
            w.WriteNumber("primitive_triangles", tot.PrimTriangles);
            w.WriteNumber("boxes", tot.Boxes);
            w.WriteNumber("spheres", tot.Spheres);
            w.WriteNumber("capsules", tot.Capsules);
            w.WriteNumber("cylinders", tot.Cylinders);
            w.WriteNumber("standalone_primitive_bounds", tot.StandaloneBounds);
            w.WriteNumber("dropped_degenerate_triangles", tot.DegenerateTriangles);
            w.WriteNumber("dropped_bad_index_polygons", tot.BadIndexPolys);
            w.WriteNumber("skipped_cloth_bounds", tot.ClothSkipped);
            w.WriteNumber("skipped_unknown_bounds", tot.UnknownBounds);
            w.WriteNumber("non_rigid_child_transforms", tot.NonRigidTransforms);
            w.WriteNumber("non_composite_roots", tot.NonCompositeRoot);
            w.WriteNumber("null_children", tot.NullChildren);
            w.WriteEndObject();

            w.WriteStartArray("ybns");
            foreach (var r in results)
            {
                var y = r.Info;
                w.WriteStartObject();
                w.WriteNumber("id", r.Id);
                w.WriteString("name", y.Name);
                w.WriteNumber("hash", y.Hash);
                w.WriteNumber("layer", y.Layer);
                w.WriteBoolean("in_cache", y.InCache);
                w.WriteString("origin", y.Origin);
                w.WriteString("dlc", y.Dlc);
                if (y.Group != null) w.WriteString("map_group", y.Group); else w.WriteNull("map_group");
                w.WriteBoolean("default_on", y.DefaultOn);
                w.WriteString("rpf_path", Paths.Fwd(y.RpfPath));
                w.WriteString("file", r.File);
                if (r.Error != null) w.WriteString("error", r.Error);
                else
                {
                    w.WriteNumber("signature", r.Header.Signature);
                    w.WriteNumber("groups", r.Header.NGroups);
                    w.WriteNumber("verts", r.Header.NVerts);
                    w.WriteNumber("tris", r.Header.NTris);
                    w.WriteNumber("prims", r.Header.NPrims);
                    w.WriteNumber("bytes", r.Bytes);
                    w.Vec3("min", r.Header.Min.X, r.Header.Min.Y, r.Header.Min.Z);
                    w.Vec3("max", r.Header.Max.X, r.Header.Max.Y, r.Header.Max.Z);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("map_groups");
            foreach (var g in ctx.Groups.Values.Where(g => g.Bounds.Count > 0).OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteNumber("hash", g.Hash);
                w.WriteNumber("flags", g.Flags);
                w.WriteNumber("hours_on_off", g.HoursOnOff);
                w.WriteStartArray("weather_types");
                foreach (var wt in g.WeatherTypes) w.WriteStringValue(wt);
                w.WriteEndArray();
                w.WriteString("manifest", Paths.Fwd(g.Manifest));
                w.WriteBoolean("ymap_found", g.YmapFound);
                w.WriteNumber("ymap_flags", g.YmapFlags);
                w.WriteNumber("ymap_content_flags", g.YmapContentFlags);
                w.WriteBoolean("script_managed", g.ScriptManaged);
                w.WriteString("ymap_path", Paths.Fwd(g.YmapPath));
                w.WriteBoolean("ymap_mounted", g.YmapMounted);
                w.WriteBoolean("default_on", g.DefaultOn);
                w.WriteString("default_confidence", g.DefaultConfidence);
                w.WriteString("default_reason", g.DefaultReason);
                w.WriteString("default_source", g.DefaultSource);
                w.WriteBoolean("story_only", g.StoryOnly);
                w.WriteNumber("bounds", g.Bounds.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // profile online: story-mode groups whose archives the multiplayer map unloads (added or only listed)
            w.WriteStartArray("story_only_groups");
            foreach (var g in ctx.StoryOnlyGroups)
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteBoolean("default_on", g.DefaultOn);
                w.WriteString("default_confidence", g.DefaultConfidence);
                w.WriteString("default_reason", g.DefaultReason);
                w.WriteString("default_source", g.DefaultSource);
                w.WriteBoolean("added", g.StoryIncluded);
                w.WriteString("note", g.StoryNote);
                w.WriteString("manifest", Paths.Fwd(g.Manifest));
                w.WriteStartArray("files");
                foreach (var f in g.StoryFiles) w.WriteStringValue(f);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // every time/weather dependent group, also those without bounds (props, stalls): for the colour exporter
            w.WriteStartArray("time_groups");
            foreach (var g in ctx.Groups.Values.Where(g => g.HoursOnOff != 0).OrderBy(g => g.Name, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteNumber("hours_on_off", g.HoursOnOff);
                w.WriteStartArray("weather_types");
                foreach (var wt in g.WeatherTypes) w.WriteStringValue(wt);
                w.WriteEndArray();
                w.WriteBoolean("on", g.DefaultOn);
                w.WriteNumber("bounds", g.Bounds.Count);
                w.WriteString("manifest", Paths.Fwd(g.TimeManifest));
                w.WriteBoolean("manifest_mounted", !g.TimeFromUnmounted);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("map_state");
            w.WriteString("note", "default_on = state at the single-player story start, at the given hour in clear weather. default_source: manifest (time/weather mask), script_table (building controller state table), script_absent (no script names the group), script_request (only requested by the scripts listed), table / rule (built-in guesses).");
            w.WriteString("profile", ctx.Map.Profile);
            w.WriteNumber("hour", GroupDefaults.Hour);
            var ev = ctx.Evidence;
            w.WriteBoolean("scripts_read", ev != null && ev.TableOk);
            w.WriteString("script_archive", Paths.Fwd(ev?.Archive ?? ""));
            w.WriteNumber("scripts", ev?.Scripts ?? 0);
            w.WriteNumber("scripts_decoded", ev?.ScriptsDecoded ?? 0);
            w.WriteNumber("building_table_names", ev?.Table.Count ?? 0);
            w.WriteBoolean("request_remove_natives_identified", ev != null && ev.NativesOk);
            w.WriteStartObject("groups_by_source");
            foreach (var kv in ctx.Groups.Values.Where(g => g.Bounds.Count > 0).GroupBy(g => g.DefaultSource).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(kv.Key);
                w.WriteNumber("on", kv.Count(g => g.DefaultOn));
                w.WriteNumber("off", kv.Count(g => !g.DefaultOn));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("skipped");
            w.WriteString("interiors_note", "MLO interior bounds are stored in interior-local space and need the interior instance transform; not exported yet (see interiors.json).");
            w.WriteNumber("interior_ybn_count", interiors.Count);
            w.WriteStartArray("interior_ybns");
            foreach (var y in interiors)
            {
                w.WriteStartObject();
                w.WriteString("name", y.Name);
                w.WriteString("interior", y.InteriorName);
                w.WriteNumber("layer", y.Layer);
                w.WriteString("dlc", y.Dlc);
                w.WriteString("rpf_path", Paths.Fwd(y.RpfPath));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("dynamic_props_note", "Prop/vehicle/ped collision lives inside ydr/yft/ydd drawables (embedded bounds), not in ybn files; none of it is exported.");
            w.WriteStartArray("failed_ybns");
            foreach (var r in results.Where(r => r.Error != null))
            {
                w.WriteStartObject();
                w.WriteString("name", r.Info.Name);
                w.WriteString("error", r.Error);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        });
    }

    static void WriteInteriors(GameContext ctx, string colDir, List<YbnInfo> interiors)
    {
        Paths.WriteJson(Path.Combine(colDir, "interiors.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "Interior (MLO) proxies from the cache_y.dat files and the interior -> bounds lists from the manifests. Interior ybn geometry is NOT exported; this is reference data for a later pass. Positions are GTA world space, orientation is a quaternion xyzw.");
            w.WriteStartArray("proxies");
            foreach (var c in ctx.Map.CacheFiles)
            {
                if (c.AllCInteriorProxies == null) continue;
                foreach (var p in c.AllCInteriorProxies)
                {
                    w.WriteStartObject();
                    w.WriteString("name", GameContext.HashName(p.Name));
                    w.WriteString("parent_ymap", GameContext.HashName(p.Parent));
                    w.Vec3("position", p.Position);
                    w.WriteStartArray("orientation");
                    w.WriteNumberValue(JsonExt.R(p.Orientation.X)); w.WriteNumberValue(JsonExt.R(p.Orientation.Y));
                    w.WriteNumberValue(JsonExt.R(p.Orientation.Z)); w.WriteNumberValue(JsonExt.R(p.Orientation.W));
                    w.WriteEndArray();
                    w.Vec3("bb_min", p.BBMin);
                    w.Vec3("bb_max", p.BBMax);
                    w.WriteString("cache", Paths.Fwd(c.FileEntry?.Path));
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteStartObject("interior_bounds");
            foreach (var kv in ctx.InteriorBounds.OrderBy(k => GameContext.HashName(k.Key), StringComparer.Ordinal))
            {
                w.WriteStartArray(GameContext.HashName(kv.Key));
                foreach (var b in kv.Value) w.WriteStringValue(b);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    public static string ExeVersion(string gameDir)
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(gameDir, "GTA5.exe"));
            return vi.FileVersion ?? "";
        }
        catch (Exception) { return ""; }
    }
}
