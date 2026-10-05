using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // The MeshRenderer counterpart of the skinned mesh merger: plain meshes that never move relative to each other and
    // render the same way are drawn by one renderer (fewer renderers; identical materials then share a submesh). The
    // first renderer in hierarchy order hosts; members' vertices are moved into the host's space, their submeshes and
    // slots appended, and their MeshRenderer and MeshFilter removed (their objects stay). Only when provably the same:
    //  - Neither the renderers nor anything up to the avatar root can be toggled, and no renderer is animated (material
    //    properties would reach the other part's materials) or referenced by another component.
    //  - Host and member never move relative to each other, and both have world scale exactly 1 that nothing can
    //    change, so the move is a rotation and translation and object-space distances stay the same; every material is
    //    an audited lilToon entry without object-space position effects, opaque (queue up to 2500), and does not read
    //    vertex or primitive IDs.
    //  - Same renderer settings, and probes are not sampled or are sampled at the same anchor (never the bounds
    //    centre, which merging moves). Same vertex layout with 32-bit positions, normals and tangents; no blend shapes
    //    or bone weights; one slot per submesh, triangles only.
    internal static class StaticMeshMerger
    {
        internal sealed class Result { public int Merged, Into; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            var candidates = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => !Exclusions.Excluded(r) && Eligible(r, analysis, swaps)).ToList();
            foreach (var group in candidates.GroupBy(r => Key(r)))
            {
                var remaining = group.ToList();
                while (remaining.Count > 1)
                {
                    var host = remaining[0];
                    var members = remaining.Skip(1).Where(m => !analysis.MovesRelative(host.transform, m.transform)).ToList();
                    remaining = remaining.Skip(1).Except(members).ToList();
                    if (members.Count == 0) continue;
                    Merge(host, members, register);
                    result.Merged += members.Count;
                    result.Into++;
                }
            }
            if (result.Merged > 0) analysis.RescanReferences();
            return result;
        }

        private static bool Eligible(MeshRenderer renderer, AvatarAnalysis analysis, HashSet<Mesh> swaps)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter ? filter.sharedMesh : null;
            if (!mesh || swaps.Contains(mesh) || renderer.additionalVertexStreams || renderer.HasPropertyBlock() || mesh.blendShapeCount > 0 || mesh.GetBonesPerVertex().Length > 0) return false;
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || analysis.ActivenessAnimated(renderer.gameObject)) return false;
            if (analysis.IsAnimated(renderer) || analysis.IsAnimated(filter) || analysis.ReferencesTo(renderer).Count > 0 || analysis.ReferencesTo(filter).Count > 0) return false;
            if (!RigidAccessoryConverter.UnitScaleForever(renderer.transform, analysis)) return false;
            var materials = renderer.sharedMaterials;
            if (materials.Length != mesh.subMeshCount || Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) return false;
            if (materials.Any(m => !SkinnedMeshMerger.OrderIndependent(m) || !RigidAccessoryConverter.ObjectSpaceSafe(m) || SkinnedMeshMerger.ReadsVertexId(m))) return false;
            foreach (var attribute in new[] { VertexAttribute.Position, VertexAttribute.Normal, VertexAttribute.Tangent })
                if (mesh.HasVertexAttribute(attribute) && mesh.GetVertexAttributeFormat(attribute) != VertexAttributeFormat.Float32) return false;
            bool probes = renderer.lightProbeUsage != LightProbeUsage.Off || renderer.reflectionProbeUsage != ReflectionProbeUsage.Off;
            return !probes || renderer.probeAnchor;
        }

        private static string Key(MeshRenderer r)
        {
            var mesh = r.GetComponent<MeshFilter>().sharedMesh;
            string layout = string.Join(",", mesh.GetVertexAttributes().Select(d => d.attribute + ":" + d.format + ":" + d.dimension + ":" + d.stream));
            return layout + "#" + r.gameObject.layer + "#" + r.shadowCastingMode + "#" + r.receiveShadows + "#" + r.lightProbeUsage + "#" +
                r.reflectionProbeUsage + "#" + (r.probeAnchor ? r.probeAnchor.GetInstanceID() : 0) + "#" +
                (r.lightProbeProxyVolumeOverride ? r.lightProbeProxyVolumeOverride.GetInstanceID() : 0) + "#" + r.motionVectorGenerationMode + "#" +
                r.allowOcclusionWhenDynamic + "#" + r.renderingLayerMask + "#" + r.sortingLayerID + "#" + r.sortingOrder + "#" + r.rendererPriority + "#" +
                r.gameObject.tag + "#" + r.staticShadowCaster;
        }

        private static void Merge(MeshRenderer host, List<MeshRenderer> members, Action<Object, Object> register)
        {
            var parts = new[] { host }.Concat(members).ToList();
            var meshes = parts.Select(p => p.GetComponent<MeshFilter>().sharedMesh).ToList();
            int total = meshes.Sum(m => m.vertexCount);
            var hostMesh = meshes[0];
            var copy = new Mesh { name = hostMesh.name, indexFormat = total > 65535 ? IndexFormat.UInt32 : hostMesh.indexFormat };
            var attributes = hostMesh.GetVertexAttributes();
            copy.SetVertexBufferParams(total, attributes);
            // Raw vertex data first (every attribute in its own format), then positions, normals and tangents moved.
            foreach (int stream in attributes.Select(d => d.stream).Distinct())
            {
                var bytes = new List<byte>();
                foreach (var mesh in meshes)
                    using (var array = UnityEditor.MeshUtility.AcquireReadOnlyMeshData(mesh))
                        bytes.AddRange(array[0].GetVertexData<byte>(stream).ToArray());
                copy.SetVertexBufferData(bytes.ToArray(), 0, 0, bytes.Count, stream, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            }
            var positions = new List<Vector3>(); var normals = new List<Vector3>(); var tangents = new List<Vector4>();
            for (int i = 0; i < parts.Count; i++)
            {
                var m = host.transform.worldToLocalMatrix * parts[i].transform.localToWorldMatrix;
                var mesh = meshes[i];
                positions.AddRange(i == 0 ? mesh.vertices : mesh.vertices.Select(v => m.MultiplyPoint3x4(v)));
                if (mesh.HasVertexAttribute(VertexAttribute.Normal)) normals.AddRange(i == 0 ? mesh.normals : mesh.normals.Select(n => m.MultiplyVector(n)));
                if (mesh.HasVertexAttribute(VertexAttribute.Tangent))
                    tangents.AddRange(i == 0 ? mesh.tangents : mesh.tangents.Select(t => { var d = m.MultiplyVector(t); return new Vector4(d.x, d.y, d.z, t.w); }));
            }
            copy.SetVertices(positions);
            if (normals.Count == total) copy.SetNormals(normals);
            if (tangents.Count == total) copy.SetTangents(tangents);

            copy.subMeshCount = meshes.Sum(m => m.subMeshCount);
            int submesh = 0, offset = 0;
            foreach (var mesh in meshes)
            {
                for (int s = 0; s < mesh.subMeshCount; s++)
                    copy.SetIndices(mesh.GetIndices(s, true).Select(i => i + offset).ToArray(), MeshTopology.Triangles, submesh++, false, 0);
                offset += mesh.vertexCount;
            }
            copy.RecalculateBounds();
            MeshAndAudioOptimizer.KeepUvDensity(copy, meshes);
            register(hostMesh, copy);
            var materials = parts.SelectMany(p => p.sharedMaterials).ToArray();
            host.GetComponent<MeshFilter>().sharedMesh = copy;
            host.sharedMaterials = materials;
            foreach (var member in members)
            {
                var filter = member.GetComponent<MeshFilter>();
                register(member, host);
                Object.DestroyImmediate(member);
                Object.DestroyImmediate(filter);
            }
        }
    }
}
