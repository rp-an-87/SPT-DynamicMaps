# Fork notice

This is a personal fork of [acidphantasm/SPT-DynamicMaps](https://github.com/acidphantasm/SPT-DynamicMaps) (version 1.2.1),
which is itself a fork of [mpstark/SPT-DynamicMaps](https://github.com/mpstark/SPT-DynamicMaps).

The original work is by Michael P. Starkweather (mpstark), with contributions from acidphantasm and DrakiaXYZ, and is
licensed under the **MIT License** (see `Plugin/LICENSE`). That license and copyright notice are unchanged here.

## What this fork changes

Branch `scav-heatmap` adds an optional **scav heatmap** overlay in-raid, instead of exact scav markers:

- One soft heat blob per scav, or per pack of nearby scavs (merge distance), updated every couple of seconds.
- A stable random offset per blob, so the center doesn't point at a scav.
- Blob radius shrinks with the hideout Intelligence Center level, never below 50 m.
- Per-map settings (enabled, base radius, radius reduction per intel level) in their own F12 sections.
- Optional inclusion of Rogues and Raiders from a configurable intel level.
- Heat fades out after scavs die or leave; per-floor visibility follows the map layers, like markers.
- Respects the server's existing "allow scav markers" permission.

Code changes: `Plugin/DynamicMarkers/ScavHeatmapProvider.cs` and `Plugin/UI/Components/ScavHeatCell.cs` (new), small
additions to `MapView.cs`, `Settings.cs` and `ModdedMapScreen.cs`, and a build change that only deploys the plugin DLL.

Parts of this fork were written with AI assistance (Claude). It is published as a personal fork, not as an upstream
contribution.

## Building

```
dotnet build Plugin -c Release                    # builds and copies DynamicMaps.dll into the game folder
dotnet build Plugin -c Release -p:DeployToGame=false
```

The default game folder is set in `Plugin/DynamicMaps.csproj` (`TarkovDir`); override it with `-p:TarkovDir=...`.
