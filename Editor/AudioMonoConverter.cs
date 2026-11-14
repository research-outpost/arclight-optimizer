using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // A stereo clip whose two channels are sample-for-sample identical carries its sound twice. Played as mono it
    // comes out exactly 3 dB quieter (measured: Unity's 2D mixer with no pan, and the Oculus and Steam Audio
    // spatializers at every distance), so the mono copy is written with +3 dB (x sqrt 2) baked in, which
    // matches the stereo output level and balance. Elsewhere the difference is not a constant 3 dB (panned 2D,
    // 3D without a spatializer, spread, mixer groups), so a clip is converted only when every component that
    // references it is an AudioSource in one of the measured setups:
    //  - spatialized: a VRC Spatial Audio Source with spatialization on, spatial blend 1, no pan, no spread;
    //  - plain 2D: a VRC Spatial Audio Source with spatialization off, spatial blend 0, no pan, no spread.
    // Sources without a VRC Spatial Audio Source are skipped: the SDK adds one at upload with settings that
    // depend on the source. The boosted samples must not clip (source peak at or below -3 dBFS). The copy is
    // written as 32-bit float WAV with the source's import settings, so only the source's own compression
    // differs between the two. Generated clips are cached under the optimizer's cache folder.
    internal static class AudioMonoConverter
    {
        // Follows the texture cache (tests redirect it into their fixture folder).
        internal static string Folder => AutomaticTextureOptimizer.CacheFolder + "/Audio";
        private const float Boost = 1.41421356f;

        // Clip -> its mono +3 dB replacement, for clips every referencing component allows. A clip any source on a vetoed object
        // holds (one whose settings an animation changes) stays as it is.
        internal static Dictionary<AudioClip, AudioClip> Convert(IEnumerable<(Component Owner, AudioClip Clip)> references, ICollection<GameObject> vetoed = null)
        {
            var result = new Dictionary<AudioClip, AudioClip>();
            // Start the pass from the on-disk record: the cache cleanup may have pruned it since the last marks, and marks
            // left over from a pass with no end point (texture crops) are not carried into this one. See MarkUsed.
            usagePending = null;
            try
            {
                foreach (var group in references.GroupBy(r => r.Clip))
                {
                    if (!group.All(r => Qualifies(r.Owner, r.Clip) && (vetoed == null || !vetoed.Contains(r.Owner.gameObject)))) continue;
                    var mono = MonoCopy(group.Key);
                    if (mono) result.Add(group.Key, mono);
                }
            }
            finally { FlushUsage(); } // One save for the whole pass, even when it throws part-way: clips marked used up to there keep their record.
            return result;
        }

        internal static bool Qualifies(Component owner, AudioClip clip)
        {
            if (!(owner is AudioSource source) || source.clip != clip) return false;
            if (source.outputAudioMixerGroup || source.spread != 0 || source.panStereo != 0) return false;
            // A custom spatial-blend or spread curve drives these values by distance.
            if (source.GetCustomCurve(AudioSourceCurveType.SpatialBlend).length > 1 ||
                source.GetCustomCurve(AudioSourceCurveType.Spread).length > 1) return false;
            bool? spatialized = VrcSpatialization(source.gameObject);
            if (spatialized == true) return source.spatialBlend == 1;
            if (spatialized == false) return source.spatialBlend == 0 && !source.spatialize;
            return false;
        }

        // EnableSpatialization of an enabled VRC Spatial Audio Source on the object, or null when there is none.
        private static bool? VrcSpatialization(GameObject gameObject)
        {
            foreach (var component in gameObject.GetComponents<Behaviour>())
            {
                if (!component || !IsVrcSpatialSource(component.GetType()) || !component.enabled) continue;
                var type = component.GetType();
                var field = type.GetField("EnableSpatialization");
                if (field != null && field.FieldType == typeof(bool)) return (bool)field.GetValue(component);
                var property = type.GetProperty("EnableSpatialization");
                if (property != null && property.PropertyType == typeof(bool)) return (bool)property.GetValue(component);
                return null;
            }
            return null;
        }

        internal static bool IsVrcSpatialSource(Type type)
        {
            for (; type != null; type = type.BaseType)
                if (type.Name == "VRC_SpatialAudioSource" || type.Name == "VRCSpatialAudioSource") return true;
            return false;
        }

        // A cached or new mono +3 dB copy, or null when the clip is not an imported stereo file with identical
        // channels and enough headroom.
        internal static AudioClip MonoCopy(AudioClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (!clip || clip.channels != 2 || string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(clip) ||
                !(AssetImporter.GetAtPath(path) is AudioImporter importer) || importer.ambisonic || importer.forceToMono) return null;
            string meta = path + ".meta";
            if (!File.Exists(LongPath.For(path)) || !File.Exists(LongPath.For(meta))) return null;
            string hash = SourceSignature(path, meta);
            if (hash == null) return null;
            AutomaticTextureOptimizer.EnsureFolder(Folder);
            string safeName = new string(Path.GetFileNameWithoutExtension(path).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').Take(36).ToArray());
            string output = OutputPath(Folder, safeName, hash);
            if (output == null) return null; // The cache folder is too deep for a generated name that fits within MAX_PATH.
            string marker = "ArclightMono:" + hash;
            if (File.Exists(LongPath.For(output)))
            {
                // Only a file of ours is reused or replaced: anything else at this path is left alone.
                if (!IsGenerated(output)) return null;
                // Reused only while its audio is the bytes written (hash in the marker), its import settings still match the
                // source's, and it decodes to the source's rate and length.
                var cached = (AudioImporter)AssetImporter.GetAtPath(output);
                var reused = AssetDatabase.LoadAssetAtPath<AudioClip>(output);
                if (cached.userData == marker + ":" + FingerprintService.FileHash(output) && SameSettings(cached, importer) &&
                    reused && reused.channels == 1 && reused.frequency == clip.frequency && reused.samples == clip.samples)
                {
                    MarkUsed(output);
                    return reused;
                }
            }

            var samples = DecodedSamples(path, out int frequency);
            if (samples == null) return null;
            int frames = samples.Length / 2;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float left = samples[2 * i], right = samples[2 * i + 1];
                if (left != right) return null; // Not the same sound on both channels.
                float boosted = left * Boost;
                if (boosted > 1f || boosted < -1f) return null; // Would clip.
                mono[i] = boosted;
            }

            var wav = FloatWav(mono, frequency);
            string copiedMeta = CopiedAudioMeta(meta, marker + ":" + FingerprintService.Hash(wav));
            if (copiedMeta == null) return null;
            if (File.Exists(LongPath.For(output))) AssetDatabase.DeleteAsset(output); // A stale generated copy of ours (checked above).
            File.WriteAllText(LongPath.For(output + ".meta"), copiedMeta); // Before the audio, so it is never imported with defaults.
            File.WriteAllBytes(LongPath.For(output), wav);
            AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
            var result = AssetDatabase.LoadAssetAtPath<AudioClip>(output);
            if (!result || result.channels != 1 || result.frequency != clip.frequency || result.samples != clip.samples)
            {
                AssetDatabase.DeleteAsset(output);
                return null;
            }
            MarkUsed(output);
            return result;
        }

        // The source's bytes and its raw .meta folded into the signature that names and marks the copy. Memoized in
        // SessionState (it survives Play Mode's domain reloads) by Unity's dependency hash, which covers the source's
        // content and importer, and by the .meta's hash, since the signature folds in the .meta's bytes: a build no
        // longer re-reads a big audio file, only clips new to the editor session pay the full read. IO failures are not memoized.
        private static string SourceSignature(string path, string meta)
        {
            string key;
            try { key = "Arclight.Optimizer.MonoSourceSignature." + FingerprintService.Version + "." + AssetDatabase.GetAssetDependencyHash(path) + "." + FingerprintService.FileHash(meta); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return null; }
            string cached = SessionState.GetString(key, "");
            if (cached.Length > 0) return cached;
            string hash = FingerprintService.Hash(File.ReadAllBytes(LongPath.For(path)).Concat(File.ReadAllBytes(LongPath.For(meta))).ToArray());
            SessionState.SetString(key, hash);
            return hash;
        }

        // Unity's own tools still fail on paths past MAX_PATH, so the generated clip keeps its full path, including the
        // longest file written ("<name>.wav.meta"), within 259 characters: the source name (shortened as needed) plus 12
        // hex digits of the source signature, mirroring GenerationCoordinator.OutputPath. A cache folder too deep to
        // leave room returns null rather than throwing: a clip left as it is beats a throw, which would stop every
        // avatar-wide pass after this one.
        private static string OutputPath(string folder, string safeName, string signature)
        {
            string tail = "_" + signature.Substring(0, 12) + "_mono.wav";
            int budget = 259 - Path.GetFullPath(folder + "/" + tail + ".meta").Length;
            if (budget < 0) return null;
            return folder + "/" + safeName.Substring(0, Math.Min(safeName.Length, budget)) + tail;
        }

        // The source's samples as Unity decodes them, read from a temporary uncompressed copy (the source and its
        // importer are never changed).
        private static float[] DecodedSamples(string path, out int frequency)
        {
            frequency = 0;
            // A fresh name each time, so the cleanup below only ever deletes this call's own copy.
            string temp = Folder + "/" + Guid.NewGuid().ToString("N") + "_decode" + Path.GetExtension(path);
            try
            {
                File.Copy(LongPath.For(path), LongPath.For(temp), true);
                AssetDatabase.ImportAsset(temp, ImportAssetOptions.ForceSynchronousImport);
                var importer = (AudioImporter)AssetImporter.GetAtPath(temp);
                var settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.PCM;
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                importer.defaultSampleSettings = settings;
                importer.forceToMono = false;
                importer.loadInBackground = false;
                foreach (string platform in new[] { "Standalone", "Android" }) importer.ClearSampleSettingOverride(platform);
                importer.SaveAndReimport();
                var decoded = AssetDatabase.LoadAssetAtPath<AudioClip>(temp);
                if (!decoded || decoded.channels != 2 || !decoded.LoadAudioData()) return null;
                var data = new float[decoded.samples * 2];
                if (!decoded.GetData(data, 0)) return null;
                frequency = decoded.frequency;
                return data;
            }
            finally { AssetDatabase.DeleteAsset(temp); }
        }

        // The source's .meta with a new GUID, our user data and no asset-bundle assignment, so the copy imports with
        // exactly the source's audio settings. Null for any layout other than one AudioImporter block.
        private static string CopiedAudioMeta(string sourceMeta, string userData)
        {
            string text = File.ReadAllText(LongPath.For(sourceMeta));
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(new[] { newline }, StringSplitOptions.None);
            int guid = 0, importer = 0, user = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("guid: ", StringComparison.Ordinal)) { lines[i] = "guid: " + Guid.NewGuid().ToString("N"); guid++; }
                else if (lines[i] == "AudioImporter:") importer++;
                else if (lines[i].StartsWith("  userData:", StringComparison.Ordinal)) { lines[i] = "  userData: " + userData; user++; }
                else if (lines[i].StartsWith("  assetBundleName:", StringComparison.Ordinal)) lines[i] = "  assetBundleName: ";
                else if (lines[i].StartsWith("  assetBundleVariant:", StringComparison.Ordinal)) lines[i] = "  assetBundleVariant: ";
            }
            return guid == 1 && importer == 1 && user == 1 ? string.Join(newline, lines) : null;
        }

        // Last build day each generated clip was used, for the cache cleanup's 30-day rule. Kept in Library: it is
        // per machine, like the builds that refresh it, and changing it never reimports anything. Test fixtures
        // redirect the cache folder into themselves (AutomaticTextureOptimizer.CacheFolder); the record follows that
        // redirect, so fixtures never touch the shared machine-wide file.
        private static string UsageFile => AutomaticTextureOptimizer.CacheFolder == AvatarTextureOptimizer.OutputFolder
            ? "Library/AvatarTextureOptimizer/audio-usage.json"
            : AutomaticTextureOptimizer.CacheFolder + "/audio-usage.json";
        [Serializable] private sealed class Usage { public List<string> paths = new List<string>(); public List<int> days = new List<int>(); }

        internal static Dictionary<string, int> LoadUsage()
        {
            var usage = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(UsageFile)) return usage;
                var stored = JsonUtility.FromJson<Usage>(File.ReadAllText(UsageFile));
                for (int i = 0; stored != null && i < Math.Min(stored.paths.Count, stored.days.Count); i++) usage[stored.paths[i]] = stored.days[i];
            }
            catch (Exception) { } // An unreadable record only restarts the 30 days.
            return usage;
        }

        internal static void SaveUsage(Dictionary<string, int> usage)
        {
            var stored = new Usage();
            foreach (var pair in usage.OrderBy(p => p.Key, StringComparer.Ordinal)) { stored.paths.Add(pair.Key); stored.days.Add(pair.Value); }
            Directory.CreateDirectory(Path.GetDirectoryName(UsageFile));
            File.WriteAllText(UsageFile, JsonUtility.ToJson(stored));
        }

        // Shared last-used record for generated files the cache cleanup tracks (mono clips and texture crops).
        // Marking used loaded and rewrote the whole file per clip, so marks land in usagePending instead and the
        // mono pass saves once at its end (try/finally in Convert, so a pass that throws part-way still keeps what
        // it marked). Passes with no end point here (texture crops) have their marks dropped, not written late:
        // the next pass starts from the on-disk record, so at most one build's crop re-touches are lost. Entries
        // only start their staleness clock, and the cache cleanup records any generated file it finds unrecorded,
        // so the cost is at most 30 more days before that crop goes.
        private static Dictionary<string, int> usagePending;

        internal static void MarkUsed(string path)
        {
            if (usagePending == null) usagePending = LoadUsage();
            usagePending[path] = CacheCleanup.Today;
        }

        // Write the marks collected so far and forget them; the next pass reloads, which also drops records the
        // cache cleanup has since pruned.
        internal static void FlushUsage()
        {
            if (usagePending != null) SaveUsage(usagePending);
            usagePending = null;
        }

        // The copy imports with the source's .meta, so its settings match the source's until someone edits either.
        private static bool SameSettings(AudioImporter a, AudioImporter b)
        {
            if (a.forceToMono != b.forceToMono || a.loadInBackground != b.loadInBackground || a.ambisonic != b.ambisonic ||
                !a.defaultSampleSettings.Equals(b.defaultSampleSettings)) return false;
            foreach (string platform in new[] { "Standalone", "Android" })
                if (a.ContainsSampleSettingsOverride(platform) != b.ContainsSampleSettingsOverride(platform) ||
                    !a.GetOverrideSampleSettings(platform).Equals(b.GetOverrideSampleSettings(platform))) return false;
            return true;
        }

        internal static bool IsGenerated(string path) =>
            path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && AssetImporter.GetAtPath(path) is AudioImporter importer &&
            (importer.userData ?? "").StartsWith("ArclightMono:", StringComparison.Ordinal);

        // IEEE-float WAV, so no precision is lost before Unity applies the source's own compression.
        private static byte[] FloatWav(float[] samples, int frequency)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 4);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)3); writer.Write((short)1); writer.Write(frequency); writer.Write(frequency * 4);
                writer.Write((short)4); writer.Write((short)32);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 4);
                foreach (float sample in samples) writer.Write(sample);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
