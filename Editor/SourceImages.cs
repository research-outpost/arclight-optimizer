using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Source images other than PNG (PSD, TGA, TIFF, BMP, JPEG). Unity flattens these itself, so the pixels are read
    // from an uncompressed copy imported with the source's own importer metadata: they are exactly the full-resolution
    // pixels the avatar's texture is built from, before GPU compression and mips. The replacement is a PNG of those
    // pixels (with the same import settings), so the file type changes but the imported texture does not.
    internal static class SourceImages
    {
        private static readonly string[] Flattened = { ".psd", ".tga", ".tif", ".tiff", ".bmp", ".jpg", ".jpeg" };
        private static readonly string[] Platforms = { "Standalone", "Android", "iPhone", "WebGL", "Windows Store Apps" };
        internal const string TempPrefix = "_flatten_";

        internal static bool IsPng(string path) => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        internal static bool IsFlattened(string path) =>
            Flattened.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        internal static bool IsSupported(string path) => IsPng(path) || IsFlattened(path);

        // Full-resolution size of the source file as Unity imports it.
        internal static void Size(TextureImporter importer, out int width, out int height) =>
            importer.GetSourceTextureWidthAndHeight(out width, out height);

        // Reads the flattened pixels. The temporary copy lives in the cache folder only for the duration of the call.
        internal static Color32[] Flatten(string path, string folder, out PngInfo info)
        {
            string sourceMeta = path + ".meta";
            if (!File.Exists(LongPath.For(sourceMeta))) throw new InvalidOperationException("Source importer metadata is missing; original retained.");
            // Same importer metadata as the source, so format options such as PSD matte removal and the alpha source match.
            string meta = GenerationCoordinator.CopiedImporterMeta(sourceMeta, "AvatarTextureOptimizer:flatten");
            if (meta == null) throw new InvalidOperationException("Source importer metadata could not be reused to flatten the image; original retained.");

            string temp = folder + "/" + TempPrefix + Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant();
            try
            {
                File.Copy(LongPath.For(path), temp);
                File.WriteAllText(temp + ".meta", meta, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(temp, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(temp) as TextureImporter;
                if (!importer) throw new InvalidOperationException("The source image could not be imported for flattening; original retained.");
                importer.isReadable = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.crunchedCompression = false;
                importer.mipmapEnabled = false;
                importer.streamingMipmaps = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = 16384;
                foreach (string platform in Platforms) importer.ClearPlatformTextureSettings(platform);
                importer.SaveAndReimport();

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(temp);
                if (!texture) throw new InvalidOperationException("The source image could not be flattened; original retained.");
                importer.GetSourceTextureWidthAndHeight(out int width, out int height);
                if (texture.width != width || texture.height != height)
                    throw new InvalidOperationException("The flattened image size differs from the source size; original retained.");
                UvCoverageRasterizer.ValidateSize(width, height);
                bool alpha = texture.format == TextureFormat.RGBA32 || texture.format == TextureFormat.ARGB32;
                info = new PngInfo { Width = width, Height = height, Alpha = alpha, BitDepth = 8, ColorType = alpha ? 6 : 2 };
                return texture.GetPixels32();
            }
            finally
            {
                if (File.Exists(temp) || File.Exists(temp + ".meta")) AssetDatabase.DeleteAsset(temp);
                if (File.Exists(temp)) File.Delete(temp);
                if (File.Exists(temp + ".meta")) File.Delete(temp + ".meta");
            }
        }
    }
}
