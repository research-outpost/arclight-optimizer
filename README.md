# Arclight Optimizer

Smaller VRChat avatar downloads, with no visible change up close.

It does five things:

- **Clears unused texture areas.** Most avatar textures contain areas that no part of the model ever shows. Arclight keeps the pixels your meshes use, adds protective padding, and fills the rest with a flat colour that compresses far better.
- **Merges duplicates**, so the same data is uploaded once (see below).
- **Shrinks mesh index buffers.** A mesh whose 32-bit index buffer fits in 16 bits gets a 16-bit one: identical triangles, half the index data.
- **Stores identical-channel stereo audio as mono.** The mono copy gets +3 dB, so it plays at the same level and balance (measured through Unity and VRChat's Steam Audio spatializer). Only where the audio source's setup has been measured to match and the clip has headroom; Vorbis clips halve in size.
- **Drops channels your shader never reads (PC).** A DXT5 texture whose alpha the shader ignores is imported as DXT1, which stores the colour the same way at half the size, in the download and in VRAM. A linear mask the shader reads through one red channel becomes BC4, which keeps that channel more precisely (smaller than DXT5, the same size as DXT1). This only happens where the shader's code has been checked (see below).

It runs automatically when you build or enter Play Mode, on a temporary copy. Your textures, materials and scenes are never modified.

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

After a build, click **Open reports folder** on the component to see what changed and why any texture was kept.

## Merging duplicates

Outfits and avatar bases often include the same data under different names. With **Merge duplicates** on (the default), these share one copy:

- **Textures** with identical pixels, colour metadata and import settings, including animated texture swaps.
- **Materials** with identical shaders, properties and keywords.
- **Animation clips** with identical curves, events and settings. This usually saves only a few kilobytes.
- **Meshes** with identical vertices, triangles, bone weights and blend shapes.
- **Audio clips** with identical files and import settings.

Texture merging needs **Optimize textures** on as well.

Only the avatar being built is changed, never your project's files. Anything that is not exactly identical is left alone.

## Supported shaders

| Shader | Support |
| --- | --- |
| lilToon 2.x | All variants |
| Poiyomi 9.x–12.x | Locked and unlocked |
| VRChat mobile shaders | Toon Standard, Toon Lit, Standard Lite and the others |
| NonToon 0.1.3 | Default modules |
| Unity | Standard and Unlit/Texture |

A texture is only replaced when its estimated compressed size in the avatar bundle gets smaller.

## Smaller formats without quality loss

On PC builds, a texture only changes format when every shader that uses it never reads the dropped channels. This was checked in each shader's source:

- **Unity Standard and Unlit/Texture:** opaque main textures, emission and detail colour, and single-channel maps.
- **lilToon 2.x:** the main texture on opaque shaders, plus its red-channel masks.
- **Poiyomi 10–12:** the main texture with **Force Opaque** or **Ignore Main Texture Alpha** on (unless alpha changes colour, such as with premultiply), masks that read one fixed or selected channel, and several colour maps. Lil Fur and Grab Pass keep alpha.
- **VRChat mobile shaders:** main textures that ignore alpha, Standard Lite maps, and Toon Standard's main texture and selected-channel masks.
- **NonToon:** the base texture in Opaque mode or on fur, the shared mask's selected channels, the SDF map and the fur noise mask.

Anything else keeps its format, as do BC7, crunched and uncompressed textures and every Quest/Android texture. Generated textures are rebuilt once after updating.

## Limitations

- It does not resize or atlas textures. Apart from the channel formats above, it keeps each texture's compression format.
- It reads 8-bit PNG, PSD, TGA, TIFF, BMP and JPEG sources. Normal maps must be PNG.
- Matcaps, ramps and textures with unknown sampling are kept, as are textures animated in ways it cannot follow.
- Distant mipmaps can still show some colour bleeding. Check your avatar at different distances.
- **Allow unsupported shaders** assumes how a shader maps its textures and may cause artifacts. Test before uploading.

## License

[MIT](LICENSE.md)
