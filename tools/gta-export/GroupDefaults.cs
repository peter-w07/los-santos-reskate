using CodeWalker.GameFiles;

namespace GtaExport;

/// <summary>
/// The state of script-managed map data groups (IPL groups) at the single-player story start, at a given hour
/// in clear weather. Decided from game data wherever the game says something (see <see cref="ScriptEvidence"/>):
///
///  1. time/weather dependent groups: the manifest's hours mask and weather list (the engine toggles these itself);
///  2. the building controller's state table: a name in a state-0 slot is on, a name only in later slots is off;
///  3. a script-managed group that no script ever names cannot be loaded: off;
///  4. a group that scripts only request by literal (missions, multiplayer content): off;
///  and a small hand-written table for what is left (multiplayer content of the online profile) or when the
///  scripts could not be read.
/// Consumers may override everything: index.json keeps group, state, confidence and the reason.
/// </summary>
public static class GroupDefaults
{
    /// <summary>Hour of day (0-23) the time-dependent groups are evaluated at. The world is built for a frozen noon.</summary>
    public static int Hour = 12;

    // Fallback / remainder table. Story-mode names repeat what the building state table says on game build
    // 1.0.3889 so that a future build whose scripts cannot be decoded still gets the same answers.
    static readonly Dictionary<string, (bool on, string conf, string why)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bh1_16_doors_shut"] = (true, "medium", "Rockford Plaza doors, shut until the refurb state"),
        ["bh1_16_refurb"] = (false, "medium", "later story state"),
        ["bh1_47_joshhse_unburnt"] = (true, "high", "Josh's house before it burns"),
        ["bh1_47_joshhse_burnt"] = (false, "high", "later story state"),
        ["bnkheist_apt_norm"] = (true, "high", "Paleto bank block before the heist"),
        ["bnkheist_apt_dest"] = (false, "high", "later story state"),
        ["canyonriver01"] = (true, "medium", "river under the rail bridge before the derailment"),
        ["canyonriver01_traincrash"] = (false, "medium", "later story state"),
        ["canyonrvrshallow"] = (true, "medium", "normal river level"),
        ["canyonrvrdeep"] = (false, "medium", "alternate river level"),
        ["cargoship"] = (true, "high", "docked cargo ship present until it is sunk"),
        ["sunkcargoship"] = (false, "high", "later story state"),
        ["carwash_with_spinners"] = (true, "medium", "car wash, normal state"),
        ["carwash_without_spinners"] = (false, "medium", "alternate state"),
        ["ch1_02_closed"] = (true, "medium", "normal state"),
        ["ch1_02_open"] = (false, "medium", "mission state"),
        ["chemgrill_grp1"] = (true, "medium", "default prop state"),
        ["chop_props"] = (false, "medium", "appears once Chop is available"),
        ["chophillskennel"] = (false, "medium", "appears after Franklin moves house"),
        ["coronertrash"] = (false, "medium", "appears after a mission"),
        ["crashed_cargoplane"] = (false, "high", "wreck appears after a mission"),
        ["cs1_02_cf_offmission"] = (true, "medium", "Cluckin' Bell factory closed (off mission)"),
        ["cs3_05_water_grp1"] = (true, "medium", "default water state"),
        ["cs3_05_water_grp2"] = (false, "medium", "alternate water state"),
        ["cs3_07_mpgates"] = (false, "medium", "multiplayer / mission gates"),
        ["cs5_4_trains"] = (true, "medium", "parked train, normal state"),
        ["des_farmhs_startimap"] = (true, "high", "farmhouse before destruction"),
        ["des_farmhs_endimap"] = (false, "high", "later story state"),
        ["des_protree_start"] = (false, "high", "North Yankton, prologue only"),
        ["des_protree_end"] = (false, "high", "North Yankton, prologue only"),
        ["des_smash2_startimap"] = (true, "high", "before destruction"),
        ["des_smash2_endimap"] = (false, "high", "later story state"),
        ["des_stilthouse_imapstart"] = (true, "high", "stilt house before it is pulled down"),
        ["des_stilthouse_imapend"] = (false, "high", "later story state"),
        ["des_stilthouse_rebuild"] = (false, "high", "later story state"),
        ["dockcrane1"] = (true, "medium", "dock crane, normal state"),
        ["dt1_03_gr_closed"] = (true, "medium", "ground plate over the construction shaft, closed until the heist"),
        ["dt1_03_shutter"] = (true, "medium", "car park shutter, closed"),
        ["dt1_05_hc_req"] = (false, "medium", "helicopter crash damage, later story state"),
        ["dt1_05_hc_end"] = (false, "medium", "later story state"),
        ["dt1_05_rubble"] = (false, "medium", "later story state"),
        ["dt1_05_woffm"] = (true, "medium", "FIB tower windows, off-mission state"),
        ["facelobbyfake"] = (true, "high", "closed Lifeinvader lobby shell"),
        ["fakeint"] = (false, "medium", "closed car showroom, later story state"),
        ["fiblobbyfake"] = (true, "high", "closed FIB lobby shell"),
        ["farm"] = (true, "high", "O'Neil farm before it burns"),
        ["farmint_cap"] = (true, "medium", "farm interior cap"),
        ["farm_burnt"] = (false, "high", "later story state"),
        ["fbi_colplug"] = (true, "medium", "FIB tower collision plug"),
        ["fbi_repair"] = (false, "medium", "FIB tower repaired after the raid, later story state"),
        ["gasstation_ipl_group1"] = (true, "medium", "gas station before the explosion"),
        ["gasstation_ipl_group2"] = (false, "medium", "later story state"),
        ["id2_14_pre_no_int"] = (true, "medium", "Lester's factory before the fire"),
        ["id2_14_post_no_int"] = (false, "medium", "later story state"),
        ["jewel2fake"] = (true, "high", "closed jewellery store shell"),
        ["koriztempwalls"] = (false, "medium", "mission state"),
        ["kt_carwash"] = (true, "medium", "car wash, normal state"),
        ["kt_carwash_nobrush"] = (false, "medium", "alternate state"),
        ["methtrailer_grp1"] = (true, "medium", "trailer before the explosion"),
        ["methtrailer_grp2"] = (false, "medium", "later story state"),
        ["methtrailer_grp3"] = (false, "medium", "later story state"),
        ["mic3_chopper_debris"] = (false, "medium", "mission state"),
        ["pcranecont"] = (false, "medium", "mission prop (Scouting the Port)"),
        ["plane_crash_trench"] = (false, "medium", "appears after a mission"),
        ["racetrack01"] = (false, "medium", "only while the race is on"),
        ["railing_start"] = (true, "medium", "bridge railing before the derailment"),
        ["railing_end"] = (false, "medium", "later story state"),
        ["rc12b_default"] = (true, "high", "hospital default state"),
        ["rc12b_fixed"] = (false, "high", "later story state"),
        ["sc1_27_cut"] = (false, "low", "cutscene variant"),
        ["scafstartimap"] = (true, "high", "construction site before the mission"),
        ["scafendimap"] = (false, "high", "later story state"),
        ["smboat"] = (true, "medium", "yacht anchored off Del Perro in story mode"),
        ["sp1_10_fake_interior"] = (true, "high", "closed stadium shell"),
        ["talklaugh_pipe"] = (false, "low", "mission prop"),
        ["tankercrash_grp1"] = (false, "low", "mission state"),
        ["tankercrash_grp2"] = (false, "low", "mission state"),
        ["tankerexp_grp0"] = (true, "low", "state before the tanker explosion"),
        ["tankerexp_grp1"] = (false, "low", "mission state"),
        ["tankerexp_grp3"] = (false, "low", "mission state"),
        ["trailerparka_grp1"] = (true, "medium", "trailer park before it is destroyed"),
        ["trailerparkb_grp1"] = (true, "medium", "trailer park before it is destroyed"),
        ["trailerparkc_grp1"] = (true, "medium", "trailer park before it is destroyed"),
        ["trailerparkd_grp1"] = (true, "medium", "trailer park before it is destroyed"),
        ["trailerparke_grp1"] = (true, "medium", "trailer park before it is destroyed"),
        ["trailerparka_grp2"] = (false, "medium", "later story state"),
        ["trailerparkb_grp2"] = (false, "medium", "later story state"),
        ["trailerparkc_grp2"] = (false, "medium", "later story state"),
        ["trailerparkd_grp2"] = (false, "medium", "later story state"),
        ["trailerparke_grp2"] = (false, "medium", "later story state"),
        ["ufo"] = (false, "high", "easter egg, only after 100% completion"),
        // interiors (script-managed MLO ymaps; used by the interior layer)
        ["v_tunnel_hole_swap"] = (true, "medium", "construction shaft tunnel, normal state"),
        ["v_tunnel_hole"] = (false, "medium", "construction shaft tunnel with the heist hole, later story state"),
        ["shr_int"] = (true, "medium", "car showroom, open at story start"),
        ["trevorstrailer"] = (true, "medium", "Trevor's trailer, state before his first mission"),
        ["trevorstrailertrash"] = (false, "medium", "later story state"),
        ["trevorstrailertidy"] = (false, "medium", "later story state"),
        ["rc12b_hospitalinterior"] = (true, "medium", "hospital interior, normal state"),
        ["coroner_int_off"] = (true, "medium", "coroner's office closed"),
        ["coroner_int_on"] = (false, "medium", "mission interior"),
        ["post_hiest_unload"] = (false, "medium", "jewellery store interior, heist state (jewel2fake shell is the default)"),
        ["refit_unload"] = (false, "medium", "later story state"),
        ["facelobby"] = (false, "medium", "mission interior (facelobbyfake shell is the default)"),
        ["fiblobby"] = (false, "medium", "mission interior (fiblobbyfake shell is the default)"),
        ["finbank"] = (false, "medium", "mission interior"),
        ["farmint"] = (false, "medium", "mission interior (farmint_cap is the default)"),
        ["id2_14_during1"] = (false, "medium", "mission state"),
        ["id2_14_during2"] = (false, "medium", "mission state"),
        // DLC groups that belong to the always-visible exterior of a base map building (online profile)
        ["hei_dlc_windows_casino"] = (true, "medium", "casino exterior"),
        ["hei_dlc_casino_door"] = (true, "medium", "casino exterior"),
        ["hei_dlc_vw_roofdoors_locked"] = (true, "medium", "casino exterior"),
    };

    static readonly uint WeatherClear = JenkHash.GenHash("clear"), WeatherExtraSunny = JenkHash.GenHash("extrasunny");

    public readonly record struct Decision(bool On, string Confidence, string Reason, string Source);

    /// <summary>
    /// <paramref name="hours"/> / <paramref name="weather"/>: the manifest's time-of-day mask (bit h = active during hour h)
    /// and weather type hashes, 0 / empty for groups that are not time dependent.
    /// </summary>
    public static Decision Decide(string name, string dlc, uint hours, uint[] weather, ScriptEvidence ev)
    {
        name = name.ToLowerInvariant();
        if (name.StartsWith("prologue", StringComparison.Ordinal))
            return new(false, "high", "North Yankton, prologue only", "rule");

        if (hours != 0)
        {
            bool hourOn = ((hours >> Hour) & 1) != 0;
            bool weatherOn = weather == null || weather.Length == 0 || weather.Contains(WeatherClear) || weather.Contains(WeatherExtraSunny);
            return new(hourOn && weatherOn, "high",
                $"time-dependent group: manifest hours mask 0x{hours:X6} is {(hourOn ? "on" : "off")} at {Hour}:00" + (weather != null && weather.Length > 0 ? $", weather list {(weatherOn ? "includes" : "excludes")} clear" : ""),
                "manifest");
        }

        if (ev != null && ev.TableOk && ev.Table.TryGetValue(name, out var slots))
        {
            var zero = slots.Where(s => s.state == 0).ToList();
            if (zero.Count > 0)
                return new(true, "high", $"building controller state table: state 0 (normal) of building {zero[0].building}; every building starts in state 0", "script_table");
            var s1 = slots.OrderBy(s => s.state).First();
            return new(false, "high", $"building controller state table: only state {s1.state} of building {s1.building}; story start is state 0", "script_table");
        }

        if (name.EndsWith("_original", StringComparison.Ordinal)) return new(true, "high", "original scenery that a DLC patch split out of the base map", "rule");
        if (name.EndsWith("_shared", StringComparison.Ordinal)) return new(true, "medium", "scenery shared by the original and the DLC variant", "rule");

        bool haveScripts = ev != null && ev.TableOk;
        if (haveScripts && !ev.MentionedBy.ContainsKey(name))
            return new(false, "medium", "script managed, and no game script names it: it is never requested", "script_absent");

        if (Table.TryGetValue(name, out var t) && (!haveScripts || dlc.Length > 0))
            return new(t.on, t.conf, t.why + (haveScripts ? "" : " (built-in table; scripts not read)"), "table");

        if (haveScripts && ev.NativesOk && ev.RequestedBy.TryGetValue(name, out var req))
            return new(false, "medium", $"only on request by: {ScriptEvidence.Short(req)}" + (dlc.Length > 0 ? " (multiplayer / DLC content)" : " (mission or event)"), "script_request");
        if (haveScripts && ev.MentionedBy.TryGetValue(name, out var men))
            return new(false, "low", $"named by {ScriptEvidence.Short(men)} but never requested with a literal", "script_other");

        if (name.Contains("legacy_fixes") || name.Contains("legacyfixes") || name.Contains("collision_fixes") || name.Contains("colfix"))
            return new(false, "low", "DLC collision fix patch, kept off (it overlays base geometry)", "rule");
        if (dlc.Length > 0) return new(false, "medium", "multiplayer / DLC scripted content", "rule");
        return new(false, "low", "unknown script-managed group", "rule");
    }
}
