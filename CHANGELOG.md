# Changelog

What changed in each version, in plain words. Commits have the technical detail.

## 1.7.8 (2026-11-27)
- First builds are faster, up to about twice as fast on locked Poiyomi and lilToon avatars. Later builds were already fast.
- Poiyomi 11 materials with outlines off, or without a curvature bake, get their textures optimized again.

## 1.7.7 (2026-11-17)
- Reports show how much space smaller menu icons save, and never show a negative saving for PNGs.
- When a texture is left at its size, the report says why.
- New Analyzer card for textures that are almost fully transparent.
- Decals and scrolling textures that can't be optimized now say why.

## 1.7.6 (2026-11-16)
- You can keep single textures out of the size cap and out of colour narrowing.
- The object exclude list is now called Exclude Objects.
- For developers: the code namespace is now `ResearchOutpost`. Scripts that use the old names need updating.

## 1.7.5 (2026-11-16)
- More Poiyomi 11 textures can be made smaller.
- Support for more Poiyomi Pro features, including SSAO and constellation.

## 1.7.4 (2026-11-16)
- Poiyomi 11 textures can be cropped.
- Six more Poiyomi settings are recognised.
- Parallax set to zero strength no longer holds a texture back.

## 1.7.3 (2026-11-16)
- Reports say "1 texture" instead of "1 textures".

## 1.7.2 (2026-11-16)
- Works with newer Poiyomi (10.0.26 and later) and Poiyomi Pro 9.3 and 11.
- Adds support for Mochie Uber, ORL, Sunao (including fur), Unity Toon, UnlitWF and Filamented shaders.

## 1.7.1 (2026-11-16)
- Merges blend shapes that always move together.
- Pauses hidden PhysBones, including ones on animated bones.
- Removes unused built-in parameters from each animator controller.
- Reports are written in plain language.

## 1.7.0 (2026-11-14)
- Caps texture size and menu icon size.
- Optional, off by default: stronger compression for opaque textures. It can show banding, so check your avatar up close.
- Cleans up unused light cookies and blend shape frames.
- Fixes to the order optimizations run in.
