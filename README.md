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

Requires **Unity 2022.3**, the **VRChat SDK 3.10.6 or later** (material contacts, which Arclight models, were introduced in 3.10.6) and **[NDMF](https://github.com/bdunderscore/ndmf) 1.14.8 or later (1.x)** (tested on 1.14.8 and 1.15.0).

**[Add to VCC or ALCOM](https://research-outpost.github.io/arclight-optimizer-vpm/)**, or add this repository in **Settings → Packages → Add Repository**:

```
https://research-outpost.github.io/arclight-optimizer-vpm/index.json
```

Manual install: extract `research-outpost.arclight-optimizer-<version>.zip` from **[Releases](https://github.com/research-outpost/arclight-optimizer/releases)** into `Packages/research-outpost.arclight-optimizer`.

## Use

1. Select the avatar root (the object with the **VRChat Avatar Descriptor**).
2. **Add Component → Arclight → Arclight Optimizer**.
3. Build or upload as usual, or enter Play Mode to preview (needs Scene Reload on).

Options: **Unsupported Shaders** (see Limitations), **MMD Support** (on by default; keeps MMD dance blend shapes), **Texture Size Cap** (Off by default; caps build copies of the avatar's textures at 1024, 2048 or 4096 pixels, PC only), **Menu Icon Cap** (Off by default; caps expression-menu icon build copies at 64, 128 or 256 pixels, only when the measured bundle shrinks), **Narrow Opaque Textures** (Off by default; ships opaque colour textures Unity imports as BC7 as BC1 at half the size, PC only, and the one setting that can visibly band), **Compress Audio** (off by default; re-encodes uncompressed clips as Vorbis at the chosen quality, only when the measured bundle shrinks) and **Exclude** (objects and their children left alone). Disable the component to skip it.

**Open reports folder** on the component shows what each build saved and changed, and why anything was kept.

## Accepted differences

- **Generated textures**: compression rounding after clearing and repadding.
- **Opaque colour textures** (PC builds): a texture Unity imports as DXT5, whose alpha nothing reads, ships as BC1 (DXT1) at half the size. BC1 stores the same colour as DXT5, so this is not visible.
- **Narrow Opaque Textures** (Off by default, PC only): a texture Unity imports as BC7 (Compressed High Quality), whose alpha nothing reads, ships as BC1 at half the size. BC1 carries less colour precision than BC7, so very smooth gradients can band. Measured in sRGB against the authored art: 1.1-1.3 RMS per channel of 255, a 99th percentile of 4-5 levels, fewer than 0.001% of channels more than 32 off — and the banding was still visible on a real avatar close up, which is why this is a choice rather than the default. Normal maps and every other texture type are never narrowed.
- **Merged meshes and bones**: at most 1/255 per colour channel.
- **Cropped textures**: only the smallest mips can differ.
- **Paused PhysBones**: restart from rest when shown again.
- **Mono audio**: level within 0.05%.
- **Compressed audio** (off by default): uncompressed clips are re-encoded as Vorbis at the chosen quality, so the sound changes by the codec's loss; a clip is only replaced when its measured bundle is smaller.
- **Menu icons**: every expression-menu icon gets a build copy — mipmaps are dropped (the menu draws an icon at one size, so no mip is sampled) and an opaque icon compresses as DXT1 instead of DXT5; in a build it is used only when it measures smaller. With **Menu Icon Cap** set, icons above 64, 128 or 256 pixels are also resampled, so they look slightly softer when the menu is displayed large.
- **Texture Size Cap** (Off by default, PC only): textures above 1024, 2048 or 4096 pixels are replaced by a resampled build copy. A texture also held by a particle, trail or line renderer, by an excluded object, or by a material an animation swaps in keeps its authored size, since only one copy of it can be repointed safely, so they look softer when displayed large. In a build the copy is used only when its measured bundle is smaller; in Play Mode there is no bundle to measure, and the copy is used anyway because capping, dropping mips and narrowing the format only ever remove data. Source textures and Edit Mode materials never change — look at the built avatar or the report, not the texture in the Project window.
- **Folded animator layers**: a rotation can differ by one float step.
- **Unified Bounds** (off by default): meshes may draw when just off screen.
- **One anchor override**: every mesh is lit from the Chest bone (Exclude keeps a mesh's own), so lighting shifts slightly on meshes that had another anchor or none.
- **Cropping on Poiyomi, Sunao and Mochie Standard** (PC builds only; Quest keeps the previous behaviour): Poiyomi accepts 10.0.x and 9.3.x, audited on 10.0.29 and 9.3.67 only; Mochie Standard accepts every 2.14 release, audited on 2.14.2 only; Sunao is audited on 1.6.2. A patch release that changes a lookup would crop unsafely. Mochie turns cropping off when its own version label cannot be read, and locked Poiyomi materials are never cropped.

## Other optimizers

- **Avatar Optimizer (AAO)**: not supported.
- **d4rk Avatar Optimizer**: supported.

## Details

**Texture clearing.** Used pixels are kept, padding is rebuilt from them, and the rest is filled with a flat colour. A texture is only replaced when its estimated compressed size gets smaller.

**Smaller formats (PC only).** DXT5 becomes DXT1 when alpha is never read or always opaque, and red-only linear masks become BC4 (R8 when uncompressed). Only for shaders whose source has been checked: Unity Standard and Unlit/Texture, lilToon 2.x, Poiyomi 10–12, VRChat mobile shaders, NonToon, Sunao, ORL Toon and Mochie Standard. BC7, crunched and Quest textures keep their format unless **Narrow Opaque Textures** is turned on.

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

- No atlasing; the only resolution reduction is the opt-in **Texture Size Cap**.
- Sources: 8- or 16-bit PNG, PSD, TGA, TIFF, BMP and JPEG, for normal maps too. Other formats (EXR/HDR, DDS/KTX, animated PNG, cubemaps and texture arrays) are kept.
- Matcaps, ramps, unknown sampling and untrackable texture animation are kept.
- Quest textures keep their formats; Quest builds are untested on a device.
- **Unsupported Shaders** guesses texture mapping for unrecognized shaders and may cause artifacts. Test before uploading.

## License

[MIT](LICENSE.md)
