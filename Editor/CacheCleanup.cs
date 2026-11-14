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
        // Analysis-only records (unchanged/not-smaller verdicts) have no PNG on disk; evicting them on the PNG clock
        // cost nothing to gain and made anyone building less than monthly re-run the pixel decode and the bundle
        // size estimate every single time. They age on a longer clock; active avatars refresh on every cache hit.
        internal const int AnalysisUnusedDays = 4 * UnusedDays;
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
            internal int AnalysisUnusedDays;
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
            if (!AssetDatabase.IsValidFolder(OldFolder)) return;
            if (AssetDatabase.IsValidFolder(AvatarTextureOptimizer.OutputFolder))
            {
                // A batchmode build can create the new folder (EnsureFolder) before this delayCall runs, or the
                // delayCall never runs at all; say so instead of silently orphaning the old cache's GUIDs, mappings and reports.
                UnityEngine.Debug.LogWarning("Arclight Optimizer: the pre-1.3.7 cache at " + OldFolder + " was not moved because " + AvatarTextureOptimizer.OutputFolder + " already exists; merge or delete " + OldFolder + " manually.");
                return;
            }
            string error = AssetDatabase.MoveAsset(OldFolder, AvatarTextureOptimizer.OutputFolder);
            if (!string.IsNullOrEmpty(error)) { UnityEngine.Debug.LogWarning("Arclight Optimizer: could not move the cache to " + AvatarTextureOptimizer.OutputFolder + ": " + error); return; }
            string parent = Path.GetDirectoryName(OldFolder).Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(parent) && AssetDatabase.GetSubFolders(parent).Length == 0 && AssetDatabase.FindAssets("", new[] { parent }).Length == 0)
                AssetDatabase.DeleteAsset(parent);
            UnityEngine.Debug.Log("Arclight Optimizer: moved the cache to " + AvatarTextureOptimizer.OutputFolder + ".");
        }

        internal static Plan Prepare(string folder = AvatarTextureOptimizer.OutputFolder, int today = -1, int unusedDays = UnusedDays)
        {
            // Manual clear (negative window) still clears everything; otherwise analysis records never age faster
            // than their floor, and never slower than a caller's explicit window.
            var plan = new Plan { Today = today < 0 ? Today : today, UnusedDays = unusedDays,
                AnalysisUnusedDays = unusedDays < 0 ? -1 : Math.Max(unusedDays, AnalysisUnusedDays) };
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
                // Read/Write-off copies have a file on disk, so they age on the file clock like generated mappings.
                foreach (var drop in cache.readableDrops ?? new List<ReadableDropCopy>())
                {
                    if (IsStale(plan, drop.lastUsedDay)) plan.StaleEntries++;
                    else if (drop.copy) kept.Add(AssetDatabase.GetAssetPath(drop.copy));
                }
                plan.StaleEntries += cache.unchangedTextures.Count(e => IsAnalysisStale(plan, e.lastUsedDay)) +
                    (cache.nonBeneficialTextures?.Count(e => IsAnalysisStale(plan, e.lastUsedDay)) ?? 0);
            }
            foreach (string file in Directory.GetFiles(folder).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))
            {
                // Only names Arclight makes: "_flatten_" plus a 32-digit GUID (SourceImages, TextureSafety), or a PNG being written.
                if (file.EndsWith(".meta", StringComparison.Ordinal) &&
                    !File.Exists(file.Substring(0, file.Length - ".meta".Length)) &&
                    File.ReadAllText(file).Contains(OwnerPrefix))
                    plan.StagingFiles.Add(file); // The orphaned meta of an owned generated PNG (its asset is already gone).
                else if (file.EndsWith(".png.writing", StringComparison.OrdinalIgnoreCase) ||
                    System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^" + SourceImages.TempPrefix + @"[0-9a-f]{32}\.[a-z0-9]+$"))
                {
                    plan.StagingFiles.Add(file);
                    plan.Bytes += new FileInfo(file).Length;
                }
                // Generated outputs are PNGs; a Read/Write-off copy keeps its source's file type (ReadableTextureCopies).
                else if ((file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || SourceImages.IsFlattened(file)) && !kept.Contains(file) && IsGeneratedOutput(file))
                {
                    plan.Assets.Add(file);
                    plan.Bytes += new FileInfo(file).Length;
                }
            }
            // Each folder's orphan metas carry that folder's own userData marker, not the main folder's.
            PrepareTracked(plan, folder + "/Audio", AudioMonoConverter.IsGenerated, decodeCopies: true, ownerMarker: "ArclightMono:");
            PrepareTracked(plan, folder + "/Audio/Vorbis", AudioCompressor.IsGenerated, decodeCopies: false, ownerMarker: "ArclightVorbis:");
            PrepareTracked(plan, folder + "/MenuIcons", MenuIconOptimizer.IsGenerated, decodeCopies: false, ownerMarker: "ArclightIcon:");
            PrepareTracked(plan, folder + "/Crops", TextureCropper.IsGenerated, decodeCopies: false, ownerMarker: "ArclightCrop:");
            return plan;
        }

        // Generated mono clips and texture crops (identified by importer userData) follow the same 30-day rule, using
        // the last-used record their passes keep. A file with no record yet starts its 30 days now, except on a manual clear. Leftover temporary
        // audio decode copies from an interrupted build are removed.
        private static void PrepareTracked(Plan plan, string folder, Func<string, bool> generated, bool decodeCopies, string ownerMarker)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            var usage = AudioMonoConverter.LoadUsage();
            bool recorded = false;
            foreach (string file in Directory.GetFiles(folder).Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (file.EndsWith(".meta", StringComparison.Ordinal))
                {
                    // An orphaned meta of a generated file (its asset is already gone, our userData still inside)
                    // goes with the sweep instead of waiting for Unity's own meta garbage collection.
                    string asset = file.Substring(0, file.Length - ".meta".Length);
                    if (!File.Exists(asset) && File.ReadAllText(file).Contains(ownerMarker)) plan.StagingFiles.Add(file);
                    continue;
                }
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
            // Load before deleting: a foreign manifest must stop Apply while every file is still untouched.
            var cache = LoadOwnedCache(folder);
            var failed = new List<string>();
            bool allRemoved = plan.Assets.Count == 0 || (moveToTrash
                ? AssetDatabase.MoveAssetsToTrash(plan.Assets.ToArray(), failed)
                : AssetDatabase.DeleteAssets(plan.Assets.ToArray(), failed));
            // A false return that names no file still guarantees nothing was removed: name them all.
            if (!allRemoved && failed.Count == 0) failed.AddRange(plan.Assets);
            // The files go first, the records after: pruning the manifest up front dropped entries whose PNGs
            // survived a partial failure, stranding the files with no entry to keep them tracked. A stale entry
            // whose file the API failed to remove stays, so the next sweep removes the pair together.
            if (cache)
            {
                cache.mappings.RemoveAll(m => IsStale(plan, m.lastUsedDay) && !SurvivedDeletion(m.replacement, failed));
                cache.readableDrops?.RemoveAll(d => IsStale(plan, d.lastUsedDay) && !SurvivedDeletion(d.copy, failed));
                cache.unchangedTextures.RemoveAll(e => IsAnalysisStale(plan, e.lastUsedDay));
                cache.nonBeneficialTextures?.RemoveAll(e => IsAnalysisStale(plan, e.lastUsedDay));
                EditorUtility.SetDirty(cache);
                AssetDatabase.SaveAssetIfDirty(cache);
            }
            var usage = AudioMonoConverter.LoadUsage();
            bool usageChanged = false;
            // Forget every record whose generated file is no longer on disk, not just this sweep's removals:
            // clips deleted by hand, or removed after an earlier failed sweep, would leak their record forever.
            foreach (string path in usage.Keys.ToList())
                if (!File.Exists(path)) { usage.Remove(path); usageChanged = true; }
            if (usageChanged) AudioMonoConverter.SaveUsage(usage);
            foreach (string file in plan.StagingFiles)
            {
                if (File.Exists(file)) File.Delete(file);
                if (File.Exists(file + ".meta")) File.Delete(file + ".meta"); // The paired meta goes too, or it waits for Unity's own meta GC.
            }
            if (failed.Count > 0)
                throw new IOException("Could not remove: " + string.Join(", ", failed));
        }

        // True only for a mapping whose PNG the removal API reported as failed and that is really still on disk.
        private static bool SurvivedDeletion(UnityEngine.Object replacement, List<string> failed)
        {
            string path = replacement ? AssetDatabase.GetAssetPath(replacement) : "";
            return path.Length > 0 && failed.Contains(path) && File.Exists(path);
        }

        private static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.Ordinal);

        // SessionState survives the domain reloads that discard the static Pending set and the queued delayCall.
        // One key holds one "folder<TAB>trash flag" line per scheduled folder; ResumeScheduled re-arms them.
        internal const string PendingKey = "dev.okarin.avatar-texture-optimizer.pending-cleanup";

        // Run after the synchronous avatar passes refresh last-used dates and finish generating PNGs.
        // Coalesce avatars sharing one cache; do not scan or save anything during inspector repaint.
        internal static void Schedule(string folder, bool moveToTrash = true)
        {
            if (!Pending.Add(folder)) return;
            var markers = MarkerLines();
            if (!markers.Any(m => MarkerFolder(m) == folder)) markers.Add(folder + "\t" + (moveToTrash ? "1" : "0"));
            SessionState.SetString(PendingKey, string.Join("\n", markers));
            EditorApplication.delayCall += () => RunScheduled(folder, moveToTrash);
        }

        private static List<string> MarkerLines() =>
            SessionState.GetString(PendingKey, "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        private static string MarkerFolder(string marker) => marker.Split('\t')[0];
        private static void Forget(string folder)
        {
            var kept = MarkerLines().Where(m => MarkerFolder(m) != folder).ToList();
            if (kept.Count == 0) SessionState.EraseString(PendingKey);
            else SessionState.SetString(PendingKey, string.Join("\n", kept));
        }

        // InitializeOnLoad methods run after every reload, when the previous life's delayCall and Pending are
        // already gone; re-arm each still-marked folder through the normal Schedule path so orphaned staging
        // files do not wait for some later build to schedule a cleanup. A folder that no longer exists is
        // forgotten; a cleanup whose Apply threw keeps its marker, so the next reload retries it.
        [InitializeOnLoadMethod]
        private static void ResumeScheduled()
        {
            foreach (string marker in MarkerLines())
            {
                string folder = MarkerFolder(marker);
                if (!AssetDatabase.IsValidFolder(folder)) { Forget(folder); continue; }
                Schedule(folder, marker.EndsWith("\t1", StringComparison.Ordinal));
            }
        }

        private static void RunScheduled(string folder, bool moveToTrash)
        {
            Pending.Remove(folder);
            try
            {
                var plan = Prepare(folder);
                if (!plan.IsEmpty) Apply(plan, folder, moveToTrash);
                Forget(folder);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("Arclight Optimizer: Automatic cache cleanup stopped: " + e.Message);
            }
        }
        private static bool IsStale(Plan plan, int lastUsedDay) =>
            lastUsedDay <= 0 || plan.Today - lastUsedDay > plan.UnusedDays;

        private static bool IsAnalysisStale(Plan plan, int lastUsedDay) =>
            lastUsedDay <= 0 || plan.Today - lastUsedDay > plan.AnalysisUnusedDays;

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
