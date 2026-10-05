using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Automatic removal of cached outputs that no recent build has used. Every cached PNG can be
    // regenerated from its source, so removal only costs a later rebuild. Only PNGs this optimizer
    // generated (identified by importer userData) are touched; they move to the system trash.
    internal static class CacheCleanup
    {
        internal const int UnusedDays = 30;
        private const string OwnerPrefix = "AvatarTextureOptimizer:";

        internal static int Today =>
            (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;

        internal sealed class Plan
        {
            internal readonly List<string> Assets = new List<string>();
            internal readonly List<string> StagingFiles = new List<string>();
            internal long Bytes;
            internal int StaleEntries;
            internal int Today;
            internal int UnusedDays;
            internal bool IsEmpty => Assets.Count == 0 && StagingFiles.Count == 0 && StaleEntries == 0;
        }

        internal static Plan Prepare(string folder = AvatarTextureOptimizer.OutputFolder, int today = -1, int unusedDays = UnusedDays)
        {
            var plan = new Plan { Today = today < 0 ? Today : today, UnusedDays = unusedDays };
            if (!AssetDatabase.IsValidFolder(folder)) return plan;
            TextureSafety.ValidateFolder(folder);
            var cache = LoadOwnedCache(folder);
            var kept = new HashSet<string>(StringComparer.Ordinal);
            if (cache)
            {
                foreach (var mapping in cache.mappings)
                {
                    if (IsStale(plan, mapping.lastUsedDay)) plan.StaleEntries++;
                    else if (mapping.replacement) kept.Add(AssetDatabase.GetAssetPath(mapping.replacement));
                }
                plan.StaleEntries += cache.unchangedTextures.Count(e => IsStale(plan, e.lastUsedDay)) +
                    (cache.nonBeneficialTextures?.Count(e => IsStale(plan, e.lastUsedDay)) ?? 0);
            }
            foreach (string file in Directory.GetFiles(folder).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (file.EndsWith(".png.writing", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).StartsWith(SourceImages.TempPrefix, StringComparison.Ordinal))
                {
                    plan.StagingFiles.Add(file);
                    plan.Bytes += new FileInfo(file).Length;
                }
                else if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !kept.Contains(file) && IsGeneratedOutput(file))
                {
                    plan.Assets.Add(file);
                    plan.Bytes += new FileInfo(file).Length;
                }
            }
            PrepareTracked(plan, folder + "/Audio", AudioMonoConverter.IsGenerated, decodeCopies: true);
            PrepareTracked(plan, folder + "/Crops", TextureCropper.IsGenerated, decodeCopies: false);
            return plan;
        }

        // Generated mono clips and texture crops (identified by importer userData) follow the same 30-day rule, using
        // the last-used record their passes keep. A file with no record yet starts its 30 days now. Leftover temporary
        // audio decode copies from an interrupted build are removed.
        private static void PrepareTracked(Plan plan, string folder, Func<string, bool> generated, bool decodeCopies)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            var usage = AudioMonoConverter.LoadUsage();
            bool recorded = false;
            foreach (string file in Directory.GetFiles(folder).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (file.EndsWith(".meta", StringComparison.Ordinal)) continue;
                bool decode = decodeCopies && Path.GetFileNameWithoutExtension(file).EndsWith("_decode", StringComparison.Ordinal);
                if (!decode && !generated(file)) continue;
                if (!decode && !usage.TryGetValue(file, out int day)) { usage[file] = plan.Today; recorded = true; continue; }
                if (!decode && !IsStale(plan, usage[file])) continue;
                plan.Assets.Add(file);
                plan.Bytes += new FileInfo(file).Length;
            }
            if (recorded) AudioMonoConverter.SaveUsage(usage);
        }

        // Tests pass moveToTrash: false so fixtures do not fill the user's trash.
        internal static void Apply(Plan plan, string folder = AvatarTextureOptimizer.OutputFolder, bool moveToTrash = true)
        {
            var cache = LoadOwnedCache(folder);
            if (cache)
            {
                cache.mappings.RemoveAll(m => IsStale(plan, m.lastUsedDay));
                cache.unchangedTextures.RemoveAll(e => IsStale(plan, e.lastUsedDay));
                cache.nonBeneficialTextures?.RemoveAll(e => IsStale(plan, e.lastUsedDay));
                EditorUtility.SetDirty(cache);
                AssetDatabase.SaveAssetIfDirty(cache);
            }
            var failed = new List<string>();
            if (plan.Assets.Count > 0 && (moveToTrash
                    ? !AssetDatabase.MoveAssetsToTrash(plan.Assets.ToArray(), failed)
                    : !AssetDatabase.DeleteAssets(plan.Assets.ToArray(), failed)) && failed.Count > 0)
                throw new IOException("Could not remove: " + string.Join(", ", failed));
            var usage = AudioMonoConverter.LoadUsage();
            if (plan.Assets.Count(usage.Remove) > 0) AudioMonoConverter.SaveUsage(usage);
            foreach (string file in plan.StagingFiles)
                if (File.Exists(file)) File.Delete(file);
        }

        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.Ordinal);

        // Run after the synchronous avatar passes refresh last-used dates and finish generating PNGs.
        // Coalesce avatars sharing one cache; do not scan or save anything during inspector repaint.
        internal static void Schedule(string folder, bool moveToTrash = true)
        {
            if (!Pending.Add(folder)) return;
            EditorApplication.delayCall += () =>
            {
                Pending.Remove(folder);
                try
                {
                    var plan = Prepare(folder);
                    if (!plan.IsEmpty) Apply(plan, folder, moveToTrash);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning("Arclight Optimizer: Automatic cache cleanup stopped: " + e.Message);
                }
            };
        }
        private static bool IsStale(Plan plan, int lastUsedDay) =>
            lastUsedDay <= 0 || plan.Today - lastUsedDay > plan.UnusedDays;

        private static bool IsGeneratedOutput(string path) =>
            AssetImporter.GetAtPath(path) is TextureImporter importer &&
            (importer.userData ?? "").StartsWith(OwnerPrefix, StringComparison.Ordinal);

        private static TextureOptimizationManifest LoadOwnedCache(string folder)
        {
            string path = folder + "/" + AutomaticTextureOptimizer.CacheFileName;
            if (!File.Exists(path)) return null;
            var cache = AssetDatabase.LoadAssetAtPath<TextureOptimizationManifest>(path);
            if (!cache || cache.cacheOwner != AutomaticTextureOptimizer.CacheOwner)
                throw new InvalidOperationException("The cache manifest is not owned by this optimizer; nothing was removed.");
            return cache;
        }
    }
}
