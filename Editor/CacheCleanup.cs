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

        // Before 1.3.7 the cache lived in Assets/Arclight/Optimizer/Textures/Cache. Moving the folder with the AssetDatabase
        // keeps every GUID, so the cache's mappings (object references) and the reports stay valid. Only when the new folder
        // does not exist yet; an emptied Textures folder is removed.
        private const string OldFolder = "Assets/Arclight/Optimizer/Textures/Cache";

        [InitializeOnLoadMethod]
        private static void ScheduleMove() => EditorApplication.delayCall += MoveOldFolder;

        internal static void MoveOldFolder()
        {
            if (!AssetDatabase.IsValidFolder(OldFolder) || AssetDatabase.IsValidFolder(AvatarTextureOptimizer.OutputFolder)) return;
            string error = AssetDatabase.MoveAsset(OldFolder, AvatarTextureOptimizer.OutputFolder);
            if (!string.IsNullOrEmpty(error)) { UnityEngine.Debug.LogWarning("Arclight Optimizer: could not move the cache to " + AvatarTextureOptimizer.OutputFolder + ": " + error); return; }
            string parent = Path.GetDirectoryName(OldFolder).Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(parent) && AssetDatabase.GetSubFolders(parent).Length == 0 && AssetDatabase.FindAssets("", new[] { parent }).Length == 0)
                AssetDatabase.DeleteAsset(parent);
            UnityEngine.Debug.Log("Arclight Optimizer: moved the cache to " + AvatarTextureOptimizer.OutputFolder + ".");
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
                // Only names Arclight makes: "_flatten_" plus a 32-digit GUID (SourceImages, TextureSafety), or a PNG being written.
                if (file.EndsWith(".png.writing", StringComparison.OrdinalIgnoreCase) ||
                    System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^" + SourceImages.TempPrefix + @"[0-9a-f]{32}\.[a-z0-9]+$"))
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
        // the last-used record their passes keep. A file with no record yet starts its 30 days now, except on a manual clear. Leftover temporary
        // audio decode copies from an interrupted build are removed.
        private static void PrepareTracked(Plan plan, string folder, Func<string, bool> generated, bool decodeCopies)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            var usage = AudioMonoConverter.LoadUsage();
            bool recorded = false;
            foreach (string file in Directory.GetFiles(folder).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (file.EndsWith(".meta", StringComparison.Ordinal)) continue;
                // AudioMonoConverter names its decode copies by a GUID (12 hex digits of the clip hash before 1.2.3) plus "_decode".
                bool decode = decodeCopies && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(file), "^([0-9A-Fa-f]{12}|[0-9a-f]{32})_decode$");
                if (!decode && !generated(file)) continue;
                // A manual clear (UnusedDays < 0) removes every generated file, recorded or not.
                if (!decode && plan.UnusedDays >= 0 && !usage.ContainsKey(file)) { usage[file] = plan.Today; recorded = true; continue; }
                if (!decode && plan.UnusedDays >= 0 && !IsStale(plan, usage[file])) continue;
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
