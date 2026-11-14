using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Expression-menu icons are drawn at a small size in VRChat's menu, but they are imported at their authored
    // resolution (commonly 512-1024 squared, BC3 with mips) and the texture pass never sees them because they are not
    // on a material. This writes a build copy of an icon whose importer caps it at the configured size, so Unity's own
    // importer resamples it; the source asset is never changed. Opt-in and only kept when the measured bundle shrinks.
    internal static class MenuIconOptimizer
    {
        internal static string Folder => AutomaticTextureOptimizer.CacheFolder + "/MenuIcons";
        private const string Marker = "ArclightIcon:";

        internal static bool IsGenerated(string path) =>
            !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer &&
            (importer.userData ?? "").StartsWith(Marker, StringComparison.Ordinal);

        // A cached or new capped copy of an icon, or null when the icon is not a main texture asset, already fits, or
        // does not measure smaller. The caller points the build's menu controls at the result.
        internal static Texture2D Resize(Texture2D source, int maxSize)
        {
            if (!source || maxSize <= 0 || !BundleSizeEstimator.CanEstimate) return null;
            if (source.width <= maxSize && source.height <= maxSize) return null;
            string path = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(source) ||
                path.StartsWith(AutomaticTextureOptimizer.CacheFolder, StringComparison.Ordinal) ||
                !(AssetImporter.GetAtPath(path) is TextureImporter importer) ||
                importer.textureShape != TextureImporterShape.Texture2D) return null;
            if (!File.Exists(LongPath.For(path))) return null;
            string hash = FingerprintService.Hash(System.Text.Encoding.UTF8.GetBytes(
                FingerprintService.FileHash(path) + "|" + TextureSafety.SettingsFingerprint(importer) + "|" + maxSize + "|icon-v1"));
            AutomaticTextureOptimizer.EnsureFolder(Folder);
            string safeName = new string(Path.GetFileNameWithoutExtension(path)
                .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').Take(36).ToArray());
            string output = OutputPath(Folder, safeName, hash, Path.GetExtension(path));
            if (output == null) return null;
            string marker = Marker + hash;
            if (File.Exists(LongPath.For(output)))
            {
                if (!IsGenerated(output)) return null;
                var cached = (TextureImporter)AssetImporter.GetAtPath(output);
                var reused = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
                if (cached.userData == marker + ":" + FingerprintService.FileHash(output) && cached.maxTextureSize == maxSize &&
                    reused && reused.width <= maxSize && reused.height <= maxSize)
                {
                    AudioMonoConverter.MarkUsed(output);
                    return reused;
                }
            }
            if (File.Exists(LongPath.For(output))) AssetDatabase.DeleteAsset(output);
            File.Copy(LongPath.For(path), LongPath.For(output), true);
            AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
            var copy = AssetImporter.GetAtPath(output) as TextureImporter;
            if (copy == null) { AssetDatabase.DeleteAsset(output); return null; }
            TextureSafety.CopyImporter(importer, copy);
            copy.maxTextureSize = maxSize;
            foreach (string platform in new[] { "Standalone", "Android" })
            {
                var settings = copy.GetPlatformTextureSettings(platform);
                if (!settings.overridden || settings.maxTextureSize <= maxSize) continue;
                settings.maxTextureSize = maxSize;
                copy.SetPlatformTextureSettings(settings);
            }
            copy.userData = marker;
            copy.SaveAndReimport();
            var result = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
            if (!result || result.width > maxSize || result.height > maxSize)
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            if (!BundleSizeEstimator.TryMeasure(path, out long before) || !BundleSizeEstimator.TryMeasure(output, out long after) || after >= before)
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            copy = (TextureImporter)AssetImporter.GetAtPath(output);
            if (copy == null) { AssetDatabase.DeleteAsset(output); return null; }
            copy.userData = marker + ":" + FingerprintService.FileHash(output);
            copy.SaveAndReimport();
            result = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
            if (!result) { AssetDatabase.DeleteAsset(output); return null; }
            AudioMonoConverter.MarkUsed(output);
            return result;
        }

        // Keeps the longest written name ("<name>_<hash>_icon<ext>.meta") within 259 characters, as the other passes do.
        private static string OutputPath(string folder, string safeName, string signature, string extension)
        {
            string tail = "_" + signature.Substring(0, 12) + "_icon" + extension;
            int budget = 259 - Path.GetFullPath(folder + "/" + tail + ".meta").Length;
            if (budget < 0) return null;
            return folder + "/" + safeName.Substring(0, Math.Min(safeName.Length, budget)) + tail;
        }
    }
}
