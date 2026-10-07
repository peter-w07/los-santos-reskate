using System.Collections.Concurrent;
using CodeWalker.GameFiles;

namespace GtaSkate;

/// <summary>What a triangle is, as far as turning it into a Minecraft block is concerned.</summary>
public enum VClass : byte
{
    Opaque = 0,     // solid surface, colour is the albedo
    Cutout = 1,     // alpha tested solid (fences, grilles, railings): alpha = fraction of the surface that exists
    Decal = 2,      // thin overlay on another surface (road markings, signs painted on walls): alpha = opacity
    Glass = 3,      // glass shader
    Water = 4,      // water shader (rivers, pools, fountains); colour is a constant
    Terrain = 5,    // blended terrain shader
    Leaves = 6,     // foliage cards of a trees* shader: alpha = coverage
    Wood = 7,       // trunk / branches of a trees* shader
    Alpha = 8,      // other alpha blended geometry
    Grass = 9,      // "fur" grass shell lying on a lawn (grass_fur*): marks the ground below as grass, colour is a hint only
}

public static class VFlags
{
    public const ushort Emissive = 1, NightOnly = 2, Tinted = 4, NoTexture = 8, Prop = 16,
        Interior = 64, ApproxTexture = 128, LodBillboard = 256;

    public static readonly string[] Names =
        { "EMISSIVE", "NIGHT_ONLY", "TINTED", "NO_TEXTURE", "PROP", "RESERVED_5", "INTERIOR", "APPROX_TEXTURE", "LOD_BILLBOARD" };
}

public sealed class ShaderInfo
{
    public int Id;
    public uint NameHash, FileHash;
    public string Name, File;       // "normal_spec", "normal_spec_alpha.sps"
    public byte Bucket;             // rage render bucket: 0 opaque, 1 alpha, 2 decal, 3 cutout, ...
    public VClass Class;
    public ushort Flags;
    public bool Skip;
    public string SkipReason = "";
    public bool Terrain, Trees, Tint2;   // Tint2: palette column comes from the second vertex colour
    public long Geometries, Triangles, OutTriangles;
    public double Area;                  // m2 in model space, summed over baked archetypes (not instances)
}

/// <summary>Classify GTA V shader presets (.sps) by name. Table is global and thread safe.</summary>
public sealed class ShaderTable
{
    readonly ConcurrentDictionary<(uint, uint, byte), ShaderInfo> _map = new();
    readonly object _gate = new();
    readonly List<ShaderInfo> _list = new();
    static readonly Dictionary<uint, string> KnownNames = new();

    public IReadOnlyList<ShaderInfo> All { get { lock (_gate) return _list.ToArray(); } }

    /// <summary>
    /// Shaders are discovered while baking in parallel, so they get their id afterwards, in name order, once
    /// per band of the export: ids are append-only and do not depend on thread timing.
    /// </summary>
    public void AssignIds()
    {
        lock (_gate)
        {
            int next = _list.Count(s => s.Id >= 0);
            foreach (var s in _list.Where(s => s.Id < 0).OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
                         .ThenBy(s => s.Bucket).ThenBy(s => s.FileHash).ThenBy(s => s.NameHash).ToArray())
                s.Id = next++;
            _list.Sort((a, b) => a.Id.CompareTo(b.Id));
        }
    }

    // Preset (.sps) names that are neither in CodeWalker's string index nor derivable from its shader list;
    // found by hashing candidate names against the hashes that occur in the map (jenkins one-at-a-time).
    static readonly string[] ExtraNames =
    {
        "cutout.sps", "alpha.sps", "cutout_tnt.sps", "cutout_um.sps", "cutout_spec_tnt.sps", "spec_const.sps", "normal_cutout_tnt.sps",
        "normal_cutout_um.sps", "normal_spec_cutout_tnt.sps", "cloth_spec_cutout.sps", "emissive_alpha_tnt.sps", "normal_tnt_pxm.sps",
        "normal_spec_tnt_pxm.sps",
    };

    /// <summary>Names that are not in CodeWalker's string index are recovered from its shader list file.</summary>
    public static void LoadNames(string xmlPath)
    {
        foreach (var n in ExtraNames) KnownNames[JenkHash.GenHash(n)] = n;
        if (!File.Exists(xmlPath)) return;
        foreach (var line in File.ReadLines(xmlPath))
        {
            foreach (var tag in new[] { "Name", "FileName" })
            {
                int a = line.IndexOf("<" + tag + ">", StringComparison.Ordinal);
                if (a < 0) continue;
                a += tag.Length + 2;
                int b = line.IndexOf("</" + tag + ">", a, StringComparison.Ordinal);
                if (b < 0) continue;
                var s = line.Substring(a, b - a).Trim().ToLowerInvariant();
                if (s.Length == 0) continue;
                KnownNames[JenkHash.GenHash(s)] = s;
                if (!s.EndsWith(".sps"))
                    foreach (var suffix in new[] { ".sps", "_alpha.sps", "_cutout.sps", "_screendooralpha.sps", "_decal.sps", "_tnt.sps", "_um.sps" })
                        KnownNames[JenkHash.GenHash(s + suffix)] = s + suffix;
            }
        }
    }

    static string NameOf(uint h)
    {
        if (h == 0) return "";
        var s = JenkIndex.TryGetString(h);
        if (!string.IsNullOrEmpty(s)) return s.ToLowerInvariant();
        return KnownNames.TryGetValue(h, out var k) ? k : "hash_" + h.ToString("x8");
    }

    public ShaderInfo Get(ShaderFX fx)
    {
        var key = (fx.FileName.Hash, fx.Name.Hash, fx.RenderBucket);
        if (_map.TryGetValue(key, out var si)) return si;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out si)) return si;
            si = Classify(fx);
            si.Id = -1;
            _list.Add(si);
            _map[key] = si;
            return si;
        }
    }

    static ShaderInfo Classify(ShaderFX fx)
    {
        var si = new ShaderInfo
        {
            NameHash = fx.Name.Hash, FileHash = fx.FileName.Hash, Name = NameOf(fx.Name.Hash), File = NameOf(fx.FileName.Hash), Bucket = fx.RenderBucket,
        };
        // the preset (.sps) name is the specific one; fall back to the base shader name when it is unknown
        string f = si.File.EndsWith(".sps") ? si.File.Substring(0, si.File.Length - 4) : si.Name;
        if (f.StartsWith("hash_")) f = si.Name;

        void Skip(string why) { si.Skip = true; si.SkipReason = why; }

        if (f.Contains("emissive")) si.Flags |= VFlags.Emissive;
        if (f.Contains("emissivenight")) si.Flags |= VFlags.NightOnly;
        if (f.EndsWith("_tnt") || f.Contains("_tnt_")) si.Flags |= VFlags.Tinted;

        if (f.StartsWith("terrain_cb")) { si.Class = VClass.Terrain; si.Terrain = true; }
        else if (f.StartsWith("water")) si.Class = VClass.Water;
        else if (f.StartsWith("trees"))
        {
            si.Trees = true;
            si.Class = VClass.Leaves;                       // refined to Wood per geometry when the texture is opaque
            if (f == "trees_shadow_proxy") Skip("shadow proxy");
            if (f.StartsWith("trees_lod")) si.Flags |= VFlags.LodBillboard;
            if (f is "trees_tnt" or "trees_normal_diffspec_tnt" or "trees_normal_spec_tnt") si.Tint2 = true;
        }
        else if (f.StartsWith("grass_fur")) si.Class = VClass.Grass;
        else if (f.StartsWith("grass")) { si.Class = VClass.Leaves; if (f.StartsWith("grass_batch")) Skip("instanced grass batch"); }
        else if (f.StartsWith("glass")) si.Class = VClass.Glass;
        else if (f == "cable") Skip("cable (hair-thin wires)");
        else if (f is "decal_normal_only" or "decal_spec_only" or "decal_shadow_only" or "decal_amb_only" or "mirror_decal" or "reflect_decal" or "spec_decal")
            Skip("decal without a colour layer (normal / spec / shadow / ambient only)");
        else if (f == "decal_dirt") Skip("dirt decal (darkening mask only)");
        else if (f.StartsWith("ptfx") || f.StartsWith("clouds") || f.StartsWith("sky_") || f is "radar" or "minimap" or "silhouettelayer" or "albedo_alpha")
            Skip("not world geometry");
        else if (f.Contains("decal") || f == "distance_map") si.Class = VClass.Decal;
        else if (f.StartsWith("cutout") || f.EndsWith("_cutout") || f.Contains("cutout_")) si.Class = VClass.Cutout;
        else
        {
            si.Class = si.Bucket switch { 1 => VClass.Alpha, 2 => VClass.Decal, 3 => VClass.Cutout, 6 => VClass.Water, _ => VClass.Opaque };
            if (f.EndsWith("_alpha") || f.EndsWith("_screendooralpha")) si.Class = si.Bucket == 3 ? VClass.Cutout : VClass.Alpha;
        }
        return si;
    }
}
