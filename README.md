# Desire Paths

A BepInEx mod for Valheim. Ground that gets walked on often wears in over time:

1. **Trampled.** Small scattered scuffs of bare ground appear, and bumps get lightly smoothed.
2. **Worn.** The patches grow and darken into a broken trail. Grass still grows.
3. **Dirt path.** Bare dirt, the same as the hoe's path tool. The ground is smoothed again.
4. **Gravel.** Stone starts showing through the dirt in patches.
5. **Stone road.** Fully paved, and smoothed once more.

The terrain paint is a blend, not on or off: red is dirt and blue is paving, and partial amounts mix the textures. The early stages paint dirt in patches, using noise, so they look scuffed rather than smeared. Worn patches are strong enough for the grass to go, giving patchy green with bare ground between. The farmland (green) channel is never touched.

Each stage needs a configurable number of steps on top of the stage before it. Routes you only walk now and then fade out of the count, so only the routes you keep using turn into roads.

## How it works

- The world is split into a grid of cells (1 m by default). Walking into a cell on bare terrain counts as one step there. Floors, rocks, water, boats, dungeons and jumping don't count.
- The same player can only add a step to the same cell once every `SameCellCooldown` seconds. That stops someone wearing a road by standing on a cell boundary or running in circles.
- When a cell's count passes a threshold, the mod edits that zone's terrain data directly: it pulls heights toward the local average for smoothing, and paints the ground. The paint is a stroke from the cell's centre to every neighbouring cell already at that stage, so the trail comes out as a continuous strip rather than a row of circles. It then saves the zone, which syncs the change to other players like any hoe edit. Edits don't grow the save, because Valheim stores terrain changes per vertex, not per operation. (Spawning a `TerrainOp` with custom settings doesn't work in current Valheim, which sends terrain operations by prefab and looks the settings up in its registry.)
- Stages are never undone. Decay only lowers the step count toward the *next* stage.

### Map

Paths and roads are drawn on the minimap and the large map in old topographic-map style. Dirt paths use the intermittent-stream symbol, a dash followed by three dots. Stone roads are heavier long dashes. Nearby path points are joined into lines, smoothed, and the pattern runs continuously along each line. The lines are drawn over the map like pins, so they stay sharp at any zoom, and they simplify as you zoom out. Paths only show in areas you, or players who share their map with you, have explored. On a server, joining players receive the existing roads, and new roads are sent to everyone as they form.

Only desire paths are drawn, not roads made with the hoe.

### Safeguards

- **Buildings.** Smoothing changes ground height, and that can leave building pieces without support. If any built piece is within `SmoothRadius + BuildingClearance`, the mod skips smoothing and only paints.
- **Farms.** Cultivated soil is left alone (`ProtectCultivated`).
- **No downgrades.** Ground you paved by hand stays paved when it reaches the dirt stage.
- **Biomes.** Steps aren't counted in `ExcludedBiomes` (Ocean by default).

## Multiplayer

- **Single player or hosting:** your game keeps the counts.
- **Dedicated server with the mod:** the server keeps the counts, so everyone's steps add up on shared routes. The server's thresholds, decay and terrain settings apply. When a cell advances, the server tells the client standing there to change the terrain. That's needed because a dedicated server only loads terrain briefly, while generating it, so it has nothing to edit. The server ignores step reports from more than 40 m away from the reporting player, so a modified client can't pave the map remotely.
- **Server without the mod:** after 10 seconds with no answer from the server, each client counts its own steps. Terrain changes still sync because they go through the game's normal terrain path. Everyone's counts are separate, though.

Step data is saved in `BepInEx/config/DesirePaths/`, one file per world. It's written every 2 minutes and on logout.

## Configuration

`BepInEx/config/cavmkii.DesirePaths.cfg` is created on first launch.

| Setting | Default | Notes |
|---|---|---|
| `Enabled` | true | |
| `CellSize` | 1.0 m | Smaller cells give narrower trails but need more traffic to wear in. |
| `SameCellCooldown` | 5 s | |
| `StepsToTrampled` | 15 | Steps from untouched ground to Trampled. |
| `StepsTrampledToWorn` | 25 | Further steps to Worn (40 total). |
| `StepsWornToDirtPath` | 60 | Further steps to Dirt path (100 total). |
| `StepsDirtPathToGravel` | 150 | Further steps to Gravel (250 total). |
| `StepsGravelToStoneRoad` | 250 | Further steps to Stone road (500 total). |
| `DecayPerDay` | 2 | Steps forgotten per in-game day without traffic. 0 turns decay off. |
| `SmoothRadius` / `SmoothPower` | 1.5 m / 3 | |
| `DirtPathRadius` / `StoneRoadRadius` | 1.0 m / 1.0 m | Painted radius around each worn cell. Neighbouring cells overlap into a continuous strip. |
| `BuildingClearance` | 2 m | -1 smooths even next to buildings. |
| `ProtectCultivated` | true | |
| `ExcludedBiomes` | Ocean | Comma-separated, e.g. `Ocean, AshLands`. |
| `ShowOnMap` | true | Draw paths and roads on the map. |
| `DirtPathMapColor` / `StoneRoadMapColor` | brown / near-black | Line colours. Changes apply right away. |
| `MapLineWidth` | 2 px | Line width. Dashes and dots scale with it, and roads are 1.5× wider. |
| `MapRespectFog` | true | Hide paths in unexplored areas. |
| `ShowStageMessages` | false | Shows a message in the corner when ground under you reaches a new stage. |
| `VerboseLogging` | false | |

A rough guide to the thresholds: a cell on the route between your bed and your workbench might get 10–20 steps a day. With the defaults, that route looks trampled after a day or two, becomes a dirt path in about a week, and becomes a stone road after a month or so of play.

## Building

You need the .NET SDK (6 or later) and a Valheim install with BepInEx. The project publicizes `assembly_valheim.dll` at build time, so no publicized copy is needed.

```sh
dotnet build src/DesirePaths/DesirePaths.csproj -c Release -p:ValheimDir="/path/to/Valheim"
# or set VALHEIM_DIR, or add -p:DeployToGame=true to copy the DLL into BepInEx/plugins
```

The project looks for the game in the default Steam location if you don't pass a path.

Add `-p:PackageZip=true` to also write `dist/DesirePaths-<version>.zip`, with the DLL at the root of the archive, ready for a mod manager.

### Installing with Vortex

1. In Vortex, manage Valheim and install **BepInExPack for Valheim** from Nexus. Launch the game once so BepInEx sets itself up, then quit.
2. Build: `dotnet build src/DesirePaths/DesirePaths.csproj -c Release -p:ValheimDir="<Valheim folder>" -p:PackageZip=true`
3. Drag `dist/DesirePaths-0.3.3.zip` onto Vortex's Mods page (or use *Install From File*). Enable it and click *Deploy*.
4. Check that `<Valheim folder>/BepInEx/plugins/` now contains `DesirePaths.dll`, either directly or in a subfolder. If it's somewhere else, open the mod in Vortex, set its *Mod Type* to the BepInEx plugin type, and deploy again.
5. Launch the game. `BepInEx/LogOutput.log` should contain `Desire Paths 0.3.3 loaded.`, and `BepInEx/config/cavmkii.DesirePaths.cfg` should exist.

When you rebuild, install the new zip over the old one in Vortex (choose *Replace*) and deploy again.

## Status

Early version. Tested in game: the plugin loads, steps are counted, and stages are reached. Things still to confirm in game:

- Editing the zone's terrain data directly shows up in game and survives a reload. Each stage change logs how many height and paint nodes it touched.
- The default radii and smoothing strength look right.
- Reading the paint under a cell, used for "don't downgrade a paved road", picks the right cell. It's best effort; if it reads wrong, the paint operation still runs.

Creatures don't wear paths. Only players do.
