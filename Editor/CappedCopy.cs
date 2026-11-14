using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // A build copy of an existing texture whose *importer* is changed rather than its pixels: an optional size cap
    // (Unity's own resampling), mipmaps dropped for a texture drawn at one size, and an opaque texture narrowed from
    // DXT5 to DXT1 on PC. The copy is a byte copy of the source with its import settings, so every authored option
    // survives; the source file and its importer are never changed. A copy is kept only when its measured bundle is
    // strictly smaller than the source's. Used for expression-menu icons (MenuIconOptimizer) and for the opt-in
    // "Texture Size" cap in the component.
    internal static class CappedCopy
    {
        internal static string Folder => AutomaticTextureOptimizer.CacheFolder + "/Capped";
        internal const string Marker = "ArclightCapped:";

        internal static bool IsGenerated(string path, string marker) =>
            !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer &&
            (importer.userData ?? "").StartsWith(marker, StringComparison.Ordinal);

        // The capped/changed copy of a main-asset 2D texture, or null when it is not one, already fits and needs no
        // other change, or does not measure smaller. before/after are the measured bundle bytes.
        internal static Texture2D Make(Texture2D source, int maxSize, bool dropMips, bool narrowOpaque, string folder, string marker,
            string suffix, bool allowCacheSource, out long before, out long after)
        {
            before = after = 0;
            if (!source || maxSize < 0) return null;
            bool cap = maxSize > 0 && (source.width > maxSize || source.height > maxSize);
            if (!cap && !dropMips && !narrowOpaque) return null;
            string path = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(source) ||
                (!allowCacheSource && path.StartsWith(AutomaticTextureOptimizer.CacheFolder, StringComparison.Ordinal)) ||
                !(AssetImporter.GetAtPath(path) is TextureImporter importer) ||
                importer.textureShape != TextureImporterShape.Texture2D) return null;
            if (!File.Exists(LongPath.For(path))) return null;
            int effectiveMax = cap ? maxSize : 0;
            bool mips = dropMips && importer.mipmapEnabled;
            if (!cap && !mips && !narrowOpaque) return null;
            string hash = FingerprintService.Hash(System.Text.Encoding.UTF8.GetBytes(
                FingerprintService.FileHash(path) + "|" + TextureSafety.SettingsFingerprint(importer) + "|" + effectiveMax + "|" +
                (mips ? "nomips" : "mips") + "|" + (narrowOpaque ? "narrow" : "keep") + "|" + (ChannelFormats.NarrowBc7 ? "bc7narrow" : "bc7keep") + "|" + marker));
            AutomaticTextureOptimizer.EnsureFolder(folder);
            string safeName = new string(Path.GetFileNameWithoutExtension(path)
                .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').Take(36).ToArray());
            string output = OutputPath(folder, safeName, hash, Path.GetExtension(path), suffix);
            if (output == null) return null;
            string tag = marker + hash;
            if (File.Exists(LongPath.For(output)))
            {
                if (!IsGenerated(output, marker)) return null;
                var cached = (TextureImporter)AssetImporter.GetAtPath(output);
                var reused = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
                bool expectedMips = !dropMips && importer.mipmapEnabled;
                if (cached.userData == tag + ":" + FingerprintService.FileHash(output) && cached.mipmapEnabled == expectedMips && reused &&
                    (effectiveMax <= 0 || reused.width <= effectiveMax && reused.height <= effectiveMax))
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
            if (effectiveMax > 0) copy.maxTextureSize = effectiveMax;
            foreach (string platform in new[] { "Standalone", "Android" })
            {
                var settings = copy.GetPlatformTextureSettings(platform);
                if (!settings.overridden || settings.maxTextureSize <= effectiveMax) continue;
                settings.maxTextureSize = Math.Max(effectiveMax, 32);
                copy.SetPlatformTextureSettings(settings);
            }
            if (dropMips) copy.mipmapEnabled = false;
            copy.userData = tag;
            copy.SaveAndReimport();
            var result = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
            if (!result || (effectiveMax > 0 && (result.width > effectiveMax || result.height > effectiveMax)))
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            // DXT1 for an opaque texture: exact only when every texel's alpha is 1, which the GPU's decode confirms.
            if (narrowOpaque && GeneratedTargetValidator.IsStandalone &&
                ChannelFormats.Choose(copy, TextureChannels.RGB) is TextureImporterFormat narrowed)
            {
                copy = SetReadable(output, true);
                var pixels = AssetDatabase.LoadAssetAtPath<Texture2D>(output)?.GetPixels32();
                copy = SetReadable(output, false);
                if (pixels != null && pixels.Length > 0 && pixels.All(p => p.a == 255) && copy != null)
                {
                    copy.SetPlatformTextureSettings(ChannelFormats.Standalone(copy, narrowed));
                    copy.userData = tag;
                    copy.SaveAndReimport();
                    result = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
                    if (!result || !TextureCropper.OpaqueOnGpu(result))
                    {
                        AssetDatabase.DeleteAsset(output);
                        return null;
                    }
                }
            }
            // Every change this helper makes is smaller by construction: a cap drops texels, dropped mips remove the
            // chain, and BC1 is half of BC3/BC7. So without a bundle estimate (Play Mode, a failed estimate) the copy is
            // kept unmeasured instead of the whole optimisation being skipped; with an estimate the measured saving
            // still decides. before/after stay 0 when unmeasured, which the report shows as unmeasured bytes.
            if (BundleSizeEstimator.CanEstimate &&
                (!BundleSizeEstimator.TryMeasure(path, out before) || !BundleSizeEstimator.TryMeasure(output, out after) || after >= before))
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            copy = (TextureImporter)AssetImporter.GetAtPath(output);
            if (copy == null) { AssetDatabase.DeleteAsset(output); return null; }
            copy.userData = tag + ":" + FingerprintService.FileHash(output);
            copy.SaveAndReimport();
            result = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
            if (!result) { AssetDatabase.DeleteAsset(output); return null; }
            AudioMonoConverter.MarkUsed(output);
            return result;
        }

        // Toggles Read/Write on the copy so a decode can be read; the copied settings are restored afterwards.
        private static TextureImporter SetReadable(string path, bool readable)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return null;
            importer.isReadable = readable;
            importer.SaveAndReimport();
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        // Keeps the longest written name ("<name>_<hash>_<suffix><ext>.meta") within 259 characters, as the other passes do.
        private static string OutputPath(string folder, string safeName, string signature, string extension, string suffix)
        {
            string tail = "_" + signature.Substring(0, 12) + "_" + suffix + extension;
            int budget = 259 - Path.GetFullPath(folder + "/" + tail + ".meta").Length;
            if (budget < 0) return null;
            return folder + "/" + safeName.Substring(0, Math.Min(safeName.Length, budget)) + tail;
        }
    }
}
