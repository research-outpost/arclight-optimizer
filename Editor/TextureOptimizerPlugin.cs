using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.fluent;
using UnityEditor;
using UnityEngine;

[assembly: ExportsPlugin(typeof(Okarin.AvatarTextureOptimizer.Editor.TextureOptimizerPlugin))]

namespace Okarin.AvatarTextureOptimizer.Editor
{
    public sealed class TextureOptimizerPlugin : Plugin<TextureOptimizerPlugin>
    {
        public override string QualifiedName => "dev.okarin.avatar-texture-optimizer";
        public override string DisplayName => "Arclight Optimizer";

        protected override void Configure()
        {
            // Run before Avatar Optimizer (it also schedules itself late). AAO then downscales/atlases the
            // cleaned persistent PNGs; after it, its in-memory textures and Direct-blend-tree toggles would
            // be retained here. Its later mesh removal only makes this coverage conservative.
            InPhase(BuildPhase.Optimizing).BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Generate and apply optimized textures", ctx =>
            {
                var config = ctx.AvatarRootObject.GetComponent<AvatarTextureOptimizer>();
                if (!config) return;
                // The component is removed during this pass; the post-d4rk hook reads this request instead.
                if (config.enabled && config.mergeDuplicates) MergeRequests.Add(ctx.AvatarRootObject);
                if (config.enabled && config.optimizeAnimations) KeyReductionRequests.Add(ctx.AvatarRootObject);
                var state = ctx.GetState<SubstitutionState>();
                // Read by the later mesh/audio pass, after this pass has removed the component.
                state.MergeMeshesAndAudio = config.enabled && config.mergeDuplicates;
                state.OptimizeMeshes = config.enabled && config.optimizeMeshes;
                state.OptimizeAudio = config.enabled && config.optimizeAudio;
                Action<UnityEngine.Object, UnityEngine.Object> register =
                    (a, b) => ObjectRegistry.RegisterReplacedObject(a, b);
                // Clip merging alone still activates the virtual controller context: deactivating it commits every
                // controller as a build-owned asset, which is what lets the later hook edit them safely.
                if (!config.enabled || !(config.optimizeTextures || config.mergeDuplicates || config.optimizeAnimations) ||
                    (EditorApplication.isPlayingOrWillChangePlaymode && AutomaticTextureOptimizer.SceneReloadDisabled))
                {
                    AutomaticTextureOptimizer.Run(ctx.AvatarRootObject, state, register);
                    return;
                }
                using (var progress = new OptimizationProgress())
                {
                    // This pass declares no compatible animator extension, so NDMF first commits
                    // preceding virtual edits. Inspect serialized marker/legacy clips before
                    // activating our own virtual context, with no intervening plugin pass.
                    if (config.optimizeTextures)
                    {
                        progress.Overall("Analyzing animation", 0, false);
                        state.SerializedAnimation = AnimationSnapshot.Analyze(ctx.AvatarRootObject, null, null);
                    }
                    try
                    {
                        progress.Overall("Preparing animation controllers", 0, false);
                        var animation = ctx.ActivateExtensionContext<VirtualControllerContext>();
                        AutomaticTextureOptimizer.Run(ctx.AvatarRootObject, state, register,
                            animationContext: animation, progress: progress);
                    }
                    finally
                    {
                        ctx.DeactivateExtensionContext<VirtualControllerContext>();
                    }
                    // Unity can restore animated renderer defaults when the committed controller
                    // is assigned. Reapply our material mapping after that rebind; curves already
                    // reference the same clones. Never edit a material through renderer.material.
                    TemporaryMaterialSubstituter.ReapplyAfterControllerCommit(ctx.AvatarRootObject, state);
                }
            });

            // After Avatar Optimizer, which merges and rebuilds meshes. New meshes stay in memory; NDMF saves
            // everything the avatar references when the build ends.
            InPhase(BuildPhase.Optimizing).AfterPlugin("com.anatawa12.avatar-optimizer")
                .Run("Merge duplicate meshes and audio, compact mesh indices", ctx =>
            {
                var state = ctx.GetState<SubstitutionState>();
                if (!state.MergeMeshesAndAudio && !state.OptimizeMeshes && !state.OptimizeAudio) return;
                try
                {
                    bool monoAudio = state.OptimizeAudio && !AudioSettingsAnimated(ctx);
                    var result = MeshAndAudioOptimizer.Run(ctx.AvatarRootObject, state.MergeMeshesAndAudio, state.OptimizeMeshes,
                        (a, b) => ObjectRegistry.RegisterReplacedObject(a, b), monoAudio);
                    if (result.Changed)
                        Debug.Log("Arclight Optimizer: " + result + " on " + ctx.AvatarRootObject.name + ".");
                    if (monoAudio) CacheCleanup.Schedule(AutomaticTextureOptimizer.CacheFolder);
                }
                catch (Exception e)
                {
                    // Optimization only: never fail the build.
                    Debug.LogWarning("Arclight Optimizer: mesh and audio optimization skipped: " + e.Message);
                }
            });
        }

        // Mono +3 dB matches stereo only for the source settings it was measured with, so any animation of an
        // AudioSource or VRC Spatial Audio Source setting other than volume, pitch, mute, enabling or the clip
        // itself turns the conversion off. NDMF marker clips stand for the platform's proxy motions, which hold
        // no audio bindings. Anything that cannot be read also turns it off.
        private static readonly HashSet<string> NeutralAudioProperties = new HashSet<string>(StringComparer.Ordinal)
            { "m_Volume", "m_Pitch", "m_Mute", "m_Enabled", "m_audioClip", "Gain" };

        private static bool AudioSettingsAnimated(BuildContext ctx)
        {
            bool Changes(EditorCurveBinding binding) => binding.type != null &&
                (typeof(AudioSource).IsAssignableFrom(binding.type) || AudioMonoConverter.IsVrcSpatialSource(binding.type)) &&
                !NeutralAudioProperties.Contains(binding.propertyName ?? "");
            try
            {
                foreach (var animation in ctx.AvatarRootObject.GetComponentsInChildren<Animation>(true))
                    foreach (var clip in AnimationUtility.GetAnimationClips(animation.gameObject))
                        if (clip && AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)).Any(Changes))
                            return true;
                var context = ctx.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    foreach (var entry in context.Controllers)
                    {
                        if (entry.Value == null) continue;
                        foreach (var node in entry.Value.AllReachableNodes())
                            if (node is VirtualClip clip && !clip.IsMarkerClip &&
                                clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings()).Any(Changes))
                                return true;
                    }
                }
                finally { ctx.DeactivateExtensionContext<VirtualControllerContext>(); }
                return false;
            }
            catch (Exception) { return true; }
        }
    }

    internal sealed class SubstitutionState
    {
        public readonly Dictionary<Material, Material> Clones = new Dictionary<Material, Material>();
        public bool Applied;
        public bool Cancelled;
        public bool MergeMeshesAndAudio, OptimizeMeshes, OptimizeAudio;
        public int AppliedTextures;
        public readonly HashSet<Texture> MergedDuplicates = new HashSet<Texture>();
        internal AnimationSnapshot SerializedAnimation;
        public readonly GenerationSummary Summary = new GenerationSummary();
    }

    internal static class TemporaryMaterialSubstituter
    {
        internal static void ReapplyAfterControllerCommit(GameObject root, SubstitutionState state)
        {
            if (state.Clones.Count == 0) return;
            // Assigning a controller makes an Animator write back the defaults it captured when last bound
            // (here, the original material slots) and capture new ones. Unbind, apply the clones, then
            // rebind so the clones become the captured defaults; any later controller reassignment (for
            // example Avatar Optimizer's) then keeps them instead of restoring the originals.
            var animators = root.GetComponentsInChildren<Animator>(true)
                .Where(animator => animator.runtimeAnimatorController)
                .ToDictionary(animator => animator, animator => animator.runtimeAnimatorController);
            foreach (var animator in animators.Keys) animator.runtimeAnimatorController = null;
            try
            {
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                    var materials = renderer.sharedMaterials;
                    bool changed = false;
                    for (int slot = 0; slot < materials.Length; slot++)
                        if (materials[slot] && state.Clones.TryGetValue(materials[slot], out var replacement))
                        {
                            materials[slot] = replacement;
                            changed = true;
                        }
                    if (changed) renderer.sharedMaterials = materials;
                }
            }
            finally
            {
                foreach (var pair in animators) pair.Key.runtimeAnimatorController = pair.Value;
            }
        }

        private sealed class RendererAssignment
        {
            internal Renderer Renderer;
            internal Material[] Original;
            internal Material[] Replacement;
        }

        private sealed class CurveEdit
        {
            internal VirtualClip Clip;
            internal EditorCurveBinding Binding;
            internal ObjectReferenceKeyframe[] Original;
            internal ObjectReferenceKeyframe[] Replacement;
        }

        // Caller must supply the NDMF build avatar, never a source scene avatar. Tests pass no scan so a
        // selection made earlier is re-checked against the avatar as it is now.
        public static void Apply(GameObject buildRoot, SubstitutionState state,
            Action<UnityEngine.Object, UnityEngine.Object> register,
            TextureOptimizationManifest selected,
            Action<UnityEngine.Object, string> report = null,
            VirtualControllerContext animationContext = null,
            ScanResult preparedScan = null)
        {
            if (state.Applied) return;
            state.Applied = true;
            var config = buildRoot.GetComponent<AvatarTextureOptimizer>();
            if (!config) return;
            report = report ?? ((target, message) => { });
            try
            {
                if (!config.enabled || !selected) return;
                var scan = preparedScan ?? TextureUsageScanner.Scan(buildRoot, animationContext);
                var replacements = new Dictionary<Texture, Texture>();
                foreach (var group in scan.Groups)
                {
                    var mapping = selected.mappings.FirstOrDefault(m => m.source == group.Source);
                    if (mapping == null) continue;
                    try
                    {
                        if (group.Warning != null) throw new InvalidOperationException(group.Warning);
                        if (IsUnsafeStandaloneSwap(group, scan.Animation))
                            throw new InvalidOperationException("Animated material or texture swaps require the NDMF virtual controller context; original texture retained.");
                        if (!FingerprintService.IsReady(mapping, FingerprintService.Recipe(group)))
                            throw new InvalidOperationException("Generated mapping changed during processing; original texture retained. Retry the build.");
                        if (!GeneratedTargetValidator.IsValidated(mapping))
                            throw new InvalidOperationException("Replacement has not passed import checks for the active build target; original texture retained.");
                        TextureSafety.Validate(mapping.replacement);
                        replacements.Add(group.Source, mapping.replacement);
                    }
                    catch (Exception e) { report(group.Source, e.Message); }
                }
                // Copies use the texture they duplicate, or its optimized replacement.
                int optimized = replacements.Count;
                foreach (var pair in scan.Duplicates)
                {
                    if (IsUnsafeStandaloneSwap(scan.Groups.First(g => g.Source == pair.Value), scan.Animation))
                    {
                        report(pair.Key, "Duplicate kept: animated swaps require the NDMF virtual controller context.");
                        continue;
                    }
                    replacements[pair.Key] = replacements.TryGetValue(pair.Value, out var target) ? target : pair.Value;
                }

                // Prepare every material, renderer array, and virtual object curve before
                // committing any renderer or controller changes.
                var assignments = new List<RendererAssignment>();
                var prepared = new List<Material>();
                var curveEdits = new List<CurveEdit>();
                try
                {
                    foreach (var renderer in buildRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                        string path = AnimationUtility.CalculateTransformPath(renderer.transform, buildRoot.transform);
                        var originalSlots = renderer.sharedMaterials;
                        var replacementSlots = (Material[])originalSlots.Clone();
                        bool changed = false;
                        for (int i = 0; i < replacementSlots.Length; i++)
                        {
                            var original = replacementSlots[i];
                            if (!original) continue;
                            var clone = PrepareClone(original, replacements, state, prepared);
                            if (!clone) continue;
                            replacementSlots[i] = clone;
                            changed = true;
                        }
                        if (changed)
                            assignments.Add(new RendererAssignment { Renderer = renderer, Original = originalSlots, Replacement = replacementSlots });

                        var animation = scan.Animation;
                        if (animation != null && animation.Renderers.TryGetValue(path, out var stateForRenderer))
                        {
                            foreach (var candidates in stateForRenderer.SlotMaterials.Values)
                                foreach (var candidate in candidates.Where(m => m))
                                    PrepareClone(candidate, replacements, state, prepared);
                        }
                    }

                    PrepareCurveEdits(scan.Animation, replacements, state, curveEdits);
                    foreach (var pair in state.Clones) register?.Invoke(pair.Key, pair.Value);
                    // NDMF's registry takes one registration per new object, so a copy is recorded as replaced by
                    // the texture it duplicates, which is in turn replaced by its optimized PNG (copies first).
                    foreach (var pair in replacements.Where(p => scan.Duplicates.ContainsKey(p.Key)))
                        register?.Invoke(pair.Key, scan.Duplicates[pair.Key]);
                    foreach (var pair in replacements.Where(p => !scan.Duplicates.ContainsKey(p.Key)))
                        register?.Invoke(pair.Key, pair.Value);

                    foreach (var edit in curveEdits) edit.Clip.SetObjectCurve(edit.Binding, edit.Replacement);
                    foreach (var assignment in assignments) assignment.Renderer.sharedMaterials = assignment.Replacement;
                    state.AppliedTextures = optimized;
                    state.MergedDuplicates.UnionWith(replacements.Keys.Where(scan.Duplicates.ContainsKey));
                }
                catch
                {
                    // Restore any virtual curves and renderer arrays that were committed before
                    // the failure, then release only temporary clones made by this transaction.
                    foreach (var edit in curveEdits)
                    {
                        try { edit.Clip.SetObjectCurve(edit.Binding, edit.Original); }
                        catch { /* retain the original exception */ }
                    }
                    foreach (var assignment in assignments)
                    {
                        try { assignment.Renderer.sharedMaterials = assignment.Original; }
                        catch { /* retain the original exception */ }
                    }
                    foreach (var material in prepared) UnityEngine.Object.DestroyImmediate(material);
                    state.Clones.Clear();
                    throw;
                }
            }
            catch (Exception e) { report(config, "Substitution aborted: " + e.Message); }
            finally { UnityEngine.Object.DestroyImmediate(config); }
        }

        private static bool IsUnsafeStandaloneSwap(TextureGroup group, AnimationSnapshot animation)
        {
            if (animation == null || animation.HasVirtualContext || !animation.HasObjectCurves) return false;
            return group.Uses.Any(use => animation.Renderers.TryGetValue(use.RendererPath, out var state) &&
                                         state.RequiresObjectRewrite);
        }

        private static Material PrepareClone(Material original, Dictionary<Texture, Texture> replacements,
            SubstitutionState state, List<Material> prepared)
        {
            if (!original) return null;
            if (state.Clones.TryGetValue(original, out var existing)) return existing;
            var properties = original.GetTexturePropertyNames().Where(p =>
                original.GetTexture(p) && replacements.ContainsKey(original.GetTexture(p))).ToArray();
            if (properties.Length == 0) return null;
            var clone = new Material(original) { name = original.name + " (Arclight Optimizer)" };
            foreach (string property in properties)
                clone.SetTexture(property, replacements[original.GetTexture(property)]);
            prepared.Add(clone);
            state.Clones.Add(original, clone);
            return clone;
        }

        private static void PrepareCurveEdits(AnimationSnapshot animation, Dictionary<Texture, Texture> replacements,
            SubstitutionState state, List<CurveEdit> edits)
        {
            if (animation == null || !animation.CanRewriteObjectCurves) return;
            foreach (var group in animation.ObjectBindings.GroupBy(b => b.Clip))
            {
                var clip = group.Key;
                foreach (var binding in group.Select(b => b.Binding).Distinct())
                {
                    var original = clip.GetObjectCurve(binding);
                    if (original == null) continue;
                    var replacement = (ObjectReferenceKeyframe[])original.Clone();
                    bool changed = false;
                    for (int i = 0; i < replacement.Length; i++)
                    {
                        var value = replacement[i].value;
                        if (value is Material material && state.Clones.TryGetValue(material, out var clone))
                        {
                            replacement[i].value = clone;
                            changed = true;
                        }
                        else if (value is Texture texture && replacements.TryGetValue(texture, out var optimized))
                        {
                            replacement[i].value = optimized;
                            changed = true;
                        }
                    }
                    if (!changed) continue;
                    edits.Add(new CurveEdit { Clip = clip, Binding = binding, Original = original, Replacement = replacement });
                }
            }
        }
    }
}
