using System.Globalization;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>materials.dat (collision material table) + materialfx.dat debug colours + procedural.meta tags -> JSON.</summary>
public static class MaterialsExport
{
    static readonly string[] Columns =
    {
        "name", "filter", "fx_group", "vfx_disturbance_type", "rumble_profile", "react_weapon_type",
        "friction", "elasticity", "density", "tyre_grip", "wet_grip", "tyre_drag", "top_speed_mult",
        "softness", "noisiness", "penetration_resistance",
        "see_thru", "shoot_thru", "shoot_thru_fx", "no_decal", "porous", "heats_tyre", "material",
    };

    public static void Run(GameContext ctx, string outDir)
    {
        var matEntry = ctx.FindCommon(@"materials\materials.dat") ?? throw new FileNotFoundException("materials.dat not found in update.rpf/common.rpf");
        var fxEntry = ctx.FindCommon(@"effects\materialfx.dat");
        var text = CodeWalker.TextUtil.GetUTF8Text(ctx.ReadEntry(matEntry));
        var fxText = fxEntry != null ? CodeWalker.TextUtil.GetUTF8Text(ctx.ReadEntry(fxEntry)) : "";

        // FX group -> debug colour
        var fx = new Dictionary<string, (int r, int g, int b)>(StringComparer.OrdinalIgnoreCase);
        bool inTable = false;
        foreach (var raw in fxText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("MTLFX_TABLE_START")) { inTable = true; continue; }
            if (line.StartsWith("MTLFX_TABLE_END")) break;
            if (!inTable || line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) continue;
            if (int.TryParse(p[1], out int r) && int.TryParse(p[2], out int g) && int.TryParse(p[3], out int b)) fx[p[0]] = (r, g, b);
        }

        var rows = new List<string[]>();
        string version = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 1 && rows.Count == 0) { version = p[0]; continue; }
            if (p.Length < 10) continue;
            rows.Add(p);
        }
        if (rows.Count == 0 || rows.Count > 256) throw new InvalidDataException($"unexpected materials.dat row count {rows.Count}");

        // every other materials.dat in the install (for the record)
        var others = ctx.Rpf.EntryDict.Keys.Where(k => k.EndsWith(@"\materials.dat")).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Paths.WriteJson(Path.Combine(outDir, "materials.json"), w =>
        {
            w.WriteStartObject();
            w.WriteString("about", "GTA V collision material table. 'id' is the per-polygon material byte in the collision tiles (row order of materials.dat).");
            w.WriteString("source", Paths.Fwd(matEntry.Path));
            w.WriteString("fx_colour_source", Paths.Fwd(fxEntry?.Path ?? ""));
            w.WriteString("file_version", version);
            w.WriteStartArray("other_materials_dat_in_install");
            foreach (var o in others) w.WriteStringValue(Paths.Fwd(o));
            w.WriteEndArray();
            w.WriteString("colour_note", "fx_rgb is the debug colour of the material's FX group from materialfx.dat (game data); preview_rgb is the exporter's own tweak of it used for preview PNGs (not game data).");
            w.WriteNumber("count", rows.Count);
            w.WriteStartArray("materials");
            for (int i = 0; i < rows.Count; i++)
            {
                var p = rows[i];
                w.WriteStartObject();
                w.WriteNumber("id", i);
                for (int c = 0; c < Columns.Length && c < p.Length; c++)
                {
                    if (c >= 6 && c <= 15 && double.TryParse(p[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) w.WriteNumber(Columns[c], d);
                    else if (c >= 16 && c <= 21) w.WriteBoolean(Columns[c], p[c] != "0");
                    else w.WriteString(Columns[c], p[c]);
                }
                var name = p[0];
                var group = p.Length > 2 ? p[2] : "";
                if (fx.TryGetValue(group, out var col))
                {
                    w.WriteStartArray("fx_rgb"); w.WriteNumberValue(col.r); w.WriteNumberValue(col.g); w.WriteNumberValue(col.b); w.WriteEndArray();
                }
                else w.WriteNull("fx_rgb");
                var pv = PreviewColour(name, group, fx);
                w.WriteStartArray("preview_rgb"); w.WriteNumberValue(pv.r); w.WriteNumberValue(pv.g); w.WriteNumberValue(pv.b); w.WriteEndArray();
                w.WriteString("class", Classify(name));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
        Log.Info($"materials: {rows.Count} rows from {matEntry.Path} -> materials.json");

        ExportProcedural(ctx, outDir);
    }

    /// <summary>Coarse class used only to make previews readable and as a hint for palette work.</summary>
    public static string Classify(string n)
    {
        if (n.StartsWith("WATER") || n == "PUDDLE") return "water";
        if (n.StartsWith("GRASS") || n == "HAY") return "grass";
        if (n.StartsWith("BUSHES") || n == "LEAVES" || n == "TWIGS") return "foliage";
        if (n == "TREE_BARK" || n == "WOODCHIPS") return "wood";
        if (n.StartsWith("SANDSTONE")) return "rock";
        if (n.StartsWith("SAND")) return "sand";
        if (n.StartsWith("ROCK") || n == "STONE" || n == "COBBLESTONE" || n == "MARBLE") return "rock";
        if (n.StartsWith("MUD") || n.StartsWith("MARSH") || n == "SOIL" || n.StartsWith("CLAY") || n == "DIRT_TRACK") return "dirt";
        if (n.StartsWith("GRAVEL")) return "gravel";
        if (n.StartsWith("SNOW") || n.StartsWith("ICE")) return "snow";
        if (n.StartsWith("TARMAC") || n == "RUMBLE_STRIP") return "road";
        if (n.StartsWith("CONCRETE") || n == "BREEZE_BLOCK" || n.StartsWith("PAVING") || n.StartsWith("PLASTER")) return "concrete";
        if (n.StartsWith("BRICK") || n == "ROOF_TILE" || n == "CERAMIC") return "brick";
        if (n.StartsWith("METAL") || n.StartsWith("VFX_METAL")) return "metal";
        if (n.StartsWith("WOOD") || n == "VFX_WOOD_BEER_BARREL") return "wood";
        if (n.StartsWith("GLASS") || n == "PERSPEX" || n.StartsWith("EMISSIVE")) return "glass";
        if (n.StartsWith("PLASTIC") || n.StartsWith("FIBREGLASS") || n.StartsWith("RUBBER") || n == "TARPAULIN" || n == "ROOF_FELT") return "plastic";
        if (n.StartsWith("CARPET") || n == "CLOTH" || n == "LINOLEUM" || n == "LAMINATE" || n == "LEATHER") return "fabric";
        if (n.StartsWith("CAR_")) return "vehicle";
        if (n.StartsWith("PHYS_") || n.StartsWith("TEMP_")) return "special";
        if (n == "DEFAULT") return "default";
        return "other";
    }

    static (int r, int g, int b) PreviewColour(string name, string group, Dictionary<string, (int r, int g, int b)> fx)
    {
        switch (Classify(name))
        {
            case "gravel": return name == "GRAVEL_TRAIN_TRACK" ? (120, 105, 95) : (165, 155, 140);
            case "default": return (150, 150, 155);
            case "water": return (55, 145, 230);
            case "snow": return name.StartsWith("ICE") ? (200, 235, 250) : (245, 248, 252);
            case "special": return (200, 60, 200);
        }
        if (fx.TryGetValue(group, out var c) && !(c.r == 255 && c.g == 255 && c.b == 255) && !(c.r == 255 && c.g == 0 && c.b == 255) && !(c.r == 0 && c.g == 0 && c.b == 0))
            return c;
        return (150, 150, 155);
    }

    static void ExportProcedural(GameContext ctx, string outDir)
    {
        try
        {
            var e = ctx.FindCommon(@"materials\procedural.meta");
            if (e == null) { Log.Warn("procedural.meta not found"); return; }
            var txt = CodeWalker.TextUtil.GetUTF8Text(ctx.ReadEntry(e));
            var doc = new XmlDocument();
            doc.LoadXml(txt);
            var tags = doc.SelectNodes("//procTagTable/Item");
            Paths.WriteJson(Path.Combine(outDir, "procedural.json"), w =>
            {
                w.WriteStartObject();
                w.WriteString("about", "procedural.meta procTagTable: index = per-polygon procedural id (proc_id) in the collision tiles. Describes what the game scatters on that surface (grass, litter, rocks).");
                w.WriteString("source", Paths.Fwd(e.Path));
                w.WriteStartArray("proc_tags");
                int i = 0;
                foreach (XmlNode n in tags)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", i++);
                    foreach (XmlNode c in n.ChildNodes)
                    {
                        if (c.NodeType != XmlNodeType.Element) continue;
                        var v = c.Attributes?["value"]?.Value ?? c.InnerText?.Trim();
                        w.WriteString(c.Name, v ?? "");
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            Log.Info($"procedural: {tags.Count} proc tags -> procedural.json");
        }
        catch (Exception ex)
        {
            Log.Warn("procedural.meta export failed: " + ex.Message);
        }
    }
}
