using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Opt-in: uncompressed (PCM) audio clips re-encoded as Vorbis in a generated copy. Music and voice-line avatars
    // can carry tens of megabytes of PCM that a codec cuts by roughly an order of magnitude; the source asset is
    // never changed. A copy is kept only when the measured bundle shrinks, so an SDK or build step that already
    // re-encodes the clip (or a clip too small to gain) leaves the source in place. Audible difference is the
    // codec's loss and is recorded as an accepted difference on the component.
    internal static class AudioCompressor
    {
        internal static string Folder => AutomaticTextureOptimizer.CacheFolder + "/Audio/Vorbis";

        private const string Marker = "ArclightVorbis:";

        // Clip -> its Vorbis replacement. skip: clips another pass already replaced (mono copies), which are left as
        // they are so two replacements never chain.
        internal static Dictionary<AudioClip, AudioClip> Convert(IEnumerable<(Component Owner, AudioClip Clip)> references,
            float quality, ICollection<AudioClip> skip = null)
        {
            var result = new Dictionary<AudioClip, AudioClip>();
            try
            {
                foreach (var group in references.GroupBy(r => r.Clip))
                {
                    var clip = group.Key;
                    if (!clip || skip != null && skip.Contains(clip)) continue;
                    var copy = VorbisCopy(clip, quality);
                    if (copy) result.Add(clip, copy);
                }
            }
            finally { AudioMonoConverter.FlushUsage(); } // One save for the whole pass, as the mono pass does.
            return result;
        }

        internal static bool IsGenerated(string path) =>
            !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is AudioImporter importer &&
            (importer.userData ?? "").StartsWith(Marker, StringComparison.Ordinal);

        // A cached or new Vorbis copy of an imported PCM clip, or null when the clip is not a candidate or the copy
        // does not measure smaller than its source.
        private static AudioClip VorbisCopy(AudioClip clip, float quality)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(clip) ||
                path.StartsWith(AutomaticTextureOptimizer.CacheFolder, StringComparison.Ordinal) ||
                !(AssetImporter.GetAtPath(path) is AudioImporter sourceImporter)) return null;
            if (sourceImporter.ambisonic) return null;
            var source = sourceImporter.defaultSampleSettings;
            // Only uncompressed sources. A platform override (or a per-platform codec) is left alone: the copy's .meta
            // would have to reproduce every block, and a wrong one changes sound on that platform.
            if (source.compressionFormat != AudioCompressionFormat.PCM || source.loadType == AudioClipLoadType.Streaming) return null;
            if (sourceImporter.ContainsSampleSettingsOverride("Standalone") || sourceImporter.ContainsSampleSettingsOverride("Android")) return null;
            if (!BundleSizeEstimator.CanEstimate) return null; // proven on the real import only
            string meta = path + ".meta";
            if (!File.Exists(LongPath.For(path)) || !File.Exists(LongPath.For(meta))) return null;
            float q = Mathf.Clamp01(quality);
            string hash = SourceSignature(path, meta, q);
            if (hash == null) return null;
            AutomaticTextureOptimizer.EnsureFolder(Folder);
            string safeName = new string(Path.GetFileNameWithoutExtension(path)
                .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').Take(36).ToArray());
            string output = OutputPath(Folder, safeName, hash, Path.GetExtension(path));
            if (output == null) return null;
            string marker = Marker + hash;

            var expected = source;
            expected.compressionFormat = AudioCompressionFormat.Vorbis;
            expected.quality = q;
            if (File.Exists(LongPath.For(output)))
            {
                if (!IsGenerated(output)) return null; // Anything else at this path is not ours.
                var cached = (AudioImporter)AssetImporter.GetAtPath(output);
                var reused = AssetDatabase.LoadAssetAtPath<AudioClip>(output);
                if (cached.userData == marker + ":" + FingerprintService.FileHash(output) && cached.forceToMono == sourceImporter.forceToMono &&
                    cached.loadInBackground == sourceImporter.loadInBackground && cached.ambisonic == sourceImporter.ambisonic &&
                    cached.defaultSampleSettings.Equals(expected) && reused &&
                    reused.channels == clip.channels && reused.frequency == clip.frequency && reused.samples == clip.samples)
                {
                    AudioMonoConverter.MarkUsed(output);
                    return reused;
                }
            }

            if (File.Exists(LongPath.For(output))) AssetDatabase.DeleteAsset(output);
            File.Copy(LongPath.For(path), LongPath.For(output), true);
            AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(output) as AudioImporter;
            if (importer == null) { AssetDatabase.DeleteAsset(output); return null; }
            importer.forceToMono = sourceImporter.forceToMono;
            importer.loadInBackground = sourceImporter.loadInBackground;
            importer.ambisonic = sourceImporter.ambisonic;
            importer.defaultSampleSettings = expected;
            importer.userData = marker;
            importer.SaveAndReimport();
            var result = AssetDatabase.LoadAssetAtPath<AudioClip>(output);
            if (!result || result.channels != clip.channels || result.frequency != clip.frequency || result.samples != clip.samples)
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            // Proven on the real import: never keep a copy that does not shrink the bundle.
            if (!BundleSizeEstimator.TryMeasure(path, out long before) || !BundleSizeEstimator.TryMeasure(output, out long after) || after >= before)
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            // The reimported asset is the bytes we wrote, so its hash keeps the cache entry honest.
            importer = (AudioImporter)AssetImporter.GetAtPath(output);
            if (importer == null) { AssetDatabase.DeleteAsset(output); return null; }
            importer.userData = marker + ":" + FingerprintService.FileHash(output);
            importer.SaveAndReimport();
            result = AssetDatabase.LoadAssetAtPath<AudioClip>(output);
            if (!result) { AssetDatabase.DeleteAsset(output); return null; }
            AudioMonoConverter.MarkUsed(output);
            return result;
        }

        // Source bytes, its .meta and the quality, memoized in SessionState by Unity's dependency hash (content and
        // importer) plus the .meta hash, as the mono pass does. A build only re-reads a big clip when one changes.
        private static string SourceSignature(string path, string meta, float quality)
        {
            string key;
            try
            {
                key = "Arclight.Optimizer.VorbisSourceSignature." + FingerprintService.Version + "." +
                    AssetDatabase.GetAssetDependencyHash(path) + "." + FingerprintService.FileHash(meta) + "." + quality.ToString("R");
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return null; }
            string cached = SessionState.GetString(key, "");
            if (cached.Length > 0) return cached;
            string hash = FingerprintService.Hash(File.ReadAllBytes(LongPath.For(path))
                .Concat(File.ReadAllBytes(LongPath.For(meta)))
                .Concat(System.Text.Encoding.UTF8.GetBytes(quality.ToString("R"))).ToArray());
            SessionState.SetString(key, hash);
            return hash;
        }

        // Keeps the longest written name ("<name>_<hash>_vorbis<ext>.meta") within 259 characters, as the mono pass
        // does; a cache folder too deep returns null instead of throwing.
        private static string OutputPath(string folder, string safeName, string signature, string extension)
        {
            string tail = "_" + signature.Substring(0, 12) + "_vorbis" + extension;
            int budget = 259 - Path.GetFullPath(folder + "/" + tail + ".meta").Length;
            if (budget < 0) return null;
            return folder + "/" + safeName.Substring(0, Math.Min(safeName.Length, budget)) + tail;
        }
    }
}
