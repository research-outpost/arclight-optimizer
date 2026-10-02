# Arclight Optimizer

Smaller VRChat avatar downloads, with no negative visual changes. It removes duplicate assets and strips data your materials and shaders never use.

## What it does

- **Clears unused texture areas** and rebuilds the padding around the parts your meshes use (see below).
- **Uses smaller texture formats on PC** where your shader never reads a channel.
- **Merges duplicates**, so identical textures, materials, clips, meshes and audio are uploaded once.
- **Shrinks mesh index buffers** from 32-bit to 16-bit where they fit.
- **Stores identical-channel stereo audio as mono**, at the same loudness.
- **Trims animation keys** that change nothing about playback.

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

Everything is automatic. The only option is **Allow unsupported shaders** (see Limitations). To skip the optimizer for an avatar, disable or remove the component.

After a build, click **Open reports folder** on the component to see what changed and why any texture was kept.

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
| Unity | Standard and Unlit/Texture |

## Limitations

- It does not resize or atlas textures. Apart from the smaller formats above, it keeps each texture's compression format.
- It reads 8-bit PNG, PSD, TGA, TIFF, BMP and JPEG sources. Normal maps must be PNG.
- Matcaps, ramps and textures with unknown sampling are kept, as are textures animated in ways it cannot follow.
- Distant mipmaps can still show some colour bleeding. Check your avatar at different distances.
- Quest/Android textures keep their formats, and Quest builds have not been tested on a device.
- **Allow unsupported shaders** assumes how a shader maps its textures and may cause artifacts. Test before uploading.

## License

[MIT](LICENSE.md)
