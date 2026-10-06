using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Removes objects and components that can never have a visible, audible or behavioural effect, by mark and sweep
    // over the build avatar.
    //  - Kept from the start (roots): every component of a type this pass does not understand (VRChat SDK components,
    //    scripts, anything unknown), wherever it is; and components of understood types that can run: their object can
    //    ever be active (it and every ancestor is active, or its activation is animated) and, for renderers, lights
    //    and audio, they are enabled or their enabling is animated. A renderer also needs a mesh (or a mesh swap).
    //  - Kept because something kept needs it: every object a kept component references (bones, root bones, probe
    //    anchors, PhysBone colliders, constraint sources, descriptor fields...), the GameObject and parents of every
    //    kept component, humanoid bones of a kept Animator, the MeshFilter of a kept MeshRenderer, and a kept
    //    component's required components.
    //  - Understood types: Transform, MeshFilter, MeshRenderer, SkinnedMeshRenderer, ParticleSystem(+Renderer),
    //    TrailRenderer, LineRenderer, Light, AudioSource, Cloth, Unity constraints (kept with their transform), colliders,
    //    rigidbodies, joints, cameras, Animators and VRC Spatial Audio Source. An AudioSource with Play On Awake off that no
    //    VRC Animator Play Audio behaviour names (and whose Play On Awake no animation changes) can never be heard.
    // Swept: whole GameObjects with nothing kept anywhere below them, and understood components that were not kept.
    // An animation that targets a swept object animated nothing visible, so it is left as is (Unity ignores
    // missing paths). Nothing is removed when the avatar's animation cannot be read.
    internal static class UnusedObjectRemover
    {
        internal sealed class Result { public int GameObjects, Components; public readonly List<string> Names = new List<string>(); }

        private static readonly Type[] Understood =
        {
            typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(ParticleSystem),
            typeof(ParticleSystemRenderer), typeof(TrailRenderer), typeof(LineRenderer), typeof(Light), typeof(AudioSource), typeof(Cloth)
        };

        // VRChat SDK components understood by type name (the SDK is not referenced): each acts only while its object
        // is active and it is enabled; a PhysBone collider acts only through the PhysBones that list it.
        private static readonly HashSet<string> UnderstoodVrc = new HashSet<string>(StringComparer.Ordinal)
        {
            "VRCPhysBone", "VRCPhysBoneCollider", "VRCContactReceiver", "VRCContactSender", "VRCParentConstraint",
            "VRCPositionConstraint", "VRCRotationConstraint", "VRCScaleConstraint", "VRCAimConstraint", "VRCLookAtConstraint",
            // Shapes how its AudioSource is heard; with that source it does nothing (it requires one, so they go together).
            "VRCSpatialAudioSource",
        };

        // Physics and other Unity components that act only while their object is active (and, where they have one, enabled):
        // colliders, rigidbodies, joints, cameras and Animators.
        private static bool IsUnderstood(Component component) =>
            component is IConstraint || Understood.Any(t => t == component.GetType()) || UnderstoodVrc.Contains(component.GetType().Name) ||
            component is Collider || component is Rigidbody || component is Joint || component is Camera || component is Animator;

        internal static Result Run(AvatarAnalysis analysis)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            var components = root.GetComponentsInChildren<Component>(true).Where(c => c).ToArray();
            var kept = new HashSet<Component>();
            var pending = new Stack<Component>();
            void Keep(Component component)
            {
                if (component && kept.Add(component)) pending.Push(component);
            }
            void KeepObject(Object value)
            {
                if (value is Component c) Keep(c);
                else if (value is GameObject g) Keep(g.transform);
            }

            foreach (var component in components)
            {
                // VRCFury's debug info is an editor-only note (stripped before upload) that nothing reads in game, so it
                // keeps nothing alive; it goes with its object when that object is swept.
                if (component.GetType().FullName == "VF.Model.VRCFuryDebugInfo" && !Exclusions.Excluded(component)) continue;
                if (!IsUnderstood(component) || CanRun(component, analysis) || Exclusions.Excluded(component) ||
                    component.GetType().Name == "VRCPhysBone" && NeedsExcludedWrite(root, component)) Keep(component);
            }
            Keep(root.transform);
            // An object-reference key can hand an avatar object to a component later (an AimConstraint's world-up object, say).
            foreach (var value in analysis.AnimatedObjectValues)
                if (((value as Component)?.transform ?? (value as GameObject)?.transform) is Transform held && held.IsChildOf(root.transform)) KeepObject(value);

            while (pending.Count > 0)
            {
                var component = pending.Pop();
                Keep(component.transform);
                if (component is Transform transform && transform.parent && transform != root.transform) Keep(transform.parent);
                if (component is Transform keptTransform && CanBeActive(keptTransform.gameObject, analysis))
                    foreach (var constraint in keptTransform.GetComponents<Component>().Where(c => c is IConstraint))
                        if (((Behaviour)constraint).enabled || analysis.IsAnimated(constraint, p => p == "m_Enabled")) Keep(constraint);
                foreach (var value in analysis.ReferencesFrom(component)) KeepObject(value);
                // A PhysBone simulates every transform below its root, end bones included: they shape how the bones
                // above them swing, so the whole chain stays with the PhysBone.
                if (component.GetType().Name == "VRCPhysBone")
                    foreach (var chain in PhysBoneRoot(component).GetComponentsInChildren<Transform>(true)) Keep(chain);
                if (component is MeshRenderer) Keep(component.GetComponent<MeshFilter>());
                if (component is ParticleSystem) Keep(component.GetComponent<ParticleSystemRenderer>());
                if (component is ParticleSystemRenderer) Keep(component.GetComponent<ParticleSystem>());
                if (component is Animator animator && animator.isHuman)
                    for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                        if (animator.GetBoneTransform(bone) is Transform boneTransform && boneTransform) Keep(boneTransform);
                foreach (var required in RequiredBy(component)) Keep(required);
            }

            // Sweep: topmost GameObjects with nothing kept in their subtree, then unkept understood components.
            var keptTransforms = new HashSet<Transform>(kept.OfType<Transform>());
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (!transform || transform == root.transform || keptTransforms.Contains(transform)) continue;
                if (transform.parent && !keptTransforms.Contains(transform.parent)) continue; // An ancestor goes instead.
                result.GameObjects++;
                result.Names.Add(transform.name);
                foreach (var physBone in transform.GetComponentsInChildren<Component>(true).Where(c => c && c.GetType().Name == "VRCPhysBone"))
                    KeepIgnoredByOthers(root, physBone);
                Object.DestroyImmediate(transform.gameObject);
            }
            foreach (var component in components)
            {
                // Components on the avatar root stay: VRChat reads the root's Animator (its rig) even when it is disabled.
                if (!component || kept.Contains(component) || component is Transform || !IsUnderstood(component) || component.transform == root.transform) continue;
                // Dependents first (a ParticleSystemRenderer before its ParticleSystem).
                foreach (var dependent in component.GetComponents<Component>().Where(c => c && c != component && !kept.Contains(c) && Requires(c, component)))
                { Object.DestroyImmediate(dependent); result.Components++; }
                if (!component) continue;
                result.Names.Add(component.name + " (" + component.GetType().Name + ")");
                if (component.GetType().Name == "VRCPhysBone") KeepIgnoredByOthers(root, component);
                Object.DestroyImmediate(component);
                result.Components++;
            }
            return result;
        }

        // Whether an understood component can ever do anything on its own.
        private static bool CanRun(Component component, AvatarAnalysis analysis)
        {
            if (component is Transform) return false; // Kept only when something needs it.
            if (component is MeshFilter) return false; // Only through its renderer.
            if (component is ParticleSystemRenderer) return false; // With its system.
            if (component is IConstraint) return false; // Kept with its transform below.
            if (!CanBeActive(component.gameObject, analysis)) return false;
            // Kept through the PhysBones that use it, unless Global Collision is on: then it can push PhysBones that never list it
            // (other players' included).
            if (component.GetType().Name == "VRCPhysBoneCollider")
                return GlobalCollision(component, analysis) && (((Behaviour)component).enabled || analysis.IsAnimated(component, p => p == "m_Enabled"));
            // Spatial audio settings act through their AudioSource only.
            if (component.GetType().Name == "VRCSpatialAudioSource") return component.GetComponent<AudioSource>() is AudioSource source && source && CanRun(source, analysis);
            if (component is Renderer renderer)
            {
                if (!renderer.enabled && !analysis.IsAnimated(renderer, p => p == "m_Enabled")) return false;
                if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh || analysis.IsAnimated(skinned, p => p == "m_Mesh");
                if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter && (filter.sharedMesh || analysis.IsAnimated(filter, p => p == "m_Mesh"));
                }
                return true;
            }
            // Play On Awake off: showing or enabling the source does not start it, and nothing else on an avatar can unless a VRC
            // Animator Play Audio behaviour names it, so it is silent in every state.
            if (component is AudioSource audio && !audio.playOnAwake && !analysis.IsAnimated(audio, p => p == "m_PlayOnAwake") && !analysis.CanStartAudio(audio))
                return false;
            // No clip, muted or at volume 0, with no animation and no Play Audio behaviour that could change that: silent in every state.
            if (component is AudioSource silent && (!silent.clip || silent.mute || silent.volume == 0) && !analysis.IsAnimated(silent) && !analysis.CanStartAudio(silent))
                return false;
            // An Animator with no controller plays nothing and writes no pose, however its humanoid Avatar maps the bones. Outfit
            // model roots merged into the avatar keep one, and its humanoid map then pins and "moves" the whole outfit armature.
            // It goes unless an animation could give it a controller or switch it (the avatar root's own Animator always stays).
            if (component is Animator idle && !idle.runtimeAnimatorController && !analysis.IsAnimated(idle)) return false;
            // A contact sender with no collision tags matches no receiver (contacts meet only on a shared tag), here or on anyone else.
            if (component.GetType().Name == "VRCContactSender" && NoTags(component, analysis)) return false;
            if (component is Behaviour behaviour) return behaviour.enabled || analysis.IsAnimated(behaviour, p => p == "m_Enabled");
            if (component is Cloth cloth) return cloth.enabled || analysis.IsAnimated(cloth, p => p == "m_Enabled");
            if (component is Collider collider) return collider.enabled || analysis.IsAnimated(collider, p => p == "m_Enabled");
            return true; // ParticleSystem: plays whenever its object is active.
        }

        private static bool NoTags(Component contact, AvatarAnalysis analysis)
        {
            if (analysis.IsAnimated(contact)) return false;
            using (var serialized = new UnityEditor.SerializedObject(contact))
            {
                var tags = serialized.FindProperty("collisionTags");
                if (tags == null || !tags.isArray) return false;
                for (int i = 0; i < tags.arraySize; i++) if (!string.IsNullOrEmpty(tags.GetArrayElementAtIndex(i).stringValue)) return false;
                return true;
            }
        }

        private static bool GlobalCollision(Component collider, AvatarAnalysis analysis)
        {
            if (analysis.IsAnimated(collider, p => p.StartsWith("globalCollision", StringComparison.Ordinal))) return true;
            using (var serialized = new UnityEditor.SerializedObject(collider))
            {
                var global = serialized.FindProperty("globalCollision");
                return global != null && (global.propertyType == UnityEditor.SerializedPropertyType.Boolean ? global.boolValue : global.intValue != 0);
            }
        }

        // VRChat SDK 3.8+: a PhysBone with Ignore Other Phys Bones on treats the root of every other PhysBone in its chain
        // as ignored, enabled or not. Removing such a PhysBone would hand its bones to the outer one, so its root is first
        // written into the outer PhysBone's own ignore list, which is what the SDK already did.
        internal static void KeepIgnoredByOthers(GameObject root, Component removed)
        {
            var removedRoot = PhysBoneRoot(removed);
            foreach (var other in IgnoringOuters(root, removed).ToList())
                using (var serialized = new UnityEditor.SerializedObject(other))
                {
                    var ignored = serialized.FindProperty("ignoreTransforms");
                    ignored.arraySize++;
                    ignored.GetArrayElementAtIndex(ignored.arraySize - 1).objectReferenceValue = removedRoot;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
        }

        // Whether removing this PhysBone would have to write into an excluded PhysBone's ignore list; such a PhysBone stays, as
        // Arclight Exclude leaves its components untouched and skipping the write would hand its bones to the outer chain.
        internal static bool NeedsExcludedWrite(GameObject root, Component physBone) => IgnoringOuters(root, physBone).Any(Exclusions.Excluded);

        // The other PhysBones whose ignore list must gain this PhysBone's root if it goes.
        private static IEnumerable<Component> IgnoringOuters(GameObject root, Component removed)
        {
            var removedRoot = PhysBoneRoot(removed);
            foreach (var other in root.GetComponentsInChildren<Component>(true).Where(c => c && c != removed && c.GetType().Name == "VRCPhysBone").ToList())
            {
                var otherRoot = PhysBoneRoot(other);
                if (removedRoot == otherRoot || !removedRoot.IsChildOf(otherRoot)) continue;
                using (var serialized = new UnityEditor.SerializedObject(other))
                {
                    var ignoreOthers = serialized.FindProperty("ignoreOtherPhysBones");
                    var ignored = serialized.FindProperty("ignoreTransforms");
                    if (ignoreOthers == null || !ignoreOthers.boolValue || ignored == null || !ignored.isArray) continue;
                    bool listed = false;
                    for (int i = 0; i < ignored.arraySize && !listed; i++) listed = ignored.GetArrayElementAtIndex(i).objectReferenceValue == removedRoot;
                    if (!listed) yield return other;
                }
            }
        }

        internal static Transform PhysBoneRoot(Component physBone)
        {
            using (var serialized = new UnityEditor.SerializedObject(physBone))
                return serialized.FindProperty("rootTransform")?.objectReferenceValue is Transform root && root ? root : physBone.transform;
        }

        // Whether the PhysBone rotates its root transform. It does unless Multi-Child Type is Ignore and the root has more
        // than one child the chain simulates; a single child, or an endpoint alone, swings the root.
        internal static bool RootMoves(Component physBone)
        {
            var start = PhysBoneRoot(physBone);
            using (var serialized = new UnityEditor.SerializedObject(physBone))
            {
                var type = serialized.FindProperty("multiChildType");
                if (type == null || type.enumValueIndex != 0) return true;
                var ignoreList = serialized.FindProperty("ignoreTransforms");
                var ignored = new HashSet<Transform>();
                for (int i = 0; ignoreList != null && i < ignoreList.arraySize; i++)
                    if (ignoreList.GetArrayElementAtIndex(i).objectReferenceValue is Transform t && t) ignored.Add(t);
                return start.Cast<Transform>().Count(child => !ignored.Contains(child)) <= 1;
            }
        }

        internal static bool CanBeActive(GameObject gameObject, AvatarAnalysis analysis)
        {
            for (var t = gameObject.transform; t; t = t == analysis.Root.transform ? null : t.parent)
                if (!t.gameObject.activeSelf && !analysis.IsAnimated(t.gameObject, p => p == "m_IsActive")) return false;
            return true;
        }

        private static IEnumerable<Component> RequiredBy(Component component) =>
            component.GetComponents<Component>().Where(other => other && other != component && Requires(component, other));

        private static bool Requires(Component component, Component other) =>
            component.GetType().GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>()
                .Any(r => new[] { r.m_Type0, r.m_Type1, r.m_Type2 }.Any(t => t != null && t.IsInstanceOfType(other)));
    }
}
