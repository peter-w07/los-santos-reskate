using System.Text.Json;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaExport;

/// <summary>interiors/index.json, portals.json and tracks.json.</summary>
public static class InteriorsJson
{
    public sealed class RunInfo
    {
        public int YtypCount, YtypFailed, Reused, Failed;
        public ScanCounts Scan = new();
        public double MeshSeconds, TileSeconds, TotalSeconds, OpenSeconds;
    }

    static void V(Utf8JsonWriter w, string name, Vector3 v) => w.Vec3(name, v.X, v.Y, v.Z);
    static void V(Utf8JsonWriter w, string name, V3 v) => w.Vec3(name, v.X, v.Y, v.Z);

    static void VArr(Utf8JsonWriter w, Vector3 v)
    {
        w.WriteStartArray();
        w.WriteNumberValue(JsonExt.R(v.X)); w.WriteNumberValue(JsonExt.R(v.Y)); w.WriteNumberValue(JsonExt.R(v.Z));
        w.WriteEndArray();
    }

    public static void Write(GameContext ctx, string intDir, List<InteriorsExport.Inst> insts, List<InteriorsExport.Src> srcs,
        List<Tiler.TileResult> tiles, Dictionary<uint, MloArchInfo> archs, List<TrainTrackInfo> tracks,
        List<YbnInfo> interiorYbns, List<YbnInfo> staticAsInterior, float margin, RunInfo run)
    {
        var cats = InteriorsExport.CategoryNames;
        var srcInst = srcs.ToDictionary(s => s.Id, s => s.Owner);
        var placed = new HashSet<uint>(srcs.Select(s => s.Ybn.Hash));
        var usedArch = new HashSet<uint>(insts.Select(i => i.I.ArchHash));

        Paths.WriteJson(Path.Combine(intDir, "index.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "Interior (MLO) collision of every placed interior instance, transformed to GTA world space. Same LSCT tile format as ../collision; a consumer merges tile (x,y) of both layers. group.ybn_id indexes 'ybns' here; ybns[].interior_id indexes 'interiors'.");
            w.WriteStartObject("format");
            w.WriteString("magic", "LSCT");
            w.WriteNumber("version", LsctHeader.CurrentVersion);
            w.WriteNumber("tile_size", Tiler.TileSize);
            w.WriteNumber("margin", margin);
            w.WriteString("tile_file", "tiles/tile_{x}_{y}.lsct");
            w.WriteString("mesh_file", "mesh/i{interior_id:04}_{ybn}.lsct");
            w.WriteStartArray("categories");
            foreach (var c in cats) w.WriteStringValue(c);
            w.WriteEndArray();
            w.WriteString("group_info_bits", "bits 0-1 layer (0 mover, 1 hi@ weapon detail); bit 2 (4) parent ymap is script managed; bit 3 (8) guessed off at story start; bits 4-6 category index into 'categories'; bit 7 (128) detached");
            w.WriteString("room_ped", "per-triangle room_ped bits 0-4 = room index inside the interior (interiors[].rooms; 0 = limbo/unassigned)");
            w.WriteEndObject();

            w.WriteStartObject("game");
            w.WriteString("install", Paths.Fwd(ctx.GameDir));
            w.WriteString("exe_version", CollisionExport.ExeVersion(ctx.GameDir));
            w.WriteNumber("dlc_count", ctx.Gfc.DlcNameList.Count);
            w.WriteString("last_dlc", ctx.Gfc.SelectedDlc);
            w.WriteNumber("ymaps_in_all_archives", run.Scan.YmapsAll);
            w.WriteNumber("active_ymaps", run.Scan.YmapsActive);
            w.WriteNumber("active_ymaps_not_meta", run.Scan.ActiveNonMeta);
            w.WriteNumber("active_ymaps_unreadable", run.Scan.ActiveFailed);
            w.WriteNumber("instances_from_active_ymaps", run.Scan.ActiveInstances);
            w.WriteNumber("story_only_instances", run.Scan.StoryOnlyInstances);
            w.WriteNumber("inactive_placements_superseded", run.Scan.Superseded);
            w.WriteNumber("ytyps_in_all_archives", run.YtypCount);
            w.WriteNumber("ytyps_without_archetypes", run.YtypFailed);
            w.WriteNumber("mlo_archetypes", archs.Count);
            w.WriteNumber("mlo_archetypes_in_active_set", archs.Values.Count(a => a.Active));
            w.WriteEndObject();

            // extents
            var geo = insts.Where(i => i.HasGeometry).ToList();
            var att = geo.Where(i => !i.Detached).ToList();
            w.WriteStartObject("extents");
            if (geo.Count > 0)
            {
                w.Vec3("min", geo.Min(i => i.Min.X), geo.Min(i => i.Min.Y), geo.Min(i => i.Min.Z));
                w.Vec3("max", geo.Max(i => i.Max.X), geo.Max(i => i.Max.Y), geo.Max(i => i.Max.Z));
            }
            if (att.Count > 0)
            {
                w.Vec3("attached_min", att.Min(i => i.Min.X), att.Min(i => i.Min.Y), att.Min(i => i.Min.Z));
                w.Vec3("attached_max", att.Max(i => i.Max.X), att.Max(i => i.Max.Y), att.Max(i => i.Max.Z));
            }
            if (tiles.Count > 0)
            {
                w.WriteStartArray("tile_range_x"); w.WriteNumberValue(tiles.Min(t => t.X)); w.WriteNumberValue(tiles.Max(t => t.X)); w.WriteEndArray();
                w.WriteStartArray("tile_range_y"); w.WriteNumberValue(tiles.Min(t => t.Y)); w.WriteNumberValue(tiles.Max(t => t.Y)); w.WriteEndArray();
            }
            w.WriteEndObject();

            w.WriteStartObject("totals");
            w.WriteNumber("interiors", insts.Count);
            w.WriteNumber("distinct_archetypes", insts.Select(i => i.I.ArchHash).Distinct().Count());
            w.WriteNumber("interiors_with_collision", geo.Count);
            w.WriteNumber("interiors_attached", insts.Count(i => !i.Detached));
            w.WriteNumber("interiors_detached", insts.Count(i => i.Detached));
            w.WriteNumber("interiors_default_off", insts.Count(i => !i.DefaultOn));
            w.WriteNumber("interiors_story_only", insts.Count(i => i.I.StoryOnly));
            w.WriteNumber("interiors_attached_default_on", insts.Count(i => !i.Detached && i.DefaultOn));
            w.WriteNumber("unique_triangles_attached_default_on", srcs.Where(s => s.Error == null && !s.Owner.Detached && s.Owner.DefaultOn).Sum(s => (long)s.Header.NTris));
            w.WriteNumber("sources", srcs.Count);
            w.WriteNumber("sources_failed", srcs.Count(s => s.Error != null));
            w.WriteNumber("unique_triangles", srcs.Where(s => s.Error == null).Sum(s => (long)s.Header.NTris));
            w.WriteNumber("unique_triangles_attached", srcs.Where(s => s.Error == null && !s.Owner.Detached).Sum(s => (long)s.Header.NTris));
            w.WriteNumber("unique_primitives", srcs.Where(s => s.Error == null).Sum(s => (long)s.Header.NPrims));
            w.WriteNumber("distinct_source_ybn", srcs.Select(s => s.Ybn.Hash).Distinct().Count());
            w.WriteNumber("mesh_bytes", srcs.Sum(s => s.Bytes));
            w.WriteNumber("tiles", tiles.Count);
            w.WriteNumber("tile_triangles", tiles.Sum(t => (long)t.Header.NTris));
            w.WriteNumber("tile_bytes", tiles.Sum(t => t.Bytes));
            w.WriteNumber("portals", insts.Sum(i => i.Portals.Count));
            w.WriteNumber("entrance_portals", insts.Sum(i => i.Portals.Count(p => p.Exterior)));
            w.WriteNumber("open_game_seconds", Math.Round(run.OpenSeconds, 1));
            w.WriteNumber("mesh_seconds", Math.Round(run.MeshSeconds, 1));
            w.WriteNumber("tile_seconds", Math.Round(run.TileSeconds, 1));
            w.WriteNumber("total_seconds", Math.Round(run.TotalSeconds, 1));
            w.WriteNumber("meshes_reused", run.Reused);
            w.WriteEndObject();

            w.WriteStartObject("categories");
            for (int c = 0; c < cats.Length; c++)
            {
                var l = insts.Where(i => i.Category == c).ToList();
                var la = l.Where(i => !i.Detached && i.HasGeometry).ToList();
                w.WriteStartObject(cats[c]);
                w.WriteNumber("index", c);
                w.WriteNumber("instances", l.Count);
                w.WriteNumber("distinct_archetypes", l.Select(i => i.I.ArchHash).Distinct().Count());
                w.WriteNumber("attached", l.Count(i => !i.Detached));
                w.WriteNumber("detached", l.Count(i => i.Detached));
                w.WriteNumber("default_off", l.Count(i => !i.DefaultOn));
                w.WriteNumber("without_collision", l.Count(i => !i.HasGeometry));
                w.WriteNumber("triangles", l.Sum(i => i.Tris));
                w.WriteNumber("attached_triangles", la.Sum(i => i.Tris));
                w.WriteNumber("attached_default_on", la.Count(i => i.DefaultOn));
                w.WriteNumber("attached_default_on_triangles", la.Where(i => i.DefaultOn).Sum(i => i.Tris));
                if (la.Count > 0)
                {
                    w.WriteNumber("attached_z_min", JsonExt.R(la.Min(i => i.Min.Z)));
                    w.WriteNumber("attached_z_max", JsonExt.R(la.Max(i => i.Max.Z)));
                    w.Vec3("attached_min", la.Min(i => i.Min.X), la.Min(i => i.Min.Y), la.Min(i => i.Min.Z));
                    w.Vec3("attached_max", la.Max(i => i.Max.X), la.Max(i => i.Max.Y), la.Max(i => i.Max.Z));
                }
                w.WriteStartObject("kinds");
                foreach (var kg in l.GroupBy(i => i.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)) w.WriteNumber(kg.Key, kg.Count());
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartArray("ybns");
            foreach (var s in srcs)
            {
                var y = s.Ybn; var it = s.Owner;
                w.WriteStartObject();
                w.WriteNumber("id", s.Id);
                w.WriteString("name", y.Name);
                w.WriteNumber("hash", y.Hash);
                w.WriteNumber("layer", y.Layer);
                w.WriteNumber("interior_id", it.I.Id);
                w.WriteString("interior", it.I.ArchName);
                w.WriteString("category", cats[it.Category]);
                w.WriteBoolean("detached", it.Detached);
                w.WriteString("origin", y.Origin);
                w.WriteString("dlc", y.Dlc);
                if (it.I.ScriptManaged) w.WriteString("map_group", it.I.Ymap); else w.WriteNull("map_group");
                w.WriteBoolean("default_on", it.DefaultOn);
                w.WriteString("rpf_path", Paths.Fwd(y.RpfPath));
                if (y.Overlaid) w.WriteBoolean("from_overlaid_rpf", true);
                w.WriteString("file", s.File);
                if (s.Error != null) w.WriteString("error", s.Error);
                else
                {
                    w.WriteNumber("signature", s.Signature);
                    w.WriteNumber("groups", s.Header.NGroups);
                    w.WriteNumber("verts", s.Header.NVerts);
                    w.WriteNumber("tris", s.Header.NTris);
                    w.WriteNumber("prims", s.Header.NPrims);
                    w.WriteNumber("bytes", s.Bytes);
                    V(w, "min", s.Header.Min);
                    V(w, "max", s.Header.Max);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("interiors");
            foreach (var it in insts)
            {
                var r = it.I;
                w.WriteStartObject();
                w.WriteNumber("id", r.Id);
                w.WriteString("name", r.ArchName);
                w.WriteNumber("hash", r.ArchHash);
                w.WriteString("category", cats[it.Category]);
                w.WriteString("kind", it.Kind);
                w.WriteString("category_reason", it.CategoryReason);
                w.WriteBoolean("detached", it.Detached);
                w.WriteString("attached_via", it.AttachedVia);
                w.WriteNumber("static_tris_at_entrances", it.PortalStaticOn);
                w.WriteNumber("static_off_tris_at_entrances", it.PortalStaticOff);
                w.WriteNumber("static_tris_in_box", it.ExteriorTrisNear);
                w.WriteNumber("static_off_tris_in_box", it.ExteriorOffTrisNear);
                w.WriteNumber("component", it.Component);
                w.WriteBoolean("default_on", it.DefaultOn);
                w.WriteString("default_reason", it.DefaultReason);
                w.WriteBoolean("script_managed", r.ScriptManaged);
                w.WriteString("ymap", r.Ymap);
                w.WriteString("ymap_path", Paths.Fwd(r.YmapPath));
                w.WriteNumber("ymap_flags", r.YmapFlags);
                w.WriteString("origin", r.Origin);
                w.WriteString("dlc", r.Dlc);
                if (r.DuplicateYmaps.Count > 0)
                {
                    w.WriteStartArray("also_placed_by");
                    foreach (var d in r.DuplicateYmaps) w.WriteStringValue(d);
                    w.WriteEndArray();
                }
                w.WriteString("placement", r.StoryOnly ? "story_only" : "active");
                if (r.Supersedes.Count > 0)
                {
                    w.WriteStartArray("supersedes");
                    foreach (var d in r.Supersedes) w.WriteStringValue(d);
                    w.WriteEndArray();
                }
                w.WriteNumber("instances_of_archetype", it.SameArchCount);
                V(w, "position", r.Pos);
                w.WriteStartArray("rotation_xyzw");
                w.WriteNumberValue(Math.Round(r.Rot.X, 6)); w.WriteNumberValue(Math.Round(r.Rot.Y, 6)); w.WriteNumberValue(Math.Round(r.Rot.Z, 6)); w.WriteNumberValue(Math.Round(r.Rot.W, 6));
                w.WriteEndArray();
                // heading of the interior's local +x axis in the world, degrees counter-clockwise from east
                var ax = Vector3.TransformNormal(Vector3.UnitX, it.Xf);
                w.WriteNumber("heading_deg", Math.Round(Math.Atan2(ax.Y, ax.X) * 180.0 / Math.PI, 2));
                // interior-local -> world, row-major 3x4: world = R * local + T
                w.WriteStartArray("local_to_world");
                var m = it.Xf;
                foreach (var f in new[] { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 }) w.WriteNumberValue(Math.Round(f, 6));
                w.WriteEndArray();
                if (Math.Abs(r.ScaleXY - 1) > 1e-4 || Math.Abs(r.ScaleZ - 1) > 1e-4) { w.WriteNumber("scale_xy", r.ScaleXY); w.WriteNumber("scale_z", r.ScaleZ); }
                w.WriteNumber("entity_flags", r.EntityFlags);
                w.WriteNumber("mlo_inst_flags", r.MloInstFlags);
                w.WriteNumber("group_id", r.GroupId);
                w.WriteNumber("floor_id", r.FloorId);
                w.WriteNumber("num_exit_portals", r.NumExitPortals);
                if (r.DefaultEntitySets.Length > 0)
                {
                    w.WriteStartArray("default_entity_sets");
                    foreach (var s in r.DefaultEntitySets) w.WriteStringValue(s);
                    w.WriteEndArray();
                }
                if (r.Arch != null)
                {
                    w.WriteString("ytyp", Paths.Fwd(r.Arch.YtypPath));
                    w.WriteBoolean("ytyp_in_active_set", r.Arch.Active);
                    if (r.Arch.AlsoIn.Count > 0)
                    {
                        w.WriteStartArray("ytyp_overrides");
                        foreach (var s in r.Arch.AlsoIn) w.WriteStringValue(Paths.Fwd(s));
                        w.WriteEndArray();
                    }
                    w.WriteNumber("mlo_flags", r.Arch.Arch._MloArchetypeDefData.mloFlags);
                    w.WriteNumber("entities", r.Arch.Arch.entities?.Length ?? 0);
                }
                else w.WriteNull("ytyp");
                w.WriteString("bounds_from", it.BoundsFrom);
                w.WriteStartArray("bounds");
                foreach (var b in it.Bounds) w.WriteStringValue(b.Name);
                w.WriteEndArray();
                w.WriteStartArray("ybn_ids");
                foreach (var s in it.Sources) w.WriteNumberValue(s.Id);
                w.WriteEndArray();
                w.WriteNumber("tris", it.Tris);
                w.WriteNumber("prims", it.Prims);
                V(w, "min", it.Min);
                V(w, "max", it.Max);
                V(w, "local_min", it.LocalMin);
                V(w, "local_max", it.LocalMax);
                if (it.MetroNodes > 0) { w.WriteNumber("metro_track_nodes_inside", it.MetroNodes); w.WriteNumber("metro_station_stops_inside", it.MetroStationNodes); }
                if (it.RailNodes > 0) w.WriteNumber("rail_track_nodes_inside", it.RailNodes);
                w.WriteNumber("portals", it.Portals.Count);
                w.WriteNumber("entrances", it.Portals.Count(p => p.Exterior));
                w.WriteNumber("entrances_to_other_interiors", it.Portals.Count(p => p.Exterior && p.LinkInst >= 0));
                w.WriteStartArray("rooms");
                var rooms = r.Arch?.Arch.rooms;
                if (rooms != null)
                {
                    for (int i = 0; i < rooms.Length; i++)
                    {
                        var rm = rooms[i];
                        w.WriteStartObject();
                        w.WriteNumber("index", i);
                        w.WriteString("name", rm.RoomName ?? "");
                        V(w, "local_min", rm.BBMin);
                        V(w, "local_max", rm._Data.bbMax);
                        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                        for (int c = 0; c < 8; c++)
                        {
                            var p = new Vector3((c & 1) != 0 ? rm._Data.bbMax.X : rm._Data.bbMin.X, (c & 2) != 0 ? rm._Data.bbMax.Y : rm._Data.bbMin.Y, (c & 4) != 0 ? rm._Data.bbMax.Z : rm._Data.bbMin.Z);
                            var wp = Vector3.TransformCoordinate(p, it.Xf);
                            mn = Vector3.Min(mn, wp); mx = Vector3.Max(mx, wp);
                        }
                        V(w, "min", mn);
                        V(w, "max", mx);
                        w.WriteNumber("flags", rm._Data.flags);
                        w.WriteNumber("portal_count", rm._Data.portalCount);
                        w.WriteNumber("floor_id", rm._Data.floorId);
                        w.WriteNumber("exterior_visibility_depth", rm._Data.exteriorVisibiltyDepth);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("tiles");
            foreach (var t in tiles)
            {
                w.WriteStartObject();
                w.WriteNumber("x", t.X);
                w.WriteNumber("y", t.Y);
                w.WriteString("file", "tiles/" + Tiler.TileFileName(t.X, t.Y));
                w.WriteNumber("tris", t.Header.NTris);
                w.WriteNumber("verts", t.Header.NVerts);
                w.WriteNumber("prims", t.Header.NPrims);
                w.WriteNumber("groups", t.Header.NGroups);
                w.WriteNumber("z_min", JsonExt.R(t.Header.Min.Z));
                w.WriteNumber("z_max", JsonExt.R(t.Header.Max.Z));
                w.WriteNumber("bytes", t.Bytes);
                w.WriteStartArray("ybns");
                foreach (var id in t.Ybns) w.WriteNumberValue(id);
                w.WriteEndArray();
                var its = t.Ybns.Select(id => srcInst[id]).Distinct().OrderBy(i => i.I.Id).ToList();
                w.WriteStartArray("interiors");
                foreach (var i in its) w.WriteNumberValue(i.I.Id);
                w.WriteEndArray();
                w.WriteStartArray("categories");
                foreach (var c in its.Select(i => i.Category).Distinct().OrderBy(c => c)) w.WriteStringValue(cats[c]);
                w.WriteEndArray();
                w.WriteBoolean("all_detached", its.All(i => i.Detached));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("notes");
            w.WriteStartArray("interior_ybn_never_placed");
            foreach (var y in interiorYbns.Where(y => !placed.Contains(y.Hash))) { w.WriteStartObject(); w.WriteString("name", y.Name); w.WriteString("interior", y.InteriorName); w.WriteString("dlc", y.Dlc); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("archetypes_never_placed");
            foreach (var a in archs.Values.Where(a => !usedArch.Contains(a.Hash)).OrderBy(a => a.Name, StringComparer.Ordinal)) w.WriteStringValue(a.Name);
            w.WriteEndArray();
            w.WriteStartArray("instances_without_archetype");
            foreach (var i in insts.Where(i => i.I.Arch == null)) w.WriteStringValue($"{i.I.Id}:{i.I.ArchName}@{i.I.Ymap}");
            w.WriteEndArray();
            w.WriteStartArray("instances_without_collision");
            foreach (var i in insts.Where(i => !i.HasGeometry)) w.WriteStringValue($"{i.I.Id}:{i.I.ArchName}@{i.I.Ymap}");
            w.WriteEndArray();
            w.WriteStartArray("static_ybn_named_like_an_interior");
            foreach (var y in staticAsInterior) w.WriteStringValue(y.Name);
            w.WriteEndArray();
            w.WriteStartArray("interiors_with_bounds_from_overlaid_rpf");
            foreach (var i in insts.Where(i => i.Bounds.Any(b => b.Overlaid))) w.WriteStringValue($"{i.I.Id}:{i.I.ArchName}");
            w.WriteEndArray();
            w.WriteNumber("triangles_from_overlaid_rpf", srcs.Where(x => x.Error == null && x.Ybn.Overlaid).Sum(x => (long)x.Header.NTris));
            w.WriteStartArray("failed_sources");
            foreach (var s in srcs.Where(s => s.Error != null)) { w.WriteStartObject(); w.WriteString("file", s.File); w.WriteString("error", s.Error); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        });

        // ---- portals.json
        Paths.WriteJson(Path.Combine(intDir, "portals.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "MLO portals in GTA world space. kind 'entrance': one side is room 0 (limbo = outside the interior) - the opening where the interior meets the outside world or the next interior; 'internal': doorway between two rooms of the same interior; 'mirror': not an opening. corners are the portal polygon (normally a quad).");
            w.WriteNumber("portals", insts.Sum(i => i.Portals.Count));
            w.WriteNumber("entrances", insts.Sum(i => i.Portals.Count(p => p.Exterior)));
            w.WriteNumber("entrances_attached", insts.Where(i => !i.Detached).Sum(i => i.Portals.Count(p => p.Exterior)));
            w.WriteNumber("entrances_linked_to_another_interior", insts.Sum(i => i.Portals.Count(p => p.Exterior && p.LinkInst >= 0)));
            w.WriteNumber("entrances_meeting_the_static_world", insts.Sum(i => i.Portals.Count(p => p.Exterior && p.LinkInst < 0 && p.StaticOn > 0)));
            w.WriteString("opens_to", "interior: a coincident entrance portal of another interior (linked_interior_id); outside: default-on static solid triangles within 2 m of the opening (static_tris_near); switched_off_map_group: only default-off static geometry there; nothing: no static geometry there (window in mid-air, teleport interior)");
            w.WriteStartArray("list");
            foreach (var it in insts)
            {
                var rooms = it.I.Arch?.Arch.rooms;
                string RoomName(uint i) => rooms != null && i < rooms.Length ? rooms[i].RoomName ?? "" : "";
                foreach (var p in it.Portals)
                {
                    w.WriteStartObject();
                    w.WriteNumber("interior_id", it.I.Id);
                    w.WriteString("interior", it.I.ArchName);
                    w.WriteString("category", cats[it.Category]);
                    w.WriteBoolean("detached", it.Detached);
                    w.WriteNumber("portal", p.Index);
                    w.WriteString("kind", p.Mirror ? "mirror" : p.Exterior ? "entrance" : "internal");
                    w.WriteNumber("room_from", p.RoomFrom);
                    w.WriteNumber("room_to", p.RoomTo);
                    w.WriteString("room_from_name", RoomName(p.RoomFrom));
                    w.WriteString("room_to_name", RoomName(p.RoomTo));
                    w.WriteNumber("flags", p.Flags);
                    w.WriteNumber("opacity", p.Opacity);
                    V(w, "centre", p.Centre);
                    V(w, "normal", p.Normal);
                    w.WriteNumber("width", JsonExt.R(p.Width));
                    w.WriteNumber("height", JsonExt.R(p.Height));
                    if (p.Corners.Length > 0)
                    {
                        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                        foreach (var c in p.Corners) { mn = Vector3.Min(mn, c); mx = Vector3.Max(mx, c); }
                        V(w, "min", mn);
                        V(w, "max", mx);
                    }
                    w.WriteStartArray("corners");
                    foreach (var c in p.Corners) VArr(w, c);
                    w.WriteEndArray();
                    if (p.Exterior)
                    {
                        if (p.LinkInst >= 0)
                        {
                            w.WriteString("opens_to", "interior");
                            w.WriteNumber("linked_interior_id", p.LinkInst);
                            w.WriteNumber("linked_portal", p.LinkPortal);
                            w.WriteNumber("link_distance", JsonExt.R(p.LinkDist));
                        }
                        else w.WriteString("opens_to", p.StaticOn > 0 ? "outside" : p.StaticOff > 0 ? "switched_off_map_group" : "nothing");
                        w.WriteNumber("static_tris_near", p.StaticOn);
                        if (p.StaticOff > 0) w.WriteNumber("static_off_tris_near", p.StaticOff);
                    }
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

        // ---- tracks.json
        Paths.WriteJson(Path.Combine(intDir, "tracks.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "Train tracks from traintracks.xml and the node files it names. nodes: [x, y, z, type] in GTA world space, in track order, z at rail/floor level. type is a bit field: 1 = station stop, 4 = the game treats the node as inside a tunnel.");
            w.WriteStartArray("tracks");
            foreach (var t in tracks)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("file", t.File);
                w.WriteString("train_config", t.Config);
                w.WriteBoolean("is_metro", t.IsMetro);
                w.WriteBoolean("ping_pong", t.PingPong);
                w.WriteBoolean("stops_at_stations", t.StopsAtStations);
                w.WriteBoolean("mp_stops_at_stations", t.MpStopsAtStations);
                w.WriteNumber("speed", t.Speed);
                w.WriteNumber("braking_dist", t.BrakingDist);
                w.WriteNumber("node_count", t.Nodes.Count);
                double len = 0;
                for (int i = 1; i < t.Nodes.Count; i++) len += Vector3.Distance(t.Nodes[i - 1].Pos, t.Nodes[i].Pos);
                w.WriteNumber("length_m", Math.Round(len, 1));
                w.WriteStartArray("nodes");
                foreach (var n in t.Nodes)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(JsonExt.R(n.Pos.X)); w.WriteNumberValue(JsonExt.R(n.Pos.Y)); w.WriteNumberValue(JsonExt.R(n.Pos.Z)); w.WriteNumberValue(n.Type);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
