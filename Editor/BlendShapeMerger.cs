using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Blend shapes that move together are applied as one: a partner's deltas are added to the host's, scaled by the
    // constant ratio between their weights, and the partner goes (Unity blends one shape instead of two). Typical
    // after merging skinned meshes, where each part's "Shrink" or toggle shape is animated by the same clips, and on
    // outfits whose narrow/wide shapes are driven together at different magnitudes. Two shapes of one renderer merge
    // when:
    //  - both have the same frames (count and weights), and vertices may overlap: Unity adds each shape's delta at
    //    its weight, so one shape holding dHost + k*dOther deforms identically, up to the float rounding the
    //    merged-mesh accepted difference already covers (at most 1/255 per colour channel);
    //  - the same clips animate both, with one shape's curve exactly k times the other's (key times, values,
    //    tangents and key weights), and the renderer holds them at rest weights in that same ratio k, so their weights never diverge;
    //  - VRChat does not drive either (visemes, eyelids) and neither is an MMD world shape on the root "Body" mesh.
    // The partner's curve is dropped (the host's drives the sum). Only meshes one renderer uses, that no animation
    // swaps and nothing else references, without Cloth. Nothing changes when the avatar's animation cannot be read.
    internal static class BlendShapeMerger
    {
        internal sealed class Result
        {
            public int Meshes, Merged;
            // Why a renderer's animated shapes were not looked at, and why a shape that shares its animation with another
            // stayed apart: the reason and what it applies to ("renderer" or "renderer: shape"). Report only, no effect.
            public readonly Dictionary<string, List<string>> RendererWhyNot = new Dictionary<string, List<string>>();
            public readonly Dictionary<string, List<string>> ShapeWhyNot = new Dictionary<string, List<string>>();
        }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, AnimationRewriter rewriter, bool keepMmdShapes = true)
        {
            var result = new Result();
            if (!analysis.Complete || rewriter == null) return result;
            var root = analysis.Root;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh).ToList();
            var moves = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
            void Why(Dictionary<string, List<string>> into, string reason, string what)
            {
                if (!into.TryGetValue(reason, out var names)) into[reason] = names = new List<string>();
                names.Add(what);
            }
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                if (mesh.blendShapeCount < 2) continue;
                var bindings = analysis.BindingsOn(renderer, p => p.StartsWith("blendShape.", StringComparison.Ordinal)).ToList();
                // Only renderers with two or more animated shapes could merge anything, so only they get a reason.
                if (bindings.Select(b => b.Property).Distinct().Count() < 2) continue;
                string refused = RendererRefusal(renderer, mesh, renderers, swaps, analysis, bindings);
                if (refused != null) { Why(result.RendererWhyNot, refused, renderer.name); continue; }
                bool mmdBody = keepMmdShapes && renderer.name == "Body" && renderer.transform.parent == root.transform;

                // A shape merges into an earlier one when the two deform the same vertices in one fixed proportion:
                // the same frames, the same clips animating both, and one weight a constant multiple (k) of the
                // other, with the renderer's rest weights in that same ratio. One shape at the host's weight, holding
                // dHost + k*dOther, then deforms exactly as both did. byClipSet groups by the clips alone, so a shape
                // that shares its clips but not its frames, weight ratio or curve shape can say so.
                var shapes = new List<(int Shape, float RestWeight, string Frames, List<AvatarAnalysis.Binding> Curves)>();
                var byClipSet = new Dictionary<string, List<int>>();
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    string name = mesh.GetBlendShapeName(shape);
                    if (analysis.BlendShapeDrivenByPlatform(renderer, name) || mmdBody && BlendShapeFreezer.MmdShapes.Contains(name)) continue;
                    var curves = bindings.Where(b => b.Property == "blendShape." + name).ToList();
                    if (curves.Count == 0) continue; // The freezer handles shapes nothing animates.
                    string frames = mesh.GetBlendShapeFrameCount(shape) + ":" + string.Join(",", Enumerable.Range(0, mesh.GetBlendShapeFrameCount(shape)).Select(f => mesh.GetBlendShapeFrameWeight(shape, f).ToString("R", CultureInfo.InvariantCulture)));
                    shapes.Add((shape, renderer.GetBlendShapeWeight(shape), frames, curves));
                    string clips = string.Join(";", curves.Select(b => b.ClipKey).OrderBy(x => x, StringComparer.Ordinal));
                    if (!byClipSet.TryGetValue(clips, out var same)) byClipSet.Add(clips, same = new List<int>());
                    same.Add(shape);
                }

                var into = new Dictionary<int, int>();
                var scales = new Dictionary<int, float>();
                foreach (var host in shapes)
                {
                    if (into.ContainsKey(host.Shape)) continue;
                    float hostMax = MaxAbs(host.Curves);
                    if (hostMax <= 1e-6f) continue;
                    foreach (var other in shapes)
                    {
                        if (other.Shape <= host.Shape || into.ContainsKey(other.Shape)) continue;
                        if (other.Frames != host.Frames || other.Curves.Count != host.Curves.Count) continue;
                        var otherCurves = other.Curves.ToDictionary(b => b.ClipKey, b => b.FloatCurve);
                        if (otherCurves.Count != host.Curves.Count) continue;
                        float k = MaxAbs(other.Curves) / hostMax;
                        if (k <= 0f) continue;
                        if (Math.Abs(other.RestWeight - k * host.RestWeight) > 1e-3f + 1e-5f * Math.Abs(other.RestWeight)) continue;
                        bool all = true;
                        foreach (var binding in host.Curves)
                            if (binding.FloatCurve == null || !otherCurves.TryGetValue(binding.ClipKey, out var curve) || !Proportional(binding.FloatCurve, curve, k)) { all = false; break; }
                        if (!all) continue;
                        into[other.Shape] = host.Shape;
                        scales[other.Shape] = k;
                    }
                }

                // Shapes that share their clips with another yet ended with no partner: the first thing that differs.
                var partnered = new HashSet<int>(into.Keys.Concat(into.Values));
                foreach (var same in byClipSet.Values.Where(g => g.Count > 1))
                    foreach (int shape in same.Where(x => !partnered.Contains(x)))
                        Why(result.ShapeWhyNot, WhyNotMerged(shape, same, shapes), renderer.name + ": " + mesh.GetBlendShapeName(shape));
                if (into.Count == 0) continue;

                var copy = Build(mesh, into, scales);
                register(mesh, copy);
                var kept = Enumerable.Range(0, mesh.blendShapeCount).Where(s => !into.ContainsKey(s)).ToArray();
                var weights = kept.Select(renderer.GetBlendShapeWeight).ToArray();
                SkinnedMeshMerger.Rebind(renderer, () => renderer.sharedMesh = copy);
                for (int i = 0; i < kept.Length; i++) renderer.SetBlendShapeWeight(i, weights[i]);
                BlendShapeFreezer.RenumberEyelids(root, renderer, kept);
                moves[renderer] = into.ToDictionary(p => mesh.GetBlendShapeName(p.Key), p => mesh.GetBlendShapeName(p.Value));
                result.Meshes++;
                result.Merged += into.Count;
            }

            if (moves.Count > 0)
            {
                var paths = new PathCache();
                rewriter.Remove((owner, binding) =>
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) return false;
                    var found = paths.Single(owner, binding.path);
                    if (found == null || !(found.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer target) ||
                        !moves.TryGetValue(target, out var map)) return false;
                    // A merged shape named no mesh shape any more: its own curve goes, and the host's curve drives the sum.
                    return map.ContainsKey(binding.propertyName.Substring(11));
                });
            }
            return result;
        }

        // Why none of the renderer's shapes may merge, or null. The same gates, in the same order, as before reasons were reported.
        private static string RendererRefusal(SkinnedMeshRenderer renderer, Mesh mesh, List<SkinnedMeshRenderer> renderers, HashSet<Mesh> swaps,
            AvatarAnalysis analysis, List<AvatarAnalysis.Binding> bindings)
        {
            if (Exclusions.Excluded(renderer)) return "excluded from Arclight";
            if (!mesh.isReadable) return "its mesh has Read/Write off";
            if (swaps.Contains(mesh)) return "an animation swaps its mesh";
            if (renderers.Count(r => r.sharedMesh == mesh) != 1) return "another renderer shares its mesh";
            // A mesh swap whose idle state writes nothing leaves the current mesh out of the swapped values.
            if (analysis.IsAnimated(renderer, p => p == "m_Mesh")) return "an animation swaps its mesh";
            if (analysis.ReferencesTo(mesh).Any(c => c != renderer)) return "another component uses its mesh";
            if (renderer.GetComponent<Cloth>()) return "Cloth simulates it";
            if (analysis.ReferencesTo(renderer).Any(c => c.GetType().Name != "VRCAvatarDescriptor")) return "a component other than the avatar descriptor refers to it";
            // Native face tracking binds these shapes by name, and a stack like FermataFT fails the build when any shape its
            // outputs bind is missing (see BlendShapeFreezer), so no shape on such a mesh may go.
            if (analysis.IsNativeFaceTrackingMesh(renderer)) return "it is a native face tracking mesh, whose shapes are all kept";
            if (bindings.Any(b => b.Legacy || b.FloatCurve == null || AvatarAnalysis.ResolveSingle(b.Owner, b.Curve.path) == null))
                return "a blend shape animation on it is legacy, not a float curve, or names more than one object";
            return null;
        }

        // The largest |value| any of the shape's curves reaches: the ratio between two shapes comes from these, so a
        // pair driven at different magnitudes (100 against 50) still takes one k.
        private static float MaxAbs(IEnumerable<AvatarAnalysis.Binding> bindings)
        {
            float max = 0f;
            foreach (var binding in bindings)
                if (binding.FloatCurve != null)
                    foreach (var key in binding.FloatCurve.keys) max = Math.Max(max, Math.Abs(key.value));
            return max;
        }

        // Whether one curve is exactly k times the other for the whole timeline: same key times and wrap modes, and
        // values and in/out tangents scaled by k (so the blended result never leaves the other's shape).
        private static bool Proportional(AnimationCurve curve, AnimationCurve other, float k)
        {
            if (curve == null || other == null || curve.keys.Length != other.keys.Length ||
                curve.preWrapMode != other.preWrapMode || curve.postWrapMode != other.postWrapMode) return false;
            for (int i = 0; i < curve.keys.Length; i++)
            {
                var a = curve.keys[i];
                var b = other.keys[i];
                if (Math.Abs(a.time - b.time) > 1e-4f) return false;
                if (Math.Abs(b.value - k * a.value) > 1e-3f + 1e-5f * Math.Abs(b.value)) return false;
                if (Math.Abs(b.inTangent - k * a.inTangent) > 1e-2f + 1e-3f * Math.Abs(b.inTangent)) return false;
                if (Math.Abs(b.outTangent - k * a.outTangent) > 1e-2f + 1e-3f * Math.Abs(b.outTangent)) return false;
                // Weights scale the effective tangent where the mode uses them, and the sum is driven by one curve.
                if (a.weightedMode != b.weightedMode) return false;
                if (Math.Abs(a.inWeight - b.inWeight) > 1e-4f || Math.Abs(a.outWeight - b.outWeight) > 1e-4f) return false;
            }
            return true;
        }

        // The first thing that kept a shape from merging into a shape it shares its clips with.
        private static string WhyNotMerged(int shape, List<int> same,
            List<(int Shape, float RestWeight, string Frames, List<AvatarAnalysis.Binding> Curves)> shapes)
        {
            var mine = shapes.First(x => x.Shape == shape);
            var partner = shapes.FirstOrDefault(x => x.Shape != shape && same.Contains(x.Shape));
            if (partner.Frames == null) return "no partner shape";
            if (partner.Frames != mine.Frames) return "its frames (count or weights) differ from its partner's";
            float mineMax = MaxAbs(mine.Curves), partnerMax = MaxAbs(partner.Curves);
            if (mineMax <= 1e-6f || partnerMax <= 1e-6f) return "a shape holds no weight to take a ratio from";
            float k = mineMax / partnerMax;
            if (Math.Abs(mine.RestWeight - k * partner.RestWeight) > 1e-3f + 1e-5f * Math.Abs(mine.RestWeight))
                return "its rest weight is not in ratio with its partner's";
            return "its curve is not a constant multiple of its partner's";
        }

        private static Mesh Build(Mesh mesh, Dictionary<int, int> into, Dictionary<int, float> scales)
        {
            int count = mesh.vertexCount;
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.ClearBlendShapes();
            Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
            Vector3[] mv = new Vector3[count], mn = new Vector3[count], mt = new Vector3[count];
            // Each host's merged shapes, in the order `into` enumerates them (the order their deltas are summed).
            var partners = into.ToLookup(p => p.Value, p => p.Key);
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                if (into.ContainsKey(shape)) continue;
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    foreach (int merged in partners[shape])
                    {
                        mesh.GetBlendShapeFrameVertices(merged, f, mv, mn, mt); // Same frame weights: frame f matches frame f.
                        // The merged shape is driven by the host's curve, so a partner's deltas enter scaled by its
                        // ratio k (k = 1 for a shape that always held the same weight).
                        float k = scales.TryGetValue(merged, out var scale) ? scale : 1f;
                        for (int v = 0; v < count; v++) { dv[v] += k * mv[v]; dn[v] += k * mn[v]; dt[v] += k * mt[v]; }
                    }
                    copy.AddBlendShapeFrame(mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, f), dv, dn, dt);
                }
            }
            copy.bounds = mesh.bounds;
            return copy;
        }
    }
}
