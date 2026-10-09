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
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.unifiedBounds)), new GUIContent("Unified Bounds",
                "Give every skinned mesh the Hips as root bone and one bounding box sized to everything the avatar can reach, so meshes never vanish in close-ups and meshes that differed only by bounds can merge. Meshes in the Exclude list, and meshes whose reach is not fixed (constrained, stretching or scaled bones), keep their own."));
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

        // The newest of this avatar's own reports. The file names are rebuilt exactly as the report writer makes them (they end with
        // a hash of the full name), for the avatar itself and its build copy, in and out of Play Mode, so a similarly named avatar
        // ("Chocofuyu" and "Chocofuyu Medium") never matches.
        private static string LastReport(string avatarName)
        {
            string folder = AvatarTextureOptimizer.OutputFolder;
            if (!Directory.Exists(folder)) return null;
            try
            {
                return new[] { avatarName, avatarName + "(Clone)" }
                    .SelectMany(name => new[] { false, true }.Select(play => folder.TrimEnd('/') + "/" + OptimizationLog.ReportFileName(name, play, folder)))
                    .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (IOException) { return null; } // The folder path is too long for any report name.
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
