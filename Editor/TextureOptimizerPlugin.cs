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
                    // Late-hook requests travel in per-build sets mirrored into SessionState by scene-rooted path:
                    // an aborted build leaves its entries behind, so clear the sets and the mirrors before anything
                    // this build may request.
                    MergeRequests.BeginBuild(); KeyReductionRequests.BeginBuild(); StreamRequests.BeginBuild();
                    // Re-assert the callback order the late hook depends on (idempotent and cheap).
                    RivalState.EnsureOrderingBeforeLate();
                    // Play Mode never runs the SDK preprocess hook that snapshots source assets, so take the snapshot
                    // here (conservative: anything VRCFury already generated counts as pre-existing).
                    if (EditorApplication.isPlayingOrWillChangePlaymode) BuildOwnership.Begin();
                    Exclusions.Record(ctx.AvatarRootObject);
                    ctx.GetState<SubstitutionState>().SourceMeshes = MeshReadWrite.Record(ctx.AvatarRootObject);
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
            AnalyzerBuildNotes.Reset();
            GenerationCoordinator.ImporterFallbacks = 0; // Diagnostics are per build, not per editor session.
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
            state.Enabled = config.enabled;
            // VRCFury keeps its components until the very end of the build and marks a built avatar with VRCFuryTest.
            // Before it builds (a manual bake, or VRCFury switched off for this kind of build), the animation, parameters
            // and readers it will add are not here yet, so nothing that depends on the avatar's animation may run.
            // VRCFury's own gate says which avatars it would build at all: its builder skips any avatar carrying no
            // VRCFury component. So an un-built avatar with no VRCFury components anywhere is one VRCFury legitimately
            // passed over, and its animation is already complete — the full passes may run. An avatar VRCFury should
            // have built still carries its components whenever it was not built, so the stand-down there is unchanged.
            var rivals = RivalState.Of(ctx.AvatarRootObject);
            state.VrcfuryPending = config.enabled && rivals.AnimationIncomplete;
            if (state.VrcfuryPending)
            {
                AnalyzerBuildNotes.VrcfuryStoodDown = true;
                const string pending = "VRCFury did not build this avatar before Arclight ran, so its animation was incomplete: only exact mesh and audio de-duplication ran.";
                BuildWarnings.Report(ctx.AvatarRootObject, "VRCFury had not built this avatar", pending,
                    "Build or upload with VRCFury enabled. A manual bake causes this, and so would a VRCFury update that changes how it marks a built avatar.");
                state.Report.Add(null, pending);
            }
            // The texture pass removes the component; the late optimizer hook reads these requests instead.
            if (config.enabled && config.mergeDuplicates) MergeRequests.Add(ctx.AvatarRootObject);
            if (config.enabled && config.optimizeAnimations) KeyReductionRequests.Add(ctx.AvatarRootObject);
            // When a later optimizer rewrites meshes, the stream pass waits until after its merge (see VertexStreamStripper).
            if (config.enabled && config.optimizeMeshes && rivals.StructureChanges)
                StreamRequests.Add(ctx.AvatarRootObject);
            // Read by the later passes; the component itself is removed by the texture pass.
            state.MergeMeshesAndAudio = config.enabled && config.mergeDuplicates;
            state.OptimizeMeshes = config.enabled && config.optimizeMeshes;
            state.OptimizeAudio = config.enabled && config.optimizeAudio;
            state.CompressAudio = config.enabled && config.optimizeAudio && config.compressAudio;
            state.AudioQuality = config.audioQuality;
            state.MenuIconMaxSize = config.enabled ? (int)config.menuIconCap : 0;
            // Quest keeps every authored size: the cap is a new route and Android device testing is unavailable.
            state.TextureSizeCap = config.enabled && GeneratedTargetValidator.IsStandalone ? (int)config.textureSizeCap : 0;
            // Opt-in, PC only: BC7 -> BC1 halves opaque colour textures, and the banding it can show is the avatar's call.
            ChannelFormats.NarrowBc7 = config.enabled && config.narrowOpaqueTextures && GeneratedTargetValidator.IsStandalone;

            state.KeepMmdShapes = config.keepMmdShapes;
            state.UnifiedBounds = config.unifiedBounds;
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
                if (state.OptimizeMeshes)
                {
                    BuildTimings.Step("ProbeAnchorUnifier");
                    var anchored = ProbeAnchorUnifier.Run(ctx.AvatarRootObject);
                    if (anchored.Changed > 0)
                    {
                        string summary = "Gave " + OptimizationLog.Plural(anchored.Changed, "mesh", "meshes") + " the same lighting point (" + anchored.Anchor + "), so they're lit alike and can merge.";
                        Announce(ctx, state, summary);
                    }
                }
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
                if (state.OptimizeMeshes && state.UnifiedBounds && !vrcfuryPending)
                {
                    BuildTimings.Step("BoundsUnifier");
                    var unified = BoundsUnifier.Run(analysis);
                    if (unified.Changed > 0)
                    {
                        string summary = "Gave " + OptimizationLog.Plural(unified.Changed, "skinned mesh", "skinned meshes") + " one " + unified.Size.ToString("0.00") +
                            " m box covering every pose, so nothing vanishes up close and meshes can merge; meshes may draw when just off screen." +
                            (unified.Kept.Count > 0 ? " Kept their own bounds: " + OptimizationLog.FirstNames(unified.Kept, 20) + "." : "");
                        Announce(ctx, state, summary);
                    }
                    else if (unified.Kept.Count > 0)
                    {
                        // The setting is on and nothing moved: say why, or it looks as if the setting did not run.
                        string summary = "Unified Bounds changed nothing: " + OptimizationLog.Plural(unified.Kept.Count, "skinned mesh keeps", "skinned meshes keep") + " their own bounds: " +
                            OptimizationLog.FirstNames(unified.Kept, 20) + ".";
                        Announce(ctx, state, summary);
                    }
                }
                if (state.OptimizeMeshes && !vrcfuryPending)
                {
                    BuildTimings.Step("ConstraintConverter");
                    var constraints = ConstraintConverter.Run(ctx.AvatarRootObject, analysis, AnimationRewriter.For(ctx));
                    if (constraints.Converted > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Components and curves changed.
                        string summary = "Converted " + OptimizationLog.Plural(constraints.Converted, "Unity constraint") + " to VRChat constraints with the SDK's converter (" + OptimizationLog.Plural(constraints.Curves, "animation curve") +
                            " moved), as VRChat does when the avatar loads, so it no longer converts them or copies their animation each frame." +
                            (constraints.Kept.Count > 0 ? " Kept " + constraints.Kept.Count + " for VRChat to convert: " + string.Join(", ", constraints.Kept.Take(10)) + "." : "");
                        Announce(ctx, state, summary);
                    }
                }
                if (state.OptimizeMeshes)
                {
                    BuildTimings.Step("UnusedObjectRemover");
                    int animators = ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true).Length;
                    var removed = UnusedObjectRemover.Run(analysis);
                    if (removed.GameObjects + removed.Components > 0)
                    {
                        // A removed Animator no longer marks its bones as moving; only a full rebuild refreshes that (Fable final review).
                        if (ctx.AvatarRootObject.GetComponentsInChildren<Animator>(true).Length < animators) analysis = AvatarAnalysis.Build(ctx);
                        else analysis.RescanReferences();
                        string summary = "Removed " + OptimizationLog.Plural(removed.GameObjects, "object") + " and " + OptimizationLog.Plural(removed.Components, "component") +
                            " that can never be seen, heard or used: " + OptimizationLog.FirstNames(removed.Names, 20) + ".";
                        Announce(ctx, state, summary, refresh: true);
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
                        layerMerge = !analysis.Complete ? new AnimatorLayerMerger.Result() : AnimatorLayerMerger.Run(controllers.Controllers
                            .Where(e => !(e.Key is UnityEngine.Object gone && !gone)) // Destroyed Animator: no readable owner to bind to.
                            .Select(e => (e.Key is Animator a && a ? a.transform : ctx.AvatarRootObject.transform, e.Value)),
                            vrcfuryPending ? null : AnimatorLayerMerger.WholeValues(ctx.AvatarRootObject), removeInert: !vrcfuryPending,
                            playables: controllers.Controllers.Where(e => e.Value != null && e.Key is Enum).GroupBy(e => e.Value).ToDictionary(g => g.Key, g => g.First().Key.ToString()),
                            unwritten: vrcfuryPending ? null : AnimatorLayerMerger.Unwritten(ctx.AvatarRootObject));
                        BuildTimings.Step("PhysicsCleaner");
                        // What the animators and menu read, not what they declare: a receiver's parameter that nothing tests is dead even
                        // though declared, and once the receiver goes, the parameter cleaner below removes the declaration too.
                        var reads = ParameterCleaner.AnimatorReads(ctx.AvatarRootObject, controllers.Controllers.Values, analysis) ??
                            new HashSet<string>(controllers.Controllers.Values.Where(c => c != null).SelectMany(c => c.Parameters.Keys));
                        physics = vrcfuryPending ? new PhysicsCleaner.Result() : PhysicsCleaner.Run(analysis, reads,
                            PhysicsCleaner.ExpressionParameters(ctx.AvatarRootObject).ToList());
                        BuildTimings.Step("ParameterCleaner");
                        unusedParameters = vrcfuryPending ? new ParameterCleaner.Result() : ParameterCleaner.Run(ctx.AvatarRootObject, controllers.Controllers.Values, analysis);
                    }
                    finally { ControllerCommit.Preserving(ctx.AvatarRootObject, () => ctx.DeactivateExtensionContext<VirtualControllerContext>()); }
                    if (physics.Parameters + physics.Receivers + physics.EmptyShapes + physics.Colliders + physics.NotAnimated + physics.MergedColliders > 0)
                    {
                        string physicsSummary = string.Join(" ", new[]
                        {
                            physics.Parameters > 0 ? "Cleared " + OptimizationLog.Plural(physics.Parameters, "PhysBone parameter") + " no animator reads." : null,
                            physics.Receivers > 0 ? "Removed " + OptimizationLog.Plural(physics.Receivers, "contact receiver") + " whose parameter nothing reads." : null,
                            physics.EmptyShapes > 0 ? "Removed " + OptimizationLog.Plural(physics.EmptyShapes, "contact receiver") + " with no volume." : null,
                            physics.MergedColliders > 0 ? "Merged " + OptimizationLog.Plural(physics.MergedColliders, "duplicate PhysBone collider") + "." : null,
                            physics.Colliders > 0 ? "Removed " + OptimizationLog.Plural(physics.Colliders, "empty or repeated collider entry", "empty or repeated collider entries") + "." : null,
                            physics.NotAnimated > 0 ? "Turned off Is Animated on " + OptimizationLog.Plural(physics.NotAnimated, "PhysBone") + " nothing else moves." : null,
                        }.Where(part => part != null));
                        Debug.Log("Arclight Optimizer: " + physicsSummary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, physicsSummary);
                    }
                    if (physics.Receivers + physics.EmptyShapes + physics.Colliders + physics.MergedColliders > 0)
                    {
                        // Objects kept only for a removed receiver or collider entry can go now (Fable final review).
                        BuildTimings.Step("UnusedObjectRemover");
                        var swept = UnusedObjectRemover.Run(analysis);
                        if (swept.GameObjects + swept.Components > 0)
                        {
                            analysis.RescanReferences();
                            string summary = "Removed " + OptimizationLog.Plural(swept.GameObjects, "object") + " and " + OptimizationLog.Plural(swept.Components, "component") + " nothing used once the physics cleanup ran: " +
                                OptimizationLog.FirstNames(swept.Names, 20) + ".";
                            Announce(ctx, state, summary);
                        }
                    }
                    WhyNotLine(state, "animator layer not merged", "animator layers not merged", layerMerge.WhyNot);
                    if (layerMerge.Inert > 0)
                    {
                        string inertSummary = "Removed or emptied " + OptimizationLog.Plural(layerMerge.Inert, "animator layer") + " that can change nothing (weight 0 with nothing shared, or animating only objects the avatar does not have).";
                        Debug.Log("Arclight Optimizer: " + inertSummary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, inertSummary);
                    }
                    if (layerMerge.FrozenTime > 0)
                    {
                        string frozenSummary = "Set " + OptimizationLog.Plural(layerMerge.FrozenTime, "animator state that never moves", "animator states that never move") + " past the first frame to that frame.";
                        Debug.Log("Arclight Optimizer: " + frozenSummary + " (" + ctx.AvatarRootObject.name + ")");
                        state.Report?.Add(null, frozenSummary);
                    }
                    if (layerMerge.Merged > 0)
                    {
                        string summary = (layerMerge.Toggles > 0 ? "Converted " + OptimizationLog.Plural(layerMerge.Toggles, "toggle state machine") + " to blend trees that switch in the same frame. " : "") +
                            "Merged " + OptimizationLog.Plural(layerMerge.Merged, "animator layer") + " into one" +
                            (layerMerge.Removed == layerMerge.Merged ? "" : layerMerge.Removed > 0 ? "; left " + (layerMerge.Merged - layerMerge.Removed) + " empty (a Layer Control refers to them by number)" : " (left empty: a Layer Control refers to them by number)") + "." +
                            (layerMerge.MergedClips > 0 ? " Merged " + OptimizationLog.Plural(layerMerge.MergedClips, "single-clip layer") + " into one clip." : "");
                        Announce(ctx, state, summary);
                    }
                    if (deadDrivers > 0)
                    {
                        string summary = "Removed " + OptimizationLog.Plural(deadDrivers, "parameter driver write") + " into parameters nothing reads (not expression or built-in parameters).";
                        Announce(ctx, state, summary);
                    }
                    if (unusedParameters.Animator > 0)
                    {
                        string summary = "Removed " +
                            OptimizationLog.Plural(unusedParameters.Animator, "animator parameter") + " that nothing reads: " + OptimizationLog.FirstNames(unusedParameters.Names, 20) + ".";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("MenuIconMerger");
                    var icons = vrcfuryPending ? new MenuIconMerger.Result() : MenuIconMerger.Run(ctx.AvatarRootObject, state.MenuIconMaxSize);
                    if (icons.Total > 0)
                    {
                        // Say which of the three things actually happened to the menu's icons.
                        var parts = new System.Collections.Generic.List<string>();
                        if (icons.Merged > 0) parts.Add(icons.Merged + " merged onto an identical icon or texture");
                        if (icons.Capped > 0) parts.Add(icons.Capped + " capped at " + state.MenuIconMaxSize + " px");
                        if (icons.Optimized - icons.Capped > 0)
                            parts.Add((icons.Optimized - icons.Capped) + " optimized without resizing (lighter format)");
                        string summary = "Pointed the expressions menu at " + OptimizationLog.Plural(icons.Total, "build copy", "build copies") + ": " + string.Join("; ", parts) + ".";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("CookieMerger");
                    int cookies = vrcfuryPending ? 0 : CookieMerger.Run(ctx.AvatarRootObject);
                    if (cookies > 0)
                    {
                        string summary = "Pointed " + OptimizationLog.Plural(cookies, "light cookie") + " at an identical copy already on the avatar (same file and import settings).";
                        Announce(ctx, state, summary);
                    }
                    // What the animator passes did, as counts next to the measured savings (each also has its own line below).
                    state.Report?.AddSavings(OptimizationLog.AnimatorCategory, 0,
                        OptimizationLog.Count(layerMerge.Merged, "layer folded", "layers folded") + OptimizationLog.Count(layerMerge.Inert, "inert layer removed or emptied", "inert layers removed or emptied") +
                        OptimizationLog.Count(unusedParameters.Animator, "unread parameter removed", "unread parameters removed") + OptimizationLog.Count(deadDrivers, "dead driver write removed", "dead driver writes removed") +
                        OptimizationLog.Count(physics.Parameters, "PhysBone parameter cleared", "PhysBone parameters cleared") +                        OptimizationLog.Count(icons.Merged + icons.Optimized, "menu icon copy pointed at", "menu icon copies pointed at"));
                    BuildTimings.Step("StaleTextureCleaner");
                    var stale = StaleTextureCleaner.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx));
                    if (stale.Materials > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Material swaps now name the cleaned copies.
                        string summary = "Cleared " + OptimizationLog.Plural(stale.Textures, "texture") + ", " + OptimizationLog.Plural(stale.Values, "value") + " and " + OptimizationLog.Plural(stale.Keywords, "keyword") + " left on " + OptimizationLog.Plural(stale.Materials, "material") +
                            " that the shader never reads (left from earlier shaders, in switched-off lilToon features, or on materials whose shader is missing); they would otherwise be uploaded.";
                        Announce(ctx, state, summary);
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
                WhyNotLine(state, "skinned mesh not merged", "skinned meshes not merged", mergedSkinned.WhyNot);
                AnalyzerBuildNotes.SkinnedWhyNot = mergedSkinned.WhyNot;
                foreach (string hint in mergedSkinned.Hints) state.Report?.Add(null, "You could: " + hint);
                // Unlocked Poiyomi: Arclight reads the full, unspecialized shader, which keeps data the locked one would not need.
                var poiyomi = new PoiyomiAdapter();
                var unlocked = ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                    .Where(m => m && poiyomi.Matches(m) && !m.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal))
                    .Select(m => m.name.Replace("(Clone)", "")).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (unlocked.Count > 0)
                    state.Report?.Add(null, "You could: lock " + OptimizationLog.Plural(unlocked.Count, "Poiyomi material") + " in Poiyomi's inspector before building (" +
                        OptimizationLog.FirstNames(unlocked, 10) + "). Unlocked, Arclight reads the full Poiyomi shader, " +
                        "so it keeps textures and values the locked shader would never use. If d4rk merges these materials, it prefers them unlocked " +
                        "(it bakes its own constants), except materials with Rename Animated properties, which should stay locked.");
                // VRCFury copies a texture whose mipmaps are on but Streaming Mipmaps is off (the SDK requires it) into its build
                // folder (FixMipmapStreamingService). The copy holds the already compressed texture, so Arclight keeps it; with
                // Streaming Mipmaps on in the source's import settings there is no copy and the source image is processed.
                var vrcfuryCopies = ctx.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials.Concat(analysis.SwappedMaterials(r))).Where(m => m).Distinct()
                    .SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture)).OfType<Texture2D>().Distinct()
                    .Where(t => AssetDatabase.GetAssetPath(t).StartsWith("Packages/com.vrcfury.temp/", StringComparison.Ordinal) && t.streamingMipmaps)
                    // Same-named textures in different folders are common: list every matching candidate
                    // rather than guessing the first one.
                    .SelectMany(t => AssetDatabase.FindAssets(t.name + " t:Texture2D").Select(AssetDatabase.GUIDToAssetPath)
                        .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && System.IO.Path.GetFileNameWithoutExtension(p) == t.name &&
                            AssetImporter.GetAtPath(p) is TextureImporter importer && importer.mipmapEnabled && !importer.streamingMipmaps))
                    .Where(p => p != null).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (vrcfuryCopies.Count > 0)
                    state.Report?.Add(null, "You could: turn on Streaming Mipmaps in the import settings of " + OptimizationLog.Plural(vrcfuryCopies.Count, "texture") + " (" +
                        string.Join(", ", vrcfuryCopies.Take(10).Select(System.IO.Path.GetFileNameWithoutExtension)) + (vrcfuryCopies.Count > 10 ? ", ..." : "") +
                        "). VRChat requires it, so VRCFury uploads an already compressed copy of each instead, which Arclight cannot optimize; with it on, Arclight optimizes the original images.");
                if (state.OptimizeMeshes)
                {
                    BuildTimings.Step("BlendShapeMerger");
                    var shapes = BlendShapeMerger.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx), state.KeepMmdShapes);
                    if (shapes.Merged > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Merged shapes' curves were removed.
                        string summary = "Merged " + OptimizationLog.Plural(shapes.Merged, "blend shape") + " into others that always move together (the same weight, or a fixed ratio of it), on " + OptimizationLog.Plural(shapes.Meshes, "mesh", "meshes") + ".";
                        Announce(ctx, state, summary);
                    }
                    WhyNotLine(state, "renderer whose animated blend shapes were not considered for merging", "renderers whose animated blend shapes were not considered for merging", shapes.RendererWhyNot);
                    WhyNotLine(state, "blend shape animated like another but not merged into it", "blend shapes animated like another but not merged into it", shapes.ShapeWhyNot);
                    BuildTimings.Step("BoneCleaner");
                    var bonesResult = BoneCleaner.Run(analysis, ReplacementRegistry.Register, physBones: !vrcfuryPending);
                    if (bonesResult.Bones + bonesResult.PhysBones + bonesResult.Constraints > 0)
                    {
                        BuildTimings.Step("UnusedObjectRemover");
                        var swept = UnusedObjectRemover.Run(analysis);
                        analysis.RescanReferences();
                        string summary = "Removed " + OptimizationLog.Plural(bonesResult.Bones, "zero-weight bone") + " from skinned meshes and " + OptimizationLog.Plural(bonesResult.PhysBones, "PhysBone") +
                            (bonesResult.Constraints > 0 ? " and " + OptimizationLog.Plural(bonesResult.Constraints, "constraint") : "") + " that move nothing visible" + (swept.GameObjects > 0 ? ", then " + OptimizationLog.Plural(swept.GameObjects, "object") + " nothing used any more" : "") + ".";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("BoneMerger");
                    var mergedBones = BoneMerger.Run(analysis, ReplacementRegistry.Register);
                    if (mergedBones.Bones > 0)
                    {
                        string summary = "Merged " + OptimizationLog.Plural(mergedBones.Bones, "bone") + " that never move relative to their parent into it (colours may differ by at most 1/255).";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("EndBoneReplacer");
                    var endBones = EndBoneReplacer.Run(analysis);
                    if (endBones.Bones > 0)
                    {
                        string summary = "Replaced " + OptimizationLog.Plural(endBones.Bones, "PhysBone end bone") + " on " + OptimizationLog.Plural(endBones.PhysBones, "PhysBone") + " with their Endpoint Position (the same virtual end point).";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("ContainerFlattener");
                    var containers = ContainerFlattener.Run(analysis, AnimationRewriter.For(ctx));
                    if (containers.Containers > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Curve paths moved.
                        string summary = "Removed " + OptimizationLog.Plural(containers.Containers, "empty container object") + " with an identity transform; their children sit on the parent with the same world transforms.";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("RigidAccessoryConverter");
                    var rigid = RigidAccessoryConverter.Run(analysis, ReplacementRegistry.Register);
                    if (rigid.Converted > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Converted " + OptimizationLog.Plural(rigid.Converted, "single-bone skinned mesh", "single-bone skinned meshes") + " into plain meshes under their bone (no skinning).";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("StaticMeshMerger");
                    var staticMerge = StaticMeshMerger.Run(analysis, ReplacementRegistry.Register);
                    if (staticMerge.Merged > 0)
                    {
                        string summary = "Merged " + OptimizationLog.Plural(staticMerge.Merged, "plain mesh renderer") + " into " + staticMerge.Into + " that never move apart from them.";
                        Announce(ctx, state, summary);
                    }
                    analysis.RescanReferences();
                    BuildTimings.Step("HiddenPhysBones");
                    var hidden = vrcfuryPending ? new HiddenPhysBones.Result() : HiddenPhysBones.Run(analysis, AnimationRewriter.For(ctx));
                    if (hidden.PhysBones > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // The PhysBones are animated now.
                        string summary = "Turned off " + OptimizationLog.Plural(hidden.PhysBones, "PhysBone") + " while the " + OptimizationLog.Plural(hidden.Objects, "object they move is", "objects they move are") +
                            " hidden; a chain restarts from its rest pose when shown again.";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("DegenerateTriangleRemover");
                    var flat = DegenerateTriangleRemover.Run(analysis, ReplacementRegistry.Register);
                    if (flat.Triangles > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Removed " + OptimizationLog.Plural(flat.Triangles, "triangle") + " that can never cover a pixel from " + OptimizationLog.Plural(flat.Meshes, "mesh", "meshes") + ".";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("SubmeshCleaner");
                    var slots = SubmeshCleaner.Run(analysis, ReplacementRegistry.Register, AnimationRewriter.For(ctx));
                    if (slots.Removed + slots.Merged > 0)
                    {
                        analysis = AvatarAnalysis.Build(ctx); // Material slots and their animation were renumbered.
                        string summary = "Removed " + OptimizationLog.Plural(slots.Removed, "empty or undrawn submesh", "empty or undrawn submeshes") + " and merged " + OptimizationLog.Plural(slots.Merged, "submesh", "submeshes") +
                            " into others drawn with an identical opaque material (fewer draw calls).";
                        Announce(ctx, state, summary);
                    }
                    BuildTimings.Step("UnusedVertexRemover");
                    var unusedVertices = UnusedVertexRemover.Run(analysis, ReplacementRegistry.Register);
                    if (unusedVertices.Meshes > 0)
                    {
                        analysis.RescanReferences();
                        string summary = "Removed " + OptimizationLog.Plural(unusedVertices.Vertices, "vertex", "vertices") + " that no triangle uses or that exactly repeat another from " +
                            OptimizationLog.Plural(unusedVertices.Meshes, "mesh", "meshes") + ".";
                        Announce(ctx, state, summary);
                    }
                }
                var monoVetoed = AudioSettingsAnimated(analysis);
                bool monoAudio = state.OptimizeAudio && monoVetoed != null;
                BuildTimings.Step("MeshAndAudioOptimizer");
                var result = MeshAndAudioOptimizer.Run(ctx.AvatarRootObject, state.MergeMeshesAndAudio, state.OptimizeMeshes,
                    ReplacementRegistry.Register, monoAudio, monoVetoed, state.CompressAudio, state.AudioQuality);
                result.FrozenShapes = frozen.Frozen + frozen.Removed;
                result.MergedSkinned = mergedSkinned.Merged - mergedSkinned.Rigid;
                result.MergedRigid = mergedSkinned.Rigid;
                result.MergedInto = mergedSkinned.Into;
                result.MeshBytes += frozen.Bytes;
                if (state.OptimizeMeshes)
                {
                    BuildTimings.Step("Reference rescan");
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
                        var parts = new List<string>();
                        if (deltas.Frames > 0) parts.Add("removed normal or tangent deltas that move nothing from " + OptimizationLog.Plural(deltas.Frames, "frame"));
                        if (deltas.DroppedFrames > 0) parts.Add("removed " + OptimizationLog.Plural(deltas.DroppedFrames, "frame") + " that only repeated their neighbours' interpolation");
                        string summary = "Blend shapes on " + OptimizationLog.Plural(deltas.Meshes, "mesh", "meshes") + ": " + string.Join("; ", parts) + ".";
                        Announce(ctx, state, summary);
                    }
                    // Vertices that differed only in a channel or delta just dropped are exact repeats now: weld again.
                    if (stripped.Meshes + deltas.Meshes > 0 && !vrcfuryPending)
                    {
                        BuildTimings.Step("UnusedVertexRemover (after stripping)");
                        var welded = UnusedVertexRemover.Run(analysis, ReplacementRegistry.Register);
                        if (welded.Meshes > 0)
                        {
                            analysis.RescanReferences();
                            string summary = "Merged " + OptimizationLog.Plural(welded.Vertices, "more duplicate vertex", "more duplicate vertices") + " once unread data was removed, in " + OptimizationLog.Plural(welded.Meshes, "mesh", "meshes") + ".";
                            Announce(ctx, state, summary);
                        }
                    }
                    BuildTimings.Step("ParticleUpperBound");
                    var particles = ParticleUpperBound.Run(analysis);
                    if (particles.Systems + particles.Removed + particles.Trails + particles.Collisions > 0)
                    {
                        string summary = "Particles: total Max Particles " + particles.Before + " -> " + particles.After + " (" +
                            (OptimizationLog.Count(particles.Systems, "system lowered to its reachable peak", "systems lowered to their reachable peak") +
                             OptimizationLog.Count(particles.Removed, "system that can never emit removed", "systems that can never emit removed") +
                             OptimizationLog.Count(particles.Trails, "zero-lifetime trail module off", "zero-lifetime trail modules off") +
                             OptimizationLog.Count(particles.Collisions, "collision module that can hit nothing off", "collision modules that can hit nothing off")).TrimEnd(',', ' ') + ").";
                        Announce(ctx, state, summary, refresh: true);
                    }
                }
                if (result.Changed)
                    Debug.Log("Arclight Optimizer: " + result + " on " + ctx.AvatarRootObject.name + ".");
                if (state.Report != null && result.Changed)
                {
                    state.Report.AddSavings("Audio", result.AudioBytes, OptimizationLog.Count(result.MonoAudio, "made mono") +
                        OptimizationLog.Count(result.AudioClips, "duplicate merged", "duplicates merged") +
                        OptimizationLog.Count(result.CompressedAudio, "re-encoded as Vorbis"), result.AudioUnmeasured);
                    state.Report.AddSavings("Meshes", result.MeshBytes, OptimizationLog.Count(result.Meshes, "duplicate merged", "duplicates merged") +
                        OptimizationLog.Count(result.Compacted, "index buffer halved", "index buffers halved") +
                        OptimizationLog.Count(result.Stripped, "with unused vertex channels removed") +
                        OptimizationLog.Count(result.FrozenShapes, "blend shape baked or removed", "blend shapes baked or removed") +
                        (result.MergedSkinned > 0 ? result.MergedSkinned + " skinned meshes merged into " + result.MergedInto +
                            (mergedSkinned.Contacts > 0 ? " (" + OptimizationLog.Plural(mergedSkinned.Contacts, "material contact") + " followed the merge)" : "") + ", " : ""));
                    state.Report.Refresh(ctx.AvatarRootObject, state);
                }
                if (monoAudio || state.CompressAudio) CacheCleanup.Schedule(AutomaticTextureOptimizer.CacheFolder);
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
            "Why not: " + reasons.Values.Sum(n => n.Count) + " " + what + ". " + string.Join("; ", reasons.OrderByDescending(p => p.Value.Count)
                .Select(p => p.Value.Count + " because " + p.Key + " (" + string.Join(", ", p.Value.Distinct().Take(6)) + (p.Value.Distinct().Count() > 6 ? ", ..." : "") + ")")) + ".";

        private static void WhyNotLine(SubstitutionState state, string one, string many, Dictionary<string, List<string>> reasons)
        {
            if (state.Report == null || reasons == null || reasons.Count == 0) return;
            state.Report.Add(null, WhyNotText(reasons.Values.Sum(n => n.Count) == 1 ? one : many, reasons));
        }

        private static void Crop(BuildContext ctx)
        {
            BuildTimings.Step("Cropping");
            try
            {
                CropTextures(ctx);
                // Last, so the crop cuts full-resolution pixels and can bring a texture under the cap by itself.
                CapTextures(ctx);
                BuildTimings.Step("MeshReadWrite");
                // Like every other pass: not when Arclight is off, removed, or declined (Avatar Optimizer, Play Mode without scene reload).
                int unreadable = ctx.GetState<SubstitutionState>().Enabled
                    ? MeshReadWrite.Run(ctx.AvatarRootObject, ctx.GetState<SubstitutionState>().SourceMeshes) : 0;
                if (unreadable > 0) ctx.GetState<SubstitutionState>().Report?.Add(null, "Turned off Read/Write on " + OptimizationLog.Plural(unreadable, "mesh", "meshes") + " this build made, so the avatar does not keep a second copy of them in memory.");
            }
            catch (Exception e)
            {
                // MeshReadWrite.Run touches every renderer and SerializedObject in the build and can
                // throw; like every other pass, a failure keeps the avatar built, just less optimized.
                Debug.LogException(e);
                string failed = "Mesh Read/Write cleanup stopped (" + e.GetType().Name + ": " + e.Message + "). The Console has the details.";
                BuildWarnings.Report(ctx.AvatarRootObject, "Mesh Read/Write cleanup stopped", failed, "Please report this with the Console's exception. The avatar still builds; it is just optimized less.");
                var state = ctx.GetState<SubstitutionState>();
                state.Report?.Add(null, failed);
                state.Report?.Refresh(ctx.AvatarRootObject, state);
            }
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

        // The opt-in texture size cap: every texture the build's materials use, after generation and cropping.
        private static void CapTextures(BuildContext ctx)
        {
            var state = ctx.GetState<SubstitutionState>();
            if (!state.Enabled || state.TextureSizeCap <= 0 || state.VrcfuryPending) return;
            BuildTimings.Step("TextureSizeCap");
            TextureSizeCapPass.Result result;
            try { result = TextureSizeCapPass.Run(ctx.AvatarRootObject, AvatarAnalysis.Build(ctx), state.TextureSizeCap, ReplacementRegistry.Register, state.FinalTextures); }
            catch (Exception e)
            {
                Debug.LogException(e);
                state.Report?.Add(null, "Texture size cap stopped (" + e.GetType().Name + "); textures kept their authored size.");
                return;
            }
            if (result.Textures == 0) return;
            string summary = "Capped " + OptimizationLog.Plural(result.Textures, "texture") + " at " + state.TextureSizeCap +
                " px in build copies (" + OptimizationLog.Plural(result.Materials, "material") +
                (result.MenuIcons > 0 ? ", " + OptimizationLog.Plural(result.MenuIcons, "menu icon") : "") + " repointed).";
            state.Report?.AddSavings("Textures", result.Bytes, "capped " + OptimizationLog.Plural(result.Textures, "texture") + " at " + state.TextureSizeCap + " px", result.Unmeasured);
            Announce(ctx, state, summary);
        }

        // A pass's one-line result: to the Console (with the avatar's name) and into the build report.
        private static void Announce(BuildContext ctx, SubstitutionState state, string summary, bool refresh = false)
        {
            Debug.Log("Arclight Optimizer: " + summary + " (" + ctx.AvatarRootObject.name + ")");
            state.Report?.Add(null, summary);
            if (refresh) state.Report?.Refresh(ctx.AvatarRootObject, state);
        }

        private static void CropTextures(BuildContext ctx)
        {
            var state = ctx.GetState<SubstitutionState>();
            if (!state.OptimizeMeshes || state.VrcfuryPending) return;
            // The cropper counts each crop here as it applies it, so a cancelled run still says what it left on the avatar.
            var crops = new TextureCropper.Result();
            bool cancelled = false;
            try
            {
                var analysis = AvatarAnalysis.Build(ctx);
                // As in the texture pass: inventory the committed clips (including NDMF marker clips) before
                // activating the virtual context, or the scan treats marker clips as opaque and keeps everything.
                var inventory = AnimationSnapshot.Analyze(ctx.AvatarRootObject, null, null);
                var controllers = ctx.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    var scan = TextureUsageScanner.Scan(ctx.AvatarRootObject, controllers, inventory);
                    // The pass rasterizes and PNG-decodes every component: give it the same progress window
                    // (and cancel button) the texture pass uses, so a cancel reaches the crop loop (fix 11).
                    using (var progress = new OptimizationProgress())
                    {
                        progress.Overall("Cropping textures", .9f);
                        TextureCropper.Run(ctx.AvatarRootObject, scan, analysis, ReplacementRegistry.Register, AutomaticTextureOptimizer.CacheFolder,
                            state.Coverage ??= new UvCoverageCache(), progress, crops);
                    }
                }
                // A user cancel is not a failure, and it does not undo anything: crops applied before it are complete
                // and stay on the avatar, so the pass goes on to report them. state.Cancelled is left alone: it says
                // the texture pass was cancelled before it applied its replacements, which is not what happened here.
                catch (OperationCanceledException) { cancelled = true; }
                finally { ControllerCommit.Preserving(ctx.AvatarRootObject, () => ctx.DeactivateExtensionContext<VirtualControllerContext>()); }
                bool cropped = crops.Textures > 0 || crops.Uniform > 0;
                if (cropped)
                {
                    analysis.RescanReferences();
                    string summary = (crops.Textures > 0 ? "Cropped " + OptimizationLog.Plural(crops.Textures, "texture") + " to the aligned part their meshes sample" : "") +
                        (crops.Textures > 0 && crops.Uniform > 0 ? "; " : "") +
                        (crops.Uniform > 0 ? "replaced " + OptimizationLog.Plural(crops.Uniform, "one-colour texture") + " with 4 x 4 copies" : "") +
                        " (" + (100 - 100 * crops.PixelsAfter / Math.Max(1, crops.PixelsBefore)) + "% fewer pixels), on " + OptimizationLog.Plural(crops.Materials, "material") + ".";
                    Announce(ctx, state, summary);
                }
                WhyNotLine(state, "material whose textures were not cropped", "materials whose textures were not cropped", crops.WhyNot);
                if (cancelled)
                {
                    string stopped = cropped
                        ? "Texture cropping was cancelled part-way: the " + OptimizationLog.Plural(crops.Textures + crops.Uniform, "texture already cropped or replaced stays", "textures already cropped or replaced stay") +
                          " as they are, and no further textures were cropped."
                        : "Texture cropping was cancelled before any texture was cropped.";
                    Debug.Log("Arclight Optimizer: " + stopped + " (" + ctx.AvatarRootObject.name + ")");
                    state.Report?.Add(null, stopped);
                }
                if (cropped || cancelled) state.Report?.Refresh(ctx.AvatarRootObject, state);
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
        // itself turns the conversion off for every clip a source on that object holds; other sources are unaffected. NDMF
        // marker clips stand for the platform's proxy motions, which hold no audio bindings. When the animation cannot be
        // read, nothing is converted (null).
        private static readonly HashSet<string> NeutralAudioProperties = new HashSet<string>(StringComparer.Ordinal)
            { "m_Volume", "m_Pitch", "m_Mute", "m_Enabled", "m_audioClip", "Gain" };

        private static HashSet<GameObject> AudioSettingsAnimated(AvatarAnalysis analysis)
        {
            bool Changes(EditorCurveBinding binding) => binding.type != null &&
                (typeof(AudioSource).IsAssignableFrom(binding.type) || AudioMonoConverter.IsVrcSpatialSource(binding.type)) &&
                !NeutralAudioProperties.Contains(binding.propertyName ?? "");
            if (!analysis.Complete) return null;
            // A binding whose path reaches no object drives nothing.
            return new HashSet<GameObject>(analysis.Bindings.Where(b => Changes(b.Curve) && b.Target is Component c && c).Select(b => ((Component)b.Target).gameObject));
        }

    }

    // What the latest build said that the Arclight Analyzer shows as advice: whether Arclight stood down because VRCFury had
    // not built the avatar, and why skinned meshes stayed apart. Written by the build passes, reset when a build starts and
    // before the Analyzer's hidden build, and read by the Analyzer right after that build; it changes nothing in a build.
    internal static class AnalyzerBuildNotes
    {
        internal static bool VrcfuryStoodDown;
        internal static Dictionary<string, List<string>> SkinnedWhyNot;
        internal static void Reset() { VrcfuryStoodDown = false; SkinnedWhyNot = null; }
    }

    internal sealed class SubstitutionState
    {
        public readonly Dictionary<Material, Material> Clones = new Dictionary<Material, Material>();
        public bool Applied;
        public bool Cancelled;
        public bool MergeMeshesAndAudio, OptimizeMeshes, OptimizeAudio;
        // Opt-in size cap for expression-menu icon build copies (0 = authored size).
        internal int MenuIconMaxSize;
        // Opt-in resolution cap for build copies of the avatar's textures (0 = authored size); PC only.
        internal int TextureSizeCap;
        // Opt-in Vorbis re-encode of uncompressed clips (see AudioCompressor).
        internal bool CompressAudio;
        internal float AudioQuality = .7f;
        // Set before Avatar Optimizer runs when the avatar carries it; then no Arclight pass acts.
        internal bool AvatarOptimizerConflict;
        // The component was present and enabled, and Prepare let the build go ahead.
        internal bool Enabled;

        // VRCFury has not built the avatar yet, so its animation is incomplete (see Prepare).
        internal bool VrcfuryPending;
        // The component's MMD Support setting: keep the shapes MMD dance worlds animate.
        internal bool KeepMmdShapes = true;
        internal bool UnifiedBounds;
        // Non-asset meshes the avatar had before any pass ran; they belong to the user's scene (see MeshReadWrite.Record).
        internal HashSet<Mesh> SourceMeshes;
        public int AppliedTextures;
        public readonly HashSet<Texture> MergedDuplicates = new HashSet<Texture>();
        // The texture pass's source -> final map, for passes that must follow a reference through a replacement.
        internal Dictionary<Texture, Texture> FinalTextures;
        // Kept readable sources and their Read/Write-off copies (ReadableTextureCopies); ReadableDropped holds those applied.
        internal Dictionary<Texture, Texture> ReadableDrops;
        internal readonly HashSet<Texture> ReadableDropped = new HashSet<Texture>();
        internal AnimationSnapshot SerializedAnimation;
        // The UV raster cache from the texture pass, reused by the later crop pass (content-keyed and LRU-bounded).
        internal UvCoverageCache Coverage;
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
                    // Excluded renderers keep their materials here too: Apply skipped them, so a clone only
                    // lands on their slots if they share a material with an optimized renderer.
                    if (Exclusions.Excluded(renderer)) continue;
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
                // A kept readable source uses its Read/Write-off copy: the same texture, so it counts as kept, not optimized.
                var readableDropped = new List<Texture>();
                if (state.ReadableDrops != null)
                    foreach (var group in scan.Groups)
                        if (!replacements.ContainsKey(group.Source) && state.ReadableDrops.TryGetValue(group.Source, out var copy) && copy)
                        {
                            replacements.Add(group.Source, copy);
                            readableDropped.Add(group.Source);
                        }
                state.FinalTextures = replacements;
                foreach (var pair in scan.Duplicates)
                {
                    if (IsUnsafeStandaloneSwap(scan.Groups.First(g => g.Source == pair.Value), scan.Animation))
                    {
                        report(pair.Key, OptimizationLog.DuplicateKeptPrefix + " animated swaps require the NDMF virtual controller context.");
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
                        // An excluded renderer keeps its materials, even where it holds an optimized texture in an unused slot.
                        if (Exclusions.Excluded(renderer)) continue;
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

                    // Object-swap curves must not aim an excluded renderer at a clone either. Curve paths are
                    // animator-object-relative: the root Animator's match is exact; each nested Animator's transform
                    // prefix is tried as well, so one bound relative to a nested Animator still hits the exclusion.
                    var excludedPaths = new HashSet<string>(buildRoot.GetComponentsInChildren<Renderer>(true)
                        .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && Exclusions.Excluded(r))
                        .Select(r => AnimationUtility.CalculateTransformPath(r.transform, buildRoot.transform)), StringComparer.Ordinal);
                    var animatorPrefixes = buildRoot.GetComponentsInChildren<Animator>(true)
                        .Where(a => a && a.transform != buildRoot.transform)
                        .Select(a => AnimationUtility.CalculateTransformPath(a.transform, buildRoot.transform) + "/")
                        .Distinct().ToList();
                    PrepareCurveEdits(scan.Animation, replacements, state, curveEdits, excludedPaths, animatorPrefixes);
                    foreach (var pair in state.Clones) register?.Invoke(pair.Key, pair.Value);
                    // NDMF's registry keys each replacement object once (TryRegisterReplacedObject refuses a second
                    // reference to it), so only the first copy of a source can be recorded as replaced by that source.
                    // Register one copy per source instead of relying on the refusals.
                    foreach (var pair in replacements.Where(p => scan.Duplicates.ContainsKey(p.Key))
                        .GroupBy(p => scan.Duplicates[p.Key]).Select(g => g.First()))
                        register?.Invoke(pair.Key, scan.Duplicates[pair.Key]);
                    foreach (var pair in replacements.Where(p => !scan.Duplicates.ContainsKey(p.Key)))
                        register?.Invoke(pair.Key, pair.Value);

                    foreach (var edit in curveEdits) edit.Clip.SetObjectCurve(edit.Binding, edit.Replacement);
                    foreach (var assignment in assignments) assignment.Renderer.sharedMaterials = assignment.Replacement;
                    state.AppliedTextures = optimized;
                    state.ReadableDropped.UnionWith(readableDropped);
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
            catch (Exception e) { report(config, OptimizationLog.SubstitutionAbortedPrefix + " " + e.Message); }
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
            SubstitutionState state, List<CurveEdit> edits, HashSet<string> excludedPaths, List<string> animatorPrefixes)
        {
            if (animation == null || !animation.CanRewriteObjectCurves) return;
            foreach (var group in animation.ObjectBindings.GroupBy(b => b.Clip))
            {
                var clip = group.Key;
                foreach (var binding in group.Select(b => b.Binding).Distinct())
                {
                    // Excluded by exact root-Animator path, or by any nested Animator's prefix (over-approximating
                    // which only ever declines a rewrite — the excluded renderer keeps its original materials).
                    if (excludedPaths.Contains(binding.path) || animatorPrefixes.Any(p => excludedPaths.Contains(p + binding.path))) continue;
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
