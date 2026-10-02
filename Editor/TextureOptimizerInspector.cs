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
                var allowUnsupported = serializedObject.FindProperty("allowUnsupportedShaders");
                EditorGUILayout.PropertyField(allowUnsupported, new GUIContent("Allow unsupported shaders"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.mergeDuplicates)),
                new GUIContent("Merge duplicates", "Use one copy wherever the build contains identical data, so the copies are not uploaded: textures with identical pixels, colour metadata and import settings (while Optimize textures is on), and materials, animation clips, meshes and audio clips identical in all data and settings. Meshes and audio merge after Avatar Optimizer; materials and clips after the other optimizers."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.optimizeMeshes)),
                new GUIContent("Optimize meshes", "Give meshes with at most 65,536 vertices a 16-bit index buffer instead of a 32-bit one: identical triangles, half the index data in the download and in VRAM. Runs after Avatar Optimizer."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarTextureOptimizer.optimizeAudio)),
                new GUIContent("Optimize audio", "Store stereo clips whose two channels are identical as mono with +3 dB, which plays at the same level and balance, halving Vorbis clips. Only for clips that do not clip with the boost and whose audio sources are spatialized or unpanned 2D VRC Spatial Audio Sources, with no animated playback settings. Runs after Avatar Optimizer."));
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("Open reports folder"))
            {
                AutomaticTextureOptimizer.EnsureFolder(AvatarTextureOptimizer.OutputFolder);
                EditorUtility.RevealInFinder(AvatarTextureOptimizer.OutputFolder);
            }
        }
    }
}
