# Arclight Optimizer

Smaller, lighter VRChat avatars, optimized on a copy at build time. Nothing looks, sounds or behaves differently, apart from the accepted differences below.

## What it does

- **Textures**: clears unused areas, rebuilds padding, crops to the used part, and uses smaller formats on PC where a channel is never read.
- **Duplicates**: identical textures, materials, clips, meshes and audio are merged.
- **Meshes**: merges compatible meshes, turns single-bone accessories into plain meshes, merges bones nothing moves, removes unused triangles, vertices, vertex data and material slots, and uses 16-bit index buffers where they fit.
- **Unused things**: removes objects, renderers, audio, PhysBones, colliders, contacts, constraints, bones and animator parameters that can never do anything. Expression parameters always stay.
- **Blend shapes**: bakes in shapes nothing changes and merges shapes that always share a weight.
- **Animator**: folds simple layers and toggles into shared Direct blend trees.
- **PhysBones**: pauses PhysBones while their outfit is hidden, and uses endpoints for matching end bones.
- **Other**: lowers Max Particles to what can actually be alive, stores identical-channel stereo audio as mono, trims animation keys that change nothing, and converts Unity constraints to VRChat constraints.

It runs automatically on build and in Play Mode. Your source assets are never modified.

### Arclight Analyzer

**Tools → Arclight → Analyzer** finds setup mistakes and says how to fix them:

- **Parameters**: missing from a controller, unused, menu controls that do nothing, or types that lose values.
- **Animators**: mixed Write Defaults, layers that never play, states that leave values stuck or have transitions that never happen, animations of things the avatar doesn't have, drivers aimed at nothing, Layer Controls pointing at missing layers.
- **Menus**: over 8 controls, empty or looping submenus, puppets on the wrong type, values a parameter can't hold.
- **Objects**: toggles that start differently in game, missing scripts, broken references, receivers and PhysBone parameters nothing reacts to.
- **Sync budget**: synced bits used out of 256.

It also checks the avatar as uploaded, so Modular Avatar and VRCFury additions count. Fix buttons (**Add**, **Remove**, **Clear**) only run when pressed, support Undo, and back up changed files for **Fix history**. **Ignore** hides a card.

## Install

Requires **Unity 2022.3** and **[NDMF](https://github.com/bdunderscore/ndmf) 1.14.8 or later (1.x)**.

**[Add to VCC or ALCOM](https://research-outpost.github.io/arclight-optimizer-vpm/)**, or add this repository in **Settings → Packages → Add Repository**:

```
https://research-outpost.github.io/arclight-optimizer-vpm/index.json
```

Manual install: extract `research-outpost.arclight-optimizer-<version>.zip` from **[Releases](https://github.com/research-outpost/arclight-optimizer/releases)** into `Packages/research-outpost.arclight-optimizer`.

## Use

1. Select the avatar root (the object with the **VRChat Avatar Descriptor**).
2. **Add Component → Arclight → Arclight Optimizer**.
3. Build or upload as usual, or enter Play Mode to preview (needs Scene Reload on).

Options: **Unsupported Shaders** (see Limitations), **MMD Support** (on by default; keeps MMD dance blend shapes) and **Exclude** (objects and their children left alone). Disable the component to skip it.

**Open reports folder** on the component shows what each build saved and changed, and why anything was kept.

## Accepted differences

- **Generated textures**: compression rounding after clearing and repadding.
- **Merged meshes and bones**: at most 1/255 per colour channel.
- **Cropped textures**: only the smallest mips can differ.
- **Paused PhysBones**: restart from rest when shown again.
- **Mono audio**: level within 0.05%.
- **Folded animator layers**: a rotation can differ by one float step.
- **Unified Bounds** (off by default): meshes may draw when just off screen.
- **One anchor override**: every mesh is lit from the Chest bone (Exclude keeps a mesh's own), so lighting shifts slightly on meshes that had another anchor or none.

## Other optimizers

- **Avatar Optimizer (AAO)**: not supported.
- **d4rk Avatar Optimizer**: supported.

## Details

**Texture clearing.** Used pixels are kept, padding is rebuilt from them, and the rest is filled with a flat colour. A texture is only replaced when its estimated compressed size gets smaller.

**Smaller formats (PC only).** DXT5 becomes DXT1 when alpha is never read or always opaque, and red-only linear masks become BC4 (R8 when uncompressed). Only for shaders whose source has been checked: Unity Standard and Unlit/Texture, lilToon 2.x, Poiyomi 10–12, VRChat mobile shaders, NonToon, Sunao, ORL Toon and Mochie Standard. BC7, crunched and Quest textures keep their format.

**Mono audio.** Identical-channel stereo is stored as mono at +3 dB (same level and balance), only when it can't clip and every source is spatialized or plain 2D without pan.

## Supported shaders

| Shader | Support |
| --- | --- |
| lilToon 2.x | All variants |
| Poiyomi 9.x–12.x | Locked and unlocked |
| VRChat mobile shaders | Toon Standard, Toon Lit, Standard Lite and others |
| NonToon 0.1.3 and 0.3.0 | Default modules |
| Sunao 1.6 | All except Fur |
| ORL Toon v1 and v2 | All variants |
| Mochie Standard 2.13 and 2.14 | Standard, Lite and Mobile |
| Unity | Standard and Unlit/Texture |

## Limitations

- No downscaling or atlasing.
- Sources: 8-bit PNG, PSD, TGA, TIFF, BMP and JPEG. Normal maps must be PNG.
- Matcaps, ramps, unknown sampling and untrackable texture animation are kept.
- Distant mips can show slight colour bleeding; check at a distance.
- Quest textures keep their formats; Quest builds are untested on a device.
- **Unsupported Shaders** guesses texture mapping for unrecognized shaders and may cause artifacts. Test before uploading.

## License

[MIT](LICENSE.md)
