# Vegetation POC

A scene for client presentations. It shows six vegetation environments, each on its own Unity terrain, arranged so they're easy to compare.

## Build the scene

1. Open the project in **Unity 2022.3.48f1** (it uses URP).
2. From the menu bar, choose **Tools ▸ Vegetation ▸ Build Vegetation POC Scene**. The build takes about a minute.
3. Unity opens `Assets/VegetationPOC/Scenes/VegetationPOC.unity`. Press **Play** to start the presentation.

To build without opening the editor (batch mode):

```
Unity.exe -batchmode -projectPath "<project>" -executeMethod VegetationPOCBuilder.BuildFromCommandLine -quit -logFile build.log
```

You can rebuild as many times as you like. Each build deletes and recreates everything under `Assets/VegetationPOC/Generated/`, and it never changes the source asset packs.

## Layout

```
 back row  │ 04 Heathland & Moor │ 05 Dry Savanna Scrubland │ 06 Riparian Wetland │
 front row │ 01 Temperate Broadleaf Forest │ 02 Coniferous Pine Forest │ 03 Wildflower Meadow │
                                   ▲ overview camera (south)
```

- Each terrain is 160 m × 160 m, with 40 m gravel paths between them.
- Every terrain has a **title sign** at its front edge showing its number, title, a description and its main species.
- Large **floating labels** appear over each terrain when the camera is high, as in the overview.

| # | Environment | Trees / large plants | Ground cover | Ground textures |
|---|---|---|---|---|
| 01 | Temperate Broadleaf Forest | Tree9 variants, Sycamore, Sample Trees, Alder, bushes | Ferns, woodland plants, shrubs, grass | Moss, soil, mud |
| 02 | Coniferous Pine Forest | Scots Pine A/B (mature and young), rocks | Heather, ferns, dry grass | Rocky soil, moss, rock on slopes |
| 03 | Wildflower Meadow | Solitary Sycamore and Tree9, bush clusters | Dense meadow grasses, flowering plants | Grass A/B, soil patches |
| 04 | Heathland & Moor | Sparse ThinTree, stunted pine, rocky outcrops | Heather carpets, dry grass, dry shrubs | Heather, dry grass, rock |
| 05 | Dry Savanna Scrubland | Mimosa (umbrella crowns), bushes, rocks | Golden dry grasses, dry shrubs | Dry grass, sand, rocky soil |
| 06 | Riparian Wetland | Alder along the shore, broadleaf trees, bushes | Reed belt, ferns, marsh plants | Grass, mud, tidal pools, pond water |

## Presentation controls (Play mode)

| Key | Action |
|---|---|
| `0` / `Home` | Overview |
| `1`–`6` | Aerial view of a biome |
| `G` | Switch between aerial and ground-level view |
| `←` / `→` | Previous / next viewpoint |
| `Space` | Start or stop the automatic tour |
| Right mouse button + `WASD` / `QE` | Free-fly (hold `Shift` to go faster) |
| `L` / `H` | Toggle floating labels / HUD |

## Assets used (all already in the project)

- **TerrainSampleAssets**: grass, dry grass, ferns, plants, heather and bush detail meshes, plus all terrain layers.
- **Tree9**: four broadleaf tree variants.
- **Tom's Terrain Tools**: Alder, Mimosa, Scots Pine A/B, Sycamore, ThinTree, Bush5/7, the rock mesh and the three Sample Trees.
- **Fantasy Skybox FREE**: the daytime sky (`FS000_Day_01_Sunless`).

The builder makes URP copies of every tree material from the original textures. Some of the original Tom's Terrain Tools materials use legacy shaders, and some of the URP copies already in the project have no textures assigned.

**Not used:** `HDRP_Oak_Tree`. Its prefabs reference a mesh and materials that aren't in the repository.

## Tuning

All settings are near the top of `Editor/VegetationPOCBuilder.cs`:

- **Layout:** `TerrainSize`, `Gap`, `Columns`.
- **Biome content:** the biome definitions live in `DefineBiomes()`. Each biome lists its terrain layers, tree rules (species, spacing, height range) and detail rules (density per m²).
- **Shadows (optional):** the project's URP shadow distance is 50 m, so tree shadows fade out in the aerial views. The builder does **not** change it, because it is an original project asset. To raise it to 220 m, choose **Tools ▸ Vegetation ▸ Apply Recommended Shadow Distance**. It asks for confirmation first and can be undone.

## What the build changes outside `Assets/VegetationPOC/`

- It adds `VegetationPOC.unity` to **File ▸ Build Settings** (`ProjectSettings/EditorBuildSettings.asset`). It only adds; nothing is removed.
- Nothing else. Every source asset and setting is left as it is.
