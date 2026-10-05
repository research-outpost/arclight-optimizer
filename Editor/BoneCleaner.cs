using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Bones and PhysBones that change nothing anyone can see.
    //  - Zero-weight bones: a skinned renderer's bone that no vertex weights above zero contributes nothing to any
    //    vertex, so it leaves the renderer's bone list (and its bind pose); zero weight entries are dropped and the
    //    rest renumbered. Only for meshes no other component shares.
    //  - Neither step runs with d4rk Avatar Optimizer, whose merge runs after Arclight: a pruned bone list or a removed
    //    PhysBone changes which bones it merges and so its rounding (measured on Chocofuyu Medium: up to 102/255 on a few
    //    silhouette pixels). d4rk removes unweighted bones itself.
    //  - Unobserved PhysBones: a PhysBone only moves the transforms below its root. When none of them carries a
    //    component (renderer, collider, contact, constraint, another PhysBone...), none is referenced by anything but
    //    the PhysBone itself (a skinned renderer only references bones it weights, after the step above), and the
    //    PhysBone reports no parameter to the animator, the motion is invisible and the PhysBone goes.
    //  - Unobserved constraints: the same rule for what a constraint moves (its target and everything below it), plus: no
    //    humanoid bone, nothing inside a PhysBone chain, nothing referencing the constraint, and at least one weighted
    //    source (constraints with no weighted source are left alone; what they do is not proven).
    // The objects left unused are then swept by the unused object pass. Nothing changes when the avatar's animation
    // cannot be read.
    internal static class BoneCleaner
    {
        internal sealed class Result { public int Bones, PhysBones, Constraints; }

        internal static Result Run(AvatarAnalysis analysis, Action<Object, Object> register, bool physBones = true)
        {
            var result = new Result();
            if (!analysis.Complete) return result;
            var root = analysis.Root;
            bool d4rk = root.GetComponentsInChildren<Component>(true).Any(c => c && c.GetType().Name == "d4rkAvatarOptimizer");
            physBones &= !d4rk; // Removing them changes which bones d4rk merges, and so its rounding (Chocofuyu: one pixel at 96/255).
            var users = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh && !d4rk).GroupBy(r => r.sharedMesh)
                .Where(g => !g.Any(Exclusions.Excluded));
            var swaps = new HashSet<Mesh>(analysis.AnimatedObjectValues.OfType<Mesh>());
            foreach (var group in users)
            {
                var mesh = group.Key;
                if (group.Count() != 1 || swaps.Contains(mesh) || analysis.ReferencesTo(mesh).Any(c => !(c is SkinnedMeshRenderer))) continue;
                var renderer = group.First();
                if (analysis.IsAnimated(renderer, p => p == "m_Mesh") || renderer.GetComponent<Cloth>()) continue;
                int removed = Prune(renderer, register);
                result.Bones += removed;
            }
            if (result.Bones > 0) analysis.RescanReferences();

            // physBones: false while VRCFury has not built yet, as it may still add readers of a PhysBone's parameter.
            foreach (var physBone in root.GetComponentsInChildren<Component>(true).Where(c => physBones && c && c.GetType().Name == "VRCPhysBone" && !Exclusions.Excluded(c)).ToList())
            {
                if (Observed(physBone, analysis)) continue;
                UnusedObjectRemover.KeepIgnoredByOthers(root, physBone);
                Object.DestroyImmediate(physBone);
                result.PhysBones++;
            }
            if (result.PhysBones > 0) analysis.RescanReferences();

            // The same rule for constraints: what a constraint moves is its target and everything below it.
            var animator = root.GetComponent<Animator>();
            var humanoid = new HashSet<Transform>();
            if (animator && animator.isHuman)
                for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                    if (animator.GetBoneTransform(bone) is Transform t && t) humanoid.Add(t);
            var chainRoots = root.GetComponentsInChildren<Component>(true).Where(c => c && c.GetType().Name == "VRCPhysBone").Select(UnusedObjectRemover.PhysBoneRoot).ToList();
            foreach (var constraint in root.GetComponentsInChildren<Component>(true).Where(c => c && IsConstraint(c) && !Exclusions.Excluded(c)).ToList())
            {
                var target = Target(constraint);
                if (!target || target == root.transform || !Weighted(constraint)) continue; // Unweighted constraints are left alone (not proven).
                if (analysis.ReferencesTo(constraint).Any(c => c != constraint)) continue;
                if (chainRoots.Any(r => target.IsChildOf(r))) continue; // A PhysBone chain reads its transforms.
                bool observed = false;
                foreach (var t in target.GetComponentsInChildren<Transform>(true))
                {
                    if (humanoid.Contains(t) || t.GetComponents<Component>().Any(c => c && !(c is Transform) && c != constraint) ||
                        analysis.ReferencesTo(t).Any(c => c != constraint) || analysis.ReferencesTo(t.gameObject).Any(c => c != constraint && c.gameObject != t.gameObject))
                    { observed = true; break; }
                }
                if (observed) continue;
                Object.DestroyImmediate(constraint);
                result.Constraints++;
            }
            if (result.Constraints > 0) analysis.RescanReferences();
            return result;
        }

        private static bool IsConstraint(Component c) => c is UnityEngine.Animations.IConstraint ||
            c.GetType().Name.StartsWith("VRC", StringComparison.Ordinal) && c.GetType().Name.EndsWith("Constraint", StringComparison.Ordinal);

        private static Transform Target(Component constraint)
        {
            if (constraint is UnityEngine.Animations.IConstraint) return constraint.transform;
            using (var serialized = new SerializedObject(constraint))
                return serialized.FindProperty("TargetTransform")?.objectReferenceValue is Transform t && t ? t : constraint.transform;
        }

        // Whether any source has a weight above zero (and the constraint's own weight is above zero). Unknown layouts count as
        // unweighted, which leaves the constraint alone.
        private static bool Weighted(Component constraint)
        {
            if (constraint is UnityEngine.Animations.IConstraint unity)
                return unity.weight > 0 && Enumerable.Range(0, unity.sourceCount).Any(i => unity.GetSource(i).weight > 0 && unity.GetSource(i).sourceTransform);
            using (var serialized = new SerializedObject(constraint))
            {
                if (!(serialized.FindProperty("GlobalWeight") is SerializedProperty global) || global.floatValue <= 0) return false;
                var it = serialized.GetIterator();
                while (it.Next(true))
                    if (it.propertyPath.StartsWith("Sources", StringComparison.Ordinal) && it.name == "Weight" && it.propertyType == SerializedPropertyType.Float && it.floatValue > 0)
                        return true;
            }
            return false;
        }

        // Removes bones no vertex weights above zero; returns how many.
        internal static int Prune(SkinnedMeshRenderer renderer, Action<Object, Object> register)
        {
            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            if (bones.Length == 0 || bindposes.Length != bones.Length) return 0;
            var perVertex = mesh.GetBonesPerVertex().ToArray();
            var weights = mesh.GetAllBoneWeights().ToArray();
            var used = new bool[bones.Length];
            foreach (var w in weights) if (w.weight > 0 && w.boneIndex < used.Length) used[w.boneIndex] = true;
            if (used.All(u => u)) return 0;
            // A vertex weighted to nothing keeps its entries as they are: leave such meshes alone.
            for (int v = 0, w = 0; v < perVertex.Length; w += perVertex[v], v++)
                if (!Enumerable.Range(w, perVertex[v]).Any(i => weights[i].weight > 0)) return 0;
            var remap = new int[bones.Length];
            int next = 0;
            for (int b = 0; b < bones.Length; b++) remap[b] = used[b] ? next++ : -1;

            var newPerVertex = new byte[perVertex.Length];
            var newWeights = new List<BoneWeight1>(weights.Length);
            for (int v = 0, w = 0; v < perVertex.Length; v++)
            {
                byte count = 0;
                for (int i = 0; i < perVertex[v]; i++, w++)
                    if (weights[w].weight > 0) { newWeights.Add(new BoneWeight1 { boneIndex = remap[weights[w].boneIndex], weight = weights[w].weight }); count++; }
                newPerVertex[v] = count;
            }
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            using (var a = new Unity.Collections.NativeArray<byte>(newPerVertex, Unity.Collections.Allocator.Temp))
            using (var b = new Unity.Collections.NativeArray<BoneWeight1>(newWeights.ToArray(), Unity.Collections.Allocator.Temp))
                copy.SetBoneWeights(a, b);
            copy.bindposes = Enumerable.Range(0, bones.Length).Where(i => used[i]).Select(i => bindposes[i]).ToArray();
            copy.bounds = mesh.bounds;
            register(mesh, copy);
            SkinnedMeshMerger.Rebind(renderer, () =>
            {
                renderer.bones = Enumerable.Range(0, bones.Length).Where(i => used[i]).Select(i => bones[i]).ToArray();
                renderer.sharedMesh = copy;
            });
            return used.Count(u => !u);
        }

        private static bool Observed(Component physBone, AvatarAnalysis analysis)
        {
            using (var serialized = new SerializedObject(physBone))
                if (!string.IsNullOrEmpty(serialized.FindProperty("parameter")?.stringValue)) return true;
            var start = UnusedObjectRemover.PhysBoneRoot(physBone);
            foreach (var t in start.GetComponentsInChildren<Transform>(true))
            {
                if (t == start) continue;
                if (t.GetComponents<Component>().Any(c => c && !(c is Transform) && c != physBone)) return true;
                if (analysis.ReferencesTo(t).Any(c => c != physBone)) return true;
                if (analysis.ReferencesTo(t.gameObject).Any(c => c != physBone && c.gameObject != t.gameObject)) return true;
            }
            return false;
        }
    }
}
