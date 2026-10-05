using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Merges skinned meshes that always render together into one SkinnedMeshRenderer, so the avatar skins and
    // counts one mesh instead of several. Only merges that keep every frame identical:
    //  - Same toggles: the objects whose activation an animation can change on each renderer's path to the root must
    //    be the same, or animated identically (same starting state, same clips and curves; see ToggleSignature), and
    //    nothing else on the path is hidden. The merged renderer lives on the first renderer's object, under toggles
    //    that are always in the same state as every other member's.
    //  - Untouched renderers: no animation binds to the renderer (materials, blend shapes, enabling), nothing else
    //    references it, no Cloth, its mesh is not swapped, its material count equals its submesh count.
    //  - Same frame of reference: the same root bone (skinned vertices are rendered in its space, so object-space
    //    shader effects and bounds stay put) and the same renderer settings (shadows, probes, anchors, layers,
    //    motion vectors, quality, sorting). Light and reflection probes are sampled at the anchor, or at the bounds
    //    centre when there is none, so without a shared anchor the bounds centres must already coincide.
    //  - Opaque only: every material in a queue up to 2500, where draw order between renderers does not change
    //    the image. Transparent materials sort by renderer, and merging would fix their order.
    //  - Same vertex layout: every source has the same attributes with the same dimensions, so no shader reads a
    //    filled-in value. Blend shape names must not collide.
    //  - Vertex IDs: merging renumbers vertices of every source after the first, so at most one source may use a
    //    material that reads SV_VertexID, and it goes first. lilToon reads it only for an active ID Mask.
    // Bones and bind poses are concatenated per source (a bone may repeat with a different bind pose), so every
    // vertex is skinned by exactly the same matrices as before; identical (bone, bind pose) pairs are shared.
    internal static class SkinnedMeshMerger
    {
        internal sealed class Result
        {
            public int Merged, Into;
            // Why a renderer stayed apart: the reason and the renderers it applies to.
            public readonly Dictionary<string, List<string>> WhyNot = new Dictionary<string, List<string>>();
            public readonly List<string> Hints = new List<string>();
        }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, AnimationRewriter rewriter = null)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var meshSwaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            var all = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList();
            var candidates = all.Where(r => Eligible(r, analysis, meshSwaps, rewriter != null)).ToList();
            var moves = new Dictionary<GameObject, Move>();
            var mergedAway = new HashSet<SkinnedMeshRenderer>();
            void Why(string reason, SkinnedMeshRenderer r) { if (!result.WhyNot.TryGetValue(reason, out var names)) result.WhyNot[reason] = names = new List<string>(); names.Add(r.name); }
            foreach (var r in all.Except(candidates)) Why(WhyNot(r, analysis, meshSwaps, rewriter != null), r);
            var singles = candidates.GroupBy(r => Key(r, analysis)).Where(g => g.Count() == 1).SelectMany(g => g).ToList();
            // For a renderer no other shares a key with: the nearest candidate (fewest differing parts), and whether a single part
            // is all that keeps them apart.
            var parts = candidates.ToDictionary(r => r, r => KeyParts(r, analysis));
            bool anchorOrBounds = false;
            foreach (var r in singles)
            {
                var differing = candidates.Where(o => o != r).Select(o => parts[r].Where((p, i) => p.Value != parts[o][i].Value).Select(p => p.Name).ToList())
                    .OrderBy(d => d.Count).FirstOrDefault();
                if (differing == null) Why("no other mesh can merge at all", r);
                else if (differing.Count == 1) { Why("only the " + differing[0] + " differs from its nearest partner", r); anchorOrBounds |= differing[0] == "probe anchor" || differing[0] == "Update When Offscreen setting"; }
                else Why("several settings differ from its nearest partner", r);
            }
            if (anchorOrBounds)
                result.Hints.Add("Some meshes differ from a partner only by their probe anchor or bounds setting. VRCFury's Anchor Override Fix and Bounding Box Fix, or Modular Avatar's Mesh Settings, give meshes one anchor and one bounds so Arclight can merge them; that changes lighting or culling slightly, so it is your choice.");

            // Merged members are destroyed as each group completes; their animation is retargeted even if a later group fails.
            try
            {
            foreach (var group in candidates.GroupBy(r => Key(r, analysis)).Where(g => g.Count() > 1))
            {
                // Within a key, also split by vertex layout, probe sampling and animated material properties;
                // greedy and deterministic.
                var remaining = group.OrderBy(r => AnimationUtility.CalculateTransformPath(r.transform, root.transform), StringComparer.Ordinal).ToList();
                while (remaining.Count > 1)
                {
                    var seed = remaining[0];
                    var members = new List<SkinnedMeshRenderer> { seed };
                    foreach (var r in remaining.Skip(1))
                        if (Compatible(seed, r) && PropertiesDisjoint(members, r, analysis)) members.Add(r);
                    remaining.RemoveAll(members.Contains);
                    if (members.Count < 2) continue;
                    // MMD worlds animate shapes on the root "Body" by name, so it hosts the merge and keeps its names.
                    var body = members.FirstOrDefault(r => r.name == "Body" && r.transform.parent == root.transform);
                    // At most one member may read vertex IDs; it goes first so its numbering is unchanged.
                    var readers = members.Where(r => ReadsVertexId(r, analysis)).ToList();
                    if (body && readers.Any(r => r != body)) members = members.Except(readers.Where(r => r != body)).ToList();
                    else if (readers.Count > 1) members = members.Except(readers.Skip(1)).ToList();
                    var first = body ? body : readers.FirstOrDefault(members.Contains);
                    if (first) { members.Remove(first); members.Insert(0, first); }
                    if (members.Count < 2) continue;
                    if (Merge(members, register, root, moves, body, analysis)) { result.Merged += members.Count; result.Into++; mergedAway.UnionWith(members); }
                }
            }
            }
            finally
            {
                if (moves.Count > 0) rewriter?.Rewrite((owner, binding) => Retarget(owner, binding, root, moves));
            }
            foreach (var r in candidates.Except(singles).Where(r => !mergedAway.Contains(r)))
                Why("a different vertex layout, probe sampling, animated material property or vertex-ID use than its matches", r);
            return result;
        }

        // Where one merged renderer's animation goes: the host's path, renamed blend shapes, shifted material slots.
        private sealed class Move
        {
            public string HostPath;
            public Dictionary<string, string> Shapes;
            public int SlotOffset;
        }

        private static EditorCurveBinding? Retarget(Transform owner, EditorCurveBinding binding, GameObject root, Dictionary<GameObject, Move> moves)
        {
            if (owner != root.transform || binding.type != typeof(SkinnedMeshRenderer)) return null;
            var targets = AvatarAnalysis.Resolve(owner, binding.path).ToList();
            if (targets.Count != 1 || !moves.TryGetValue(targets[0].gameObject, out var move)) return null;
            string property = binding.propertyName ?? "";
            const string shape = "blendShape.", slot = "m_Materials.Array.data[";
            if (property.StartsWith(shape, StringComparison.Ordinal) && move.Shapes.TryGetValue(property.Substring(shape.Length), out var renamed))
                property = shape + renamed;
            else if (property.StartsWith(slot, StringComparison.Ordinal) && property.EndsWith("]", StringComparison.Ordinal) &&
                     int.TryParse(property.Substring(slot.Length, property.Length - slot.Length - 1), out int index))
                property = slot + (index + move.SlotOffset) + "]";
            var moved = binding;
            moved.path = move.HostPath;
            moved.propertyName = property;
            return moved;
        }

        // Animated material properties apply to every material of a renderer; after merging they would reach the other
        // members' materials too, so members must not share a property another member animates.
        private static bool PropertiesDisjoint(List<SkinnedMeshRenderer> members, SkinnedMeshRenderer candidate, AvatarAnalysis analysis)
        {
            bool Touches(SkinnedMeshRenderer animated, SkinnedMeshRenderer other) =>
                analysis.AnimatedMaterialProperties(animated).Any(p => other.sharedMaterials.Concat(analysis.SwappedMaterials(other)).Any(m => m && m.HasProperty(p)));
            return members.All(m => !Touches(m, candidate) && !Touches(candidate, m));
        }

        // Animation of these renderer properties can be moved to the merged renderer exactly.
        private static bool Movable(AvatarAnalysis.Binding binding, GameObject root) =>
            !binding.Legacy && binding.Owner == root.transform && (binding.Property.StartsWith("blendShape.", StringComparison.Ordinal) ||
            binding.Property.StartsWith("material.", StringComparison.Ordinal) || binding.Property.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal));

        private static bool UniquePath(Transform transform, Transform root)
        {
            for (var t = transform; t != root; t = t.parent)
                if (t.parent.Cast<Transform>().Count(sibling => sibling.name == t.name) != 1) return false;
            return true;
        }

        private static bool Eligible(SkinnedMeshRenderer renderer, AvatarAnalysis analysis, HashSet<Mesh> meshSwaps, bool canMoveAnimation) =>
            WhyNot(renderer, analysis, meshSwaps, canMoveAnimation) == null;

        // Why this renderer cannot merge with any other, for the report; null when it can.
        private static string WhyNot(SkinnedMeshRenderer renderer, AvatarAnalysis analysis, HashSet<Mesh> meshSwaps, bool canMoveAnimation)
        {
            var mesh = renderer.sharedMesh;
            if (!mesh || Exclusions.Excluded(renderer)) return "no mesh, or excluded from Arclight";
            if (!renderer.enabled) return "the renderer is disabled";
            if (meshSwaps.Contains(mesh)) return "an animation swaps its mesh";
            if (renderer.GetComponent<Cloth>()) return "Cloth simulates it";
            if (TextureUsageScanner.HasPropertyBlock(renderer)) return "a material property block sets values on it";
            var bindings = analysis.BindingsOn(renderer).ToList();
            if (bindings.Count > 0 && (!canMoveAnimation || !UniquePath(renderer.transform, analysis.Root.transform) ||
                bindings.Any(b => !Movable(b, analysis.Root)))) return "an animation on the renderer cannot be moved to the merged one";
            if (analysis.ReferencesTo(renderer).Any(c => c.GetType().Name != "VRCAvatarDescriptor")) return "another component references the renderer";
            if (analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer))) return "another component uses its mesh";
            if (renderer.sharedMaterials.Length != mesh.subMeshCount) return "its material slots do not match its submeshes";
            if (renderer.sharedMaterials.Concat(analysis.SwappedMaterials(renderer)).Any(m => !OrderIndependent(m))) return "a transparent material (render queue above 2500), whose draw order would change";
            if (Enumerable.Range(0, 8).Any(i => mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + i) && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + i) == 1)) return "a one-component UV channel";
            if (Enumerable.Range(0, mesh.subMeshCount).Any(i => mesh.GetTopology(i) != MeshTopology.Triangles)) return "a submesh that is not triangles";
            if (mesh.bindposes.Length != renderer.bones.Length || renderer.bones.Any(b => !b)) return "missing bones";
            // Hidden for good is step 2's job; here every object on the path is visible or animated.
            for (var t = renderer.transform; t != analysis.Root.transform; t = t.parent)
                if (!t.gameObject.activeSelf && !analysis.IsAnimated(t.gameObject, p => p == "m_IsActive")) return "it is never shown";
            return null;
        }

        // What decides an object's activation at every moment: its starting state and, for each controller clip that animates it,
        // that clip and the exact curve. Two curves in one clip share its layer, weight and time, so they always give the same
        // value; avatar masks never filter activation (MaskFilteringTests). Objects with equal signatures are therefore always
        // active together. Anything else (a legacy Animation clip) keeps the object's own identity.
        private static string ToggleSignature(GameObject toggled, AvatarAnalysis analysis)
        {
            var bindings = analysis.BindingsOn(toggled, p => p == "m_IsActive").ToList();
            // A path that names two same-named siblings reaches only one of them in Unity, so such objects keep their identity.
            if (bindings.Any(b => b.Legacy || b.FloatCurve == null || AvatarAnalysis.Resolve(b.Owner, b.Curve.path).Count() != 1))
                return "object:" + toggled.GetInstanceID();
            string Curve(AnimationCurve c) => (int)c.preWrapMode + "/" + (int)c.postWrapMode + "/" + string.Join(",", c.keys.Select(k =>
                string.Join(" ", new[] { k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight }.Select(f => f.ToString("R"))) + " " + (int)k.weightedMode));
            return toggled.activeSelf + ":" + string.Join(";", bindings.Select(b => b.ClipKey + "=" + Curve(b.FloatCurve)).OrderBy(s => s, StringComparer.Ordinal));
        }

        // Toggles, root bone and every renderer setting that changes how the merged mesh would render.
        private static string Key(SkinnedMeshRenderer r, AvatarAnalysis analysis) => string.Join("#", KeyParts(r, analysis).Select(p => p.Value));

        // The parts of the merge key, each with the name the report uses for it.
        private static (string Name, string Value)[] KeyParts(SkinnedMeshRenderer r, AvatarAnalysis analysis)
        {
            var toggles = new List<string>();
            for (var t = r.transform; t != analysis.Root.transform; t = t.parent)
                if (analysis.IsAnimated(t.gameObject, p => p == "m_IsActive")) toggles.Add(ToggleSignature(t.gameObject, analysis));
            toggles.Sort(StringComparer.Ordinal);
            var rootBone = r.rootBone ? r.rootBone : r.transform;
            return new[]
            {
                ("toggle", string.Join("|", toggles)), ("root bone", rootBone.GetInstanceID().ToString()), ("layer", r.gameObject.layer.ToString()),
                ("shadow casting", r.shadowCastingMode.ToString()), ("receive shadows setting", r.receiveShadows.ToString()),
                ("light probe setting", r.lightProbeUsage.ToString()), ("reflection probe setting", r.reflectionProbeUsage.ToString()),
                ("probe anchor", (r.probeAnchor ? r.probeAnchor.GetInstanceID() : 0).ToString()),
                ("light probe proxy volume", (r.lightProbeProxyVolumeOverride ? r.lightProbeProxyVolumeOverride.GetInstanceID() : 0).ToString()),
                ("motion vector setting", r.motionVectorGenerationMode + "/" + r.skinnedMotionVectors), ("skin quality", r.quality.ToString()),
                ("dynamic occlusion setting", r.allowOcclusionWhenDynamic.ToString()), ("rendering layer mask", r.renderingLayerMask.ToString()),
                ("sorting", r.sortingLayerID + "/" + r.sortingOrder + "/" + r.rendererPriority), ("Update When Offscreen setting", r.updateWhenOffscreen.ToString()),
                ("tag", r.gameObject.tag),
            };
        }

        private static bool Compatible(SkinnedMeshRenderer a, SkinnedMeshRenderer b)
        {
            if (Layout(a.sharedMesh) != Layout(b.sharedMesh)) return false;
            bool probesSampled = a.lightProbeUsage != LightProbeUsage.Off || a.reflectionProbeUsage != ReflectionProbeUsage.Off;
            // Without an anchor, probes are sampled at the bounds centre; the merged bounds must keep it. With Update When
            // Offscreen the centre is recomputed from the skinned vertices every frame, so no two renderers share it.
            if (probesSampled && !a.probeAnchor && (a.updateWhenOffscreen || !a.localBounds.center.Equals(b.localBounds.center))) return false;
            return true;
        }

        // Unity rebuilds a skinned renderer's skinning buffers for a new mesh layout only when the renderer is enabled
        // again after the change. The renderer's own enabled state is restored afterwards (a disabled renderer stays off).
        // Sets dst's layout to the first source's non-skin attributes and fills it with every source's vertices in turn.
        private static void CopyVertexData(Mesh dst, List<Mesh> sources)
        {
            var attributes = sources[0].GetVertexAttributes().Where(d => d.attribute != VertexAttribute.BlendWeight && d.attribute != VertexAttribute.BlendIndices).ToArray();
            int total = sources.Sum(s => s.vertexCount);
            dst.SetVertexBufferParams(total, attributes);
            var buffers = attributes.Select(d => d.stream).Distinct().ToDictionary(s => s, s => new byte[dst.GetVertexBufferStride(s) * total]);
            int first = 0;
            foreach (var source in sources)
            {
                using (var array = MeshUtility.AcquireReadOnlyMeshData(source))
                {
                    var data = array[0];
                    foreach (var d in attributes)
                    {
                        int size = (d.format == VertexAttributeFormat.Float32 || d.format == VertexAttributeFormat.UInt32 || d.format == VertexAttributeFormat.SInt32 ? 4
                            : d.format == VertexAttributeFormat.UNorm8 || d.format == VertexAttributeFormat.SNorm8 || d.format == VertexAttributeFormat.UInt8 || d.format == VertexAttributeFormat.SInt8 ? 1 : 2) * d.dimension;
                        int stream = data.GetVertexAttributeStream(d.attribute), from = data.GetVertexAttributeOffset(d.attribute), stride = data.GetVertexBufferStride(stream);
                        var src = data.GetVertexData<byte>(stream);
                        int to = dst.GetVertexAttributeOffset(d.attribute), dstStride = dst.GetVertexBufferStride(d.stream);
                        var bytes = buffers[d.stream];
                        for (int v = 0; v < source.vertexCount; v++)
                            Unity.Collections.NativeArray<byte>.Copy(src, v * stride + from, bytes, (first + v) * dstStride + to, size);
                    }
                }
                first += source.vertexCount;
            }
            foreach (var pair in buffers)
                dst.SetVertexBufferData(pair.Value, 0, 0, pair.Value.Length, pair.Key, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        // Draw order between these materials cannot change the image: an opaque queue, a shader an adapter recognizes, and
        // the default opaque state in every pass the material configures (depth write on, no blending, LEqual or Less
        // depth test, no stencil). Merging renderers or submeshes changes draw order, so only such materials take part.
        internal static bool OrderIndependent(Material material)
        {
            if (!material || material.renderQueue > 2500 || !ShaderAdapterRegistry.Recognizes(material)) return false;
            // Sunao's [Stencil Outline] entries hard-code a stencil write and test with no property to inspect.
            if (material.shader.name.IndexOf("Stencil", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            // lilToon declares the outline state on every entry; only entries with an outline pass draw with it.
            // Other shaders may name their outline pass differently, so for them outline state always counts.
            bool outlinePass = material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) < 0 ||
                Enumerable.Range(0, material.passCount).Any(i => material.GetPassName(i).IndexOf("OUTLINE", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (string name in material.GetPropertyNames(MaterialPropertyType.Float))
            {
                if (!outlinePass && name.StartsWith("_Outline", StringComparison.Ordinal)) continue;
                // The ForwardAdd pass blends additively by design (lilToon and NonToon "...FA", Poiyomi "_Add...");
                // per-light additive passes commute, so they never decide order.
                if (name.StartsWith("_Add", StringComparison.Ordinal) || name.EndsWith("FA", StringComparison.Ordinal)) continue;
                bool Is(params string[] suffixes) => suffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
                float value = material.GetFloat(name);
                if (Is("ZWrite") && value != 1) return false;
                if (Is("SrcBlend") && value != 1) return false;
                if (Is("DstBlend") && value != 0) return false;
                if (Is("BlendOp") && value != 0) return false; // Add; Min/Max are order-dependent even with One/Zero.
                if (Is("ZTest") && value != 4 && value != 2) return false;
                // lilToon/NonToon name stencil state _Stencil{Comp,Pass,Fail,ZFail}, Poiyomi ..._{CompareFunction,PassOp,FailOp,ZFailOp}
                // (main, outline, front and back): Always (or off) and Keep everywhere.
                if (Is("StencilComp", "CompareFunction") && value != 8 && value != 0) return false;
                if (Is("StencilPass", "StencilFail", "StencilZFail", "PassOp", "FailOp") && value != 0) return false;
            }
            return true;
        }

        internal static void Rebind(SkinnedMeshRenderer renderer, Action change)
        {
            bool enabled = renderer.enabled;
            renderer.enabled = false;
            change();
            renderer.enabled = true;
            renderer.enabled = enabled;
        }

        private static string Layout(Mesh mesh) =>
            string.Join(",", mesh.GetVertexAttributes().Where(d => d.attribute != VertexAttribute.BlendWeight && d.attribute != VertexAttribute.BlendIndices)
                .Select(d => d.attribute + ":" + d.format + ":" + d.dimension).OrderBy(s => s, StringComparer.Ordinal));

        internal static readonly Dictionary<string, bool> VertexIdReaders = new Dictionary<string, bool>();
        private static readonly ShaderType[] VertexIdStages = { ShaderType.Vertex, ShaderType.Hull, ShaderType.Domain, ShaderType.Geometry };
        private static readonly VRChatMobileAdapter MobileShaders = new VRChatMobileAdapter();

        // Whether drawing this material can depend on vertex numbering.
        // A shader Unity could not compile (shown pink); asking for its variants only logs errors.
        internal static bool Broken(Shader shader) => !shader.isSupported || shader.name == "Hidden/InternalErrorShader" || ShaderUtil.ShaderHasError(shader);

        // Whether any material this renderer draws or an animation swaps in can depend on vertex numbering, with the renderer's
        // animated material properties.
        internal static bool ReadsVertexId(Renderer renderer, AvatarAnalysis analysis)
        {
            var animated = new HashSet<string>(analysis.AnimatedMaterialProperties(renderer), StringComparer.Ordinal);
            return renderer.sharedMaterials.Concat(analysis.SwappedMaterials(renderer)).Any(m => ReadsVertexId(m, animated.Contains));
        }

        // animated: material properties an animation can change; null when unknown (then a mask could be turned on later).
        internal static bool ReadsVertexId(Material material, Func<string, bool> animated = null)
        {
            if (!material || !material.shader) return false;
            if (Broken(material.shader)) return true; // Cannot be compiled to check; compiling only logs errors.
            if (VertexStreamStripper.IsAuditedLilToonShader(material.shader, out _))
            {
                // lil_common_vert.hlsl: the ID mask reads input.vertexID unless _IDMaskFrom picks UV0-UV7 (then the ID moves with its vertex).
                // It hides geometry only while a mask flag is on, so a flag an animation can turn on counts too.
                float from = material.HasProperty("_IDMaskFrom") ? material.GetFloat("_IDMaskFrom") : 8;
                bool fromVertexId = !(from >= 0 && from <= 7 && from == Mathf.Floor(from)) || animated == null || animated("_IDMaskFrom");
                return fromVertexId && Enumerable.Range(1, 8).Any(i => material.GetFloat("_IDMask" + i) != 0 || material.GetFloat("_IDMaskPrior" + i) != 0 ||
                    animated == null || animated("_IDMask" + i) || animated("_IDMaskPrior" + i));
            }
            // The check reads Direct3D byte code, which cannot answer for Android's graphics APIs; there only VRChat's pinned mobile
            // shaders (one source for every API, audited) are trusted to the Direct3D answer.
            if (VertexStreamStripper.Android && !MobileShaders.Matches(material)) return true;
            string key = VertexStreamStripper.CacheKey(material);
            if (VertexIdReaders.TryGetValue(key, out bool cached)) return cached;
            string fact = ShaderFacts.Key(material, "d3d", string.Join(",", VertexIdStages) + "|SV_VertexID,SV_PrimitiveID|no-meta-never");
            if (ShaderFacts.TryGet("vertex-id", fact, out string known)) return VertexIdReaders[key] = known == "1";
            bool clean = true;
            bool reads = false;
            try
            {
                var data = ShaderUtil.GetShaderData(material.shader);
                var sub = data.ActiveSubshader;
                for (int p = 0; p < sub.PassCount && !reads; p++)
                {
                    var pass = sub.GetPass(p);
                    string mode = pass.FindTagValue(new ShaderTagId("LightMode")).name ?? "";
                    if (mode.Equals("Meta", StringComparison.OrdinalIgnoreCase) || mode == "Never") continue;
                    foreach (var stage in VertexIdStages)
                    {
                        if (!pass.HasShaderStage(stage)) continue;
                        var info = pass.CompileVariant(stage, material.shaderKeywords, ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                        if (!info.Success) clean = false; // Cautious, but possibly a one-off compiler failure: not kept on disk.
                        if (!info.Success || DxbcInputs.Has(info.ShaderData, "SV_VertexID") || DxbcInputs.Has(info.ShaderData, "SV_PrimitiveID")) { reads = true; break; }
                    }
                }
            }
            catch (Exception) { reads = true; clean = false; }
            // A failed variant compile records an error on the shader, and VRCFury then refuses builds. Broken() returned above
            // for a shader that already had one, so any error now is ours.
            finally { if (ShaderUtil.ShaderHasError(material.shader)) ShaderUtil.ClearShaderMessages(material.shader); }
            if (clean) ShaderFacts.Put("vertex-id", fact, reads ? "1" : "0");
            return VertexIdReaders[key] = reads;
        }

        private static bool Merge(List<SkinnedMeshRenderer> members, Action<Object, Object> register, GameObject root,
            Dictionary<GameObject, Move> moves, SkinnedMeshRenderer mmdBody, AvatarAnalysis analysis)
        {
            // Shape names: the host keeps its own; a later member's colliding name (or, when the root Body hosts, an MMD
            // world name) gets the member's object name appended.
            // A removed shape can leave curves behind (the freezer drops shapes whose curves change nothing). Those names
            // count as used, so no member shape takes a name the host's leftover curves drive, and a member's leftover
            // curves are renamed to a name no shape has, so they keep driving nothing on the host.
            var used = new HashSet<string>(StringComparer.Ordinal);
            List<string> Leftover(SkinnedMeshRenderer r) => analysis.BindingsOn(r, p => p.StartsWith("blendShape.", StringComparison.Ordinal))
                .Select(b => b.Property.Substring("blendShape.".Length)).Distinct().Where(n => r.sharedMesh.GetBlendShapeIndex(n) < 0).ToList();
            used.UnionWith(Leftover(members[0]));
            var names = new List<Dictionary<string, string>>();
            var shapeCounts = members.Select(m => m.sharedMesh.blendShapeCount).ToList();
            var materialCounts = members.Select(m => m.sharedMaterials.Length).ToList();
            foreach (var member in members)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int s = 0; s < member.sharedMesh.blendShapeCount; s++)
                {
                    string name = member.sharedMesh.GetBlendShapeName(s), next = name;
                    bool hostShape = member == members[0];
                    for (int n = 1; !hostShape && (used.Contains(next) || mmdBody && BlendShapeFreezer.MmdShapes.Contains(next)); n++)
                        next = name + " (" + member.name + (n > 1 ? " " + n : "") + ")";
                    used.Add(next);
                    map[name] = next;
                }
                if (member != members[0])
                    foreach (var name in Leftover(member))
                    {
                        string next = name + " (removed from " + member.name + ")";
                        for (int n = 2; used.Contains(next); n++) next = name + " (removed from " + member.name + " " + n + ")";
                        used.Add(next);
                        map[name] = next;
                    }
                names.Add(map);
            }
            var host = members[0];
            var bones = new List<Transform>(); var bindposes = new List<Matrix4x4>();
            var boneSlot = new Dictionary<(Transform, Matrix4x4), int>();
            var mesh = new Mesh { name = string.Join(" + ", members.Select(m => m.sharedMesh.name)) };
            int total = members.Sum(m => m.sharedMesh.vertexCount);
            mesh.indexFormat = total > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;

            var positions = new List<Vector3>(); var normals = new List<Vector3>();
            var bonesPerVertex = new List<byte>(); var weights = new List<BoneWeight1>();
            var submeshes = new List<int[]>(); var materials = new List<Material>();
            var layout = members[0].sharedMesh;
            bool hasNormals = layout.HasVertexAttribute(VertexAttribute.Normal);
            var shapeSources = new List<(SkinnedMeshRenderer Renderer, int Offset)>();
            Bounds? bounds = null;

            foreach (var renderer in members)
            {
                var source = renderer.sharedMesh;
                int offset = positions.Count;
                shapeSources.Add((renderer, offset));
                var remap = new int[renderer.bones.Length];
                var sourceBindposes = source.bindposes;
                for (int b = 0; b < remap.Length; b++)
                {
                    var key = (renderer.bones[b], sourceBindposes[b]);
                    if (!boneSlot.TryGetValue(key, out int slot)) { slot = bones.Count; boneSlot.Add(key, slot); bones.Add(key.Item1); bindposes.Add(key.Item2); }
                    remap[b] = slot;
                }
                positions.AddRange(source.vertices);
                if (hasNormals) normals.AddRange(source.normals);
                var perVertex = source.GetBonesPerVertex(); var sourceWeights = source.GetAllBoneWeights();
                bonesPerVertex.AddRange(perVertex);
                foreach (var w in sourceWeights) weights.Add(new BoneWeight1 { boneIndex = remap[w.boneIndex], weight = w.weight });
                for (int s = 0; s < source.subMeshCount; s++)
                {
                    submeshes.Add(source.GetIndices(s).Select(i => i + offset).ToArray());
                    materials.Add(renderer.sharedMaterials[s]);
                }
                var b0 = renderer.localBounds;
                bounds = bounds == null ? b0 : Encapsulated(bounds.Value, b0);
            }

            // Vertex data is copied byte for byte in the members' own formats (every member has the same layout), so
            // compressed channels such as half-precision UVs stay compressed.
            CopyVertexData(mesh, members.Select(m => m.sharedMesh).ToList());
            if (!mesh.vertices.SequenceEqual(positions) || hasNormals && !mesh.normals.SequenceEqual(normals))
                throw new InvalidOperationException("Merged vertex data differs from its sources.");
            mesh.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++) mesh.SetTriangles(submeshes[s], s, false);
            if (bonesPerVertex.All(c => c <= 4))
            {
                // Classic four-weight layout, which every skinning path accepts.
                var classic = new BoneWeight[bonesPerVertex.Count];
                for (int v = 0, w = 0; v < classic.Length; w += bonesPerVertex[v], v++)
                {
                    var b = new BoneWeight();
                    if (bonesPerVertex[v] > 0) { b.boneIndex0 = weights[w].boneIndex; b.weight0 = weights[w].weight; }
                    if (bonesPerVertex[v] > 1) { b.boneIndex1 = weights[w + 1].boneIndex; b.weight1 = weights[w + 1].weight; }
                    if (bonesPerVertex[v] > 2) { b.boneIndex2 = weights[w + 2].boneIndex; b.weight2 = weights[w + 2].weight; }
                    if (bonesPerVertex[v] > 3) { b.boneIndex3 = weights[w + 3].boneIndex; b.weight3 = weights[w + 3].weight; }
                    classic[v] = b;
                }
                mesh.boneWeights = classic;
            }
            else
                using (var perVertexArray = new Unity.Collections.NativeArray<byte>(bonesPerVertex.ToArray(), Unity.Collections.Allocator.Temp))
                using (var weightArray = new Unity.Collections.NativeArray<BoneWeight1>(weights.ToArray(), Unity.Collections.Allocator.Temp))
                    mesh.SetBoneWeights(perVertexArray, weightArray);
            mesh.bindposes = bindposes.ToArray();
            var dv = new Vector3[0]; var dn = new Vector3[0]; var dt = new Vector3[0];
            foreach (var (renderer, offset) in shapeSources)
            {
                var source = renderer.sharedMesh;
                for (int shape = 0; shape < source.blendShapeCount; shape++)
                    for (int f = 0; f < source.GetBlendShapeFrameCount(shape); f++)
                    {
                        Array.Resize(ref dv, source.vertexCount); Array.Resize(ref dn, source.vertexCount); Array.Resize(ref dt, source.vertexCount);
                        source.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                        var fv = new Vector3[total]; var fn = new Vector3[total]; var ft = new Vector3[total];
                        Array.Copy(dv, 0, fv, offset, dv.Length); Array.Copy(dn, 0, fn, offset, dn.Length); Array.Copy(dt, 0, ft, offset, dt.Length);
                        mesh.AddBlendShapeFrame(names[members.IndexOf(renderer)][source.GetBlendShapeName(shape)], source.GetBlendShapeFrameWeight(shape, f), fv, fn, ft);
                    }
            }
            mesh.RecalculateBounds();
            MeshAndAudioOptimizer.KeepUvDensity(mesh, members.Select(m => m.sharedMesh));

            var sources = members.Select(m => m.sharedMesh).ToList(); // Before the host takes the merged mesh.
            var weightsByName = members.SelectMany(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount)
                .Select(s => (r.sharedMesh.GetBlendShapeName(s), r.GetBlendShapeWeight(s)))).ToList();
            // Everything that can fail happens before the first change; afterwards only assignments remain, and a failure
            // while retargeting the descriptor puts the host back, so no group is ever left half merged.
            var original = (Bones: host.bones, Mesh: host.sharedMesh, Materials: host.sharedMaterials, Bounds: host.localBounds,
                Weights: Enumerable.Range(0, host.sharedMesh.blendShapeCount).Select(host.GetBlendShapeWeight).ToArray());
            Rebind(host, () =>
            {
                host.bones = bones.ToArray();
                host.sharedMesh = mesh;
                host.sharedMaterials = materials.ToArray();
                host.localBounds = bounds.Value;
            });
            for (int s = 0; s < weightsByName.Count; s++) host.SetBlendShapeWeight(s, weightsByName[s].Item2);
            string hostPath = AnimationUtility.CalculateTransformPath(host.transform, root.transform);
            var planned = new List<Move>();
            try
            {
                int slotOffset = 0, shapeOffset = 0;
                for (int i = 0; i < members.Count; i++)
                {
                    planned.Add(new Move { HostPath = hostPath, Shapes = names[i], SlotOffset = slotOffset });
                    RetargetDescriptor(root, members[i], host, names[i], shapeOffset);
                    slotOffset += materialCounts[i];
                    shapeOffset += shapeCounts[i];
                }
            }
            catch
            {
                Rebind(host, () =>
                {
                    host.bones = original.Bones;
                    host.sharedMesh = original.Mesh;
                    host.sharedMaterials = original.Materials;
                    host.localBounds = original.Bounds;
                });
                for (int s = 0; s < original.Weights.Length; s++) host.SetBlendShapeWeight(s, original.Weights[s]);
                Object.DestroyImmediate(mesh);
                throw;
            }
            foreach (var source in sources.Distinct()) register(source, mesh);
            for (int i = 1; i < members.Count; i++)
            {
                moves[members[i].gameObject] = planned[i];
                register(members[i], host);
                Object.DestroyImmediate(members[i]);
            }
            return true;
        }

        // The avatar descriptor names the viseme mesh and eyelid mesh; point them at the merged renderer, with renamed
        // viseme shapes and eyelid shape indices shifted to the member's place in the merged mesh.
        private static void RetargetDescriptor(GameObject root, SkinnedMeshRenderer member, SkinnedMeshRenderer host, Dictionary<string, string> names, int shapeOffset)
        {
            foreach (var descriptor in root.GetComponents<Component>().Where(c => c && c.GetType().Name == "VRCAvatarDescriptor"))
                using (var serialized = new SerializedObject(descriptor))
                {
                    var viseme = serialized.FindProperty("VisemeSkinnedMesh");
                    if (viseme != null && viseme.objectReferenceValue == member)
                    {
                        viseme.objectReferenceValue = host;
                        var shapes = serialized.FindProperty("VisemeBlendShapes");
                        for (int i = 0; shapes != null && i < shapes.arraySize; i++)
                        {
                            var element = shapes.GetArrayElementAtIndex(i);
                            if (names.TryGetValue(element.stringValue ?? "", out var renamed)) element.stringValue = renamed;
                        }
                        var mouth = serialized.FindProperty("MouthOpenBlendShapeName");
                        if (mouth != null && names.TryGetValue(mouth.stringValue ?? "", out var mouthRenamed)) mouth.stringValue = mouthRenamed;
                    }
                    var eyelids = serialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh");
                    if (eyelids != null && eyelids.objectReferenceValue == member)
                    {
                        eyelids.objectReferenceValue = host;
                        var indices = serialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                        for (int i = 0; indices != null && i < indices.arraySize; i++)
                        {
                            var element = indices.GetArrayElementAtIndex(i);
                            if (element.intValue >= 0) element.intValue += shapeOffset;
                        }
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
        }

        private static Bounds Encapsulated(Bounds a, Bounds b) { a.Encapsulate(b); return a; }
    }
}
