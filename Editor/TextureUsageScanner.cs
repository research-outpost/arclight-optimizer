using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    public sealed class TextureUsageRecord
    {
        public Renderer Renderer;
        public Material Material;
        public int Slot;
        public string Property;
        public Texture Texture;
        public Mesh Mesh;
        public int Submesh;
        public SamplingDescription Sampling;
        public string Warning;
        public string RendererPath;
        public bool FromAnimation;
        internal MeshSnapshotCache MeshCache;

        internal MeshSnapshot ReadMesh(int channel) =>
            MeshCache != null ? MeshCache.Read(Mesh, Submesh, channel) : MeshSnapshot.Read(Mesh, Submesh, channel);
    }

    public sealed class TextureGroup
    {
        public Texture Source;
        public readonly List<TextureUsageRecord> Uses = new List<TextureUsageRecord>();
        public string Warning;
        public string Status;
        internal bool RepairsPadding => TextureUsageScanner.RepairMipmapPadding && ActiveUses.Any() &&
            ActiveUses.All(use => use.Sampling.Semantics == TextureSemantics.Color);
        // Computed once per scan by FingerprintService; a new scan starts fresh.
        internal string CachedRecipe;
        internal string CachedSourceHash;
        public IEnumerable<TextureUsageRecord> ActiveUses => Uses.Where(u => !u.Sampling.NotSampled);
    }

    public sealed class ScanResult
    {
        public readonly List<TextureGroup> Groups = new List<TextureGroup>();
        public readonly List<string> Warnings = new List<string>();
        // Duplicate texture -> the texture whose group now holds its uses (see MergeDuplicates).
        public readonly Dictionary<Texture, Texture> Duplicates = new Dictionary<Texture, Texture>();
        internal AnimationSnapshot Animation;
        internal readonly MeshSnapshotCache Meshes = new MeshSnapshotCache();
    }

    public static class TextureUsageScanner
    {
        // Colour textures always get their mipmap padding rebuilt; it gave the smallest downloads on every avatar
        // measured. This switch exists only so tests written for the plain-padding path can keep using it.
        internal const bool RepairMipmapPaddingByDefault = true;
        internal static bool RepairMipmapPadding = RepairMipmapPaddingByDefault;

        public static ScanResult Scan(GameObject root) => Scan(root, null);

        // The NDMF overload reads the already-active virtual controller context. The no-context
        // overload remains useful for editor tooling and deliberately cannot rewrite source clips.
        internal static ScanResult Scan(GameObject root, VirtualControllerContext animationContext,
            AnimationSnapshot serializedSnapshot = null, OptimizationProgress progress = null)
        {
            if (!root) throw new ArgumentNullException(nameof(root));
            ShaderAdapterRegistry.BeginScan();
            var result = new ScanResult();
            var groups = new Dictionary<Texture, TextureGroup>();
            var seenUses = new HashSet<string>(StringComparer.Ordinal);
            var config = root.GetComponent<AvatarTextureOptimizer>();
            bool allowUnsupportedShaders = config && config.allowUnsupportedShaders;
            progress?.Scanning(null, "Scanning animation data");
            var animation = AnimationSnapshot.Analyze(root, animationContext, result.Warnings, serializedSnapshot);
            progress?.CheckCancelled();
            result.Animation = animation;

            progress?.Scanning(null, "Scanning renderer uses");
            var renderers = root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
                .ToArray();
            string d4rkWarning = D4rkOrdering.RanBeforeWarning(renderers);
            if (d4rkWarning != null) result.Warnings.Add(d4rkWarning);
            foreach (var renderer in renderers)
            {
                progress?.CheckCancelled();
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh :
                    renderer.GetComponent<MeshFilter>()?.sharedMesh;
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
                if (!mesh) result.Warnings.Add($"{path}: renderer has no mesh.");
                bool ambiguousSlots = mesh && renderer.sharedMaterials.Length < mesh.subMeshCount;
                if (ambiguousSlots) result.Warnings.Add($"{path}: fewer material slots than submeshes; retained.");

                animation.Renderers.TryGetValue(path, out var state);
                var currentMaterials = renderer.sharedMaterials;
                int slotCount = currentMaterials.Length;
                if (state != null && state.SlotMaterials.Count > 0)
                    slotCount = Math.Max(slotCount, state.SlotMaterials.Keys.Max() + 1);
                for (int slot = 0; slot < slotCount; slot++)
                {
                    progress?.CheckCancelled();
                    var material = slot < currentMaterials.Length ? currentMaterials[slot] : null;
                    AddMaterialUses(renderer, path, mesh, slot, material, false, null,
                        ambiguousSlots, allowUnsupportedShaders, groups, result, seenUses, progress);

                    if (state != null && state.SlotMaterials.TryGetValue(slot, out var candidates))
                    {
                        foreach (var candidate in candidates.OrderBy(m => MaterialIdentity(m), StringComparer.Ordinal))
                            AddMaterialUses(renderer, path, mesh, slot, candidate, true, state,
                                ambiguousSlots, allowUnsupportedShaders, groups, result, seenUses, progress);
                    }
                }

                // A texture object curve is evaluated by the renderer's current material at the
                // active state. Include every direct property value on every possible material.
                if (state != null)
                {
                    var candidateMaterials = new List<Material>();
                    for (int slot = 0; slot < slotCount; slot++)
                    {
                        if (slot < currentMaterials.Length && currentMaterials[slot]) candidateMaterials.Add(currentMaterials[slot]);
                        if (state.SlotMaterials.TryGetValue(slot, out var candidates)) candidateMaterials.AddRange(candidates.Where(m => m));
                    }
                    foreach (var property in state.TextureValues.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        foreach (var material in candidateMaterials.Distinct())
                        {
                            if (!material || !material.shader || !material.HasProperty(property.Key)) continue;
                            foreach (var texture in property.Value.Where(t => t).OrderBy(t => TextureIdentity(t), StringComparer.Ordinal))
                            {
                                progress?.Scanning(texture, "Scanning animated texture uses");
                                var sampling = DescribeAnimated(material, property.Key, texture, allowUnsupportedShaders);
                                progress?.CheckCancelled();
                                for (int slot = 0; slot < slotCount; slot++)
                                {
                                    bool present = slot < currentMaterials.Length && currentMaterials[slot] == material;
                                    if (!present && (!state.SlotMaterials.TryGetValue(slot, out var slotCandidates) || !slotCandidates.Contains(material))) continue;
                                    AddUsage(renderer, path, mesh, slot, material, property.Key, texture, sampling,
                                        true, state, ambiguousSlots, groups, result, seenUses);
                                }
                            }
                        }
                    }
                    progress?.Scanning(null, "Checking animated sampling");
                    state.ComplexTextureCrossProduct = HasBorrowedSamplerDependency(state, groups, path,
                        allowUnsupportedShaders, progress);
                }
            }

            if (config && config.optimizeTextures && config.mergeDuplicateTextures)
            {
                progress?.Scanning(null, "Merging duplicate textures");
                MergeDuplicates(result, progress);
            }
            progress?.Scanning(null, "Checking animation safety");
            ApplyAnimationSafety(result, animation, allowUnsupportedShaders, progress);
            progress?.Scanning(null, "Validating texture groups");
            ValidateGroups(result, progress);
            var assumed = result.Groups.SelectMany(g => g.Uses).Where(u => u.Sampling.UnsafeOverride)
                .Select(u => u.Material.shader.name + " [" + u.Property + "]").Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            if (assumed.Length > 0)
                result.Warnings.Add("Unsupported shader override used. Assumes UV0 and property tiling/offset; alternate UVs, screen-space/procedural sampling and animation may show artifacts. Test before uploading. Fields: " + string.Join(", ", assumed));
            result.Groups.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a.Source), AssetDatabase.GetAssetPath(b.Source)));
            return result;
        }

        // A byte-identical texture file with identical import settings imports to an identical texture, so
        // every use of a copy can use one of them without any visible change, and the copies are then not
        // uploaded. The first by asset path keeps the merged uses, so coverage covers them together.
        // Only main-asset Texture2Ds with a TextureImporter qualify (not textures embedded in models).
        private sealed class DuplicateCandidate
        {
            public TextureGroup Group;
            public string Path, Settings;
            public int Width, Height;
            public bool NormalMap;
        }

        // Keep byte-identical files on the old cheap path. Differently encoded PNGs pass a
        // header/importer bucket, then a memoized pixel signature; only signature matches are
        // decoded again, retaining one candidate plus one representative pixel array while verifying.
        private static void MergeDuplicates(ScanResult result, OptimizationProgress progress)
        {
            var candidates = result.Groups
                .Select(group => (Group: group, Path: AssetDatabase.GetAssetPath(group.Source)))
                .Where(c => c.Group.Source is Texture2D && AssetDatabase.IsMainAsset(c.Group.Source) &&
                            AssetImporter.GetAtPath(c.Path) is TextureImporter && File.Exists(c.Path))
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ToArray();

            foreach (var bucket in candidates
                .GroupBy(c => new FileInfo(c.Path).Length + "|" +
                    TextureSafety.SettingsFingerprint((TextureImporter)AssetImporter.GetAtPath(c.Path)))
                .Where(bucket => bucket.Count() > 1))
            {
                var byFileHash = new Dictionary<string, List<(TextureGroup Group, string Path)>>(StringComparer.Ordinal);
                var copiesByHash = new List<List<(TextureGroup Group, string Path)>>();
                foreach (var candidate in bucket)
                {
                    progress?.Scanning(candidate.Group.Source, "Comparing duplicate texture files");
                    string hash = FingerprintService.FileHash(candidate.Path);
                    progress?.CheckCancelled();
                    if (!byFileHash.TryGetValue(hash, out var copies))
                    {
                        copies = new List<(TextureGroup Group, string Path)>();
                        byFileHash.Add(hash, copies);
                        copiesByHash.Add(copies);
                    }
                    copies.Add(candidate);
                }
                foreach (var copies in copiesByHash)
                {
                    if (copies.Count < 2) continue;
                    var keep = copies[0].Group;
                    for (int i = 1; i < copies.Count; i++)
                        MergeDuplicate(result, keep, copies[i].Group);
                }
            }

            var possible = result.Groups
                .Select(group => (Group: group, Path: AssetDatabase.GetAssetPath(group.Source)))
                .Where(c => c.Group.Source is Texture2D && AssetDatabase.IsMainAsset(c.Group.Source) &&
                            c.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                            AssetImporter.GetAtPath(c.Path) is TextureImporter && File.Exists(c.Path) &&
                            new FileInfo(c.Path).Length <= 128L * 1024 * 1024)
                .Select(c => new DuplicateCandidate
                {
                    Group = c.Group,
                    Path = c.Path,
                    Settings = TextureSafety.SettingsFingerprint((TextureImporter)AssetImporter.GetAtPath(c.Path)),
                    NormalMap = ((TextureImporter)AssetImporter.GetAtPath(c.Path)).textureType == TextureImporterType.NormalMap
                })
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .GroupBy(c => c.Group.Source.width + "|" + c.Group.Source.height + "|" + c.Settings)
                .Where(bucket => bucket.Count() > 1);

            foreach (var coarseBucket in possible)
            {
                var structural = new List<DuplicateCandidate>();
                foreach (var candidate in coarseBucket)
                {
                    try
                    {
                        progress?.Scanning(candidate.Group.Source, "Inspecting duplicate PNG");
                        var header = PngPixels.InspectHeader(candidate.Path);
                        candidate.Width = header.Width;
                        candidate.Height = header.Height;
                        if (candidate.NormalMap && (header.BitDepth != 8 ||
                            (header.ColorType != 2 && header.ColorType != 6))) continue;
                        structural.Add(candidate);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                        error is InvalidOperationException || error is OverflowException || error is ArgumentException)
                    {
                        // Unsupported or malformed sources remain separate and are handled by normal safety checks.
                    }
                }

                foreach (var bucket in structural
                    .GroupBy(c => c.Width + "|" + c.Height + "|" + c.Settings)
                    .Where(bucket => bucket.Count() > 1))
                    MergeDecodedDuplicates(result, bucket, progress);
            }
        }

        private static void MergeDecodedDuplicates(ScanResult result, IEnumerable<DuplicateCandidate> candidates, OptimizationProgress progress)
        {
            var bySignature = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
            foreach (var candidate in candidates.OrderBy(c => c.Path, StringComparer.Ordinal))
            {
                progress?.Scanning(candidate.Group.Source, "Comparing duplicate PNG pixels");
                string signature = PixelSignature(candidate, progress);
                if (signature == null) continue;
                if (!bySignature.TryGetValue(signature, out var representatives))
                {
                    bySignature.Add(signature, new List<DuplicateCandidate> { candidate });
                    continue;
                }

                // Matching signatures are verified pixel by pixel before merging.
                if (!TryDecode(candidate, out var pixels, out var info)) continue;
                bool merged = false;
                foreach (var representative in representatives)
                {
                    progress?.CheckCancelled();
                    if (!TryDecode(representative, out var otherPixels, out var otherInfo)) continue;
                    progress?.CheckCancelled();
                    if (info.Alpha != otherInfo.Alpha || !SameColourMetadata(info, otherInfo) ||
                        !pixels.SequenceEqual(otherPixels)) continue;
                    MergeDuplicate(result, representative.Group, candidate.Group);
                    merged = true;
                    break;
                }
                if (!merged) representatives.Add(candidate);
            }
        }

        // Decoded size, alpha, colour metadata and pixel hash, or null if the PNG cannot be decoded. Memoized per
        // file content in SessionState (it survives Play Mode domain reloads), so repeat runs only hash the file
        // instead of decoding every candidate. Failures are not memoized.
        private static string PixelSignature(DuplicateCandidate candidate, OptimizationProgress progress)
        {
            string key;
            try { key = "Arclight.Optimizer.PixelSignature." + FingerprintService.Version + "." + FingerprintService.FileHash(candidate.Path); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return null; }
            string signature = SessionState.GetString(key, "");
            if (signature.Length > 0) return signature;
            progress?.CheckCancelled();
            if (!TryDecode(candidate, out var pixels, out var info)) return null;
            progress?.CheckCancelled();
            signature = info.Width + "x" + info.Height + "|" + info.Alpha + "|" +
                FingerprintService.Hash(info.ColourMetadata.SelectMany(chunk => chunk).ToArray()) + "|" + HashPixels(pixels, progress);
            SessionState.SetString(key, signature);
            return signature;
        }

        private static bool TryDecode(DuplicateCandidate candidate, out Color32[] pixels, out PngInfo info)
        {
            try
            {
                pixels = PngPixels.Decode(File.ReadAllBytes(LongPath.For(candidate.Path)), out info);
                if (info.Width == candidate.Width && info.Height == candidate.Height)
                    return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is InvalidOperationException || error is OverflowException || error is ArgumentException ||
                error is UnityException)
            {
            }
            pixels = null;
            info = null;
            return false;
        }

        private static bool SameColourMetadata(PngInfo a, PngInfo b)
        {
            if (a.ColourMetadata.Count != b.ColourMetadata.Count) return false;
            for (int i = 0; i < a.ColourMetadata.Count; i++)
                if (!a.ColourMetadata[i].SequenceEqual(b.ColourMetadata[i])) return false;
            return true;
        }

        private static string HashPixels(Color32[] pixels, OptimizationProgress progress)
        {
            using (var sha = SHA256.Create())
            {
                byte[] block = new byte[64 * 1024];
                for (int first = 0; first < pixels.Length;)
                {
                    progress?.CheckCancelled();
                    int count = Math.Min(pixels.Length - first, block.Length / 4);
                    for (int i = 0; i < count; i++)
                    {
                        var color = pixels[first + i];
                        int offset = i * 4;
                        block[offset] = color.r;
                        block[offset + 1] = color.g;
                        block[offset + 2] = color.b;
                        block[offset + 3] = color.a;
                    }
                    int bytes = count * 4;
                    sha.TransformBlock(block, 0, bytes, block, 0);
                    first += count;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void MergeDuplicate(ScanResult result, TextureGroup keep, TextureGroup duplicate)
        {
            keep.Uses.AddRange(duplicate.Uses);
            if (keep.Warning == null) keep.Warning = duplicate.Warning;
            result.Groups.Remove(duplicate);
            foreach (var alias in result.Duplicates.Where(pair => pair.Value == duplicate.Source)
                .Select(pair => pair.Key).ToArray())
                result.Duplicates[alias] = keep.Source;
            result.Duplicates.Add(duplicate.Source, keep.Source);
        }
        private static void AddMaterialUses(Renderer renderer, string path, Mesh mesh, int slot,
            Material material, bool fromAnimation, AnimationRendererState state, bool ambiguousSlots,
            bool allowUnsupportedShaders, Dictionary<Texture, TextureGroup> groups, ScanResult result,
            HashSet<string> seenUses, OptimizationProgress progress)
        {
            if (!material || !material.shader)
            {
                if (!fromAnimation) result.Warnings.Add($"{path} slot {slot}: missing material or shader.");
                return;
            }
            foreach (string property in material.GetTexturePropertyNames())
            {
                progress?.CheckCancelled();
                var texture = material.GetTexture(property);
                if (!texture) continue;
                var sampling = ShaderAdapterRegistry.Describe(material, property, allowUnsupportedShaders);
                AddUsage(renderer, path, mesh, slot, material, property, texture, sampling, fromAnimation,
                    state, ambiguousSlots, groups, result, seenUses);
            }
        }

        // A renderer property block only matters when it sets something the sampling model reads: any texture slot or
        // its "<texture>_ST" transform (this also covers borrowed samplers and swapped textures), or a material input
        // the adapter recorded. Values it sets for anything else (a tint colour, for example) cannot change which texels
        // are sampled, the same assumption animated float curves already rely on. Unknown inputs keep the blanket rule.
        internal static bool PropertyBlockMayOverrideSampling(Renderer renderer, int slot, Material material,
            string property, SamplingDescription sampling, out string overridden)
        {
            overridden = null;
            if (!renderer.HasPropertyBlock()) return false;
            if (sampling.MaterialInputs == null) { overridden = "an unmodeled input"; return true; }
            // A renderer can carry one block for every material and one per material slot; either may apply.
            var blocks = new System.Collections.Generic.List<MaterialPropertyBlock> { new MaterialPropertyBlock() };
            renderer.GetPropertyBlock(blocks[0]);
            if (slot >= 0 && slot < renderer.sharedMaterials.Length)
            {
                blocks.Add(new MaterialPropertyBlock());
                renderer.GetPropertyBlock(blocks[1], slot);
            }
            foreach (var block in blocks)
            {
                if (block.isEmpty) continue;
                if (block.HasProperty(property)) { overridden = property; return true; }
                foreach (string texture in material.GetTexturePropertyNames())
                {
                    if (block.HasProperty(texture)) { overridden = texture; return true; }
                    if (block.HasProperty(texture + "_ST")) { overridden = texture + "_ST"; return true; }
                }
                foreach (string input in sampling.MaterialInputs)
                    if (block.HasProperty(input)) { overridden = input; return true; }
            }
            return false;
        }

        private static void AddUsage(Renderer renderer, string path, Mesh mesh, int slot, Material material,
            string property, Texture texture, SamplingDescription sampling, bool fromAnimation,
            AnimationRendererState state, bool ambiguousSlots, Dictionary<Texture, TextureGroup> groups,
            ScanResult result, HashSet<string> seenUses)
        {
            if (!texture || !material || sampling == null) return;
            string useKey = renderer.GetInstanceID() + "|" + slot + "|" + material.GetInstanceID() + "|" +
                property + "|" + texture.GetInstanceID();
            if (!seenUses.Add(useKey)) return;
            int submesh = mesh && mesh.subMeshCount > 0 ? Math.Min(Math.Max(slot, 0), mesh.subMeshCount - 1) : -1;
            string warning = !sampling.Supported ? sampling.Reason : null;
            if (!(texture is Texture2D)) warning = "Only ordinary Texture2D assets are supported.";
            if (PropertyBlockMayOverrideSampling(renderer, slot, material, property, sampling, out string blockInput))
                warning = "Material property block may override sampling: it sets " + blockInput + ".";
            if (ambiguousSlots) warning = "Ambiguous material/submesh assignment.";
            if (!mesh || submesh < 0) warning = "Missing mesh or submesh.";
            else if (mesh.GetTopology(submesh) != MeshTopology.Triangles) warning = "Non-triangle topology.";
            if (!groups.TryGetValue(texture, out var group))
            {
                group = new TextureGroup { Source = texture };
                groups.Add(texture, group);
                result.Groups.Add(group);
            }
            group.Uses.Add(new TextureUsageRecord
            {
                Renderer = renderer, RendererPath = path, Material = material, Slot = slot,
                Property = property, Texture = texture, Mesh = mesh, Submesh = submesh,
                Sampling = sampling, Warning = warning, FromAnimation = fromAnimation,
                MeshCache = result.Meshes
            });
            if (warning != null && !sampling.NotSampled) group.Warning = warning;
        }

        private static SamplingDescription DescribeAnimated(Material material, string property,
            Texture animatedValue, bool allowUnsupportedShaders)
        {
            if (!material.HasProperty(property) || !animatedValue) return ShaderAdapterRegistry.Describe(material, property, allowUnsupportedShaders);
            // Adapters commonly inspect the currently assigned texture (for importer semantics,
            // wrap mode or optional feature paths). Describe each curve value on a short-lived copy
            // so borrowed samplers and their UV models are unioned conservatively.
            var copy = new Material(material);
            try
            {
                copy.SetTexture(property, animatedValue);
                return ShaderAdapterRegistry.Describe(copy, property, allowUnsupportedShaders);
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }

        private static bool HasBorrowedSamplerDependency(AnimationRendererState state,
            Dictionary<Texture, TextureGroup> groups, string path, bool allowUnsupportedShaders,
            OptimizationProgress progress)
        {
            // Material swaps do not change the material's own texture dependency model and
            // should remain eligible. This check is only for actual texture-property curves.
            if (state == null || !state.HasTextureSwap || state.TextureValues.Count == 0) return false;

            var materials = groups.Values.SelectMany(g => g.Uses)
                .Where(u => u.RendererPath == path && u.Material)
                .Select(u => u.Material)
                .Distinct()
                .ToArray();
            if (materials.Length == 0) return false;

            foreach (var material in materials)
            {
                progress?.CheckCancelled();
                if (!material.shader) return true;
                var changed = state.TextureValues.Keys.Where(material.HasProperty)
                    .OrderBy(p => p, StringComparer.Ordinal).ToArray();
                if (changed.Length == 0) continue;
                // Uses record each animated property with only its own texture swapped. Curves in
                // different clips and layers combine freely, so every property may independently hold
                // its authored texture or any keyframed value (an empty key or missing reference is a
                // null value). Check that no combination changes any property's sampling from what
                // the uses describe.
                var options = changed.Select(p => state.TextureValues[p].Select(t => t ? t : null)
                    .Concat(new[] { material.GetTexture(p) }).Distinct().ToArray()).ToArray();
                long combinations = options.Aggregate(1L, (n, o) => n * o.Length);
                if (combinations > MaxTextureSwapCombinations) return true;
                var properties = material.GetTexturePropertyNames();
                var authored = properties.Distinct().ToDictionary(p => p,
                    p => ShaderAdapterRegistry.Describe(material, p, allowUnsupportedShaders));
                var swappedAlone = new Dictionary<(string, Texture), SamplingDescription>();
                var assignment = new Texture[changed.Length];
                for (long n = 0; n < combinations; n++)
                {
                    progress?.CheckCancelled();
                    long rest = n;
                    for (int i = 0; i < changed.Length; i++)
                    {
                        assignment[i] = options[i][(int)(rest % options[i].Length)];
                        rest /= options[i].Length;
                    }
                    using (var combination = new TemporaryMaterialScope(material, changed, assignment))
                    {
                        foreach (string property in authored.Keys)
                        {
                            int slot = Array.IndexOf(changed, property);
                            SamplingDescription expected;
                            if (slot < 0) expected = authored[property];
                            else
                            {
                                var value = assignment[slot];
                                if (!value) continue; // An empty slot has no use to check.
                                if (!swappedAlone.TryGetValue((property, value), out expected))
                                    swappedAlone.Add((property, value),
                                        expected = DescribeAnimated(material, property, value, allowUnsupportedShaders));
                            }
                            var actual = ShaderAdapterRegistry.Describe(combination.Material, property, allowUnsupportedShaders);
                            if (!EquivalentSampling(expected, actual, combination.Material.GetTexture(property))) return true;
                        }
                    }
                }
            }
            return false;
        }

        private const int MaxTextureSwapCombinations = 256;

        private sealed class TemporaryMaterialScope : IDisposable
        {
            internal readonly Material Material;

            internal TemporaryMaterialScope(Material source, string[] properties, Texture[] values)
            {
                Material = new Material(source);
                for (int i = 0; i < properties.Length; i++) Material.SetTexture(properties[i], values[i]);
            }

            public void Dispose() => UnityEngine.Object.DestroyImmediate(Material);
        }

        // Coverage depends on a borrowed sampler only through its wrap modes. With the sampled texture
        // known, a swapped sampler texture with the same wrap modes samples identically.
        internal static bool EquivalentSampling(SamplingDescription left, SamplingDescription right, Texture sampled = null)
        {
            if (left == null || right == null) return left == right;
            if (left.AdapterId != right.AdapterId || left.Supported != right.Supported ||
                left.UnsafeOverride != right.UnsafeOverride || left.OverrideExcluded != right.OverrideExcluded ||
                left.NotSampled != right.NotSampled || left.Semantics != right.Semantics) return false;
            var leftPaths = left.GetPaths().ToArray();
            var rightPaths = right.GetPaths().ToArray();
            if (leftPaths.Length != rightPaths.Length) return false;
            for (int i = 0; i < leftPaths.Length; i++)
            {
                var a = leftPaths[i];
                var b = rightPaths[i];
                if (a.UvChannel != b.UvChannel || a.Scale != b.Scale || a.Offset != b.Offset) return false;
                // Without the sampled texture, compare sampler identity; with it, only the wrap modes matter.
                bool sameSampler = sampled
                    ? a.WrapU(sampled) == b.WrapU(sampled) && a.WrapV(sampled) == b.WrapV(sampled)
                    : a.FixedRepeat == b.FixedRepeat && a.SamplerTexture == b.SamplerTexture;
                if (!sameSampler) return false;
            }
            return true;
        }

        private static string MaterialIdentity(Material material) => material
            ? AssetDatabase.GetAssetPath(material) + "|" + material.name
            : "<null>";

        private static string TextureIdentity(Texture texture) => texture
            ? AssetDatabase.GetAssetPath(texture) + "|" + texture.name
            : "<null>";

        private static void ApplyAnimationSafety(ScanResult result, AnimationSnapshot animation,
            bool allowUnsupportedShaders, OptimizationProgress progress)
        {
            foreach (var pair in animation.Renderers)
            {
                progress?.CheckCancelled();
                var state = pair.Value;
                // Without a virtual controller context, known swaps still contribute coverage here;
                // TemporaryMaterialSubstituter.IsUnsafeStandaloneSwap blocks their substitution.
                string warning = null;
                if (animation.Uninspectable)
                    warning = "Animation references could not be inspected. Skipped; original texture retained.";
                else if (state.HasMeshBinding)
                    warning = "Mesh animation exists on this renderer. Skipped; original texture retained.";
                else if (state.UnsupportedObjectCurve)
                    warning = "Animated material or texture assignment exceeded the supported safety limits. Skipped; original texture retained.";
                else if (state.ComplexTextureCrossProduct)
                    warning = "Animated texture properties change a borrowed sampler or adapter dependency in some combination, or have more than " +
                        MaxTextureSwapCombinations + " combinations. Skipped; original texture retained.";

                if (warning == null)
                {
                    ApplyFloatAnimationSafety(result, pair.Key, state, allowUnsupportedShaders, progress);
                    continue;
                }
                foreach (var group in result.Groups)
                {
                    progress?.CheckCancelled();
                    var uses = group.Uses.Where(u => u.RendererPath == pair.Key).ToArray();
                    if (uses.Length == 0) continue;
                    foreach (var use in uses) use.Warning = warning;
                    if (group.Warning == null || warning.StartsWith("Animation", StringComparison.Ordinal) ||
                        warning.StartsWith("Material", StringComparison.Ordinal)) group.Warning = warning;
                }
            }

            if (animation.Uninspectable)
            {
                const string warning = "Animation references could not be inspected. Skipped; original texture retained.";
                foreach (var group in result.Groups)
                {
                    foreach (var use in group.Uses) use.Warning = warning;
                    group.Warning = warning;
                }
            }
        }

        // A float curve can only change a use's coverage through a material value its adapter read
        // while describing it. Colour/strength animation is therefore safe. Animation of a read value
        // is modeled when its range is bounded and it only moves UV transforms (see AnimatedSampling);
        // otherwise, and for uses with unknown material inputs, it is retained.
        private static void ApplyFloatAnimationSafety(ScanResult result, string path, AnimationRendererState state,
            bool allowUnsupportedShaders, OptimizationProgress progress)
        {
            if (state.FloatProperties.Count == 0) return;
            foreach (var group in result.Groups)
            {
                progress?.CheckCancelled();
                foreach (var use in group.Uses)
                {
                    // A use the adapter already rejected keeps that reason (matcap, unmodeled field, ...); the animation
                    // note would replace it with a less specific one and point at the wrong fix.
                    if (use.RendererPath != path || use.Warning != null) continue;
                    var inputs = use.Sampling.MaterialInputs;
                    var affecting = state.FloatProperties.Where(p => inputs == null || inputs.Contains(p))
                        .OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    if (affecting.Length == 0) continue;
                    SamplingDescription expanded = null;
                    string reason = inputs == null ? "the shader adapter does not report its inputs"
                        : use.Sampling.NotSampled || !use.Sampling.Supported ? "the texture is not sampled through a modeled path"
                        // Swap combinations are checked at current values only; do not combine them with animated ranges.
                        : state.TextureValues.Keys.Any(p => p != use.Property && use.Material.HasProperty(p))
                            ? "texture swaps on other properties of this material are not modeled together with it"
                        : AnimatedSampling.TryExpand(use, state, affecting, allowUnsupportedShaders, out expanded);
                    if (reason == null)
                    {
                        use.Sampling = expanded;
                        continue;
                    }
                    use.Warning = "Material animation of " + string.Join(", ", affecting) +
                        " can change how this texture is sampled on this renderer (" + reason + "). Skipped; original texture retained.";
                    if (group.Warning == null) group.Warning = use.Warning;
                }
            }
        }

        private static void ValidateGroups(ScanResult result, OptimizationProgress progress)
        {
            foreach (var group in result.Groups)
            {
                progress?.Scanning(group.Source, "Validating texture group");
                group.Status = "Unsupported";
                if (!group.ActiveUses.Any())
                {
                    group.Warning = "Assigned only to verified unused compatibility aliases.";
                    group.Status = "Not sampled";
                    continue;
                }
                if (group.Warning == null)
                {
                    try
                    {
                        foreach (var use in group.ActiveUses)
                        {
                            if (use.Sampling.Semantics != TextureSemantics.Color && use.Sampling.Semantics != TextureSemantics.Data && use.Sampling.Semantics != TextureSemantics.Normal)
                                throw new InvalidOperationException("Unknown data semantics are not yet certified.");
                            if (!use.Sampling.GetPaths().Any()) throw new InvalidOperationException("Adapter supplied no sampling paths.");
                            foreach (var samplingPath in use.Sampling.GetPaths())
                            {
                                progress?.CheckCancelled();
                                use.ReadMesh(samplingPath.UvChannel);
                                if (samplingPath.WrapU(group.Source) == TextureWrapMode.MirrorOnce ||
                                    samplingPath.WrapV(group.Source) == TextureWrapMode.MirrorOnce)
                                    throw new InvalidOperationException("Mirror Once sampler equivalence is not yet certified on PC/Android.");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { group.Warning = e.Message; }
                    if (group.Warning == null)
                    {
                        try { TextureSafety.ValidateUsage(group); group.Status = "Eligible"; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { group.Warning = e.Message; group.Status = "Importer blocked"; }
                    }
                }
            }
            foreach (var group in result.Groups)
            {
                if (group.Warning == null) continue;
                var details = group.ActiveUses.Where(u => u.Warning != null)
                    .GroupBy(u => new { u.Material, u.Warning })
                    .Select(uses =>
                    {
                        var material = uses.Key.Material;
                        var shader = material ? material.shader : null;
                        return uses.Key.Warning +
                            "\nMaterial: " + (material ? material.name : "(missing)") +
                            "\nShader: " + (shader ? shader.name : "(missing)") +
                            "\nShader asset: " + (shader ? AssetDatabase.GetAssetPath(shader) : "(missing)") +
                            "\nTexture properties: " + string.Join(", ", uses.Select(u => u.Property).Distinct().OrderBy(p => p, StringComparer.Ordinal));
                    }).ToArray();
                if (details.Length > 0)
                    group.Warning = "Skipped; original texture retained for all its assignments.\n" + string.Join("\n\n", details);
            }
        }
    }
}
