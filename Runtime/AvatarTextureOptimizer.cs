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
        [Tooltip("Clear texture areas the meshes never show, so the PNGs compress smaller.")]
        public bool optimizeTextures = true;
        [Tooltip("Use one copy wherever the build contains identical textures, materials, animation clips, meshes or audio clips, so the copies are not uploaded.")]
        public bool mergeDuplicates = true;
        [Tooltip("Give meshes with at most 65,536 vertices a 16-bit index buffer instead of a 32-bit one. Identical triangles, half the index data.")]
        public bool optimizeMeshes = true;
        [Tooltip("Store stereo clips whose two channels are identical as mono with +3 dB, which plays at the same level, where the audio source's setup has been measured to match.")]
        public bool optimizeAudio = true;
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
        public const string OutputFolder = "Assets/Arclight/Optimizer/Textures/Cache";
        [Tooltip("Attempt unsupported texture fields using UV0 and property tiling/offset. Incorrect sampling assumptions can cause rendering artifacts.")]
        public bool allowUnsupportedShaders;
    }
}
