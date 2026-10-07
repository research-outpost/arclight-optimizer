using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // One read-only model of what can change on the build avatar at runtime and what refers to what, shared by
    // every pass that needs a safety proof. Built from the avatar as it is when the pass runs; build it again after
    // a pass changes the hierarchy, components or controllers.
    //  - Animation: every binding of every clip reachable from the controllers NDMF knows (VRChat playable layers,
    //    other Animators) and legacy Animation components, resolved to the objects it really drives. Unity binds by
    //    path from the animator's object; a path that matches nothing animates nothing, a path that matches several
    //    same-named siblings is treated as animating all of them. Platform marker clips stand for proxy motions.
    //  - Implicit animation: VRChat drives visemes, eyelid blend shapes, eye and jaw bones without clips.
    //  - References: every serialized object reference held by a component, so a pass can tell whether anything
    //    else uses an object before replacing or removing it.
    //  - Transform motion: transforms whose local pose can change (animated, humanoid, PhysBone chains, constrained,
    //    eye and jaw bones).
    // Complete is false when any animation could not be read; passes must then treat everything as animated.
    internal sealed class AvatarAnalysis
    {
        internal sealed class Binding
        {
            public EditorCurveBinding Curve;
            public Object Target;           // The GameObject (for GameObject bindings) or component the curve drives.
            public Object[] Values;         // Object-curve keyframe values; null for float curves.
            public Transform Owner;         // The object the clip's paths start from.
            public bool Legacy;             // From a legacy Animation component rather than a controller.
            public AnimationCurve FloatCurve; // The float curve; null for object curves.
            public int Clip;                // Identifies the clip within this analysis (with Owner: one curve source).
            public bool Summed;             // Its value adds to others: an additive layer or playable, or a non-normalized Direct tree.
            public string ClipKey => Owner.GetInstanceID() + "/" + Clip;
            public string Property => Curve.propertyName ?? "";
        }

        public readonly GameObject Root;
        public bool Complete { get; private set; } = true;
        public readonly List<Binding> Bindings = new List<Binding>();
        // Every (owner, path) a curve uses, including paths that name nothing.
        internal readonly HashSet<(Transform Owner, string Path)> CurvePaths = new HashSet<(Transform Owner, string Path)>();
        private readonly Dictionary<Object, List<Binding>> byTarget = new Dictionary<Object, List<Binding>>();
        private readonly Dictionary<Object, HashSet<Component>> referencedBy = new Dictionary<Object, HashSet<Component>>();
        private readonly Dictionary<Component, List<Object>> referencesFrom = new Dictionary<Component, List<Object>>();
        private readonly Dictionary<SkinnedMeshRenderer, HashSet<string>> implicitBlendShapes = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();
        private readonly HashSet<Transform> movingLocally = new HashSet<Transform>();
        private readonly HashSet<Transform> scaling = new HashSet<Transform>();
        private readonly HashSet<Transform> otherMotion = new HashSet<Transform>();
        private readonly Dictionary<Transform, List<Component>> physBonesMoving = new Dictionary<Transform, List<Component>>();
        private readonly Dictionary<object, int> clipIds = new Dictionary<object, int>();

        private AvatarAnalysis(GameObject root) { Root = root; }

        // Reads the build avatar through NDMF's controller context (activated and deactivated here).
        internal static AvatarAnalysis Build(BuildContext context) => BuildTimings.Analysis(() => BuildUntimed(context));

        private static AvatarAnalysis BuildUntimed(BuildContext context)
        {
            var analysis = new AvatarAnalysis(context.AvatarRootObject);
            try
            {
                var controllers = context.ActivateExtensionContext<VirtualControllerContext>();
                try { analysis.AddControllers(controllers.Controllers); }
                finally { ControllerCommit.Preserving(context.AvatarRootObject, () => context.DeactivateExtensionContext<VirtualControllerContext>()); }
            }
            catch (Exception e) { Debug.LogException(e); analysis.Complete = false; }
            analysis.Finish();
            return analysis;
        }

        // For tests: virtual controllers keyed as NDMF keys them (an Animator, or a VRChat playable layer type).
        internal static AvatarAnalysis Build(GameObject root, IEnumerable<KeyValuePair<object, VirtualAnimatorController>> controllers)
        {
            var analysis = new AvatarAnalysis(root);
            analysis.AddControllers(controllers);
            analysis.Finish();
            return analysis;
        }

        private void AddControllers(IEnumerable<KeyValuePair<object, VirtualAnimatorController>> controllers)
        {
            foreach (var entry in controllers)
            {
                if (entry.Value == null) continue;
                var owner = entry.Key is Animator animator && animator ? animator.transform : Root.transform;
                var summed = Summed(entry.Key, entry.Value);
                // A synced layer keeps its own behaviours for the states it shares.
                foreach (var behaviour in entry.Value.Layers.SelectMany(l => l.SyncedLayerBehaviourOverrides.Values.SelectMany(v => v))) AddPlayAudio(owner, behaviour);
                foreach (var node in entry.Value.AllReachableNodes())
                {
                    if (node is VirtualState state) foreach (var behaviour in state.Behaviours) AddPlayAudio(owner, behaviour);
                    if (node is VirtualStateMachine machine) foreach (var behaviour in machine.Behaviours) AddPlayAudio(owner, behaviour);
                    if (!(node is VirtualClip clip) || clip.IsMarkerClip) continue;
                    bool sums = summed.Contains(clip);
                    foreach (var curve in clip.GetFloatCurveBindings()) Add(owner, curve, null, clip, clip.GetFloatCurve(curve), summed: sums);
                    foreach (var curve in clip.GetObjectCurveBindings())
                        Add(owner, curve, clip.GetObjectCurve(curve)?.Select(k => k.value).ToArray() ?? Array.Empty<Object>(), clip, summed: sums);
                }
            }
        }

        // Audio sources a VRC Animator Play Audio behaviour can start (resolved from its SourcePath, relative to the Animator's
        // object). An unreadable or unresolved path makes every source count as startable.
        private readonly HashSet<Transform> playAudioTargets = new HashSet<Transform>();
        private bool playAudioUnknown;

        private void AddPlayAudio(Transform owner, StateMachineBehaviour behaviour)
        {
            if (!behaviour || behaviour.GetType().Name != "VRCAnimatorPlayAudio") return;
            if (behaviour.GetType().GetField("SourcePath")?.GetValue(behaviour) is string path && (path.Length == 0 ? owner : owner.Find(path)) is Transform target && target)
                playAudioTargets.Add(target);
            else playAudioUnknown = true;
        }

        // Whether anything on the avatar besides Play On Awake can start this source.
        internal bool CanStartAudio(AudioSource source) => !Complete || playAudioUnknown || playAudioTargets.Contains(source.transform);
        // The objects Play Audio behaviours name by path, or null when one could not be resolved.
        internal IReadOnlyCollection<Transform> PlayAudioTargets => playAudioUnknown ? null : playAudioTargets;

        // Clips whose values add to what is below or beside them: every clip of an additive layer or of VRChat's Additive
        // playable, and every clip under a Direct tree that does not normalize its weights (except Arclight's own folded
        // layers, whose children never share a property).
        private static HashSet<VirtualClip> Summed(object key, VirtualAnimatorController controller)
        {
            var clips = new HashSet<VirtualClip>();
            bool additivePlayable = AnimationSnapshot.IsAdditivePlayableLayer(key);
            foreach (var layer in controller.Layers)
                foreach (var node in layer.AllReachableNodes())
                {
                    if ((additivePlayable || layer.BlendingMode == UnityEditor.Animations.AnimatorLayerBlendingMode.Additive) && node is VirtualClip clip) clips.Add(clip);
                    if (node is VirtualBlendTree tree && tree.BlendType == UnityEditor.Animations.BlendTreeType.Direct && !tree.NormalizedBlendValues && !AnimationSnapshot.FoldedLayers(tree))
                        foreach (var child in tree.AllReachableNodes())
                            if (child is VirtualClip nested) clips.Add(nested);
                }
            return clips;
        }

        // An analysis that could not read the avatar's animation; every query answers conservatively.
        internal static AvatarAnalysis Incomplete(GameObject root)
        {
            var analysis = new AvatarAnalysis(root) { Complete = false };
            analysis.Finish();
            return analysis;
        }

        // For callers that already hold clips (tests, legacy paths): each clip with the transform its paths start from.
        internal static AvatarAnalysis Build(GameObject root, IEnumerable<(Transform Owner, AnimationClip Clip)> clips)
        {
            var analysis = new AvatarAnalysis(root);
            foreach (var (owner, clip) in clips)
            {
                if (!clip) continue;
                foreach (var curve in AnimationUtility.GetCurveBindings(clip)) analysis.Add(owner, curve, null, clip, AnimationUtility.GetEditorCurve(clip, curve));
                foreach (var curve in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    analysis.Add(owner, curve, AnimationUtility.GetObjectReferenceCurve(clip, curve)?.Select(k => k.value).ToArray() ?? Array.Empty<Object>(), clip);
            }
            analysis.Finish();
            return analysis;
        }

        private void Finish()
        {
            try
            {
                foreach (var animation in Root.GetComponentsInChildren<Animation>(true))
                    foreach (var clip in AnimationUtility.GetAnimationClips(animation.gameObject))
                    {
                        if (!clip) continue;
                        foreach (var curve in AnimationUtility.GetCurveBindings(clip)) Add(animation.transform, curve, null, clip, AnimationUtility.GetEditorCurve(clip, curve), true);
                        foreach (var curve in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                            Add(animation.transform, curve, AnimationUtility.GetObjectReferenceCurve(clip, curve)?.Select(k => k.value).ToArray() ?? Array.Empty<Object>(), clip, null, true);
                    }
            }
            catch (Exception e) { Debug.LogException(e); Complete = false; }
            ScanReferences();
            ScanImplicit();
            ScanMotion();
        }

        private void Add(Transform owner, EditorCurveBinding curve, Object[] values, object clip, AnimationCurve floatCurve = null, bool legacy = false, bool summed = false)
        {
            if (!clipIds.TryGetValue(clip, out int id)) clipIds.Add(clip, id = clipIds.Count);
            CurvePaths.Add((owner, curve.path ?? ""));
            foreach (var transform in Resolve(owner, curve.path))
            {
                Object target = curve.type == typeof(GameObject) ? transform.gameObject
                    : curve.type == typeof(Transform) ? transform
                    : curve.type != null ? transform.GetComponent(curve.type) : null;
                if (!target) continue;
                var binding = new Binding { Curve = curve, Target = target, Values = values, Owner = owner, Legacy = legacy, FloatCurve = floatCurve, Clip = id, Summed = summed };
                Bindings.Add(binding);
                if (!byTarget.TryGetValue(target, out var list)) byTarget.Add(target, list = new List<Binding>());
                list.Add(binding);
            }
        }

        // Every transform the path can name, starting at owner (same-named siblings all match).
        internal static IEnumerable<Transform> Resolve(Transform owner, string path)
        {
            IEnumerable<Transform> current = new[] { owner };
            if (string.IsNullOrEmpty(path)) return current;
            foreach (string part in path.Split('/'))
            {
                string name = part;
                current = current.SelectMany(t => t.Cast<Transform>().Where(child => child.name == name)).ToArray();
            }
            return current;
        }

        // Curves that drive this GameObject or component (optionally only properties the predicate accepts).
        internal IEnumerable<Binding> BindingsOn(Object target, Func<string, bool> property = null) =>
            target && byTarget.TryGetValue(target, out var list) ? list.Where(b => property == null || property(b.Property)) : Enumerable.Empty<Binding>();

        internal bool IsAnimated(Object target, Func<string, bool> property = null) => !Complete || BindingsOn(target, property).Any();

        // Whether the object's active state, or that of any ancestor up to the avatar root, can change.
        internal bool ActivenessAnimated(GameObject gameObject)
        {
            if (!Complete) return true;
            for (var t = gameObject.transform; t; t = t == Root.transform ? null : t.parent)
                if (IsAnimated(t.gameObject, p => p == "m_IsActive")) return true;
            return false;
        }

        // Material property names an animation can change on this renderer ("material._Color.r" -> "_Color").
        internal IEnumerable<string> AnimatedMaterialProperties(Renderer renderer) =>
            BindingsOn(renderer, p => p.StartsWith("material.", StringComparison.Ordinal)).Select(b => b.Property.Substring(9).Split('.')[0]).Distinct();

        // Objects an animation can put into the renderer's material slots.
        internal IEnumerable<Material> SwappedMaterials(Renderer renderer) =>
            BindingsOn(renderer, p => p.StartsWith("m_Materials", StringComparison.Ordinal)).SelectMany(b => b.Values ?? Array.Empty<Object>()).OfType<Material>();

        // Every object any object curve can assign, anywhere on the avatar.
        internal IEnumerable<Object> AnimatedObjectValues => Bindings.Where(b => b.Values != null).SelectMany(b => b.Values).Where(v => v);

        // References change when a pass replaces objects (for example meshes); scan them again before relying on them.
        internal void RescanReferences() { referencedBy.Clear(); referencesFrom.Clear(); ScanReferences(); }

        private void ScanReferences()
        {
            foreach (var component in Root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component is Transform) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                    {
                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = iterator.objectReferenceValue;
                        if (!value || value == component) continue;
                        if (!referencedBy.TryGetValue(value, out var set)) referencedBy.Add(value, set = new HashSet<Component>());
                        set.Add(component);
                        if (!referencesFrom.TryGetValue(component, out var list)) referencesFrom.Add(component, list = new List<Object>());
                        list.Add(value);
                    }
                }
            }
        }

        // Objects this component holds serialized references to (scene objects and assets).
        internal IReadOnlyList<Object> ReferencesFrom(Component component) =>
            component && referencesFrom.TryGetValue(component, out var list) ? (IReadOnlyList<Object>)list : Array.Empty<Object>();

        // Components that hold a serialized reference to target, other than target itself.
        internal IReadOnlyCollection<Component> ReferencesTo(Object target) =>
            target && referencedBy.TryGetValue(target, out var set) ? (IReadOnlyCollection<Component>)set : Array.Empty<Component>();

        // VRChat's avatar descriptor animates these without clips. Read by serialized field name, so no SDK reference
        // is needed; a descriptor that cannot be read makes the analysis incomplete.
        private void ScanImplicit()
        {
            foreach (var component in Root.GetComponents<Component>())
            {
                if (!component || component.GetType().Name != "VRCAvatarDescriptor") continue;
                try
                {
                    using (var serialized = new SerializedObject(component))
                    {
                        var visemeMesh = serialized.FindProperty("VisemeSkinnedMesh")?.objectReferenceValue as SkinnedMeshRenderer;
                        var visemes = serialized.FindProperty("VisemeBlendShapes");
                        if (visemeMesh && visemes != null)
                            for (int i = 0; i < visemes.arraySize; i++) AddImplicit(visemeMesh, visemes.GetArrayElementAtIndex(i).stringValue);
                        string mouth = serialized.FindProperty("MouthOpenBlendShapeName")?.stringValue;
                        if (visemeMesh && !string.IsNullOrEmpty(mouth)) AddImplicit(visemeMesh, mouth);
                        var eyelids = serialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh")?.objectReferenceValue as SkinnedMeshRenderer;
                        var eyelidShapes = serialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                        if (eyelids && eyelids.sharedMesh && eyelidShapes != null)
                            for (int i = 0; i < eyelidShapes.arraySize; i++)
                            {
                                int index = eyelidShapes.GetArrayElementAtIndex(i).intValue;
                                if (index >= 0 && index < eyelids.sharedMesh.blendShapeCount) AddImplicit(eyelids, eyelids.sharedMesh.GetBlendShapeName(index));
                            }
                        foreach (string bone in new[] { "customEyeLookSettings.leftEye", "customEyeLookSettings.rightEye", "lipSyncJawBone" })
                            if (serialized.FindProperty(bone)?.objectReferenceValue is Transform driven && driven) { movingLocally.Add(driven); otherMotion.Add(driven); }
                    }
                }
                catch (Exception e) { Debug.LogException(e); Complete = false; }
            }
        }

        private void AddImplicit(SkinnedMeshRenderer renderer, string shape)
        {
            if (string.IsNullOrEmpty(shape)) return;
            if (!implicitBlendShapes.TryGetValue(renderer, out var set)) implicitBlendShapes.Add(renderer, set = new HashSet<string>(StringComparer.Ordinal));
            set.Add(shape);
        }

        // Blend shapes VRChat drives without clips (visemes, eyelids).
        internal bool BlendShapeDrivenByPlatform(SkinnedMeshRenderer renderer, string shape) =>
            !Complete || implicitBlendShapes.TryGetValue(renderer, out var set) && set.Contains(shape);

        // Blend shapes the platform or a clip can change on this renderer.
        internal bool BlendShapeAnimated(SkinnedMeshRenderer renderer, string shape) =>
            IsAnimated(renderer, p => p == "blendShape." + shape) ||
            implicitBlendShapes.TryGetValue(renderer, out var set) && set.Contains(shape);

        private void ScanMotion()
        {
            void Other(Transform t) { if (!t) return; movingLocally.Add(t); otherMotion.Add(t); }
            foreach (var binding in Bindings)
                if (binding.Target is Transform transform) Other(transform);
            foreach (var animator in Root.GetComponentsInChildren<Animator>(true))
            {
                if (!animator.isHuman) continue;
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    var transform = animator.GetBoneTransform(bone);
                    if (transform) Other(transform);
                }
            }
            foreach (var component in Root.GetComponentsInChildren<Component>(true))
            {
                if (!component) continue;
                if (component is IConstraint constraint) { Other(component.transform); continue; }
                // Physics moves a non-kinematic body, and either end of a joint, every frame.
                // An animation can turn a kinematic body's physics on.
                if (component is Rigidbody body) { if (!body.isKinematic || IsAnimated(body, p => p == "m_IsKinematic")) Other(component.transform); continue; }
                if (component is Joint joint) { Other(component.transform); if (joint.connectedBody) Other(joint.connectedBody.transform); continue; }
                string type = component.GetType().Name;
                if (type.StartsWith("VRC", StringComparison.Ordinal) && type.EndsWith("Constraint", StringComparison.Ordinal))
                {
                    using (var serialized = new SerializedObject(component))
                        Other(serialized.FindProperty("TargetTransform")?.objectReferenceValue as Transform ?? component.transform);
                }
                else if (type == "VRCHeadChop")
                {
                    // Head Chop scales its target bones (for the local player); every transform it names moves and scales.
                    using (var serialized = new SerializedObject(component))
                    {
                        var iterator = serialized.GetIterator();
                        while (iterator.Next(true))
                            if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is Transform target && target)
                            { Other(target); scaling.Add(target); }
                    }
                }
                else if (type == "VRCPhysBone")
                {
                    using (var serialized = new SerializedObject(component))
                    {
                        var start = serialized.FindProperty("rootTransform")?.objectReferenceValue as Transform ?? component.transform;
                        // Conservative: the whole chain below the root moves (ignored transforms move with their parents), and the root
                        // itself unless the PhysBone leaves it still (UnusedObjectRemover.RootMoves).
                        bool rootMoves = UnusedObjectRemover.RootMoves(component);
                        foreach (var moving in start.GetComponentsInChildren<Transform>(true))
                            if (moving != start || rootMoves)
                            {
                                movingLocally.Add(moving);
                                if (!physBonesMoving.TryGetValue(moving, out var list)) physBonesMoving[moving] = list = new List<Component>();
                                list.Add(component);
                            }
                    }
                }
            }
        }

        // Whether something other than animation can change this transform's scale at runtime (VRC Head Chop).
        internal bool ScaleChanges(Transform transform) => !Complete || scaling.Contains(transform);

        // Whether this transform's own local pose can change at runtime.
        internal bool MovesLocally(Transform transform) => !Complete || movingLocally.Contains(transform);

        // Whether something other than this PhysBone can change the transform's local pose.
        internal bool MovesLocallyBesides(Transform transform, Component physBone)
        {
            if (!Complete || otherMotion.Contains(transform)) return true;
            return physBonesMoving.TryGetValue(transform, out var list) && list.Any(p => p != physBone);
        }

        // Whether the pose of b relative to a can change: some transform between them (excluding their common
        // ancestor) moves locally.
        internal bool MovesRelative(Transform a, Transform b)
        {
            if (!Complete) return true;
            var chainA = Chain(a); var chainB = Chain(b);
            var common = chainA.Intersect(chainB).FirstOrDefault();
            return chainA.TakeWhile(t => t != common).Concat(chainB.TakeWhile(t => t != common)).Any(movingLocally.Contains);
        }

        private List<Transform> Chain(Transform t)
        {
            var chain = new List<Transform>();
            for (; t; t = t.parent) chain.Add(t);
            return chain;
        }
    }
}
