using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Models float animation of a use's sampling inputs (e.g. tiling/offset) when every animated value
    // has a bounded range. The adapter is re-evaluated at the corners of the value box, at 0 and at the
    // integers inside each range (adapters branch only on those), and at every cell centre. The sampling
    // structure must be identical everywhere, and each path's transform must be multilinear in the
    // animated values within each cell; the coverage is then the convex hull over the corner transforms.
    internal static class AnimatedSampling
    {
        private const int MaxGridValuesPerInput = 34;
        private const int MaxEvaluations = 4096;
        private const int MaxTransformsPerPath = 256;

        private enum Kind { Float, Int, Vector, TextureScaleOffset }

        private sealed class Input
        {
            internal string Key, Property;
            internal int Component;
            internal Kind Kind;
            internal float[] Values;
        }

        // Returns null and sets expanded when the animation is modeled; otherwise returns the reason.
        internal static string TryExpand(TextureUsageRecord use, AnimationRendererState state, IEnumerable<string> affecting,
            bool allowUnsupportedShaders, out SamplingDescription expanded)
        {
            expanded = null;
            var material = use.Material;
            var affected = new HashSet<string>(affecting, StringComparer.Ordinal);
            var inputs = new List<Input>();
            foreach (var pair in state.FloatRanges.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!affected.Contains(AnimationSnapshot.MaterialPropertyName("material." + pair.Key))) continue;
                var input = Resolve(material, pair.Key);
                if (input == null) continue; // Not a property of this shader: the curve has no effect here.
                var range = pair.Value;
                if (!range.Bounded) return "its values are not bounded by keyframes (additive layer, Direct/2D blend tree or non-finite key)";
                var bounds = new FloatRange();
                bounds.Union(range);
                bounds.Include(Current(material, input));
                if (!bounds.Bounded) return "the current value is not finite";
                input.Values = GridValues(bounds, input.Kind == Kind.Int);
                if (input.Values == null) return "its range spans too many integer thresholds";
                inputs.Add(input);
            }
            if (inputs.Count == 0) return "the animated property could not be resolved on this shader";
            long evaluations = inputs.Aggregate(1L, (n, input) => n * input.Values.Length);
            long cells = inputs.Aggregate(1L, (n, input) => n * Math.Max(1, input.Values.Length - 1));
            if (evaluations + cells > MaxEvaluations) return "too many animated value combinations";

            var baseline = use.Sampling;
            var basePaths = baseline.GetPaths().ToArray();
            var probe = new Material(material);
            try
            {
                if (use.Texture && probe.HasProperty(use.Property)) probe.SetTexture(use.Property, use.Texture);
                var corners = new Vector4[(int)evaluations][];
                var index = new int[inputs.Count];
                // Channels any evaluated value can read; non-integer channel selectors read every channel.
                var channels = baseline.Channels;
                for (long n = 0; n < evaluations; n++)
                {
                    Decompose(n, inputs, index);
                    for (int i = 0; i < inputs.Count; i++) Apply(probe, inputs[i], inputs[i].Values[index[i]]);
                    var described = ShaderAdapterRegistry.Describe(probe, use.Property, allowUnsupportedShaders);
                    channels |= described?.Channels ?? TextureChannels.All;
                    corners[n] = Transforms(described, baseline, basePaths);
                    if (corners[n] == null) return "it changes the sampling model, not only UV transforms";
                }
                string nonlinear = CheckCellCentres(probe, use, inputs, corners, baseline, basePaths, allowUnsupportedShaders);
                if (nonlinear != null) return nonlinear;

                var paths = new List<SamplingPath>();
                for (int p = 0; p < basePaths.Length; p++)
                {
                    var transforms = corners.Select(c => c[p]).Distinct().ToList();
                    if (transforms.Count > MaxTransformsPerPath) return "too many distinct UV transforms";
                    var path = basePaths[p];
                    paths.Add(new SamplingPath
                    {
                        UvChannel = path.UvChannel, Scale = path.Scale, Offset = path.Offset,
                        SamplerTexture = path.SamplerTexture, FixedRepeat = path.FixedRepeat,
                        Label = path.Label + " / animated", AnimatedTransforms = transforms
                    });
                }
                expanded = new SamplingDescription
                {
                    AdapterId = baseline.AdapterId, Supported = baseline.Supported, UnsafeOverride = baseline.UnsafeOverride,
                    OverrideExcluded = baseline.OverrideExcluded, NotSampled = baseline.NotSampled,
                    Reason = baseline.Reason,
                    Semantics = baseline.Semantics, Channels = channels, MaterialInputs = baseline.MaterialInputs, Paths = paths
                };
                return null;
            }
            finally { UnityEngine.Object.DestroyImmediate(probe); }
        }

        private static string CheckCellCentres(Material probe, TextureUsageRecord use, List<Input> inputs, Vector4[][] corners,
            SamplingDescription baseline, SamplingPath[] basePaths, bool allowUnsupportedShaders)
        {
            var varying = Enumerable.Range(0, inputs.Count).Where(i => inputs[i].Values.Length > 1).ToArray();
            if (varying.Length == 0) return null;
            long cells = varying.Aggregate(1L, (n, i) => n * (inputs[i].Values.Length - 1));
            var cell = new int[inputs.Count];
            var corner = new int[inputs.Count];
            for (long c = 0; c < cells; c++)
            {
                long rest = c;
                foreach (int i in varying)
                {
                    int count = inputs[i].Values.Length - 1;
                    cell[i] = (int)(rest % count);
                    rest /= count;
                }
                for (int i = 0; i < inputs.Count; i++)
                {
                    var values = inputs[i].Values;
                    Apply(probe, inputs[i], values.Length > 1 ? (values[cell[i]] + values[cell[i] + 1]) * 0.5f : values[0]);
                }
                var centre = Transforms(ShaderAdapterRegistry.Describe(probe, use.Property, allowUnsupportedShaders), baseline, basePaths);
                if (centre == null) return "it changes the sampling model between keyframe values";
                // A multilinear transform equals the average of its cell's corners at the centre.
                var average = new Vector4[basePaths.Length];
                int cornerCount = 1 << varying.Length;
                for (int mask = 0; mask < cornerCount; mask++)
                {
                    Array.Clear(corner, 0, corner.Length);
                    for (int bit = 0; bit < varying.Length; bit++)
                        corner[varying[bit]] = cell[varying[bit]] + ((mask >> bit) & 1);
                    var transforms = corners[Compose(corner, inputs)];
                    for (int p = 0; p < average.Length; p++) average[p] += transforms[p] / cornerCount;
                }
                for (int p = 0; p < average.Length; p++)
                    for (int k = 0; k < 4; k++)
                        if (Math.Abs(centre[p][k] - average[p][k]) > 1e-4f * Math.Max(1f, Math.Abs(average[p][k])))
                            return "its UV transform is not linear in the animated values";
            }
            return null;
        }

        // Per-path (scale.xy, offset.zw), or null when the description's structure differs from the baseline.
        private static Vector4[] Transforms(SamplingDescription description, SamplingDescription baseline, SamplingPath[] basePaths)
        {
            if (description == null || description.Supported != baseline.Supported || description.NotSampled != baseline.NotSampled ||
                description.UnsafeOverride != baseline.UnsafeOverride || description.OverrideExcluded != baseline.OverrideExcluded ||
                description.Semantics != baseline.Semantics || description.AdapterId != baseline.AdapterId) return null;
            var paths = description.GetPaths().ToArray();
            if (paths.Length != basePaths.Length) return null;
            var result = new Vector4[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                var a = paths[i];
                var b = basePaths[i];
                if (a.UvChannel != b.UvChannel || a.FixedRepeat != b.FixedRepeat || a.SamplerTexture != b.SamplerTexture ||
                    a.AnimatedTransforms != null) return null;
                result[i] = new Vector4(a.Scale.x, a.Scale.y, a.Offset.x, a.Offset.y);
                if (float.IsNaN(result[i].x + result[i].y + result[i].z + result[i].w) ||
                    float.IsInfinity(result[i].x + result[i].y + result[i].z + result[i].w)) return null;
            }
            return result;
        }

        private static float[] GridValues(FloatRange range, bool integers)
        {
            var values = new SortedSet<float> { range.Min, range.Max };
            if (range.Min < 0 && range.Max > 0) values.Add(0);
            double first = Math.Floor(range.Min) + 1, last = Math.Ceiling(range.Max) - 1;
            if (last - first + 1 > MaxGridValuesPerInput - 3) return null;
            for (double v = first; v <= last; v++) values.Add((float)v);
            if (integers)
            {
                // Integer properties only take whole values: use every integer the range can round to.
                values = new SortedSet<float>(values.Select(v => (float)Math.Round(v)));
                values.Add((float)Math.Floor(range.Min)); values.Add((float)Math.Ceiling(range.Max));
            }
            return values.ToArray();
        }

        private static Input Resolve(Material material, string key)
        {
            string property = key;
            int component = -1;
            int dot = key.LastIndexOf('.');
            if (dot > 0 && key.Length - dot == 2 && "xyzwrgba".IndexOf(key[dot + 1]) >= 0)
            {
                component = "xyzwrgba".IndexOf(key[dot + 1]) % 4;
                property = key.Substring(0, dot);
            }
            var shader = material.shader;
            if (component >= 0 && property.EndsWith("_ST", StringComparison.Ordinal))
            {
                int texture = shader.FindPropertyIndex(property.Substring(0, property.Length - 3));
                if (texture >= 0 && shader.GetPropertyType(texture) == ShaderPropertyType.Texture)
                    return new Input { Key = key, Property = property.Substring(0, property.Length - 3), Component = component, Kind = Kind.TextureScaleOffset };
            }
            int index = shader.FindPropertyIndex(property);
            if (index < 0) return null;
            switch (shader.GetPropertyType(index))
            {
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return component < 0 ? new Input { Key = key, Property = property, Kind = Kind.Float } : null;
                case ShaderPropertyType.Int:
                    return component < 0 ? new Input { Key = key, Property = property, Kind = Kind.Int } : null;
                case ShaderPropertyType.Color:
                case ShaderPropertyType.Vector:
                    return component >= 0 ? new Input { Key = key, Property = property, Component = component, Kind = Kind.Vector } : null;
                default:
                    return null;
            }
        }

        private static float Current(Material material, Input input)
        {
            switch (input.Kind)
            {
                case Kind.Float: return material.GetFloat(input.Property);
                case Kind.Int: return material.GetInteger(input.Property);
                case Kind.Vector: return material.GetVector(input.Property)[input.Component];
                default:
                    return input.Component < 2 ? material.GetTextureScale(input.Property)[input.Component]
                        : material.GetTextureOffset(input.Property)[input.Component - 2];
            }
        }

        private static void Apply(Material material, Input input, float value)
        {
            switch (input.Kind)
            {
                case Kind.Float: material.SetFloat(input.Property, value); break;
                case Kind.Int: material.SetInteger(input.Property, Mathf.RoundToInt(value)); break;
                case Kind.Vector:
                    var vector = material.GetVector(input.Property);
                    vector[input.Component] = value;
                    material.SetVector(input.Property, vector);
                    break;
                default:
                    if (input.Component < 2)
                    {
                        var scale = material.GetTextureScale(input.Property);
                        scale[input.Component] = value;
                        material.SetTextureScale(input.Property, scale);
                    }
                    else
                    {
                        var offset = material.GetTextureOffset(input.Property);
                        offset[input.Component - 2] = value;
                        material.SetTextureOffset(input.Property, offset);
                    }
                    break;
            }
        }

        private static void Decompose(long n, List<Input> inputs, int[] index)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                index[i] = (int)(n % inputs[i].Values.Length);
                n /= inputs[i].Values.Length;
            }
        }

        private static long Compose(int[] index, List<Input> inputs)
        {
            long n = 0, stride = 1;
            for (int i = 0; i < inputs.Count; i++)
            {
                n += index[i] * stride;
                stride *= inputs[i].Values.Length;
            }
            return n;
        }
    }
}
