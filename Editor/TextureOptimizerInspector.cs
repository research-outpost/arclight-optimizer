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
            EditorGUILayout.PropertyField(allowUnsupported, new GUIContent("Unsupported Shaders",
                "Also optimizes textures on unknown shaders by guessing how they're mapped. A wrong guess can show seams; test before uploading. Known shaders are never affected."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.keepMmdShapes)), new GUIContent("MMD Support",
                "Keep the blend shapes MMD dance worlds animate on the Body mesh, so the face still moves in those worlds. Turn off to freeze them when nothing else animates them; the face then won't move in MMD worlds."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.unifiedBounds)), new GUIContent("Unified Bounds",
                "Gives all skinned meshes one shared bounding box, so nothing vanishes up close and more meshes can merge. Cost: meshes may draw when just off screen. Excluded meshes and meshes on stretching, scaled or constrained bones keep their own."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.compressAudio)), new GUIContent("Compress Audio",
                "Compresses uncompressed audio in the build to about a tenth of the size, with some quality loss."));
            using (new EditorGUI.DisabledScope(!serializedObject.FindProperty(nameof(AvatarTextureOptimizer.compressAudio)).boolValue))
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.audioQuality)), new GUIContent("Audio Quality",
                    "0 = smallest, 1 = best. 0.7 is a good balance; at 1 most clips sound like the original."));
            // Hand-built popups instead of enum PropertyFields only for the "1024 px" style labels; the widget is the same control.
            var textureSizeCap = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.textureSizeCap));
            EditorGUI.BeginChangeCheck();
            int sizeIndex = EditorGUILayout.Popup(new GUIContent("Texture Size Cap",
                    "PC only. Shrinks larger textures to this size in the build; they look softer when displayed large. Off keeps full size."),
                textureSizeCap.hasMultipleDifferentValues ? -1 : textureSizeCap.enumValueIndex, TextureSizeCapLabels);
            if (EditorGUI.EndChangeCheck()) textureSizeCap.enumValueIndex = sizeIndex;

            var menuIconCap = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.menuIconCap));
            EditorGUI.BeginChangeCheck();
            int capIndex = EditorGUILayout.Popup(new GUIContent("Menu Icon Cap",
                    "Cap expression-menu icon build copies at 64, 128 or 256 pixels, or keep the authored size (Off). Icons are drawn small in VRChat's menu, so a cap usually looks the same and cuts their size several-fold."),
                menuIconCap.hasMultipleDifferentValues ? -1 : menuIconCap.enumValueIndex, MenuIconCapLabels);
            if (EditorGUI.EndChangeCheck()) menuIconCap.enumValueIndex = capIndex;
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.narrowOpaqueTextures)), new GUIContent("Narrow Opaque Textures",
                "PC only. Stores high-quality (BC7) opaque colour textures as BC1, half the size. Very smooth gradients can band; check close up before uploading. Normal maps and textures with used alpha are untouched."));
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

        // Dropdown labels for TextureSizeCap, in the enum's declaration order (Off, Size1024, Size2048, Size4096).
        private static readonly GUIContent[] TextureSizeCapLabels = { new GUIContent("Off"), new GUIContent("1024 px"), new GUIContent("2048 px"), new GUIContent("4096 px") };

        // Dropdown labels for MenuIconCap, in the enum's declaration order (Off, Size64, Size128, Size256).
        private static readonly GUIContent[] MenuIconCapLabels = { new GUIContent("Off"), new GUIContent("64 px"), new GUIContent("128 px"), new GUIContent("256 px") };

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
            if (!EditorUtility.DisplayDialog("Clear cache", "Cached files: " + plan.Assets.Count + " (" + EditorUtility.FormatBytes(plan.Bytes) +
                "). Move to the trash? They're rebuilt on the next build.", "Clear", "Cancel")) return;
            CacheCleanup.Apply(plan, folder);
        }
    }
}
