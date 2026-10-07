using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor.Analyzer
{
    // Step 3: menu structure, objects that start differently in game than in the scene, and the synced-bit budget.
    internal static partial class AvatarAnalyzer
    {
        // VRChat's limit for synced expression parameters.
        internal const int SyncLimit = 256;

        internal static int SyncedBits(Avatar avatar) =>
            avatar.Expressions.Where(e => e.Synced).Sum(e => e.Type == AnimatorControllerParameterType.Bool ? 1 : 8);

        private static readonly Dictionary<string, (Severity Severity, Func<int, string> Title, string Detail, string Fix)> MenuKinds = new Dictionary<string, (Severity, Func<int, string>, string, string)>
        {
            { "full", (Severity.Broken, n => N(n, "menu", "has", "have") + " more than " + MenuLimit + " controls", "VRChat only shows the first " + MenuLimit + " controls on a page, so the rest can't be reached",
                "Move the extra controls into a submenu, so each page has " + MenuLimit + " or fewer.") },
            { "empty", (Severity.Broken, n => N(n, "submenu button", "opens", "open") + " nothing", "No submenu is set on them, so pressing them does nothing", "Pick the submenu each button should open, or remove the button.") },
            { "loop", (Severity.WorthChecking, n => N(n, "submenu", "opens", "open") + " a menu above " + (n == 1 ? "it" : "them"), "They lead back to a menu that's already open, which is confusing to use and usually a mistake",
                "Point each one at the submenu you meant.") },
            { "nothing", (Severity.WorthChecking, n => N(n, "puppet control", "has", "have") + " no rotation or axis parameter", "A radial or axis puppet without a rotation or axis parameter does nothing when you move it", "Pick the parameter each puppet should change, or remove it.") },
        };

        private static void MenuStructure(Avatar avatar, List<Finding> findings)
        {
            foreach (var group in avatar.MenuIssues.GroupBy(i => i.Kind))
            {
                var kind = MenuKinds[group.Key];
                var places = group.Select(i => i.Path).Distinct().ToList();
                findings.Add(new Finding
                {
                    Severity = kind.Severity, Key = "menus|" + group.Key, Target = group.First().Menu,
                    Title = kind.Title(places.Count),
                    Detail = kind.Detail + ":\n" + string.Join("\n", places.Take(8).Select(p => "• " + p)) + (places.Count > 8 ? "\n• and " + (places.Count - 8) + " more" : ""),
                    Fix = kind.Fix
                });
            }
            int bits = SyncedBits(avatar);
            if (bits > SyncLimit)
                findings.Add(new Finding
                {
                    Severity = Severity.Broken, Key = "budget", Target = avatar.ExpressionAsset,
                    Title = "Too many synced parameters: " + bits + " of " + SyncLimit + " bits",
                    Detail = "VRChat won't upload an avatar over the limit. A Bool costs 1 bit; an Int or Float costs 8.",
                    Fix = "Untick Synced on parameters only you need to see (local-only effects), remove ones nothing uses, or combine groups of Bools into one Int."
                });
        }

        // Objects that start in a different on/off state in game than they have in the scene. VRChat starts every layer in its
        // default state with each parameter at its Expression Parameters default (else the animator's), so a layer whose start
        // (Entry, then one transition at most) plays a clip setting an object on or off decides how the object first appears. Judged as authored,
        // for state machines whose start plays a plain clip; the highest layer writing an object wins.
        private static void StartStates(Avatar avatar, List<Finding> findings)
        {
            var fx = avatar.Playables.FirstOrDefault(p => p.Name == "FX");
            if (fx == null) return;
            var values = fx.Controller.parameters.ToDictionary(p => p.name, p => p.type == AnimatorControllerParameterType.Float ? p.defaultFloat
                : p.type == AnimatorControllerParameterType.Int ? p.defaultInt : p.defaultBool ? 1f : 0f, StringComparer.Ordinal);
            // VRChat's built-ins that are not 0 when an avatar loads in standing.
            values["Grounded"] = 1; values["Upright"] = 1; values["AvatarVersion"] = 3;
            foreach (var e in avatar.Expressions) values[e.Name] = e.Default;
            bool Fires(AnimatorTransitionBase t) => t && !t.mute && t.conditions.All(c => Holds(c, values));
            // Where Entry leads: the first Entry transition that fires, else the default state; null for a sub-state machine.
            AnimatorState Enter(AnimatorStateMachine machine)
            {
                var entry = machine.entryTransitions.FirstOrDefault(Fires);
                return entry ? entry.destinationState : machine.defaultState;
            }
            var starts = new Dictionary<GameObject, (bool On, string Layer)>();
            var layers = fx.Controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                if (i > 0 && layers[i].defaultWeight == 0 || layers[i].syncedLayerIndex >= 0 || !layers[i].stateMachine) continue;
                var machine = layers[i].stateMachine;
                var state = Enter(machine);
                if (!state) continue;
                // One step on: an Any State or state transition that fires (an exit goes back through Entry).
                var next = machine.anyStateTransitions.Concat(state.transitions).FirstOrDefault(t => Fires(t) && (t.destinationState || t.isExit));
                if (next) state = next.isExit ? Enter(machine) : next.destinationState;
                if (!state || !(state.motion is AnimationClip clip)) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip).Where(b => b.type == typeof(GameObject) && b.propertyName == "m_IsActive"))
                {
                    var target = binding.path.Length == 0 ? null : avatar.Root.transform.Find(binding.path);
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (target && curve != null && curve.length > 0) starts[target.gameObject] = (curve.keys[0].value >= .5f, layers[i].name);
                }
            }
            var different = starts.Where(s => s.Key.activeSelf != s.Value.On).OrderBy(s => s.Key.name, StringComparer.Ordinal).ToList();
            if (different.Count == 0) return;
            findings.Add(new Finding
            {
                Severity = Severity.WorthChecking, Key = "start|FX", Playable = "FX", Target = different[0].Key,
                Title = N(different.Count, "object", "starts", "start") + " differently in game than in your scene",
                Detail = "In game, each toggle starts from its parameter's default value, which doesn't match what you see in Unity:\n" +
                    string.Join("\n", different.Take(8).Select(s => "• " + s.Key.name + ": " + (s.Value.On ? "on" : "off") + " in game, " + (s.Value.On ? "off" : "on") + " in the scene (layer \"" + s.Value.Layer + "\")")) +
                    (different.Count > 8 ? "\n• and " + (different.Count - 8) + " more" : "") +
                    (avatar.Expressions.Any(e => e.Saved) ? "\nToggles set to Saved start from your last choice instead, so for those this only applies the first time." : ""),
                Fix = "Decide which is right. To match the game, turn the object on or off in the scene. To match the scene, change the parameter's default in Expression Parameters."
            });
        }

        private static bool Holds(AnimatorCondition condition, Dictionary<string, float> values)
        {
            values.TryGetValue(condition.parameter, out float v); // A missing parameter reads 0 here; check 1 reports it.
            switch (condition.mode)
            {
                case AnimatorConditionMode.If: return v != 0;
                case AnimatorConditionMode.IfNot: return v == 0;
                case AnimatorConditionMode.Greater: return v > condition.threshold;
                case AnimatorConditionMode.Less: return v < condition.threshold;
                case AnimatorConditionMode.Equals: return v == condition.threshold;
                case AnimatorConditionMode.NotEqual: return v != condition.threshold;
                default: return false; // Triggers start unset.
            }
        }
    }
}
