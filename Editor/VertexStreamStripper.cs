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
    // On PC, a stripped channel is read by the shader as Unity's default value, verified on Direct3D 11. On Android only
    // channels that no pass declares for Vulkan or OpenGL ES 3 are removed, so no default value is ever read there.
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
        // Versions whose Built-in pipeline shader sources match 2.3.4 at every audited point. 2.3.2 differs only in a
        // URP-only additional-light mode, URP probe-volume skip_variants pragmas and two decal property declarations
        // (diffed 2026-10-03): no vertex input, object-space read, ID mask, render state or sampling guard changes.
        internal static readonly HashSet<string> AuditedLilToonVersions = new HashSet<string>(StringComparer.Ordinal) { "2.3.4", "2.3.2" };
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

        // Compile results held in memory last one build: shaders without a disk key (made in memory or by an importer) can change
        // without their key changing, and a result that depended on a failed compile is used for that build only.
        internal static void NewBuild()
        {
            CompiledInputs.Clear();
            CompiledUniforms.Clear();
            SkinnedMeshMerger.VertexIdReaders.Clear();
            DegenerateTriangleRemover.Safe.Clear();
        }

        internal sealed class Result { public int Meshes; public long Bytes; }

        internal static Result Run(AvatarAnalysis analysis, Action<UnityEngine.Object, UnityEngine.Object> register)
        {
            var result = new Result();
            if (!GeneratedTargetValidator.IsStandalone && !Android || !analysis.Complete) return result;
            var root = analysis.Root;
            var components = root.GetComponentsInChildren<Component>(true).Where(c => c).ToArray();
            if (components.Any(c => c.GetType().Name == "d4rkAvatarOptimizer")) return result;

            var users = new Dictionary<Mesh, List<Renderer>>();
            var blocked = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            foreach (var component in components)
            {
                if (component is SkinnedMeshRenderer skinned && skinned.sharedMesh) Add(users, skinned.sharedMesh, skinned);
                else if (component is MeshFilter filter && filter.sharedMesh)
                {
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer) Add(users, filter.sharedMesh, renderer); else blocked.Add(filter.sharedMesh);
                }
                if (component is Cloth cloth && cloth.GetComponent<SkinnedMeshRenderer>()?.sharedMesh is Mesh simulated) blocked.Add(simulated);
            }
            foreach (var mesh in users.Keys)
                if (analysis.ReferencesTo(mesh).Any(c => !(c is MeshFilter) && !(c is SkinnedMeshRenderer))) blocked.Add(mesh);
            foreach (var pair in users) if (pair.Value.Any(Exclusions.Excluded)) blocked.Add(pair.Key);

            foreach (var pair in users)
            {
                var mesh = pair.Key;
                if (blocked.Contains(mesh)) continue;
                var materials = pair.Value.SelectMany(r => r.sharedMaterials.Concat(analysis.SwappedMaterials(r))).Distinct().ToArray();
                var animated = new HashSet<string>(pair.Value.SelectMany(analysis.AnimatedMaterialProperties), StringComparer.Ordinal);
                var strip = Optional.Where(a => mesh.HasVertexAttribute(a) && materials.All(m => !Needs(m, a, animated.Contains))).ToArray();
                // lilToon declares every UV input as float2 (lil_common_appdata.hlsl), so on a mesh only audited lilToon entries draw
                // (VRChat's non-particle fallbacks read UVs as float2 too), the third and fourth components of a 32-bit UV channel
                // are never read.
                var narrow = !Android && materials.Length > 0 && materials.All(m => m && m.shader && IsAuditedLilToon(m.shader, out _) && !ParticleOrSpriteFallback(m))
                    ? Enumerable.Range(0, 8).Select(i => VertexAttribute.TexCoord0 + i).Where(a => !strip.Contains(a) && mesh.HasVertexAttribute(a) &&
                        mesh.GetVertexAttributeDimension(a) > 2 && mesh.GetVertexAttributeFormat(a) == VertexAttributeFormat.Float32).ToArray()
                    : new VertexAttribute[0];
                if (strip.Length == 0 && narrow.Length == 0) continue;
                long before = MeshAndAudioOptimizer.MeshBytes(mesh);
                var copy = Strip(mesh, strip, narrow);
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

        // animated: material properties an animation can change on a renderer drawing this material.
        internal static bool Needs(Material material, VertexAttribute attribute, Func<string, bool> animated)
        {
            if (!material) return false;
            var shader = material.shader;
            if (!shader || !shader.isSupported || shader.name == "Hidden/InternalErrorShader") return true;
            if (attribute == VertexAttribute.Tangent && material.HasProperty("_BumpMap") &&
                (material.GetTexture("_BumpMap") || animated("_BumpMap"))) return true;
            if (attribute == VertexAttribute.Color && ParticleOrSpriteFallback(material)) return true;
            // The lilToon rules rely on a stripped channel reading as Unity's default, verified on Direct3D 11 only. On Android
            // (where lilToon cannot be uploaded anyway) only channels no compiled pass declares are removed.
            if (!Android && IsAuditedLilToon(shader, out bool outline))
            {
                bool On(string property) => material.HasProperty(property) &&
                    (material.GetFloat(property) != 0 || animated(property));
                switch (attribute)
                {
                    case VertexAttribute.Tangent:
                        return TangentSwitches.Any(On) || TangentPairs.Any(p => On(p.Toggle) && On(p.Value)) ||
                            outline && (On("_OutlineVectorScale") || On("_OutlineVertexR2Width"));
                    case VertexAttribute.Color:
                        return outline && On("_OutlineVertexR2Width");
                    // UV4 to UV7 are read only by the ID mask's source switch (lil_common_vert.hlsl and lil_common_vert_fur.hlsl,
                    // "switch(_IDMaskFrom)"; the LightMode Never pass that lists every input is never drawn), and VRChat's
                    // fallback shaders read UV0 and UV1 at most.
                    case VertexAttribute.TexCoord4: case VertexAttribute.TexCoord5: case VertexAttribute.TexCoord6: case VertexAttribute.TexCoord7:
                        if (!material.HasProperty("_IDMaskFrom") || animated("_IDMaskFrom")) return true;
                        float from = material.GetFloat("_IDMaskFrom");
                        return from != Mathf.Round(from) || (int)from == attribute - VertexAttribute.TexCoord0;
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

        internal static bool IsAuditedLilToonShader(Shader shader, out bool outline) => IsAuditedLilToon(shader, out outline);

        private static bool IsAuditedLilToon(Shader shader, out bool outline)
        {
            outline = false;
            string path = AssetDatabase.GetAssetPath(shader);
            string file = System.IO.Path.GetFileName(path);
            if (path != LilToonSourceGuard.Root + "/Shader/" + file || !LilToonEntries.Contains(file)) return false;
            if (!(UnityEditor.PackageManager.PackageInfo.FindForAssetPath(LilToonSourceGuard.Root + "/package.json")?.version is string version) || !AuditedLilToonVersions.Contains(version))
                return false;
            outline = file.EndsWith("_o.shader", StringComparison.Ordinal) || file.EndsWith("_oo.shader", StringComparison.Ordinal);
            return true;
        }

        // Global keywords the Built-in render pipeline sets on avatars in VRChat (lights, shadows, probes, lightmaps, fog,
        // instancing and stereo rendering).
        // Keyword sets the Built-in pipeline turns on together in VRChat (forward base and add lights with shadows, probes,
        // vertex lights and fog), for uniforms a shader declares only when two of them are on.
        private static readonly string[][] LightingSets =
        {
            new[] { "DIRECTIONAL", "LIGHTPROBE_SH" }, new[] { "DIRECTIONAL", "SHADOWS_SCREEN" }, new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "SHADOWS_SCREEN" },
            new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "VERTEXLIGHT_ON" }, new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "SHADOWS_SCREEN", "VERTEXLIGHT_ON" },
            new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "FOG_LINEAR" }, new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "FOG_EXP2" },
            new[] { "POINT", "SHADOWS_CUBE" }, new[] { "SPOT", "SHADOWS_DEPTH" }, new[] { "POINT_COOKIE", "SHADOWS_CUBE" }, new[] { "DIRECTIONAL_COOKIE", "SHADOWS_SCREEN" },
        };

        private static readonly string[] BuiltInGlobals =
            ("DIRECTIONAL DIRECTIONAL_COOKIE POINT POINT_COOKIE SPOT SHADOWS_SCREEN SHADOWS_DEPTH SHADOWS_CUBE SHADOWS_SOFT " +
             "LIGHTPROBE_SH VERTEXLIGHT_ON LIGHTMAP_ON DIRLIGHTMAP_COMBINED DYNAMICLIGHTMAP_ON LIGHTMAP_SHADOW_MIXING SHADOWS_SHADOWMASK " +
             "FOG_LINEAR FOG_EXP FOG_EXP2 INSTANCING_ON UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON UNITY_HDR_ON").Split(' ');

        internal static bool Android => ForceAndroid ?? EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
        internal static bool? ForceAndroid; // Tests: compile for Android without switching the build target.

        // The graphics APIs the build can run on: Direct3D on PC; Vulkan and OpenGL ES 3 on Android (a channel is kept if either
        // declares it).
        private static (ShaderCompilerPlatform, BuildTarget)[] Targets => Android
            ? new[] { (ShaderCompilerPlatform.Vulkan, BuildTarget.Android), (ShaderCompilerPlatform.GLES3x, BuildTarget.Android) }
            : new[] { (ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64) };

        // Vertex inputs declared by any pass that can draw this material on the build's graphics APIs.
        internal static HashSet<VertexAttribute> Inputs(Material material)
        {
            var shader = material.shader;
            string key = CacheKey(material);
            if (CompiledInputs.TryGetValue(key, out var cached)) return cached;
            var inputs = new HashSet<VertexAttribute>();
            if (SkinnedMeshMerger.Broken(shader)) { inputs.UnionWith(Optional); return CompiledInputs[key] = inputs; } // Keep everything.
            string fact = ShaderFacts.Key(material, Android ? "android" : "d3d", "inputs|" + CompileTables);
            if (ShaderFacts.TryGet("inputs", fact, out string known))
                return CompiledInputs[key] = new HashSet<VertexAttribute>(known.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => (VertexAttribute)int.Parse(s)));
            bool clean = true; // Only a result from a complete, normal compile is kept on disk.
            bool hadError = ShaderUtil.ShaderHasError(shader); // A failed variant compile records an error on the shader; cleared below.
            try
            {
                string[] globalKeywords = null;
                var data = ShaderUtil.GetShaderData(shader);
                var subshader = data.ActiveSubshader;
                for (int p = 0; p < subshader.PassCount; p++)
                {
                    var pass = subshader.GetPass(p);
                    string mode = pass.FindTagValue(new ShaderTagId("LightMode")).name ?? "";
                    if (mode.Equals("Meta", StringComparison.OrdinalIgnoreCase) || mode == "Never" || !pass.HasShaderStage(ShaderType.Vertex)) continue;
                    // The global keywords the shader declares, from its whole keyword space: a superset of each pass's own (a pass
                    // ignores a keyword it does not declare). Asking per pass (ShaderUtil.GetPassKeywords) logs "Invalid pass
                    // identifier" for passes a shader takes from others, and "Snippet not found" when keyword data is not loaded.
                    // The Built-in pipeline's own global keywords are always added, so a space that comes back short (keyword data not
                    // loaded) cannot hide a variant.
                    string[] globals;
                    try { globals = globalKeywords ?? (globalKeywords = shader.keywordSpace.keywords.Where(k => k.isOverridable).Select(k => k.name).Union(BuiltInGlobals).ToArray()); }
                    catch (Exception) { globals = BuiltInGlobals; clean = false; }
                    // The lighting sets VRChat turns on together cover keyword combinations (a shadowed point light) one global alone misses.
                    var sets = new[] { new string[0], globals }.Concat(globals.Select(g => new[] { g })).Concat(LightingSets);
                    foreach (var extra in sets)
                    {
                        foreach (var (platform, target) in Targets)
                        {
                            var info = pass.CompileVariant(ShaderType.Vertex, material.shaderKeywords.Concat(extra).ToArray(), platform, target);
                            // A list without Position is not trusted to be complete (a reflection gap would strip every channel).
                            if (info.Success && info.Attributes != null && info.Attributes.Contains(VertexAttribute.Position)) inputs.UnionWith(info.Attributes);
                            else if (info.Success) { inputs.UnionWith(Optional); clean = false; }
                            else if (!ReferenceEquals(extra, globals)) { inputs.UnionWith(Optional); clean = false; } // All globals at once may conflict.
                        }
                    }
                }
            }
            catch (Exception) { inputs.UnionWith(Optional); clean = false; }
            finally { if (!hadError && ShaderUtil.ShaderHasError(shader)) ShaderUtil.ClearShaderMessages(shader); }
            if (clean) ShaderFacts.Put("inputs", fact, string.Join(" ", inputs.Select(a => ((int)a).ToString())));
            CompiledInputs[key] = inputs;
            return inputs;
        }

        private static readonly Dictionary<string, HashSet<string>> CompiledUniforms = new Dictionary<string, HashSet<string>>();

        // The code tables the compiled facts depend on; part of their disk key, so changing a table recompiles.
        private static string compileTables;
        private static string CompileTables => compileTables ?? (compileTables = FingerprintService.Hash(System.Text.Encoding.UTF8.GetBytes(
            string.Join(" ", BuiltInGlobals) + "|" + string.Join(";", LightingSets.Select(s => string.Join(" ", s))) + "|" + string.Join(" ", Optional) +
            "|vertex-inputs: no meta or never, shader and built-in globals each alone, all at once and each lighting set|uniforms and texture bindings: vertex fragment geometry hull domain, each global alone and each lighting set")));

        // Keyed by the shader file's dependency hash (its includes too), so a shader edited since the last build is compiled
        // again while unchanged shaders are not.
        internal static string CacheKey(Material material) => (Android ? "android|" : "") + material.shader.GetInstanceID() + "|" +
            AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(material.shader)) + "|" +
            string.Join(" ", material.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal));

        // Names of every uniform any stage of any pass reads, in each variant the material can draw with on PC (its
        // keywords alone, with each global keyword, and with the lighting sets above), or null if any variant fails to
        // compile or cannot be listed.
        internal static HashSet<string> Uniforms(Material material)
        {
            var shader = material.shader;
            string key = CacheKey(material);
            if (CompiledUniforms.TryGetValue(key, out var cached)) return cached;
            string fact = ShaderFacts.Key(material, "d3d", "uniforms|" + CompileTables);
            if (ShaderFacts.TryGet("uniforms", fact, out string known))
                return CompiledUniforms[key] = new HashSet<string>(known.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            // A variant that fails to compile records an error on the shader, which other tools (VRCFury's upload check)
            // then read as a broken material; errors this check caused are cleared again (with any earlier warnings, which
            // change nothing).
            bool hadError = ShaderUtil.ShaderHasError(shader);
            try
            {
                var data = ShaderUtil.GetShaderData(shader);
                var subshader = data.ActiveSubshader;
                var stages = new[] { ShaderType.Vertex, ShaderType.Fragment, ShaderType.Geometry, ShaderType.Hull, ShaderType.Domain };
                void Add(ShaderData.ConstantInfo[] fields)
                {
                    foreach (var field in fields ?? Array.Empty<ShaderData.ConstantInfo>())
                    {
                        names.Add(field.Name);
                        Add(field.StructFields);
                    }
                }
                for (int p = 0; p < subshader.PassCount; p++)
                {
                    var pass = subshader.GetPass(p);
                    foreach (var stage in stages.Where(pass.HasShaderStage))
                    {
                        // Each global keyword the Built-in pipeline sets, one at a time (asking each stage for its own list logs
                        // errors, and all at once defines conflicting light textures, an error Unity then keeps on the shader).
                        var globals = BuiltInGlobals;
                        foreach (var extra in new[] { new string[0] }.Concat(globals.Select(g => new[] { g })).Concat(LightingSets))
                        {
                            var info = pass.CompileVariant(stage, material.shaderKeywords.Concat(extra).ToArray(), ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                            if (info.Success)
                            {
                                foreach (var buffer in info.ConstantBuffers ?? Array.Empty<ShaderData.ConstantBufferInfo>()) Add(buffer.Fields);
                                // Textures the variant binds, by name: a saved texture no variant binds is never read.
                                foreach (var texture in info.TextureBindings ?? Array.Empty<ShaderData.TextureBindingInfo>()) names.Add(texture.Name);
                            }
                            // A failed variant may be one VRChat draws (the failure can be a one-off), so the check stops: every value stays.
                            else return CompiledUniforms[key] = null;
                        }
                    }
                }
            }
            catch (Exception) { return CompiledUniforms[key] = null; }
            finally { if (!hadError && ShaderUtil.ShaderHasError(shader)) ShaderUtil.ClearShaderMessages(shader); }
            ShaderFacts.Put("uniforms", fact, string.Join(" ", names)); // A failed check (null) is not kept.
            return CompiledUniforms[key] = names;
        }

        // A copy without the given channels, or null if any other data changed.
        // narrow: UV channels kept with their first two components only.
        internal static Mesh Strip(Mesh mesh, VertexAttribute[] channels, VertexAttribute[] narrow = null)
        {
            narrow = narrow ?? new VertexAttribute[0];
            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name;
            foreach (var channel in channels)
            {
                if (channel == VertexAttribute.Tangent) copy.tangents = null;
                else if (channel == VertexAttribute.Color) copy.colors32 = null;
                else copy.SetUVs(channel - VertexAttribute.TexCoord0, (Vector2[])null);
            }
            foreach (var channel in narrow)
            {
                var uvs = new List<Vector2>();
                mesh.GetUVs(channel - VertexAttribute.TexCoord0, uvs);
                copy.SetUVs(channel - VertexAttribute.TexCoord0, uvs);
            }
            copy.bounds = mesh.bounds;
            if (SameExcept(mesh, copy, channels, narrow)) return copy;
            UnityEngine.Object.DestroyImmediate(copy);
            return null;
        }

        private static bool SameExcept(Mesh a, Mesh b, VertexAttribute[] removed, VertexAttribute[] narrowed)
        {
            var expected = a.GetVertexAttributes().Where(d => !removed.Contains(d.attribute)).Select(d => (d.attribute, d.format, narrowed.Contains(d.attribute) ? 2 : d.dimension));
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
                if (narrowed.Contains(VertexAttribute.TexCoord0 + i) ? !x.Select(v => (Vector2)v).SequenceEqual(y.Select(v => (Vector2)v)) : !x.SequenceEqual(y)) return false;
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
