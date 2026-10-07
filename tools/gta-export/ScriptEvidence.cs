using System.Text.RegularExpressions;

namespace GtaExport;

/// <summary>
/// What the game's own scripts say about script-managed map data (IPL) names, read from the compiled scripts at
/// every run (nothing is hard-coded except the shape of two patterns):
///
///  - the building controller's state table: for each "building" it copies up to three IPL names into the
///    slots of states 0 (normal), 1 (destroyed) and 2 (cleanup). Every building starts in state 0 and missions
///    move it on (the UFO, the burnt farm, the sunk ship are all state 1), so a name in a state-0 slot is part of
///    the world at story start and a name that only appears in later slots is not;
///  - which scripts pass the name as a literal to the request / remove natives.
/// </summary>
public sealed class ScriptEvidence
{
    public bool TableOk;                    // the building state table was found
    public bool NativesOk;                  // request / remove natives were told apart
    public string Archive = "";
    public int Scripts, ScriptsDecoded;
    public string Note = "";
    public int TableFunc, TableSlotOffset;
    /// <summary>IPL name (lower case) -> (building id, state index) for every slot it is copied into.</summary>
    public Dictionary<string, List<(int building, int state)>> Table = new();
    public Dictionary<string, SortedSet<string>> RequestedBy = new(), RemovedBy = new(), MentionedBy = new();

    static readonly Regex SlotRx = new(@"^PUSH_(\d):0 LOCAL_U8_LOAD:0 IOFFSET_U8:(\d+) ARRAY_U8:(\d+)$", RegexOptions.Compiled);
    static readonly Regex CaseRx = new(@"^case (-?\d+) of switch@(\d+)$", RegexOptions.Compiled);

    public static ScriptEvidence Load(GameContext ctx, int threads)
    {
        var ev = new ScriptEvidence();
        try
        {
            ev.Archive = ScriptScan.FindScriptArchive(ctx) ?? "";
            if (ev.Archive.Length == 0) { ev.Note = "no script archive with building_controller.ysc found"; return ev; }
            var hits = ScriptScan.Collect(ctx, threads, ev.Archive, out var info);
            ev.Scripts = info.Count; ev.ScriptsDecoded = info.Count(i => i.ok);

            // 1. state table: the function of building_controller with the most "literal -> param0.slot[k]" copies
            var slots = new List<(int func, int building, int state, int off, string name)>();
            foreach (var h in hits)
            {
                if (h.Script != "building_controller" || h.Kind != "label") continue;
                var m = SlotRx.Match(h.Dest ?? ""); var c = CaseRx.Match(h.Ctx ?? "");
                if (!m.Success || !c.Success) continue;
                slots.Add((h.Func, int.Parse(c.Groups[1].Value), int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), h.Str.ToLowerInvariant()));
            }
            if (slots.Count > 0)
            {
                var best = slots.GroupBy(s => (s.func, s.off)).OrderByDescending(g => g.Count()).First();
                ev.TableFunc = best.Key.func; ev.TableSlotOffset = best.Key.off;
                foreach (var s in best)
                {
                    if (!ev.Table.TryGetValue(s.name, out var l)) ev.Table[s.name] = l = new List<(int, int)>();
                    if (!l.Contains((s.building, s.state))) l.Add((s.building, s.state));
                }
                // sanity: a real table has many buildings, and names both in state 0 and in later states
                int b = best.Select(s => s.building).Distinct().Count();
                ev.TableOk = b >= 40 && best.Any(s => s.state == 0) && best.Any(s => s.state == 1);
            }

            // 2. natives: the three natives that most often take an IPL name literal; "is active" returns a value.
            //    "if (!IS_ACTIVE(x)) REQUEST(x)" / "if (IS_ACTIVE(x)) REMOVE(x)" tells request from remove.
            var nat = hits.Where(h => h.Kind == "native" && h.Argc == 1).ToList();
            ulong isActive = nat.Where(h => h.Retc == 1).GroupBy(h => h.Native).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var cand = nat.Where(h => h.Retc == 0).GroupBy(h => h.Native).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToList();
            ulong request = 0, remove = 0;
            if (isActive != 0 && cand.Count == 2)
            {
                var votes = new Dictionary<ulong, (int req, int rem)> { [cand[0]] = (0, 0), [cand[1]] = (0, 0) };
                for (int i = 0; i + 1 < hits.Count; i++)
                {
                    var a = hits[i]; var b = hits[i + 1];
                    if (a.Kind != "native" || a.Native != isActive || b.Kind != "native" || !votes.ContainsKey(b.Native)) continue;
                    if (a.Script != b.Script || a.Str != b.Str || b.Off - a.Off > 24) continue;
                    var v = votes[b.Native];
                    if (a.Ctx.StartsWith("INOT JZ", StringComparison.Ordinal)) v.req++;
                    else if (a.Ctx.StartsWith("JZ", StringComparison.Ordinal)) v.rem++;
                    votes[b.Native] = v;
                }
                var v0 = votes[cand[0]]; var v1 = votes[cand[1]];
                if (v0.req > 4 * Math.Max(1, v0.rem) && v1.rem > 4 * Math.Max(1, v1.req)) { request = cand[0]; remove = cand[1]; }
                else if (v1.req > 4 * Math.Max(1, v1.rem) && v0.rem > 4 * Math.Max(1, v0.req)) { request = cand[1]; remove = cand[0]; }
                ev.Note = $"request/remove votes: {cand[0]:x16} req {v0.req} rem {v0.rem}; {cand[1]:x16} req {v1.req} rem {v1.rem}";
            }
            ev.NativesOk = request != 0;

            foreach (var h in hits)
            {
                var name = h.Str.ToLowerInvariant();
                Add(ev.MentionedBy, name, h.Script);
                if (h.Kind != "native" || !ev.NativesOk) continue;
                if (h.Native == request) Add(ev.RequestedBy, name, h.Script);
                else if (h.Native == remove) Add(ev.RemovedBy, name, h.Script);
            }
        }
        catch (Exception ex)
        {
            ev.TableOk = false; ev.NativesOk = false;
            ev.Note = "script evidence failed: " + ex.GetType().Name + ": " + ex.Message;
        }
        return ev;
    }

    static void Add(Dictionary<string, SortedSet<string>> d, string name, string script)
    {
        if (!d.TryGetValue(name, out var s)) d[name] = s = new SortedSet<string>(StringComparer.Ordinal);
        s.Add(script);
    }

    public string Summary() => TableOk
        ? $"script evidence: {ScriptsDecoded}/{Scripts} scripts decoded from {Archive}; building state table with {Table.Count} IPL names ({Table.Values.SelectMany(v => v).Select(v => v.building).Distinct().Count()} buildings); request/remove natives {(NativesOk ? "identified" : "NOT identified")}; {MentionedBy.Count} IPL names used by scripts"
        : $"script evidence: NOT available ({Note}); falling back to the built-in rule table";

    /// <summary>"script a, script b ... (+n)" for reasons.</summary>
    public static string Short(IEnumerable<string> scripts, int max = 3)
    {
        var l = scripts.ToList();
        return string.Join(", ", l.Take(max)) + (l.Count > max ? $" (+{l.Count - max})" : "");
    }
}
