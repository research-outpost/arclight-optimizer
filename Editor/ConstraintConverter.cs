using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // VRChat converts every Unity constraint on an avatar into a VRChat constraint when the avatar loads, and while a clip
    // animates the Unity one it copies the animated values across before the constraints run, every frame. Converting with
    // the SDK's own routine (AvatarDynamicsSetup.DoConvertUnityConstraints, which creates the substitute through
    // VRCConstraintManager.TryCreateSubstituteConstraint) gives the constraints players already get, without the load-time
    // conversion or the per-frame copy. Curves on the Unity constraint move to the SDK's substitute bindings.
    // Left alone (VRChat still converts them when the avatar loads):
    //  - constraints under an Arclight Exclude component or that another component references;
    //  - objects where an animated property has no substitute binding or is an object reference, or that would end up with
    //    two VRChat constraints of one type (the SDK warns that animating those is unreliable);
    //  - every constraint when the animation could not be read, the avatar has legacy Animation components (their clips
    //    are not rewritten here) or an animation preview is active (the SDK then converts nothing);
    //  - constraints the SDK could not substitute (their curves stay as they are).
    // The SDK records an undo step for each removed Unity constraint; on the build copy that is harmless.
    internal static class ConstraintConverter
    {
        private static readonly Type Setup = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("VRC.SDK3.Avatars.AvatarDynamicsSetup", false)).FirstOrDefault(t => t != null);
        private static readonly MethodInfo Convert = Setup?.GetMethod("DoConvertUnityConstraints", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo Substitute = Setup?.GetMethod("TryGetSubstituteAnimationBinding", BindingFlags.Public | BindingFlags.Static);

        internal sealed class Result { public int Converted, Curves; public readonly List<string> Kept = new List<string>(); }

        internal static Result Run(GameObject root, AvatarAnalysis analysis, AnimationRewriter rewriter)
        {
            var result = new Result();
            if (Convert == null || Substitute == null || !analysis.Complete || AnimationMode.InAnimationMode() || root.GetComponentsInChildren<Animation>(true).Any()) return result;
            var candidates = root.GetComponentsInChildren<IConstraint>(true).OfType<Component>().Where(c => c && !Exclusions.Excluded(c)).ToList();
            if (candidates.Count == 0) return result;

            // Every animated property of a candidate, mapped to its substitute; an object with an unmapped one, or with an
            // animated object reference (World Up Object, a source's transform), stays.
            var map = new Dictionary<(GameObject Host, Type Unity, string Property), (Type Type, string Property)>();
            var blocked = new HashSet<GameObject>();
            foreach (var constraint in candidates)
            {
                if (analysis.ReferencesTo(constraint).Any(c => c != constraint)) { blocked.Add(constraint.gameObject); continue; }
                foreach (var binding in analysis.BindingsOn(constraint))
                {
                    // A path naming several objects (siblings with one name) would move its curve for all of them or none.
                    var targets = AvatarAnalysis.Resolve(binding.Owner, binding.Curve.path).ToList();
                    if (targets.Count != 1) { blocked.Add(constraint.gameObject); foreach (var t in targets) blocked.Add(t.gameObject); continue; }
                    var args = new object[] { constraint.GetType(), binding.Curve.propertyName, null, null, null };
                    if (!binding.Curve.isPPtrCurve && Substitute.Invoke(null, args) is bool ok && ok)
                        map[(constraint.gameObject, constraint.GetType(), binding.Curve.propertyName)] = ((Type)args[2], (string)args[3]);
                    else blocked.Add(constraint.gameObject);
                }
            }
            // Two Unity constraints of one kind on an object, or one beside a VRChat constraint of its kind, become two VRChat
            // constraints of one type.
            foreach (var group in candidates.GroupBy(c => c.gameObject))
                if (group.GroupBy(c => c.GetType()).Any(g => g.Count() > 1 ||
                        group.Key.GetComponents<Component>().Any(o => o && o.GetType().Name == "VRC" + g.Key.Name))) blocked.Add(group.Key);
            var convert = candidates.Where(c => !blocked.Contains(c.gameObject)).ToList();
            result.Kept.AddRange(candidates.Where(c => blocked.Contains(c.gameObject)).Select(c => c.name + " (" + c.GetType().Name + ")"));
            if (convert.Count == 0) return result;

            // The SDK converts nothing while an animation preview is active and skips a constraint it cannot substitute, so
            // curves move only for the constraints it actually replaced.
            var hosts = convert.Select(c => (c.gameObject, Type: c.GetType(), Component: c)).ToList();
            Convert.Invoke(null, new object[] { convert.Cast<IConstraint>().ToArray(), null, false });
            var converted = new HashSet<(GameObject, Type)>(hosts.Where(h => !h.Component).Select(h => (h.gameObject, h.Type)));
            result.Converted = converted.Count;
            result.Kept.AddRange(hosts.Where(h => h.Component).Select(h => h.Component.name + " (" + h.Type.Name + ")"));
            if (converted.Count == 0) return result;
            rewriter.Rewrite((owner, binding) =>
            {
                if (binding.isPPtrCurve || binding.type == null || !typeof(IConstraint).IsAssignableFrom(binding.type)) return null;
                foreach (var target in AvatarAnalysis.Resolve(owner, binding.path))
                    if (converted.Contains((target.gameObject, binding.type)) && map.TryGetValue((target.gameObject, binding.type, binding.propertyName), out var to))
                    {
                        result.Curves++;
                        return EditorCurveBinding.FloatCurve(binding.path, to.Type, to.Property);
                    }
                return null;
            });
            return result;
        }
    }
}
