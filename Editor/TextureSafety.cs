using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    internal static class TextureSafety
    {
        public static TextureImporter Validate(Texture2D source)
        {
            if (!source) throw new InvalidOperationException("Only persistent Texture2D assets are supported.");
            string path = AssetDatabase.GetAssetPath(source);
            if (!SourceImages.IsSupported(path))
                throw new InvalidOperationException("Only PNG, PSD, TGA, TIFF, BMP and JPEG sources are processed; other formats are retained.");
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (!importer || (importer.textureType != TextureImporterType.Default && importer.textureType != TextureImporterType.NormalMap) ||
                importer.textureShape != TextureImporterShape.Texture2D)
                throw new InvalidOperationException("Only Default or authored NormalMap 2D textures are supported.");
            bool flattened = !SourceImages.IsPng(path);
            if (flattened && importer.textureType == TextureImporterType.NormalMap)
                throw new InvalidOperationException("Normal maps must be authored PNGs; this source format is retained.");
            if (importer.textureType == TextureImporterType.NormalMap)
            {
                if (importer.convertToNormalmap)
                    throw new InvalidOperationException("Heightmap-to-normal conversion is unsupported; original retained.");
                if (importer.sRGBTexture)
                    throw new InvalidOperationException("Normal maps require linear import settings; original retained.");
                if (importer.alphaIsTransparency)
                    throw new InvalidOperationException("Normal maps with Alpha Is Transparency enabled are unsupported; original retained.");
            }
            if (source.wrapModeU == TextureWrapMode.MirrorOnce || source.wrapModeV == TextureWrapMode.MirrorOnce)
                throw new InvalidOperationException("Mirror Once coverage is implemented, but PC/Android sampler equivalence is not certified.");
            if (new FileInfo(path).Length > 128L * 1024 * 1024) throw new InvalidOperationException("Source file exceeds the 128 MiB encoded-file budget.");
            if (flattened)
            {
                SourceImages.Size(importer, out int flatWidth, out int flatHeight);
                UvCoverageRasterizer.ValidateSize(flatWidth, flatHeight);
                return importer;
            }
            var header = PngPixels.Inspect(File.ReadAllBytes(LongPath.For(path)));
            if (importer.textureType == TextureImporterType.NormalMap &&
                (header.BitDepth != 8 && header.BitDepth != 16 || (header.ColorType != 2 && header.ColorType != 6)))
                throw new InvalidOperationException("NormalMap sources must remain authored 8- or 16-bit RGB/RGBA PNGs; grayscale and indexed input are unsupported.");
            UvCoverageRasterizer.ValidateSize(header.Width, header.Height);
            // Coverage and padding use encoded source dimensions. Unity applies the copied mip,
            // compression, NPOT and per-platform size settings to the newly generated PNG.
            return importer;
        }

        internal static TextureImporter ValidateUsage(TextureGroup group)
        {
            var importer = Validate(group.Source as Texture2D);
            var semantics = group.ActiveUses.Select(u => u.Sampling.Semantics).Distinct().ToArray();
            bool hasNormal = semantics.Contains(TextureSemantics.Normal);
            if (hasNormal && (semantics.Length != 1 || importer.textureType != TextureImporterType.NormalMap))
                throw new InvalidOperationException("Normal sampling requires a NormalMap importer and no mixed normal/colour/data uses; original retained.");
            if (!hasNormal && importer.textureType == TextureImporterType.NormalMap)
                throw new InvalidOperationException("NormalMap textures used as colour or data are unsupported; original retained.");
            if (semantics.Any(s => s != TextureSemantics.Color && s != TextureSemantics.Data && s != TextureSemantics.Normal))
                throw new InvalidOperationException("Unknown texture semantics; original retained.");
            return importer;
        }

        // withoutStandalone leaves out the Standalone platform settings, which a channel format change replaces. ignoreReadable leaves
        // out Read/Write, which generated copies turn off (see DropReadable).
        public static string SettingsFingerprint(TextureImporter importer, bool withoutStandalone = false, bool ignoreReadable = false)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (ignoreReadable) settings.readable = false;
            return JsonUtility.ToJson(settings) + "|" +
                importer.textureCompression + "|" + importer.compressionQuality + "|" + importer.crunchedCompression + "|" +
                importer.maxTextureSize + "|" + importer.streamingMipmaps + "|" + importer.streamingMipmapsPriority + "|" +
                (ignoreReadable ? "" : importer.isReadable.ToString()) + "|" + importer.ignorePngGamma + "|" +
                JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings()) + "|" +
                (withoutStandalone ? "" : JsonUtility.ToJson(importer.GetPlatformTextureSettings("Standalone"))) + "|" +
                JsonUtility.ToJson(importer.GetPlatformTextureSettings("Android"));
        }

        // A readable texture keeps a CPU copy in the uploaded avatar that nothing on an avatar reads (shaders sample the GPU copy), so a
        // generated copy turns Read/Write off: the same texture, less RAM for everyone who loads the avatar. Reimports when it changes.
        internal static void DropReadable(TextureImporter importer)
        {
            if (!importer.isReadable) return;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        public static void CopyImporter(TextureImporter source, TextureImporter destination)
        {
            var settings = new TextureImporterSettings();
            source.ReadTextureSettings(settings);
            destination.SetTextureSettings(settings);
            destination.textureCompression = source.textureCompression;
            destination.compressionQuality = source.compressionQuality;
            destination.crunchedCompression = source.crunchedCompression;
            destination.maxTextureSize = source.maxTextureSize;
            destination.streamingMipmaps = source.streamingMipmaps;
            destination.streamingMipmapsPriority = source.streamingMipmapsPriority;
            destination.isReadable = source.isReadable;
            destination.ignorePngGamma = source.ignorePngGamma;
            destination.SetPlatformTextureSettings(source.GetDefaultPlatformTextureSettings());
            foreach (string platform in new[] { "Standalone", "Android" })
                destination.SetPlatformTextureSettings(source.GetPlatformTextureSettings(platform));
            // Unity 2022.3's public platform setter normalizes legacy BC6H/BC7 quality flags.
            // Copy only the platform-settings subtree to preserve them and other stored overrides.
            // This serialized field is version-dependent; absence fails closed, never edits the source.
            using (var original = new SerializedObject(source))
            using (var generated = new SerializedObject(destination))
            {
                var platforms = original.FindProperty("m_PlatformSettings");
                if (platforms == null || generated.FindProperty("m_PlatformSettings") == null)
                    throw new InvalidOperationException("This Unity version's platform-settings schema is unsupported.");
                generated.CopyFromSerializedProperty(platforms);
                generated.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        public static string ValidateFolder(string folder)
        {
            folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a folder under Assets/.");
            string full = Path.GetFullPath(folder);
            string assets = Path.GetFullPath(Application.dataPath);
            if (full != assets && !full.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output folder escapes Assets/.");
            if (!AssetDatabase.IsValidFolder(folder)) throw new InvalidOperationException("Output folder must already exist in the Project window.");
            for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Output through filesystem links is not supported.");
                if (string.Equals(directory.FullName, assets, StringComparison.OrdinalIgnoreCase)) break;
            }
            return folder;
        }
    }

    // A PC texture Unity compresses to DXT5 (BC3) stores its colour in the same block format as BC1, plus a
    // separate alpha block. When no shader reading the texture uses alpha, or its alpha is 255 in every texel (DXT5 then
    // decodes it to exactly 1, as BC1 does; GenerationCoordinator decides that), BC1 keeps that colour at half the size.
    // A linear texture read only through .r becomes BC4 instead (from DXT5 or DXT1), which stores its one channel
    // more precisely than a BC1/BC3 colour channel; from DXT1 that is the same size, so it is a quality gain only.
    // An uncompressed (RGBA32, ARGB32 or RGB24) linear texture read only through .r becomes R8: the same red bytes, a quarter or
    // a third of the size. BC7, crunched and Android formats, and other uncompressed ones, are never changed.
    internal static class ChannelFormats
    {
        public static TextureImporterFormat? Choose(TextureImporter importer, TextureChannels read)
        {
            if (importer.textureType != TextureImporterType.Default || (read & TextureChannels.A) != 0) return null;
            var format = Current(importer);
            bool redOnly = read == TextureChannels.R && !importer.sRGBTexture;
            if (Uncompressed(format)) return redOnly ? TextureImporterFormat.R8 : (TextureImporterFormat?)null;
            if (format == TextureImporterFormat.DXT1) return redOnly ? TextureImporterFormat.BC4 : (TextureImporterFormat?)null;
            if (format != TextureImporterFormat.DXT5) return null;
            return redOnly ? TextureImporterFormat.BC4 : TextureImporterFormat.DXT1;
        }

        // True when the chosen format shrinks the stored size (a DXT5 or uncompressed source), which alone can justify a replacement.
        public static bool Shrinks(TextureImporter importer) => Current(importer) == TextureImporterFormat.DXT5 || Uncompressed(Current(importer));

        private static bool Uncompressed(TextureImporterFormat format) =>
            format == TextureImporterFormat.RGBA32 || format == TextureImporterFormat.ARGB32 || format == TextureImporterFormat.RGB24;

        private static TextureImporterFormat Current(TextureImporter importer)
        {
            var standalone = importer.GetPlatformTextureSettings("Standalone");
            return standalone.overridden ? standalone.format : importer.GetAutomaticFormat("Standalone");
        }

        // The importer's effective Standalone settings with only the format replaced.
        public static TextureImporterPlatformSettings Standalone(TextureImporter importer, TextureImporterFormat format)
        {
            var settings = importer.GetPlatformTextureSettings("Standalone");
            if (!settings.overridden)
            {
                // Choose only accepts automatic DXT1/DXT5, so the default settings are not crunched.
                var defaults = importer.GetDefaultPlatformTextureSettings();
                settings.maxTextureSize = defaults.maxTextureSize;
                settings.resizeAlgorithm = defaults.resizeAlgorithm;
                settings.compressionQuality = defaults.compressionQuality;
                settings.crunchedCompression = false;
                settings.overridden = true;
            }
            settings.format = format;
            return settings;
        }

        public static bool Matches(TextureImporter source, TextureImporter output, TextureImporterFormat format)
        {
            var expected = Standalone(source, format);
            var actual = output.GetPlatformTextureSettings("Standalone");
            return actual.overridden && actual.format == format && actual.maxTextureSize == expected.maxTextureSize &&
                actual.resizeAlgorithm == expected.resizeAlgorithm && actual.compressionQuality == expected.compressionQuality &&
                actual.crunchedCompression == expected.crunchedCompression;
        }
    }

    internal sealed class PngInfo
    {
        public int Width, Height, BitDepth, ColorType;
        public bool Alpha;
        // Complete original chunks, including CRC, copied back before image data.
        public readonly System.Collections.Generic.List<byte[]> ColourMetadata =
            new System.Collections.Generic.List<byte[]>();
    }

    internal static class PngPixels
    {
        private static bool IsColourMetadata(string type) =>
            type == "gAMA" || type == "cHRM" || type == "iCCP" || type == "sRGB";

        public static PngInfo InspectHeader(string path)
        {
            var bytes = new byte[33];
            using (var stream = File.OpenRead(LongPath.For(path)))
            {
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new InvalidOperationException("Truncated PNG header.");
                    read += count;
                }
            }
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (!bytes.Take(8).SequenceEqual(signature) || BigEndian(bytes, 8) != 13 ||
                System.Text.Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR")
                throw new InvalidOperationException("Invalid PNG header.");
            if (Crc(bytes, 12, 17) != BigEndian(bytes, 29))
                throw new InvalidOperationException("PNG IHDR CRC mismatch.");
            return new PngInfo
            {
                Width = checked((int)BigEndian(bytes, 16)),
                Height = checked((int)BigEndian(bytes, 20)),
                BitDepth = bytes[24],
                ColorType = bytes[25]
            };
        }

        public static PngInfo Inspect(byte[] bytes)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes == null || bytes.Length < 33 || !bytes.Take(8).SequenceEqual(signature))
                throw new InvalidOperationException("Invalid PNG signature.");
            var info = new PngInfo();
            bool header = false, imageData = false, end = false, palette = false, transparency = false;
            int paletteEntries = 0, metadataBytes = 0;
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int p = 8; p + 12 <= bytes.Length;)
            {
                uint size = BigEndian(bytes, p);
                if (size > int.MaxValue || (long)p + size + 12 > bytes.Length)
                    throw new InvalidOperationException("Truncated PNG.");
                string type = System.Text.Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "IHDR")
                {
                    if (header || size != 13 || p != 8) throw new InvalidOperationException("Invalid PNG header.");
                    if (Crc(bytes, p + 4, 17) != BigEndian(bytes, p + 21))
                        throw new InvalidOperationException("PNG IHDR CRC mismatch.");
                    info.Width = checked((int)BigEndian(bytes, p + 8)); info.Height = checked((int)BigEndian(bytes, p + 12));
                    info.BitDepth = bytes[p + 16]; info.ColorType = bytes[p + 17];
                    bool supported =
                        (info.ColorType == 0 && (info.BitDepth == 1 || info.BitDepth == 2 || info.BitDepth == 4 || info.BitDepth == 8)) ||
                        (info.ColorType == 2 && info.BitDepth == 8) ||
                        (info.ColorType == 3 && (info.BitDepth == 1 || info.BitDepth == 2 || info.BitDepth == 4 || info.BitDepth == 8)) ||
                        (info.ColorType == 4 && info.BitDepth == 8) ||
                        (info.ColorType == 6 && info.BitDepth == 8) ||
                        // 16-bit: read rounded to 8 bits, which is what Unity compresses (checked per texture on import).
                        (info.ColorType != 3 && info.BitDepth == 16 && bytes[p + 20] == 0);
                    if (!supported)
                        throw new InvalidOperationException(info.BitDepth > 8
                            ? "PNG precision above 8 bits is unsupported; original retained."
                            : "Unsupported PNG color type/bit depth; only 8-bit RGB/RGBA, 8-bit grayscale-alpha, and 1/2/4/8-bit grayscale/indexed PNGs are supported.");
                    if (bytes[p + 18] != 0 || bytes[p + 19] != 0 || bytes[p + 20] > 1)
                        throw new InvalidOperationException("Unsupported PNG compression, filter, or interlace method.");
                    info.Alpha = info.ColorType == 4 || info.ColorType == 6;
                    UvCoverageRasterizer.ValidateSize(info.Width, info.Height);
                    header = true;
                }
                if (type == "PLTE")
                {
                    if (!header || imageData || palette || transparency)
                        throw new InvalidOperationException("Invalid PNG PLTE order or duplicate.");
                    if (info.ColorType != 2 && info.ColorType != 3 && info.ColorType != 6)
                        throw new InvalidOperationException("PNG PLTE is not valid for this color type.");
                    if (size < 3 || size > 768 || size % 3 != 0)
                        throw new InvalidOperationException("Invalid PNG PLTE length.");
                    paletteEntries = (int)size / 3;
                    if (info.ColorType == 3 && paletteEntries > (1 << info.BitDepth))
                        throw new InvalidOperationException("PNG PLTE has more entries than the indexed bit depth allows.");
                    if (Crc(bytes, p + 4, (int)size + 4) != BigEndian(bytes, p + 8 + (int)size))
                        throw new InvalidOperationException("PNG PLTE CRC mismatch.");
                    palette = true;
                }
                if (type == "tRNS")
                {
                    if (!header || imageData || transparency)
                        throw new InvalidOperationException("Invalid PNG tRNS order or duplicate.");
                    bool validLength = info.ColorType == 0 ? size == 2 :
                        info.ColorType == 2 ? size == 6 :
                        info.ColorType == 3 && palette && size > 0 && size <= paletteEntries;
                    if (!validLength)
                        throw new InvalidOperationException("PNG tRNS has an invalid length, order, or color type.");
                    if (info.BitDepth == 16)
                        throw new InvalidOperationException("A 16-bit PNG with a transparency key is unsupported; original retained.");
                    if (Crc(bytes, p + 4, (int)size + 4) != BigEndian(bytes, p + 8 + (int)size))
                        throw new InvalidOperationException("PNG tRNS CRC mismatch.");
                    transparency = true;
                    info.Alpha = true;
                }
                if (type == "acTL" || type == "fcTL" || type == "fdAT")
                    throw new InvalidOperationException("Animated PNG (" + type + ") is not supported; original retained.");
                if (type == "cICP" || type == "mDCv" || type == "cLLi" || type == "sBIT")
                    throw new InvalidOperationException("PNG " + type + " colour/precision metadata is not yet supported; original retained.");
                if (type == "iCCP" && (info.ColorType == 0 || info.ColorType == 4))
                    throw new InvalidOperationException("Grayscale PNG iCCP profiles cannot be preserved on RGB/RGBA output; original retained.");
                if (IsColourMetadata(type))
                {
                    if (!header || imageData || palette || !seen.Add(type))
                        throw new InvalidOperationException("Invalid PNG " + type + " metadata order or duplicate.");
                    if (seen.Contains("sRGB") && seen.Contains("iCCP"))
                        throw new InvalidOperationException("PNG contains conflicting sRGB and iCCP declarations.");
                    metadataBytes = checked(metadataBytes + (int)size + 12);
                    if (metadataBytes > 8 * 1024 * 1024)
                        throw new InvalidOperationException("PNG colour metadata exceeds the 8 MiB budget.");
                    if ((type == "gAMA" && (size != 4 || BigEndian(bytes, p + 8) == 0)) ||
                        (type == "cHRM" && size != 32) ||
                        (type == "sRGB" && (size != 1 || bytes[p + 8] > 3)))
                        throw new InvalidOperationException("Invalid PNG " + type + " metadata.");
                    if (type == "iCCP")
                    {
                        int separator = Array.IndexOf(bytes, (byte)0, p + 8, (int)size);
                        int nameLength = separator - (p + 8);
                        if (nameLength < 1 || nameLength > 79 || separator + 2 >= p + 8 + size || bytes[separator + 1] != 0)
                            throw new InvalidOperationException("Invalid PNG iCCP profile header.");
                    }
                    if (Crc(bytes, p + 4, (int)size + 4) != BigEndian(bytes, p + 8 + (int)size))
                        throw new InvalidOperationException("PNG " + type + " metadata CRC mismatch.");
                    var chunk = new byte[(int)size + 12];
                    Buffer.BlockCopy(bytes, p, chunk, 0, chunk.Length);
                    info.ColourMetadata.Add(chunk);
                }
                if (type == "IDAT") imageData = true;
                p += (int)size + 12;
                if (type == "IEND") { end = size == 0; break; }
            }
            if (!header || !imageData || !end) throw new InvalidOperationException("Incomplete PNG.");
            if (info.ColorType == 3 && !palette) throw new InvalidOperationException("Indexed PNG requires a PLTE chunk.");
            return info;
        }

        private static uint BigEndian(byte[] b, int p) => ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];

        private static uint Crc(byte[] bytes, int start, int count)
        {
            uint crc = 0xffffffff;
            for (int i = start; i < start + count; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            return ~crc;
        }

        private static byte[] WithColourMetadata(byte[] png, System.Collections.Generic.IEnumerable<byte[]> chunks)
        {
            using (var output = new MemoryStream())
            {
                output.Write(png, 0, 8);
                for (int p = 8; p + 12 <= png.Length;)
                {
                    int count = checked((int)BigEndian(png, p) + 12);
                    string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
                    if (!IsColourMetadata(type)) output.Write(png, p, count);
                    if (type == "IHDR")
                        foreach (var chunk in chunks) output.Write(chunk, 0, chunk.Length);
                    p += count;
                }
                return output.ToArray();
            }
        }

        public static Color32[] Decode(byte[] bytes, out PngInfo info)
        {
            info = Inspect(bytes);
            if (info.BitDepth == 16) return Decode16(bytes, info);
            // Decode encoded samples without colour-profile interpretation or legacy gAMA conversion.
            // This changes only an in-memory copy. Exact metadata goes back on the generated PNG.
            byte[] raw = info.ColourMetadata.Count == 0 ? bytes : WithColourMetadata(bytes, Array.Empty<byte[]>());
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(decoded, raw, false) || decoded.width != info.Width || decoded.height != info.Height)
                    throw new InvalidOperationException("PNG decoding failed.");
                return decoded.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(decoded); }
        }

        // A non-interlaced 16-bit PNG (validated by Inspect), each sample rounded to 8 bits as round(v / 257): Unity
        // imports 16-bit PNGs this way before compressing them (an 8-bit copy rounded so compresses to identical bytes).
        private static Color32[] Decode16(byte[] bytes, PngInfo info)
        {
            int channels = info.ColorType == 0 ? 1 : info.ColorType == 2 ? 3 : info.ColorType == 4 ? 2 : 4;
            int bpp = channels * 2, stride = checked(info.Width * bpp);
            var compressed = new MemoryStream();
            for (int p = 8; p + 12 <= bytes.Length;)
            {
                int size = checked((int)BigEndian(bytes, p));
                string type = System.Text.Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "IDAT") compressed.Write(bytes, p + 8, size);
                p += size + 12;
                if (type == "IEND") break;
            }
            var raw = new byte[checked((stride + 1) * info.Height)];
            using (var zlib = new System.IO.Compression.DeflateStream(new MemoryStream(compressed.GetBuffer(), 2, (int)compressed.Length - 2),
                System.IO.Compression.CompressionMode.Decompress))
            {
                int read = 0, count;
                while (read < raw.Length && (count = zlib.Read(raw, read, raw.Length - read)) > 0) read += count;
                if (read != raw.Length) throw new InvalidOperationException("Truncated 16-bit PNG image data.");
            }
            var rows = new byte[stride * info.Height];
            for (int y = 0; y < info.Height; y++)
            {
                int filter = raw[y * (stride + 1)], s = y * (stride + 1) + 1, d = y * stride;
                if (filter > 4) throw new InvalidOperationException("Invalid PNG row filter.");
                for (int x = 0; x < stride; x++)
                {
                    int left = x >= bpp ? rows[d + x - bpp] : 0, up = y > 0 ? rows[d - stride + x] : 0, corner = x >= bpp && y > 0 ? rows[d - stride + x - bpp] : 0;
                    int predicted = filter == 0 ? 0 : filter == 1 ? left : filter == 2 ? up : filter == 3 ? (left + up) >> 1 : Paeth(left, up, corner);
                    rows[d + x] = (byte)(raw[s + x] + predicted);
                }
            }
            byte Sample(int i) => (byte)((((rows[2 * i] << 8) | rows[2 * i + 1]) + 128) / 257);
            // PNG rows run top to bottom; Unity's pixel arrays run bottom to top.
            var pixels = new Color32[info.Width * info.Height];
            for (int y = 0; y < info.Height; y++)
                for (int x = 0; x < info.Width; x++)
                {
                    int i = (y * info.Width + x) * channels;
                    var c = channels == 1 ? new Color32(Sample(i), Sample(i), Sample(i), 255)
                        : channels == 2 ? new Color32(Sample(i), Sample(i), Sample(i), Sample(i + 1))
                        : channels == 3 ? new Color32(Sample(i), Sample(i + 1), Sample(i + 2), 255)
                        : new Color32(Sample(i), Sample(i + 1), Sample(i + 2), Sample(i + 3));
                    pixels[(info.Height - 1 - y) * info.Width + x] = c;
                }
            return pixels;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        // For a 16-bit PNG source: whether an 8-bit copy rounded as Decode16 reads it, imported with the source's
        // settings, gives exactly the texture data Unity builds from the source on the active platform. Only then does
        // anything derived from the rounded pixels match the original. The temporary copy lives in folder for the call.
        private static readonly System.Collections.Generic.Dictionary<string, bool> roundedMatches = new System.Collections.Generic.Dictionary<string, bool>();

        internal static bool RoundedImportMatches(Texture2D source, string folder)
        {
            string path = AssetDatabase.GetAssetPath(source);
            if (!SourceImages.IsPng(path)) return true; // Flattened sources (PSD, TGA...) are read by Unity at 8 bits.
            var bytes = File.ReadAllBytes(LongPath.For(path));
            var info = Inspect(bytes);
            if (info.BitDepth != 16) return true;
            string key = FingerprintService.Hash(bytes) + "|" + FingerprintService.ImporterHash(source) + "|" + EditorUserBuildSettings.activeBuildTarget;
            if (roundedMatches.TryGetValue(key, out bool cached)) return cached;
            string meta = GenerationCoordinator.CopiedImporterMeta(path + ".meta", "AvatarTextureOptimizer:rounding");
            if (meta == null) return roundedMatches[key] = false;
            string temp = folder + "/" + SourceImages.TempPrefix + Guid.NewGuid().ToString("N") + ".png";
            try
            {
                File.WriteAllBytes(temp, Encode(Decode(bytes, out _), info));
                File.WriteAllText(temp + ".meta", meta, new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(temp, ImportAssetOptions.ForceSynchronousImport);
                var copy = AssetDatabase.LoadAssetAtPath<Texture2D>(temp);
                bool match = copy && copy.format == source.format && copy.width == source.width && copy.height == source.height &&
                    copy.mipmapCount == source.mipmapCount && copy.GetRawTextureData().SequenceEqual(source.GetRawTextureData());
                return roundedMatches[key] = match;
            }
            finally
            {
                if (File.Exists(temp) || File.Exists(temp + ".meta")) AssetDatabase.DeleteAsset(temp);
                if (File.Exists(temp)) File.Delete(temp);
                if (File.Exists(temp + ".meta")) File.Delete(temp + ".meta");
            }
        }

        public static byte[] Encode(Color32[] pixels, PngInfo info)
        {
            var texture = new Texture2D(info.Width, info.Height, info.Alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
            try
            {
                texture.SetPixels32(pixels); texture.Apply(false, false);
                byte[] png = ImageConversion.EncodeToPNG(texture);
                return info.ColourMetadata.Count == 0 ? png : WithColourMetadata(png, info.ColourMetadata);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
    internal static class BackgroundValueDetector
    {
        // Sample the UV coverage before padding, so authored grey padding cannot dominate the fill.
        public static Color32? DetectUsed(Color32[] pixels, bool[] sampled) =>
            sampled.Any(used => used) ? MostCommon(pixels, i => sampled[i]) : (Color32?)null;

        // Most common cleared colour.
        public static Color32 Detect(Color32[] pixels, bool[] preserved)
        {
            if (preserved.Count(used => !used) < RetainedException.MinimumUnusedTexels)
                throw new RetainedException("Fewer than 256 unused texels remain after padding; no useful output.");
            return MostCommon(pixels, i => !preserved[i]);
        }

        // Most common colour among protected texels that touch a cleared texel (4-neighbour), i.e. the
        // island edges. Null when no protected texel touches a cleared one.
        public static Color32? DetectEdge(Color32[] pixels, bool[] preserved, int width, int height)
        {
            bool Edge(int i)
            {
                if (!preserved[i]) return false;
                int x = i % width, y = i / width;
                return (x > 0 && !preserved[i - 1]) || (x < width - 1 && !preserved[i + 1]) ||
                       (y > 0 && !preserved[i - width]) || (y < height - 1 && !preserved[i + width]);
            }
            for (int i = 0; i < pixels.Length; i++)
                if (Edge(i)) return MostCommon(pixels, Edge);
            return null;
        }

        private static Color32 MostCommon(Color32[] pixels, Func<int, bool> include)
        {
            // Quantize RGB only; keep exact alpha and choose an existing RGBA tuple, not independent channel averages.
            var counts = new int[1 << 20];
            var representatives = new uint[1 << 20];
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!include(i)) continue;
                var c = pixels[i];
                int bin = (c.a << 12) | ((c.r >> 4) << 8) | ((c.g >> 4) << 4) | (c.b >> 4);
                uint packed = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
                if (counts[bin] == 0 || packed < representatives[bin]) representatives[bin] = packed;
                counts[bin]++;
            }
            int winner = 0;
            for (int i = 1; i < counts.Length; i++)
                if (counts[i] > counts[winner] || (counts[i] == counts[winner] && representatives[i] < representatives[winner])) winner = i;
            uint value = representatives[winner];
            return new Color32((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
    }
}
