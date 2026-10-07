using System.Globalization;

namespace GtaSkate;

public static class Program
{
    const string DefaultOut = @"work/map";

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var o = new Opts(args);
        var cmd = o.Positional.Count > 0 ? o.Positional[0] : "help";
        if (cmd == "help" || o.Has("help"))
        {
            Console.WriteLine("""
                gta-skate model [options]      the same selection as one portable glTF scene: <name>.gltf, geometry/*.bin,
                                               textures/*.png, collision/*.glb (default out work/model, 512 px textures)
                gta-skate export [options]     GTA V map (read-only) -> normalized map folder for reskate_cli compile-map
                  --name test                  map folder under --out
                  --region x0,y0,x1,y1         GTA metres (default -100,-1200,500,-600: Legion Square)
                  --spawn x,y[,z[,yaw]]        GTA coordinates (default: middle of the region, on the ground)
                  --out dir                    default work/map
                  --game dir                   GTA V folder
                  --tile 200                   tile size in metres
                  --min-size 0.5               skip entities with a bounding radius below this
                  --level 1  --tree-level 1    model inside each drawable: 0 high ... 3 vlow
                  --tex-cap 256                largest texture side
                  --min-instances 3  --min-instance-tris 150   when a repeated archetype stays instanced
                  --display "Los Santos"       level name shown in ReSkate
                  --stops "Name:x,y[,yaw[,z]];..." fast-travel bus stops (GTA coordinates; without z: nearest open road)
                  --offset dx,dy               added to every position (Studio's world is 9600 m square about 0,0)
                  --no-interiors               leave out the placed interiors (metro, tunnels, car parks, shops)
                  --map-height 2160            pixel height of the top-down picture ui/map_image.png (an overview of a big region: 6480)
                  --no-props                   leave out every prop (objects without a LOD parent); an experiment switch
                  --no-water                   leave out the sea and lakes (the game's water quads as Studio's ocean)
                  --no-prop-collision          props do not collide (only the static world does)
                  --threads N
                """);
            return 0;
        }
        string gameDir = o.Str("game", Paths.DefaultGame), outRoot = o.Str("out", DefaultOut);
        if (Path.GetFullPath(outRoot).StartsWith(Path.GetFullPath(gameDir), StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("refusing an output folder inside the game folder"); return 2; }
        int threads = o.Int("threads", Math.Max(2, Environment.ProcessorCount - 2));
        try
        {
            switch (cmd)
            {
                case "export":
                case "model":
                {
                    var po = new PackOptions
                    {
                        Name = o.Str("name", "test"),
                        Tile = (float)o.Num("tile", 200), MinSize = (float)o.Num("min-size", 0.5),
                        Level = o.Int("level", 1), TreeLevel = o.Int("tree-level", 1), TexCap = o.Int("tex-cap", 256),
                        MinInstances = o.Int("min-instances", 3), MinInstanceTris = o.Int("min-instance-tris", 150),
                        BandRows = o.Int("band-rows", 8),
                        PropCollision = !o.Flag("no-prop-collision"), Water = !o.Flag("no-water"), Interiors = !o.Flag("no-interiors"), NoProps = o.Flag("no-props"), MapHeight = o.Int("map-height", 2160),
                    };
                    if (cmd == "model") { po.Model = true; po.TexCap = o.Int("tex-cap", 512); if (!o.Has("out")) outRoot = @"work/model"; }
                    var region = o.Nums("region", 4);
                    if (region != null) po.Region = region;
                    po.Limit = (float)o.Num("limit", 4780);
                    var offs = o.Nums("offset", 2);
                    if (offs != null) po.Offset = offs;
                    po.DisplayName = o.Str("display");
                    var stops = o.Str("stops");
                    if (stops != null)
                        foreach (var item in stops.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int c = item.LastIndexOf(':');
                            var v = item.Substring(c + 1).Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                            po.Stops.Add((item.Substring(0, c).Trim(), v[0], v[1], v.Length > 2 ? v[2] : 0f, v.Length > 3 ? v[3] : float.NaN));
                        }
                    var sp = o.Str("spawn");
                    if (sp != null) po.Spawn = sp.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    var game = Game.Open(gameDir);
                    Pack.Run(game, outRoot, o.Str("collision", Paths.DefaultCollision), threads, po);
                    return 0;
                }
                default:
                    Console.Error.WriteLine("unknown command " + cmd);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAILED: " + ex);
            return 1;
        }
    }
}
