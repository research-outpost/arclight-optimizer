using System;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    internal static class GeneratedTargetValidator
    {
        public static bool IsStandalone => EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64 ||
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows ||
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneOSX ||
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneLinux64;

        // "Validated" records a successful import/settings check, not pixel-identical compressed mip chains.
        public static bool IsValidated(GeneratedTextureMapping mapping) =>
            EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ? mapping.androidValidated : IsStandalone && mapping.standaloneValidated;

        // standaloneFormat: the mapping's channel format (0 = the source's settings are copied unchanged).
        internal static void ValidatePair(Texture2D source, Texture2D replacement, bool skipPngSizeGate = false, int standaloneFormat = 0)
        {
            if (!IsStandalone && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new InvalidOperationException("Only Standalone and Android targets are supported.");
            if (!skipPngSizeGate) TextureFileSizePolicy.Validate(new System.IO.FileInfo(AssetDatabase.GetAssetPath(source)).Length,
                new System.IO.FileInfo(AssetDatabase.GetAssetPath(replacement)).Length);
            var originalImporter = TextureSafety.Validate(source);
            var outputImporter = TextureSafety.Validate(replacement);
            bool changed = standaloneFormat != 0;
            if (TextureSafety.SettingsFingerprint(originalImporter, changed) != TextureSafety.SettingsFingerprint(outputImporter, changed) ||
                (changed && !ChannelFormats.Matches(originalImporter, outputImporter, (TextureImporterFormat)standaloneFormat)))
                throw new InvalidOperationException("Generated importer settings differ from the source; output retained but not mapped.");
            if (source.width != replacement.width || source.height != replacement.height || source.mipmapCount != replacement.mipmapCount)
                throw new InvalidOperationException("Generated target dimensions/mipmap count differ from the source; output retained but not mapped.");
            if (!PngPixels.RoundedImportMatches(source, System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(replacement)).Replace('\\', '/')))
                throw new InvalidOperationException("This 16-bit source does not compress the same once rounded to 8 bits on this platform; original retained.");
        }

        // Called by generation/cache reuse, including NDMF processing; source importers are never changed.
        public static void ValidateExisting(GeneratedTextureMapping mapping)
        {
            ValidatePair(mapping.source, mapping.replacement, TextureFileSizePolicy.SkipsPngGate(mapping), mapping.standaloneFormat);
            mapping.outputImporterHash = FingerprintService.ImporterHash(mapping.replacement);
            if (IsStandalone) mapping.standaloneValidated = true;
            else mapping.androidValidated = true;
        }
    }
}