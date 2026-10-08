using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // Operations that cannot work, read straight from the avatar's data (VRChat creator docs and SDK 3.10.5): conditions that
    // don't fit their parameter's type, face bindings to blendshapes the mesh lacks, writes to VRChat's read-only parameters,
    // contacts that can never match, Play Audio choosing clips by a parameter that isn't synced, unsupported driver and layer
    // control operations, VRChat's hard component limits and global plane colliders. Each is judged on the uploaded version
    // (see Breakage): build tools add and rename parameters, contacts and colliders.
    internal static partial class AvatarAnalyzer
    {
        // VRChat's built-in animator parameters, which VRChat sets itself: a menu or driver cannot change them. VRCEmote and
        // VRCFaceBlendH/V are left out: menus and drivers set those on purpose.
        internal static readonly HashSet<string> ReadOnlyParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight", "AngularY",
            "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode",
            "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled", "ScaleModified", "ScaleFactor",
            "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent"
        };

        // VRChat's limits per avatar (creators.vrchat.com: animator parameters and Avatar Dynamics pages).
        internal const int MaxPhysBones = 256, MaxColliders = 256, MaxContacts = 256, MaxPhysBoneTransforms = 256, MaxExpressionParameters = 8192;

        private static void SetupChecks(Avatar avatar, List<Finding> findings)
        {
            foreach (var playable in avatar.Playables) ConditionTypes(playable, findings);
            FaceBindings(avatar, findings);
            ReadOnlyWrites(avatar, findings);
            Behaviourals(avatar, findings);
            DynamicsChecks(avatar, findings);
        }

        // 1. A transition condition whose test doesn't fit its parameter's type (the parameter was recreated with another type, or
        // the controller was edited as text): Unity never takes that transition.
        private static void ConditionTypes(Playable playable, List<Finding> findings)
        {
            var controller = playable.Controller;
            var types = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            foreach (var p in controller.parameters) types[p.name] = p.type;
            bool Fits(AnimatorControllerParameterType type, AnimatorConditionMode mode)
            {
                switch (type)
                {
                    case AnimatorControllerParameterType.Bool: return mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;
                    case AnimatorControllerParameterType.Trigger: return mode == AnimatorConditionMode.If;
                    case AnimatorControllerParameterType.Int:
                        return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less || mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
                    default: return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less;
                }
            }
            var wrong = new List<string>();
            foreach (var layer in controller.layers)
                foreach (var machine in Machines(layer.stateMachine))
                {
                    var transitions = machine.anyStateTransitions.Cast<AnimatorTransitionBase>().Concat(machine.entryTransitions)
                        .Concat(machine.states.Where(s => s.state).SelectMany(s => s.state.transitions))
                        .Concat(machine.stateMachines.Where(c => c.stateMachine).SelectMany(c => machine.GetStateMachineTransitions(c.stateMachine)));
                    foreach (var transition in transitions.Where(t => t))
                        foreach (var condition in transition.conditions)
                        {
                            if (!types.TryGetValue(condition.parameter ?? "", out var type) || Fits(type, condition.mode)) continue;
                            string line = "\"" + condition.parameter + "\" is " + type + " but is tested with " + condition.mode + " (" + layer.name + " → " + machine.name + ")";
                            if (!wrong.Contains(line)) wrong.Add(line);
                        }
                }
            if (wrong.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.Broken, Key = "condtype|" + playable.Name, Playable = playable.Name, Target = controller,
                Title = playable.Name + " has " + N(wrong.Count, "transition condition") + " that can't work",
                Detail = "A Bool can only be tested with true or false, a Float with Greater or Less, and an Int with Greater, Less, Equals or NotEqual. These tests don't fit their parameter's type, so those transitions never happen:\n" + Bullets(wrong),
                Fix = "Select each transition and set the condition again. Usually the parameter was deleted and recreated with another type; pick the test you meant for the new type.",
                Identity = string.Join("\n", wrong)
            });
        }

        // 2. Lip sync and eyelid blendshapes chosen on the descriptor that the mesh doesn't have.
        private static void FaceBindings(Avatar avatar, List<Finding> findings)
        {
            var wrong = new List<string>();
            using (var serialized = new SerializedObject(avatar.Descriptor))
            {
                var lipSync = serialized.FindProperty("lipSync");
                string mode = lipSync != null && lipSync.propertyType == SerializedPropertyType.Enum && lipSync.enumValueIndex >= 0 ? lipSync.enumNames[lipSync.enumValueIndex] : "";
                var visemeMesh = (serialized.FindProperty("VisemeSkinnedMesh")?.objectReferenceValue as SkinnedMeshRenderer)?.sharedMesh;
                bool Lacks(Mesh mesh, string shape) => !string.IsNullOrEmpty(shape) && shape != "-none-" && mesh.GetBlendShapeIndex(shape) < 0;
                if (visemeMesh && mode == "JawFlapBlendShape")
                {
                    string shape = serialized.FindProperty("MouthOpenBlendShapeName")?.stringValue;
                    if (Lacks(visemeMesh, shape)) wrong.Add("Lip sync (mouth open): \"" + shape + "\" is not on " + visemeMesh.name);
                }
                if (visemeMesh && mode == "VisemeBlendShape")
                {
                    var shapes = serialized.FindProperty("VisemeBlendShapes");
                    for (int i = 0; shapes != null && i < shapes.arraySize; i++)
                    {
                        string shape = shapes.GetArrayElementAtIndex(i).stringValue;
                        if (Lacks(visemeMesh, shape)) wrong.Add("Lip sync viseme " + i + ": \"" + shape + "\" is not on " + visemeMesh.name);
                    }
                }
                var eyelidType = serialized.FindProperty("customEyeLookSettings.eyelidType");
                bool eyeLook = serialized.FindProperty("enableEyeLook")?.boolValue ?? false;
                if (eyeLook && eyelidType != null && eyelidType.propertyType == SerializedPropertyType.Enum && eyelidType.enumValueIndex >= 0 &&
                    eyelidType.enumNames[eyelidType.enumValueIndex] == "Blendshapes" &&
                    serialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh")?.objectReferenceValue is SkinnedMeshRenderer eyelids && eyelids.sharedMesh)
                {
                    var indices = serialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                    string[] slots = { "Blink", "Looking Up", "Looking Down" };
                    for (int i = 0; indices != null && i < indices.arraySize; i++)
                    {
                        int index = indices.GetArrayElementAtIndex(i).intValue;
                        if (index >= eyelids.sharedMesh.blendShapeCount) // -1 is "none".
                            wrong.Add("Eyelids (" + (i < slots.Length ? slots[i] : "slot " + i) + "): blendshape number " + index + ", but " + eyelids.sharedMesh.name + " has " + eyelids.sharedMesh.blendShapeCount);
                    }
                }
            }
            if (wrong.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.Broken, Key = "face|", Target = avatar.Descriptor,
                Title = N(wrong.Count, "face blendshape") + " chosen on the avatar descriptor " + (wrong.Count == 1 ? "doesn't" : "don't") + " exist",
                Detail = "VRChat can't move a blendshape the mesh doesn't have, so " + (wrong.Count == 1 ? "that part" : "those parts") + " of lip sync or blinking do nothing:\n" + Bullets(wrong),
                Fix = "Select the avatar, open Lip Sync or Eye Look on the VRC Avatar Descriptor and pick the blendshapes again (they are often renamed or removed when the mesh is updated).",
                Identity = string.Join("\n", wrong)
            });
        }

        // 3. Menu controls and parameter drivers that set one of VRChat's own parameters: VRChat overwrites them, so the change never sticks.
        private static void ReadOnlyWrites(Avatar avatar, List<Finding> findings)
        {
            var wrong = new List<string>();
            foreach (var control in avatar.Controls.Where(c => ReadOnlyParameters.Contains(c.Parameter)))
                wrong.Add("Menu " + control.Path + " sets \"" + control.Parameter + "\"");
            foreach (var playable in avatar.Playables)
            {
                var where = Where(playable);
                foreach (var driver in Behaviours(playable.Controller).Where(b => b && b.GetType().Name == "VRCAvatarParameterDriver").Distinct())
                    using (var serialized = new SerializedObject(driver))
                    {
                        var entries = serialized.FindProperty("parameters");
                        for (int i = 0; entries != null && i < entries.arraySize; i++)
                        {
                            string name = entries.GetArrayElementAtIndex(i).FindPropertyRelative("name")?.stringValue;
                            if (name != null && ReadOnlyParameters.Contains(name))
                                wrong.Add("Parameter Driver in " + (where.TryGetValue(driver, out string place) ? place : playable.Name) + " sets \"" + name + "\"");
                        }
                    }
            }
            wrong = wrong.Distinct().ToList();
            if (wrong.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.Broken, Key = "readonly|", Target = avatar.MenuAsset ? avatar.MenuAsset : avatar.Descriptor,
                Title = N(wrong.Count, "menu control or driver", "sets", "set") + " a parameter only VRChat can change",
                Detail = "VRChat sets these parameters itself (gestures, movement, viseme, scale...), so whatever these write is replaced at once and has no effect:\n" + Bullets(wrong),
                Fix = "Make your own parameter (add it to Expression Parameters and the FX controller) and set that instead. Reading these parameters in animators is fine.",
                Identity = string.Join("\n", wrong)
            });
        }

        // 4 to 6. State behaviours: Play Audio choosing clips by a parameter that isn't an Expression Parameter, drivers adding to a
        // Bool, and Animator Layer Controls changing the weight of layer 0, which Unity always plays at weight 1.
        private static void Behaviourals(Avatar avatar, List<Finding> findings)
        {
            var expressions = new HashSet<string>(avatar.Expressions.Select(e => e.Name), StringComparer.Ordinal);
            var audio = new List<string>();
            var adds = new List<string>();
            var baseLayer = new List<string>();
            Playable first = null, firstAdd = null, firstBase = null;
            string EnumName(SerializedProperty p) => p != null && p.propertyType == SerializedPropertyType.Enum && p.enumValueIndex >= 0 ? p.enumNames[p.enumValueIndex] : "";
            foreach (var playable in avatar.Playables)
            {
                var where = Where(playable);
                var types = playable.Controller.parameters.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().type, StringComparer.Ordinal);
                foreach (var behaviour in Behaviours(playable.Controller).Where(b => b).Distinct())
                {
                    string place = where.TryGetValue(behaviour, out string w) ? w : playable.Name;
                    string kind = behaviour.GetType().Name;
                    using (var serialized = new SerializedObject(behaviour))
                    {
                        if (kind == "VRCAnimatorPlayAudio" && EnumName(serialized.FindProperty("PlaybackOrder")) == "Parameter" &&
                            EnumName(serialized.FindProperty("ClipsApplySettings")) != "NeverApply")
                        {
                            string name = serialized.FindProperty("ParameterName")?.stringValue;
                            if (string.IsNullOrEmpty(name) || !expressions.Contains(name))
                            {
                                audio.Add(place + (string.IsNullOrEmpty(name) ? " (no parameter set)" : " (\"" + name + "\")"));
                                first = first ?? playable;
                            }
                        }
                        else if (kind == "VRCAvatarParameterDriver")
                        {
                            var entries = serialized.FindProperty("parameters");
                            for (int i = 0; entries != null && i < entries.arraySize; i++)
                            {
                                var entry = entries.GetArrayElementAtIndex(i);
                                string name = entry.FindPropertyRelative("name")?.stringValue;
                                if (EnumName(entry.FindPropertyRelative("type")) != "Add" || name == null || !types.TryGetValue(name, out var type) || type != AnimatorControllerParameterType.Bool) continue;
                                adds.Add(place + " adds to \"" + name + "\"");
                                firstAdd = firstAdd ?? playable;
                            }
                        }
                        else if (kind == "VRCAnimatorLayerControl" && serialized.FindProperty("layer")?.intValue == 0)
                        {
                            float goal = serialized.FindProperty("goalWeight")?.floatValue ?? 1;
                            if (goal == 1) continue;
                            baseLayer.Add(place + " sets layer 0 of " + EnumName(serialized.FindProperty("playable")) + " to weight " + goal);
                            firstBase = firstBase ?? playable;
                        }
                    }
                }
            }
            if (audio.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Broken, Key = "playaudio|", Playable = first.Name, Target = first.Controller,
                    Title = N(audio.Count, "Play Audio behaviour") + " " + (audio.Count == 1 ? "chooses" : "choose") + " clips by a parameter that isn't an Expression Parameter",
                    Detail = "With Playback Order set to Parameter, VRChat reads the clip number from an Expression Parameter. Without one it always plays the first clip:\n" + Bullets(audio),
                    Fix = "Add the parameter (Int) to Expression Parameters, or set Playback Order to Random, Unique Random or Roundabout.",
                    Identity = string.Join("\n", audio)
                });
            if (adds.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Broken, Key = "driveradd|", Playable = firstAdd.Name, Target = firstAdd.Controller,
                    Title = N(adds.Count, "Parameter Driver") + " " + (adds.Count == 1 ? "adds" : "add") + " to a Bool",
                    Detail = "Add only works on Int and Float parameters, so these entries do nothing:\n" + Bullets(adds),
                    Fix = "Use Set (true or false) for a Bool, or change the parameter to an Int if you need to count.",
                    Identity = string.Join("\n", adds)
                });
            if (baseLayer.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "layer0|", Playable = firstBase.Name, Target = firstBase.Controller,
                    Title = N(baseLayer.Count, "Animator Layer Control") + " " + (baseLayer.Count == 1 ? "changes" : "change") + " the weight of layer 0",
                    Detail = "Unity always plays the first layer of a controller at full weight, so this changes nothing:\n" + Bullets(baseLayer),
                    Fix = "Point the Layer Control at the layer you meant (layers count from 0 at the top), or move what should fade into its own layer.",
                    Identity = string.Join("\n", baseLayer)
                });
        }

        // 7 and 8. Contacts that can never touch anything, proximity receivers on a parameter that isn't a Float, VRChat's hard
        // limits on PhysBones, colliders, contacts and parameters, and plane colliders set to collide globally.
        private static void DynamicsChecks(Avatar avatar, List<Finding> findings)
        {
            var objects = avatar.Root.GetComponentsInChildren<Transform>(true).Where(t => !EditorOnly(t, avatar.Root.transform)).ToList();
            var components = objects.SelectMany(t => t.GetComponents<Component>()).Where(c => c).ToList();
            List<Component> Of(string type) => components.Where(c => c.GetType().Name == type).ToList();
            var bones = Of("VRCPhysBone");
            var colliders = Of("VRCPhysBoneCollider");
            var senders = Of("VRCContactSender");
            var receivers = Of("VRCContactReceiver");
            var floats = avatar.Playables.SelectMany(p => p.Controller.parameters).GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.Select(p => p.type).ToList(), StringComparer.Ordinal);

            var contacts = new List<string>();
            Component firstContact = null;
            foreach (var contact in senders.Concat(receivers))
                using (var serialized = new SerializedObject(contact))
                {
                    var tags = serialized.FindProperty("collisionTags");
                    bool none = tags != null && tags.isArray && Enumerable.Range(0, tags.arraySize).All(i => string.IsNullOrEmpty(tags.GetArrayElementAtIndex(i).stringValue));
                    string kind = contact.GetType().Name == "VRCContactSender" ? "sender" : "receiver";
                    if (none) contacts.Add(PathOf(avatar, contact.gameObject) + " (" + kind + " with no collision tags: it can never touch anything)");
                    var type = serialized.FindProperty("receiverType");
                    string name = serialized.FindProperty("parameter")?.stringValue;
                    if (kind == "receiver" && type != null && type.propertyType == SerializedPropertyType.Enum && type.enumValueIndex >= 0 &&
                        type.enumNames[type.enumValueIndex] == "Proximity" && !string.IsNullOrEmpty(name) && floats.TryGetValue(name, out var declared) &&
                        declared.All(t => t != AnimatorControllerParameterType.Float))
                        contacts.Add(PathOf(avatar, contact.gameObject) + " (Proximity receiver on \"" + name + "\", which is " + declared[0] + ": proximity sends a distance between 0 and 1)");
                    else if (!none) continue;
                    firstContact = firstContact ?? contact;
                }
            if (contacts.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "contacts|", Target = firstContact.gameObject,
                    Title = N(contacts.Count, "contact") + " can't work as set up",
                    Detail = Bullets(contacts),
                    Fix = "Add the collision tags it should send or react to (for example Hand or Head), and use a Float parameter for Proximity receivers.",
                    Identity = string.Join("\n", contacts)
                });

            var limits = new List<string>();
            if (bones.Count > MaxPhysBones) limits.Add(bones.Count + " PhysBone components (VRChat allows " + MaxPhysBones + ")");
            if (colliders.Count > MaxColliders) limits.Add(colliders.Count + " PhysBone colliders (VRChat allows " + MaxColliders + ")");
            if (senders.Count + receivers.Count > MaxContacts) limits.Add((senders.Count + receivers.Count) + " contacts (VRChat allows " + MaxContacts + ")");
            if (avatar.Expressions.Count > MaxExpressionParameters) limits.Add(avatar.Expressions.Count + " Expression Parameters (VRChat allows " + MaxExpressionParameters + ")");
            foreach (var bone in bones)
                using (var serialized = new SerializedObject(bone))
                {
                    var root = serialized.FindProperty("rootTransform")?.objectReferenceValue as Transform ?? bone.transform;
                    var ignore = new HashSet<Transform>();
                    var list = serialized.FindProperty("ignoreTransforms");
                    for (int i = 0; list != null && i < list.arraySize; i++)
                        if (list.GetArrayElementAtIndex(i).objectReferenceValue is Transform t) ignore.Add(t);
                    int count = 0;
                    void Walk(Transform t) { if (ignore.Contains(t)) return; count++; foreach (Transform child in t) Walk(child); }
                    Walk(root);
                    if (count > MaxPhysBoneTransforms) limits.Add(PathOf(avatar, bone.gameObject) + ": one PhysBone moves " + count + " transforms (VRChat allows " + MaxPhysBoneTransforms + ")");
                }
            if (limits.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Broken, Key = "limit|", Target = avatar.Descriptor,
                    Title = "The avatar is over " + N(limits.Count, "VRChat limit"),
                    Detail = "VRChat refuses or cuts off more than this, whatever the performance rank:\n" + Bullets(limits),
                    Fix = "Remove or combine the extra components (several PhysBones on one chain can often be one), or split a very large PhysBone chain.",
                    Identity = string.Join("\n", limits)
                });

            var planes = new List<string>();
            Component firstPlane = null;
            foreach (var collider in colliders)
                using (var serialized = new SerializedObject(collider))
                {
                    var shape = serialized.FindProperty("shapeType");
                    if (shape == null || shape.propertyType != SerializedPropertyType.Enum || shape.enumValueIndex < 0 || shape.enumNames[shape.enumValueIndex] != "Plane" ||
                        !(serialized.FindProperty("globalCollision")?.boolValue ?? false)) continue;
                    planes.Add(PathOf(avatar, collider.gameObject));
                    firstPlane = firstPlane ?? collider;
                }
            if (planes.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "planeglobal|", Target = firstPlane.gameObject,
                    Title = N(planes.Count, "plane collider") + " " + (planes.Count == 1 ? "is" : "are") + " set to collide globally",
                    Detail = "VRChat doesn't support global collision for plane colliders, so other avatars' PhysBones ignore " + (planes.Count == 1 ? "it" : "them") + ". PhysBones that list " + (planes.Count == 1 ? "it" : "them") + " directly still collide:\n" + Bullets(planes),
                    Fix = "Turn off global collision on the plane collider and add it to the PhysBones' Colliders list, or use a sphere or capsule collider.",
                    Identity = string.Join("\n", planes)
                });
        }
    }
}
