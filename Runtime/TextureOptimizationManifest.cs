using System;
using System.Collections.Generic;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer
{
    public sealed class TextureOptimizationManifest : ScriptableObject
    {
        public int schemaVersion = 1;
        public string cacheOwner;
        public List<GeneratedTextureMapping> mappings = new List<GeneratedTextureMapping>();
        public List<UnchangedTextureAnalysis> unchangedTextures = new List<UnchangedTextureAnalysis>();
        public List<NonBeneficialTextureAnalysis> nonBeneficialTextures = new List<NonBeneficialTextureAnalysis>();
    }

    [Serializable]
    public sealed class UnchangedTextureAnalysis
    {
        public Texture2D source;
        public string sourceId;
        public string recipeHash;
        public int lastUsedDay; // UTC days since 1970 of the last build that used this entry; 0 = unknown.
        // Set when the texture was retained for a reason fixed by its recipe (see RetainedException); empty for "no pixel changes".
        public string retainedReason;
        // Whether the compressed size could be estimated when this was recorded. A "no pixel changes" found without it (Play Mode,
        // or a record older than this field) may have missed a smaller format alone, so an upload that can estimate checks it again.
        public bool estimated;
    }

    [Serializable]
    public sealed class NonBeneficialTextureAnalysis
    {
        public Texture2D source;
        public string sourceId;
        public string recipeHash;
        public int lastUsedDay; // UTC days since 1970 of the last build that used this entry; 0 = unknown.
        public long sourceBytes;
        public long candidateBytes;
        // True when the two sizes are estimated compressed bundle bytes, not PNG file sizes.
        public bool compressedEstimate;
    }

    [Serializable]
    public sealed class GeneratedTextureMapping
    {
        public Texture2D source;
        public Texture2D replacement;
        public string sourceId;
        public string recipeHash;
        public int lastUsedDay; // UTC days since 1970 of the last build that used this entry; 0 = unknown.
        public string outputHash;
        public string outputImporterHash;
        public int width;
        public int height;
        public int padding; // Radius in imported Unity pixels, before conversion to source PNG texels.
        public int preservedPixels;
        public bool repairedPadding;
        // False for mappings created before report metadata was saved; their default colour/radii are unknown.
        public bool hasReportMetadata;
        public int extensionRadiusX;
        public int extensionRadiusY;
        public Color32 background;
        public bool standaloneValidated;
        public bool androidValidated;
        // Estimated compressed bundle bytes of the source and replacement texture when generated (active build
        // target); 0 for mappings made under the PNG file-size rule, whose validity is still judged by that rule.
        public long compressedSourceBytes;
        public long compressedReplacementBytes;
        // Standalone format the replacement was given because its shaders never read the dropped channels
        // (UnityEditor.TextureImporterFormat value); 0 when it keeps the source's import settings unchanged.
        public int standaloneFormat;
    }
}
