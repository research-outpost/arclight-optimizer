using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ResearchOutpost.AvatarTextureOptimizer.Editor
{
    // Objects every pass leaves alone: those in the Arclight Optimizer component's Exclude Objects list, those under an Arclight
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
            // The sets are static and die with a domain reload. Mid-build that would silently turn "excluded" into
            // "not excluded", so the verdict is mirrored per avatar in SessionState (keyed by scene path, which
            // survives a reload and cannot be inherited by a later avatar).
            SessionState.SetString(Key(root), Recorded.Count > 0 || Listed.Count > 0 ? "1" : "");
        }

        // Whether this avatar has any excluded object. Mirrored, so a late pass whose sets were emptied by a reload
        // stands down rather than touching objects the Resolving phase excluded.
        internal static bool Any(GameObject root) =>
            Recorded.Count > 0 || Listed.Count > 0 || SessionState.GetString(Key(root), "") == "1";

        private static string Key(GameObject root) => "Arclight.Optimizer.Excluded." + LateBuildRequests.ScenePath(root);

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
