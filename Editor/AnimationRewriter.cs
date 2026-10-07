using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Moves animation curves from one binding to another in every clip of the avatar. The map receives the object a
    // clip's paths start from and a binding, and returns the binding to move the curve to, or null to leave it.
    internal abstract class AnimationRewriter
    {
        internal abstract void Rewrite(Func<Transform, EditorCurveBinding, EditorCurveBinding?> map);

        // Replaces object-curve keyframe values (for example materials) in every clip; the map returns null to keep a value.
        internal abstract void RewriteValues(Func<UnityEngine.Object, UnityEngine.Object> map);

        // Copies float curves onto further bindings in the same clip; the map returns the bindings to copy to (or null).
        internal abstract void Copy(Func<Transform, EditorCurveBinding, IEnumerable<EditorCurveBinding>> map);

        // Transform paths the layers' Avatar Masks name, with the transform they are relative to.
        internal abstract IEnumerable<(Transform Owner, string Path)> MaskPaths();

        // Renames Avatar Mask transform entries; the map returns the new path, or null to drop the entry.
        internal abstract void RewriteMasks(Func<Transform, string, string> map);

        internal static AnimationRewriter For(BuildContext context) => new Ndmf(context);
        internal static AnimationRewriter For(IEnumerable<(Transform Owner, AnimationClip Clip)> clips, IEnumerable<(Transform Owner, AvatarMask Mask)> masks = null) =>
            new Clips(clips.ToList()) { Masks = masks?.ToList() ?? new List<(Transform, AvatarMask)>() };

        // Every clip reachable from the controllers NDMF knows (platform marker clips excepted).
        private sealed class Ndmf : AnimationRewriter
        {
            private readonly BuildContext context;
            internal Ndmf(BuildContext context) { this.context = context; }

            private Transform Owner(object key) => key is Animator animator && animator ? animator.transform : context.AvatarRootObject.transform;

            internal override IEnumerable<(Transform Owner, string Path)> MaskPaths()
            {
                var controllers = context.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    return controllers.Controllers.Where(e => e.Value != null).SelectMany(e => e.Value.Layers
                        .Where(l => l.AvatarMask != null).SelectMany(l => l.AvatarMask.Elements.Keys.Select(k => (Owner(e.Key), k)))).ToList();
                }
                finally { ControllerCommit.Preserving(context.AvatarRootObject, () => context.DeactivateExtensionContext<VirtualControllerContext>()); }
            }

            internal override void RewriteMasks(Func<Transform, string, string> map)
            {
                var controllers = context.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    var done = new HashSet<VirtualAvatarMask>();
                    foreach (var entry in controllers.Controllers.Where(e => e.Value != null))
                        foreach (var mask in entry.Value.Layers.Select(l => l.AvatarMask).Where(m => m != null && done.Add(m)))
                        {
                            var elements = System.Collections.Immutable.ImmutableDictionary<string, float>.Empty.ToBuilder();
                            foreach (var element in mask.Elements)
                                if (map(Owner(entry.Key), element.Key) is string path) elements[path] = element.Value;
                            mask.Elements = elements.ToImmutable();
                        }
                }
                finally { ControllerCommit.Preserving(context.AvatarRootObject, () => context.DeactivateExtensionContext<VirtualControllerContext>()); }
            }

            internal override void RewriteValues(Func<UnityEngine.Object, UnityEngine.Object> map)
            {
                var controllers = context.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    foreach (var clip in controllers.Controllers.Values.Where(c => c != null).SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>().Where(c => !c.IsMarkerClip).Distinct())
                        foreach (var binding in clip.GetObjectCurveBindings().ToList())
                        {
                            var keys = clip.GetObjectCurve(binding);
                            if (keys == null) continue;
                            bool changed = false;
                            for (int i = 0; i < keys.Length; i++)
                                if (keys[i].value && map(keys[i].value) is UnityEngine.Object next) { keys[i].value = next; changed = true; }
                            if (changed) clip.SetObjectCurve(binding, keys);
                        }
                }
                finally { ControllerCommit.Preserving(context.AvatarRootObject, () => context.DeactivateExtensionContext<VirtualControllerContext>()); }
            }

            internal override void Copy(Func<Transform, EditorCurveBinding, IEnumerable<EditorCurveBinding>> map)
            {
                var controllers = context.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    foreach (var entry in controllers.Controllers)
                    {
                        if (entry.Value == null) continue;
                        var owner = entry.Key is Animator animator && animator ? animator.transform : context.AvatarRootObject.transform;
                        foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Where(c => !c.IsMarkerClip).Distinct())
                            foreach (var binding in clip.GetFloatCurveBindings().ToList())
                                foreach (var to in map(owner, binding) ?? Enumerable.Empty<EditorCurveBinding>())
                                    clip.SetFloatCurve(to, clip.GetFloatCurve(binding));
                    }
                }
                finally { ControllerCommit.Preserving(context.AvatarRootObject, () => context.DeactivateExtensionContext<VirtualControllerContext>()); }
            }

            internal override void Rewrite(Func<Transform, EditorCurveBinding, EditorCurveBinding?> map)
            {
                var controllers = context.ActivateExtensionContext<VirtualControllerContext>();
                try
                {
                    foreach (var entry in controllers.Controllers)
                    {
                        if (entry.Value == null) continue;
                        var owner = entry.Key is Animator animator && animator ? animator.transform : context.AvatarRootObject.transform;
                        foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Where(c => !c.IsMarkerClip).Distinct())
                        {
                            // Two phases, so a curve moving onto a binding that itself moves away is never overwritten.
                            var floats = clip.GetFloatCurveBindings().Select(b => (From: b, To: map(owner, b))).Where(m => m.To != null)
                                .Select(m => (m.From, To: m.To.Value, Curve: clip.GetFloatCurve(m.From))).ToList();
                            var objects = clip.GetObjectCurveBindings().Select(b => (From: b, To: map(owner, b))).Where(m => m.To != null)
                                .Select(m => (m.From, To: m.To.Value, Curve: clip.GetObjectCurve(m.From))).ToList();
                            foreach (var m in floats) clip.SetFloatCurve(m.From, null);
                            foreach (var m in objects) clip.SetObjectCurve(m.From, null);
                            foreach (var m in floats) clip.SetFloatCurve(m.To, m.Curve);
                            foreach (var m in objects) clip.SetObjectCurve(m.To, m.Curve);
                        }
                    }
                }
                finally { ControllerCommit.Preserving(context.AvatarRootObject, () => context.DeactivateExtensionContext<VirtualControllerContext>()); }
            }
        }

        private sealed class Clips : AnimationRewriter
        {
            private readonly List<(Transform Owner, AnimationClip Clip)> clips;
            internal Clips(List<(Transform, AnimationClip)> clips) { this.clips = clips; }
            internal List<(Transform Owner, AvatarMask Mask)> Masks = new List<(Transform, AvatarMask)>();

            internal override IEnumerable<(Transform Owner, string Path)> MaskPaths() =>
                Masks.SelectMany(m => Enumerable.Range(0, m.Mask.transformCount).Select(i => (m.Owner, m.Mask.GetTransformPath(i)))).ToList();

            internal override void RewriteMasks(Func<Transform, string, string> map)
            {
                foreach (var (owner, mask) in Masks)
                {
                    var elements = Enumerable.Range(0, mask.transformCount)
                        .Select(i => (Path: map(owner, mask.GetTransformPath(i)), Active: mask.GetTransformActive(i))).Where(e => e.Path != null).ToList();
                    mask.transformCount = elements.Count;
                    for (int i = 0; i < elements.Count; i++) { mask.SetTransformPath(i, elements[i].Path); mask.SetTransformActive(i, elements[i].Active); }
                }
            }

            internal override void RewriteValues(Func<UnityEngine.Object, UnityEngine.Object> map)
            {
                foreach (var (_, clip) in clips)
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        bool changed = false;
                        for (int i = 0; i < keys.Length; i++)
                            if (keys[i].value && map(keys[i].value) is UnityEngine.Object next) { keys[i].value = next; changed = true; }
                        if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
                    }
            }

            internal override void Copy(Func<Transform, EditorCurveBinding, IEnumerable<EditorCurveBinding>> map)
            {
                foreach (var (owner, clip) in clips)
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        foreach (var to in map(owner, binding) ?? Enumerable.Empty<EditorCurveBinding>())
                            AnimationUtility.SetEditorCurve(clip, to, AnimationUtility.GetEditorCurve(clip, binding));
            }

            internal override void Rewrite(Func<Transform, EditorCurveBinding, EditorCurveBinding?> map)
            {
                foreach (var (owner, clip) in clips)
                {
                    var floats = AnimationUtility.GetCurveBindings(clip).Select(b => (From: b, To: map(owner, b))).Where(m => m.To != null)
                        .Select(m => (m.From, To: m.To.Value, Curve: AnimationUtility.GetEditorCurve(clip, m.From))).ToList();
                    var objects = AnimationUtility.GetObjectReferenceCurveBindings(clip).Select(b => (From: b, To: map(owner, b))).Where(m => m.To != null)
                        .Select(m => (m.From, To: m.To.Value, Curve: AnimationUtility.GetObjectReferenceCurve(clip, m.From))).ToList();
                    foreach (var m in floats) AnimationUtility.SetEditorCurve(clip, m.From, null);
                    foreach (var m in objects) AnimationUtility.SetObjectReferenceCurve(clip, m.From, null);
                    foreach (var m in floats) AnimationUtility.SetEditorCurve(clip, m.To, m.Curve);
                    foreach (var m in objects) AnimationUtility.SetObjectReferenceCurve(clip, m.To, m.Curve);
                }
            }
        }
    }
}
