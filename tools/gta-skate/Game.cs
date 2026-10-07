using System.Collections.Concurrent;
using System.Text.Json;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaSkate;

/// <summary>
/// The user's GTA V install opened read-only through CodeWalker.Core, the same way tools/gta-export does
/// (DLC on, mods off), plus archetypes (.ytyp), which the visual pipeline needs to go from a placed
/// entity to its drawable and texture dictionary.
/// </summary>
public sealed class Game
{
    public string GameDir;
    public GameFileCache Gfc;
    public RpfManager Rpf => Gfc.RpfMan;
    public readonly List<string> Errors = new();
    public double InitSeconds;

    public static Game Open(string gameDir)
    {
        if (!File.Exists(Path.Combine(gameDir, "GTA5.exe")))
            throw new FileNotFoundException("GTA5.exe not found in " + gameDir);

        double t0 = Log.Seconds;
        var g = new Game { GameDir = gameDir };

        // Keys are derived in memory from the user's own GTA5.exe on every run; never written, never logged.
        Log.Info("deriving archive keys from GTA5.exe (in memory only)...");
        GTA5Keys.LoadFromPath(gameDir, false, null);
        if (GTA5Keys.PC_AES_KEY == null || GTA5Keys.PC_NG_KEYS == null)
            throw new InvalidOperationException("could not derive archive keys from GTA5.exe (unsupported build?)");

        Log.Info("scanning RPF archives and archetypes (read-only)...");
        var gfc = new GameFileCache(1L << 30, 10.0, gameDir, false, "", false, "Installers;_CommonRedist");
        gfc.EnableDlc = true;
        gfc.EnableMods = false;
        gfc.LoadArchetypes = true;          // .ytyp: entity -> drawable / texture dictionary
        gfc.LoadVehicles = false;
        gfc.LoadPeds = false;
        gfc.LoadAudio = false;
        gfc.BuildExtendedJenkIndex = false;
        gfc.Init(_ => { }, e => { lock (g.Errors) g.Errors.Add(e.Split('\n')[0]); });
        g.Gfc = gfc;
        g.InitSeconds = Log.Seconds - t0;
        Log.Info($"game opened in {g.InitSeconds:F1}s: {gfc.AllRpfs.Count} rpfs, {gfc.DlcNameList.Count} dlc packs, " +
                 $"{gfc.YmapDict.Count} active ymap, {gfc.YtypDict.Count} ytyp, {gfc.YdrDict.Count} ydr, {gfc.YddDict.Count} ydd, " +
                 $"{gfc.YftDict.Count} yft, {gfc.YtdDict.Count} ytd, {g.Errors.Count} init errors");
        return g;
    }

    public T Load<T>(RpfFileEntry e) where T : class, PackedFile, new()
    {
        if (e == null) return null;
        return Rpf.GetFile<T>(e);
    }

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
}

/// <summary>
/// Which script-managed ymaps count as "on" at story start. Non-scripted ymaps are always streamed by the game.
/// For scripted ones the default lives in game scripts, which nobody here reads, so the decision is the same
/// hand-written rule table X1 uses for collision (read back from work/export/collision/index.json when it
/// exists so both exports agree), with name rules as the fallback.
/// </summary>
public sealed class MapStateRules
{
    readonly Dictionary<string, bool> _groups = new(StringComparer.OrdinalIgnoreCase);
    public int GroupsLoaded => _groups.Count;

    public static MapStateRules Load(string collisionExportDir)
    {
        var r = new MapStateRules();
        var p = Path.Combine(collisionExportDir, "collision", "index.json");
        if (!File.Exists(p)) return r;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            if (doc.RootElement.TryGetProperty("map_groups", out var mg))
                foreach (var g in mg.EnumerateArray())
                    r._groups[g.GetProperty("name").GetString()] = g.GetProperty("default_on").GetBoolean();
        }
        catch (Exception ex) { Log.Warn("could not read map group defaults from " + p + ": " + ex.Message); }
        return r;
    }

    static readonly string[] Suffixes = { "_lod", "_slod", "_slod2", "_strm", "_long", "_critical", "_occl", "_lodlights", "_distantlights" };

    /// <summary>Strip streaming suffixes like _strm_3, _long_0, _lod so a child ymap maps to its group name.</summary>
    public static string BaseName(string name)
    {
        var n = name;
        for (int guard = 0; guard < 4; guard++)
        {
            int us = n.LastIndexOf('_');
            if (us > 0 && us < n.Length - 1 && n.AsSpan(us + 1).IndexOfAnyExceptInRange('0', '9') < 0) n = n.Substring(0, us);
            bool cut = false;
            foreach (var s in Suffixes)
                if (n.EndsWith(s, StringComparison.Ordinal)) { n = n.Substring(0, n.Length - s.Length); cut = true; break; }
            if (!cut) break;
        }
        return n;
    }

    /// <summary>True when the ymap (or the group its name derives from) is a map group the collision export has switched on.</summary>
    public bool TableOn(string name)
    {
        if (_groups.TryGetValue(name, out var on)) return on;
        var b = BaseName(name);
        return b != name && _groups.TryGetValue(b, out on) && on;
    }

    public (bool on, string why) ScriptedDefault(string name, string dlc)
    {
        if (_groups.TryGetValue(name, out var on)) return (on, "collision group table");
        var b = BaseName(name);
        if (b != name && _groups.TryGetValue(b, out on)) return (on, "collision group table (base name " + b + ")");
        if (name.StartsWith("prologue", StringComparison.Ordinal)) return (false, "North Yankton");
        if (b.EndsWith("_original", StringComparison.Ordinal)) return (true, "original scenery split out by a DLC patch");
        if (b.EndsWith("_shared", StringComparison.Ordinal)) return (true, "scenery shared by original and DLC variant");
        return (false, dlc.Length > 0 ? "DLC scripted content" : "unknown scripted ymap");
    }
}
