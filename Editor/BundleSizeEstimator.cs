using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // What an avatar download carries for a texture is its compressed texture data inside the bundle (the imported
    // GPU format with mips, then the bundle's own compression), not the PNG. This builds a throwaway bundle holding
    // only that one asset and measures the file, so source and candidate are compared on the real import settings
    // for the active build target. The bundle also compresses across assets, so this is a close proxy, not the
    // exact byte count of the final upload. Nothing here touches importer or asset-bundle names of any asset.
    internal static class BundleSizeEstimator
    {
        // Tests replace the build with a stub. A result of 0 or less means "no estimate available".
        internal static Func<string, long> MeasureOverride;

        // A bundle build unloads every object that only managed code references, which would destroy in-memory
        // objects other build plugins hold mid-build (and our own manifests). The scope marks every loaded object
        // DontUnloadUnusedAsset and restores the original flags afterwards. Nested scopes share the outer one.
        private static int guardDepth;
        private static System.Collections.Generic.List<(UnityEngine.Object Object, HideFlags Original)> guarded;

        internal sealed class KeepAlive : IDisposable
        {
            private bool disposed;

            internal KeepAlive(bool active)
            {
                if (!active || guardDepth++ > 0) { if (!active) disposed = true; return; }
                guarded = new System.Collections.Generic.List<(UnityEngine.Object, HideFlags)>();
                foreach (var candidate in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
                {
                    try
                    {
                        if (!candidate || (candidate.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0) continue;
                        guarded.Add((candidate, candidate.hideFlags));
                        candidate.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    }
                    catch (Exception) { }
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (--guardDepth > 0) return;
                foreach (var entry in guarded)
                    try { if (entry.Object) entry.Object.hideFlags = entry.Original; } catch (Exception) { }
                guarded = null;
            }
        }

        private static bool WillBuild => MeasureOverride == null && !EditorApplication.isPlayingOrWillChangePlaymode;

        // Whether an estimate can be attempted now (not in Play Mode; with a test stub, whether it gives estimates at all).
        internal static bool CanEstimate => MeasureOverride != null ? MeasureOverride(string.Empty) > 0 : !EditorApplication.isPlayingOrWillChangePlaymode;

        // Hold this around work that estimates repeatedly, so the object scan happens once. A no-op when no bundle is built.
        internal static KeepAlive KeepObjectsAlive() => new KeepAlive(WillBuild);

        // A failed estimate (manifest missing, zero bytes, exception) is systemic, not per-texture: after the first
        // failure for a build target, stop paying a throwaway bundle build for every later texture. Retried when
        // the target changes; a successful target is never poisoned, and the memo below still records successes.
        private static BuildTarget? failedTarget;

        // False when no estimate could be made (Play Mode, a failed build); callers fall back to the PNG file size.
        internal static bool TryMeasure(string assetPath, out long bytes)
        {
            bytes = 0;
            if (MeasureOverride != null)
            {
                bytes = MeasureOverride(assetPath);
                return bytes > 0;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (failedTarget == target) return false;
            string dependencyHash = AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
            // Survives domain reloads within an editor session; keyed on the asset content, its import settings and the target.
            string key = "Arclight.Optimizer.BundleBytes." + target + "." + dependencyHash;
            if (long.TryParse(SessionState.GetString(key, ""), out bytes) && bytes > 0) return true;
            // Then the memo that survives the editor session (see PersistentBytes).
            if (PersistentBytes.TryGet(PersistentKey(target, dependencyHash), out bytes))
            {
                SessionState.SetString(key, bytes.ToString());
                return true;
            }
            bytes = 0;

            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "ArclightBundleSize", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var build = new AssetBundleBuild { assetBundleName = "size", assetNames = new[] { assetPath } };
                AssetBundleManifest manifest;
                using (KeepObjectsAlive())
                    manifest = BuildPipeline.BuildAssetBundles(directory, new[] { build }, BuildAssetBundleOptions.None,
                        EditorUserBuildSettings.activeBuildTarget);
                string file = Path.Combine(directory, "size");
                if (!manifest || !File.Exists(file)) { failedTarget = target; return false; }
                bytes = new FileInfo(file).Length;
                if (bytes <= 0) { failedTarget = target; return false; }
                SessionState.SetString(key, bytes.ToString());
                PersistentBytes.Set(PersistentKey(target, dependencyHash), bytes);
                return true;
            }
            catch (Exception) { bytes = 0; failedTarget = target; return false; }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        // A measurement is a function of the asset's content and import settings (its dependency hash), the build target and
        // the Unity version that compresses it; the package version is not part of it.
        private static string PersistentKey(BuildTarget target, string dependencyHash) =>
            target + "|" + Application.unityVersion + "|" + dependencyHash;

        // Measures every not yet memoized asset in ONE bundle build (one single-asset bundle each) and records the sizes
        // where TryMeasure finds them, so a build whose textures all miss the cache pays one throwaway build for its sources
        // instead of one per texture. Each bundle holds exactly the asset TryMeasure's own bundle would hold; every bundle
        // name has the same length as TryMeasure's, so the name adds the same bytes. Best effort: any failure records
        // nothing (failures are never memoized here), and each asset is then measured on its own by TryMeasure, which keeps
        // its failed-target rule. Returns how many sizes were recorded.
        internal static int MeasureBatch(System.Collections.Generic.IEnumerable<string> assetPaths)
        {
            if (!WillBuild) return 0;
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (failedTarget == target) return 0;
            var pending = new System.Collections.Generic.List<(string Path, string Hash)>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (string path in assetPaths)
            {
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                string hash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                if (long.TryParse(SessionState.GetString("Arclight.Optimizer.BundleBytes." + target + "." + hash, ""), out long known) && known > 0) continue;
                if (PersistentBytes.TryGet(PersistentKey(target, hash), out _)) continue;
                pending.Add((path, hash));
            }
            // One asset gains nothing from a batch; names run s000..szzz.
            if (pending.Count < 2 || pending.Count > 36 * 36 * 36) return 0;
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            string Name(int i) => "s" + digits[i / 1296 % 36] + digits[i / 36 % 36] + digits[i % 36];
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "ArclightBundleSize", Guid.NewGuid().ToString("N"));
            int recorded = 0;
            try
            {
                Directory.CreateDirectory(directory);
                var builds = new AssetBundleBuild[pending.Count];
                for (int i = 0; i < builds.Length; i++) builds[i] = new AssetBundleBuild { assetBundleName = Name(i), assetNames = new[] { pending[i].Path } };
                AssetBundleManifest manifest;
                using (KeepObjectsAlive())
                    manifest = BuildPipeline.BuildAssetBundles(directory, builds, BuildAssetBundleOptions.None, target);
                if (!manifest) return 0;
                for (int i = 0; i < builds.Length; i++)
                {
                    string file = Path.Combine(directory, Name(i));
                    long bytes = File.Exists(file) ? new FileInfo(file).Length : 0;
                    if (bytes <= 0) continue;
                    SessionState.SetString("Arclight.Optimizer.BundleBytes." + target + "." + pending[i].Hash, bytes.ToString());
                    PersistentBytes.Set(PersistentKey(target, pending[i].Hash), bytes);
                    recorded++;
                }
                return recorded;
            }
            catch (Exception e) when (!(e is OperationCanceledException)) { return recorded; }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
            }
        }

        // Measured sizes kept across editor sessions, so an upload after a restart does not pay every throwaway bundle build
        // again. One "key<TAB>bytes<TAB>day" line per measurement in the project's Library folder (never version-controlled,
        // like NotUniform's memo); a later line for a key replaces an earlier one. Entries unused for
        // CacheCleanup.AnalysisUnusedDays are dropped when the file is next loaded. Only successful measurements are written.
        // Fails closed to "not known": an unreadable file, a malformed line or a failed write only means the bundle is built
        // again, never that a wrong size is used. Tests that stub the measurement never reach it.
        internal static class PersistentBytes
        {
            internal static string FileName = "Library/AvatarTextureOptimizer/bundle-bytes.txt";
            private static System.Collections.Generic.Dictionary<string, (long Bytes, int Day)> known;

            // Tests point FileName elsewhere; the next access loads that file.
            internal static void Reload() => known = null;

            internal static bool TryGet(string key, out long bytes)
            {
                bytes = 0;
                if (!Load().TryGetValue(key, out var entry) || entry.Bytes <= 0) return false;
                bytes = entry.Bytes;
                if (entry.Day != CacheCleanup.Today) Set(key, bytes); // Day granularity: at most one line per key per day.
                return true;
            }

            internal static void Set(string key, long bytes)
            {
                if (bytes <= 0 || key.IndexOf('\t') >= 0 || key.IndexOf('\n') >= 0) return;
                var entries = Load();
                if (entries.TryGetValue(key, out var entry) && entry.Bytes == bytes && entry.Day == CacheCleanup.Today) return;
                entries[key] = (bytes, CacheCleanup.Today);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                    File.AppendAllText(FileName, key + "\t" + bytes + "\t" + CacheCleanup.Today + "\n");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } // A memo only: measured again next session.
            }

            private static System.Collections.Generic.Dictionary<string, (long Bytes, int Day)> Load()
            {
                if (known != null) return known;
                known = new System.Collections.Generic.Dictionary<string, (long, int)>(StringComparer.Ordinal);
                try
                {
                    if (!File.Exists(FileName)) return known;
                    var lines = File.ReadAllLines(FileName);
                    int today = CacheCleanup.Today;
                    bool compact = false;
                    foreach (string line in lines)
                    {
                        var parts = line.Split('\t');
                        if (parts.Length != 3 || !long.TryParse(parts[1], out long bytes) || bytes <= 0 || !int.TryParse(parts[2], out int day)) { compact = true; continue; }
                        if (today - day > CacheCleanup.AnalysisUnusedDays) { compact = true; continue; }
                        if (known.ContainsKey(parts[0])) compact = true;
                        known[parts[0]] = (bytes, day);
                    }
                    if (compact)
                        File.WriteAllLines(FileName, System.Linq.Enumerable.Select(known, p => p.Key + "\t" + p.Value.Bytes + "\t" + p.Value.Day));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                return known;
            }
        }
    }
}
