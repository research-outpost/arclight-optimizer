using System;
using System.Diagnostics;
using UnityEngine;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    /// <summary>Copies original protected edge values into the extra resolution-based padding band.</summary>
    internal static class EdgeValueExtender
    {

        public static int Extend(Color32[] source, Color32[] output, bool[] protectedMask,
            int width, int height, int radiusX, int radiusY, bool repeatX, bool repeatY, Action cancel = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (protectedMask == null) throw new ArgumentNullException(nameof(protectedMask));
            UvCoverageRasterizer.ValidateSize(width, height);
            int length = checked(width * height);
            if (source.Length != length || output.Length != length || protectedMask.Length != length)
                throw new ArgumentException("Pixel buffers must match the texture dimensions.");
            if (radiusX < 0 || radiusY < 0) throw new ArgumentOutOfRangeException("Extension radii cannot be negative.");
            if (radiusX == 0 && radiusY == 0) return 0;

            radiusX = Math.Min(radiusX, width);
            radiusY = Math.Min(radiusY, height);
            var budget = new WorkBudget(cancel);
            return radiusX == radiusY
                ? ExtendSquare(source, output, protectedMask, width, height, radiusX, repeatX, repeatY, budget)
                : ExtendRectangle(source, output, protectedMask, width, height, radiusX, radiusY, repeatX, repeatY, budget);
        }

        private static int ExtendSquare(Color32[] source, Color32[] output, bool[] protectedMask,
            int width, int height, int radius, bool repeatX, bool repeatY, WorkBudget budget)
        {
            // available is also the visited set. Original protected texels remain unavailable to overwrite.
            var available = new bool[protectedMask.Length];
            var queue = new int[protectedMask.Length];
            int head = 0, tail = 0;
            for (int i = 0; i < protectedMask.Length; i++)
            {
                if ((i & 4095) == 0) budget.Check();
                if (!protectedMask[i]) continue;
                output[i] = source[i];
                int x = i % width, y = i / width;
                if (!IsBoundary(protectedMask, width, height, x, y, repeatX, repeatY)) continue;
                available[i] = true;
                queue[tail++] = i;
            }

            int extended = 0;
            int layerEnd = tail;
            for (int distance = 0; head < tail && distance < radius; distance++)
            {
                while (head < layerEnd)
                {
                    budget.CheckOccasionally(head);
                    int i = queue[head++];
                    int x = i % width, y = i / width;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = Neighbor(y + dy, height, repeatY);
                        if (ny < 0) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = Neighbor(x + dx, width, repeatX);
                            if (nx < 0) continue;
                            int j = ny * width + nx;
                            if (protectedMask[j] || available[j]) continue;
                            available[j] = true;
                            output[j] = output[i];
                            queue[tail++] = j;
                            extended++;
                        }
                    }
                }
                layerEnd = tail;
            }
            budget.Check();
            return extended;
        }

        private static int ExtendRectangle(Color32[] source, Color32[] output, bool[] protectedMask,
            int width, int height, int radiusX, int radiusY, bool repeatX, bool repeatY, WorkBudget budget)
        {
            // This set starts with only original protected perimeter texels and grows in two separable passes.
            var available = new bool[protectedMask.Length];
            for (int i = 0; i < protectedMask.Length; i++)
            {
                if ((i & 4095) == 0) budget.Check();
                if (!protectedMask[i]) continue;
                output[i] = source[i];
                int x = i % width, y = i / width;
                if (IsBoundary(protectedMask, width, height, x, y, repeatX, repeatY)) available[i] = true;
            }

            int scratchLength = Math.Max(width, height);
            var left = new int[scratchLength];
            var right = new int[scratchLength];
            int extended = 0;
            for (int y = 0; y < height; y++)
            {
                budget.CheckOccasionally(y);
                extended += ExtendLine(available, protectedMask, output, 1, y * width, width,
                    radiusX, repeatX, left, right, budget);
            }
            for (int x = 0; x < width; x++)
            {
                budget.CheckOccasionally(x);
                extended += ExtendLine(available, protectedMask, output, width, x, height,
                    radiusY, repeatY, left, right, budget);
            }
            budget.Check();
            return extended;
        }

        private static int ExtendLine(bool[] available, bool[] protectedMask, Color32[] output,
            int step, int start, int length, int radius, bool repeat,
            int[] left, int[] right, WorkBudget budget)
        {
            int previous = -1;
            for (int i = 0; i < length; i++)
            {
                budget.CheckOccasionally(i);
                if (available[start + i * step]) previous = i;
                left[i] = previous;
            }
            int next = -1;
            for (int i = length - 1; i >= 0; i--)
            {
                budget.CheckOccasionally(length - 1 - i);
                if (available[start + i * step]) next = i;
                right[i] = next;
            }

            int first = right[0], last = left[length - 1];
            int extended = 0;
            for (int i = 0; i < length; i++)
            {
                budget.CheckOccasionally(i);
                int destination = start + i * step;
                if (protectedMask[destination] || available[destination]) continue;

                int leftCandidate = left[i];
                int rightCandidate = right[i];
                bool hasLeft = leftCandidate >= 0;
                bool hasRight = rightCandidate >= 0;
                if (repeat)
                {
                    if (!hasLeft && last >= 0) { leftCandidate = last - length; hasLeft = true; }
                    if (!hasRight && first >= 0) { rightCandidate = first + length; hasRight = true; }
                }
                int leftDistance = hasLeft ? i - leftCandidate : int.MaxValue;
                int rightDistance = hasRight ? rightCandidate - i : int.MaxValue;
                bool useLeft = leftDistance <= rightDistance;
                int distance = useLeft ? leftDistance : rightDistance;
                if (distance > radius) continue;

                int candidate = useLeft ? leftCandidate : rightCandidate;
                if (leftDistance == rightDistance)
                {
                    int leftIndex = Mod(leftCandidate, length), rightIndex = Mod(rightCandidate, length);
                    if (rightIndex < leftIndex) candidate = rightCandidate;
                }
                int source = start + Mod(candidate, length) * step;
                output[destination] = output[source];
                available[destination] = true;
                extended++;
            }
            return extended;
        }

        private static bool IsBoundary(bool[] mask, int width, int height, int x, int y, bool repeatX, bool repeatY)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = Neighbor(y + dy, height, repeatY);
                if (ny < 0) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = Neighbor(x + dx, width, repeatX);
                    if (nx >= 0 && !mask[ny * width + nx]) return true;
                }
            }
            return false;
        }

        private static int Neighbor(int index, int size, bool repeat)
        {
            if (repeat) return Mod(index, size);
            return index < 0 || index >= size ? -1 : index;
        }

        private static int Mod(int index, int size) => ((index % size) + size) % size;

        // Only the Cancel button: every pass is linear in the texture (each texel is queued at most once), and the texture is
        // already limited to 16 megapixels, so no time limit is needed and the outcome never depends on the machine.
        private sealed class WorkBudget
        {
            private readonly Action cancel;
            public WorkBudget(Action cancel) { this.cancel = cancel; }
            public void CheckOccasionally(int index) { if ((index & 4095) == 0) Check(); }
            public void Check() => cancel?.Invoke();
        }
    }
}
