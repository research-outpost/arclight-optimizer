using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Meshes this build made (merged, stripped or otherwise copied) are created readable, so the uploaded avatar would keep a
    // second, CPU-side copy of each in memory that nothing on an avatar reads: skinning, blend shapes and drawing use the GPU
    // copy. Their Read/Write flag goes off at the end of Arclight's passes, on upload builds only. The Editor keeps the data, so
    // d4rk and the post-d4rk passes still read them. Left readable: source assets (never edited), meshes Cloth simulates or a
    // particle system emits from or draws (both read the CPU copy at runtime), and anything under Arclight Exclude.
    internal static class MeshReadWrite
    {
        internal static int Run(GameObject root)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return 0;
            var particleMeshes = new HashSet<Mesh>();
            var particleRenderers = new HashSet<Renderer>();
            foreach (var system in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var shape = system.shape;
                if (shape.mesh) particleMeshes.Add(shape.mesh);
                if (shape.meshRenderer) particleRenderers.Add(shape.meshRenderer);
                if (shape.skinnedMeshRenderer) particleRenderers.Add(shape.skinnedMeshRenderer);
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer)
                {
                    var meshes = new Mesh[renderer.meshCount];
                    renderer.GetMeshes(meshes);
                    particleMeshes.UnionWith(meshes.Where(m => m));
                }
            }
            Mesh MeshOf(Renderer r) => r is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                : r is MeshRenderer && r.GetComponent<MeshFilter>() is MeshFilter filter && filter ? filter.sharedMesh : null;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            // A mesh any protected user holds stays readable for every user.
            particleMeshes.UnionWith(renderers.Where(r => particleRenderers.Contains(r) || r.GetComponent<Cloth>() || Exclusions.Excluded(r)).Select(MeshOf).Where(m => m));
            int count = 0;
            foreach (var mesh in renderers.Select(MeshOf).Where(m => m).Distinct())
            {
                if (!mesh.isReadable || !BuildOwned(mesh) || particleMeshes.Contains(mesh)) continue;
                using (var serialized = new SerializedObject(mesh))
                {
                    var readable = serialized.FindProperty("m_IsReadable");
                    if (readable == null) continue;
                    readable.boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                count++;
            }
            return count;
        }

        // Made during this build: in memory, or in NDMF's generated assets. Never a source asset.
        private static bool BuildOwned(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            return path.Length == 0 || path.StartsWith("Packages/nadena.dev.ndmf/__Generated/", System.StringComparison.Ordinal);
        }
    }
}
