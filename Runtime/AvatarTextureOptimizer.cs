using nadena.dev.ndmf;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer
{
    // Opt-in resolution cap for build copies of the avatar's textures. Values are the pixel cap itself, so the
    // inspector's dropdown and the importer's maxTextureSize are the same number; Off keeps every authored size.
    public enum TextureSizeCap
    {
        Off = 0,
        Size1024 = 1024,
        Size2048 = 2048,
        Size4096 = 4096
    }

    // Values are the pixel cap itself, so the inspector's dropdown and the importer's maxTextureSize are the same number.
    public enum MenuIconCap
    {
        Off = 0,
        Size64 = 64,
        Size128 = 128,
        Size256 = 256
    }

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
        [Tooltip("Cap build copies of the avatar's textures at 1024, 2048 or 4096 pixels, or keep the authored size (Off). Textures above the cap are resampled by Unity's importer in a build copy, so they look softer when displayed large; the source files are never changed and a copy is used only when its measured bundle is smaller.")]
        public TextureSizeCap textureSizeCap;
        [Tooltip("Cap expression-menu icon build copies at 64, 128 or 256 pixels. Icons are drawn small in VRChat's menu, so a cap usually looks the same and cuts their bundle cost several-fold. A copy is used only when its measured bundle is smaller. Source files are never changed.")]
        public MenuIconCap menuIconCap;
        [Tooltip("Narrow opaque colour textures that Unity imports as BC7 (\"Compressed (High Quality)\") to BC1, at half their size. Very smooth gradients can band; look at the built avatar before uploading. Normal maps, textures whose alpha is read, and every other format are never narrowed. Source files are never changed.")]
        public bool narrowOpaqueTextures;
        [Tooltip("Re-encode uncompressed (PCM) audio clips as Vorbis in the built avatar. Cuts music and voice lines by roughly an order of magnitude; the sound changes by the codec's loss. The source files are never changed.")]
        public bool compressAudio;
        [Range(0f, 1f)]
        [Tooltip("Vorbis quality for Compress Audio. 0.7 is a good size/quality trade; 1 is transparent for most material.")]
        public float audioQuality = 0.7f;
    }
}
