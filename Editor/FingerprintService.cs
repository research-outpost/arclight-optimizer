using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Mono's file APIs in the editor are not long-path aware even when Windows is, so a project or package file whose
    // full path reaches MAX_PATH (Unity itself imports such files) cannot be opened by name. The \\?\ form lifts that
    // limit; it needs the absolute, backslash-separated path that Path.GetFullPath returns. Shorter paths are unchanged.
    internal static class LongPath
    {
        internal static string For(string path)
        {
            if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor ||
                path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
            string full = Path.GetFullPath(path);
            return full.Length < 260 ? path : @"\\?\" + full;
        }
    }

    internal static class FingerprintService
    {
        public const string Version = "png-reimport-v21-opaque-dxt1";
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        public static string FileHash(string path)
        {
            using (var stream = File.OpenRead(LongPath.For(path)))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }
        public static string Id(UnityEngine.Object asset)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId) || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("Texture needs a persistent asset GUID.");
            return guid + "_" + localId;
        }
        public static string ImporterHash(UnityEngine.Object asset) => FileHash(AssetDatabase.GetAssetPath(asset) + ".meta");

        // The recipe identifies the generated PNG, so it covers only what determines its pixels and
        // import: source bytes and importer (padding, normal fill, copied settings), and the sampled UV
        // geometry of every use. Material values, object names and animation affect the output only
        // through the sampling paths recorded here, so colour edits, renamed objects or other avatars with
        // the same coverage reuse one output. Eligibility and animation safety are re-checked by every
        // scan. Shader assets and adapter ids stay included so verified-model changes regenerate.
        public static string Recipe(TextureGroup group)
        {
            if (group.CachedRecipe != null) return group.CachedRecipe;
            var path = AssetDatabase.GetAssetPath(group.Source);
            string sourceHash = FileHash(path);
            var uses = new SortedSet<string>(group.Uses.Select(use => UseEntry(group.Source, use)), StringComparer.Ordinal);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Version); writer.Write(Id(group.Source)); writer.Write(sourceHash);
                writer.Write(ImporterHash(group.Source));
                writer.Write(group.RepairsPadding);
                // Code constants a cached "retained" outcome depends on: changing either revisits those textures.
                writer.Write(RetainedException.MinimumUnusedTexels);
                writer.Write(AvatarTextureOptimizer.GetPaddingPixels(group.Source.width, group.Source.height));
                // The compressed-size estimate that gates a replacement depends on the build target.
                writer.Write((int)EditorUserBuildSettings.activeBuildTarget);
                // Platform switches can change the imported resolution without changing source metadata.
                writer.Write(group.Source.width); writer.Write(group.Source.height);
                writer.Write((int)group.Source.wrapModeU); writer.Write((int)group.Source.wrapModeV);
                writer.Write(uses.Count);
                foreach (string use in uses) writer.Write(use);
                writer.Flush(); stream.Position = 0;
                using (var sha = SHA256.Create())
                {
                    group.CachedSourceHash = sourceHash;
                    return group.CachedRecipe = Hex(sha.ComputeHash(stream));
                }
            }
        }

        // The source file hash the group's recipe describes.
        internal static string SourceHash(TextureGroup group)
        {
            Recipe(group);
            return group.CachedSourceHash;
        }

        private static string UseEntry(UnityEngine.Texture source, TextureUsageRecord use)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(use.Sampling.AdapterId ?? ""); writer.Write(use.Sampling.NotSampled);
                writer.Write((int)use.Sampling.Semantics);
                writer.Write((int)use.Sampling.Channels);
                writer.Write(use.Material.shader.name);
                writer.Write(AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(use.Material.shader)).ToString());
                var paths = use.Sampling.GetPaths().ToArray();
                writer.Write(paths.Length);
                foreach (var samplingPath in paths)
                {
                    writer.Write(samplingPath.UvChannel);
                    writer.Write(samplingPath.Scale.x); writer.Write(samplingPath.Scale.y);
                    writer.Write(samplingPath.Offset.x); writer.Write(samplingPath.Offset.y);
                    writer.Write(samplingPath.FixedRepeat);
                    writer.Write((int)samplingPath.WrapU(source)); writer.Write((int)samplingPath.WrapV(source));
                    var transforms = samplingPath.AnimatedTransforms;
                    writer.Write(transforms?.Count ?? 0);
                    if (transforms != null)
                        foreach (var t in transforms) { writer.Write(t.x); writer.Write(t.y); writer.Write(t.z); writer.Write(t.w); }
                    writer.Write(use.ReadMesh(samplingPath.UvChannel).UvHash);
                }
                writer.Flush();
                return Hash(stream.ToArray());
            }
        }

        public static bool IsReady(GeneratedTextureMapping mapping, string recipe)
        {
            if (mapping == null || !mapping.replacement || mapping.recipeHash != recipe ||
                !mapping.source || mapping.width <= 0 || mapping.height <= 0 || mapping.padding != AvatarTextureOptimizer.GetPaddingPixels(mapping.source.width, mapping.source.height)) return false;
            string path = AssetDatabase.GetAssetPath(mapping.replacement);
            try
            {
                return path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(path) && TextureFileSizePolicy.IsBeneficial(mapping) && FileHash(path) == mapping.outputHash && ImporterHash(mapping.replacement) == mapping.outputImporterHash;
            }
            catch (IOException) { return false; }
        }
    }
}
