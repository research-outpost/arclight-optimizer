using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // d4rkAvatarOptimizer is a VRChat SDK preprocess hook, not an NDMF plugin: it runs after NDMF finishes and
    // rewrites materials and clips, which can make previously different ones identical. So duplicates are merged
    // here, after it. The component is already gone by then (NDMF removes it), so the NDMF pass records a request.
    // d4rk concatenates the parts it merges into one new mesh and never welds or drops triangles, so the pixel-preserving mesh
    // passes (submesh slots, degenerate triangles, unused and duplicate vertices, vertex streams, unread blend shape deltas) run
    // again here on its final meshes, only on meshes in this build's own containers.
    internal sealed class PostOptimizerMerge : IVRCSDKPreprocessAvatarCallback
    {
        // After d4rk (-15 with Modular Avatar, -1025 without) and NDMF's Optimizing hook.
        public int callbackOrder => -10;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            bool merge = MergeRequests.Take(avatarGameObject), reduce = KeyReductionRequests.Take(avatarGameObject), streams = StreamRequests.Take(avatarGameObject);
            if (!merge && !reduce && !streams) return true;
            var late = new System.Collections.Generic.List<string>();
            void Note(string line) { Debug.Log("Arclight Optimizer: " + line + " (" + avatarGameObject.name + ")"); late.Add(line); }
            try
            {
                var bindings = VRChatPlatformAnimatorBindings.Instance;
                var controllers = bindings.GetInnateControllers(avatarGameObject).Select(entry => entry.Item2)
                    .Where(controller => controller).ToArray();
                System.Func<Motion, bool> special = motion => bindings.IsSpecialMotion(motion);
                var owned = controllers.OfType<AnimatorController>().Where(IsBuildOwned).ToArray();
                if (merge)
                {
                    // Materials first: clips that swap between identical materials then become identical themselves.
                    int materials = DuplicateMaterialMerger.Merge(avatarGameObject, controllers, special, IsBuildOwned);
                    // d4rk's merged-toggle layer joins a lower Direct-tree layer when that is exact (see D4rkLayerFold).
                    foreach (var controller in owned)
                        if (D4rkLayerFold.Run(controller, controllers, IsBuildOwned) is string into)
                            Note($"Moved d4rk's merged layer into the \"{into}\" blend tree in {controller.name} (one animator layer fewer).");
                    int clips = AnimationClipDeduplicator.MergeCommitted(owned, special, IsBuildOwned);
                    if (materials + clips > 0 || owned.Length < controllers.Length)
                        Note($"Merged {materials} duplicate material(s) and {clips} duplicate animation clip(s) d4rk left" +
                            (owned.Length < controllers.Length ? $"; {controllers.Length - owned.Length} controller(s) skipped as source assets." : "."));
                }
                // VRCFury that has not built yet can still add objects, readers and clips: nothing that proves from
                // the animation is sound then. A built one has finished, and one that will not build this run leaves
                // the authored animation final, so the avatar is safe to clean either way.
                bool animationFinal = !RivalState.Of(avatarGameObject).AnimationIncomplete;
                if (reduce && !animationFinal)
                    Note("Clip cleanup skipped: VRCFury had not built this avatar when the late stage ran.");
                else if (reduce)
                {
                    // After merging, so each kept clip is reduced once.
                    var clips = owned.SelectMany(AnimationClipDeduplicator.AllClips).Where(clip => clip && !special(clip) && IsBuildOwned(clip)).ToList();
                    // Clips another Animator on the avatar also plays read their paths from that Animator's object.
                    var elsewhere = new System.Collections.Generic.HashSet<AnimationClip>(avatarGameObject.GetComponentsInChildren<Animator>(true)
                        .Where(a => a && a.gameObject != avatarGameObject && a.runtimeAnimatorController)
                        .SelectMany(a => a.runtimeAnimatorController.animationClips));
                    // Every renderer any animation on the avatar swaps a mesh onto, from any controller, Animator or legacy clip.
                    var meshSwapped = new System.Collections.Generic.HashSet<Transform>();
                    // Same path under the same owner is walked once; Swaps runs over every clip binding on the avatar.
                    var resolvedByOwnerPath = new System.Collections.Generic.Dictionary<(UnityEngine.Transform, string), System.Collections.Generic.List<UnityEngine.Transform>>();
                    void Swaps(Transform owner, System.Collections.Generic.IEnumerable<AnimationClip> source)
                    {
                        foreach (var clip in source.Where(c => c))
                            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(b => b.propertyName == "m_Mesh"))
                            {
                                if (!resolvedByOwnerPath.TryGetValue((owner, binding.path), out var found))
                                    resolvedByOwnerPath[(owner, binding.path)] = found = AvatarAnalysis.Resolve(owner, binding.path).ToList();
                                meshSwapped.UnionWith(found);
                            }
                    }
                    Swaps(avatarGameObject.transform, controllers.SelectMany(c => c.animationClips));
                    foreach (var animator in avatarGameObject.GetComponentsInChildren<Animator>(true).Where(a => a && a.runtimeAnimatorController))
                        Swaps(animator.transform, animator.runtimeAnimatorController.animationClips);
                    foreach (var animation in avatarGameObject.GetComponentsInChildren<Animation>(true))
                        Swaps(animation.transform, AnimationUtility.GetAnimationClips(animation.gameObject));
                    int curves = AnimationKeyReducer.RemoveDead(clips, avatarGameObject.transform, elsewhere, meshSwapped);
                    int keys = AnimationKeyReducer.Reduce(clips);
                    int events = AnimationKeyReducer.RemoveEvents(clips, avatarGameObject);
                    if (keys > 0 || curves > 0 || events > 0)
                        Note($"Removed {curves} animation curve(s) that drive nothing, {keys} redundant animation key(s) and {events} animation event(s) no component can receive.");
                    // Clips that differed only in those keys, curves or events are identical now.
                    if (merge && (keys > 0 || curves > 0 || events > 0) && AnimationClipDeduplicator.MergeCommitted(owned, special, IsBuildOwned) is int same && same > 0)
                        Note($"Merged {same} animation clip(s) that became identical once redundant keys, curves or events were removed.");
                    int lateLayers = AnimatorLayerMerger.RemoveInertWeightZeroCommitted(owned);
                    if (lateLayers > 0)
                        Note($"Removed {lateLayers} never-raised weight-0 animator layer(s) left after the build's optimizers.");
                }
                if (streams) foreach (string cleaned in CleanAfterD4rk(avatarGameObject, bindings)) Note(cleaned);
                // d4rk has finished reading and writing meshes: its final meshes and NDMF's can drop their CPU copy now. Only meshes in
                // this build's own asset containers (never a scene-stored or source mesh), and none an excluded object could hold.
                if (streams && !Exclusions.Any)
                {
                    int unreadable = MeshReadWrite.Run(avatarGameObject, afterD4rk: true, eligible: mesh => AssetDatabase.Contains(mesh) && IsBuildOwned(mesh));
                    if (unreadable > 0) Note("Turned off Read/Write on " + unreadable + " mesh(es) after d4rk's merge, so the avatar does not keep a second copy of them in memory.");
                }
            }
            catch (System.Exception e)
            {
                // Optimization only: never fail the upload.
                Debug.LogWarning("Arclight Optimizer: post-d4rk optimization stopped: " + e.Message);
                late.Add("Stopped: " + e.Message + " Changes made before it stay; the upload continues.");
            }
            OptimizationLog.AppendLate(avatarGameObject, late);
            return true;
        }

        // The mesh passes on d4rk's final meshes, with an analysis of the final controllers (d4rk rewrites clips and materials).
        // Objects d4rk merged lose their identity, so an avatar with excluded objects keeps its meshes as d4rk left them.
        // Material slots an animation swaps are not renumbered here (no rewriter for the committed clips), so those renderers keep
        // their slots.
        private static System.Collections.Generic.List<string> CleanAfterD4rk(GameObject avatar, VRChatPlatformAnimatorBindings bindings)
        {
            var notes = new System.Collections.Generic.List<string>();
            if (Exclusions.Any) { notes.Add("Vertex streams and meshes were left as they are after d4rk's merge: the avatar has excluded objects, which d4rk's merge can hide."); return notes; }
            var clones = new CloneContext(bindings);
            var entries = bindings.GetInnateControllers(avatar).Where(e => e.Item2)
                .Select(e => new System.Collections.Generic.KeyValuePair<object, VirtualAnimatorController>(e.Item1, clones.Clone(e.Item2)))
                .Concat(avatar.GetComponentsInChildren<Animator>(true).Where(a => a && a.gameObject != avatar && a.runtimeAnimatorController)
                    .Select(a => new System.Collections.Generic.KeyValuePair<object, VirtualAnimatorController>(a, clones.Clone(a.runtimeAnimatorController))))
                .ToList();
            var analysis = AvatarAnalysis.Build(avatar, entries);
            // N5 late: d4rk's merge leaves groups Arclight could not see before it ran. Run the skinned merger again
            // with no animation rewriter for renderers it refuses, and only where the host mesh lives in a build-owned
            // asset (the merged mesh is saved beside it). Animated renderers keep their existing handling.
            var realControllers = bindings.GetInnateControllers(avatar).Select(e => e.Item2).OfType<AnimatorController>()
                .Concat(avatar.GetComponentsInChildren<Animator>(true)
                    .Where(a => a && a.gameObject != avatar && a.runtimeAnimatorController)
                    .Select(a => a.runtimeAnimatorController).OfType<AnimatorController>())
                .Where(c => c && AssetDatabase.Contains(c) && BuildOwnership.IsBuildOwned(c)).Distinct().ToArray();
            var lateClips = realControllers.SelectMany(AnimationClipDeduplicator.AllClips)
                .Where(c => c && AssetDatabase.Contains(c) && BuildOwnership.IsBuildOwned(c))
                .Select(c => (avatar.transform, c)).ToList();
            var lateMasks = realControllers.SelectMany(c => c.layers).Select(l => l.avatarMask)
                .Where(m => m && AssetDatabase.Contains(m) && BuildOwnership.IsBuildOwned(m))
                .Distinct().Select(m => (avatar.transform, m)).ToList();
            var lateRewriter = AnimationRewriter.For(lateClips, lateMasks);
            var lateMerge = SkinnedMeshMerger.Run(analysis, (a, b) => { }, lateRewriter,
                (source, added) => AssetDatabase.AddObjectToAsset(added, AssetDatabase.GetAssetPath(source)),
                mesh => mesh && AssetDatabase.Contains(mesh) && BuildOwnership.IsBuildOwned(mesh));
            if (lateMerge.Merged > 0)
            {
                analysis.RescanReferences();
                notes.Add($"Merged {lateMerge.Merged} skinned mesh(es) that survived the build's optimizers.");
            }
            // NDMF has already saved the build's assets, and the SDK saves the avatar as a prefab for upload, so a mesh made here
            // must be saved too or the upload loses it: each copy goes into its source mesh's asset. Only meshes in this build's own
            // containers (NDMF's generated assets, d4rk's TrashBin) qualify, so no source asset (a .asset or an imported model) is
            // ever written to; any other mesh is left alone.
            System.Action<Object, Object> save = (a, b) => AssetDatabase.AddObjectToAsset(b, AssetDatabase.GetAssetPath(a));
            System.Func<Mesh, bool> owned = mesh => AssetDatabase.Contains(mesh) && IsBuildOwned(mesh);
            // Slots first, then triangles (which can leave vertices unused), then vertices, streams, and the deltas of removed streams.
            var slots = SubmeshCleaner.Run(analysis, save, null, owned);
            var flat = DegenerateTriangleRemover.Run(analysis, save, owned);
            var vertices = UnusedVertexRemover.Run(analysis, save, owned);
            if (slots.Removed + slots.Merged + flat.Triangles + vertices.Vertices > 0)
                notes.Add($"After d4rk's merge: removed {slots.Removed} empty or undrawn submesh(es), joined {slots.Merged} submesh(es) with an identical material, " +
                    $"removed {flat.Triangles} triangle(s) that can never cover a pixel and {vertices.Vertices} unused or duplicate vertex(es).");
            var result = VertexStreamStripper.Run(analysis, save, afterD4rk: true, eligible: owned);
            if (result.Meshes > 0) notes.Add($"Removed vertex streams no material reads from {result.Meshes} mesh(es) after d4rk's merge ({result.Bytes / 1024:N0} KiB).");
            var deltas = BlendShapeDeltaStripper.Run(analysis, save, afterD4rk: true, eligible: owned);
            if (deltas.Frames > 0) notes.Add($"Removed normal and tangent deltas the mesh has no channel for from {deltas.Frames} blend shape frame(s) after d4rk's merge.");
            return notes;
        }

        // Source assets must never be edited. Objects made during this build are in-memory or live in generated
        // asset containers (NDMF, a late optimizer); the rule is shared with every other pass (see BuildOwnership).
        private static bool IsBuildOwned(Object asset) => BuildOwnership.IsBuildOwned(asset);
    }
}
