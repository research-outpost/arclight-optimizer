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
    // limit; it needs the absolute, backslash-separated path that Path.GetFullPath returns, and UNC roots take the
    // \\?\UNC\ form. Shorter paths are unchanged.
    internal static class LongPath
    {
        internal static string For(string path)
        {
            if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor ||
                path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            string full = Path.GetFullPath(path);
            if (full.Length < 260) return path;
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        // The file's size in bytes. One place for every size check, so none of them stats the bare path and fails
        // on a file past MAX_PATH while the reads next to it succeed.
        internal static long Length(string path) => new FileInfo(For(path)).Length;
    }

    // Names, markers and signatures shared by the generated build copies (audio, capped textures, crops, generated PNGs).
    // Every string built here is part of a cache file's name, marker or SessionState key: changing one regenerates the cache.
    internal static class GeneratedFiles
    {
        // The source name with anything but letters, digits, '-' and '_' replaced by '_', at most 36 characters.
        internal static string SafeName(string name) =>
            new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').Take(36).ToArray());

        // folder/<safeName, shortened as needed>_<12 signature hex digits><tail>. Unity's own tools still fail on paths
        // past MAX_PATH, so the longest file written (the copy's .meta) stays within 259 characters; a cache folder too
        // deep to leave room returns null rather than throwing (a source left as it is beats stopping every later pass).
        internal static string OutputPath(string folder, string safeName, string signature, string tail)
        {
            tail = "_" + signature.Substring(0, 12) + tail;
            int budget = 259 - Path.GetFullPath(folder + "/" + tail + ".meta").Length;
            if (budget < 0) return null;
            return folder + "/" + safeName.Substring(0, Math.Min(safeName.Length, budget)) + tail;
        }

        // Whether path holds an importer of type T whose userData starts with marker (a copy Arclight wrote).
        internal static bool HasMarker<T>(string path, string marker) where T : AssetImporter =>
            !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is T importer &&
            (importer.userData ?? "").StartsWith(marker, StringComparison.Ordinal);

        // The source's bytes, its raw .meta and extra hashed together: the signature that names and marks a copy. Memoized in
        // SessionState (it survives Play Mode's domain reloads) under keyPrefix + version + Unity's dependency hash (content and
        // importer) + the .meta's hash + keySuffix, so a build re-reads a big source only when it changes. Null when the .meta
        // cannot be read; IO failures are not memoized.
        internal static string SourceSignature(string keyPrefix, string path, string meta, string keySuffix = "", byte[] extra = null)
        {
            string key;
            try { key = keyPrefix + FingerprintService.Version + "." + AssetDatabase.GetAssetDependencyHash(path) + "." + FingerprintService.FileHash(meta) + keySuffix; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return null; }
            string cached = SessionState.GetString(key, "");
            if (cached.Length > 0) return cached;
            var source = File.ReadAllBytes(LongPath.For(path));
            var metaBytes = File.ReadAllBytes(LongPath.For(meta));
            string hash = extra == null ? FingerprintService.Hash(source, metaBytes) : FingerprintService.Hash(source, metaBytes, extra);
            SessionState.SetString(key, hash);
            return hash;
        }
    }

    internal static class FingerprintService
    {
        public const string Version = "png-reimport-v22-r8";
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }
        // The hash of the parts' concatenation (equal to Hash of the joined array), without copying large files into one array.
        public static string Hash(params byte[][] parts) => Hex(Sha256(parts));
        internal static byte[] Sha256(params byte[][] parts)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var part in parts) sha.TransformBlock(part, 0, part.Length, null, 0);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return sha.Hash;
            }
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
            return RecomputeRecipe(group);
        }

        // Re-hash the source file and importer, bypassing this scan's memo: the recheck during generation must
        // notice a source that changed on disk after the recipe was first captured, which the memo would hide.
        public static string RecipeRefreshed(TextureGroup group) => RecomputeRecipe(group);

        private static string RecomputeRecipe(TextureGroup group)
        {
            var path = AssetDatabase.GetAssetPath(group.Source);
            string sourceHash = FileHash(path);
            var uses = new SortedSet<string>(group.Uses.Select(use => UseEntry(group.Source, use)), StringComparer.Ordinal);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Version); writer.Write(Id(group.Source)); writer.Write(sourceHash);
                writer.Write(ImporterHash(group.Source));
                writer.Write(group.RepairsPadding);
                // The BC7 narrowing is opt-in: a verdict decided with it on must never be reused with it off.
                writer.Write(ChannelFormats.NarrowBc7);
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
