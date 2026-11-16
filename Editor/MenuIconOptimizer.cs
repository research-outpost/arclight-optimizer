using UnityEditor;
using UnityEngine;

namespace ResearchOutpost.AvatarTextureOptimizer.Editor
{
    // Expression-menu icons are drawn at a small size in VRChat's menu, but they are imported at their authored
    // resolution (commonly 512-1024 squared, BC3 with mips) and the texture pass never sees them because they are not
    // on a material. This writes a build copy of an icon through CappedCopy: mipmaps are dropped (the menu draws the
    // icon at one size, so no mip is ever sampled), an opaque icon narrows from DXT5 to DXT1, and an optional cap makes
    // Unity's own importer resample it. The source asset is never changed, and a copy is kept only when the measured
    // bundle shrinks, so an icon whose format is already minimal costs nothing.
    internal static class MenuIconOptimizer
    {
        internal static string Folder => AutomaticTextureOptimizer.CacheFolder + "/MenuIcons";
        internal const string Marker = "ArclightIcon:";

        internal static bool IsGenerated(string path) => CappedCopy.IsGenerated(path, Marker);

        // A cached or new build copy of an icon, or null when the icon is not a main texture asset, is already as small
        // as these changes allow, or does not measure smaller. maxSize 0 keeps the authored size.
        internal static Texture2D Resize(Texture2D source, int maxSize) =>
            CappedCopy.Make(source, maxSize, dropMips: true, narrowOpaque: true, Folder, Marker, "icon", allowCacheSource: false, out _, out _);
    }
}
