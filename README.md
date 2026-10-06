# Arclight Optimizer

Smaller, lighter VRChat avatars, optimized on a copy at build time. It strips data your materials and shaders never use, removes what can never be seen, heard or used, and merges what can be merged, without changing how the avatar looks, sounds or behaves (beyond the few accepted differences listed below).

## What it does

- **Clears unused texture areas** and rebuilds the padding around the parts your meshes use (see below).
- **Uses smaller texture formats on PC** where your shader never reads a channel.
- **Merges duplicates**, so identical textures, materials, clips, meshes and audio are uploaded once.
- **Shrinks mesh index buffers** from 32-bit to 16-bit where they fit.
- **Removes unused vertex data on PC**: tangents, vertex colours and extra UV sets that no material on a mesh reads. For lilToon, tangents go when no material uses a normal map, anisotropy, parallax, decals or an outline vector (outline Vector Scale 0), UV4 to UV7 go unless the ID mask reads them, and four-component UVs keep the two components lilToon reads. Avatars using d4rk Avatar Optimizer are left alone.
- **Lowers Max Particles** on each particle system to the most particles it can actually have alive, so the effect looks the same with less to reserve. Particle systems that can never emit are removed, and trails with lifetime 0 or collision that can hit nothing are turned off.
- **Stores identical-channel stereo audio as mono**, at the same loudness.
- **Trims animation keys** that change nothing about playback.

### Avatar-wide optimization (new in 1.1.0)

- **Removes what can never be seen, heard or used**: objects that are never active, renderers never enabled, silent audio sources, unused PhysBones, colliders, contacts, constraints and bones, empty containers, and animator parameters nothing reads. Expression parameters always stay, for OSC and PC/Quest sync.
- **Blend shapes**: shapes nothing changes are baked in, and shapes that always share a weight are merged.
- **Meshes**: compatible skinned meshes are merged, plain meshes that never move apart are merged, single-bone accessories become plain meshes, and bones nothing moves merge into their parent.
- **Cleanup**: zero-sized triangles, unused and repeated vertices, empty or duplicate material slots, and material data the shader never reads.
- **Animator**: single-state layers and toggle state machines (including two-parameter toggles and toggles through Entry and Exit) fold into shared Direct blend trees that switch in the same frame. With d4rk Avatar Optimizer, its merged-toggle layer joins an existing blend tree layer when that is exact.
- **PhysBones**: a PhysBone that only moves a hidden outfit pauses while the outfit is hidden, and chains ending in matching end bones use an endpoint instead.
- **Texture cropping**: a texture whose meshes only use an aligned part of it is cropped to that part.
- **Unity constraints** are converted to VRChat constraints with the SDK's own converter, as VRChat would do when the avatar loads.

It runs automatically when you build or enter Play Mode, on a temporary copy. Your textures, materials, meshes, audio and scenes are never modified.

## Install

Requires **[NDMF](https://github.com/bdunderscore/ndmf) 1.14.8 or later (1.x)**.

**[Add to VCC or ALCOM](https://research-outpost.github.io/arclight-optimizer-vpm/)**

Or add this repository URL by hand in **Settings → Packages → Add Repository**:

```
https://research-outpost.github.io/arclight-optimizer-vpm/index.json
```

For a manual install, download `research-outpost.arclight-optimizer-<version>.zip` from **Releases** and extract it into `Packages/research-outpost.arclight-optimizer`.

## Use

1. Select your avatar root, the object with the **VRChat Avatar Descriptor**.
2. Choose **Add Component → Arclight → Arclight Optimizer**.
3. Build and upload as usual, or enter Play Mode to preview.

Everything is automatic. The options are **Allow Unsupported Shaders** (see Limitations), **MMD Support** (on by default: keeps the blend shapes MMD dance worlds animate on the Body mesh) and **Exclude** (objects Arclight leaves alone, with everything under them). To skip the optimizer for an avatar, disable or remove the component.

After a build, click **Open reports folder** on the component. Each report opens with how much the build saved (estimated download and mesh data, as separate figures), then shows what changed, why any texture was kept (largest first) and why meshes or layers were not merged.

## Accepted differences

Everything else is exact. These differences are accepted:

- **Generated textures**: unused areas are cleared, padding is rebuilt and the texture is compressed again, so texels can differ by compression rounding (see below).
- **Merged meshes and bones**: at most 1/255 per colour channel from re-rounding.
- **Cropped textures**: only the smallest mip levels can differ.
- **Paused PhysBones**: a chain restarts from its rest pose when its outfit is shown again.
- **Mono audio**: the level matches within 0.05%.
- **Folded animator layers**: a rotation they animate can differ by one float step.

## Other optimizers

- **Avatar Optimizer (AAO)**: not supported.
- **d4rk Avatar Optimizer**: supported.

## Clearing unused texture areas

Most avatar textures contain areas that no part of the model ever shows. Arclight keeps the pixels your meshes use, rebuilds a protective padding band around them from those pixels, and fills the rest with a flat colour that compresses far better. Rebuilding the padding also repairs stock textures whose UV islands were padded too thinly, which otherwise show seams at a distance.

A texture is only replaced when its estimated compressed size in the avatar bundle gets smaller.

## Smaller texture formats

On PC builds, a DXT5 texture whose alpha the shader never reads is imported as DXT1, which stores the colour the same way at half the size, in the download and in VRAM. A linear mask that the shader reads only through its red channel becomes BC4, which keeps that channel more precisely (smaller than DXT5, the same size as DXT1).

This only happens where the shader's source has been checked:

- **Unity Standard and Unlit/Texture:** opaque main textures, emission and detail colour, and single-channel maps.
- **lilToon 2.x:** the main texture on opaque shaders, plus its red-channel masks.
- **Poiyomi 10–12:** the main texture with **Force Opaque** or **Ignore Main Texture Alpha** on (unless alpha changes colour, such as with premultiply), masks that read one fixed or selected channel, and several colour maps. Lil Fur and Grab Pass keep alpha. Poiyomi 9.x gets texture clearing but not format changes.
- **VRChat mobile shaders:** main textures that ignore alpha, Standard Lite maps, and Toon Standard's main texture and selected-channel masks.
- **NonToon:** the base texture in Opaque mode or on fur, the shared mask's selected channels, the SDF map and the fur noise mask.
- **Sunao:** masks that never read alpha (alpha, occlusion, shade, lighting boost, rim light and outline masks).
- **ORL Toon:** the main texture on opaque variants, single-channel masks, and masks that read one selected channel.
- **Mochie Standard:** the detail, emission and alpha masks, which read one selected channel.

Anything else keeps its format, as do BC7, crunched and uncompressed textures and every Quest/Android texture.

## Merging duplicates

Outfits and avatar bases often include the same data under different names. These share one copy:

- **Textures** with identical pixels, colour metadata and import settings, including animated texture swaps.
- **Materials** with identical shaders, properties and keywords.
- **Animation clips** with identical curves, events and settings. This usually saves only a few kilobytes.
- **Meshes** with identical vertices, triangles, bone weights and blend shapes.
- **Audio clips** with identical files and import settings.

Only the avatar being built is changed, never your project's files. Anything that is not exactly identical is left alone.

## Audio

A stereo clip whose two channels are identical carries the same sound twice. Arclight stores it as mono with +3 dB, which plays at the same level and balance (measured through Unity's mixer and the Oculus and Steam Audio spatializers VRChat uses). Vorbis clips halve in size.

This only happens when the clip leaves room for the +3 dB without clipping, and every audio source playing it has a **VRC Spatial Audio Source** that is either spatialized or plain 2D with no pan, and no animation changes those settings. Anything else stays stereo.

## Supported shaders

| Shader | Support |
| --- | --- |
| lilToon 2.x | All variants |
| Poiyomi 9.x–12.x | Locked and unlocked |
| VRChat mobile shaders | Toon Standard, Toon Lit, Standard Lite and the others |
| NonToon 0.1.3 | Default modules |
| Sunao 1.6 | All variants except Fur |
| ORL Toon v1 and v2 | All variants |
| Mochie Standard 2.13 | Standard, Lite and Mobile (detail maps are kept) |
| Unity | Standard and Unlit/Texture |

## Limitations

- It does not resize or atlas textures. Apart from the smaller formats above, it keeps each texture's compression format.
- It reads 8-bit PNG, PSD, TGA, TIFF, BMP and JPEG sources. Normal maps must be PNG.
- Matcaps, ramps and textures with unknown sampling are kept, as are textures animated in ways it cannot follow.
- Distant mipmaps can still show some colour bleeding. Check your avatar at different distances.
- Quest/Android textures keep their formats, and Quest builds have not been tested on a device.
- **Allow Unsupported Shaders** assumes how a shader maps its textures and may cause artifacts. It applies only to shaders Arclight does not recognize; it never overrides what lilToon, Poiyomi or another supported shader keeps. Test before uploading.

## License

[MIT](LICENSE.md)
