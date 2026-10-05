using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Runs on the build avatar among the avatar-wide passes. Object references on the avatar's components are rewritten
    // through one replacement map. A reference this does not rewrite (an animation curve, an animator behaviour)
    // keeps its own identical original, so the avatar never looks or sounds different; it only saves less.
    //  - Duplicate meshes: identical vertex and index buffers, vertex layout, submeshes, bounds, bind poses,
    //    bone weights and blend shapes. The first mesh found keeps every use.
    //  - Duplicate audio clips: byte-identical source files with identical import settings (the .meta file
    //    without its GUID, user data and asset-bundle lines), which import to identical audio.
    //  - Index compaction: a mesh whose 32-bit indices all fit in 16 bits is replaced by a copy with a 16-bit
    //    index buffer and the same submeshes. Its triangles are compared with the original before it is used.
    // Source assets are never edited; copies live in memory and NDMF saves them with the build.
    internal static class MeshAndAudioOptimizer
    {
        internal sealed class Result
        {
            public int Meshes, AudioClips, Compacted, MonoAudio, Stripped, FrozenShapes, MergedSkinned, MergedInto;
            // Mesh bytes are uncompressed vertex and index data; audio bytes are estimated bundle sizes.
            public long MeshBytes, AudioBytes;
            public int AudioUnmeasured;
            public bool Changed => Meshes + AudioClips + Compacted + MonoAudio + Stripped + FrozenShapes + MergedSkinned > 0;
            public override string ToString() =>
                $"merged {Meshes} duplicate mesh(es) and {AudioClips} duplicate audio clip(s), gave {Compacted} mesh(es) a 16-bit index buffer, " +
                $"made {MonoAudio} identical-channel stereo clip(s) mono (+3 dB), removed unused vertex channels from {Stripped} mesh(es), baked or removed {FrozenShapes} blend shape(s), merged {MergedSkinned} skinned mesh(es) into {MergedInto}";
        }

        // monoAudio: convert qualifying identical-channel stereo clips. Callers pass false when animation can change
        // an AudioSource's or VRC Spatial Audio Source's playback settings, since sources are judged on stored values.
        internal static Result Run(GameObject root, bool merge, bool compact, Action<UnityEngine.Object, UnityEngine.Object> register,
            bool monoAudio = false)
        {
            var result = new Result();
            // Components under an Arclight Exclude keep their own meshes and clips (identical copies stay identical), and a mesh
            // or clip they share with other components is left alone for those too.
            var all = root.GetComponentsInChildren<Component>(true).Where(c => c && !(c is Transform)).ToArray();
            var components = all.Where(c => !Exclusions.Excluded(c)).ToArray();
            var excluded = new HashSet<UnityEngine.Object>(References(all.Where(Exclusions.Excluded).ToArray()));
            var references = References(components).Where(r => !excluded.Contains(r)).ToList();
            var replace = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            if (merge)
            {
                result.Meshes = MergeIdentical(references.OfType<Mesh>(), MeshSignature, replace);
                result.AudioClips = MergeIdentical(references.OfType<AudioClip>(), AudioSignature, replace);
                foreach (var dropped in replace.Keys)
                    if (dropped is Mesh mesh) result.MeshBytes += MeshBytes(mesh);
                    else if (dropped is AudioClip clip)
                    {
                        if (TryMeasure(clip, out long bytes)) result.AudioBytes += bytes;
                        else result.AudioUnmeasured++;
                    }
            }
            if (compact)
                foreach (var mesh in references.OfType<Mesh>().Where(m => !replace.ContainsKey(m)).ToArray())
                {
                    var copy = Compact(mesh);
                    if (!copy) continue;
                    result.MeshBytes += 2 * IndexCount(mesh);
                    // Duplicates already pointed at this mesh follow it to the compact copy.
                    foreach (var key in replace.Where(p => p.Value == mesh).Select(p => p.Key).ToArray()) replace[key] = copy;
                    replace.Add(mesh, copy);
                    result.Compacted++;
                }
            if (replace.Count > 0)
            {
                foreach (var pair in replace) register(pair.Key, pair.Value);
                Rewrite(components, replace);
            }
            if (monoAudio)
            {
                // Judged after merging, on the clips the components now hold.
                var mono = AudioMonoConverter.Convert(ClipReferences(components).Where(r => !excluded.Contains(r.Item2)).ToList());
                if (mono.Count > 0)
                {
                    foreach (var pair in mono)
                        if (TryMeasure(pair.Key, out long stereo) && TryMeasure(pair.Value, out long single)) result.AudioBytes += stereo - single;
                        else result.AudioUnmeasured++;
                    var monoReplace = mono.ToDictionary(p => (UnityEngine.Object)p.Key, p => (UnityEngine.Object)p.Value);
                    foreach (var pair in monoReplace) register(pair.Key, pair.Value);
                    Rewrite(components, monoReplace);
                    result.MonoAudio = mono.Count;
                }
            }
            return result;
        }

        // Texture streaming picks a texture's mip from its renderer's mesh UV distribution metric, which Unity computes only when it
        // imports a mesh; a mesh built with new Mesh() has none. A mesh built from others gets, per UV channel, the largest metric
        // of its sources (each source keeps its own vertex positions and UVs, or moves only rigidly), so none of their textures is
        // streamed at less detail than before; a mesh with the same triangles gets exactly its source's.
        internal static void KeepUvDensity(Mesh built, IEnumerable<Mesh> sources)
        {
            var list = sources.Where(m => m).ToList();
            using (var serialized = new UnityEditor.SerializedObject(built))
            {
                for (int channel = 0; channel < 2; channel++)
                {
                    var metric = serialized.FindProperty("m_MeshMetrics[" + channel + "]");
                    if (metric != null && list.Count > 0) metric.floatValue = list.Max(m => m.GetUVDistributionMetric(channel));
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // Vertex streams plus index buffer; blend shapes are not counted, so merged meshes report a lower bound.
        internal static long MeshBytes(Mesh mesh)
        {
            long bytes = 0;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++) bytes += (long)mesh.GetVertexBufferStride(stream) * mesh.vertexCount;
            return bytes + IndexCount(mesh) * (mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4);
        }

        private static long IndexCount(Mesh mesh)
        {
            long count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) count += mesh.GetIndexCount(i);
            return count;
        }

        private static bool TryMeasure(AudioClip clip, out long bytes)
        {
            bytes = 0;
            string path = AssetDatabase.GetAssetPath(clip);
            return !string.IsNullOrEmpty(path) && BundleSizeEstimator.TryMeasure(path, out bytes);
        }

        private static List<(Component, AudioClip)> ClipReferences(Component[] components)
        {
            var list = new List<(Component, AudioClip)>();
            foreach (var component in components)
                using (var serialized = new SerializedObject(component))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue is AudioClip clip && clip)
                            list.Add((component, clip));
                }
            return list;
        }

        // Distinct objects referenced by the components, in component order.
        private static List<UnityEngine.Object> References(Component[] components)
        {
            var seen = new HashSet<UnityEngine.Object>();
            var list = new List<UnityEngine.Object>();
            foreach (var component in components)
                using (var serialized = new SerializedObject(component))
                {
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                            iterator.objectReferenceValue && seen.Add(iterator.objectReferenceValue))
                            list.Add(iterator.objectReferenceValue);
                }
            return list;
        }

        private static void Rewrite(Component[] components, Dictionary<UnityEngine.Object, UnityEngine.Object> replace)
        {
            foreach (var component in components)
                using (var serialized = new SerializedObject(component))
                {
                    bool changed = false;
                    var iterator = serialized.GetIterator();
                    while (iterator.Next(true))
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue &&
                            replace.TryGetValue(iterator.objectReferenceValue, out var keeper))
                        {
                            iterator.objectReferenceValue = keeper;
                            changed = true;
                        }
                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
        }

        // Points each object at the first identical one; returns how many were replaced. Null signatures never merge.
        private static int MergeIdentical<T>(IEnumerable<T> items, Func<T, byte[]> signature,
            Dictionary<UnityEngine.Object, UnityEngine.Object> replace) where T : UnityEngine.Object
        {
            var keepers = new Dictionary<string, List<(T Item, byte[] Data)>>(StringComparer.Ordinal);
            int merged = 0;
            foreach (var item in items)
            {
                byte[] data;
                try { data = signature(item); }
                catch (Exception) { continue; }
                if (data == null) continue;
                string hash = FingerprintService.Hash(data);
                if (!keepers.TryGetValue(hash, out var bucket)) keepers.Add(hash, bucket = new List<(T, byte[])>());
                var keeper = bucket.FirstOrDefault(k => k.Data.SequenceEqual(data)).Item;
                if (keeper == null) bucket.Add((item, data));
                else { replace.Add(item, keeper); merged++; }
            }
            return merged;
        }

        internal static byte[] MeshSignature(Mesh mesh)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = array[0];
                writer.Write((int)data.indexFormat); writer.Write(data.vertexCount); writer.Write(data.vertexBufferCount);
                // Texture streaming picks mips from the UV distribution metrics, so meshes that differ only there stay apart.
                using (var serialized = new SerializedObject(mesh))
                    for (int channel = 0; channel < 2; channel++) writer.Write(serialized.FindProperty("m_MeshMetrics[" + channel + "]")?.floatValue ?? -1);
                foreach (var attribute in mesh.GetVertexAttributes())
                {
                    writer.Write((int)attribute.attribute); writer.Write((int)attribute.format);
                    writer.Write(attribute.dimension); writer.Write(attribute.stream);
                }
                for (int s = 0; s < data.vertexBufferCount; s++) writer.Write(data.GetVertexData<byte>(s).ToArray());
                writer.Write(data.GetIndexData<byte>().ToArray());
                writer.Write(data.subMeshCount);
                for (int i = 0; i < data.subMeshCount; i++)
                {
                    var d = data.GetSubMesh(i);
                    writer.Write((int)d.topology); writer.Write(d.indexStart); writer.Write(d.indexCount); writer.Write(d.baseVertex);
                    writer.Write(d.firstVertex); writer.Write(d.vertexCount); Write(writer, d.bounds);
                }
                Write(writer, mesh.bounds);
                var bindposes = mesh.bindposes;
                writer.Write(bindposes.Length);
                foreach (var pose in bindposes) for (int k = 0; k < 16; k++) writer.Write(pose[k]);
                writer.Write(mesh.GetBonesPerVertex().ToArray());
                foreach (var weight in mesh.GetAllBoneWeights()) { writer.Write(weight.boneIndex); writer.Write(weight.weight); }
                writer.Write(mesh.blendShapeCount);
                var deltas = new[] { new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount] };
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    writer.Write(mesh.GetBlendShapeName(shape));
                    int frames = mesh.GetBlendShapeFrameCount(shape);
                    writer.Write(frames);
                    for (int frame = 0; frame < frames; frame++)
                    {
                        writer.Write(mesh.GetBlendShapeFrameWeight(shape, frame));
                        mesh.GetBlendShapeFrameVertices(shape, frame, deltas[0], deltas[1], deltas[2]);
                        foreach (var set in deltas) foreach (var v in set) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                    }
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void Write(BinaryWriter writer, Bounds bounds)
        {
            writer.Write(bounds.center.x); writer.Write(bounds.center.y); writer.Write(bounds.center.z);
            writer.Write(bounds.extents.x); writer.Write(bounds.extents.y); writer.Write(bounds.extents.z);
        }

        // Null for clips that are not imported audio files (generated clips have nothing to compare).
        internal static byte[] AudioSignature(AudioClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsMainAsset(clip) || !(AssetImporter.GetAtPath(path) is AudioImporter) ||
                !File.Exists(LongPath.For(path)) || !File.Exists(LongPath.For(path + ".meta"))) return null;
            var settings = File.ReadAllText(LongPath.For(path + ".meta")).Replace("\r\n", "\n").Split('\n')
                .Where(line => !line.StartsWith("guid:", StringComparison.Ordinal) && !line.StartsWith("  userData:", StringComparison.Ordinal) &&
                               !line.StartsWith("  assetBundleName:", StringComparison.Ordinal) && !line.StartsWith("  assetBundleVariant:", StringComparison.Ordinal));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(string.Join("\n", settings));
                writer.Write(File.ReadAllBytes(LongPath.For(path)));
                writer.Flush();
                return stream.ToArray();
            }
        }

        // A copy with a 16-bit index buffer, or null when the mesh already has one, an index does not fit, or the
        // copy's triangles differ in any way from the original's.
        internal static Mesh Compact(Mesh mesh)
        {
            if (!mesh || mesh.indexFormat != IndexFormat.UInt32) return null;
            ushort[] indices;
            SubMeshDescriptor[] submeshes;
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = array[0];
                var source = data.GetIndexData<uint>();
                indices = new ushort[source.Length];
                for (int i = 0; i < source.Length; i++)
                {
                    if (source[i] >= ushort.MaxValue) return null; // 65535 is the strip-restart value some graphics APIs skip.
                    indices[i] = (ushort)source[i];
                }
                submeshes = Enumerable.Range(0, data.subMeshCount).Select(data.GetSubMesh).ToArray();
            }
            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name;
            const MeshUpdateFlags keep = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontResetBoneBounds |
                MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds;
            copy.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
            copy.SetIndexBufferData(indices, 0, 0, indices.Length, keep);
            copy.subMeshCount = submeshes.Length;
            for (int i = 0; i < submeshes.Length; i++) copy.SetSubMesh(i, submeshes[i], keep);
            copy.bounds = mesh.bounds;
            bool same = copy.indexFormat == IndexFormat.UInt16 && copy.subMeshCount == mesh.subMeshCount && copy.bounds == mesh.bounds;
            for (int i = 0; same && i < submeshes.Length; i++)
            {
                var a = copy.GetSubMesh(i);
                same = a.topology == submeshes[i].topology && a.indexStart == submeshes[i].indexStart && a.indexCount == submeshes[i].indexCount &&
                    a.baseVertex == submeshes[i].baseVertex && a.bounds == submeshes[i].bounds &&
                    copy.GetIndices(i, true).SequenceEqual(mesh.GetIndices(i, true));
            }
            if (same) return copy;
            UnityEngine.Object.DestroyImmediate(copy);
            return null;
        }
    }
}
