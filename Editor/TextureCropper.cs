using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Shrinks textures whose sampled area (with the usual protected padding) fits in an aligned power-of-two part of the
    // image: the texture is cropped to that part and the tiling/offset that maps UVs onto it is rewritten, on build
    // copies of the materials, so every lookup reads the same texel of the same mip level. A crop of width w/2^k starting
    // at a multiple of w/2^k lines up with every mip level and compression block, so the cropped texture's levels are
    // the original's levels cut to that part. Only the original's last levels, where the whole part is a few texels or
    // less, are missing; the GPU then uses the crop's last level. That differs only where the part covers a few pixels
    // on screen.
    // Requirements, all checked:
    //  - Every lookup of every texture involved goes through an adapter-audited tiling/offset ("basis") that the shader
    //    reads for nothing but texture lookups (lilToon, Standard, Unlit/Texture), unanimated, with UVs inside the crop
    //    (no wrapping); every texture read through a rewritten basis is cropped the same way, and no lookup also reads
    //    another rewritten basis.
    //  - PNG power-of-two sources with box-filtered mipmaps and without crunch, coverage-preserving or border mipmaps,
    //    height-to-normal conversion or alpha-is-transparency dilation, all of which read beyond the crop; the crop's import
    //    gets a max size that keeps the original's import scale, and is checked after import.
    // Textures and materials linked through shared textures or bases are decided together. Materials an animation swaps
    // in keep their original textures. Generated crops are cached by source content and crop.
    internal static class TextureCropper
    {
        internal sealed class Result { public int Textures, Uniform, Materials; public long PixelsBefore, PixelsAfter; }

        private sealed class Crop { public int Kx, Ky, Jx, Jy; } // Size 2^-k of the texture per axis, cell index j.

        internal static Result Run(GameObject root, ScanResult scan, AvatarAnalysis analysis, Action<Object, Object> register, string folder)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            // Union-find over textures and (material, basis) pairs.
            var parent = new Dictionary<object, object>();
            object Find(object x) { if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; } return p == x ? x : parent[x] = Find(p); }
            void Union(object a, object b) => parent[Find(a)] = Find(b);
            var bad = new HashSet<object>();
            var groups = new Dictionary<Texture, TextureGroup>();
            var bases = new HashSet<(Material, string)>();
            var paths = new List<(TextureUsageRecord Use, SamplingPath Path)>();

            foreach (var group in scan.Groups)
            {
                if (!(group.Source is Texture2D)) continue;
                groups[group.Source] = group;
                Find(group.Source);
                if (group.Warning != null || scan.Duplicates.ContainsValue(group.Source)) bad.Add(group.Source);
                foreach (var use in group.Uses)
                {
                    if (use.FromAnimation || use.Sampling.NotSampled) continue; // Swapped-in materials keep their textures.
                    if (!use.Sampling.Supported || use.Texture != group.Source || !use.Material) { bad.Add(group.Source); continue; }
                    foreach (var path in use.Sampling.GetPaths())
                    {
                        if (path.Basis == null || path.AnimatedTransforms != null) { bad.Add(group.Source); continue; }
                        var node = (use.Material, path.Basis);
                        bases.Add(node);
                        Union(group.Source, node);
                        paths.Add((use, path));
                    }
                }
            }
            // An animated texture swap reads the swapped-in texture through the same, rewritten tiling/offset.
            foreach (var use in scan.Groups.SelectMany(g => g.Uses))
                if (use.FromAnimation && use.Material && use.Material.HasProperty(use.Property) && use.Material.GetTexture(use.Property) != use.Texture)
                    foreach (var node in bases.Where(b => b.Item1 == use.Material)) bad.Add(node);
            // A material is cropped only if every texture it holds takes part (unless never sampled).
            foreach (var material in bases.Select(b => b.Item1).Distinct())
                foreach (string property in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(property);
                    if (!texture) continue;
                    if (!groups.TryGetValue(texture, out var group)) { bad.Add(bases.First(b => b.Item1 == material)); continue; }
                    if (group.ActiveUses.Any()) Union(texture, bases.First(b => b.Item1 == material)); // Never-sampled textures stay as they are.
                }
            // Coverage only knows mesh and skinned mesh renderers.
            foreach (var renderer in renderers.Where(r => !(r is MeshRenderer) && !(r is SkinnedMeshRenderer)))
                foreach (var material in renderer.sharedMaterials)
                    foreach (var node in bases.Where(b => b.Item1 == material)) bad.Add(node);
            foreach (var x in bad.ToList()) bad.Add(Find(x));

            var croppedSources = new HashSet<Texture>();
            // Textures of one material often share every sampling path (a main map and its normal map), so their coverage is built once.
            var coverage = new UvCoverageCache();
            var components = parent.Keys.ToList().GroupBy(Find).Where(c => !bad.Contains(c.Key)).ToList();
            foreach (var component in components)
            {
                var textures = component.OfType<Texture2D>().ToList();
                var rewritten = new HashSet<(Material, string)>(component.OfType<ValueTuple<Material, string>>());
                if (textures.Count == 0 || rewritten.Count == 0) continue;
                var componentPaths = paths.Where(p => rewritten.Contains((p.Use.Material, p.Path.Basis))).ToList();
                // No lookup may also read another tiling/offset that is being rewritten.
                if (componentPaths.Any(p => (p.Path.Dependencies ?? new[] { p.Path.Basis }).Any(d => d != p.Path.Basis && rewritten.Contains((p.Use.Material, d))))) continue;
                if (rewritten.Any(b => renderers.Any(r => r.sharedMaterials.Contains(b.Item1) &&
                    analysis.AnimatedMaterialProperties(r).Any(p => p == b.Item2 + "_ST")))) continue;
                Crop crop;
                try { crop = Plan(textures, groups, componentPaths, coverage); }
                catch (Exception) { continue; }
                if (crop == null) continue;

                var cropped = new Dictionary<Texture, Texture2D>();
                try
                {
                    foreach (var texture in textures) cropped[texture] = Generate(texture, crop, folder);
                }
                catch (Exception e)
                {
                    Debug.Log("Arclight Optimizer: texture crop skipped for " + string.Join(", ", textures.Select(t => t.name)) + ": " + e.Message);
                    continue;
                }

                foreach (var material in rewritten.Select(b => b.Item1).Distinct().ToList())
                {
                    var copy = new Material(material) { name = material.name };
                    foreach (string property in material.GetTexturePropertyNames())
                        if (material.GetTexture(property) is Texture t && t && cropped.TryGetValue(t, out var c)) copy.SetTexture(property, c);
                    foreach (var basis in rewritten.Where(b => b.Item1 == material).Select(b => b.Item2))
                    {
                        // New lookup = (old - o) / s per axis: scale / s, (offset - o) / s.
                        float sx = 1f / (1 << crop.Kx), sy = 1f / (1 << crop.Ky);
                        Vector2 scale = material.GetTextureScale(basis), offset = material.GetTextureOffset(basis);
                        copy.SetTextureScale(basis, new Vector2(scale.x / sx, scale.y / sy));
                        copy.SetTextureOffset(basis, new Vector2((offset.x - crop.Jx * sx) / sx, (offset.y - crop.Jy * sy) / sy));
                    }
                    register(material, copy);
                    foreach (var renderer in renderers)
                    {
                        var slots = renderer.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < slots.Length; i++) if (slots[i] == material) { slots[i] = copy; changed = true; }
                        if (changed) renderer.sharedMaterials = slots;
                    }
                    result.Materials++;
                }
                foreach (var pair in cropped)
                {
                    register(pair.Key, pair.Value);
                    croppedSources.Add(pair.Key);
                    result.Textures++;
                    result.PixelsBefore += (long)pair.Key.width * pair.Key.height;
                    result.PixelsAfter += (long)pair.Value.width * pair.Value.height;
                }
            }
            CollapseUniform(renderers, scan, croppedSources, register, folder, result);
            return result;
        }

        // A texture that is one colour everywhere (often what clearing leaves) reads that colour at every lookup, mip
        // level and filter. A 4 x 4 copy with the same import settings decodes to the same value at every level (probed
        // for BC1, DXT5, BC4, BC5 and BC7, sRGB and linear, and checked on every copy by sampling both on the GPU), so it
        // replaces the texture without any UV change. On PC only for audited lilToon uses, lilSSAO excluded: lilToon never reads a
        // material texture's size. On Android only for VRChat's pinned mobile shaders (no material texture's size is read: the
        // bicubic helper in VRChat.cginc reads only lightmaps, and AudioLink only its global texture) and only for uncompressed formats or square
        // blocks of the sizes the ASTC probe passed (4 x 4, 6 x 6, 8 x 8; UniformTextureTests). ETC2 and 4-bit PVRTC also use 4 x 4 blocks;
        // every copy is checked on the GPU against its original before use.
        private static readonly int[] ProbedAstcBlocks = { 1, 4, 6, 8 };
        private static void CollapseUniform(Renderer[] renderers, ScanResult scan, HashSet<Texture> cropped, Action<Object, Object> register, string folder, Result result)
        {
            bool android = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
            if (!GeneratedTargetValidator.IsStandalone && !android) return;
            var collapsed = new Dictionary<Texture, Texture2D>();
            // The scan only audits mesh and skinned mesh renderers that are not excluded; a texture any other renderer holds stays.
            var unaudited = new HashSet<Texture>(renderers.Where(r => !(r is MeshRenderer) && !(r is SkinnedMeshRenderer) || Exclusions.Excluded(r))
                .SelectMany(r => r.sharedMaterials).Where(m => m).SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture)).Where(t => t));
            foreach (var group in scan.Groups)
            {
                if (!(group.Source is Texture2D texture) || cropped.Contains(texture) || unaudited.Contains(texture) || group.Warning != null || scan.Duplicates.ContainsValue(texture)) continue;
                if (group.Uses.Count == 0 || group.Uses.Any(u => !u.Material || !u.Sampling.Supported || (android
                        ? u.Sampling.AdapterId != VRChatMobileAdapter.Id
                        : !(u.Sampling.AdapterId ?? "").StartsWith(LilToonAdapter.Id + "/", StringComparison.Ordinal) || u.Sampling.AdapterId.Contains("/lilssao")))) continue;
                if (android && (!ProbedAstcBlocks.Contains((int)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetBlockWidth(texture.graphicsFormat)) ||
                        UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetBlockWidth(texture.graphicsFormat) != UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetBlockHeight(texture.graphicsFormat))) continue;
                string path = AssetDatabase.GetAssetPath(texture);
                if (!SourceImages.IsPng(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
                // Each of these makes mip levels differ from a plain average, or reads something other than the pixels.
                if (importer.mipMapsPreserveCoverage || importer.borderMipmap || importer.fadeout || importer.crunchedCompression ||
                    importer.convertToNormalmap || importer.alphaIsTransparency || importer.mipmapFilter != TextureImporterMipFilter.BoxFilter) continue;
                if (new[] { "Standalone", "Android" }.Select(importer.GetPlatformTextureSettings).Any(s => s.overridden && s.crunchedCompression)) continue;
                var header = PngPixels.InspectHeader(path);
                if (header.BitDepth > 8 || header.Width * header.Height <= 16) continue;
                var bytes = File.ReadAllBytes(LongPath.For(path));
                string hash = NotUniform.Key(bytes);
                if (NotUniform.Known(hash)) continue; // Decoded before with this decoder: more than one colour.
                var pixels = PngPixels.Decode(bytes, out var info);
                if (pixels.Any(p => !p.Equals(pixels[0]))) { NotUniform.Add(hash); continue; }
                try { collapsed[texture] = GenerateUniform(texture, bytes, pixels[0], info, folder); }
                catch (Exception e) { Debug.Log("Arclight Optimizer: one-colour texture kept for " + texture.name + ": " + e.Message); }
            }
            if (collapsed.Count == 0) return;
            var copies = new Dictionary<Material, Material>();
            foreach (var renderer in renderers.Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && !Exclusions.Excluded(r)))
            {
                var slots = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    var material = slots[i];
                    if (!material || !material.GetTexturePropertyNames().Any(p => material.GetTexture(p) is Texture t && t && collapsed.ContainsKey(t))) continue;
                    if (!copies.TryGetValue(material, out var copy))
                    {
                        copy = new Material(material) { name = material.name };
                        foreach (string property in material.GetTexturePropertyNames())
                            if (material.GetTexture(property) is Texture t && t && collapsed.TryGetValue(t, out var small)) copy.SetTexture(property, small);
                        register(material, copy);
                        copies[material] = copy;
                        result.Materials++;
                    }
                    slots[i] = copy;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = slots;
            }
            foreach (var pair in collapsed)
            {
                register(pair.Key, pair.Value);
                result.Uniform++;
                result.PixelsBefore += (long)pair.Key.width * pair.Key.height;
                result.PixelsAfter += (long)pair.Value.width * pair.Value.height;
            }
        }

        // The 4 x 4 one-colour copy (cached), imported with the source's settings.
        private static Texture2D GenerateUniform(Texture2D texture, byte[] bytes, Color32 colour, PngInfo info, string folder)
        {
            string sourcePath = AssetDatabase.GetAssetPath(texture);
            string recipe = FingerprintService.Hash(bytes.Concat(System.Text.Encoding.UTF8.GetBytes(
                File.ReadAllText(LongPath.For(sourcePath + ".meta")) + "|uniform-v2")).ToArray());
            string userData = "ArclightCrop:" + recipe;
            folder += "/Crops";
            AutomaticTextureOptimizer.EnsureFolder(folder);
            string outPath = GenerationCoordinator.OutputPath(folder, texture.name, recipe);
            if (File.Exists(outPath) && AssetImporter.GetAtPath(outPath) is TextureImporter cachedImporter && cachedImporter.userData == userData)
            {
                var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
                if (cached && cached.width == 4 && cached.height == 4 && Checked(texture, cached, outPath)) { TextureSafety.DropReadable(cachedImporter); AudioMonoConverter.MarkUsed(outPath); return cached; } // Copies made before 1.1.13 lose Read/Write.
            }
            if (File.Exists(outPath) || File.Exists(outPath + ".meta")) outPath = AssetDatabase.GenerateUniqueAssetPath(outPath);

            var small = Enumerable.Repeat(colour, 16).ToArray();
            var smallInfo = new PngInfo { Width = 4, Height = 4, BitDepth = info.BitDepth, ColorType = info.ColorType, Alpha = info.Alpha };
            smallInfo.ColourMetadata.AddRange(info.ColourMetadata);
            var png = PngPixels.Encode(small, smallInfo);
            if (!PngPixels.Decode(png, out _).SequenceEqual(small)) throw new InvalidOperationException("PNG round-trip changed the colour.");
            string meta = GenerationCoordinator.CopiedImporterMeta(sourcePath + ".meta", userData);
            if (meta == null) throw new InvalidOperationException("The source importer settings could not be copied.");
            File.WriteAllBytes(outPath, png);
            File.WriteAllText(outPath + ".meta", meta, new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (!imported || imported.width != 4 || imported.height != 4 || imported.mipmapCount > 1 != texture.mipmapCount > 1 ||
                imported.format != texture.format || imported.wrapModeU != texture.wrapModeU || imported.wrapModeV != texture.wrapModeV ||
                imported.filterMode != texture.filterMode || imported.anisoLevel != texture.anisoLevel)
            {
                AssetDatabase.DeleteAsset(outPath);
                throw new InvalidOperationException("The one-colour copy did not import with the original's settings.");
            }
            if (!Checked(texture, imported, outPath)) throw new InvalidOperationException("The one-colour copy decodes to a different value.");
            TextureSafety.DropReadable((TextureImporter)AssetImporter.GetAtPath(outPath));
            AudioMonoConverter.MarkUsed(outPath);
            return imported;
        }

        // Whether the copy reads the same as the original on the GPU; a copy that does not (or cannot be checked) is deleted.
        private static bool Checked(Texture original, Texture copy, string path)
        {
            bool same = false;
            try { same = SameDecoded(original, copy); }
            finally { if (!same) AssetDatabase.DeleteAsset(path); }
            return same;
        }

        // The GPU's decode of the crop against the same part of the original, texel for texel at level 0 and at each level whose
        // part is still at least 4 texels on both axes (the levels a crop keeps exact). Each target pixel lands on a texel centre,
        // so the read is that texel at that level.
        private static bool SameRegion(Texture2D original, Texture2D cropped, Crop crop)
        {
            var scale = new Vector2(1f / (1 << crop.Kx), 1f / (1 << crop.Ky));
            var offset = new Vector2(crop.Jx * scale.x, crop.Jy * scale.y);
            for (int level = 0; level < cropped.mipmapCount; level++)
            {
                int w = cropped.width >> level, h = cropped.height >> level;
                if (w < 4 || h < 4) break;
                if (!Read(original, w, h, scale, offset).SequenceEqual(Read(cropped, w, h, Vector2.one, Vector2.zero))) return false;
            }
            return true;
        }

        private static Color[] Read(Texture texture, int w, int h, Vector2 scale, Vector2 offset)
        {
            var target = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var read = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(texture, target, scale, offset);
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply();
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(read);
            }
        }

        // True when the GPU decodes alpha 1 everywhere: level 0 in 512-texel tiles at 1:1, then the whole texture into 64, 4, 2 and
        // 1 pixels for the coarser levels. Guards DXT1 replacements a shader reads alpha from (Fable final review).
        internal static bool OpaqueOnGpu(Texture2D texture)
        {
            const int tile = 512;
            for (int y = 0; y < texture.height; y += tile)
                for (int x = 0; x < texture.width; x += tile)
                {
                    int w = Math.Min(tile, texture.width - x), h = Math.Min(tile, texture.height - y);
                    var scale = new Vector2((float)w / texture.width, (float)h / texture.height);
                    if (Read(texture, w, h, scale, new Vector2((float)x / texture.width, (float)y / texture.height)).Any(c => c.a != 1f)) return false;
                }
            return new[] { 64, 4, 2, 1 }.All(size => Sampled(texture, size, false).All(c => c.a == 1f));
        }

        // Draws both textures into float targets and compares what the GPU read: a 64-texel part at 1:1 (level 0), then
        // the whole texture into 64, 4, 2 and 1 pixels, so it samples ever coarser levels down to the last.
        private static bool SameDecoded(Texture a, Texture b)
        {
            if (!Sampled(a, 64, true).SequenceEqual(Sampled(b, 64, true))) return false;
            foreach (int size in new[] { 64, 4, 2, 1 })
                if (!Sampled(a, size, false).SequenceEqual(Sampled(b, size, false))) return false;
            return true;
        }

        private static Color[] Sampled(Texture texture, int size, bool part)
        {
            var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var read = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            try
            {
                if (part) Graphics.Blit(texture, target, new Vector2((float)size / texture.width, (float)size / texture.height), Vector2.zero);
                else Graphics.Blit(texture, target);
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                read.Apply();
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(read);
            }
        }

        // The deepest aligned crop holding every texture's protected texels and every lookup's UVs, or null.
        private static Crop Plan(List<Texture2D> textures, Dictionary<Texture, TextureGroup> groups, List<(TextureUsageRecord Use, SamplingPath Path)> paths,
            UvCoverageCache coverage)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            void Include(double ax, double ay, double bx, double by) { x0 = Math.Min(x0, ax); y0 = Math.Min(y0, ay); x1 = Math.Max(x1, bx); y1 = Math.Max(y1, by); }
            foreach (var (use, path) in paths)
            {
                var snapshot = use.ReadMesh(path.UvChannel);
                foreach (int index in snapshot.Indices)
                {
                    var uv = Vector2.Scale(snapshot.Uvs[index], path.Scale) + path.Offset;
                    Include(uv.x, uv.y, uv.x, uv.y);
                }
            }
            if (x0 < 0 || y0 < 0 || x1 > 1 || y1 > 1) return null; // Lookups that wrap cannot move.
            var sizes = new List<(int W, int H)>();
            foreach (var texture in textures)
            {
                string path = AssetDatabase.GetAssetPath(texture);
                if (!SourceImages.IsPng(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return null;
                // Each step here reads texels beyond the crop's edge (wider mip kernels, height-to-normal, alpha dilation).
                if (importer.mipMapsPreserveCoverage || importer.borderMipmap || importer.crunchedCompression || importer.mipmapFilter != TextureImporterMipFilter.BoxFilter ||
                    importer.convertToNormalmap || importer.alphaIsTransparency) return null;
                if (new[] { "Standalone", "Android" }.Select(importer.GetPlatformTextureSettings).Any(s => s.overridden && s.crunchedCompression)) return null;
                var info = PngPixels.InspectHeader(path);
                if (info.BitDepth > 8) return null; // 16-bit sources are only proven equal once rounded for whole images (see PngPixels.RoundedImportMatches).
                if (!Mathf.IsPowerOfTwo(info.Width) || !Mathf.IsPowerOfTwo(info.Height) || !Mathf.IsPowerOfTwo(texture.width) || !Mathf.IsPowerOfTwo(texture.height)) return null;
                // Crops keep 4 x 4 compression blocks whole. ASTC's other block sizes (5 to 12 texels, on Android) do not divide
                // a power-of-two crop, so every block would be encoded again from different texels.
                int blockW = (int)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetBlockWidth(texture.graphicsFormat);
                int blockH = (int)UnityEngine.Experimental.Rendering.GraphicsFormatUtility.GetBlockHeight(texture.graphicsFormat);
                if (blockW != 1 && blockW != 4 || blockH != 1 && blockH != 4) return null;
                // PVRTC also uses 4-texel blocks but blends neighbouring blocks, so a crop is never exact there.
                if (UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsPVRTCFormat(texture.graphicsFormat)) return null;
                var mask = UvCoverageRasterizer.Build(groups[texture], info.Width, info.Height, out _, coverage);
                int mx0 = int.MaxValue, my0 = int.MaxValue, mx1 = -1, my1 = -1;
                for (int y = 0; y < info.Height; y++)
                    for (int x = 0; x < info.Width; x++)
                        if (mask[y * info.Width + x]) { mx0 = Math.Min(mx0, x); mx1 = Math.Max(mx1, x); my0 = Math.Min(my0, y); my1 = Math.Max(my1, y); }
                if (mx1 < 0) return null;
                Include((double)mx0 / info.Width, (double)my0 / info.Height, (double)(mx1 + 1) / info.Width, (double)(my1 + 1) / info.Height);
                sizes.Add((info.Width, info.Height));
                sizes.Add((texture.width, texture.height)); // The imported size must also divide.
            }
            int Depth(double a, double b, Func<(int W, int H), int> size)
            {
                int k = 0;
                // Deeper while the next level still holds [a, b] in one cell and every texture keeps 4-texel blocks.
                while (k < 12 && Math.Floor(a * (1 << (k + 1))) == Math.Floor((b - 1e-9) * (1 << (k + 1))) &&
                       sizes.All(s => size(s) >> (k + 1) >= 4)) k++;
                return k;
            }
            var crop = new Crop { Kx = Depth(x0, x1, s => s.W), Ky = Depth(y0, y1, s => s.H) };
            if (crop.Kx == 0 && crop.Ky == 0) return null;
            crop.Jx = (int)Math.Floor(x0 * (1 << crop.Kx));
            crop.Jy = (int)Math.Floor(y0 * (1 << crop.Ky));
            return crop;
        }

        // The cropped copy of the texture (cached), imported with its settings and a max size keeping its import scale.
        private static Texture2D Generate(Texture2D texture, Crop crop, string folder)
        {
            string sourcePath = AssetDatabase.GetAssetPath(texture);
            var bytes = File.ReadAllBytes(LongPath.For(sourcePath));
            string recipe = FingerprintService.Hash(bytes.Concat(System.Text.Encoding.UTF8.GetBytes(
                File.ReadAllText(LongPath.For(sourcePath + ".meta")) + "|" + crop.Kx + "," + crop.Ky + "," + crop.Jx + "," + crop.Jy + "|crop-v2")).ToArray());
            string userData = "ArclightCrop:" + recipe;
            int importedW = texture.width >> crop.Kx, importedH = texture.height >> crop.Ky;
            folder += "/Crops";
            AutomaticTextureOptimizer.EnsureFolder(folder);
            string outPath = GenerationCoordinator.OutputPath(folder, texture.name, recipe);
            if (File.Exists(outPath) && AssetImporter.GetAtPath(outPath) is TextureImporter cachedImporter && cachedImporter.userData == userData)
            {
                // A cached crop passes the same import and GPU checks as a new one (it may have been edited, or imported for
                // another platform); otherwise it is made again.
                var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
                if (cached && SameImport(cached, texture, importedW, importedH) && SameRegion(texture, cached, crop)) { TextureSafety.DropReadable(cachedImporter); AudioMonoConverter.MarkUsed(outPath); return cached; } // Copies made before 1.1.13 lose Read/Write.
                AssetDatabase.DeleteAsset(outPath);
            }
            if (File.Exists(outPath) || File.Exists(outPath + ".meta")) outPath = AssetDatabase.GenerateUniqueAssetPath(outPath);

            var pixels = PngPixels.Decode(bytes, out var info);
            int w = info.Width >> crop.Kx, h = info.Height >> crop.Ky, ox = crop.Jx * w, oy = crop.Jy * h;
            var cut = new Color32[w * h];
            for (int y = 0; y < h; y++) Array.Copy(pixels, (oy + y) * info.Width + ox, cut, y * w, w);
            var cutInfo = new PngInfo { Width = w, Height = h, BitDepth = info.BitDepth, ColorType = info.ColorType, Alpha = info.Alpha };
            cutInfo.ColourMetadata.AddRange(info.ColourMetadata);
            var png = PngPixels.Encode(cut, cutInfo);
            if (!PngPixels.Decode(png, out _).SequenceEqual(cut)) throw new InvalidOperationException("PNG round-trip changed the cropped pixels.");

            string meta = GenerationCoordinator.CopiedImporterMeta(sourcePath + ".meta", userData);
            if (meta == null) throw new InvalidOperationException("The source importer settings could not be copied.");
            // Max size caps the longer side; keep the original's import scale for the part.
            int maxSize = Math.Max(importedW, importedH);
            if (maxSize < 32) throw new InvalidOperationException("The crop would import below Unity's smallest max size.");
            meta = System.Text.RegularExpressions.Regex.Replace(meta, @"(?m)^(\s*maxTextureSize: )\d+", "${1}" + maxSize);
            File.WriteAllBytes(outPath, png);
            File.WriteAllText(outPath + ".meta", meta, new System.Text.UTF8Encoding(false));
            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            if (!imported || !SameImport(imported, texture, importedW, importedH))
            {
                AssetDatabase.DeleteAsset(outPath);
                throw new InvalidOperationException("The cropped texture did not import with the original's settings and scale.");
            }
            // The size and format checks cover the known causes of a mismatch (block alignment, import scale); this covers the rest.
            bool same = false;
            try { same = SameRegion(texture, imported, crop); }
            finally { if (!same) AssetDatabase.DeleteAsset(outPath); }
            if (!same) throw new InvalidOperationException("The cropped texture decodes differently from that part of the original.");
            TextureSafety.DropReadable((TextureImporter)AssetImporter.GetAtPath(outPath));
            AudioMonoConverter.MarkUsed(outPath); // Starts its 30 days for the cache cleanup.
            return imported;
        }

        private static bool SameImport(Texture2D crop, Texture2D texture, int width, int height) =>
            crop.width == width && crop.height == height && crop.mipmapCount > 1 == texture.mipmapCount > 1 && crop.format == texture.format &&
            crop.graphicsFormat == texture.graphicsFormat && crop.wrapModeU == texture.wrapModeU && crop.wrapModeV == texture.wrapModeV &&
            crop.filterMode == texture.filterMode && crop.anisoLevel == texture.anisoLevel;

        internal static bool IsGenerated(string path) =>
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && AssetImporter.GetAtPath(path) is TextureImporter importer &&
            (importer.userData ?? "").StartsWith("ArclightCrop:", StringComparison.Ordinal);
    }

    // PNG contents already decoded and found to hold more than one colour, so a repeat build skips decoding them again.
    // Keyed by the file bytes and the decoder; only that negative answer is kept, so every texture that
    // might collapse is still decoded and checked in full.
    internal static class NotUniform
    {
        private const string FileName = "Library/AvatarTextureOptimizer/not-uniform.txt";
        private static HashSet<string> known;

        internal static string Key(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return "png-v1:" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); // png-v1: Arclight's PngPixels decoder.
        }

        internal static bool Known(string key) => Load().Contains(key);

        internal static void Add(string key)
        {
            if (!Load().Add(key)) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(FileName)); File.AppendAllText(FileName, key + "\n"); }
            catch (IOException) { } // A cache only: the next build decodes again.
        }

        private static HashSet<string> Load()
        {
            if (known != null) return known;
            known = new HashSet<string>(StringComparer.Ordinal);
            try { if (File.Exists(FileName)) known.UnionWith(File.ReadAllLines(FileName).Where(l => l.Length > 0)); }
            catch (IOException) { }
            return known;
        }
    }
}
