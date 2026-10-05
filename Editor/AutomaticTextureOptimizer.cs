using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    internal static class AutomaticTextureOptimizer
    {
        internal const string CacheFileName = "TextureOptimizerCache.asset";
        internal const string CacheOwner = "dev.okarin.avatar-texture-optimizer/automatic-v1";

        // Test fixtures redirect the automatic cache and reports into their own folder. SessionState survives
        // Play Mode's domain reload and ends with the editor session. The redirect applies only while it
        // points inside an existing test fixture folder, so builds otherwise always use the fixed cache.
        internal const string TestCacheFolderKey = "dev.okarin.avatar-texture-optimizer.test-cache-folder";
        private const string TestFixturePrefix = "Assets/__TextureOptimizerTest_";

        internal static string CacheFolder
        {
            get
            {
                string redirect = SessionState.GetString(TestCacheFolderKey, "");
                if (!redirect.StartsWith(TestFixturePrefix, StringComparison.Ordinal)) return AvatarTextureOptimizer.OutputFolder;
                int slash = redirect.IndexOf('/', TestFixturePrefix.Length);
                string fixture = slash < 0 ? redirect : redirect.Substring(0, slash);
                return AssetDatabase.IsValidFolder(fixture) ? redirect : AvatarTextureOptimizer.OutputFolder;
            }
        }

        // Only the NDMF entrypoint calls this in production. The optional folder isolates test fixtures.
        internal static void Run(GameObject buildRoot, SubstitutionState state,
            Action<UnityEngine.Object, UnityEngine.Object> register,
            string folder = null,
            VirtualControllerContext animationContext = null,
            OptimizationProgress progress = null)
        {
            folder = folder ?? CacheFolder;
            if (state.Applied) { progress?.Dispose(); return; }
            var config = buildRoot.GetComponent<AvatarTextureOptimizer>();
            if (!config) { progress?.Dispose(); return; }
            bool enabled = config.enabled;
            bool optimizeTextures = config.optimizeTextures && !state.VrcfuryPending; // Coverage needs the complete animation.
            // The pipeline starts the report so earlier passes can add to it; tests calling this directly get a new one.
            var log = state.Report ?? new OptimizationLog(EditorApplication.isPlayingOrWillChangePlaymode);
            if (config.enabled && EditorApplication.isPlayingOrWillChangePlaymode && SceneReloadDisabled)
            {
                // Do not destroy even the authoring component when scene restoration is unavailable.
                state.Applied = true;
                log.Add(config, "Play preview requires Scene Reload so Unity can restore the original avatar when Play Mode ends.");
                try { log.Emit(buildRoot, state, folder); }
                finally { progress?.Dispose(); }
                return;
            }
            TextureOptimizationManifest selected = null;
            ScanResult scan = null;
            if (enabled && optimizeTextures && progress == null) progress = new OptimizationProgress();
            try
            {
                if (!enabled || !optimizeTextures) return;
                // Never call the legacy authoring generator here: it records Undo and edits scene configuration.
                scan = TextureUsageScanner.Scan(buildRoot, animationContext, state.SerializedAnimation, progress);
                foreach (string warning in scan.Warnings) log.Add(config, warning);
                if (TextureUsageScanner.RepairMipmapPadding)
                    log.Add(config, "Mipmap padding repair enabled for colour textures. Existing padding is rebuilt; larger replacement PNGs are allowed. Final download size and mip appearance require a build/visual comparison.");
                selected = Prepare(scan, folder, state.Summary, log.Add, progress);

                // Last cancellable checkpoint (a forced dialog poll); applying replacements cannot be cancelled.
                progress?.Overall("Ready to apply replacements", .95f);
                progress?.Overall("Applying replacements", .95f, false);
                TemporaryMaterialSubstituter.Apply(buildRoot, state, register, selected, log.Add, animationContext, scan);
                log.Capture(scan, selected, state.Summary, state, optimizeTextures);
            }
            catch (OperationCanceledException)
            {
                state.Cancelled = true;
                log.Add(config, "Substitution aborted: Texture optimization cancelled; original texture assignments retained. Completed outputs remain cached.");
            }
            catch (Exception e) { log.Add(config, "Automatic optimization skipped: " + e.Message); }
            finally
            {
                try
                {
                    state.Applied = true;
                    if (config) UnityEngine.Object.DestroyImmediate(config);
                    if (scan != null && !log.HasCaptured)
                        log.Capture(scan, selected, state.Summary, state, optimizeTextures);
                    if (selected) UnityEngine.Object.DestroyImmediate(selected);
                    if (enabled)
                    {
                        log.Emit(buildRoot, state, folder);
                        state.Report = log;
                        KeptTextureSummary.Record(buildRoot, scan, state);
                        if (scan != null) CacheCleanup.Schedule(folder);
                    }
                }
                finally { progress?.Dispose(); }
            }
        }

        internal static bool SceneReloadDisabled => EditorSettings.enterPlayModeOptionsEnabled &&
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableSceneReload) != 0;

        internal static TextureOptimizationManifest Prepare(ScanResult scan, string folder, GenerationSummary summary,
            Action<UnityEngine.Object, string> warn, OptimizationProgress progress = null)
        {
            var selected = ScriptableObject.CreateInstance<TextureOptimizationManifest>();
            // A bundle build (the compressed-size estimate) unloads objects that only managed code references.
            var keepAlive = BundleSizeEstimator.KeepObjectsAlive();
            TextureOptimizationManifest cache = null;
            bool cacheWarned = false;
            var coverageCache = new UvCoverageCache();
            summary.TextureResults.Clear();
            try
            {
                try
                {
                    int textureIndex = 0;
                    foreach (var group in scan.Groups)
                    {
                        var result = new TextureGenerationResult { Source = group.Source, Outcome = TextureResultKind.NotProcessed };
                        var timer = Stopwatch.StartNew();
                        bool measured = false;
                        try
                        {
                            progress?.BeginTexture(group.Source, textureIndex++, scan.Groups.Count);
                            progress?.Stage("Checking texture uses");
                            if (!group.ActiveUses.Any())
                            {
                                result.Outcome = TextureResultKind.NotSampled;
                                result.Reason = group.Warning ?? "No active sampled uses were found.";
                                continue;
                            }
                            if (group.Warning != null)
                            {
                                summary.Skipped++;
                                result.Outcome = TextureResultKind.Skipped;
                                result.Reason = group.Warning;
                                warn?.Invoke(group.Source, group.Warning);
                                continue;
                            }

                            measured = true;
                            result.Operation = group.RepairsPadding ? "Mipmap padding repair" : "Texture optimization";
                            string recipe = null, id = null; // Read by the RetainedException handler.
                            try
                            {
                                progress?.Stage("Fingerprinting texture");
                                recipe = FingerprintService.Recipe(group);
                                id = FingerprintService.Id(group.Source);
                                progress?.Stage("Checking cache");
                                if (!cache)
                                {
                                    try { cache = LoadOrCreateCache(folder); }
                                    catch (Exception e) when (!cacheWarned && !(e is OperationCanceledException))
                                    {
                                        // Every texture is skipped until this is fixed by hand, so say so once where it is seen.
                                        cacheWarned = true;
                                        BuildWarnings.Report(null, "The texture cache could not be used", e.Message + " Every texture keeps its original.",
                                            "Delete " + folder + "/" + CacheFileName + " (for example after a version-control merge conflict); it is rebuilt on the next build.");
                                        throw;
                                    }
                                }
                                var mapping = cache.mappings.FirstOrDefault(m => m.sourceId == id && m.recipeHash == recipe);
                                if (mapping != null && FingerprintService.IsReady(mapping, recipe) &&
                                    AssetDatabase.GetAssetPath(mapping.replacement).StartsWith(folder + "/", StringComparison.Ordinal) &&
                                    // Padding repair made without a size estimate (Play Mode) may have grown the texture; where an estimate can
                                    // be made now (a build), it is made again and the size gate applies.
                                    !(mapping.repairedPadding && mapping.compressedSourceBytes == 0 && BundleSizeEstimator.CanEstimate))
                                {
                                    result.CacheLookup = true;
                                    result.CacheValidation = true;
                                    progress?.Stage("Validating cached replacement");
                                    GeneratedTargetValidator.ValidatePair((Texture2D)group.Source, mapping.replacement, TextureFileSizePolicy.SkipsPngGate(mapping), mapping.standaloneFormat);
                                    if (!GeneratedTargetValidator.IsValidated(mapping))
                                    {
                                        GeneratedTargetValidator.ValidateExisting(mapping);
                                        EditorUtility.SetDirty(cache);
                                    }
                                    progress?.CheckCancelled();
                                    MarkUsed(cache, ref mapping.lastUsedDay);
                                    selected.mappings.Add(mapping);
                                    summary.Reused++;
                                    result.Outcome = TextureResultKind.Reused;
                                    result.Reason = "A matching validated replacement was reused from the cache.";
                                    continue;
                                }
                                var unchanged = cache.unchangedTextures.FirstOrDefault(m => m.sourceId == id && m.recipeHash == recipe);
                                if (unchanged != null)
                                {
                                    result.CacheLookup = true;
                                    MarkUsed(cache, ref unchanged.lastUsedDay);
                                    if (!string.IsNullOrEmpty(unchanged.retainedReason))
                                    {
                                        summary.Skipped++;
                                        result.Outcome = TextureResultKind.Skipped;
                                        result.Reason = unchanged.retainedReason;
                                        warn?.Invoke(group.Source, unchanged.retainedReason);
                                        continue;
                                    }
                                    summary.Unchanged++;
                                    result.Outcome = TextureResultKind.Unchanged;
                                    result.Reason = "A matching cached result found no pixel changes; the original was retained.";
                                    continue;
                                }
                                var nonBeneficial = TextureFileSizePolicy.Find(cache, id, recipe);
                                // A "no saving" found by PNG size alone (Play Mode) is checked again where the compressed size can be estimated.
                                if (nonBeneficial != null && !(!nonBeneficial.compressedEstimate && BundleSizeEstimator.CanEstimate))
                                {
                                    result.CacheLookup = true;
                                    MarkUsed(cache, ref nonBeneficial.lastUsedDay);
                                    summary.NotSmaller++;
                                    result.Outcome = TextureResultKind.NotSmaller;
                                    result.Reason = "Cached size check: " + NoFileSizeReductionException.Explain(nonBeneficial.sourceBytes, nonBeneficial.candidateBytes, nonBeneficial.compressedEstimate);
                                    warn?.Invoke(group.Source, result.Reason);
                                    continue;
                                }
                                try
                                {
                                    progress?.Stage("Generating optimized texture");
                                    mapping = GenerationCoordinator.GenerateOne(group, folder, recipe,
                                        coverageCache: coverageCache, allowNewRevision: true, progress: progress);
                                }
                                catch (NoFileSizeReductionException e)
                                {
                                    TextureFileSizePolicy.Remember(cache, (Texture2D)group.Source, id, recipe, e);
                                    summary.NotSmaller++;
                                    result.Outcome = TextureResultKind.NotSmaller;
                                    result.Reason = e.Message;
                                    warn?.Invoke(group.Source, e.Message);
                                    continue;
                                }
                                catch (NoTextureChangesException e)
                                {
                                    cache.unchangedTextures.Add(new UnchangedTextureAnalysis { source = (Texture2D)group.Source, sourceId = id, recipeHash = recipe, lastUsedDay = CacheCleanup.Today });
                                    EditorUtility.SetDirty(cache);
                                    summary.Unchanged++;
                                    result.Outcome = TextureResultKind.Unchanged;
                                    result.Reason = e.Message;
                                    continue;
                                }
                                // Keep other avatars' recipes. Never delete or overwrite old PNG revisions.
                                cache.mappings.RemoveAll(m => m.sourceId == id && m.recipeHash == recipe);
                                mapping.lastUsedDay = CacheCleanup.Today;
                                cache.mappings.Add(mapping);
                                EditorUtility.SetDirty(cache);
                                selected.mappings.Add(mapping);
                                summary.Generated++;
                                result.Outcome = TextureResultKind.Generated;
                                result.Reason = group.RepairsPadding
                                    ? "A replacement PNG was generated and selected; mipmap padding repair may increase its file size."
                                    : "A smaller replacement PNG was generated and selected.";
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (RetainedException e)
                            {
                                // Fixed by the recipe: remembered, so later builds skip the analysis.
                                if (recipe != null && cache)
                                {
                                    cache.unchangedTextures.Add(new UnchangedTextureAnalysis { source = (Texture2D)group.Source, sourceId = id, recipeHash = recipe, lastUsedDay = CacheCleanup.Today, retainedReason = e.Message });
                                    EditorUtility.SetDirty(cache);
                                }
                                summary.Skipped++;
                                result.Outcome = TextureResultKind.Skipped;
                                result.Reason = e.Message;
                                warn?.Invoke(group.Source, e.Message);
                            }
                            catch (Exception e)
                            {
                                summary.Skipped++;
                                result.Outcome = TextureResultKind.Skipped;
                                result.Reason = e.Message;
                                warn?.Invoke(group.Source, e.Message);
                            }
                        }
                        catch (OperationCanceledException e)
                        {
                            result.Outcome = TextureResultKind.NotProcessed;
                            result.Reason = e.Message;
                            throw;
                        }
                        finally
                        {
                            timer.Stop();
                            result.PreparationMilliseconds = measured ? timer.ElapsedMilliseconds : -1;
                            summary.TextureResults.Add(result);
                        }
                    }
                }
                finally
                {
                    if (cache) AssetDatabase.SaveAssetIfDirty(cache);
                    keepAlive.Dispose();
                }
                return selected;
            }
            catch { UnityEngine.Object.DestroyImmediate(selected); throw; }
        }
        // Day granularity keeps the cache asset from being rewritten on every build.
        private static void MarkUsed(TextureOptimizationManifest cache, ref int lastUsedDay)
        {
            int today = CacheCleanup.Today;
            if (lastUsedDay == today) return;
            lastUsedDay = today;
            EditorUtility.SetDirty(cache);
        }

        internal static TextureOptimizationManifest LoadOrCreateCache(string folder)
        {
            EnsureFolder(folder);
            string path = folder + "/" + CacheFileName;
            var cache = AssetDatabase.LoadAssetAtPath<TextureOptimizationManifest>(path);
            if (cache)
            {
                if (cache.cacheOwner != CacheOwner || cache.schemaVersion != 1)
                    throw new InvalidOperationException("Automatic cache path is occupied by an unrecognized manifest; original textures retained.");
                cache.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                return cache;
            }
            if (File.Exists(path) || File.Exists(path + ".meta"))
                throw new InvalidOperationException("Automatic cache path is occupied by another asset; it will not be overwritten.");
            cache = ScriptableObject.CreateInstance<TextureOptimizationManifest>();
            cache.cacheOwner = CacheOwner;
            AssetDatabase.CreateAsset(cache, path);
            cache.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return cache;
        }

        internal static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            if (parts.Length < 2 || parts[0] != "Assets" || parts.Any(p => p == ".." || p == "." || string.IsNullOrEmpty(p)))
                throw new InvalidOperationException("Generated cache must remain under Assets.");
            string current = "Assets";
            foreach (var part in parts.Skip(1))
            {
                TextureSafety.ValidateFolder(current); // Reject filesystem links before creating children.
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (File.Exists(next) || Directory.Exists(next) || File.Exists(next + ".meta"))
                        throw new InvalidOperationException("Cache folder path is occupied or not imported: " + next);
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, part)))
                        throw new InvalidOperationException("Cannot create cache folder: " + next);
                }
                current = next;
            }
            TextureSafety.ValidateFolder(current);
        }
    }
}
