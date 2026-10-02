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
            bool merge = MergeRequests.Take(avatarGameObject), reduce = KeyReductionRequests.Take(avatarGameObject);
            if (!merge && !reduce) return true;
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
                    int clips = AnimationClipDeduplicator.MergeCommitted(owned, special);
                    Debug.Log($"Arclight Optimizer: merged {materials} duplicate material(s) and {clips} duplicate animation clip(s) on {avatarGameObject.name}" +
                        (owned.Length < controllers.Length ? $"; {controllers.Length - owned.Length} controller(s) skipped as source assets." : "."));
                }
                if (reduce)
                {
                    // After merging, so each kept clip is reduced once.
                    var clips = owned.SelectMany(AnimationClipDeduplicator.AllClips).Where(clip => clip && !special(clip) && IsBuildOwned(clip));
                    int keys = AnimationKeyReducer.Reduce(clips);
                    if (keys > 0) Debug.Log($"Arclight Optimizer: removed {keys} redundant animation key(s) on {avatarGameObject.name}.");
                }
            }
            catch (System.Exception e)
            {
                // Optimization only: never fail the upload.
                Debug.LogWarning("Arclight Optimizer: duplicate merge and key reduction skipped: " + e.Message);
            }
            return true;
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
