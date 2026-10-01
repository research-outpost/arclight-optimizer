using System;
using UnityEditor;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Compatibility inference from a distinctive lilToon property inventory, not shader-source certification.
    // No VRCFury API, package version, generated filename hash or exact source match is required.
    internal sealed class SpsLilToonAdapter : ITextureSamplingAdapter
    {
        private const string Id = "sps-liltoon-inventory-v1";
        private readonly LilToonAdapter lilToon = new LilToonAdapter();

        public bool Matches(Material material) => material.shader &&
            (material.shader.name.StartsWith("Hidden/SPSPatched/", StringComparison.Ordinal) ||
             material.shader.name.StartsWith("Hidden/Locked/SPSPatched/", StringComparison.Ordinal));

        internal static bool HasLilToonInventory(Material material)
        {
            foreach (string property in new[] { "_lilToonVersion", "_MainTex", "_MainTex_ScrollRotate",
                "_UseMain2ndTex", "_Main2ndTex_UVMode", "_EmissionMap_UVMode", "_RimShadeMask", "_ShiftBackfaceUV" })
                if (!material.HasProperty(property)) return false;
            return true;
        }

        public SamplingDescription Describe(Material material, string property)
        {
            if (property.StartsWith("_SPS_", StringComparison.Ordinal))
                return ShaderAdapterRegistry.Unsupported(Id,
                    "SPS internal deformation/baked data is not an ordinary mesh-UV colour texture; original retained.");
            if (!HasLilToonInventory(material))
                return ShaderAdapterRegistry.Unsupported(Id,
                    "SPS shader does not expose the expected lilToon property inventory. Its underlying shader sampling is not supported.");
            // Use the outline model as a conservative superset: alpha coverage includes both
            // main and outline transforms even when the generated variant has no outline pass.
            var basis = AssetDatabase.LoadAssetAtPath<Shader>(LilToonSourceGuard.Root + "/Shader/lts_trans_o.shader");
            if (!basis)
                return ShaderAdapterRegistry.Unsupported(Id, "A lilToon 2.x installation is required for the SPS lilToon sampling model.");
            var result = lilToon.DescribeWithSourceShader(material, property, basis);
            result.AdapterId = Id + "/" + result.AdapterId;
            if (result.Supported)
                result.Reason = "SPS lilToon property inventory recognized; assumes standard lilToon static UV conventions. " +
                    result.Reason;
            return result;
        }
    }
}