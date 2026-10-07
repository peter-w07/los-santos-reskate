using System.Text.Json;

namespace GtaExport;

public static class Program
{
    const string Usage = """
        gta-export <command> [options]      (GTA V install is only ever read)

        commands
          all         materials + water + collision (meshes + tiles + index) + preview
          materials   materials.dat / materialfx.dat / procedural.meta -> materials.json, procedural.json
          water       water.xml, waterheight.dat, heightmap.dat -> water/
          meshes      phase 1: every static world .ybn -> collision/ybn/*.lsct + collision/sources.json
          tiles       phase 2: meshes -> collision/tiles/*.lsct + collision/index.json (no game access)
          collision   meshes + tiles
          preview     top-down PNGs rendered from the exported tiles -> preview/
          info        print how the active .ybn files are classified
          interiors   every placed interior (MLO) instance -> interiors/ (world-space tiles, index, portals, tracks)
                      --list [--only TEXT] prints the instance table without writing anything
          interiors-preview   top-down and cross-section PNGs of the interior layer -> interiors/preview/ (no game access)
                      --region x0,y0,x1,y1 --mpp M --name N [--buildings] [--detached]      one plan view
                      --nodes a,b [--track trains4] --name N [--hmpp 1 --vmpp 0.5 --z z0,z1 --halo 45]   one section along a track
          check-static  read-only: do the static tiles on disk match what the current tiler would write?
          probe       development helper: --what ytyp|ymap|ls|find|mlo|rpfs [--names a,b]

        options
          --game DIR      GTA V folder (default: Steam install)
          --out DIR       output folder (default: work/export)
          --threads N     worker threads (default: logical cores)
          --force         rebuild files even if they are up to date
          --margin M      tile overlap margin in metres (default 1)
          --hour H        hour of day for the time-dependent map groups (default 12; the world is built for a frozen noon)
          --no-scripts    do not read the game scripts; group defaults then come from the built-in table only
          --no-story-groups   profile online: do not add the story-mode map groups that the multiplayer map unloads
          --profile P     which map: online (default; multiplayer map = all DLC map change sets), story (single-player map:
                          GROUP_MAP change sets not applied), codewalker (CodeWalker's own active set, as before MapSet)
          --only TEXT     meshes: only ybn whose name contains TEXT (debugging)
          preview: --name N --region x0,y0,x1,y1 --mpp M [--mode default|all|ungrouped] [--auto-ramp] [--holes] [--dump-z]
                   [--no-foliage] [--no-water] [--exclude p1,p2]
                   without --region the standard set is rendered (whole map 4 m/px, downtown 1 m/px)
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        var cmd = args[0].ToLowerInvariant();
        var o = new Opts(args.Skip(1));
        var game = o.Str("game", Paths.DefaultGame);
        var outDir = Path.GetFullPath(o.Str("out", Paths.DefaultOut));
        int threads = Math.Max(1, o.Int("threads", Environment.ProcessorCount));
        bool force = o.Flag("force");
        float margin = (float)o.Num("margin", 1.0);
        GameContext.DefaultProfile = o.Str("profile", "online");
        GameContext.UseScripts = !o.Flag("no-scripts");
        GameContext.IncludeStoryGroups = !o.Flag("no-story-groups");
        GroupDefaults.Hour = Math.Clamp(o.Int("hour", 12), 0, 23);

        // safety: never write inside the game folder
        var gameFull = Path.GetFullPath(game).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if ((outDir + Path.DirectorySeparatorChar).StartsWith(gameFull, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("refusing to write output inside the game folder");
            return 2;
        }
        Directory.CreateDirectory(outDir);

        try
        {
            double t0 = Log.Seconds;
            switch (cmd)
            {
                case "all":
                {
                    var ctx = GameContext.Open(game);
                    MaterialsExport.Run(ctx, outDir);
                    WaterExport.Run(ctx, outDir);
                    CollisionExport.Run(ctx, outDir, threads, force, o.Str("only"));
                    ctx = null;
                    GC.Collect();
                    Tiler.Run(outDir, threads, force, margin);
                    StandardPreviews(outDir, threads);
                    break;
                }
                case "materials": MaterialsExport.Run(GameContext.Open(game), outDir); break;
                case "water": WaterExport.Run(GameContext.Open(game), outDir); break;
                case "meshes": CollisionExport.Run(GameContext.Open(game), outDir, threads, force, o.Str("only")); break;
                case "tiles": Tiler.Run(outDir, threads, force, margin); break;
                case "collision":
                    CollisionExport.Run(GameContext.Open(game), outDir, threads, force, o.Str("only"));
                    GC.Collect();
                    Tiler.Run(outDir, threads, force, margin);
                    break;
                case "preview":
                {
                    var region = o.Nums("region", 4);
                    if (region == null) StandardPreviews(outDir, threads);
                    else
                    {
                        Preview.Render(outDir, new PreviewOptions
                        {
                            Name = o.Str("name", "custom"),
                            X0 = region[0], Y0 = region[1], X1 = region[2], Y1 = region[3],
                            Mpp = o.Num("mpp", 1),
                            Mode = o.Str("mode", "default"),
                            AutoRamp = o.Flag("auto-ramp"),
                            Holes = o.Flag("holes"),
                            DumpZ = o.Flag("dump-z"),
                            Foliage = !o.Flag("no-foliage"),
                            Water = !o.Flag("no-water"),
                            ExcludeNamePrefixes = o.Str("exclude", "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                        }, threads);
                    }
                    break;
                }
                case "info": Info(GameContext.Open(game)); break;
                case "probe": Probe.Run(GameContext.Open(game), o); break;
                case "check-static": return Probe.TileSignatures(outDir, margin);
                case "audit": StateAudit.Run(GameContext.Open(game), o, outDir, threads); break;
                case "interiors-preview":
                {
                    var region = o.Nums("region", 4);
                    var nodes = o.Nums("nodes", 2);
                    if (nodes != null)
                    {
                        var zr = o.Nums("z", 2) ?? new[] { -30.0, 110.0 };
                        InteriorsPreview.RenderSection(outDir, new InteriorsPreview.SectionOptions
                        {
                            Name = o.Str("name", "section"), Track = o.Str("track", "trains4"), FirstNode = (int)nodes[0], LastNode = (int)nodes[1],
                            Hmpp = o.Num("hmpp", 1), Vmpp = o.Num("vmpp", 0.5), Z0 = zr[0], Z1 = zr[1], Halo = o.Num("halo", 45),
                        }, threads);
                    }
                    else if (region != null)
                    {
                        InteriorsPreview.RenderPlan(outDir, new InteriorsPreview.PlanOptions
                        {
                            Name = o.Str("name", "plan"), X0 = region[0], Y0 = region[1], X1 = region[2], Y1 = region[3], Mpp = o.Num("mpp", 1),
                            Buildings = o.Flag("buildings"), Detached = o.Flag("detached"), Labels = !o.Flag("no-labels"),
                        }, threads);
                    }
                    else StandardInteriorPreviews(outDir, threads);
                    break;
                }
                case "interiors":
                    InteriorsExport.Run(GameContext.Open(game), outDir, threads, force, margin, o.Flag("list"), o.Str("only"));
                    break;
                default:
                    Console.Error.WriteLine("unknown command: " + cmd);
                    Console.WriteLine(Usage);
                    return 2;
            }
            Log.Info($"{cmd}: finished in {Log.Seconds - t0:F1}s");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Info("FAILED: " + ex);
            return 1;
        }
    }

    /// <summary>The verification renders the task asks for: whole map at 4 m/px and downtown Los Santos at 1 m/px.</summary>
    static void StandardPreviews(string outDir, int threads)
    {
        Preview.Render(outDir, new PreviewOptions { Name = "wholemap_4m", X0 = -4200, Y0 = -4400, X1 = 4700, Y1 = 8600, Mpp = 4, Holes = true, DumpZ = true }, threads);
        Preview.Render(outDir, new PreviewOptions { Name = "downtown_1m", X0 = -1100, Y0 = -1924, X1 = 948, Y1 = 124, Mpp = 1, AutoRamp = true }, threads);
    }

    /// <summary>
    /// Plan views of the transit / tunnel / parking interiors (downtown + Vespucci 1 m/px, the city 2 m/px, the
    /// whole map 5 m/px) and vertical sections along the metro track (trains4): the downtown tunnel from the
    /// east portal to Burton station, Little Seoul station to the open-air Pillbox South station, and the whole
    /// western underground loop.
    /// </summary>
    static void StandardInteriorPreviews(string outDir, int threads)
    {
        InteriorsPreview.RenderPlan(outDir, new InteriorsPreview.PlanOptions { Name = "plan_downtown_vespucci_1m", X0 = -1900, Y0 = -2000, X1 = 700, Y1 = 0, Mpp = 1 }, threads);
        InteriorsPreview.RenderPlan(outDir, new InteriorsPreview.PlanOptions { Name = "plan_city_2m", X0 = -2400, Y0 = -3500, X1 = 1600, Y1 = 700, Mpp = 2 }, threads);
        InteriorsPreview.RenderPlan(outDir, new InteriorsPreview.PlanOptions { Name = "plan_wholemap_5m", X0 = -4200, Y0 = -4400, X1 = 4700, Y1 = 8600, Mpp = 5 }, threads);
        InteriorsPreview.RenderPlan(outDir, new InteriorsPreview.PlanOptions { Name = "plan_city_all_2m", X0 = -2400, Y0 = -3500, X1 = 1600, Y1 = 700, Mpp = 2, Buildings = true, Detached = true }, threads);
        InteriorsPreview.RenderSection(outDir, new InteriorsPreview.SectionOptions { Name = "section_metro_downtown", FirstNode = 850, LastNode = 1100, Hmpp = 1, Vmpp = 0.4, Z0 = -8, Z1 = 112 }, threads);
        InteriorsPreview.RenderSection(outDir, new InteriorsPreview.SectionOptions { Name = "section_metro_little_seoul_pillbox", FirstNode = 1318, LastNode = 1412, Hmpp = 0.5, Vmpp = 0.25, Z0 = -4, Z1 = 76 }, threads);
        InteriorsPreview.RenderSection(outDir, new InteriorsPreview.SectionOptions { Name = "section_metro_west_loop", FirstNode = 850, LastNode = 1412, Hmpp = 4, Vmpp = 0.5, Z0 = -20, Z1 = 120 }, threads);
    }

    static string DlcShort(string path) { var d = GameContext.DlcOf(path); return d.Length == 0 ? "base" : d; }

    static void Info(GameContext ctx)
    {
        var all = ctx.ClassifyBounds();
        var rows = all.GroupBy(y => (y.Interior ? "interior" : "static", y.Layer, y.InCache ? "cached" : "uncached", y.Group != null ? "grouped" : "ungrouped", y.Origin))
            .OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Layer).ThenBy(g => g.Key.Item3).ThenBy(g => g.Key.Item4).ThenBy(g => g.Key.Origin);
        Console.WriteLine($"{all.Count} active ybn after DLC/patch resolution");
        foreach (var g in rows)
            Console.WriteLine($"{g.Count(),6}  {g.Key.Item1,-8} layer {g.Key.Layer}  {g.Key.Item3,-8} {g.Key.Item4,-9} {g.Key.Origin,-6}  e.g. {g.First().Name}");
        Console.WriteLine($"{ctx.Groups.Count} map data groups, {ctx.Groups.Values.Count(g => g.Bounds.Count > 0)} with bounds");
        foreach (var g in ctx.Groups.Values.Where(g => g.Bounds.Count > 0).OrderBy(g => g.Name, StringComparer.Ordinal))
            Console.WriteLine($"  group {g.Name,-44} flags={g.Flags,-3} hours={g.HoursOnOff,-10} weather={g.WeatherTypes.Length} bounds={g.Bounds.Count,-4} ymap={(g.YmapFound ? "y" : "n")} ymapflags={g.YmapFlags} content={g.YmapContentFlags} dlc={DlcShort(g.Manifest)} default={(g.DefaultOn ? "ON" : "off")}/{g.DefaultConfidence} [{g.DefaultSource}] {g.DefaultReason}");
    }
}
