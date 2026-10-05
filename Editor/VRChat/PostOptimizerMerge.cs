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
    internal sealed class PostOptimizerMerge : IVRCSDKPreprocessAvatarCallback
    {
        // After d4rk (-15 with Modular Avatar, -1025 without) and NDMF's Optimizing hook.
        public int callbackOrder => -10;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            bool merge = MergeRequests.Take(avatarGameObject), reduce = KeyReductionRequests.Take(avatarGameObject), streams = StreamRequests.Take(avatarGameObject);
            if (!merge && !reduce && !streams) return true;
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
                            Debug.Log($"Arclight Optimizer: moved d4rk's merged layer into the \"{into}\" blend tree in {controller.name} (one animator layer fewer) on {avatarGameObject.name}.");
                    int clips = AnimationClipDeduplicator.MergeCommitted(owned, special);
                    Debug.Log($"Arclight Optimizer: merged {materials} duplicate material(s) and {clips} duplicate animation clip(s) on {avatarGameObject.name}" +
                        (owned.Length < controllers.Length ? $"; {controllers.Length - owned.Length} controller(s) skipped as source assets." : "."));
                }
                if (reduce)
                {
                    // After merging, so each kept clip is reduced once.
                    var clips = owned.SelectMany(AnimationClipDeduplicator.AllClips).Where(clip => clip && !special(clip) && IsBuildOwned(clip)).ToList();
                    // Clips another Animator on the avatar also plays read their paths from that Animator's object.
                    var elsewhere = new System.Collections.Generic.HashSet<AnimationClip>(avatarGameObject.GetComponentsInChildren<Animator>(true)
                        .Where(a => a && a.gameObject != avatarGameObject && a.runtimeAnimatorController)
                        .SelectMany(a => a.runtimeAnimatorController.animationClips));
                    // Every renderer any animation on the avatar swaps a mesh onto, from any controller, Animator or legacy clip.
                    var meshSwapped = new System.Collections.Generic.HashSet<Transform>();
                    void Swaps(Transform owner, System.Collections.Generic.IEnumerable<AnimationClip> source)
                    {
                        foreach (var clip in source.Where(c => c))
                            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(b => b.propertyName == "m_Mesh"))
                                meshSwapped.UnionWith(AvatarAnalysis.Resolve(owner, binding.path));
                    }
                    Swaps(avatarGameObject.transform, controllers.SelectMany(c => c.animationClips));
                    foreach (var animator in avatarGameObject.GetComponentsInChildren<Animator>(true).Where(a => a && a.runtimeAnimatorController))
                        Swaps(animator.transform, animator.runtimeAnimatorController.animationClips);
                    foreach (var animation in avatarGameObject.GetComponentsInChildren<Animation>(true))
                        Swaps(animation.transform, AnimationUtility.GetAnimationClips(animation.gameObject));
                    int curves = AnimationKeyReducer.RemoveDead(clips, avatarGameObject.transform, elsewhere, meshSwapped);
                    int keys = AnimationKeyReducer.Reduce(clips);
                    if (keys > 0 || curves > 0)
                        Debug.Log($"Arclight Optimizer: removed {curves} animation curve(s) that drive nothing and {keys} redundant animation key(s) on {avatarGameObject.name}.");
                }
                if (streams) StripAfterD4rk(avatarGameObject, bindings);
            }
            catch (System.Exception e)
            {
                // Optimization only: never fail the upload.
                Debug.LogWarning("Arclight Optimizer: duplicate merge and key reduction skipped: " + e.Message);
            }
            return true;
        }

        // The vertex stream pass on d4rk's final meshes, with an analysis of the final controllers (d4rk rewrites clips and
        // materials). Objects d4rk merged lose their identity, so an avatar with excluded objects keeps its streams.
        private static void StripAfterD4rk(GameObject avatar, VRChatPlatformAnimatorBindings bindings)
        {
            if (Exclusions.Any) return;
            var clones = new CloneContext(bindings);
            var entries = bindings.GetInnateControllers(avatar).Where(e => e.Item2)
                .Select(e => new System.Collections.Generic.KeyValuePair<object, VirtualAnimatorController>(e.Item1, clones.Clone(e.Item2)))
                .Concat(avatar.GetComponentsInChildren<Animator>(true).Where(a => a && a.gameObject != avatar && a.runtimeAnimatorController)
                    .Select(a => new System.Collections.Generic.KeyValuePair<object, VirtualAnimatorController>(a, clones.Clone(a.runtimeAnimatorController))))
                .ToList();
            var analysis = AvatarAnalysis.Build(avatar, entries);
            var result = VertexStreamStripper.Run(analysis, (a, b) => { }, afterD4rk: true);
            if (result.Meshes > 0)
                Debug.Log($"Arclight Optimizer: removed vertex streams no material reads from {result.Meshes} mesh(es) after d4rk's merge ({result.Bytes / 1024:N0} KiB) on {avatar.name}.");
        }

        // Source assets must never be edited. Objects made during this build are in-memory or live in generated
        // asset containers (NDMF, d4rkAvatarOptimizer).
        private static bool IsBuildOwned(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return path.Length == 0 || path.StartsWith("Packages/nadena.dev.ndmf/__Generated/", System.StringComparison.Ordinal) ||
                path.IndexOf("/TrashBin/", System.StringComparison.Ordinal) >= 0;
        }
    }
}
