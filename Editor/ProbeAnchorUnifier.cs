using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Gives every mesh renderer of the avatar one anchor override: the Chest bone, else Spine, else Hips. Unity samples a
    // renderer's light probes and reflection probe at its anchor, or at its bounds centre when it has none, so meshes with
    // different anchors can be lit differently where they meet (a seam or brightness step between face and body). One
    // anchor lights the whole avatar from one point, and meshes that differed only by their anchor can then merge.
    // It changes lighting wherever an anchor moves (an accepted difference). Excluded objects, such as world-dropped
    // props, keep their anchor.
    internal static class ProbeAnchorUnifier
    {
        internal sealed class Result { public int Changed; public string Anchor; }

        internal static Result Run(GameObject root)
        {
            var result = new Result();
            var animator = root.GetComponent<Animator>();
            if (!animator || !animator.isHuman) return result;
            var anchor = new[] { HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips }.Select(animator.GetBoneTransform).FirstOrDefault(t => t);
            if (!anchor) return result;
            result.Anchor = anchor.name;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer) || renderer.probeAnchor == anchor || Exclusions.Excluded(renderer)) continue;
                renderer.probeAnchor = anchor;
                result.Changed++;
            }
            return result;
        }
    }
}
