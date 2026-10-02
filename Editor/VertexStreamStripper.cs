using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Removes tangents, vertex colours and UV1-UV7 from a mesh when no material that can ever render it uses them.
    // PC builds only: a stripped channel is read by the shader as Unity's default value, verified on Direct3D 11.
    //  - lilToon 2.3.4 standard entries (opaque, cutout, transparent, one/two-pass, overlay, outline and
    //    outline-only): tangents only feed normal maps, anisotropy, parallax, matcap custom normals, emission
    //    parallax, decals (mirror/left/right handling) and the outline vector (any non-zero Vector Scale, or vertex
    //    colour as the outline direction). Outline entries read vertex colour only when Vertex Color is used for the
    //    outline. Every lilToon entry reads UV1-UV3 and the ID mask may read UV4-UV7, so lilToon UVs are kept.
    //  - Other shaders: a channel is kept if any pass that can render declares it as a vertex input, for the
    //    material's keywords alone, with each global keyword, and with all of them (Meta and Never passes excepted).
    // Kept regardless: tangents when a normal map is assigned and vertex colours when the VRChat fallback can be a
    // particle or sprite shader (what others see with shaders blocked); everything for missing or broken shaders.
    // Meshes that anything other than a MeshFilter or SkinnedMeshRenderer references, that Cloth simulates, or that
    // an animation swaps in are left alone, as is every mesh on an avatar using d4rk Avatar Optimizer, whose merge
    // fills missing tangents with zeros. Animated lilToon switches count as on.
    internal static class VertexStreamStripper
    {
        internal const string AuditedLilToon = "2.3.4";
        internal static readonly VertexAttribute[] Optional =
        {
            VertexAttribute.Tangent, VertexAttribute.Color, VertexAttribute.TexCoord1, VertexAttribute.TexCoord2, VertexAttribute.TexCoord3,
            VertexAttribute.TexCoord4, VertexAttribute.TexCoord5, VertexAttribute.TexCoord6, VertexAttribute.TexCoord7
        };
        private static readonly HashSet<string> LilToonEntries = new HashSet<string>(
            ("lts lts_o lts_oo lts_cutout lts_cutout_o lts_cutout_oo lts_trans lts_trans_o lts_trans_oo " +
             "lts_onetrans lts_onetrans_o lts_twotrans lts_twotrans_o lts_overlay lts_overlay_one")
            .Split(' ').Select(name => name + ".shader"), StringComparer.Ordinal);
        // lilToon properties whose non-zero value makes a material read tangents.
        private static readonly string[] TangentSwitches = { "_UseBumpMap", "_UseBump2ndMap", "_UseAnisotropy", "_UseParallax" };
        private static readonly (string Toggle, string Value)[] TangentPairs =
        {
            ("_UseMatCap", "_MatCapCustomNormal"), ("_UseMatCap2nd", "_MatCap2ndCustomNormal"),
            ("_UseEmission", "_EmissionParallaxDepth"), ("_UseEmission2nd", "_Emission2ndParallaxDepth"),
            ("_UseMain2ndTex", "_Main2ndTexIsDecal"), ("_UseMain3rdTex", "_Main3rdTexIsDecal")
        };
        private static readonly Dictionary<string, HashSet<VertexAttribute>> CompiledInputs = new Dictionary<string, HashSet<VertexAttribute>>();

        // Animated material properties (by name, on any renderer) and object-curve bindings with their values.
        internal sealed class AnimationInfo
        {
            public readonly HashSet<string> Properties = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<EditorCurveBinding> Bindings = new List<EditorCurveBinding>();
            public readonly List<(EditorCurveBinding Binding, UnityEngine.Object[] Values)> Objects = new List<(EditorCurveBinding, UnityEngine.Object[])>();

            internal void Add(EditorCurveBinding binding, UnityEngine.Object[] values)
            {
                Bindings.Add(binding);
                if (values != null) Objects.Add((binding, values));
                string name = binding.propertyName ?? "";
                if (name.StartsWith("material.", StringComparison.Ordinal)) Properties.Add(name.Substring(9).Split('.')[0]);
            }
        }

        internal sealed class Result { public int Meshes; public long Bytes; }

        internal static Result Run(GameObject root, AnimationInfo animation, Action<UnityEngine.Object, UnityEngine.Object> register)
        {
            var result = new Result();
            if (!GeneratedTargetValidator.IsStandalone || animation == null) return result;
            var components = root.GetComponentsInChildren<Component>(true).Where(c => c).ToArray();
            if (components.Any(c => c.GetType().Name == "d4rkAvatarOptimizer")) return result;

            var users = new Dictionary<Mesh, List<Renderer>>();
            var blocked = new HashSet<Mesh>();
            foreach (var component in components)
            {
                if (component is SkinnedMeshRenderer skinned && skinned.sharedMesh) Add(users, skinned.sharedMesh, skinned);
                else if (component is MeshFilter filter && filter.sharedMesh)
                {
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer) Add(users, filter.sharedMesh, renderer); else blocked.Add(filter.sharedMesh);
                }
                if (component is Cloth cloth && cloth.GetComponent<SkinnedMeshRenderer>()?.sharedMesh is Mesh simulated) blocked.Add(simulated);
                if (component is Transform || component is MeshFilter || component is SkinnedMeshRenderer) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Mesh referenced)
                            blocked.Add(referenced);
                }
            }
            foreach (var entry in animation.Objects)
                foreach (var value in entry.Values)
                    if (value is Mesh swapped) blocked.Add(swapped);

            foreach (var pair in users)
            {
                var mesh = pair.Key;
                if (blocked.Contains(mesh)) continue;
                var materials = pair.Value.SelectMany(renderer => Materials(root, renderer, animation)).Distinct().ToArray();
                var strip = Optional.Where(a => mesh.HasVertexAttribute(a) && materials.All(m => !Needs(m, a, animation))).ToArray();
                if (strip.Length == 0) continue;
                long before = MeshAndAudioOptimizer.MeshBytes(mesh);
                var copy = Strip(mesh, strip);
                if (!copy) continue;
                register(mesh, copy);
                foreach (var renderer in pair.Value)
                    if (renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = copy;
                    else renderer.GetComponent<MeshFilter>().sharedMesh = copy;
                result.Meshes++;
                result.Bytes += before - MeshAndAudioOptimizer.MeshBytes(copy);
            }
            return result;
        }

        private static void Add(Dictionary<Mesh, List<Renderer>> users, Mesh mesh, Renderer renderer)
        {
            if (!users.TryGetValue(mesh, out var list)) users.Add(mesh, list = new List<Renderer>());
            list.Add(renderer);
        }

        // The renderer's slots plus every material an animation can swap into a renderer at its path (or, for
        // animators below the root, at any path ending in its name).
        private static IEnumerable<Material> Materials(GameObject root, Renderer renderer, AnimationInfo animation)
        {
            foreach (var material in renderer.sharedMaterials) yield return material;
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            foreach (var entry in animation.Objects)
            {
                var binding = entry.Binding;
                if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type) ||
                    !(binding.propertyName ?? "").StartsWith("m_Materials", StringComparison.Ordinal)) continue;
                if (binding.path != path && binding.path.Split('/').Last() != renderer.name) continue;
                foreach (var value in entry.Values) if (value is Material material) yield return material;
            }
        }

        internal static bool Needs(Material material, VertexAttribute attribute, AnimationInfo animation)
        {
            if (!material) return false;
            var shader = material.shader;
            if (!shader || !shader.isSupported || shader.name == "Hidden/InternalErrorShader") return true;
            if (attribute == VertexAttribute.Tangent && material.HasProperty("_BumpMap") &&
                (material.GetTexture("_BumpMap") || animation.Properties.Contains("_BumpMap"))) return true;
            if (attribute == VertexAttribute.Color && ParticleOrSpriteFallback(material)) return true;
            if (IsAuditedLilToon(shader, out bool outline))
            {
                bool On(string property) => material.HasProperty(property) &&
                    (material.GetFloat(property) != 0 || animation.Properties.Contains(property));
                switch (attribute)
                {
                    case VertexAttribute.Tangent:
                        return TangentSwitches.Any(On) || TangentPairs.Any(p => On(p.Toggle) && On(p.Value)) ||
                            outline && (On("_OutlineVectorScale") || On("_OutlineVertexR2Width"));
                    case VertexAttribute.Color:
                        return outline && On("_OutlineVertexR2Width");
                    default:
                        return true;
                }
            }
            return Inputs(material).Contains(attribute);
        }

        private static bool ParticleOrSpriteFallback(Material material)
        {
            string fallback = (material.GetTag("VRCFallback", false, "") + " " + material.shader.name).ToLowerInvariant();
            return fallback.Contains("particle") || fallback.Contains("sprite");
        }

        private static bool IsAuditedLilToon(Shader shader, out bool outline)
        {
            outline = false;
            string path = AssetDatabase.GetAssetPath(shader);
            string file = System.IO.Path.GetFileName(path);
            if (path != LilToonSourceGuard.Root + "/Shader/" + file || !LilToonEntries.Contains(file)) return false;
            if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath(LilToonSourceGuard.Root + "/package.json")?.version != AuditedLilToon)
                return false;
            outline = file.EndsWith("_o.shader", StringComparison.Ordinal) || file.EndsWith("_oo.shader", StringComparison.Ordinal);
            return true;
        }

        // Vertex inputs declared by any pass that can draw this material on PC.
        internal static HashSet<VertexAttribute> Inputs(Material material)
        {
            var shader = material.shader;
            string key = shader.GetInstanceID() + "|" + string.Join(" ", material.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal));
            if (CompiledInputs.TryGetValue(key, out var cached)) return cached;
            var inputs = new HashSet<VertexAttribute>();
            try
            {
                var data = ShaderUtil.GetShaderData(shader);
                var subshader = data.ActiveSubshader;
                for (int p = 0; p < subshader.PassCount; p++)
                {
                    var pass = subshader.GetPass(p);
                    string mode = pass.FindTagValue(new ShaderTagId("LightMode")).name ?? "";
                    if (mode.Equals("Meta", StringComparison.OrdinalIgnoreCase) || mode == "Never" || !pass.HasShaderStage(ShaderType.Vertex)) continue;
                    var globals = ShaderUtil.GetPassKeywords(shader, new PassIdentifier((uint)data.ActiveSubshaderIndex, (uint)p), ShaderType.Vertex)
                        .Where(k => k.isOverridable).Select(k => k.name).ToArray();
                    var sets = new[] { new string[0], globals }.Concat(globals.Select(g => new[] { g }));
                    foreach (var extra in sets)
                    {
                        var info = pass.CompileVariant(ShaderType.Vertex, material.shaderKeywords.Concat(extra).ToArray(),
                            ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                        if (info.Success) inputs.UnionWith(info.Attributes);
                        else if (!ReferenceEquals(extra, globals)) inputs.UnionWith(Optional); // All globals at once may conflict.
                    }
                }
            }
            catch (Exception) { inputs.UnionWith(Optional); }
            CompiledInputs[key] = inputs;
            return inputs;
        }

        // A copy without the given channels, or null if any other data changed.
        internal static Mesh Strip(Mesh mesh, VertexAttribute[] channels)
        {
            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name;
            foreach (var channel in channels)
            {
                if (channel == VertexAttribute.Tangent) copy.tangents = null;
                else if (channel == VertexAttribute.Color) copy.colors32 = null;
                else copy.SetUVs(channel - VertexAttribute.TexCoord0, (Vector2[])null);
            }
            copy.bounds = mesh.bounds;
            if (SameExcept(mesh, copy, channels)) return copy;
            UnityEngine.Object.DestroyImmediate(copy);
            return null;
        }

        private static bool SameExcept(Mesh a, Mesh b, VertexAttribute[] removed)
        {
            var expected = a.GetVertexAttributes().Where(d => !removed.Contains(d.attribute)).Select(d => (d.attribute, d.format, d.dimension));
            var actual = b.GetVertexAttributes().Select(d => (d.attribute, d.format, d.dimension));
            if (!new HashSet<(VertexAttribute, VertexAttributeFormat, int)>(expected).SetEquals(actual) || b.vertexCount != a.vertexCount) return false;
            if (removed.Any(b.HasVertexAttribute)) return false;
            if (!a.vertices.SequenceEqual(b.vertices) || !a.normals.SequenceEqual(b.normals) || a.bounds != b.bounds) return false;
            if (!removed.Contains(VertexAttribute.Tangent) && !a.tangents.SequenceEqual(b.tangents)) return false;
            if (!removed.Contains(VertexAttribute.Color) && !a.colors.SequenceEqual(b.colors)) return false;
            for (int i = 0; i < 8; i++)
            {
                if (removed.Contains(VertexAttribute.TexCoord0 + i)) continue;
                List<Vector4> x = new List<Vector4>(), y = new List<Vector4>();
                a.GetUVs(i, x); b.GetUVs(i, y);
                if (!x.SequenceEqual(y)) return false;
            }
            if (!a.GetBonesPerVertex().SequenceEqual(b.GetBonesPerVertex()) || !a.GetAllBoneWeights().SequenceEqual(b.GetAllBoneWeights()) ||
                !a.bindposes.SequenceEqual(b.bindposes)) return false;
            if (a.indexFormat != b.indexFormat || a.subMeshCount != b.subMeshCount) return false;
            for (int i = 0; i < a.subMeshCount; i++)
            {
                var x = a.GetSubMesh(i); var y = b.GetSubMesh(i);
                if (x.topology != y.topology || x.indexStart != y.indexStart || x.indexCount != y.indexCount || x.baseVertex != y.baseVertex ||
                    !a.GetIndices(i, false).SequenceEqual(b.GetIndices(i, false))) return false;
            }
            if (a.blendShapeCount != b.blendShapeCount) return false;
            var shapes = new[] { new Vector3[a.vertexCount], new Vector3[a.vertexCount], new Vector3[a.vertexCount],
                new Vector3[a.vertexCount], new Vector3[a.vertexCount], new Vector3[a.vertexCount] };
            for (int s = 0; s < a.blendShapeCount; s++)
            {
                if (a.GetBlendShapeName(s) != b.GetBlendShapeName(s) || a.GetBlendShapeFrameCount(s) != b.GetBlendShapeFrameCount(s)) return false;
                for (int f = 0; f < a.GetBlendShapeFrameCount(s); f++)
                {
                    if (a.GetBlendShapeFrameWeight(s, f) != b.GetBlendShapeFrameWeight(s, f)) return false;
                    a.GetBlendShapeFrameVertices(s, f, shapes[0], shapes[1], shapes[2]);
                    b.GetBlendShapeFrameVertices(s, f, shapes[3], shapes[4], shapes[5]);
                    if (!shapes[0].SequenceEqual(shapes[3]) || !shapes[1].SequenceEqual(shapes[4]) ||
                        !removed.Contains(VertexAttribute.Tangent) && !shapes[2].SequenceEqual(shapes[5])) return false;
                }
            }
            return true;
        }
    }
}
