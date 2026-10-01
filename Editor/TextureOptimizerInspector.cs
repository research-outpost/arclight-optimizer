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
            var optimize = serializedObject.FindProperty(nameof(AvatarTextureOptimizer.optimizeTextures));
            EditorGUILayout.PropertyField(optimize, new GUIContent("Optimize textures",
                "Clear texture areas the meshes never show. Colour textures get their padding rebuilt from the used pixels, and a texture is replaced only if its estimated compressed size shrinks."));
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(!optimize.boolValue))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.mergeDuplicateTextures)),
                    new GUIContent("Merge duplicate textures", "Use one texture wherever PNGs have identical pixels, compatible colour metadata and identical import settings, so the copies are not uploaded."));
                var allowUnsupported = serializedObject.FindProperty("allowUnsupportedShaders");
                EditorGUILayout.PropertyField(allowUnsupported, new GUIContent("Allow unsupported shaders"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.mergeDuplicates)),
                new GUIContent("Merge duplicates", "Use one copy wherever the build contains identical animation clips or materials (compared on all data and settings), so the copies are not uploaded. Runs after the other optimizers."));
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("Open reports folder"))
            {
                AutomaticTextureOptimizer.EnsureFolder(AvatarTextureOptimizer.OutputFolder);
                EditorUtility.RevealInFinder(AvatarTextureOptimizer.OutputFolder);
            }
        }
    }
}
