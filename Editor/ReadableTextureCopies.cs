using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Read/Write-off build copies of readable source textures the texture pass keeps as they are (no changes needed, no
    // saving, or skipped by a safety check). A readable texture ships a CPU copy that every viewer holds in RAM and that
    // nothing on an avatar reads (see TextureSafety.DropReadable); generated replacements already lose it. Here the kept
    // source's file is copied byte for byte into the cache with the source's importer metadata and only Read/Write turned
    // off, so the imported texture is the same texture without the CPU copy. The authoring asset is never edited.
    // Eligibility (all checked; anything else keeps the source):
    //  - a main-asset Texture2D with a TextureImporter, Read/Write on, in a format the optimizer reads (PNG, PSD, TGA...);
    //  - every holder on the avatar is a material slot the substitution rewrites: no excluded renderer (Arclight Exclude),
    //    no particle/trail/line renderer, no other component or asset it references (menus, controllers, particle shapes),
    //    and no animated material or texture swap. Otherwise the readable original would ship next to the copy;
    //  - the animation could be inspected, and the avatar carries no Udon program or d4rk Fighting Actions (the same
    //    fail-closed stand-down as every "nothing reads this" pass);
    //  - the copy's file hash equals the source's, its importer settings equal the source's apart from Read/Write, and
    //    its imported format, size, mip count and raw texture data equal the source's. A copy that fails is deleted.
    // Serialized reads fail closed: a component or asset that cannot be read keeps every source (the pass makes no copy
    // for that avatar).
    internal static class ReadableTextureCopies
    {
        internal const string UserData = "AvatarTextureOptimizer:readable-drop";
        private const long MaximumBytes = 128L * 1024 * 1024;

        // Source -> copy for each eligible kept texture. A texture whose copy cannot be made or proven keeps its source.
        internal static Dictionary<Texture, Texture> Prepare(GameObject root, ScanResult scan, TextureOptimizationManifest selected, string folder)
        {
            var copies = new Dictionary<Texture, Texture>();
            if (!root || scan == null) return copies;
            if (!GeneratedTargetValidator.IsStandalone && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) return copies;
            // Fail closed: animated holders cannot be listed when the animation could not be inspected.
            if (scan.Animation == null || scan.Animation.Uninspectable) return copies;
            var candidates = scan.Groups.Where(group => Eligible(group, selected, folder)).ToList();
            if (candidates.Count == 0) return copies;
            // Fail closed with the parameter passes: an Udon program or d4rk Fighting Actions is a reader this scan cannot see.
            if (ParameterCleaner.HasInvisibleReaders(root)) return copies;
            HashSet<Texture> held;
            try { held = HeldElsewhere(root, scan.Animation); }
            catch (Exception e) when (!(e is OperationCanceledException)) { return copies; } // Unreadable holder: keep every source.
            TextureOptimizationManifest cache;
            try { cache = AutomaticTextureOptimizer.LoadOrCreateCache(folder); }
            catch (Exception e) when (!(e is OperationCanceledException)) { return copies; }
            try
            {
                foreach (var group in candidates)
                {
                    var source = (Texture2D)group.Source;
                    if (held.Contains(source)) continue;
                    try
                    {
                        var copy = CopyFor(source, folder, cache);
                        if (copy) copies.Add(source, copy);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is UnityException)
                    {
                        Debug.Log("Arclight Optimizer: Read/Write kept on " + source.name + ": " + e.Message);
                    }
                }
            }
            finally { AssetDatabase.SaveAssetIfDirty(cache); }
            return copies;
        }

        private static bool Eligible(TextureGroup group, TextureOptimizationManifest selected, string folder)
        {
            if (!(group.Source is Texture2D source) || !source.isReadable || !AssetDatabase.IsMainAsset(source)) return false;
            string path = AssetDatabase.GetAssetPath(source);
            if (!SourceImages.IsSupported(path) || path.StartsWith(folder + "/", StringComparison.Ordinal)) return false;
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer) || !importer.isReadable ||
                importer.textureShape != TextureImporterShape.Texture2D) return false;
            if (selected && selected.mappings.Any(m => m.source == source)) return false; // A generated replacement already drops it.
            // An excluded renderer keeps its materials, and an animated swap may keep the original object: either would
            // ship the readable source next to its copy.
            return group.Uses.Count > 0 && group.Uses.All(use => use.Renderer && !Exclusions.Excluded(use.Renderer) && !use.FromAnimation);
        }

        // Every texture something other than a rewritten material slot can hold: components other than non-excluded mesh
        // and skinned mesh renderers (with every asset they reference, followed through its dependencies), and every
        // material or texture an animation can assign.
        private static HashSet<Texture> HeldElsewhere(GameObject root, AnimationSnapshot animation)
        {
            var held = new HashSet<Texture>();
            var visited = new HashSet<Object>();
            void Hold(Object value)
            {
                if (!value || !visited.Add(value)) return;
                if (value is Mesh || value is MonoScript || value is Shader || value is AudioClip || value is Avatar) return;
                // Scene objects are visited as components of the avatar; only assets are followed.
                if ((value is GameObject || value is Component) && !EditorUtility.IsPersistent(value)) return;
                foreach (var dependency in EditorUtility.CollectDependencies(new[] { value }))
                    if (dependency is Texture texture && texture) held.Add(texture);
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component is Transform) continue;
                if ((component is MeshRenderer || component is SkinnedMeshRenderer) && !Exclusions.Excluded(component)) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference) Hold(iterator.objectReferenceValue);
                }
            }
            foreach (var state in animation.Renderers.Values)
            {
                foreach (var material in state.SlotMaterials.Values.SelectMany(m => m)) Hold(material);
                foreach (var texture in state.TextureValues.Values.SelectMany(t => t)) if (texture) held.Add(texture);
            }
            foreach (var clip in animation.ObjectBindings.GroupBy(b => b.Clip))
                foreach (var binding in clip.Select(b => b.Binding).Distinct())
                    foreach (var key in clip.Key.GetObjectCurve(binding) ?? Array.Empty<ObjectReferenceKeyframe>())
                        Hold(key.value);
            return held;
        }

        private static string Recipe(Texture2D source, string sourceHash) =>
            FingerprintService.Hash(System.Text.Encoding.UTF8.GetBytes(FingerprintService.Version + "|readable-drop-v1|" +
                FingerprintService.Id(source) + "|" + sourceHash + "|" + FingerprintService.ImporterHash(source) + "|" +
                EditorUserBuildSettings.activeBuildTarget));

        private static Texture2D CopyFor(Texture2D source, string folder, TextureOptimizationManifest cache)
        {
            string path = AssetDatabase.GetAssetPath(source);
            if (LongPath.Length(path) > MaximumBytes) return null;
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var bytes = File.ReadAllBytes(LongPath.For(path));
            string sourceHash = FingerprintService.Hash(bytes), id = FingerprintService.Id(source), recipe = Recipe(source, sourceHash);
            if (cache.readableDrops == null) cache.readableDrops = new List<ReadableDropCopy>();
            var entry = cache.readableDrops.FirstOrDefault(e => e.sourceId == id && e.recipeHash == recipe);
            if (entry != null)
            {
                string cachedPath = entry.copy ? AssetDatabase.GetAssetPath(entry.copy) : "";
                if (cachedPath.StartsWith(folder + "/", StringComparison.Ordinal) && File.Exists(cachedPath) &&
                    FingerprintService.ImporterHash(entry.copy) == entry.outputImporterHash &&
                    Same(source, importer, entry.copy, AssetImporter.GetAtPath(cachedPath) as TextureImporter, sourceHash, cachedPath, rawData: false))
                {
                    if (entry.lastUsedDay != CacheCleanup.Today) { entry.lastUsedDay = CacheCleanup.Today; EditorUtility.SetDirty(cache); }
                    return entry.copy;
                }
                // Never memoize a failure: the entry goes, its file is left to the cache cleanup, and a new copy is made.
                cache.readableDrops.RemoveAll(e => e.sourceId == id && e.recipeHash == recipe);
                EditorUtility.SetDirty(cache);
            }

            string meta = WithoutReadable(GenerationCoordinator.CopiedImporterMeta(path + ".meta", UserData));
            if (meta == null) return null; // Unexpected .meta layout: keep the source (fail closed).
            folder = TextureSafety.ValidateFolder(folder);
            string outPath = GenerationCoordinator.OutputPath(folder, source.name, recipe);
            outPath = outPath.Substring(0, outPath.Length - ".png".Length) + Path.GetExtension(path).ToLowerInvariant();
            if (File.Exists(outPath) || File.Exists(outPath + ".meta"))
            {
                outPath = AssetDatabase.GenerateUniqueAssetPath(outPath);
                if (File.Exists(outPath) || File.Exists(outPath + ".meta")) return null;
            }
            bool ownsFile = false, ownsMeta = false, imported = false;
            try
            {
                // CreateNew: nothing is ever overwritten.
                using (var stream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write)) { ownsFile = true; stream.Write(bytes, 0, bytes.Length); }
                using (var stream = new FileStream(outPath + ".meta", FileMode.CreateNew, FileAccess.Write))
                {
                    ownsMeta = true;
                    var metaBytes = new System.Text.UTF8Encoding(false).GetBytes(meta);
                    stream.Write(metaBytes, 0, metaBytes.Length);
                }
                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);
                imported = true;
                var copy = AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
                if (!Same(source, importer, copy, AssetImporter.GetAtPath(outPath) as TextureImporter, sourceHash, outPath, rawData: true)) return null;
                // House contract: remove the same (sourceId, recipeHash) key before adding.
                cache.readableDrops.RemoveAll(e => e.sourceId == id && e.recipeHash == recipe);
                cache.readableDrops.Add(new ReadableDropCopy
                {
                    source = source, copy = copy, sourceId = id, recipeHash = recipe, lastUsedDay = CacheCleanup.Today,
                    outputImporterHash = FingerprintService.ImporterHash(copy)
                });
                EditorUtility.SetDirty(cache);
                ownsFile = ownsMeta = false;
                return copy;
            }
            finally
            {
                // A copy that was not accepted leaves nothing behind.
                if (ownsFile || ownsMeta)
                {
                    if (imported) AssetDatabase.DeleteAsset(outPath);
                    if (ownsFile && File.Exists(outPath)) File.Delete(outPath);
                    if (ownsMeta && File.Exists(outPath + ".meta")) File.Delete(outPath + ".meta");
                }
            }
        }

        // The copied importer metadata with Read/Write off. Exactly one "isReadable: 1" line is expected (the importer of a
        // readable texture); any other layout returns null, which keeps the source.
        internal static string WithoutReadable(string meta)
        {
            if (meta == null) return null;
            string newline = meta.Contains("\r\n") ? "\r\n" : "\n";
            var lines = meta.Split(new[] { newline }, StringSplitOptions.None);
            int found = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("  isReadable:", StringComparison.Ordinal)) continue;
                if (lines[i] != "  isReadable: 1") return null;
                lines[i] = "  isReadable: 0";
                found++;
            }
            return found == 1 ? string.Join(newline, lines) : null;
        }

        // Whether the copy is the source texture without its CPU copy: identical file bytes, importer settings that differ
        // only in Read/Write, and the same imported texture. rawData also compares the imported texture data (when the copy
        // is made); an empty array is an imported-state edge, never a match.
        private static bool Same(Texture2D source, TextureImporter sourceImporter, Texture2D copy, TextureImporter copyImporter, string sourceHash, string copyPath, bool rawData)
        {
            if (!copy || !copyImporter || copyImporter.userData != UserData || copyImporter.isReadable || copy.isReadable) return false;
            if (FingerprintService.FileHash(copyPath) != sourceHash) return false;
            if (TextureSafety.SettingsFingerprint(sourceImporter, ignoreReadable: true) != TextureSafety.SettingsFingerprint(copyImporter, ignoreReadable: true)) return false;
            if (copy.width != source.width || copy.height != source.height || copy.mipmapCount != source.mipmapCount || copy.format != source.format ||
                copy.graphicsFormat != source.graphicsFormat || copy.wrapModeU != source.wrapModeU || copy.wrapModeV != source.wrapModeV ||
                copy.filterMode != source.filterMode || copy.anisoLevel != source.anisoLevel || copy.mipMapBias != source.mipMapBias) return false;
            if (!rawData) return true;
            var sourceData = source.GetRawTextureData();
            var copyData = copy.GetRawTextureData();
            return sourceData.Length > 0 && copyData.Length > 0 && copyData.SequenceEqual(sourceData);
        }
    }
}
