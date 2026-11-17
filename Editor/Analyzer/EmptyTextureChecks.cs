using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ResearchOutpost.AvatarTextureOptimizer.Editor.Analyzer
{
    // A texture big enough to matter whose texels are almost all fully transparent. A decal or cutout like that draws
    // next to nothing wherever the shader uses its alpha, and still costs its full size in texture memory. No build pass
    // can shrink one — a decal maps its whole texture inside the region its placement covers, so cropping has no unused
    // texel to take — so the honest output is a card that says what it costs and what to do by hand.
    // Read from the decoded PNG rather than the imported texture: the import may be compressed already, and a palette
    // image's alpha only shows once decoded.
    internal static partial class AvatarAnalyzer
    {
        private const int EmptySizeCandidate = 1024;
        private const double EmptyTransparentShare = 0.999;
        // Below this the texels are not worth a card even when the image is empty (a 2048 px title card holding 98 KiB of
        // mostly-transparent text next to a 5.3 MiB decal only dilutes it): the card is about memory somebody would miss.
        private const long EmptyMemoryFloor = 512 * 1024;

        private static void EmptyTextureChecks(Avatar avatar, List<Finding> findings)
        {
            var seen = new HashSet<Texture2D>();
            var empty = new List<(Texture2D Texture, double Share, int Size, string Content)>();
            var textures = avatar.Root.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is SkinnedMeshRenderer || r is MeshRenderer) && !EditorOnly(r.transform, avatar.Root.transform))
                .SelectMany(r => r.sharedMaterials).Where(m => m)
                .SelectMany(m => m.GetTexturePropertyNames().Select(m.GetTexture)).OfType<Texture2D>();
            foreach (var texture in textures)
            {
                if (!texture || !seen.Add(texture)) continue;
                int size = Mathf.Max(texture.width, texture.height);
                if (size < EmptySizeCandidate || CostHints.Bytes(texture) < EmptyMemoryFloor) continue;
                double share = TransparentShare(texture, out string content);
                if (share < EmptyTransparentShare) continue;
                empty.Add((texture, share, size, content));
            }
            if (empty.Count == 0) return;

            empty = empty.OrderByDescending(entry => CostHints.Bytes(entry.Texture)).ToList();
            long bytes = empty.Sum(entry => CostHints.Bytes(entry.Texture));
            findings.Add(new Finding
            {
                Severity = Severity.TidyUp, Key = "emptytexture|", Target = empty[0].Texture,
                Title = N(empty.Count, "texture") + (empty.Count == 1 ? " is" : " are") + " almost entirely transparent",
                Detail = N(empty.Count, "texture") + " at " + EmptySizeCandidate + " px or more " +
                    (empty.Count == 1 ? "is" : "are") + " at least 99.9% fully transparent: wherever a shader uses that alpha " +
                    "(a decal, a mask, a cutout) almost nothing is drawn, yet the texels cost their full size in texture memory. " +
                    "No build pass can shrink one, because a decal maps its whole texture inside the region its placement covers. " +
                    "Texture memory: " + Bytes(bytes) + "." + "\n" +
                    Bullets(empty.Select(entry => entry.Texture.name + " (" + entry.Size + " px, " + Percent(entry.Share) +
                        " transparent, " + Bytes(CostHints.Bytes(entry.Texture)) + ")" +
                        (string.IsNullOrEmpty(entry.Content) ? "" : "; " + entry.Content))),
                Fix = "If the decal or mask is not needed, clear its texture slot. If it is, a texture whose whole content is the " +
                    "square named above can be replaced by a copy of exactly that square, with the decal position set to the place " +
                    "and its scale to the size the line gives: it draws the same for a fraction of the memory. Otherwise lower that " +
                    "texture's Max Size in its import settings, and Texture Size Cap does the same for every texture on the avatar.",
                Identity = string.Join("\n", empty.Select(entry => entry.Texture.name + "|" + entry.Size + "|" + Percent(entry.Share)))
            });
        }

        // Percentage of texels whose alpha is exactly zero, or -1 when the file cannot be read as a PNG here (not an
        // asset, no alpha channel, a header this reader does not handle). A source with no alpha is opaque by definition.
        private static double TransparentShare(Texture2D texture, out string content)
        {
            content = null;
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(LongPath.For(path))) return -1;
            try
            {
                // Decoded rather than gated on the header's alpha flag: a palette PNG carries its transparency in a tRNS
                // chunk, so the header says "no alpha" for a decal that is almost entirely see-through (LowPoly's error.png).
                var pixels = PngPixels.Decode(File.ReadAllBytes(LongPath.For(path)), out var info);
                if (pixels == null || pixels.Length == 0) return -1;
                long transparent = pixels.LongCount(pixel => pixel.a == 0);
                content = Content(pixels, info.Width, info.Height);
                return (double)transparent / pixels.Length;
            }
            catch (Exception) { return -1; }
        }

        // What the visible texels are, when they are one colour: the square to use instead of the sheet, its colour, the
        // decal position that places it (the square's centre) and the scale that sizes it (its share of the sheet). Empty
        // when there is nothing visible or the content is more than one colour, where no such copy exists.
        private static string Content(Color32[] pixels, int width, int height)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var colours = new HashSet<Color32>();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a == 0) continue;
                int x = i % width, y = i / width;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (colours.Count < 8) colours.Add(pixels[i]);
            }
            if (maxX < minX || colours.Count > 1) return null;
            int squareWidth = maxX - minX + 1, squareHeight = maxY - minY + 1;
            var colour = colours.First();
            string where = "at (" + minX + "," + minY + "), " + "place " +
                Number((minX + squareWidth / 2f) / width, 4) + ", " + Number((minY + squareHeight / 2f) / height, 4) +
                ", scale " + Number((float)squareWidth / width, 4);
            return "content " + squareWidth + "x" + squareHeight + " of RGBA(" + colour.r + "," + colour.g + "," + colour.b + "," + colour.a +
                ") " + where;
        }

        private static string Number(float value, int decimals) =>
            value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);

        private static string Percent(double share) => (share * 100.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
