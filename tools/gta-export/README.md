# gta-export

C# console tool that reads a GTA V install (read only) through CodeWalker.Core and writes the static world
collision, the interiors, the material table and the water data into the work folder. `python lossantos.py prepare`
builds and runs it; by hand:

```
dotnet build -c Release
dotnet bin/Release/net8.0/gta-export.dll all --game "<GTA V folder>" --out "<work>/export"          # about 3 minutes, 6 GB
dotnet bin/Release/net8.0/gta-export.dll interiors --game "<GTA V folder>" --out "<work>/export"    # 0.6 GB
dotnet bin/Release/net8.0/gta-export.dll --help
```

- Archive keys are derived in memory from `GTA5.exe` on each run; never written, never printed.
- The game folder is only opened for reading; the tool refuses an output folder inside it.
- Resumable and deterministic: up-to-date files are skipped; `--force` rebuilds.
- `--profile online` (default) is the multiplayer map, `--profile story` the single-player map.
