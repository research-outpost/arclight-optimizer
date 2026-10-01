# Arclight Optimizer

Smaller VRChat avatar downloads, with no visible change up close.

It does two things:

- **Clears unused texture areas.** Most avatar textures contain areas that no part of the model ever shows. Arclight keeps the pixels your meshes use, adds protective padding, and fills the rest with a flat colour that compresses far better.
- **Merges duplicates**, so the same data is uploaded once (see below).

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

Outfits and avatar bases often include the same data under different names. With **Merge duplicate textures** and **Merge duplicates** on (both are on by default), these share one copy:

- **Textures** with identical pixels, colour metadata and import settings, including animated texture swaps.
- **Materials** with identical shaders, properties and keywords.
- **Animation clips** with identical curves, events and settings. This usually saves only a few kilobytes.

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

## Limitations

- It does not resize, recompress or atlas textures, and it does not reduce VRAM use.
- It reads 8-bit PNG, PSD, TGA, TIFF, BMP and JPEG sources. Normal maps must be PNG.
- Matcaps, ramps and textures with unknown sampling are kept, as are textures animated in ways it cannot follow.
- Distant mipmaps can still show some colour bleeding. Check your avatar at different distances.
- **Allow unsupported shaders** assumes how a shader maps its textures and may cause artifacts. Test before uploading.

## License

[MIT](LICENSE.md)
