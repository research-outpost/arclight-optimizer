using nadena.dev.ndmf;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer
{
    // The Arclight Optimizer component: one per avatar root, one toggle per feature. The class name and
    // script GUID stay from the texture-only releases so existing avatars keep their component.
    [DisallowMultipleComponent]
    [AddComponentMenu("Arclight/Arclight Optimizer")]
    public sealed class AvatarTextureOptimizer : MonoBehaviour, INDMFEditorOnly
    {
        // Every lossless feature is always on. These are not serialized, so values saved by earlier versions (where
        // they were toggles) are ignored; only tests switch them off, on the object they build directly.
        [System.NonSerialized] public bool optimizeTextures = true;
        [System.NonSerialized] public bool mergeDuplicates = true;
        [System.NonSerialized] public bool optimizeMeshes = true;
        [System.NonSerialized] public bool optimizeAudio = true;
        [System.NonSerialized] public bool optimizeAnimations = true;
        [HideInInspector] // Parked: no toggle, and the build ignores it. Kept so saved components stay readable.
        public bool splitPhysBones;
        // Imported Unity dimensions after active-platform sizing. Rectangles use their longer side.
        public static int GetPaddingPixels(int width, int height)
        {
            if (width <= 0) throw new System.ArgumentOutOfRangeException(nameof(width), "Texture dimensions must be positive.");
            if (height <= 0) throw new System.ArgumentOutOfRangeException(nameof(height), "Texture dimensions must be positive.");
            int resolution = System.Math.Max(width, height);
            if (resolution <= 128) return 4;
            if (resolution <= 512) return 8;
            if (resolution <= 1024) return 16;
            if (resolution <= 2048) return 24;
            return 32;
        }
        public const string OutputFolder = "Assets/Arclight/Optimizer/Cache";
        [Tooltip("Attempt unsupported texture fields using UV0 and property tiling/offset. Incorrect sampling assumptions can cause rendering artifacts.")]
        public bool allowUnsupportedShaders;
        [Tooltip("Keep the blend shapes MMD dance worlds animate on the Body mesh, so the face still moves in those worlds. Turn off to let Arclight bake them when nothing else animates them.")]
        public bool keepMmdShapes = true;
        [Tooltip("Give every skinned mesh the Hips as root bone and one bounding box sized to everything the avatar can reach, so meshes never vanish in close-ups and meshes that differed only by bounds can merge.")]
        public bool unifiedBounds;
        [Tooltip("Objects Arclight leaves alone, with everything under them: no renderer, mesh, material, texture, bone, PhysBone, contact, particle or audio change or removal. Use it if an automatic decision is ever wrong for an object.")]
        public System.Collections.Generic.List<Transform> exclude = new System.Collections.Generic.List<Transform>();
    }
}
