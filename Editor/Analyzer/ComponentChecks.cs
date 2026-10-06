using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // Step 4: missing scripts, references to deleted things, and contact receivers whose parameter no animator has. Judged as
    // authored: the uploaded copy is built from it.
    internal static partial class AvatarAnalyzer
    {
        private static void Components(Avatar avatar, List<Finding> findings, ICollection<string> laterNames)
        {
            var objects = avatar.Root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToList();

            var scripts = objects.Where(o => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(o) > 0).ToList();
            if (scripts.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Broken, Key = "scripts", Target = scripts[0],
                    Title = N(scripts.Count, "object", "has", "have") + " missing scripts",
                    Detail = "A script they use is no longer in the project (its package was removed or isn't installed), so those components do nothing:\n" +
                        Bullets(scripts.Select(o => PathOf(avatar, o))),
                    Fix = "If you still need them, install the package they came from. Otherwise press Remove, or remove each empty component in the Inspector.",
                    Data = new Fixes.Scripts { Objects = scripts }
                });

            // A reference field that once pointed at something now deleted shows "Missing" in the Inspector: it still holds an id.
            var broken = new List<string>();
            var missing = new Fixes.References();
            GameObject first = null;
            foreach (var component in objects.SelectMany(o => o.GetComponents<Component>()).Where(c => c && !(c is Transform)))
                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.NextVisible(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = property.objectReferenceValue;
                        // A scene object outside the avatar (another avatar's bone or collider, a world object) is not uploaded with it.
                        var outside = value is GameObject g ? g.transform : value is Component c ? c.transform : null;
                        bool external = outside && !EditorUtility.IsPersistent(value) && !outside.IsChildOf(avatar.Root.transform);
                        if (!value && property.objectReferenceInstanceIDValue != 0 || external)
                        {
                            broken.Add(PathOf(avatar, component.gameObject) + " (" + component.GetType().Name + ", " + property.displayName + (external ? ": \"" + outside.name + "\" outside the avatar" : "") + ")");
                            if (!first) first = component.gameObject;
                            if (!external) missing.Fields.Add((component, property.propertyPath));
                        }
                    }
                }
            if (broken.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "refs", Target = first,
                    Title = N(broken.Count, "reference", "points", "point") + " at something deleted or outside the avatar",
                    Detail = (broken.Count == 1 ? "This field shows \"Missing\" in the Inspector, or points at an object outside this avatar, which isn't uploaded with it. Whatever it was for is skipped in game:\n"
                        : "These fields show \"Missing\" in the Inspector, or point at an object outside this avatar, which isn't uploaded with it. Whatever they were for is skipped in game:\n") + Bullets(broken),
                    Fix = "Drag the right object or asset from this avatar into each field. If a field is no longer needed, press Clear.",
                    Data = missing.Fields.Count > 0 ? missing : null,
                });

            var parameters = new HashSet<string>(avatar.Playables.SelectMany(p => p.Controller.parameters.Select(q => q.name)), StringComparer.Ordinal);
            var receivers = new List<string>();
            GameObject receiver = null;
            foreach (var component in objects.SelectMany(o => o.GetComponents<Component>()).Where(c => c && c.GetType().Name == "VRCContactReceiver"))
                using (var serialized = new SerializedObject(component))
                {
                    string name = serialized.FindProperty("parameter")?.stringValue;
                    if (string.IsNullOrEmpty(name) || parameters.Contains(name) || laterNames != null && laterNames.Contains(name) || BuildTool(avatar, component.transform)) continue;
                    receivers.Add(PathOf(avatar, component.gameObject) + " (\"" + name + "\")");
                    if (!receiver) receiver = component.gameObject;
                }
            if (receivers.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.TidyUp, Key = "receivers", Target = receiver,
                    Title = N(receivers.Count, "contact receiver", "triggers", "trigger") + " nothing",
                    Detail = (receivers.Count == 1 ? "It detects touches, but no animator has the parameter it sets, so nothing happens:\n"
                        : "They detect touches, but no animator has the parameters they set, so nothing happens:\n") + Bullets(receivers),
                    Fix = "Add the parameter to the animator that should react (usually FX), fix the name on the receiver, or remove the receiver."
                });
        }

        // Inside a VRCFury or Modular Avatar prefab, whose parameters those tools rename and wire up at upload.
        private static bool BuildTool(Avatar avatar, Transform transform)
        {
            for (; transform && transform != avatar.Root.transform; transform = transform.parent)
                if (transform.GetComponents<Component>().Any(c => c && (c.GetType().Name.Contains("VRCFury") || c.GetType().Namespace?.StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal) == true)))
                    return true;
            return false;
        }

        private static string Bullets(IEnumerable<string> names)
        {
            var list = names.ToList();
            return string.Join("\n", list.Take(8).Select(n => "• " + n)) + (list.Count > 8 ? "\n• and " + (list.Count - 8) + " more" : "");
        }

        private static string PathOf(Avatar avatar, GameObject o) => o == avatar.Root ? "(avatar root)" : Short(AnimationUtility.CalculateTransformPath(o.transform, avatar.Root.transform));
    }
}
