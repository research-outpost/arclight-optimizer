using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
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
            // Avatar Optimizer acts from Resolving on, and some of its components remove themselves as they apply, so the
            // conflict is checked before its first pass. Excluded objects are recorded here too, before Modular Avatar and
            // VRCFury move bones out from under their Exclude component.
            InPhase(BuildPhase.Resolving).BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run("Record exclusions and check for Avatar Optimizer", ctx =>
                {
                    Exclusions.Record(ctx.AvatarRootObject);
                    var config = ctx.AvatarRootObject.GetComponent<AvatarTextureOptimizer>();
                    if (config && config.enabled && AvatarOptimizerConflict.Report(ctx.AvatarRootObject))
                        ctx.GetState<SubstitutionState>().AvatarOptimizerConflict = true;
                });
            // One sequence, in the order each pass needs: settings and the report first; then the avatar-wide passes, so
            // textures are only processed for objects and material slots that survive, and coverage is measured on the
            // final meshes; then texture generation; then cropping of the generated textures. Arclight does not run
            // alongside Avatar Optimizer (see AvatarOptimizerConflict). After Modular Avatar, so the analysis sees the
            // hierarchy it leaves.
            InPhase(BuildPhase.Optimizing).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Read settings and start the report", Prepare)
                .Then.Run("Avatar-wide optimizations", AvatarWide)
                .Then.Run("Generate and apply optimized textures", Textures)
                .Then.Run("Crop textures to the part their meshes sample", Crop);
        }

        private static void Prepare(BuildContext ctx)
        {
            BuildTimings.Current = null; // Never carry another avatar's timings.
            var config = ctx.AvatarRootObject.GetComponent<AvatarTextureOptimizer>();
            if (!config) return;
            var state = ctx.GetState<SubstitutionState>();
            if (state.AvatarOptimizerConflict)
            {
                UnityEngine.Object.DestroyImmediate(config);
                return;
            }
            if (config.enabled) state.Report = new OptimizationLog(EditorApplication.isPlayingOrWillChangePlaymode);
            BuildTimings.Current = config.enabled ? new BuildTimings() : null;
            // Without scene reload, Play Mode cannot restore the original avatar: the texture pass declines (and says
            // why), and nothing else may act on it either.
            if (EditorApplication.isPlayingOrWillChangePlaymode && AutomaticTextureOptimizer.SceneReloadDisabled) return;
            // VRCFury keeps its components until the very end of the build and marks a built avatar with VRCFuryTest.
            // Before it builds (a manual bake, or VRCFury switched off for this kind of build), the animation, parameters
            // and readers it will add are not here yet, so nothing that depends on the avatar's animation may run.
            state.VrcfuryPending = config.enabled && !ctx.AvatarRootObject.GetComponents<Component>().Any(c => c && c.GetType().Name == "VRCFuryTest") &&
                ctx.AvatarRootObject.GetComponentsInChildren<Component>(true)
                    .Any(c => c && (c.GetType().Namespace ?? "").StartsWith("VF.", StringComparison.Ordinal) && c.GetType().Name != "VRCFuryDebugInfo");
            if (state.VrcfuryPending)
            {
                const string pending = "VRCFury did not build this avatar before Arclight ran, so its animation was incomplete: only exact mesh and audio de-duplication ran.";
                BuildWarnings.Report(ctx.AvatarRootObject, "VRCFury had not built this avatar", pending,
                    "Build or upload with VRCFury enabled. A manual bake causes this, and so would a VRCFury update that changes how it marks a built avatar.");
                state.Report.Add(null, pending);
            }
            // The texture pass removes the component; the post-d4rk hook reads these requests instead.
            if (config.enabled && config.mergeDuplicates) MergeRequests.Add(ctx.AvatarRootObject);
            if (config.enabled && config.optimizeAnimations) KeyReductionRequests.Add(ctx.AvatarRootObject);
            if (config.enabled && config.splitPhysBones && !state.VrcfuryPending) PhysBoneSplitRequests.Add(ctx.AvatarRootObject);
            // Read by the later passes; the component itself is removed by the texture pass.
            state.MergeMeshesAndAudio = config.enabled && config.mergeDuplicates;
            state.OptimizeMeshes = config.enabled && config.optimizeMeshes;
            state.OptimizeAudio = config.enabled && config.optimizeAudio;
            state.KeepMmdShapes = config.keepMmdShapes;
        }

        private static void Textures(BuildContext ctx)
        {
            BuildTimings.Step("Texture pass");
            var config = ctx.AvatarRootObject.GetComponent<AvatarTextureOptimizer>();
            if (!config) return;
            var state = ctx.GetState<SubstitutionState>();
            Action<UnityEngine.Object, UnityEngine.Object> register =
                ReplacementRegistry.Register;
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
        }

        // New meshes stay in memory; NDMF saves everything the avatar references when the build ends.
        private static void AvatarWide(BuildContext ctx)
        {
            var state = ctx.GetState<SubstitutionState>();
            VertexStreamStripper.NewBuild(); // Compile results in memory last one build.
            AnimatorLayerMerger.NewBuild();
            if (!state.MergeMeshesAndAudio && !state.OptimizeMeshes && !state.OptimizeAudio) return;
            try
            {
                // PhysBone network IDs are fixed before any pass can remove a PhysBone (see NetworkIdPins).
                NetworkIdPins.Pin(ctx.AvatarRootObject);
                BuildTimings.Step("First avatar analysis");
                // An incomplete analysis makes every animation-dependent pass stand down (see Prepare).
                var analysis = state.VrcfuryPending ? AvatarAnalysis.Incomplete(ctx.AvatarRootObject) : AvatarAnalysis.Build(ctx);
                if (!state.VrcfuryPending && !analysis.Complete)
                {
                    const string unread = "Arclight could not read all of this avatar's animation or components, so every optimization that depends on them was skipped.";
                    BuildWarnings.Report(ctx.AvatarRootObject, "The avatar could not be fully read", unread, "The Console has the exception. If it keeps happening, please report it with the avatar's setup.");
                    state.Report?.Add(null, unread);
                }
                bool vrcfuryPending = state.VrcfuryPending;
                if (state.OptimizeMeshes && !vrcfuryPending)
                {
                    BuildTimings.Step("ConstraintConverter");
                    var constraints = ConstraintConverter.Run(ctx.AvatarRootObject, analysis, AnimationRewriter.For(ctx));
                    if (constraints.Converted > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Components and curves changed.
                        string summary = "Converted " + constraints.Converted + " Unity constraint(s) to VRChat constraints with the SDK's converter (" + constraints.Curves +
                            " animation curve(s) moved), as VRChat does when the avatar loads, so it no longer converts them or copies their animation each frame." +
                            (constraints.Kept.Count > 0 ? " Kept " + constraints.Kept.Count + " for VRChat to convert: " + string.Join(", ", constraints.Kept.Take(10)) + "." : "");
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                }
                if (state.OptimizeMeshes)
                {
                    BuildTimings.Step("UnusedObjectRemover");
                    var removed = UnusedObjectRemover.Run(analysis);
                    if (removed.GameObjects + removed.Components > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Removed " + removed.GameObjects + " object(s) and " + removed.Components +
                            " component(s) that can never be seen, heard or used: " + string.Join(", ", removed.Names.Take(20)) +
                            (removed.Names.Count > 20 ? ", ..." : "") + ".";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                        state.Report?.Refresh(ctx.AvatarRootObject, state);
                    }
                    var controllers = ctx.ActivateExtensionContext<VirtualControllerContext>();
                    ParameterCleaner.Result unusedParameters;
                    AnimatorLayerMerger.Result layerMerge;
                    PhysicsCleaner.Result physics;
                    int deadDrivers = 0;
                    try
                    {
                        BuildTimings.Step("ParameterDrivers");
                        // Before folding: a toggle layer whose only behaviour was a dead driver can then fold.
                        if (!vrcfuryPending) deadDrivers = ParameterCleaner.RemoveDeadDrivers(ctx.AvatarRootObject, controllers.Controllers.Values, analysis);
                        BuildTimings.Step("AnimatorLayerMerger");
                        // Folding reads every animation, so it stands down with the rest when the analysis is incomplete.
                        layerMerge = !analysis.Complete ? new AnimatorLayerMerger.Result() : AnimatorLayerMerger.Run(controllers.Controllers.Select(e => (e.Key is Animator a && a ? a.transform : ctx.AvatarRootObject.transform, e.Value)),
                            vrcfuryPending ? null : AnimatorLayerMerger.WholeValues(ctx.AvatarRootObject), removeInert: !vrcfuryPending,
                            playables: controllers.Controllers.Where(e => e.Value != null && e.Key is Enum).GroupBy(e => e.Value).ToDictionary(g => g.Key, g => g.First().Key.ToString()));
                        BuildTimings.Step("ParameterCleaner");
                        unusedParameters = vrcfuryPending ? new ParameterCleaner.Result() : ParameterCleaner.Run(ctx.AvatarRootObject, controllers.Controllers.Values, analysis, ReplacementRegistry.Register);
                        BuildTimings.Step("PhysicsCleaner");
                        physics = vrcfuryPending ? new PhysicsCleaner.Result() : PhysicsCleaner.Run(analysis, controllers.Controllers.Values.Where(c => c != null).SelectMany(c => c.Parameters.Keys),
                            PhysicsCleaner.ExpressionParameters(ctx.AvatarRootObject).ToList());
                    }
                    finally { ControllerCommit.Preserving(ctx.AvatarRootObject, () => ctx.DeactivateExtensionContext<VirtualControllerContext>()); }
                    if (physics.Parameters + physics.Receivers + physics.Colliders + physics.NotAnimated + physics.MergedColliders > 0)
                    {
                        string physicsSummary = "Cleared " + physics.Parameters + " PhysBone parameter(s) no animator reads, removed " + physics.Receivers +
                            " contact receiver(s) whose parameter nothing reads, merged " + physics.MergedColliders + " PhysBone collider(s) identical to another and removed " + physics.Colliders + " empty or repeated PhysBone collider entr(ies), and turned off Is Animated on " +
                            physics.NotAnimated + " PhysBone(s) whose bones nothing else moves.";
                        Debug.Log("Arclight Optimizer: " + physicsSummary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, physicsSummary);
                    }
                    WhyNotLine(state, "animator layer(s) not folded", layerMerge.WhyNot);
                    if (layerMerge.Inert > 0)
                    {
                        string inertSummary = "Removed or emptied " + layerMerge.Inert + " animator layer(s) that can change nothing (weight 0 with nothing shared, or animating only objects the avatar does not have).";
                        Debug.Log("Arclight Optimizer: " + inertSummary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, inertSummary);
                    }
                    if (layerMerge.Merged > 0)
                    {
                        string summary = (layerMerge.Toggles > 0 ? "Converted " + layerMerge.Toggles + " toggle state machine(s) to blend trees that switch in the same frame. " : "") +
                            "Folded " + layerMerge.Merged + " animator layer(s) into a shared Direct blend tree layer" +
                            (layerMerge.Removed == layerMerge.Merged ? " and removed them" : layerMerge.Removed > 0 ? "; removed " + layerMerge.Removed + " and left " + (layerMerge.Merged - layerMerge.Removed) + " empty below a layer a layer control names by index" : " (left empty, as layer controls refer to layers by index)") + "." +
                            (layerMerge.MergedClips > 0 ? " Joined " + layerMerge.MergedClips + " single-clip layer(s) into one clip inside it." : "");
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    if (deadDrivers > 0)
                    {
                        string summary = "Removed " + deadDrivers + " parameter driver write(s) into parameters nothing reads (not expression or built-in parameters).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    if (unusedParameters.Animator > 0)
                    {
                        string summary = "Removed " +
                            unusedParameters.Animator + " animator parameter(s) that nothing reads: " + string.Join(", ", unusedParameters.Names.Take(20)) +
                            (unusedParameters.Names.Count > 20 ? ", ..." : "") + ".";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("StaleTextureCleaner");
                    var stale = StaleTextureCleaner.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx));
                    if (stale.Materials > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Material swaps now name the cleaned copies.
                        string summary = "Cleared " + stale.Textures + " texture(s), " + stale.Values + " value(s) and " + stale.Keywords + " keyword(s) left on " + stale.Materials +
                            " material(s) that the shader never reads (left from earlier shaders, in switched-off lilToon features, or on materials whose shader is missing); they would otherwise be uploaded.";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                }
                BuildTimings.Step("BlendShapeFreezer");
                var frozen = state.OptimizeMeshes
                    ? BlendShapeFreezer.Run(analysis, ReplacementRegistry.Register, state.KeepMmdShapes)
                    : new BlendShapeFreezer.Result();
                BuildTimings.Step("SkinnedMeshMerger");
                var mergedSkinned = state.OptimizeMeshes
                    ? SkinnedMeshMerger.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx))
                    : new SkinnedMeshMerger.Result();
                if (mergedSkinned.Merged > 0) analysis = AvatarAnalysis.Build(ctx); // Renderers and their animation moved.
                WhyNotLine(state, "skinned mesh(es) not merged", mergedSkinned.WhyNot);
                foreach (string hint in mergedSkinned.Hints) state.Report?.Add(null, "You could: " + hint);
                if (state.OptimizeMeshes)
                {
                    BuildTimings.Step("BlendShapeMerger");
                    var shapes = BlendShapeMerger.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx), state.KeepMmdShapes);
                    if (shapes.Merged > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Merged shapes' curves were removed.
                        string summary = "Merged " + shapes.Merged + " blend shape(s) into others that always hold the same weight, on " + shapes.Meshes + " mesh(es).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("BoneCleaner");
                    var bonesResult = BoneCleaner.Run(analysis, ReplacementRegistry.Register, physBones: !vrcfuryPending);
                    if (bonesResult.Bones + bonesResult.PhysBones + bonesResult.Constraints > 0)
                    {
                        BuildTimings.Step("UnusedObjectRemover");
                        var swept = UnusedObjectRemover.Run(analysis);
                        analysis.RescanReferences();
                        string summary = "Removed " + bonesResult.Bones + " zero-weight bone(s) from skinned meshes and " + bonesResult.PhysBones +
                            " PhysBone(s)" + (bonesResult.Constraints > 0 ? " and " + bonesResult.Constraints + " constraint(s)" : "") + " that move nothing visible" + (swept.GameObjects > 0 ? ", then " + swept.GameObjects + " object(s) nothing used any more" : "") + ".";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("BoneMerger");
                    var mergedBones = BoneMerger.Run(analysis, ReplacementRegistry.Register);
                    if (mergedBones.Bones > 0)
                    {
                        string summary = "Merged " + mergedBones.Bones + " bone(s) that never move relative to their parent into it (vertices may round by at most 1/255 per colour channel).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("EndBoneReplacer");
                    var endBones = EndBoneReplacer.Run(analysis);
                    if (endBones.Bones > 0)
                    {
                        string summary = "Replaced " + endBones.Bones + " PhysBone end bone(s) on " + endBones.PhysBones + " PhysBone(s) with their Endpoint Position (the same virtual end point).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("ContainerFlattener");
                    var containers = ContainerFlattener.Run(analysis, AnimationRewriter.For(ctx));
                    if (containers.Containers > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Curve paths moved.
                        string summary = "Removed " + containers.Containers + " empty container object(s) with an identity transform; their children sit on the parent with the same world transforms.";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("RigidAccessoryConverter");
                    var rigid = RigidAccessoryConverter.Run(analysis, ReplacementRegistry.Register);
                    if (rigid.Converted > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Converted " + rigid.Converted + " single-bone skinned mesh(es) into plain meshes under their bone (no skinning).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("StaticMeshMerger");
                    var staticMerge = StaticMeshMerger.Run(analysis, ReplacementRegistry.Register);
                    if (staticMerge.Merged > 0)
                    {
                        string summary = "Merged " + staticMerge.Merged + " plain mesh renderer(s) into " + staticMerge.Into + " that never move apart from them.";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    analysis.RescanReferences();
                    BuildTimings.Step("HiddenPhysBones");
                    var hidden = vrcfuryPending ? new HiddenPhysBones.Result() : HiddenPhysBones.Run(analysis, AnimationRewriter.For(ctx));
                    if (hidden.PhysBones > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // The PhysBones are animated now.
                        string summary = "Turned off " + hidden.PhysBones + " PhysBone(s) while the " + hidden.Objects +
                            " object(s) whose meshes they move are hidden; a chain restarts from its rest pose when shown again.";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("DegenerateTriangleRemover");
                    var flat = DegenerateTriangleRemover.Run(analysis, ReplacementRegistry.Register);
                    if (flat.Triangles > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Removed " + flat.Triangles + " triangle(s) that can never cover a pixel from " + flat.Meshes + " mesh(es).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("SubmeshCleaner");
                    var slots = SubmeshCleaner.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx));
                    if (slots.Removed + slots.Merged > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Material slots and their animation were renumbered.
                        string summary = "Removed " + slots.Removed + " empty or undrawn submesh(es) and merged " + slots.Merged +
                            " submesh(es) into others drawn with an identical opaque material (fewer draw calls).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("UnusedVertexRemover");
                    var unusedVertices = UnusedVertexRemover.Run(analysis, ReplacementRegistry.Register);
                    if (unusedVertices.Meshes > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Removed " + unusedVertices.Vertices + " vertex(es) that no triangle uses or that exactly repeat another from " +
                            unusedVertices.Meshes + " mesh(es).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                }
                bool monoAudio = state.OptimizeAudio && !AudioSettingsAnimated(analysis);
                BuildTimings.Step("MeshAndAudioOptimizer");
                var result = MeshAndAudioOptimizer.Run(ctx.AvatarRootObject, state.MergeMeshesAndAudio, state.OptimizeMeshes,
                    ReplacementRegistry.Register, monoAudio);
                result.FrozenShapes = frozen.Frozen + frozen.Removed;
                result.MergedSkinned = mergedSkinned.Merged;
                result.MergedInto = mergedSkinned.Into;
                result.MeshBytes += frozen.Bytes;
                if (state.OptimizeMeshes)
                {
                    analysis.RescanReferences(); // Merging and compaction replaced meshes.
                    BuildTimings.Step("VertexStreamStripper");
                    var stripped = VertexStreamStripper.Run(analysis, ReplacementRegistry.Register);
                    result.Stripped = stripped.Meshes;
                    result.MeshBytes += stripped.Bytes;
                    if (stripped.Meshes > 0) analysis.RescanReferences();
                    BuildTimings.Step("BlendShapeDeltaStripper");
                    var deltas = BlendShapeDeltaStripper.Run(analysis, ReplacementRegistry.Register);
                    if (deltas.Meshes > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Dropped normal or tangent deltas that move nothing from " + deltas.Frames + " blend shape frame(s) on " + deltas.Meshes + " mesh(es).";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, summary);
                    }
                    BuildTimings.Step("ParticleUpperBound");
                    var particles = ParticleUpperBound.Run(analysis);
                    if (particles.Systems + particles.Removed + particles.Trails + particles.Collisions > 0)
                    {
                        string summary = "Particles: total Max Particles " + particles.Before + " -> " + particles.After + " (" +
                            (OptimizationLog.Count(particles.Systems, "system(s) lowered to their reachable peak") +
                             OptimizationLog.Count(particles.Removed, "system(s) that can never emit removed") +
                             OptimizationLog.Count(particles.Trails, "zero-lifetime trail module(s) off") +
                             OptimizationLog.Count(particles.Collisions, "collision module(s) that can hit nothing off")).TrimEnd(',', ' ') + ").";
                        Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                        if (state.Report != null) { state.Report.Add(null, summary); state.Report.Refresh(ctx.AvatarRootObject, state); }
                    }
                }
                if (result.Changed)
                    Debug.Log("Arclight Optimizer: " + result + " on " + ctx.AvatarRootObject.name + ".");
                if (state.Report != null && result.Changed)
                {
                    state.Report.AddSavings("Audio", result.AudioBytes, OptimizationLog.Count(result.MonoAudio, "made mono") +
                        OptimizationLog.Count(result.AudioClips, "duplicate(s) merged"), result.AudioUnmeasured);
                    state.Report.AddSavings("Meshes", result.MeshBytes, OptimizationLog.Count(result.Meshes, "duplicate(s) merged") +
                        OptimizationLog.Count(result.Compacted, "index buffer(s) halved") +
                        OptimizationLog.Count(result.Stripped, "with unused vertex channels removed") +
                        OptimizationLog.Count(result.FrozenShapes, "blend shape(s) baked or removed") +
                        (result.MergedSkinned > 0 ? result.MergedSkinned + " skinned meshes merged into " + result.MergedInto + ", " : ""));
                    state.Report.Refresh(ctx.AvatarRootObject, state);
                }
                if (monoAudio) CacheCleanup.Schedule(AutomaticTextureOptimizer.CacheFolder);
            }
            catch (Exception e)
            {
                // Optimization only: never fail the build. Every pass after the failing one is skipped, so say so.
                Debug.LogException(e);
                string failed = "Avatar-wide optimization stopped part-way (" + e.GetType().Name + ": " + e.Message +
                    "); the passes after it were skipped. The Console has the details.";
                BuildWarnings.Report(ctx.AvatarRootObject, "Avatar-wide optimization stopped part-way", failed, "Please report this with the Console's exception. The avatar still builds; it is just optimized less.");
                state.Report?.Add(null, failed);
                state.Report?.Refresh(ctx.AvatarRootObject, state);
            }
        }

        // Crops generated and original textures to the aligned part their meshes sample, after texture generation.
        // "Why not" lines: what kept meshes apart or layers unfolded, most common reason first, for the user to act on.
        internal static string WhyNotText(string what, Dictionary<string, List<string>> reasons) =>
            "Why not: " + reasons.Values.Sum(n => n.Count) + " " + what + ": " + string.Join("; ", reasons.OrderByDescending(p => p.Value.Count)
                .Select(p => p.Value.Count + " x " + p.Key + " (" + string.Join(", ", p.Value.Distinct().Take(6)) + (p.Value.Distinct().Count() > 6 ? ", ..." : "") + ")")) + ".";

        private static void WhyNotLine(SubstitutionState state, string what, Dictionary<string, List<string>> reasons)
        {
            if (state.Report == null || reasons == null || reasons.Count == 0) return;
            state.Report.Add(null, WhyNotText(what, reasons));
        }

        private static void Crop(BuildContext ctx)
        {
            BuildTimings.Step("Cropping");
            try { CropTextures(ctx); }
            finally
            {
                // The last Optimizing pass: report where this build spent its time.
                var state = ctx.GetState<SubstitutionState>();
                if (state.Report != null)
                    try { foreach (string hint in CostHints.Collect(ctx.AvatarRootObject)) state.Report.Add(null, "You could: " + hint); }
                    catch (Exception e) { Debug.LogException(e); }
                string timings = BuildTimings.Current?.Summary();
                BuildTimings.Current = null;
                if (timings != null)
                {
                    Debug.Log("Arclight Optimizer: " + timings + " (" + ctx.AvatarRootObject.name + ")");
                    state.Report?.Add(null, timings);
                    state.Report?.Refresh(ctx.AvatarRootObject, state);
                }
            }
        }

        private static void CropTextures(BuildContext ctx)
        {
            var state = ctx.GetState<SubstitutionState>();
            if (!state.OptimizeMeshes || state.VrcfuryPending) return;
            try
            {
                var analysis = AvatarAnalysis.Build(ctx);
                // As in the texture pass: inventory the committed clips (including NDMF marker clips) before
                // activating the virtual context, or the scan treats marker clips as opaque and keeps everything.
                var inventory = AnimationSnapshot.Analyze(ctx.AvatarRootObject, null, null);
                var controllers = ctx.ActivateExtensionContext<VirtualControllerContext>();
                TextureCropper.Result crops;
                try
                {
                    var scan = TextureUsageScanner.Scan(ctx.AvatarRootObject, controllers, inventory);
                    crops = TextureCropper.Run(ctx.AvatarRootObject, scan, analysis, ReplacementRegistry.Register, AutomaticTextureOptimizer.CacheFolder);
                }
                finally { ControllerCommit.Preserving(ctx.AvatarRootObject, () => ctx.DeactivateExtensionContext<VirtualControllerContext>()); }
                if (crops.Textures > 0 || crops.Uniform > 0)
                {
                    analysis.RescanReferences();
                    string summary = (crops.Textures > 0 ? "Cropped " + crops.Textures + " texture(s) to the aligned part their meshes sample" : "") +
                        (crops.Textures > 0 && crops.Uniform > 0 ? "; " : "") +
                        (crops.Uniform > 0 ? "replaced " + crops.Uniform + " one-colour texture(s) with 4 x 4 copies" : "") +
                        " (" + (100 - 100 * crops.PixelsAfter / Math.Max(1, crops.PixelsBefore)) + "% fewer imported texels), on " + crops.Materials + " material(s).";
                    Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
                    state.Report?.Add(null, summary);
                    state.Report?.Refresh(ctx.AvatarRootObject, state);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                string failed = "Texture cropping stopped (" + e.GetType().Name + ": " + e.Message + "). The Console has the details.";
                BuildWarnings.Report(ctx.AvatarRootObject, "Texture cropping stopped", failed, "Please report this with the Console's exception. The avatar still builds; it is just optimized less.");
                state.Report?.Add(null, failed);
                state.Report?.Refresh(ctx.AvatarRootObject, state);
            }
        }

        // Mono +3 dB matches stereo only for the source settings it was measured with, so any animation of an
        // AudioSource or VRC Spatial Audio Source setting other than volume, pitch, mute, enabling or the clip
        // itself turns the conversion off. NDMF marker clips stand for the platform's proxy motions, which hold
        // no audio bindings. Anything that cannot be read also turns it off.
        private static readonly HashSet<string> NeutralAudioProperties = new HashSet<string>(StringComparer.Ordinal)
            { "m_Volume", "m_Pitch", "m_Mute", "m_Enabled", "m_audioClip", "Gain" };

        private static bool AudioSettingsAnimated(AvatarAnalysis analysis)
        {
            bool Changes(EditorCurveBinding binding) => binding.type != null &&
                (typeof(AudioSource).IsAssignableFrom(binding.type) || AudioMonoConverter.IsVrcSpatialSource(binding.type)) &&
                !NeutralAudioProperties.Contains(binding.propertyName ?? "");
            return !analysis.Complete || analysis.Bindings.Any(b => Changes(b.Curve));
        }

    }

    internal sealed class SubstitutionState
    {
        public readonly Dictionary<Material, Material> Clones = new Dictionary<Material, Material>();
        public bool Applied;
        public bool Cancelled;
        public bool MergeMeshesAndAudio, OptimizeMeshes, OptimizeAudio;
        // Set before Avatar Optimizer runs when the avatar carries it; then no Arclight pass acts.
        internal bool AvatarOptimizerConflict;

        // VRCFury has not built the avatar yet, so its animation is incomplete (see Prepare).
        internal bool VrcfuryPending;
        // The component's MMD Support setting: keep the shapes MMD dance worlds animate.
        internal bool KeepMmdShapes = true;
        public int AppliedTextures;
        public readonly HashSet<Texture> MergedDuplicates = new HashSet<Texture>();
        internal AnimationSnapshot SerializedAnimation;
        public readonly GenerationSummary Summary = new GenerationSummary();
        // The build report: started by the first pass, written by the texture pass, refreshed by later passes.
        internal OptimizationLog Report;
    }

    internal static class ReplacementRegistry
    {
        // Records that the late passes replaced an object, for tools that follow references through the build.
        // Recording is information only: when the replacement is an object that already had its reference taken (a
        // merge host, for example), NDMF refuses the record, and the pass carries on rather than stopping the build.
        internal static void Register(UnityEngine.Object original, UnityEngine.Object replacement)
        {
            if (!original || !replacement) throw new ArgumentNullException(original ? nameof(replacement) : nameof(original));
            ObjectRegistry.TryRegisterReplacedObject(ObjectRegistry.GetReference(original), replacement);
        }
    }
    internal static class ControllerCommit
    {
        // NDMF's commit (deactivating the virtual controller context) assigns every controller again, and a bound Animator
        // then writes back the defaults it captured at its last bind into animated slots. Those can be materials a pass has
        // since replaced, or slots a pass has renumbered. Keep every slot as it was before the commit, and rebind, so the
        // kept materials become the captured defaults.
        internal static void Preserving(GameObject root, Action commit)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToList();
            var before = renderers.ToDictionary(r => r, r => r.sharedMaterials);
            BuildTimings.Commit(commit);
            var changed = renderers.Where(r => r && !r.sharedMaterials.SequenceEqual(before[r])).ToList();
            if (changed.Count == 0) return;
            var animators = root.GetComponentsInChildren<Animator>(true).Where(a => a.runtimeAnimatorController)
                .ToDictionary(a => a, a => a.runtimeAnimatorController);
            foreach (var animator in animators.Keys) animator.runtimeAnimatorController = null;
            try { foreach (var renderer in changed) renderer.sharedMaterials = before[renderer]; }
            finally { foreach (var pair in animators) pair.Key.runtimeAnimatorController = pair.Value; }
        }
    }

    internal static class TemporaryMaterialSubstituter
    {
        internal static void ReapplyAfterControllerCommit(GameObject root, SubstitutionState state)
        {
            if (state.Clones.Count == 0) return;
            // Assigning a controller makes an Animator write back the defaults it captured when last bound
            // (here, the original material slots) and capture new ones. Unbind, apply the clones, then
            // rebind so the clones become the captured defaults; any later controller reassignment (for
            // example d4rk's or VRCFury's) then keeps them instead of restoring the originals.
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
