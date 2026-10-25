# Fork notice

This is a personal fork of [acidphantasm/SPT-DynamicMaps](https://github.com/acidphantasm/SPT-DynamicMaps) (version 1.2.1),
which is itself a fork of [mpstark/SPT-DynamicMaps](https://github.com/mpstark/SPT-DynamicMaps).

The original work is by Michael P. Starkweather (mpstark), with contributions from acidphantasm and DrakiaXYZ, and is
licensed under the **MIT License** (see `Plugin/LICENSE`). That license and copyright notice are unchanged here.

## What this fork changes

Branch `scav-heatmap` adds an optional **scav heatmap** overlay in-raid, instead of exact scav markers:

- One soft heat blob per scav, or per pack of nearby scavs (merge distance), updated every couple of seconds.
- A stable random offset per blob, so the center doesn't point at a scav.
- Blob radius shrinks with the hideout Intelligence Center level, never below 25 m.
- Per-map settings (enabled, base radius, radius reduction per intel level, pack merge distance) in their own F12 sections.
- Optional inclusion of Rogues and Raiders from a configurable intel level.
- Heat fades out after scavs die or leave. By default it is flat: shown on every floor, and scavs on different floors merge into one blob ("Show on all floors"). With that off, per-floor visibility follows the map layers, like markers. Each map also has a "Per-floor heat" setting that does the same for that map alone (on by default for Interchange and Labs, whose floors overlap).
- Respects the server's existing "allow scav markers" permission.

Corpse markers (section "9. Corpse Markers"):

- Corpses you opened yourself (bots looting don't count) fade to a configurable opacity, bosses included.
- Corpses killed by you or your group only show once you are within a radius of them (grows with Perception level, bonus at elite) or once you opened them. Other corpses show by a chosen rule: always, once you opened them, once you are very close (3 m by default, 10 m at elite Perception), or either (default: close). Group members' corpses always show. A shown marker stays. Optionally also reveal your kills a configurable time after death (off by default, no background work while off). Both radii show as extra rows in the Perception skill tooltip.

Code changes: `Plugin/DynamicMarkers/ScavHeatmapProvider.cs` and `Plugin/UI/Components/ScavHeatCell.cs` (new), changes to `CorpseMarkerProvider.cs`, `MapMarker.cs`, `PlayerPatches.cs` and `PerceptionSkillUiPatches.cs` (new), small
additions to `MapView.cs`, `Settings.cs` and `ModdedMapScreen.cs`, and a build change that only deploys the plugin DLL.

Parts of this fork were written with AI assistance (Claude). It is published as a personal fork, not as an upstream
contribution.

## Building

```
dotnet build Plugin -c Release                    # builds and copies DynamicMaps.dll into the game folder
dotnet build Plugin -c Release -p:DeployToGame=false
```

The default game folder is set in `Plugin/DynamicMaps.csproj` (`TarkovDir`); override it with `-p:TarkovDir=...`.
