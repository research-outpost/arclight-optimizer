using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    [CustomEditor(typeof(AvatarTextureOptimizer))]
    internal sealed class TextureOptimizerInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var root = ((Component)target).gameObject; // The component sits on the avatar root.
            var avatarOptimizer = AvatarOptimizerConflict.Find(root);
            if (avatarOptimizer)
                EditorGUILayout.HelpBox("This avatar has Avatar Optimizer's " + avatarOptimizer.GetType().Name + " component on " + avatarOptimizer.gameObject.name +
                    ". Arclight Optimizer does not work alongside Avatar Optimizer, so the build will stop with an error. Remove Avatar Optimizer's components or this one.", MessageType.Error);
            var allowUnsupported = serializedObject.FindProperty("allowUnsupportedShaders");
            EditorGUILayout.PropertyField(allowUnsupported, new GUIContent("Allow Unsupported Shaders",
                "Attempt texture fields of shaders Arclight does not recognize, assuming UV0 and the property's tiling/offset. Never overrides what a recognized shader (lilToon, Poiyomi and the others) keeps. Can cause visible seams; test before uploading."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.keepMmdShapes)), new GUIContent("MMD Support",
                "Keep the blend shapes MMD dance worlds animate on the Body mesh, so the face still moves in those worlds. Turn off to let Arclight bake them when nothing else animates them."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.exclude)), new GUIContent("Exclude",
                "Objects Arclight leaves alone, with everything under them: no renderer, mesh, material, texture, bone, PhysBone, contact, particle or audio change or removal. Use it if an automatic decision is ever wrong for an object."), true);
            serializedObject.ApplyModifiedProperties();
            using (new EditorGUILayout.HorizontalScope())
            {
                string report = LastReport(root.name);
                using (new EditorGUI.DisabledScope(report == null))
                    if (GUILayout.Button(new GUIContent("Open last report", report == null ? "No report for this avatar yet; build or enter Play Mode first." : report)))
                        EditorUtility.OpenWithDefaultApp(report);
                if (GUILayout.Button("Open reports folder"))
                {
                    AutomaticTextureOptimizer.EnsureFolder(AvatarTextureOptimizer.OutputFolder);
                    EditorUtility.RevealInFinder(AvatarTextureOptimizer.OutputFolder);
                }
                if (GUILayout.Button(new GUIContent("Clear cache", "Moves every generated texture and audio clip to the trash. They are generated again on the next build, which then takes longer.")))
                    ClearCache();
            }
        }

        // The newest report whose file name carries this avatar's name (reports are named after the build copy).
        private static string LastReport(string avatarName)
        {
            string folder = AvatarTextureOptimizer.OutputFolder;
            if (!Directory.Exists(folder)) return null;
            string name = System.Text.RegularExpressions.Regex.Replace(avatarName ?? "", @"[^A-Za-z0-9_-]+", "_").Trim('_', '-');
            return Directory.GetFiles(folder, "ArclightTextureOptimizer_*.txt")
                .Where(f => name.Length == 0 || Path.GetFileName(f).StartsWith("ArclightTextureOptimizer_" + name, System.StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        private static void ClearCache()
        {
            string folder = AutomaticTextureOptimizer.CacheFolder;
            var plan = CacheCleanup.Prepare(folder, unusedDays: -1); // -1: every entry counts as unused.
            if (plan.IsEmpty) { EditorUtility.DisplayDialog("Clear cache", "The cache is already empty.", "OK"); return; }
            if (!EditorUtility.DisplayDialog("Clear cache", "Move " + plan.Assets.Count + " cached file(s) (" + EditorUtility.FormatBytes(plan.Bytes) +
                ") to the trash? Later builds generate them again.", "Clear", "Cancel")) return;
            CacheCleanup.Apply(plan, folder);
        }
    }
}
