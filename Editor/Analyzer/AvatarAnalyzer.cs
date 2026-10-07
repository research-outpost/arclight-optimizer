using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    internal enum Severity { Broken, WorthChecking, TidyUp }

    // One problem, written for the avatar's owner: what is wrong, why it matters, and how to fix it by hand.
    internal sealed class Finding
    {
        public Severity Severity;
        public string Title, Detail, Fix;
        public Object Target;
        // Same problem, same place: compares the avatar as authored with the built one.
        public string Key;
        // Null when found in every version checked; else says it is only in the uploaded version.
        public string Where;
        // What a one-click fix needs (see Fixes), or null when it has none.
        public object Data;
        // The playable layer it is in (FX, Gesture...), or null.
        public string Playable;
        // Everything the card lists, untruncated (Detail shows the first few): what Ignore remembers. Null when Detail lists it all.
        public string Identity;
    }

    // Arclight Analyzer: reads a VRChat avatar and reports setup mistakes. It never changes the avatar. The VRChat SDK is not
    // referenced, so its components and assets are read through serialized fields.
    internal static partial class AvatarAnalyzer
    {
        // VRCAvatarDescriptor.AnimLayerType values (1 is an unused old value).
        private static readonly string[] LayerNames = { "Base", "Layer 1", "Additive", "Gesture", "Action", "FX", "Sitting", "TPose", "IKPose" };

        internal sealed class Playable { public string Name; public AnimatorController Controller; }
        internal sealed class ExpressionParameter { public string Name; public AnimatorControllerParameterType Type; public bool Synced, Saved; public float Default; }
        internal sealed class MenuControl { public string Path; public string Parameter; public Object Menu; public string Type; public float Value; }

        internal sealed class Avatar
        {
            public GameObject Root;
            public Component Descriptor;
            public readonly List<Playable> Playables = new List<Playable>();
            public Object ExpressionAsset, MenuAsset;
            public readonly List<ExpressionParameter> Expressions = new List<ExpressionParameter>();
            public readonly List<MenuControl> Controls = new List<MenuControl>();
            // Menu structure problems: (kind, where, menu asset).
            public readonly List<(string Kind, string Path, Object Menu)> MenuIssues = new List<(string, string, Object)>();
        }

        internal static Component DescriptorOf(GameObject root) =>
            root ? root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().Name == "VRCAvatarDescriptor") : null;

        internal static Avatar Read(GameObject root)
        {
            var avatar = new Avatar { Root = root, Descriptor = DescriptorOf(root) };
            if (!avatar.Descriptor) return avatar;
            using (var serialized = new SerializedObject(avatar.Descriptor))
            {
                // Without "Customize" VRChat uses its own controllers for every layer.
                bool custom = serialized.FindProperty("customizeAnimationLayers")?.boolValue ?? true;
                foreach (string list in custom ? new[] { "baseAnimationLayers", "specialAnimationLayers" } : new string[0])
                {
                    var layers = serialized.FindProperty(list);
                    for (int i = 0; layers != null && i < layers.arraySize; i++)
                    {
                        var layer = layers.GetArrayElementAtIndex(i);
                        if (layer.FindPropertyRelative("isDefault")?.boolValue == true) continue; // VRChat's own controller.
                        if (!(layer.FindPropertyRelative("animatorController")?.objectReferenceValue is AnimatorController controller)) continue;
                        int type = layer.FindPropertyRelative("type")?.intValue ?? -1;
                        avatar.Playables.Add(new Playable { Name = type >= 0 && type < LayerNames.Length ? LayerNames[type] : "Layer " + type, Controller = controller });
                    }
                }
                // Without "Customize" under Expressions VRChat uses its default menu and parameters.
                if (serialized.FindProperty("customExpressions")?.boolValue ?? true)
                {
                    avatar.ExpressionAsset = serialized.FindProperty("expressionParameters")?.objectReferenceValue;
                    avatar.MenuAsset = serialized.FindProperty("expressionsMenu")?.objectReferenceValue;
                }
            }
            if (avatar.ExpressionAsset)
                using (var serialized = new SerializedObject(avatar.ExpressionAsset))
                {
                    var array = serialized.FindProperty("parameters");
                    for (int i = 0; array != null && i < array.arraySize; i++)
                    {
                        var entry = array.GetArrayElementAtIndex(i);
                        string name = entry.FindPropertyRelative("name")?.stringValue;
                        if (string.IsNullOrEmpty(name)) continue;
                        int valueType = entry.FindPropertyRelative("valueType")?.intValue ?? 1;
                        avatar.Expressions.Add(new ExpressionParameter
                        {
                            Name = name, Synced = entry.FindPropertyRelative("networkSynced")?.boolValue ?? true, Default = entry.FindPropertyRelative("defaultValue")?.floatValue ?? 0, Saved = entry.FindPropertyRelative("saved")?.boolValue ?? false,
                            Type = valueType == 0 ? AnimatorControllerParameterType.Int : valueType == 2 ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float
                        });
                    }
                }
            if (avatar.MenuAsset) ReadMenu(avatar, avatar.MenuAsset, "Menu", new HashSet<Object>(), new List<Object>());
            return avatar;
        }

        // A control's name without rich-text tags or line breaks, shortened for a path.
        private static string Plain(string name)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(name ?? "", "<[^>]*>", "").Replace('\n', ' ').Trim();
            if (plain.Length == 0) return "(unnamed)";
            return plain.Length > 40 ? plain.Substring(0, 40) + "..." : plain;
        }

        // VRChat shows at most this many controls on one menu page.
        private const int MenuLimit = 8;

        // ancestors: the menus open above this one (a submenu naming one of them loops); seen: menus already read once.
        private static void ReadMenu(Avatar avatar, Object menu, string path, HashSet<Object> seen, List<Object> ancestors)
        {
            if (!seen.Add(menu)) return;
            ancestors.Add(menu);
            using (var serialized = new SerializedObject(menu))
            {
                var controls = serialized.FindProperty("controls");
                if (controls != null && controls.arraySize > MenuLimit) avatar.MenuIssues.Add(("full", path + " (" + controls.arraySize + " controls)", menu));
                for (int i = 0; controls != null && i < controls.arraySize; i++)
                {
                    var control = controls.GetArrayElementAtIndex(i);
                    string name = control.FindPropertyRelative("name")?.stringValue;
                    string here = path + " > " + Plain(name);
                    var typeProperty = control.FindPropertyRelative("type");
                    int code = typeProperty?.intValue ?? 0;
                    string type = typeProperty != null && typeProperty.propertyType == SerializedPropertyType.Enum && typeProperty.enumValueIndex >= 0 ? typeProperty.enumNames[typeProperty.enumValueIndex]
                        : code == 101 ? "Button" : code == 102 ? "Toggle" : code == 103 ? "SubMenu" : code > 200 && code < 300 ? "Puppet" : "";
                    string parameter = control.FindPropertyRelative("parameter.name")?.stringValue;
                    if (!string.IsNullOrEmpty(parameter)) avatar.Controls.Add(new MenuControl { Path = here, Parameter = parameter, Menu = menu, Type = type, Value = control.FindPropertyRelative("value")?.floatValue ?? 1 });
                    var subs = control.FindPropertyRelative("subParameters");
                    bool anySub = false;
                    // Only puppets use sub-parameters; a Toggle or Button keeps stale ones from before its type was changed.
                    for (int j = 0; subs != null && type.Contains("Puppet") && j < subs.arraySize; j++)
                    {
                        string sub = subs.GetArrayElementAtIndex(j).FindPropertyRelative("name")?.stringValue;
                        if (string.IsNullOrEmpty(sub)) continue;
                        anySub = true;
                        avatar.Controls.Add(new MenuControl { Path = here, Parameter = sub, Menu = menu, Type = "Sub" });
                    }
                    var subMenu = control.FindPropertyRelative("subMenu")?.objectReferenceValue;
                    if (type == "SubMenu")
                    {
                        if (!subMenu) avatar.MenuIssues.Add(("empty", here, menu));
                        else if (ancestors.Contains(subMenu)) avatar.MenuIssues.Add(("loop", here, menu));
                        else ReadMenu(avatar, subMenu, here, seen, ancestors);
                    }
                    // A Button or Toggle without a parameter is often a text label (GoGo Loco, SPS and VRCFT menus use them); puppets need one.
                    else if (type.Length > 0 && type != "Button" && type != "Toggle" && !anySub) avatar.MenuIssues.Add(("nothing", here, menu));
                }
            }
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        // The avatar as authored and, with includeBuild, as uploaded: a copy goes through the same build steps an upload runs (Modular
        // Avatar, VRCFury, NDMF plugins, d4rk...) in a hidden scene, is checked, and is deleted. Things that break (missing parameters,
        // dead menu controls) are taken from the uploaded version, so what a tool adds at upload counts, a problem the build fixes is
        // not shown, and one only the build causes says so. Types and unused entries are judged on the avatar as authored only: build
        // tools retype and trim parameters on purpose.
        // syncedBits: synced expression parameter bits of the uploaded version (as authored when it is not checked).
        internal static List<Finding> Run(GameObject root, bool includeBuild, out string buildError, out int syncedBits)
        {
            buildError = null;
            syncedBits = SyncedBits(Read(root));
            if (!includeBuild) return Group(Check(root));
            (List<Finding> Findings, HashSet<string> Names, int Bits) built;
            try { built = CheckBuilt(root); }
            catch (Exception e) { buildError = e.GetBaseException().Message; return Group(Check(root)); }
            syncedBits = built.Bits;
            return Group(Merge(Check(root, laterNames: built.Names), built.Findings, Read(root)));
        }

        // The findings to show from the authored and the uploaded version (see Run).
        internal static List<Finding> Merge(List<Finding> authored, List<Finding> builtFindings, Avatar source)
        {
            bool Breaks(Finding f) => Breakage(f);
            // Animation targets and driver targets count only when missing in both: before the build, Modular Avatar may not have
            // added them yet; after it, optimizers can leave curves to objects they removed.
            bool InBoth(Finding f) => f.Key.StartsWith("path|", StringComparison.Ordinal) || f.Key.StartsWith("driver|", StringComparison.Ordinal);
            var authoredKeys = new HashSet<string>(authored.Select(f => f.Key));
            var builtKeys = new HashSet<string>(builtFindings.Select(f => f.Key));
            var result = authored.Where(f => !Breaks(f) && (!InBoth(f) || builtKeys.Contains(f.Key))).ToList();
            // The uploaded version's own finding, so its list of names is what the upload really lacks (keys group several names).
            foreach (var finding in builtFindings.Where(f => !InBoth(f)))
            {
                finding.Where = authoredKeys.Contains(finding.Key) ? null : "Only in the uploaded version: a tool that runs at upload causes this";
                // The built copy is deleted; point at the authored asset it came from.
                finding.Target = finding.Playable != null ? source.Playables.FirstOrDefault(p => p.Name == finding.Playable)?.Controller
                    : finding.Key.StartsWith("menu", StringComparison.Ordinal) || finding.Key.StartsWith("puppet|", StringComparison.Ordinal) ? source.MenuAsset : source.ExpressionAsset;
                result.Add(finding);
            }
            return result;
        }

        // Problems judged on the uploaded version: tools that run at upload often fix them (Modular Avatar, VRCFury).
        internal static bool Breakage(Finding f) => new[] { "missing|", "menu|", "menus|", "menuvalue", "budget", "puppet|", "ft|", "control|" }.Any(p => f.Key.StartsWith(p, StringComparison.Ordinal));

        // True while CheckBuilt runs the upload steps, so the upload report (UploadReport) stays quiet for that copy.
        internal static bool Building { get; private set; }

        private static (List<Finding>, HashSet<string>, int) CheckBuilt(GameObject root)
        {
            string temporary = "Assets/__ArclightAnalyzer_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(temporary));
            var scene = EditorSceneManager.NewPreviewScene();
            var clone = Object.Instantiate(root);
            clone.name = root.name;
            try
            {
                SceneManager.MoveGameObjectToScene(clone, scene);
                Building = true;
                // The SDK's own entry point runs every upload hook in order; without it (no SDK found), NDMF alone.
                var sdk = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks")).FirstOrDefault(t => t != null)
                    ?.GetMethod("OnPreprocessAvatar", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                using (new OverrideTemporaryDirectoryScope(temporary))
                {
                    if (sdk != null) { if (sdk.Invoke(null, new object[] { clone }) is bool ok && !ok) throw new InvalidOperationException("A build step stopped the build. The console says which."); }
                    else AvatarProcessor.ProcessAvatar(clone);
                }
                var avatar = Read(clone);
                return (Check(clone, built: true), new HashSet<string>(avatar.Playables.SelectMany(p => p.Controller.parameters.Select(q => q.name)), StringComparer.Ordinal), SyncedBits(avatar));
            }
            finally
            {
                Building = false;
                if (clone) Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(temporary);
            }
        }

        // Every finding for one version of the avatar.
        // built: the uploaded version, where only what breaks is checked. laterNames: animator parameters the uploaded version has.
        internal static List<Finding> Check(GameObject root, bool built = false, ICollection<string> laterNames = null)
        {
            var findings = new List<Finding>();
            var avatar = Read(root);
            if (!avatar.Descriptor) return findings;
            foreach (var playable in avatar.Playables)
            {
                MissingParameters(avatar, playable, findings);
                AnimationTargets(avatar, playable, findings);
                if (!built) { WriteDefaults(playable, findings); DeadLayers(avatar, playable, findings); DeadStates(playable, findings); }
            }
            DriverTargets(avatar, findings);
            if (avatar.Playables.Count > 0) LayerControls(avatar, findings);
            MenuStructure(avatar, findings);
            if (!built) { StartStates(avatar, findings); Components(avatar, findings, laterNames); }
            ExpressionsAndMenu(avatar, findings, built, laterNames);
            return findings;
        }

        // Check 1: parameters a controller reads but does not declare. Unity then reads nothing it can rely on.
        private static void MissingParameters(Avatar avatar, Playable playable, List<Finding> findings)
        {
            var controller = playable.Controller;
            var declared = new HashSet<string>(controller.parameters.Select(p => p.name), StringComparer.Ordinal);
            // Name to (how it is read, the type the read implies, where).
            var reads = new Dictionary<string, (string How, AnimatorControllerParameterType Type, List<string> Where)>(StringComparer.Ordinal);
            void Read(string name, string how, AnimatorControllerParameterType type, string where)
            {
                if (string.IsNullOrEmpty(name) || declared.Contains(name)) return;
                if (!reads.TryGetValue(name, out var entry)) reads[name] = entry = (how, type, new List<string>());
                else if (Rank(how) < Rank(entry.How)) reads[name] = entry = (how, type, entry.Where);
                if (!entry.Where.Contains(where)) entry.Where.Add(where);
            }
            void Motion(Motion motion, string where, HashSet<Motion> seen)
            {
                if (!(motion is BlendTree tree) || !seen.Add(tree)) return;
                if (tree.blendType == BlendTreeType.Direct)
                    foreach (var child in tree.children) Read(child.directBlendParameter, "direct", AnimatorControllerParameterType.Float, where);
                else
                {
                    Read(tree.blendParameter, "tree", AnimatorControllerParameterType.Float, where);
                    if (tree.blendType != BlendTreeType.Simple1D) Read(tree.blendParameterY, "tree", AnimatorControllerParameterType.Float, where);
                }
                foreach (var child in tree.children) Motion(child.motion, where, seen);
            }
            void Transitions(IEnumerable<AnimatorTransitionBase> transitions, string where)
            {
                foreach (var transition in transitions.Where(t => t))
                    foreach (var condition in transition.conditions)
                        Read(condition.parameter, "condition", ConditionType(condition.mode), where);
            }
            var motions = new HashSet<Motion>();
            for (int index = 0; index < controller.layers.Length; index++)
            {
                var layer = controller.layers[index];
                var machine = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < controller.layers.Length ? controller.layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;
                foreach (var (sm, state) in States(machine))
                {
                    string where = layer.name;
                    var motion = layer.syncedLayerIndex >= 0 ? layer.GetOverrideMotion(state) : state.motion;
                    Motion(motion, where, motions);
                    if (layer.syncedLayerIndex >= 0) continue; // A synced layer shares the source layer's transitions and parameters.
                    Transitions(state.transitions, where);
                    if (state.speedParameterActive) Read(state.speedParameter, "state", AnimatorControllerParameterType.Float, where);
                    if (state.timeParameterActive) Read(state.timeParameter, "state", AnimatorControllerParameterType.Float, where);
                    if (state.cycleOffsetParameterActive) Read(state.cycleOffsetParameter, "state", AnimatorControllerParameterType.Float, where);
                    if (state.mirrorParameterActive) Read(state.mirrorParameter, "state", AnimatorControllerParameterType.Bool, where);
                }
                if (layer.syncedLayerIndex >= 0) continue;
                foreach (var sm in Machines(machine))
                {
                    Transitions(sm.anyStateTransitions, layer.name);
                    Transitions(sm.entryTransitions, layer.name);
                    foreach (var child in sm.stateMachines) Transitions(sm.GetStateMachineTransitions(child.stateMachine), layer.name);
                }
            }

            // One card per controller, a line per way of reading, so a template missing many parameters stays short.
            var expressionNames = new HashSet<string>(avatar.Expressions.Select(e => e.Name), StringComparer.Ordinal);
            // The type to add. Blend tree weights and inputs and state speed/time read a Float only, whatever the Expression Parameter is
            // (VRChat converts it); a condition takes the Expression Parameter's type when there is one, else what the read implies.
            AnimatorControllerParameterType TypeOf(string name, AnimatorControllerParameterType implied) =>
                reads[name].How != "condition" ? implied : avatar.Expressions.FirstOrDefault(e => e.Name == name)?.Type ?? implied;
            if (reads.Count == 0) return;
            var all = reads.OrderBy(p => Rank(p.Value.How)).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            var lines = new List<string>();
            foreach (var group in all.GroupBy(p => p.Value.How))
            {
                var names = group.Select(p => p.Key).ToList();
                bool one = names.Count == 1;
                // The effect first, then the names, so the sentence reads the same for one name or many.
                string effect = group.Key == "direct" ? (one ? "This one sets how strongly an animation plays, so that animation stays off" : "These set how strongly animations play, so those animations stay off")
                    : group.Key == "tree" ? (one ? "This one decides what a blend tree plays, so the tree behaves unpredictably" : "These decide what blend trees play, so those trees behave unpredictably")
                    : group.Key == "condition" ? (one ? "This one decides when a transition happens, so that transition never fires as it should" : "These decide when transitions happen, so those transitions never fire as they should")
                    : (one ? "This one sets a state's speed, time or mirroring, which stays at its default" : "These set states' speed, time or mirroring, which stay at their defaults");
                lines.Add("• " + effect + ": " + Join(names, 6) + ".");
            }
            int inExpressions = all.Count(p => expressionNames.Contains(p.Key));
            if (inExpressions > 0) lines.Add(inExpressions == all.Count ? (all.Count == 1 ? "It's already in your Expression Parameters, so only " + playable.Name + " needs it."
                : "They're already in your Expression Parameters, so only " + playable.Name + " needs them.")
                : inExpressions + " of them are already in your Expression Parameters.");
            var layersUsed = all.SelectMany(p => p.Value.Where).Distinct().ToList();
            lines.Add("Found in " + (layersUsed.Count == 1 ? "layer " : "layers ") + Join(layersUsed, 4) + ".");
            findings.Add(new Finding
            {
                Severity = Severity.Broken,
                Key = "missing|" + playable.Name, Playable = playable.Name,
                Title = all.Count == 1 ? playable.Name + " uses a parameter it doesn't have: \"" + all[0].Key + "\"" : playable.Name + " uses " + all.Count + " parameters it doesn't have",
                Detail = string.Join("\n", lines),
                Fix = "Open the " + playable.Name + " controller, go to its Parameters tab and add " + string.Join(", ", all.Take(20).Select(p => "\"" + p.Key + "\" (" + TypeOf(p.Key, p.Value.Type) + ")")) +
                    (all.Count > 20 ? " and " + (all.Count - 20) + " more" : "") + ". If these came with a template (face tracking, for example), its setup guide lists them.",
                Target = controller,
                Data = new Fixes.Parameters { Names = all.Select(p => (p.Key, TypeOf(p.Key, p.Value.Type))).ToList() },
                Identity = string.Join("\n", all.Select(p => p.Key + " " + p.Value.How + " " + string.Join(",", p.Value.Where)))
            });
        }

        // "1 layer" / "3 layers", with a verb that agrees: N(1, "layer", "never does", "never do").
        internal static string N(int n, string noun, string one = null, string many = null) =>
            n + " " + (n == 1 ? noun : noun.EndsWith("s") ? noun + "es" : noun + "s") + (one == null ? "" : " " + (n == 1 ? one : many));

        // A transform path shortened to its last three parts, for lists.
        internal static string Short(string path)
        {
            var parts = path.Split('/');
            return parts.Length <= 3 ? path : "…/" + string.Join("/", parts.Skip(parts.Length - 3));
        }

        // Up to n names, quoted, then "and N more".
        internal static string Join(IReadOnlyList<string> names, int n) =>
            string.Join(", ", names.Take(n).Select(x => "\"" + x + "\"")) + (names.Count > n ? " and " + (names.Count - n) + " more" : "");

        private static int Rank(string how) => how == "direct" ? 0 : how == "tree" ? 1 : how == "condition" ? 2 : 3;

        private static AnimatorControllerParameterType ConditionType(AnimatorConditionMode mode) =>
            mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot ? AnimatorControllerParameterType.Bool
            : mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual ? AnimatorControllerParameterType.Int
            : AnimatorControllerParameterType.Float;

        private static IEnumerable<AnimatorStateMachine> Machines(AnimatorStateMachine machine)
        {
            if (!machine) yield break;
            yield return machine;
            foreach (var child in machine.stateMachines)
                foreach (var sub in Machines(child.stateMachine)) yield return sub;
        }

        private static IEnumerable<(AnimatorStateMachine, AnimatorState)> States(AnimatorStateMachine machine) =>
            Machines(machine).SelectMany(sm => sm.states.Where(s => s.state).Select(s => (sm, s.state)));

        // Checks 2 and 3: expression parameters, the menu and the animators agreeing on names and types.
        private static void ExpressionsAndMenu(Avatar avatar, List<Finding> findings, bool built, ICollection<string> laterNames)
        {
            var animatorTypes = new Dictionary<string, List<(string Playable, AnimatorControllerParameterType Type)>>(StringComparer.Ordinal);
            foreach (var playable in avatar.Playables)
                foreach (var p in playable.Controller.parameters)
                {
                    if (!animatorTypes.TryGetValue(p.name, out var list)) animatorTypes[p.name] = list = new List<(string, AnimatorControllerParameterType)>();
                    list.Add((playable.Name, p.type));
                }
            var expressionNames = new HashSet<string>(avatar.Expressions.Select(e => e.Name), StringComparer.Ordinal);
            var menuNames = new HashSet<string>(avatar.Controls.Select(c => c.Parameter), StringComparer.Ordinal);

            foreach (var group in avatar.Controls.Where(c => !expressionNames.Contains(c.Parameter)).GroupBy(c => c.Parameter))
                findings.Add(new Finding
                {
                    Severity = Severity.Broken,
                    Key = "menu|" + group.Key,
                    Title = "A menu control does nothing: \"" + group.Key + "\" isn't an Expression Parameter",
                    Detail = "The menu can only change Expression Parameters, so pressing this control has no effect. Control: " +
                        string.Join("; ", group.Select(c => c.Path).Distinct().Take(5)) + ".",
                    Fix = "Add \"" + group.Key + "\" to the avatar's Expression Parameters with the type the animator uses" +
                        (animatorTypes.TryGetValue(group.Key, out var used) ? " (" + used[0].Type + ")" : "") + ", or point the control at the right parameter.",
                    Target = group.First().Menu,
                    Identity = string.Join("\n", group.Select(c => c.Path).Distinct())
                });

            var types = avatar.Expressions.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Type, StringComparer.Ordinal);
            // Toggle and Button values the parameter can't hold (an Int toggle at 0 is fine: swap menus use it to pick option 0):
            // an Int that isn't a whole number from 0 to 255, or a Float outside -1 to 1.
            var badValues = avatar.Controls.Where(c => (c.Type == "Toggle" || c.Type == "Button") && types.TryGetValue(c.Parameter, out var t) &&
                (t == AnimatorControllerParameterType.Int && (c.Value < 0 || c.Value > 255 || c.Value != Mathf.Round(c.Value)) ||
                 t == AnimatorControllerParameterType.Float && (c.Value < -1 || c.Value > 1))).ToList();
            if (badValues.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.Broken, Key = "menuvalue", Target = badValues[0].Menu,
                    Title = N(badValues.Count, "menu control", "sets", "set") + " a value its parameter can't use",
                    Detail = "Ints hold whole numbers from 0 to 255 and Floats -1 to 1, so these controls can't set what they ask for:\n" +
                        string.Join("\n", badValues.Take(8).Select(c => "• " + c.Path + " (\"" + c.Parameter + "\" = " + c.Value + ")")) + (badValues.Count > 8 ? "\n• and " + (badValues.Count - 8) + " more" : ""),
                    Fix = "Give each control a value its parameter can hold.",
                    Identity = string.Join("\n", badValues.Select(c => c.Path + " " + c.Parameter + " " + c.Value))
                });

            // Radial and axis puppets only write Floats, so one on a Bool or Int parameter does nothing.
            foreach (var group in avatar.Controls.Where(c => c.Type == "Sub" && types.TryGetValue(c.Parameter, out var t) && t != AnimatorControllerParameterType.Float).GroupBy(c => c.Parameter))
                findings.Add(new Finding
                {
                    Severity = Severity.Broken,
                    Key = "puppet|" + group.Key,
                    Title = "A puppet control does nothing: \"" + group.Key + "\" is a " + types[group.Key] + ", not a Float",
                    Detail = "Radial and axis puppets can only move Float parameters. Control: " + string.Join("; ", group.Select(c => c.Path).Distinct().Take(5)) + ".",
                    Fix = "Change \"" + group.Key + "\" to Float in Expression Parameters and in the animator, or use a Toggle or Button for it instead.",
                    Target = group.First().Menu,
                    Identity = string.Join("\n", group.Select(c => c.Path).Distinct())
                });

            // Face tracking (VRCFaceTracking) sets its parameters over OSC, which only reaches Expression Parameters; an animator
            // parameter named like one ("FT/v2/..." or "v2/...") that isn't listed there never receives a value.
            // Names the animator writes itself (a clip's Animator curve or a Parameter Driver, as binary decoders do) aren't fed by OSC.
            var written = new HashSet<string>(avatar.Playables.SelectMany(p => ClipsOf(p.Controller))
                .SelectMany(c => AnimationUtility.GetCurveBindings(c)).Where(b => b.type == typeof(Animator) && b.path.Length == 0).Select(b => b.propertyName), StringComparer.Ordinal);
            written.UnionWith(DriverSets(avatar));
            var unreachable = animatorTypes.Keys.Where(n => (n.StartsWith("FT/v2/", StringComparison.Ordinal) || n.StartsWith("v2/", StringComparison.Ordinal)) && !expressionNames.Contains(n) && !written.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            // Binary bits (JawOpen1, JawOpen2, ...Negative) carry the synced data, so they must be synced; the plain values can stay local.
            bool Bit(string n) => char.IsDigit(n[n.Length - 1]) || n.EndsWith("Negative", StringComparison.Ordinal);
            var values = unreachable.Where(n => !Bit(n)).ToList();
            var bits = unreachable.Where(Bit).ToList();
            if (unreachable.Count > 0)
                findings.Add(new Finding
                {
                    Severity = Severity.WorthChecking, Key = "ft|", Target = avatar.ExpressionAsset,
                    Title = N(unreachable.Count, "face-tracking parameter", "can't receive", "can't receive") + " tracking data",
                    Detail = "Face tracking can only send values to Expression Parameters. " + (unreachable.Count == 1 ? "This one is in the animator but missing from Expression Parameters, so it never moves: " : "These are in the animator but missing from Expression Parameters, so they never move: ") +
                        Join(unreachable, 8) + ".",
                    Fix = (values.Count > 0 && avatar.ExpressionAsset ? "List " + Join(values, 4) + " in Expression Parameters with Synced off (no synced bits). " : "") +
                        (bits.Count > 0 ? "Add " + Join(bits, 4) + " to Expression Parameters by hand with Synced on: they carry the tracking to other players and cost " +
                            N(bits.Count, "synced bit") + ". " : "") + "Reinstalling the face-tracking template's parameters also works.",
                    // Only the plain values get a button; binary bits must be synced, which spends the user's bit budget.
                    Data = values.Count > 0 && avatar.ExpressionAsset ? new Fixes.ExpressionAdds { Names = values.Select(n => (n, animatorTypes[n][0].Type)).ToList() } : null,
                    Identity = string.Join("\n", unreachable)
                });

            if (built) return;
            // VRChat's own controllers (left as default, so not read here) use its built-in emote and face-blend parameters.
            foreach (var expression in avatar.Expressions.Where(e => e.Name != "VRCEmote" && e.Name != "VRCFaceBlendH" && e.Name != "VRCFaceBlendV"))
            {
                if (!animatorTypes.TryGetValue(expression.Name, out var used))
                {
                    if (laterNames != null && laterNames.Contains(expression.Name)) continue; // A tool adds it at upload.
                    bool fromMenu = menuNames.Contains(expression.Name);
                    findings.Add(new Finding
                    {
                        Severity = fromMenu ? Severity.WorthChecking : Severity.TidyUp,
                        Key = "unused|" + expression.Name,
                        Title = fromMenu ? "A menu control changes \"" + expression.Name + "\", but nothing reacts to it" : "Expression parameter \"" + expression.Name + "\" isn't used by anything",
                        Detail = (fromMenu ? "No animator has a parameter with this name, so the control does nothing on the avatar."
                            : "No animator has a parameter with this name, so its value goes nowhere.") +
                            (expression.Synced ? " It still takes " + (expression.Type == AnimatorControllerParameterType.Bool ? "1 synced bit" : "8 synced bits") + "." : ""),
                        Fix = fromMenu ? "Add a " + expression.Type + " parameter named \"" + expression.Name + "\" to the controller that should react (usually FX), or fix the spelling in one of the two places."
                            : "If it's a leftover (an old toggle, for example), remove it from Expression Parameters. If an OSC app or a build tool fills it in, it's fine as it is.",
                        Target = avatar.ExpressionAsset
                    });
                    continue;
                }
                foreach (var (playable, type) in used.Where(u => Lossy(expression.Type, u.Type)))
                    findings.Add(new Finding
                    {
                        Severity = Severity.WorthChecking,
                        Key = "type|" + playable + "|" + expression.Name, Playable = playable,
                        Title = "\"" + expression.Name + "\" is a " + expression.Type + " in Expression Parameters but " + (type == AnimatorControllerParameterType.Int ? "an " : "a ") + type + " in " + playable,
                        Detail = "VRChat converts the value on the way in, and " + Conversion(expression.Type, type) + " That's fine if it's intended.",
                        Fix = "If it isn't intended, give both the same type.",
                        Target = avatar.Playables.First(p => p.Name == playable).Controller
                    });
            }
        }

        // Bool or Int into a Float (or Bool into Int) keeps every value, as templates intend; these lose some.
        private static bool Lossy(AnimatorControllerParameterType from, AnimatorControllerParameterType to) =>
            from == AnimatorControllerParameterType.Float && (to == AnimatorControllerParameterType.Int || to == AnimatorControllerParameterType.Bool) ||
            from == AnimatorControllerParameterType.Int && to == AnimatorControllerParameterType.Bool;

        private static string Conversion(AnimatorControllerParameterType from, AnimatorControllerParameterType to) =>
            from == AnimatorControllerParameterType.Float ? "fractions are lost: 0.5 doesn't arrive as 0.5." : "every value above 0 becomes true, so 2 and 1 look the same.";
    }
}
