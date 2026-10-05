using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

[assembly: ExportsPlugin(typeof(Okarin.AvatarTextureOptimizer.Editor.PhysBoneSplitPlugin))]

namespace Okarin.AvatarTextureOptimizer.Editor
{
    public sealed class PhysBoneSplitPlugin : Plugin<PhysBoneSplitPlugin>
    {
        public override string QualifiedName => "dev.okarin.arclight-physbone-split";
        public override string DisplayName => "Arclight PhysBone Split";

        protected override void Configure()
        {
            // After Avatar Optimizer: its Trace and Optimize merges PhysBones with equal settings, which would undo a
            // split, and it may remove PhysBones or bones. The texture pass (which records the request) runs before AAO.
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("dev.okarin.avatar-texture-optimizer")
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .Run("Split PhysBones across solver batches", ctx =>
                {
                    if (!PhysBoneSplitRequests.Take(ctx.AvatarRootObject)) return;
                    PhysBoneSplitPass.Run(ctx.AvatarRootObject);
                });
        }
    }

    // Splits eligible PhysBones by whole direct branches on the build copy. Each piece gets a new root object with
    // the original root's local pose, holds its branches, and carries a copy of the original component; the last
    // piece stays on the original. Rules carried over from the PhysBone Lab, where every split passed the pose and
    // force gates: Multi-Child Ignore, at least two branches per piece, nothing else rooted in or above the chain,
    // no animator parameter, not animated, and curves time-rescaled when a piece is shallower than the original.
    internal static class PhysBoneSplitPass
    {
        // VRC.Dynamics evaluates these at CalcBoneRatio(i) = i / (maxBoneChainIndex + e - 1), and radiusCurve at
        // CalcTransformRatio(i) = i / (maxBoneChainIndex + e), where e is 1 when endpointPosition is set (IL of
        // PhysBoneManager.AddChains). maxBoneChainIndex is the deepest bone's index, the root being 0.
        static readonly string[] BoneRatioCurves =
        {
            "pullCurve", "springCurve", "stiffnessCurve", "gravityCurve", "gravityFalloffCurve", "immobileCurve",
            "maxAngleXCurve", "maxAngleZCurve", "limitRotationXCurve", "limitRotationYCurve", "limitRotationZCurve",
            "maxStretchCurve", "maxSquishCurve", "stretchMotionCurve"
        };

        internal static void Run(GameObject avatar)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS)
            {
                Debug.Log("Arclight Optimizer: PhysBone split skipped on mobile; its PhysBone limits leave no room for extra components.");
                return;
            }
            VRCPhysBoneBase[] all = avatar.GetComponentsInChildren<VRCPhysBoneBase>(true);
            int budget = ComponentBudget(all.Length);
            var notes = new List<string>();
            Inventory inventory = Inventory.Build(avatar, all, notes);
            List<PhysBoneSplitPlanner.Choice> plan = PhysBoneSplitPlanner.Plan(inventory.model, budget);
            for (int i = 0; i < inventory.model.chains.Count; i++)
                if (inventory.model.chains[i].branchWeights != null && plan.All(choice => choice.chain != i))
                {
                    var (ratio, partitions, pieces) = PhysBoneSplitPlanner.BestAlone(inventory.model, i, budget);
                    var chain = inventory.model.chains[i];
                    notes.Add(inventory.components[i].GetRootTransform().name + (partitions == 0
                        ? ": no partition keeps every piece's curves reproducible"
                        : $": best predicted span {ratio:0.00} with {pieces} pieces ({chain.branchWeights.Length} branches, {chain.Weight} bones, group {chain.group}, {partitions} partitions)"));
                }
            float before = PhysBoneSplitPlanner.Cost(inventory.model, new PhysBoneSplitPlanner.Choice[0]);
            float after = PhysBoneSplitPlanner.Cost(inventory.model, plan);
            // Chain order, matching the planner's order of pieces appended under a shared parent.
            foreach (PhysBoneSplitPlanner.Choice choice in plan.OrderBy(choice => choice.chain)) Apply(inventory, choice);
            string summary = plan.Count == 0
                ? "no split predicted to help"
                : string.Join("; ", plan.Select(choice => inventory.components[choice.chain].GetRootTransform().name + " into " + choice.Pieces +
                    " (pieces under " + inventory.nodes[choice.hostParent].name + ")")) +
                  $". Predicted solver span {after / before:0.00} of the original (transform-count model, not measured)";
            Debug.Log($"Arclight Optimizer: PhysBone split on {avatar.name}: {summary}. Component budget {budget}." +
                (notes.Count == 0 ? "" : " Not split: " + string.Join("; ", notes.Distinct()) + "."));
        }

        /// <summary>Extra components allowed without moving the PC PhysBone component count into a worse rating.</summary>
        static int ComponentBudget(int count)
        {
            foreach (PerformanceRating rating in new[] { PerformanceRating.Excellent, PerformanceRating.Good, PerformanceRating.Medium, PerformanceRating.Poor })
            {
                int limit = AvatarPerformanceStats.GetStatLevelForRating(rating, false).physBone.componentCount;
                if (count <= limit) return limit - count;
            }
            return 0; // Already Very Poor on this count: leave it alone rather than add more.
        }

        sealed class Inventory
        {
            public readonly PhysBoneSplitPlanner.Model model = new PhysBoneSplitPlanner.Model();
            public readonly List<VRCPhysBoneBase> components = new List<VRCPhysBoneBase>();
            public readonly List<Transform> nodes = new List<Transform>();
            public readonly List<Transform[]> branches = new List<Transform[]>();

            public static Inventory Build(GameObject avatar, VRCPhysBoneBase[] all, List<string> notes)
            {
                var inventory = new Inventory();
                var index = new Dictionary<Transform, int>();
                void Add(Transform t)
                {
                    index[t] = inventory.nodes.Count;
                    inventory.nodes.Add(t);
                    inventory.model.children.Add(new List<int>());
                    foreach (Transform child in t) Add(child);
                }
                Add(avatar.transform);
                for (int i = 0; i < inventory.nodes.Count; i++)
                    foreach (Transform child in inventory.nodes[i]) inventory.model.children[i].Add(index[child]);

                // Components inactive at load register later, at the end of their group; they are left out of the
                // order model and never split.
                VRCPhysBoneBase[] live = all.Where(pb => pb.enabled && pb.gameObject.activeInHierarchy).ToArray();
                var roots = live.Select(pb => pb.GetRootTransform()).ToArray();
                var rootSet = new HashSet<Transform>(roots);
                int[] parentChain = roots.Select(root =>
                {
                    for (Transform t = root.parent; t != null; t = t.parent)
                        if (rootSet.Contains(t)) return Array.IndexOf(roots, t);
                    return -1;
                }).ToArray();
                int[] groups = PhysBoneSplitPlanner.Groups(live.Select(pb => pb.colliders.Any(c => c != null)).ToArray(), parentChain);
                HashSet<Transform> animated = AnimatedTransforms(avatar);

                for (int i = 0; i < live.Length; i++)
                {
                    VRCPhysBoneBase pb = live[i];
                    Transform root = roots[i];
                    Transform[] branchRoots = null;
                    string refusal = Refusal(pb, root, all, avatar, animated);
                    if (refusal != null) { if (Splittable(root, pb)) notes.Add(root.name + ": " + refusal); }
                    else branchRoots = root.Cast<Transform>().ToArray();
                    inventory.components.Add(pb);
                    inventory.branches.Add(branchRoots);
                    var exclusions = new HashSet<Transform>(pb.ignoreTransforms.Where(t => t != null));
                    var chain = new PhysBoneSplitPlanner.Chain
                    {
                        host = index[pb.transform],
                        root = index[root],
                        group = groups[i],
                        bones = Simulated(root, exclusions),
                        branchWeights = branchRoots?.Select(b => Simulated(b, exclusions)).ToArray()
                    };
                    if (branchRoots != null)
                    {
                        int[] depths = branchRoots.Select(b => Depth(b, exclusions)).ToArray();
                        int deepest = depths.Max();
                        chain.branchDepths = depths;
                        int endpoint = pb.endpointPosition != Vector3.zero ? 1 : 0;
                        // A shallower piece gets rescaled curves; RescaleCurves says when that is exact.
                        chain.accepts = partition => partition.All(piece => RescaleIsExact(pb, deepest, piece.Max(b => depths[b]), endpoint));
                        // A piece's component object may go under the original's object or any ancestor, as long as
                        // everything in between is always active (so it follows the same toggles) and the parent is
                        // not inside a chain (a new child there would become a bone).
                        var hostParents = new List<int>();
                        for (Transform t = pb.transform; t != null; t = t.parent)
                        {
                            if (!all.Any(other => t.IsChildOf(other.GetRootTransform()))) hostParents.Add(index[t]);
                            if (t == avatar.transform || !t.gameObject.activeSelf || animated.Contains(t)) break;
                        }
                        chain.hostParents = hostParents.ToArray();
                    }
                    inventory.model.chains.Add(chain);
                }
                return inventory;
            }
        }

        // Large enough to be worth naming in the log when refused.
        static bool Splittable(Transform root, VRCPhysBoneBase pb) => root.childCount >= 2 * PhysBoneSplitPlanner.MinBranchesPerPiece;

        static string Refusal(VRCPhysBoneBase pb, Transform root, VRCPhysBoneBase[] all, GameObject avatar, HashSet<Transform> animated)
        {
            if (!(pb is VRCPhysBone)) return "not a VRCPhysBone";
            if (root == avatar.transform || root.parent == null) return "rooted on the avatar root";
            if (pb.multiChildType != VRCPhysBoneBase.MultiChildType.Ignore) return "Multi-Child Type is not Ignore";
            if (!string.IsNullOrEmpty(pb.parameter)) return "drives an animator parameter";
            if (pb.isAnimated) return "marked as animated";
            if (root.childCount < 2 * PhysBoneSplitPlanner.MinBranchesPerPiece) return "fewer than four direct branches";
            var names = new HashSet<string>();
            foreach (Transform child in root)
            {
                if (!child.gameObject.activeSelf) return "has an inactive branch";
                if (!names.Add(child.name)) return "has duplicate branch names";
            }
            foreach (Transform excluded in pb.ignoreTransforms)
            {
                if (excluded == null) continue;
                if (excluded.parent == root) return "excludes a whole branch";
                if (!excluded.IsChildOf(root)) return "excludes a transform outside its chain";
            }
            if (pb.GetComponents<VRCPhysBoneBase>().Length != 1) return "shares its object with another PhysBone";
            if (pb.transform != root && pb.transform.IsChildOf(root)) return "its component sits inside its own chain";
            // Pieces copy the component, so an animated enable or toggle on its object would not reach them.
            if (animated.Contains(pb.transform) || animated.Contains(root)) return "its object or root is animated";
            foreach (VRCPhysBoneBase other in all)
            {
                if (other == pb) continue;
                Transform otherRoot = other.GetRootTransform();
                if (otherRoot.IsChildOf(root) || root.IsChildOf(otherRoot) || other.transform.IsChildOf(root))
                    return "overlaps another PhysBone";
                if (other.ignoreTransforms.Any(t => t != null && t.IsChildOf(root))) return "another PhysBone excludes part of it";
            }
            foreach (VRCPhysBoneColliderBase collider in avatar.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
                if (collider.GetRootTransform().IsChildOf(root) || collider.transform.IsChildOf(root)) return "a collider sits on its bones";
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && animated.Contains(t)) return "an animation targets its bones by path";
            return null;
        }

        /// <summary>Every transform some controller clip binds to. Splitting changes the paths below a chain root.</summary>
        static HashSet<Transform> AnimatedTransforms(GameObject avatar)
        {
            var result = new HashSet<Transform>();
            var seen = new HashSet<(Transform, AnimationClip)>();
            void Scan(Transform owner, RuntimeAnimatorController controller)
            {
                if (controller == null) return;
                foreach (AnimationClip clip in controller.animationClips)
                {
                    if (clip == null || !seen.Add((owner, clip))) continue;
                    foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                    {
                        Transform target = binding.path.Length == 0 ? owner : owner.Find(binding.path);
                        if (target != null) result.Add(target);
                    }
                }
            }
            foreach (Animator animator in avatar.GetComponentsInChildren<Animator>(true)) Scan(animator.transform, animator.runtimeAnimatorController);
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
                foreach (var layer in (descriptor.baseAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0])
                         .Concat(descriptor.specialAnimationLayers ?? new VRCAvatarDescriptor.CustomAnimLayer[0]))
                    Scan(avatar.transform, layer.animatorController);
            return result;
        }

        static int Simulated(Transform t, HashSet<Transform> exclusions)
        {
            if (exclusions.Contains(t)) return 0;
            int count = 1;
            foreach (Transform child in t) count += Simulated(child, exclusions);
            return count;
        }

        // Chain index of the deepest simulated transform in a branch; the branch root is 1.
        static int Depth(Transform t, HashSet<Transform> exclusions)
        {
            if (exclusions.Contains(t)) return 0;
            int deepest = 0;
            foreach (Transform child in t) deepest = Math.Max(deepest, Depth(child, exclusions));
            return 1 + deepest;
        }

        static void Apply(Inventory inventory, PhysBoneSplitPlanner.Choice choice)
        {
            var source = (VRCPhysBone)inventory.components[choice.chain];
            Transform root = source.GetRootTransform();
            Transform[] branches = inventory.branches[choice.chain];
            var exclusions = new HashSet<Transform>(source.ignoreTransforms.Where(t => t != null));
            int originalMax = branches.Max(b => Depth(b, exclusions));
            var poses = root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => (t.position, t.rotation));
            Transform hostParent = inventory.nodes[choice.hostParent];
            int insertAt = root.GetSiblingIndex();
            var pieces = new List<(VRCPhysBoneBase component, int max)>();
            for (int p = 0; p < choice.Pieces - 1; p++)
            {
                var pieceRoot = new GameObject(UniqueName(root.parent, root.name + " Split " + (p + 1))).transform;
                pieceRoot.SetParent(root.parent, false);
                pieceRoot.localPosition = root.localPosition;
                pieceRoot.localRotation = root.localRotation;
                pieceRoot.localScale = root.localScale;
                pieceRoot.SetSiblingIndex(insertAt + p);
                foreach (int b in choice.groups[p]) branches[b].SetParent(pieceRoot, false);
                var componentHost = new GameObject(UniqueName(hostParent, root.name + " Split " + (p + 1) + " PhysBone"));
                componentHost.transform.SetParent(hostParent, false);
                componentHost.transform.SetAsLastSibling();
                var copy = componentHost.AddComponent<VRCPhysBone>();
                EditorUtility.CopySerialized(source, copy);
                var serialized = new SerializedObject(copy);
                serialized.FindProperty("rootTransform").objectReferenceValue = pieceRoot;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                pieces.Add((copy, choice.groups[p].Max(b => Depth(branches[b], exclusions))));
            }
            pieces.Add((source, choice.groups[choice.Pieces - 1].Max(b => Depth(branches[b], exclusions))));
            foreach (var piece in pieces)
                if (piece.max != originalMax) RescaleCurves(piece.component, originalMax, piece.max, source.endpointPosition != Vector3.zero ? 1 : 0);

            // Reparenting keeps local poses under an identical parent, so world poses must not move.
            foreach (var pose in poses)
                if ((pose.Key.position - pose.Value.position).sqrMagnitude > 1e-10f || Quaternion.Angle(pose.Key.rotation, pose.Value.rotation) > 0.01f)
                    throw new InvalidOperationException("Arclight PhysBone split moved " + pose.Key.name + "; the build was stopped instead of changing physics.");
            foreach (var piece in pieces)
                if (piece.component.GetRootTransform().childCount < PhysBoneSplitPlanner.MinBranchesPerPiece)
                    throw new InvalidOperationException("Arclight PhysBone split left a piece of " + root.name + " with fewer than two branches.");
        }

        static string UniqueName(Transform parent, string name)
        {
            string candidate = name;
            for (int n = 2; parent.Find(candidate) != null; n++) candidate = name + " " + n;
            return candidate;
        }

        /// <summary>
        /// A rescale is exact unless the SDK's Clamp01 folds two bones onto one curve point. With an endpoint it never
        /// does. Without one, a piece's deepest bone (index m) clamps onto the next one up (bone-ratio curves) and its
        /// end radius onto its begin radius, so those original values must already be equal. A piece whose bone-ratio
        /// denominator is 0 gets ratio 0 everywhere, which only matches when every curve is flat.
        /// </summary>
        static bool RescaleIsExact(Component component, int originalMax, int pieceMax, int endpoint)
        {
            if (pieceMax == originalMax || endpoint == 1) return true;
            if (pieceMax - 1 <= 0) return false;
            var serialized = new SerializedObject(component);
            float Value(string name, float t)
            {
                AnimationCurve curve = serialized.FindProperty(name)?.animationCurveValue;
                return curve == null || curve.length == 0 ? 1f : curve.Evaluate(t);
            }
            foreach (string name in BoneRatioCurves)
                if (Math.Abs(Value(name, (float)(pieceMax - 1) / (originalMax - 1)) - Value(name, (float)pieceMax / (originalMax - 1))) > 1e-6f)
                    return false;
            return Math.Abs(Value("radiusCurve", (float)pieceMax / originalMax) - Value("radiusCurve", (float)(pieceMax + 1) / originalMax)) <= 1e-6f;
        }

        /// <summary>
        /// Time-scales a shallower piece's curves so every bone evaluates its original value: a bone at index i reads
        /// n(i / dPiece) = c(i / dOriginal) when key times are multiplied by dOriginal / dPiece. Bone-ratio curves use
        /// d = max + e - 1 and radiusCurve uses d = max + e (see BoneRatioCurves).
        /// </summary>
        static void RescaleCurves(Component component, int originalMax, int pieceMax, int endpoint)
        {
            var serialized = new SerializedObject(component);
            float boneScale = (float)(originalMax + endpoint - 1) / (pieceMax + endpoint - 1);
            float transformScale = (float)(originalMax + endpoint) / (pieceMax + endpoint);
            foreach (string name in BoneRatioCurves.Append("radiusCurve"))
            {
                SerializedProperty property = serialized.FindProperty(name);
                if (property == null || property.propertyType != SerializedPropertyType.AnimationCurve)
                    throw new InvalidOperationException("The installed PhysBone has no curve " + name + "; the split was stopped.");
                AnimationCurve curve = property.animationCurveValue;
                if (curve == null || curve.length == 0 || curve.keys.All(key => key.time == 0f)) continue;
                float scale = name == "radiusCurve" ? transformScale : boneScale;
                Keyframe[] keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i].time *= scale;
                    keys[i].inTangent /= scale;
                    keys[i].outTangent /= scale;
                }
                property.animationCurveValue = new AnimationCurve(keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
