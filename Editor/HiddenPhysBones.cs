using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // PhysBones that only move bones drawn by renderers inside one toggled object stop while that object is hidden.
    // The object's m_IsActive curves are copied onto the PhysBone's m_Enabled in the same clips, and the PhysBone
    // starts in the object's starting state, so the two always hold the same value (Write Defaults on or off, in any
    // layer). While hidden, nothing the chain moves is drawn; when shown again the chain restarts from its rest pose
    // instead of the pose it would have reached (an accepted difference). A PhysBone qualifies only when:
    //  - it reports no parameter, is enabled, is the only PhysBone on its GameObject, is not animated itself, and does
    //    not sit inside the object (where hiding the object already stops it);
    //  - every transform it moves (below its root) carries no component but it, is not animated except for its own
    //    active toggle, and is referenced only by renderers inside the object and by the PhysBone;
    //  - the object (the lowest toggled ancestor of all those renderers) is toggled only by controller clips played from
    //    an object above the PhysBone, by keys of 0 or 1 with flat or stepped tangents; both
    //    paths must name exactly one object.
    // With no such object, a chain drawn by one renderer whose own enabled flag is animated the same way follows that flag.
    // Outfit bones are usually merged into the main armature, which is why their PhysBones keep running otherwise.
    internal static class HiddenPhysBones
    {
        internal sealed class Result { public int PhysBones, Objects; }

        internal static Result Run(AvatarAnalysis analysis, AnimationRewriter rewriter)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root.transform;
            var follow = new Dictionary<Object, List<Component>>();
            foreach (var physBone in root.GetComponentsInChildren<Component>(true).Where(c => c && c.GetType().Name == "VRCPhysBone" && !Exclusions.Excluded(c)))
            {
                var toggle = Toggle(physBone, analysis);
                if (toggle == null) continue;
                if (!follow.TryGetValue(toggle, out var list)) follow[toggle] = list = new List<Component>();
                list.Add(physBone);
            }
            if (follow.Count == 0) return result;

            rewriter.Copy((owner, binding) =>
            {
                bool active = binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive";
                bool enabled = typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName == "m_Enabled";
                if (!active && !enabled) return null;
                var targets = AvatarAnalysis.Resolve(owner, binding.path).ToList();
                if (targets.Count != 1) return null;
                Object source = active ? (Object)targets[0].gameObject : targets[0].GetComponent(binding.type);
                if (!source || !follow.TryGetValue(source, out var physBones)) return null;
                return physBones.Select(p => EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(p.transform, owner), p.GetType(), "m_Enabled"));
            });
            foreach (var entry in follow)
                foreach (var physBone in entry.Value)
                    ((Behaviour)physBone).enabled = entry.Key is GameObject g ? g.activeSelf : ((Renderer)entry.Key).enabled;
            result.Objects = follow.Count;
            result.PhysBones = follow.Values.Sum(l => l.Count);
            return result;
        }

        // The object whose activeness the PhysBone can follow, or the one renderer whose own enabled flag it can follow, or null.
        private static Object Toggle(Component physBone, AvatarAnalysis analysis)
        {
            if (!(physBone is Behaviour behaviour) || !behaviour.enabled || analysis.IsAnimated(physBone)) return null;
            if (physBone.GetComponents(physBone.GetType()).Length != 1) return null;
            using (var serialized = new SerializedObject(physBone))
                if (!string.IsNullOrEmpty(serialized.FindProperty("parameter")?.stringValue)) return null;

            var start = UnusedObjectRemover.PhysBoneRoot(physBone);
            var renderers = new HashSet<Renderer>();
            foreach (var t in start.GetComponentsInChildren<Transform>(true))
            {
                if (t == start && !UnusedObjectRemover.RootMoves(physBone)) continue;
                if (t.GetComponents<Component>().Any(c => c && !(c is Transform) && c != physBone)) return null;
                // A bone's own active toggle (outfit bones merged into the armature keep the outfit's toggle) moves
                // nothing and draws nothing by itself, so only its observers matter.
                if (analysis.IsAnimated(t) || analysis.IsAnimated(t.gameObject, p => p != "m_IsActive")) return null;
                foreach (var c in analysis.ReferencesTo(t).Concat(analysis.ReferencesTo(t.gameObject).Where(c => c.GetType().Name != "VRCAvatarDescriptor"))) // Network IDs only name it.
                {
                    if (c == physBone) continue;
                    if (!(c is Renderer renderer)) return null;
                    renderers.Add(renderer);
                }
            }
            if (renderers.Count == 0) return null; // Nothing drawn: the bone cleaner's case.

            var root = analysis.Root.transform;
            for (var t = renderers.First().transform; t && t != root; t = t.parent)
            {
                if (!renderers.All(r => r.transform.IsChildOf(t)) || !analysis.IsAnimated(t.gameObject, p => p == "m_IsActive")) continue;
                if (physBone.transform.IsChildOf(t)) return null;
                if (!Copyable(analysis.BindingsOn(t.gameObject, p => p == "m_IsActive"), physBone)) return null;
                return t.gameObject;
            }
            // No toggled object, but the one renderer drawing the chain is switched on and off itself (hand-made toggles often
            // animate the renderer instead of its object): while it is off nothing the chain moves is drawn either.
            if (renderers.Count == 1 && analysis.IsAnimated(renderers.First(), p => p == "m_Enabled") &&
                Copyable(analysis.BindingsOn(renderers.First(), p => p == "m_Enabled"), physBone)) return renderers.First();
            return null;
        }

        // Every toggle curve must reach the copy unchanged: a unique path for the object (the copy skips ambiguous ones) and
        // for the PhysBone (avatar masks filter neither curve; see MaskFilteringTests), and keys of 0 or 1 with flat or
        // stepped tangents, so the value never leaves 0 to 1 (a finite slope can overshoot; values in between switch both at
        // the same point, as a test checks).
        private static bool Copyable(IEnumerable<AvatarAnalysis.Binding> bindings, Component physBone) =>
            !bindings.Any(b => b.Legacy || b.FloatCurve == null || !physBone.transform.IsChildOf(b.Owner) ||
                AvatarAnalysis.Resolve(b.Owner, b.Curve.path).Count() != 1 ||
                AvatarAnalysis.Resolve(b.Owner, AnimationUtility.CalculateTransformPath(physBone.transform, b.Owner)).Count() != 1 ||
                b.FloatCurve.keys.Length == 0 || b.FloatCurve.keys.Any(k => k.value != 0 && k.value != 1 ||
                    !AnimatorLayerMerger.Flat(k.inTangent) || !AnimatorLayerMerger.Flat(k.outTangent)));
    }
}
