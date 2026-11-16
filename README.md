# Arclight Optimizer

**[Add to VCC or ALCOM](https://research-outpost.github.io/arclight-optimizer-vpm/)**

Smaller, lighter VRChat avatars, optimized on a copy at build time. By default you shouldn't see or hear a difference; settings that can change how the avatar looks or sounds stay off until you turn them on.

## What it does

- **Textures**: clears unused areas, fixes their edges, crops to what's used, and on PC uses smaller formats where it can.
- **Duplicates**: identical textures, materials, clips, meshes and audio are merged.
- **Meshes**: merges compatible meshes and bones that never move, turns single-bone accessories into plain meshes, and removes unused geometry and material slots.
- **Unused things**: removes objects, renderers, audio, PhysBones, colliders, contacts, constraints, bones and animator parameters that can never do anything. Expression parameters always stay.
- **Blend shapes**: bakes in shapes nothing changes and merges shapes that always move together, at the same weight or a fixed ratio of it.
- **Animator**: merges simple layers and toggles into fewer layers.
- **PhysBones**: pauses PhysBones while their outfit is hidden, and replaces end bones that only mark a point with the PhysBone's endpoint.
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

```
https://research-outpost.github.io/arclight-optimizer-vpm/index.json
```

Manual install: extract `research-outpost.arclight-optimizer-<version>.zip` from **[Releases](https://github.com/research-outpost/arclight-optimizer/releases)** into `Packages/research-outpost.arclight-optimizer`.

## Use

1. Select the avatar root (the object with the **VRChat Avatar Descriptor**).
2. **Add Component → Arclight → Arclight Optimizer**.
3. Build or upload as usual, or enter Play Mode to preview (needs Reload Scene on, in Project Settings → Editor).

Settings are explained under **Optional settings** below. **Exclude** lists objects (and their children) that Arclight leaves alone. Disable the component to skip it.

**Open last report** on the component shows what the last build saved, changed and kept.

## Optional settings

Each of these trades some quality for size, so it's off until you choose it. Check the built avatar before uploading.

- **Unsupported Shaders**: also optimizes textures on shaders Arclight doesn't recognize, by guessing how they're mapped. A wrong guess can show seams.
- **MMD Support** (on by default): keeps the blend shapes MMD dance worlds use. Turn it off to let Arclight bake them; the face then won't move in those worlds.
- **Unified Bounds**: gives every skinned mesh one shared bounding box, so nothing vanishes up close and more meshes can merge. Meshes may draw when just off screen.
- **Compress Audio**: re-encodes uncompressed clips as Vorbis at the chosen **Audio Quality**, about a tenth of the size, with some quality loss.
- **Texture Size Cap** (PC only): shrinks textures above 1024, 2048 or 4096 px in the build, so they look softer when displayed large. Textures also used by particles, trails, lines, excluded objects or animation-swapped materials keep full size.
- **Menu Icon Cap**: shrinks menu icons above 64, 128 or 256 px, so they look slightly softer when the menu is displayed large.
- **Narrow Opaque Textures** (PC only): stores opaque colour textures that Unity compresses as BC7 (High Quality) in BC1 instead, half the size. Smooth gradients can band: typical error 1.1–1.3 levels of 255, 99% within 4–5 levels, under 0.001% more than 32 off, and still visible close up on a real avatar. Normal maps and textures whose alpha is used are never narrowed.

## Other optimizers

- **Avatar Optimizer (AAO)**: can't be used together; the build stops with an error.
- **d4rk Avatar Optimizer**: supported.

## Details

**Texture clearing.** Used pixels are kept, padding is rebuilt from them, and the rest is filled with a flat colour. A texture is only replaced when its estimated compressed size gets smaller.

**Smaller formats (PC only).** DXT5 becomes DXT1 when alpha is never read or always opaque, and red-only linear masks become BC4 (R8 when uncompressed). Only for shaders whose source has been checked: Unity Standard and Unlit/Texture, lilToon 2.x, Poiyomi 11, 10 and 9.3 Toon and Pro (three red-only masks on other 9.x Toon), VRChat mobile shaders, NonToon, Sunao, ORL Toon and Mochie Standard. BC7, crunched and Quest textures keep their format unless **Narrow Opaque Textures** is turned on.

**Mono audio.** Identical-channel stereo is stored as mono at +3 dB (same level and balance), only when it can't clip and every source is spatialized or plain 2D without pan.

## Supported shaders

| Shader | Support |
| --- | --- |
| lilToon 2.x | All variants |
| Poiyomi 9.x–11.x | Locked and unlocked (not Triplanar Projection, Triple Outline or URP) |
| VRChat mobile shaders | Toon Standard, Toon Lit, Standard Lite and others |
| NonToon 0.1.3 and 0.3.0 | Default modules |
| Sunao 1.6 | All variants (Fur is not cropped) |
| ORL Toon v1 and v2 (7.3 and later) | All variants |
| Mochie Standard 2.13 and 2.14 | Standard, Lite and Mobile |
| Mochie Uber 1.35 | Uber and Uber (Outline); not cropped |
| Unity Toon Shader 0.16 | Toon/Toon (built-in pipeline); not cropped |
| UnlitWF 2.15 | UnToon family except Tessellation and Custom; not cropped |
| Silent's Filamented | Standard, Roughness, Specular and Cloth setups; not cropped |
| Unity | Standard and Unlit/Texture |

## Limitations

- No atlasing; the only resolution reduction is the opt-in **Texture Size Cap**.
- Sources: 8- or 16-bit PNG, PSD, TGA, TIFF, BMP and JPEG, for normal maps too. Other formats (EXR/HDR, DDS/KTX, animated PNG, cubemaps and texture arrays) are kept.
- Matcaps, ramps, unknown sampling and untrackable texture animation are kept.
- Quest textures keep their formats; Quest builds are untested on a device.
- PhysBones on a hidden outfit are paused, so they restart from their rest pose when it's shown.
- Poiyomi is checked on Toon 10.0.20–10.0.29 and 9.3.63–9.3.67 and Pro 11.0.43, 10.0.28 and 9.3.67. Other 11.x, 10.x and 9.x releases are accepted without being checked. Poiyomi 11 keeps every channel and is cropped on the same fields as 10.x. The URP, Triplanar Projection and Triple Outline entries aren't supported.
- Cropping on Poiyomi, Sunao, Mochie Standard and ORL Toon (PC only) is checked against Poiyomi Toon 10.0.20–10.0.29 and 9.3.63–9.3.67, Mochie Standard 2.14.2, Sunao 1.6.2 and ORL 7.3–7.4.0-dev.4, but Poiyomi Pro and other 10.0.x, 11.0.x, 9.3.x, 2.14 and 7.x releases are cropped too. ORL Toon is not cropped while AudioLink or emission Mask By UV Tile is on. NonToon, Sunao Fur, Mochie Uber, Unity Toon Shader, UnlitWF and Filamented are never cropped (NonToon's textures have no tiling/offset to rewrite, and Sunao Fur's fur pattern reads the main UV). A patch that changes how the shader reads textures could show wrong colours. Locked Poiyomi materials are never cropped: the lock bakes each texture's tiling/offset into the shader.

## License

[MIT](LICENSE.md)
