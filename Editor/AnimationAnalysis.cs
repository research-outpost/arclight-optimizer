using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // This is deliberately a bounded, conservative view of animation. It records the values
    // needed by texture scanning and keeps the virtual index alive for the later substitution pass.
    internal sealed class AnimationSnapshot
    {
        // Runaway guards, applied to each analysis pass separately. Clips count whether or not they animate anything
        // relevant, so the limit leaves room for face-tracking and VRCFury avatars with thousands of blendshape clips.
        internal const int MaxClips = 16384;
        internal const int MaxBindings = 16384;
        internal const int MaxKeyframes = 131072;
        internal const int MaxCandidatesPerRenderer = 128;
        internal const int MaxBindingsPerRenderer = 1024;
        internal const string IgnoredBindingWarning =
            "Animation bindings to missing objects or particle/trail/line renderers were ignored; they cannot change optimized mesh textures.";

        internal readonly Dictionary<string, AnimationRendererState> Renderers =
            new Dictionary<string, AnimationRendererState>(StringComparer.Ordinal);
        internal readonly List<VirtualObjectBinding> ObjectBindings = new List<VirtualObjectBinding>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly bool HasVirtualContext;
        internal readonly AnimationIndex VirtualIndex;
        internal bool Uninspectable;
        internal bool HasObjectCurves;
        internal bool HasMaterialAnimation;
        internal int ClipCount;
        internal int BindingCount;
        internal int KeyframeCount;
        // Mesh renderers that the scanner models, with their root-relative paths.
        private List<(string Path, Renderer Renderer)> scannedRenderers =
            new List<(string Path, Renderer Renderer)>();

        internal bool CanRewriteObjectCurves => HasVirtualContext && !Uninspectable && VirtualIndex != null;

        internal AnimationSnapshot(bool hasVirtualContext, AnimationIndex virtualIndex)
        {
            HasVirtualContext = hasVirtualContext;
            VirtualIndex = virtualIndex;
        }

        internal AnimationRendererState Get(string path, Renderer renderer)
        {
            if (!Renderers.TryGetValue(path, out var state))
            {
                state = new AnimationRendererState(renderer);
                Renderers.Add(path, state);
            }
            else if (!state.Renderer) state.Renderer = renderer;
            return state;
        }

        internal void Fail(string warning)
        {
            Uninspectable = true;
            AddWarning(warning);
        }

        internal void AddWarning(string warning)
        {
            if (!string.IsNullOrEmpty(warning) && !Warnings.Contains(warning)) Warnings.Add(warning);
        }

        internal static AnimationSnapshot Analyze(GameObject root, VirtualControllerContext context,
            List<string> warnings, AnimationSnapshot serializedSnapshot = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var snapshot = new AnimationSnapshot(context != null,
                context == null ? null : new AnimationIndex(context.GetAllControllers()));
            try
            {
                snapshot.scannedRenderers = root.GetComponentsInChildren<Renderer>(true)
                    .Where(IsScannedRenderer)
                    .Select(r => (AnimationUtility.CalculateTransformPath(r.transform, root.transform), r))
                    .ToList();
                if (serializedSnapshot != null) snapshot.Merge(serializedSnapshot);
                if (context != null) AnalyzeVirtual(root, context, snapshot, serializedSnapshot != null);
                else AnalyzeSerialized(root, snapshot);
            }
            catch (Exception e)
            {
                snapshot.Fail("Animation references could not be inspected: " + e.Message);
            }

            foreach (var warning in snapshot.Warnings)
                if (warnings != null && !warnings.Contains(warning)) warnings.Add(warning);
            return snapshot;
        }

        private void Merge(AnimationSnapshot source)
        {
            Uninspectable |= source.Uninspectable;
            HasObjectCurves |= source.HasObjectCurves;
            HasMaterialAnimation |= source.HasMaterialAnimation;
            foreach (var warning in source.Warnings) AddWarning(warning);
            foreach (var pair in source.Renderers)
            {
                var original = pair.Value;
                var state = Get(pair.Key, original.Renderer);
                state.HasTextureSwap |= original.HasTextureSwap;
                state.FloatProperties.UnionWith(original.FloatProperties);
                foreach (var range in original.FloatRanges) state.AddFloatRange(range.Key, range.Value);
                state.HasMeshBinding |= original.HasMeshBinding;
                state.RequiresObjectRewrite |= original.RequiresObjectRewrite;
                state.UnsupportedObjectCurve |= original.UnsupportedObjectCurve;
                foreach (var entry in original.SlotMaterials)
                    state.SlotMaterials[entry.Key] = new HashSet<Material>(entry.Value);
                foreach (var entry in original.TextureValues)
                    state.TextureValues[entry.Key] = new HashSet<Texture>(entry.Value);
                foreach (var binding in original.BindingKeys) CountBinding(state, binding);
            }
        }

        private static void AnalyzeVirtual(GameObject root, VirtualControllerContext context,
            AnimationSnapshot snapshot, bool hasSerializedInventory)
        {
            // Marker clips are opaque by design. Platform bindings expose the source motions,
            // so inspect known special motions directly before walking their virtual markers.
            try
            {
                foreach (var entry in context.PlatformBindings.GetInnateControllers(root))
                {
                    string prefix = ControllerPrefix(root, entry.Item1);
                    foreach (var clip in entry.Item2.animationClips)
                    {
                        if (!clip || !context.PlatformBindings.IsSpecialMotion(clip)) continue;
                        snapshot.ClipCount++;
                        // Special platform motions carry no layer/blend context here; treat ranges as unbounded.
                        AnalyzeSerializedClip(root, prefix, clip, snapshot, unbounded: true);
                    }
                }
            }
            catch (Exception e)
            {
                snapshot.Fail("Could not inspect platform animation references: " + e.Message);
            }

            var unbounded = UnboundedVirtualClips(context);
            var visited = new Dictionary<VirtualClip, HashSet<string>>();
            foreach (var entry in context.Controllers)
            {
                if (entry.Value == null) continue;
                // Tools can delete an Animator after merging its controller elsewhere (VRCFury and Avatar Pose System
                // do this with their prefab's own Animator). NDMF keeps the entry, but that controller never plays, and
                // without its Animator the paths would be misread as relative to the avatar root.
                if (entry.Key is UnityEngine.Object owner && !owner) continue;
                string prefix = ControllerPrefix(root, entry.Key);
                foreach (var node in entry.Value.AllReachableNodes())
                {
                    var clip = node as VirtualClip;
                    if (clip == null) continue;
                    if (!visited.TryGetValue(clip, out var prefixes))
                    {
                        prefixes = new HashSet<string>(StringComparer.Ordinal);
                        visited.Add(clip, prefixes);
                    }
                    if (!prefixes.Add(prefix)) continue;
                    snapshot.ClipCount++;
                    if (snapshot.ClipCount > MaxClips)
                    {
                        snapshot.Fail("Animation analysis exceeded the clip limit; affected textures retained.");
                        return;
                    }
                    // A preceding non-virtual pass inventories committed clips, including markers.
                    // Never infer marker identity from a name. Immutable clips retain their original
                    // object references; their complete serialized coverage is still included.
                    if (clip.IsMarkerClip)
                    {
                        if (!hasSerializedInventory)
                            snapshot.Fail("Animation contains an opaque NDMF marker clip; affected textures retained.");
                        continue;
                    }
                    AnalyzeVirtualClip(root, prefix, clip, snapshot, unbounded.Contains(clip));
                    if (snapshot.Uninspectable && snapshot.BindingCount > MaxBindings) return;
                }
            }
        }

        // Keyframes bound a curve's value only when the animator interpolates between clip values.
        // Additive layers (including VRChat's Additive playable layer) add values, and Direct or 2D
        // blend trees can sum or extrapolate them, so clips reached through them have unbounded ranges.
        private static HashSet<VirtualClip> UnboundedVirtualClips(VirtualControllerContext context)
        {
            var clips = new HashSet<VirtualClip>();
            foreach (var entry in context.Controllers)
            {
                if (entry.Value == null) continue;
                bool additivePlayable = IsAdditivePlayableLayer(entry.Key);
                foreach (var layer in entry.Value.Layers)
                {
                    bool additive = additivePlayable || layer.BlendingMode == AnimatorLayerBlendingMode.Additive;
                    foreach (var node in layer.AllReachableNodes())
                    {
                        if (additive && node is VirtualClip clip) clips.Add(clip);
                        if (node is VirtualBlendTree tree && tree.BlendType != BlendTreeType.Simple1D && !FoldedLayers(tree))
                            foreach (var child in tree.AllReachableNodes())
                                if (child is VirtualClip nested) clips.Add(nested);
                    }
                }
            }
            return clips;
        }

        // Arclight's own folded layer: a Direct tree whose children all play at weight 1 (AlwaysOne) and never share a property,
        // so every child keeps exactly the values it would have alone; its children are judged on their own.
        internal static bool FoldedLayers(VirtualBlendTree tree) => tree.BlendType == BlendTreeType.Direct && !tree.NormalizedBlendValues &&
            AnimatorLayerMerger.AlwaysOne != null && tree.Children.All(c => c.DirectBlendParameter == AnimatorLayerMerger.AlwaysOne);

        // VRChat keys its playable layers by VRCAvatarDescriptor.AnimLayerType; this package does not
        // reference the SDK, so match the enum by type and value name.
        internal static bool IsAdditivePlayableLayer(object key) =>
            key != null && key.GetType().Name == "AnimLayerType" && key.ToString() == "Additive";

        private static void AnalyzeVirtualClip(GameObject root, string prefix, VirtualClip clip,
            AnimationSnapshot snapshot, bool unbounded)
        {
            foreach (var binding in clip.GetFloatCurveBindings())
            {
                if (!IsRelevantFloat(binding)) continue;
                if (!NextBinding(snapshot)) return;

                var curve = clip.GetFloatCurve(binding);
                AnalyzeFloatBinding(root, prefix, binding, curve == null ? null : curve.keys, clip.Name, snapshot, unbounded);
            }
            foreach (var binding in clip.GetObjectCurveBindings())
            {
                if (!IsRelevantObject(binding)) continue;
                if (!NextBinding(snapshot)) return;

                if (AnalyzeObjectBinding(root, prefix, binding, clip.GetObjectCurve(binding), clip.Name, snapshot))
                    snapshot.ObjectBindings.Add(new VirtualObjectBinding(clip, binding));
            }
        }

        private static void AnalyzeSerialized(GameObject root, AnimationSnapshot snapshot)
        {
            var clips = new Dictionary<AnimationClip, HashSet<string>>();
            var unbounded = new HashSet<AnimationClip>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component) continue; // Missing scripts: the VRChat SDK blocks such uploads.
                if (IsStrippedBeforeUpload(component)) continue;
                try
                {
                    string prefix = component is Animator || component is Animation
                        ? AnimationUtility.CalculateTransformPath(component.transform, root.transform)
                        : "";
                    using (var serialized = new SerializedObject(component))
                    {
                        var property = serialized.GetIterator();
                        while (property.Next(true))
                        {
                            if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                            var reference = property.objectReferenceValue;
                            var controller = reference as RuntimeAnimatorController;
                            if (controller != null)
                            {
                                foreach (var clip in controller.animationClips)
                                    AddClip(clips, clip, prefix);
                                CollectUnboundedClips(controller, IsAdditivePlayableSlot(serialized, property), unbounded);
                            }
                            else
                            {
                                var motion = reference as Motion;
                                if (motion != null)
                                {
                                    AddMotion(clips, motion, prefix, new HashSet<Motion>(), unbounded, false);
                                    // A custom component may store clips relative to its own
                                    // object. Include both roots conservatively.
                                    if (!(component is Animator) && !(component is Animation) && component.transform != root.transform)
                                        AddMotion(clips, motion, AnimationUtility.CalculateTransformPath(component.transform, root.transform),
                                            new HashSet<Motion>(), unbounded, false);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    snapshot.Fail("Could not inspect animation references: " + e.Message);
                }
            }

            foreach (var pair in clips)
            {
                snapshot.ClipCount++;
                if (snapshot.ClipCount > AnimationSnapshot.MaxClips)
                {
                    snapshot.Fail("Animation analysis exceeded the clip limit; affected textures retained.");
                    return;
                }
                foreach (string prefix in pair.Value)
                    AnalyzeSerializedClip(root, prefix, pair.Key, snapshot, unbounded.Contains(pair.Key));
            }
        }

        // The VRChat SDK strips editor-only components and EditorOnly-tagged objects before upload (VRCFury defers this
        // to the end of the build), so animation they reference never plays. VRCFury, for example, keeps its component
        // holding the source controller after merging a copy with rewritten paths; reading the source from the avatar
        // root would block every texture. NDMF-managed controllers are still read through the virtual context.
        private static bool IsStrippedBeforeUpload(Component component)
        {
            if (component.GetType().GetInterfaces().Any(i => i.FullName == "VRC.SDKBase.IEditorOnly")) return true;
            for (var transform = component.transform; transform; transform = transform.parent)
                if (transform.CompareTag("EditorOnly")) return true;
            return false;
        }

        // Serialized counterpart of UnboundedVirtualClips. Controllers whose layer structure cannot be
        // walked (nested override chains or other controller types) mark all their clips unbounded.
        private static void CollectUnboundedClips(RuntimeAnimatorController controller, bool additivePlayable,
            HashSet<AnimationClip> unbounded)
        {
            var overrides = new Dictionary<AnimationClip, AnimationClip>();
            var structure = controller as AnimatorController;
            if (controller is AnimatorOverrideController overrideController)
            {
                structure = overrideController.runtimeAnimatorController as AnimatorController;
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrideController.GetOverrides(pairs);
                foreach (var pair in pairs)
                    if (pair.Key && pair.Value) overrides[pair.Key] = pair.Value;
            }
            if (!structure)
            {
                foreach (var clip in controller.animationClips) if (clip) unbounded.Add(clip);
                return;
            }
            var layers = structure.layers;
            foreach (var layer in layers)
            {
                bool additive = additivePlayable || layer.blendingMode == AnimatorLayerBlendingMode.Additive;
                bool synced = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < layers.Length;
                var machine = synced ? layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;
                foreach (var state in States(machine, new HashSet<AnimatorStateMachine>()))
                    MarkMotion(synced ? layer.GetOverrideMotion(state) : state.motion, additive, overrides, unbounded,
                        new HashSet<Motion>());
            }
        }

        private static IEnumerable<AnimatorState> States(AnimatorStateMachine machine, HashSet<AnimatorStateMachine> visited)
        {
            if (!machine || !visited.Add(machine)) yield break;
            foreach (var child in machine.states) if (child.state) yield return child.state;
            foreach (var child in machine.stateMachines)
                foreach (var state in States(child.stateMachine, visited)) yield return state;
        }

        private static void MarkMotion(Motion motion, bool unboundedContext, Dictionary<AnimationClip, AnimationClip> overrides,
            HashSet<AnimationClip> unbounded, HashSet<Motion> visited)
        {
            if (!motion || !visited.Add(motion)) return;
            if (motion is AnimationClip clip)
            {
                if (unboundedContext) unbounded.Add(overrides.TryGetValue(clip, out var replacement) ? replacement : clip);
            }
            else if (motion is BlendTree tree)
            {
                bool nested = unboundedContext || tree.blendType != BlendTreeType.Simple1D;
                foreach (var child in tree.children) MarkMotion(child.motion, nested, overrides, unbounded, visited);
            }
        }

        // A controller assigned to VRChat's Additive playable layer is applied additively as a whole.
        private static bool IsAdditivePlayableSlot(SerializedObject serialized, SerializedProperty property)
        {
            int dot = property.propertyPath.LastIndexOf('.');
            if (dot < 0) return false;
            var type = serialized.FindProperty(property.propertyPath.Substring(0, dot) + ".type");
            return type != null && type.propertyType == SerializedPropertyType.Enum &&
                type.enumValueIndex >= 0 && type.enumValueIndex < type.enumNames.Length &&
                type.enumNames[type.enumValueIndex] == "Additive";
        }

        private static void AnalyzeSerializedClip(GameObject root, string prefix, AnimationClip clip,
            AnimationSnapshot snapshot, bool unbounded)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsRelevantFloat(binding)) continue;
                if (!NextBinding(snapshot)) return;

                AnalyzeFloatBinding(root, prefix, binding,
                    AnimationUtility.GetEditorCurve(clip, binding)?.keys, clip.name, snapshot, unbounded);
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!IsRelevantObject(binding)) continue;
                if (!NextBinding(snapshot)) return;

                AnalyzeObjectBinding(root, prefix, binding,
                    AnimationUtility.GetObjectReferenceCurve(clip, binding), clip.name, snapshot);
            }
        }

        private static void AddClip(Dictionary<AnimationClip, HashSet<string>> clips, AnimationClip clip, string prefix)
        {
            if (!clip) return;
            if (clips.Count >= MaxClips && !clips.ContainsKey(clip))
                throw new InvalidOperationException("Animation clip inventory exceeded its safety limit.");
            if (!clips.TryGetValue(clip, out var prefixes))
            {
                prefixes = new HashSet<string>(StringComparer.Ordinal);
                clips.Add(clip, prefixes);
            }
            prefixes.Add(prefix ?? "");
        }

        private static void AddMotion(Dictionary<AnimationClip, HashSet<string>> clips, Motion motion,
            string prefix, HashSet<Motion> visited, HashSet<AnimationClip> unbounded, bool unboundedContext)
        {
            if (!motion || !visited.Add(motion)) return;
            if (visited.Count > MaxClips) throw new InvalidOperationException("Animation motion graph exceeded its safety limit.");
            if (motion is AnimationClip clip)
            {
                AddClip(clips, clip, prefix);
                if (unboundedContext) unbounded.Add(clip);
            }
            else if (motion is BlendTree tree)
            {
                bool nested = unboundedContext || tree.blendType != BlendTreeType.Simple1D;
                foreach (var child in tree.children) AddMotion(clips, child.motion, prefix, visited, unbounded, nested);
            }
            else throw new InvalidOperationException("Unrecognized animation motion type: " + motion.GetType().Name);
        }

        private static string ControllerPrefix(GameObject root, object key)
        {
            // Ordinary Animator controllers are relative to their Animator root. NDMF
            // IVirtualizeAnimatorController/IVirtualizeMotion paths have already received
            // their base path before they reach VirtualControllerContext.Controllers.
            var animator = key as Animator;
            return animator && animator.transform
                ? AnimationUtility.CalculateTransformPath(animator.transform, root.transform)
                : "";
        }

        private static string CombinePath(string prefix, string path)
        {
            if (string.IsNullOrEmpty(prefix)) return path ?? "";
            if (string.IsNullOrEmpty(path)) return prefix;
            return prefix + "/" + path;
        }

        private static bool IsRelevantFloat(EditorCurveBinding binding)
        {
            string property = binding.propertyName ?? "";
            return property.StartsWith("material.", StringComparison.OrdinalIgnoreCase) ||
                   property.IndexOf("m_Mesh", StringComparison.Ordinal) >= 0;
        }

        private static bool IsRelevantObject(EditorCurveBinding binding)
        {
            string property = binding.propertyName ?? "";
            return IsMaterialSlot(property) || property.StartsWith("material.", StringComparison.OrdinalIgnoreCase) ||
                   property.IndexOf("m_Mesh", StringComparison.Ordinal) >= 0;
        }

        private static bool IsMaterialSlot(string property) =>
            property.IndexOf("m_Materials.Array.data[", StringComparison.Ordinal) >= 0;

        private static bool IsMeshBinding(EditorCurveBinding binding) =>
            (binding.propertyName ?? "").IndexOf("m_Mesh", StringComparison.Ordinal) >= 0;

        private static bool IsRendererType(Type type) => type != null && typeof(Renderer).IsAssignableFrom(type);

        // Only these renderers are scanned and substituted; other renderer types keep their originals.
        private static bool IsScannedRenderer(Renderer renderer) =>
            renderer is MeshRenderer || renderer is SkinnedMeshRenderer;

        private static bool BindingCanTarget(EditorCurveBinding binding, Renderer renderer)
        {
            if (IsRendererType(binding.type)) return binding.type.IsInstanceOfType(renderer);
            return IsMeshBinding(binding) && binding.type == typeof(MeshFilter) && renderer is MeshRenderer;
        }

        // An unresolved binding could still reach a scanned renderer if its clip were played from an
        // animator root other than the one assumed here. Treat any matching path suffix as ambiguous.
        private bool CouldTargetScannedRenderer(EditorCurveBinding binding)
        {
            string relative = binding.path ?? "";
            return scannedRenderers.Any(entry => BindingCanTarget(binding, entry.Renderer) &&
                (relative.Length == 0 || entry.Path == relative ||
                 entry.Path.EndsWith("/" + relative, StringComparison.Ordinal)));
        }

        private static Renderer ResolveRenderer(GameObject root, string path, EditorCurveBinding binding)
        {
            var transform = string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);
            if (!transform) return null;
            if (IsRendererType(binding.type)) return transform.GetComponent(binding.type) as Renderer;
            if (IsMeshBinding(binding) && binding.type == typeof(MeshFilter))
                return transform.GetComponent<MeshFilter>() ? transform.GetComponent<MeshRenderer>() as Renderer : null;
            if (IsMeshBinding(binding) && binding.type == typeof(SkinnedMeshRenderer))
                return transform.GetComponent<SkinnedMeshRenderer>();
            return null;
        }

        private static AnimationRendererState GetState(GameObject root, string prefix,
            EditorCurveBinding binding, AnimationSnapshot snapshot)
        {
            string path = CombinePath(prefix, binding.path);
            var renderer = ResolveRenderer(root, path, binding);
            if (renderer && IsScannedRenderer(renderer)) return snapshot.Get(path, renderer);
            if (!renderer && snapshot.CouldTargetScannedRenderer(binding))
            {
                snapshot.Fail("Animation binding could not be matched to a renderer: " + path + " / " + binding.propertyName);
                return null;
            }
            // A binding to a missing object or component does nothing at runtime. Particle, trail and
            // line renderers are never substituted, so their curves must keep original references.
            snapshot.AddWarning(IgnoredBindingWarning);
            return null;
        }

        private static void AnalyzeFloatBinding(GameObject root, string prefix, EditorCurveBinding binding,
            Keyframe[] keys, string clipName, AnimationSnapshot snapshot, bool unbounded)
        {
            if (!CountKeys(snapshot, keys?.Length ?? 0)) return;
            var state = GetState(root, prefix, binding, snapshot);
            if (state == null) return;
            string property = binding.propertyName ?? "";
            CountBinding(state, clipName + "|" + CombinePath(prefix, binding.path) + "|" + property);
            if (IsMeshBinding(binding)) { state.HasMeshBinding = true; return; }
            if (!property.StartsWith("material.", StringComparison.OrdinalIgnoreCase)) return;
            state.FloatProperties.Add(MaterialPropertyName(property));
            state.AddFloatRange(property.Substring("material.".Length), FloatRange.OfCurve(keys, unbounded));
            snapshot.HasMaterialAnimation = true;
        }

        private static bool AnalyzeObjectBinding(GameObject root, string prefix, EditorCurveBinding binding,
            ObjectReferenceKeyframe[] keys, string clipName, AnimationSnapshot snapshot)
        {
            if (!CountKeys(snapshot, keys?.Length ?? 0)) return false;
            var state = GetState(root, prefix, binding, snapshot);
            if (state == null) return false;
            string property = binding.propertyName ?? "";
            CountBinding(state, clipName + "|" + CombinePath(prefix, binding.path) + "|" + property);
            if (IsMeshBinding(binding)) { state.HasMeshBinding = true; return false; }
            snapshot.HasObjectCurves = true;
            snapshot.HasMaterialAnimation = true;
            if (IsMaterialSlot(property))
            {
                state.RequiresObjectRewrite = true;
                int slot = ParseSlot(property);
                if (slot < 0 || slot >= MaxCandidatesPerRenderer)
                {
                    snapshot.Fail("Animated material slot exceeds the supported safety limits; original textures retained.");
                    return false;
                }
                if (!state.SlotMaterials.TryGetValue(slot, out var materials))
                {
                    materials = new HashSet<Material>();
                    state.SlotMaterials.Add(slot, materials);
                }
                foreach (var key in keys ?? Array.Empty<ObjectReferenceKeyframe>())
                {
                    if (key.value is Material material) materials.Add(material);
                    else if (key.value != null) state.UnsupportedObjectCurve = true;
                }
                if (materials.Count > AnimationSnapshot.MaxCandidatesPerRenderer)
                    state.UnsupportedObjectCurve = true;
                return true;
            }
            if (!property.StartsWith("material.", StringComparison.OrdinalIgnoreCase)) return false;
            string textureProperty = property.Substring("material.".Length);
            // Unity has emitted both material._Tex and material._Tex.m_Texture over its history.
            if (textureProperty.EndsWith(".m_Texture", StringComparison.Ordinal))
                textureProperty = textureProperty.Substring(0, textureProperty.Length - ".m_Texture".Length);
            state.HasTextureSwap = true;
            state.RequiresObjectRewrite = true;
            if (!state.TextureValues.TryGetValue(textureProperty, out var textures))
            {
                textures = new HashSet<Texture>();
                state.TextureValues.Add(textureProperty, textures);
            }
            foreach (var key in keys ?? Array.Empty<ObjectReferenceKeyframe>())
            {
                if (key.value == null) { textures.Add(null); continue; }
                var texture = key.value as Texture;
                if (!texture) state.UnsupportedObjectCurve = true;
                else textures.Add(texture);
            }
            if (textures.Count > AnimationSnapshot.MaxCandidatesPerRenderer)
                state.UnsupportedObjectCurve = true;
            return true;
        }

        // "material._MainTex_ST.x" -> "_MainTex_ST"; "material._Color.r" -> "_Color";
        // "material._Cutoff" -> "_Cutoff". Shader property names never contain '.'.
        internal static string MaterialPropertyName(string bindingProperty)
        {
            string name = bindingProperty.Substring("material.".Length);
            int dot = name.LastIndexOf('.');
            if (dot > 0 && name.Length - dot == 2 && "xyzwrgba".IndexOf(name[dot + 1]) >= 0)
                name = name.Substring(0, dot);
            return name;
        }

        // Counts one analyzed binding; false (and the snapshot fails) once past MaxBindings.
        private static bool NextBinding(AnimationSnapshot snapshot)
        {
            if (++snapshot.BindingCount <= AnimationSnapshot.MaxBindings) return true;
            snapshot.Fail("Animation analysis exceeded the binding limit; affected textures retained.");
            return false;
        }

        private static bool CountKeys(AnimationSnapshot snapshot, int count)
        {
            snapshot.KeyframeCount += count;
            if (snapshot.KeyframeCount <= AnimationSnapshot.MaxKeyframes) return true;
            snapshot.Fail("Animation keyframe inventory exceeded its safety limit; original textures retained.");
            return false;
        }

        private static int ParseSlot(string property)
        {
            const string marker = "m_Materials.Array.data[";
            int start = property.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return -1;
            start += marker.Length;
            int end = property.IndexOf(']', start);
            if (end < 0 || !int.TryParse(property.Substring(start, end - start), out int slot)) return -1;
            return slot >= 0 ? slot : -1;
        }

        // Bounds the distinct animated bindings per renderer; beyond the bound the renderer is retained.
        private static void CountBinding(AnimationRendererState state, string binding)
        {
            if (state.BindingKeys.Contains(binding)) return;
            if (state.BindingKeys.Count >= AnimationSnapshot.MaxBindingsPerRenderer) state.UnsupportedObjectCurve = true;
            else state.BindingKeys.Add(binding);
        }
    }

    internal sealed class AnimationRendererState
    {
        internal Renderer Renderer;
        internal readonly Dictionary<int, HashSet<Material>> SlotMaterials = new Dictionary<int, HashSet<Material>>();
        internal readonly Dictionary<string, HashSet<Texture>> TextureValues =
            new Dictionary<string, HashSet<Texture>>(StringComparer.Ordinal);
        // Distinct animated bindings (clip|path|property), bounded by MaxBindingsPerRenderer.
        internal readonly HashSet<string> BindingKeys = new HashSet<string>(StringComparer.Ordinal);
        // Material properties changed by float curves, e.g. "_MainTex_ST" or "_Color".
        internal readonly HashSet<string> FloatProperties = new HashSet<string>(StringComparer.Ordinal);
        // Value range per animated component, keyed like the binding without "material.",
        // e.g. "_MainTex_ST.z", "_Color.r" or "_Cutoff".
        internal readonly Dictionary<string, FloatRange> FloatRanges = new Dictionary<string, FloatRange>(StringComparer.Ordinal);

        internal void AddFloatRange(string component, FloatRange range)
        {
            if (!FloatRanges.TryGetValue(component, out var existing))
                FloatRanges.Add(component, existing = new FloatRange());
            existing.Union(range);
        }
        internal bool HasTextureSwap;
        internal bool HasMeshBinding;
        internal bool RequiresObjectRewrite;
        internal bool UnsupportedObjectCurve;
        internal bool ComplexTextureCrossProduct;

        internal AnimationRendererState(Renderer renderer) => Renderer = renderer;
    }

    // Conservative bound on the values a float curve can produce.
    internal sealed class FloatRange
    {
        internal float Min = float.PositiveInfinity, Max = float.NegativeInfinity;
        internal bool Bounded = true;
        internal bool HasValues => Min <= Max;

        internal void Include(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) { Bounded = false; return; }
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }

        internal void Union(FloatRange other)
        {
            Bounded &= other.Bounded;
            if (other.HasValues) { Include(other.Min); Include(other.Max); }
        }

        // Every Unity curve segment is a cubic Bezier in value (Hermite tangents are the 1/3-weight case;
        // weighted tangents use their weights), so it stays within its control values, including any
        // overshoot between keys. A segment with an infinite tangent holds the first key's value.
        // Wrap modes only repeat or hold existing segment values.
        internal static FloatRange OfCurve(Keyframe[] keys, bool unbounded)
        {
            var range = new FloatRange { Bounded = !unbounded };
            if (keys == null) return range;
            for (int i = 0; i < keys.Length; i++)
            {
                var a = keys[i];
                range.Include(a.value);
                if (i + 1 == keys.Length) continue;
                var b = keys[i + 1];
                if (float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent)) continue;
                float dt = b.time - a.time;
                float outWeight = (a.weightedMode & WeightedMode.Out) != 0 ? a.outWeight : 1f / 3;
                float inWeight = (b.weightedMode & WeightedMode.In) != 0 ? b.inWeight : 1f / 3;
                range.Include(a.value + a.outTangent * outWeight * dt);
                range.Include(b.value - b.inTangent * inWeight * dt);
            }
            return range;
        }
    }

    internal sealed class VirtualObjectBinding
    {
        internal readonly VirtualClip Clip;
        internal readonly EditorCurveBinding Binding;

        internal VirtualObjectBinding(VirtualClip clip, EditorCurveBinding binding)
        {
            Clip = clip;
            Binding = binding;
        }
    }
}
