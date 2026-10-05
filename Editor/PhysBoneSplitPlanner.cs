using System;
using System.Collections.Generic;
using System.Linq;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Avatars whose Arclight Optimizer asked for PhysBone splitting. The texture pass removes the component before
    // Avatar Optimizer runs, and the split must run after it (AAO merges PhysBones with equal settings), so the
    // request travels here, keyed by the build avatar object.
    internal static class PhysBoneSplitRequests
    {
        private static readonly HashSet<int> Requested = new HashSet<int>();
        internal static void Add(UnityEngine.GameObject avatar) => Requested.Add(avatar.GetInstanceID());
        internal static bool Take(UnityEngine.GameObject avatar) => Requested.Remove(avatar.GetInstanceID());
    }

    // Chooses PhysBone splits that let the SDK solve one heavy chain on several workers. SDK facts this relies
    // on (VRC.Dynamics IL and Lab measurements, Library/Arclight/PhysBoneLab/ExecutionGroupEvidence and
    // ChainOrderEvidence):
    //  - each PhysBone component is one chain, solved whole by one job;
    //  - chains sit in execution groups that run one after another: a PhysBone with colliders, or rooted below
    //    another PhysBone's root, runs in a later group than what it depends on;
    //  - each group is a parallel-for over its chains in batches of 8 consecutive chains;
    //  - within a group, chains keep the avatar's depth-first hierarchy order of the components.
    // So a split only helps when its pieces land in another batch of 8 than the heavy part of the original.
    // The planner works on a plain hierarchy model and needs no SDK.
    internal static class PhysBoneSplitPlanner
    {
        public const int BatchSize = 8;
        public const int MaxPieces = 8;
        public const int MinBranchesPerPiece = 2; // Multi-Child Ignore keeps a root fixed only with two or more children.
        public const float RequiredGain = 0.10f;  // The whole plan must cut the predicted span this much; splits add a little work.
        public const float StepGain = 0.03f;      // Each split must add at least this much on its own.

        public sealed class Chain
        {
            public int host;             // node holding the component; the order key
            public int root;             // chain root node; split pieces' roots become its siblings
            public int[] hostParents;    // nodes that may hold the pieces' component objects as new last children
            public int group;            // SDK execution group
            public int[] branchWeights;  // simulated transforms per direct branch; null when the chain cannot be split
            public int[] branchDepths;   // optional: deepest chain index per branch; pieces are seeded with deepest branches
            public Func<int[][], bool> accepts; // optional veto per partition (for example, curve rescaling limits)

            public int bones;            // simulated transforms, used when branchWeights is null

            public int Weight => branchWeights != null ? 1 + branchWeights.Sum() : Math.Max(1, bones);
        }

        public sealed class Model
        {
            public List<List<int>> children = new List<List<int>>(); // per node, in sibling order; node 0 is the avatar root
            public List<Chain> chains = new List<Chain>();           // in component order
        }

        public sealed class Choice
        {
            public int chain;
            public int hostParent;       // node that gets the pieces' component objects as its last children
            public int[][] groups;       // branch indices per piece; the last group stays on the original component

            public int Pieces => groups.Length;
        }

        /// <summary>
        /// Greedy plan: heaviest splittable chains first, each taking its best piece count and placement. Nothing is
        /// split unless the whole plan reaches RequiredGain.
        /// </summary>
        public static List<Choice> Plan(Model model, int componentBudget)
        {
            var choices = new List<Choice>();
            float cost = Cost(model, choices), baseline = cost;
            var order = Enumerable.Range(0, model.chains.Count).Where(i => model.chains[i].branchWeights != null)
                .OrderByDescending(i => model.chains[i].Weight).ThenBy(i => i).ToList();
            foreach (int index in order)
            {
                Choice best = null;
                float bestCost = cost;
                foreach (Choice candidate in Candidates(model.chains[index], index))
                {
                    if (candidate.Pieces - 1 > componentBudget) continue;
                    choices.Add(candidate);
                    float candidateCost = Cost(model, choices);
                    choices.RemoveAt(choices.Count - 1);
                    // Fewer pieces win ties: candidates come in increasing piece count.
                    if (candidateCost < bestCost - 1e-4f) { best = candidate; bestCost = candidateCost; }
                }
                if (best == null || bestCost > cost * (1f - StepGain)) continue;
                choices.Add(best);
                componentBudget -= best.Pieces - 1;
                cost = bestCost;
            }
            return cost <= baseline * (1f - RequiredGain) ? choices : new List<Choice>();
        }

        /// <summary>Best predicted cost ratio for splitting one chain alone, and how many partitions were allowed.</summary>
        public static (float ratio, int partitions, int pieces) BestAlone(Model model, int chain, int componentBudget)
        {
            float baseline = Cost(model, new Choice[0]), best = baseline;
            int partitions = 0, pieces = 1;
            foreach (Choice candidate in Candidates(model.chains[chain], chain))
            {
                partitions++;
                if (candidate.Pieces - 1 > componentBudget) continue;
                float cost = Cost(model, new[] { candidate });
                if (cost < best) { best = cost; pieces = candidate.Pieces; }
            }
            return (best / baseline, partitions, pieces);
        }

        static IEnumerable<Choice> Candidates(Chain chain, int index)
        {
            int limit = Math.Min(MaxPieces, chain.branchWeights.Length / MinBranchesPerPiece);
            for (int pieces = 2; pieces <= limit; pieces++)
            {
                // Giving every piece one of the deepest branches keeps each piece's chain depth, so its force curves
                // need no rescaling. Plain balance is the fallback.
                int[] seeds = null;
                if (chain.branchDepths != null)
                {
                    int deepest = chain.branchDepths.Max();
                    seeds = Enumerable.Range(0, chain.branchDepths.Length).Where(b => chain.branchDepths[b] == deepest)
                        .OrderByDescending(b => chain.branchWeights[b]).ThenBy(b => b).Take(pieces).ToArray();
                    if (seeds.Length < pieces) seeds = null;
                }
                int[][] groups = new[] { seeds == null ? null : Balance(chain.branchWeights, pieces, seeds), Balance(chain.branchWeights, pieces) }
                    .FirstOrDefault(partition => partition != null && (chain.accepts == null || chain.accepts(partition)));
                if (groups == null) continue;
                // The pieces' components sit on new objects; where those objects go sets their place in the order.
                foreach (int hostParent in chain.hostParents)
                    yield return new Choice { chain = index, hostParent = hostParent, groups = groups };
            }
        }

        /// <summary>
        /// Longest-processing-time partition of whole branches: larger branches go to the currently lightest group,
        /// except that once the remaining branches are only enough to give every group two, they go to the groups
        /// still short. Groups keep ascending branch order. Null when there are fewer than two branches per group.
        /// </summary>
        public static int[][] Balance(int[] branchWeights, int pieces, int[] seeds = null)
        {
            if (branchWeights.Length < pieces * MinBranchesPerPiece) return null;
            var groups = Enumerable.Range(0, pieces).Select(_ => new List<int>()).ToArray();
            var sums = new int[pieces];
            int remaining = branchWeights.Length;
            // Optional seeds: one given branch per group, placed before the balancing pass.
            for (int g = 0; seeds != null && g < pieces; g++)
            {
                groups[g].Add(seeds[g]);
                sums[g] += branchWeights[seeds[g]];
                remaining--;
            }
            foreach (int branch in Enumerable.Range(0, branchWeights.Length).Where(b => seeds == null || Array.IndexOf(seeds, b) < 0)
                         .OrderByDescending(b => branchWeights[b]).ThenBy(b => b))
            {
                int deficit = groups.Sum(group => Math.Max(0, MinBranchesPerPiece - group.Count));
                bool shortOnly = remaining <= deficit;
                int target = -1;
                for (int g = 0; g < pieces; g++)
                {
                    if (shortOnly && groups[g].Count >= MinBranchesPerPiece) continue;
                    if (target < 0 || sums[g] < sums[target] || (sums[g] == sums[target] && groups[g].Count < groups[target].Count)) target = g;
                }
                groups[target].Add(branch);
                sums[target] += branchWeights[branch];
                remaining--;
            }
            return groups.Select(group => group.OrderBy(b => b).ToArray()).ToArray();
        }

        /// <summary>
        /// Predicted solver span: for each execution group, the heaviest batch of 8 consecutive chains, summed over
        /// groups (groups run in sequence, batches in parallel). Weights are simulated transforms, a work proxy.
        /// </summary>
        public static float Cost(Model model, IList<Choice> choices)
        {
            float total = 0;
            foreach (var group in Order(model, choices).GroupBy(entry => entry.group))
            {
                var weights = group.Select(entry => entry.weight).ToList();
                float span = 0;
                for (int start = 0; start < weights.Count; start += BatchSize)
                    span = Math.Max(span, weights.Skip(start).Take(BatchSize).Sum());
                total += span;
            }
            return total;
        }

        /// <summary>Chains in the SDK's order after applying the choices: depth-first over the hierarchy.</summary>
        public static List<(int chain, int piece, int group, int weight)> Order(Model model, IList<Choice> choices)
        {
            // Virtual nodes for the pieces' component objects, appended under each choice's host parent. The pieces'
            // roots hold no component, so they do not affect the order.
            var children = model.children.Select(list => new List<int>(list)).ToList();
            var hosted = new Dictionary<int, List<(int chain, int piece, int weight)>>();
            void Host(int node, int chain, int piece, int weight)
            {
                if (!hosted.TryGetValue(node, out var list)) hosted[node] = list = new List<(int, int, int)>();
                list.Add((chain, piece, weight));
            }
            var choiceByChain = choices.ToDictionary(choice => choice.chain);
            for (int c = 0; c < model.chains.Count; c++)
            {
                Chain chain = model.chains[c];
                if (!choiceByChain.TryGetValue(c, out Choice choice))
                {
                    Host(chain.host, c, -1, chain.Weight);
                    continue;
                }
                int pieces = choice.Pieces;
                Host(chain.host, c, pieces - 1, PieceWeight(chain, choice.groups[pieces - 1]));
                for (int p = 0; p < pieces - 1; p++)
                {
                    int node = children.Count;
                    children.Add(new List<int>());
                    children[choice.hostParent].Add(node);
                    Host(node, c, p, PieceWeight(chain, choice.groups[p]));
                }
            }
            var result = new List<(int, int, int, int)>();
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (hosted.TryGetValue(node, out var list))
                    foreach (var entry in list) result.Add((entry.chain, entry.piece, model.chains[entry.chain].group, entry.weight));
                for (int i = children[node].Count - 1; i >= 0; i--) stack.Push(children[node][i]);
            }
            return result;
        }

        static int PieceWeight(Chain chain, int[] branches) => 1 + branches.Sum(b => chain.branchWeights[b]);

        /// <summary>
        /// SDK execution groups: colliders sit in group 0, so a chain with colliders is at least 1, and a chain is
        /// one past the chain whose root is its nearest ancestor PhysBone root. Avatars get at most 8 groups.
        /// </summary>
        public static int[] Groups(bool[] usesColliders, int[] parentChain)
        {
            var groups = new int[usesColliders.Length];
            for (int i = 0; i < groups.Length; i++) groups[i] = -1;
            int Resolve(int i, int depth)
            {
                if (groups[i] >= 0) return groups[i];
                if (depth > groups.Length) throw new InvalidOperationException("PhysBone parent dependencies form a cycle.");
                int group = usesColliders[i] ? 1 : 0;
                if (parentChain[i] >= 0) group = Math.Max(group, Resolve(parentChain[i], depth + 1) + 1);
                return groups[i] = group;
            }
            for (int i = 0; i < groups.Length; i++) Resolve(i, 0);
            return groups;
        }
    }
}
