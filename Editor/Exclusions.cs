using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Objects every pass leaves alone: those in the Arclight Optimizer component's Exclude list, those under an Arclight
    // Exclude component (kept for avatars set up before the list existed), and everything under either. Modular Avatar
    // and VRCFury move an outfit's bones into the avatar's armature before Arclight runs, so everything under each
    // excluded object is also recorded in the Resolving phase, before they act; an object counts as excluded when it or
    // a parent was recorded then, or is listed.
    internal static class Exclusions
    {
        private static readonly HashSet<Transform> Recorded = new HashSet<Transform>();
        private static readonly HashSet<Transform> Listed = new HashSet<Transform>();

        // One avatar builds at a time, so each build replaces the previous avatar's set.
        internal static void Record(GameObject root)
        {
            Recorded.Clear();
            Listed.Clear();
            var config = root.GetComponent<AvatarTextureOptimizer>();
            if (config && config.exclude != null) Listed.UnionWith(config.exclude.Where(t => t));
            foreach (var t in Listed.ToList()) Recorded.UnionWith(t.GetComponentsInChildren<Transform>(true));
            foreach (var exclude in root.GetComponentsInChildren<ArclightExclude>(true))
                Recorded.UnionWith(exclude.GetComponentsInChildren<Transform>(true));
        }

        internal static bool Excluded(Component component) => component && Excluded(component.transform);
        internal static bool Excluded(GameObject gameObject) => gameObject && Excluded(gameObject.transform);

        private static bool Excluded(Transform transform)
        {
            if (transform.GetComponentInParent<ArclightExclude>(true)) return true;
            for (var t = transform; t; t = t.parent)
                if (Recorded.Contains(t) || Listed.Contains(t)) return true;
            return false;
        }
    }
}
