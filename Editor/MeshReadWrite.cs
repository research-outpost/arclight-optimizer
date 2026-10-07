using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Meshes this build made (merged, stripped or otherwise copied) are created readable, so the uploaded avatar would keep a
    // second, CPU-side copy of each in memory that nothing on an avatar reads: skinning, blend shapes and drawing use the GPU
    // copy. Their Read/Write flag goes off at the end of Arclight's passes, on upload builds only, and never when d4rk Avatar
    // Optimizer may run afterwards: its mesh merge reads vertices, normals, UVs and indices and fails on a non-readable mesh (an
    // upload failed this way on 1.1.13). Left readable: source assets (never edited), meshes Cloth simulates or a
    // particle system emits from or draws (both read the CPU copy at runtime), and anything under Arclight Exclude.
    internal static class MeshReadWrite
    {
        // Meshes already on the avatar when the build starts that are not asset files: scene-stored meshes (ProBuilder, scene
        // tools) the build clone shares with the user's scene. They are source objects, so their flag is never changed.
        internal static HashSet<Mesh> Record(GameObject root) => new HashSet<Mesh>(
            root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh)
                .Concat(root.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh))
                .Where(m => m && AssetDatabase.GetAssetPath(m).Length == 0));

        // afterD4rk: called from the post-d4rk hook on d4rk's final meshes; only meshes eligible accepts (build-owned assets) are touched.
        internal static int Run(GameObject root, ICollection<Mesh> sourceMeshes = null, bool afterD4rk = false, System.Func<Mesh, bool> eligible = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return 0;
            if (!afterD4rk && D4rkOrdering.MayRun(root)) return 0; // d4rk reads these meshes after Arclight.
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
            // A MeshCollider can cook its mesh at runtime (negative scale, non-default cooking options), which needs it readable.
            particleMeshes.UnionWith(root.GetComponentsInChildren<MeshCollider>(true).Select(c => c.sharedMesh).Where(m => m));
            int count = 0;
            foreach (var mesh in renderers.Select(MeshOf).Where(m => m).Distinct())
            {
                if (!mesh.isReadable || !(eligible != null ? eligible(mesh) : BuildOwned(mesh)) || particleMeshes.Contains(mesh) || sourceMeshes != null && sourceMeshes.Contains(mesh)) continue;
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
