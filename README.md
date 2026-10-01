# Desire Paths

A BepInEx mod for Valheim. Ground that gets walked on often wears in over time:

1. **Smoothed.** Bumps and lumps along the route get evened out.
2. **Dirt path.** The ground is painted the way the hoe's path tool paints it, and the grass goes away.
3. **Stone road.** The path gets paved.

Each stage needs a configurable number of steps. Routes you only walk now and then fade out of the count, so only the routes you keep using turn into roads.

## How it works

- The world is split into a grid of cells (1 m by default). Walking into a cell on bare terrain counts as one step there. Floors, rocks, water, boats, dungeons and jumping don't count.
- The same player can only add a step to the same cell once every `SameCellCooldown` seconds. That stops someone wearing a road by standing on a cell boundary or running in circles.
- When a cell's count passes a threshold, the mod runs one of the game's own terrain operations at that spot (`TerrainOp`, the same thing a hoe piece does when placed). The change is saved in the world and synced to other players like any hoe edit. The number of edits doesn't grow the save, because Valheim stores terrain changes per vertex, not per operation.
- Stages are never undone. Decay only lowers the step count toward the *next* stage.

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
| `StepsToSmooth` | 30 | 0 skips the stage. |
| `StepsToDirtPath` | 100 | 0 skips the stage. |
| `StepsToStoneRoad` | 400 | 0 skips the stage. |
| `DecayPerDay` | 2 | Steps forgotten per in-game day without traffic. 0 turns decay off. |
| `SmoothRadius` / `SmoothPower` | 1.5 m / 3 | |
| `DirtPathRadius` / `StoneRoadRadius` | 1.0 m / 1.0 m | Painted radius around each worn cell. Neighbouring cells overlap into a continuous strip. |
| `BuildingClearance` | 2 m | -1 smooths even next to buildings. |
| `ProtectCultivated` | true | |
| `ExcludedBiomes` | Ocean | Comma-separated, e.g. `Ocean, AshLands`. |
| `ShowStageMessages` | false | Shows a message in the corner when ground under you reaches a new stage. |
| `VerboseLogging` | false | |

A rough guide to the thresholds: a cell on the route between your bed and your workbench might get 10–20 steps a day. With the defaults, that route smooths in a couple of days, becomes a dirt path in about a week, and becomes a road after a few weeks of play.

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
3. Drag `dist/DesirePaths-0.1.0.zip` onto Vortex's Mods page (or use *Install From File*). Enable it and click *Deploy*.
4. Check that `<Valheim folder>/BepInEx/plugins/` now contains `DesirePaths.dll`, either directly or in a subfolder. If it's somewhere else, open the mod in Vortex, set its *Mod Type* to the BepInEx plugin type, and deploy again.
5. Launch the game. `BepInEx/LogOutput.log` should contain `Desire Paths 0.1.0 loaded.`, and `BepInEx/config/cavmkii.DesirePaths.cfg` should exist.

When you rebuild, install the new zip over the old one in Vortex (choose *Replace*) and deploy again.

## Status

This is an untested first version. It compiles, and the step counting, decay and save file logic were tested outside the game. Nobody has run it in Valheim yet. Things to check in game:

- Spawning a bare `TerrainOp` applies the operation and cleans up after itself (`TerrainShaper.Run`).
- The default radii and smoothing strength look right.
- Reading the paint under a cell, used for "don't downgrade a paved road", picks the right cell. It's best effort; if it reads wrong, the paint operation still runs.

Creatures don't wear paths. Only players do.
