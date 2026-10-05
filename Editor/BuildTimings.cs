using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Wall-clock time per Arclight step on one avatar build, for the report's timing line. Measures only; changes nothing.
    // Step(name) ends the running step and starts the next, so every moment between two Step calls belongs to one step.
    // Analysis rebuilds and controller commits are also totalled on their own (that time is inside the steps too).
    internal sealed class BuildTimings
    {
        internal static BuildTimings Current;

        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly Dictionary<string, double> steps = new Dictionary<string, double>();
        private string running;
        private double started;
        private double analysisSeconds, commitSeconds;
        private int analyses, commits;

        internal static void Step(string name) => Current?.Start(name);

        private void Start(string name)
        {
            double now = watch.Elapsed.TotalSeconds;
            if (running != null) steps[running] = (steps.TryGetValue(running, out double s) ? s : 0) + now - started;
            running = name;
            started = now;
        }

        internal static T Analysis<T>(Func<T> build) => Measure(build, (t, s) => { t.analyses++; t.analysisSeconds += s; });
        internal static void Commit(Action commit) => Measure<object>(() => { commit(); return null; }, (t, s) => { t.commits++; t.commitSeconds += s; });

        private static T Measure<T>(Func<T> work, Action<BuildTimings, double> add)
        {
            var timings = Current;
            if (timings == null) return work();
            double from = timings.watch.Elapsed.TotalSeconds;
            try { return work(); }
            finally { add(timings, timings.watch.Elapsed.TotalSeconds - from); }
        }

        // The slowest steps first, then the totals; null when nothing was timed.
        internal string Summary()
        {
            Start(null);
            if (steps.Count == 0) return null;
            string Seconds(double s) => s.ToString(s < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + " s";
            return "Timings: " + Seconds(steps.Values.Sum()) + " in Arclight's passes (" +
                string.Join(", ", steps.Where(p => p.Value >= 0.05).OrderByDescending(p => p.Value).Select(p => p.Key + " " + Seconds(p.Value))) +
                "); of that, " + analyses + " avatar analyses took " + Seconds(analysisSeconds) + " and " + commits + " animator commits " + Seconds(commitSeconds) + ".";
        }
    }
}
