using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Fewer submeshes and material slots, so fewer draw calls, with the same pixels:
    //  - Empty submeshes (no indices) draw nothing; they and their slots go.
    //  - Submeshes beyond the renderer's slot count have no material and are never drawn; they go.
    //  - A submesh whose material is identical in everything Unity serializes (the same material, or a copy) to an
    //    earlier slot joins it when every slot drawn between them is order-independent too (opaque state, see
    //    SkinnedMeshMerger.OrderIndependent) and none of them is swapped: its triangles are appended to that slot, and among
    //    such materials drawing them earlier changes nothing.
    // Slots an animation swaps are never merged; animations of the remaining slots are renumbered. Only meshes that
    // one renderer uses, that nothing else references or swaps, with no more slots than submeshes and triangle topology.
    // Nothing changes when the avatar's animation cannot be read.
    internal static class SubmeshCleaner
    {
        internal sealed class Result { public int Removed, Merged; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, AnimationRewriter rewriter)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            var renderers = root.GetComponentsInChildren<Renderer>(true).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
            var meshUsers = renderers.Select(r => (Renderer: r, Mesh: MeshOf(r))).Where(p => p.Mesh).GroupBy(p => p.Mesh)
                .ToDictionary(g => g.Key, g => g.Count());
            var renumber = new Dictionary<GameObject, (Type Type, string Path, int[] Map)>();

            // Renumber the slots of renderers already changed even if a later renderer fails.
            try
            {
            foreach (var renderer in renderers)
            {
                var mesh = MeshOf(renderer);
                // A property block can hold values per material slot, which renumbering slots would move.
                if (!mesh || meshUsers[mesh] != 1 || swaps.Contains(mesh) || Exclusions.Excluded(renderer) || TextureUsageScanner.HasPropertyBlock(renderer)) continue;
                // A mesh swap whose idle state writes nothing leaves the current mesh out of the swapped values.
                if (analysis.IsAnimated(renderer, p => p == "m_Mesh") ||
                    (renderer is MeshRenderer && renderer.GetComponent<MeshFilter>() is MeshFilter filter && analysis.IsAnimated(filter, p => p == "m_Mesh"))) continue;
                if (analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer) && !(c is MeshFilter))) continue;
                // Cloth builds its constraints from the triangles, and a particle shape can emit from this renderer's triangles
                // (or one submesh by number).
                if (renderer.GetComponent<Cloth>() || analysis.ReferencesTo(renderer).Any(c => c is ParticleSystem)) continue;
                var materials = renderer.sharedMaterials;
                // Submeshes beyond the slot count have no material, so Unity never draws them; they go. (More slots than
                // submeshes draws the last submesh again, so those renderers are left alone.)
                if (materials.Length > mesh.subMeshCount || materials.Length == 0 || mesh.subMeshCount < 2) continue;
                int undrawn = mesh.subMeshCount - materials.Length;
                if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) continue;
                var bindings = analysis.BindingsOn(renderer).ToList();
                var swapped = new HashSet<int>();
                foreach (var b in bindings.Where(b => b.Property.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal)))
                {
                    if (!int.TryParse(b.Property.Substring(23).TrimEnd(']'), out int slot)) { swapped.Add(-1); continue; }
                    swapped.Add(slot);
                }
                if (swapped.Contains(-1)) continue;
                if (swapped.Count > 0 && (rewriter == null || bindings.Any(b => b.Legacy || b.Owner != root.transform) ||
                    AvatarAnalysis.Resolve(root.transform, AnimationUtility.CalculateTransformPath(renderer.transform, root.transform)).Count() != 1)) continue;

                // Plan: each old slot maps to a new slot, or -1 when removed.
                var signatures = materials.Select(m => m ? DuplicateMaterialMerger.Signature(m) : null).ToArray();
                var target = new int[materials.Length];
                var keptSlots = new List<int>();
                // Opaque triangles drawn earlier only change the picture where two surfaces overlap at exactly equal depth, which needs
                // them in one plane (triangles sharing an edge never cover the same pixel). Each kept slot holds its triangles'
                // planes, quantized, both ways round; a query probes the neighbouring keys so rounding never splits a plane.
                // ponytail: bind-pose planes only; a blend shape or skinning that brings two separate surfaces into one plane is
                // not modeled (that takes exact coincidence).
                var keptPlanes = new List<HashSet<(int, int, int, int)>>();
                Vector3[] vertices = null;
                List<(int, int, int, int)> Planes(int slot, bool bothWays)
                {
                    if (vertices == null) vertices = mesh.vertices;
                    var planes = new List<(int, int, int, int)>();
                    var used = mesh.GetIndices(slot, true);
                    (int, int, int, int) Key(Vector3 n, float d) => (Mathf.FloorToInt(n.x * 1000), Mathf.FloorToInt(n.y * 1000), Mathf.FloorToInt(n.z * 1000), Mathf.FloorToInt(d * 10000));
                    for (int i = 0; i + 2 < used.Length; i += 3)
                    {
                        var a = vertices[used[i]];
                        var normal = Vector3.Cross(vertices[used[i + 1]] - a, vertices[used[i + 2]] - a);
                        float length = normal.magnitude;
                        if (length == 0 || float.IsNaN(length) || float.IsInfinity(length)) continue; // No area, nothing drawn.
                        normal /= length;
                        planes.Add(Key(normal, Vector3.Dot(normal, a)));
                        if (bothWays) planes.Add(Key(-normal, -Vector3.Dot(normal, a)));
                    }
                    return planes;
                }
                bool SharesPlane(HashSet<(int, int, int, int)> kept, List<(int, int, int, int)> mine)
                {
                    foreach (var (x, y, z, w) in mine)
                        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dw = -1; dw <= 1; dw++)
                            if (kept.Contains((x + dx, y + dy, z + dz, w + dw))) return true;
                    return false;
                }
                int removed = 0, merged = 0;
                for (int s = 0; s < materials.Length; s++)
                {
                    if (mesh.GetIndexCount(s) == 0 && !swapped.Contains(s)) { target[s] = -1; removed++; continue; }
                    int into = -1;
                    // Into the nearest earlier identical slot, provided every slot drawn between them is order-independent and
                    // never swapped too: among such materials, drawing these triangles earlier changes nothing.
                    if (!swapped.Contains(s) && SkinnedMeshMerger.OrderIndependent(materials[s]) && signatures[s] != null)
                    {
                        List<(int, int, int, int)> mine = null;
                        for (int previous = keptSlots.Count - 1; previous >= 0; previous--)
                        {
                            int k = keptSlots[previous];
                            if (swapped.Contains(k) || !materials[k]) break;
                            if (signatures[k] != null && signatures[k].SequenceEqual(signatures[s])) { into = previous; break; }
                            // Moving these triangles before slot k changes nothing only if none lies in a plane of k's.
                            if (!SkinnedMeshMerger.OrderIndependent(materials[k]) || SharesPlane(keptPlanes[previous], mine ?? (mine = Planes(s, false)))) break;
                        }
                    }
                    if (into >= 0)
                    {
                        target[s] = into; merged++;
                        keptPlanes[into].UnionWith(Planes(s, true));
                        continue;
                    }
                    target[s] = keptSlots.Count;
                    keptSlots.Add(s);
                    keptPlanes.Add(new HashSet<(int, int, int, int)>(Planes(s, true)));
                }
                removed += undrawn;
                if (removed + merged == 0) continue;

                var indices = Enumerable.Range(0, keptSlots.Count).Select(_ => new List<int>()).ToList();
                for (int s = 0; s < materials.Length; s++)
                    if (target[s] >= 0) indices[target[s]].AddRange(mesh.GetIndices(s, true));
                // A 16-bit mesh can reach vertices past 65535 through a submesh base vertex; rebuilt with base vertex 0, those indices
                // would not fit, so such a mesh keeps its submeshes.
                if (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 && indices.Any(list => list.Any(i => i >= ushort.MaxValue))) continue;
                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name;
                copy.subMeshCount = keptSlots.Count;
                for (int s = 0; s < keptSlots.Count; s++) copy.SetIndices(indices[s].ToArray(), MeshTopology.Triangles, s, false, 0);
                copy.bounds = mesh.bounds;
                register(mesh, copy);
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    SkinnedMeshMerger.Rebind(skinned, () =>
                    {
                        skinned.sharedMesh = copy;
                        skinned.sharedMaterials = keptSlots.Select(s => materials[s]).ToArray();
                    });
                }
                else
                {
                    renderer.GetComponent<MeshFilter>().sharedMesh = copy;
                    renderer.sharedMaterials = keptSlots.Select(s => materials[s]).ToArray();
                }
                if (swapped.Count > 0)
                    renumber[renderer.gameObject] = (renderer.GetType(), AnimationUtility.CalculateTransformPath(renderer.transform, root.transform), target);
                result.Removed += removed;
                result.Merged += merged;
            }
            }
            finally
            {
            if (renumber.Count > 0)
                rewriter.Rewrite((owner, binding) =>
                {
                    if (owner != root.transform) return null;
                    var found = AvatarAnalysis.Resolve(owner, binding.path).ToList();
                    if (found.Count != 1 || !renumber.TryGetValue(found[0].gameObject, out var plan) || binding.type != plan.Type) return null;
                    const string prefix = "m_Materials.Array.data[";
                    if (!binding.propertyName.StartsWith(prefix, StringComparison.Ordinal) ||
                        !int.TryParse(binding.propertyName.Substring(prefix.Length).TrimEnd(']'), out int old) || old >= plan.Map.Length) return null;
                    if (plan.Map[old] == old) return null;
                    var moved = binding;
                    moved.propertyName = prefix + plan.Map[old] + "]";
                    return moved;
                });
            }
            return result;
        }

        private static Mesh MeshOf(Renderer renderer) =>
            renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
            : renderer.GetComponent<MeshFilter>() is MeshFilter filter && filter ? filter.sharedMesh : null;
    }
}
