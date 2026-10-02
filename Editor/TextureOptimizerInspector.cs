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
            EditorGUILayout.HelpBox("Clears unused texture areas, uses smaller texture formats where a channel is never read, merges duplicate " +
                "textures, materials, clips, meshes and audio, compacts mesh indices, stores identical-channel stereo audio as mono, and trims " +
                "redundant animation keys. Everything runs on a build copy and never changes how the avatar looks or sounds.", MessageType.None);
            var allowUnsupported = serializedObject.FindProperty("allowUnsupportedShaders");
            EditorGUILayout.PropertyField(allowUnsupported, new GUIContent("Allow unsupported shaders",
                "Attempt texture fields of shaders whose sampling is not known, assuming UV0 and the property's tiling/offset. Can cause visible seams; test before uploading."));
            serializedObject.ApplyModifiedProperties();
            if (GUILayout.Button("Open reports folder"))
            {
                AutomaticTextureOptimizer.EnsureFolder(AvatarTextureOptimizer.OutputFolder);
                EditorUtility.RevealInFinder(AvatarTextureOptimizer.OutputFolder);
            }
        }
    }
}
