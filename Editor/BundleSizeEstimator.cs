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

        // Hold this around work that estimates repeatedly, so the object scan happens once. A no-op when no bundle is built.
        internal static KeepAlive KeepObjectsAlive() => new KeepAlive(WillBuild);

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
            string dependencyHash = AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
            // Survives domain reloads within an editor session; keyed on the asset content, its import settings and the target.
            string key = "Arclight.Optimizer.BundleBytes." + EditorUserBuildSettings.activeBuildTarget + "." + dependencyHash;
            if (long.TryParse(SessionState.GetString(key, ""), out bytes) && bytes > 0) return true;
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
                if (!manifest || !File.Exists(file)) return false;
                bytes = new FileInfo(file).Length;
                if (bytes <= 0) return false;
                SessionState.SetString(key, bytes.ToString());
                return true;
            }
            catch (Exception) { bytes = 0; return false; }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { }
            }
        }
    }
}
