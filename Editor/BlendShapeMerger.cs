using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Blend shapes that always hold the same weight are applied as one: the second's deltas are added to the first's
    // and the second goes (Unity blends one shape instead of two). Typical after merging skinned meshes, where each
    // part's "Shrink" or toggle shape is animated by the same clips. Two shapes of one renderer merge when:
    //  - both have the same frames (count and weights), and no vertex has a delta in both in any frame, so at every weight
    //    each vertex keeps its own shape's frames and interpolation factor (every sum adds zero);
    //  - the renderer holds both at the same weight, and every clip that animates either animates both with
    //    identical curves (controllers only, on a path that names this renderer alone);
    //  - VRChat does not drive either (visemes, eyelids) and neither is an MMD world shape on the root "Body" mesh.
    // The second shape's curves are removed (moved onto the first's identical ones). Only meshes one renderer uses,
    // that no animation swaps and nothing else references, without Cloth. Nothing changes when the avatar's animation
    // cannot be read.
    internal static class BlendShapeMerger
    {
        internal sealed class Result { public int Meshes, Merged; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, AnimationRewriter rewriter, bool keepMmdShapes = true)
        {
            var result = new Result();
            if (!analysis.Complete || rewriter == null) return result;
            var root = analysis.Root;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh).ToList();
            var moves = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                if (mesh.blendShapeCount < 2 || swaps.Contains(mesh) || renderers.Count(r => r.sharedMesh == mesh) != 1 || Exclusions.Excluded(renderer)) continue;
                // A mesh swap whose idle state writes nothing leaves the current mesh out of the swapped values.
                if (analysis.IsAnimated(renderer, p => p == "m_Mesh")) continue;
                if (analysis.ReferencesTo(mesh).Any(c => c != renderer) || renderer.GetComponent<Cloth>()) continue;
                if (analysis.ReferencesTo(renderer).Any(c => c.GetType().Name != "VRCAvatarDescriptor")) continue;
                var bindings = analysis.BindingsOn(renderer, p => p.StartsWith("blendShape.", StringComparison.Ordinal)).ToList();
                if (bindings.Any(b => b.Legacy || b.FloatCurve == null ||
                    AvatarAnalysis.Resolve(b.Owner, b.Curve.path).Count() != 1)) continue;
                bool mmdBody = keepMmdShapes && renderer.name == "Body" && renderer.transform.parent == root.transform;

                // Shapes that could share a weight, grouped by everything that decides it.
                var groups = new Dictionary<string, List<int>>();
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    string name = mesh.GetBlendShapeName(shape);
                    if (analysis.BlendShapeDrivenByPlatform(renderer, name) || mmdBody && BlendShapeFreezer.MmdShapes.Contains(name)) continue;
                    var curves = bindings.Where(b => b.Property == "blendShape." + name).ToList();
                    if (curves.Count == 0) continue; // The freezer handles shapes nothing animates.
                    string key = renderer.GetBlendShapeWeight(shape).ToString("R", CultureInfo.InvariantCulture) + "|" + mesh.GetBlendShapeFrameCount(shape) + ":" + string.Join(",", Enumerable.Range(0, mesh.GetBlendShapeFrameCount(shape)).Select(f => mesh.GetBlendShapeFrameWeight(shape, f).ToString("R", CultureInfo.InvariantCulture))) + "|" +
                        string.Join(";", curves.Select(b => b.ClipKey + ":" + CurveKey(b.FloatCurve)).OrderBy(s => s, StringComparer.Ordinal));
                    if (!groups.TryGetValue(key, out var list)) groups.Add(key, list = new List<int>());
                    list.Add(shape);
                }

                // Within a group, each shape joins the first host whose touched vertices it does not share.
                var into = new Dictionary<int, int>();
                foreach (var group in groups.Values.Where(g => g.Count > 1))
                {
                    var hosts = new List<(int Shape, bool[] Touched)>();
                    foreach (int shape in group)
                    {
                        var touched = Touched(mesh, shape);
                        int host = hosts.FindIndex(h => !h.Touched.Where((t, v) => t && touched[v]).Any());
                        if (host < 0) { hosts.Add((shape, touched)); continue; }
                        into[shape] = hosts[host].Shape;
                        for (int v = 0; v < touched.Length; v++) hosts[host].Touched[v] |= touched[v];
                    }
                }
                if (into.Count == 0) continue;

                var copy = Build(mesh, into);
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
                rewriter.Rewrite((owner, binding) =>
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) return null;
                    var found = AvatarAnalysis.Resolve(owner, binding.path).ToList();
                    if (found.Count != 1 || !(found[0].GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer target) || !moves.TryGetValue(target, out var map) ||
                        !map.TryGetValue(binding.propertyName.Substring(11), out string host)) return null;
                    var moved = binding;
                    moved.propertyName = "blendShape." + host; // Onto the host's identical curve: the merged shape's curve goes.
                    return moved;
                });
            return result;
        }

        private static string CurveKey(AnimationCurve curve) =>
            curve.preWrapMode + "," + curve.postWrapMode + "," + string.Join(",", curve.keys.Select(k =>
                string.Join(" ", new[] { k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight }.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + " " + (int)k.weightedMode));

        // Vertices with any delta in any of the shape's frames.
        private static bool[] Touched(Mesh mesh, int shape)
        {
            int count = mesh.vertexCount;
            Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
            var touched = new bool[count];
            for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
            {
                mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                for (int v = 0; v < count; v++) touched[v] |= !dv[v].Equals(Vector3.zero) || !dn[v].Equals(Vector3.zero) || !dt[v].Equals(Vector3.zero);
            }
            return touched;
        }

        private static Mesh Build(Mesh mesh, Dictionary<int, int> into)
        {
            int count = mesh.vertexCount;
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            copy.ClearBlendShapes();
            Vector3[] dv = new Vector3[count], dn = new Vector3[count], dt = new Vector3[count];
            Vector3[] mv = new Vector3[count], mn = new Vector3[count], mt = new Vector3[count];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                if (into.ContainsKey(shape)) continue;
                for (int f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                    foreach (int merged in into.Where(p => p.Value == shape).Select(p => p.Key))
                    {
                        mesh.GetBlendShapeFrameVertices(merged, f, mv, mn, mt); // Same frame weights: frame f matches frame f.
                        for (int v = 0; v < count; v++) { dv[v] += mv[v]; dn[v] += mn[v]; dt[v] += mt[v]; } // Disjoint: one side is zero.
                    }
                    copy.AddBlendShapeFrame(mesh.GetBlendShapeName(shape), mesh.GetBlendShapeFrameWeight(shape, f), dv, dn, dt);
                }
            }
            copy.bounds = mesh.bounds;
            return copy;
        }
    }
}
