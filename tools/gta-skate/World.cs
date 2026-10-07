using System.Collections.Concurrent;
using CodeWalker.GameFiles;
using SharpDX;

namespace GtaSkate;

/// <summary>One active .ymap (after DLC/patch overlay) with the facts the pipeline needs.</summary>
public sealed class YmapInfo
{
    public int Id;                  // index in World.Ymaps (sorted by name) = ymap id in the output tables
    public uint Hash;
    public string Name;
    public RpfFileEntry Entry;
    public string Dlc;              // "" = base game
    public uint Flags;              // CMapData.flags: bit 0 script managed, bit 1 LOD in parent
    public uint ContentFlags;       // 1 HD, 2 LOD, 4 SLOD2+, 8 interior, 16 SLOD, 32 occlusion, 64 physics, 128 lod lights, 256 distant lights, 512 critical, 1024 grass
    public uint ParentHash;
    public Vector3 EMin, EMax, SMin, SMax;
    public bool Scripted;
    public bool On;                 // part of the story-start world
    public string Why = "";
    public bool LoadFailed;
    public bool Hidden;             // not in CodeWalker's active map set: its archive was replaced by a DLC version without this file
    public int[] LodCounts = new int[7];        // entities per rage__eLodType
    public int[] LodLeafCounts = new int[7];    // ... of those, entities without children
    public int Entities, MloInstances, GrassBatches, PropBatches, CarGens;
    public long GrassInstances;
    public YmapFile File;           // kept only when the caller asked for it (export)
}

public sealed class World
{
    public Game Game;
    public List<YmapInfo> Ymaps = new();
    public Dictionary<uint, YmapInfo> ByHash = new();
    public MapStateRules Rules;

    /// <summary>
    /// Load every active ymap (parallel), classify it and optionally keep the parsed file when its entity
    /// extents touch <paramref name="keep"/>.
    /// </summary>
    public static World Load(Game game, MapStateRules rules, int threads, BoundingBox? keep, bool keepGrass = false)
    {
        double t0 = Log.Seconds;
        var w = new World { Game = game, Rules = rules };
        // CodeWalker applies every DLC map change set by swapping whole archives. Some script-managed ymaps of the
        // base game (the docked cargo ship, the O'Neil farm, the closed lobby shells ...) only exist in the base
        // *_metadata.rpf that a DLC replaced, so they are missing from its active set, while their collision is
        // still active. They are taken from the full file list when the map group table has them switched on.
        var hidden = new HashSet<uint>();
        var all = new List<KeyValuePair<uint, RpfFileEntry>>(game.Gfc.YmapDict);
        foreach (var kv in game.Gfc.AllYmapsDict)
        {
            if (game.Gfc.YmapDict.ContainsKey(kv.Key)) continue;
            var n = kv.Value.NameLower.EndsWith(".ymap") ? kv.Value.NameLower.Substring(0, kv.Value.NameLower.Length - 5) : kv.Value.NameLower;
            if (!rules.TableOn(n)) continue;
            hidden.Add(kv.Key);
            all.Add(kv);
        }
        var entries = all.OrderBy(kv => kv.Value.NameLower, StringComparer.Ordinal).ThenBy(kv => kv.Key).ToArray();
        var infos = new YmapInfo[entries.Length];
        Parallel.For(0, entries.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var e = entries[i].Value;
            var name = e.NameLower.EndsWith(".ymap") ? e.NameLower.Substring(0, e.NameLower.Length - 5) : e.NameLower;
            var info = new YmapInfo { Hash = entries[i].Key, Name = name, Entry = e, Dlc = Game.DlcOf(e.Path), Hidden = hidden.Contains(entries[i].Key) };
            infos[i] = info;
            YmapFile y = null;
            try { y = game.Load<YmapFile>(e); } catch (Exception) { }
            if (y == null || y.Meta == null) { info.LoadFailed = true; return; }   // PSO/RBF ymaps do not occur in the PC map data
            var md = y._CMapData;
            info.Flags = md.flags; info.ContentFlags = md.contentFlags; info.ParentHash = md.parent;
            info.EMin = md.entitiesExtentsMin; info.EMax = md.entitiesExtentsMax;
            info.SMin = md.streamingExtentsMin; info.SMax = md.streamingExtentsMax;
            info.Scripted = (md.flags & 1) != 0;
            if (y.AllEntities != null)
            {
                info.Entities = y.AllEntities.Length;
                foreach (var ent in y.AllEntities)
                {
                    if (ent.IsMlo) { info.MloInstances++; continue; }
                    int l = (int)ent._CEntityDef.lodLevel;
                    if ((uint)l < 7)
                    {
                        info.LodCounts[l]++;
                        if (ent._CEntityDef.numChildren == 0) info.LodLeafCounts[l]++;
                    }
                }
            }
            if (y.GrassInstanceBatches != null)
            {
                info.GrassBatches = y.GrassInstanceBatches.Length;
                foreach (var b in y.GrassInstanceBatches) info.GrassInstances += b.Instances?.Length ?? 0;
            }
            info.PropBatches = y.PropInstanceBatches?.Length ?? 0;
            info.CarGens = y.CarGenerators?.Length ?? 0;
            if (keep.HasValue)
            {
                var k = keep.Value;
                bool touch = info.EMin.X <= k.Maximum.X && info.EMax.X >= k.Minimum.X && info.EMin.Y <= k.Maximum.Y && info.EMax.Y >= k.Minimum.Y;
                if (touch) info.File = y;
            }
            if (keepGrass && info.GrassBatches > 0) info.File = y;
        });
        w.Ymaps.AddRange(infos);
        for (int i = 0; i < infos.Length; i++) { infos[i].Id = i; w.ByHash[infos[i].Hash] = infos[i]; }

        // State: a non-scripted ymap is always streamed. A scripted one follows the rule table. A child inherits
        // "off" from a scripted parent that is off (its LOD parent would never be there).
        foreach (var y in infos)
        {
            if (y.LoadFailed) { y.On = false; y.Why = "load failed"; continue; }
            if (y.Hidden) { y.On = true; y.Why = "map group switched on; ymap taken from a replaced archive"; continue; }
            if (!y.Scripted) { y.On = true; y.Why = "static"; continue; }
            var (on, why) = rules.ScriptedDefault(y.Name, y.Dlc);
            y.On = on; y.Why = why;
        }
        foreach (var y in infos)
        {
            if (!y.On) continue;
            uint p = y.ParentHash; int guard = 0;
            while (p != 0 && guard++ < 8 && w.ByHash.TryGetValue(p, out var pi))
            {
                if (!pi.On) { y.On = false; y.Why = "parent " + pi.Name + " off"; break; }
                p = pi.ParentHash;
            }
        }
        Log.Info($"ymaps: {infos.Length} loaded in {Log.Seconds - t0:F1}s ({hidden.Count} from replaced archives), {infos.Count(y => y.LoadFailed)} failed, " +
                 $"{infos.Count(y => y.Scripted)} scripted, {infos.Count(y => y.On)} on, {infos.Count(y => y.File != null)} kept");
        return w;
    }

    public static string ContentFlagNames(uint f)
    {
        string[] n = { "HD", "LOD", "SLOD2", "INTERIOR", "SLOD", "OCCLUSION", "PHYSICS", "LOD_LIGHTS", "DISTANT_LIGHTS", "CRITICAL", "GRASS" };
        var parts = new List<string>();
        for (int i = 0; i < n.Length; i++) if ((f & (1u << i)) != 0) parts.Add(n[i]);
        if ((f >> n.Length) != 0) parts.Add("0x" + (f & ~((1u << n.Length) - 1)).ToString("x"));
        return parts.Count == 0 ? "NONE" : string.Join("|", parts);
    }
}
