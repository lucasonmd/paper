using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TopicManager.Extensions;

namespace AggregationEngine.Benchmarks
{
    internal static class Program
    {
        private const string ResultsDir = "results";

        private static void Main(string[] args)
        {
            Directory.CreateDirectory(ResultsDir);

            // --exp2 runs only the aggregate-size sweep, so the ratio can be
            // re-measured across several thermally settled runs without
            // paying for the whole suite each time.
            if (Array.IndexOf(args, "--exp2") >= 0)
            {
                RunExperiment2();
                return;
            }

            if (Array.IndexOf(args, "--exp4") >= 0)
            {
                RunExperiment4();
                return;
            }

            Console.WriteLine("==================================================================");
            Console.WriteLine(" Experiment 1: notification fan-out under Shared Aggregation");
            Console.WriteLine("==================================================================");
            RunExperiment1();

            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine(" Experiment 2: steady-state re-publish cost vs. aggregate size");
            Console.WriteLine("==================================================================");
            RunExperiment2();

            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine(" Experiment 3: completion latency (Upsert call that completes)");
            Console.WriteLine("==================================================================");
            RunExperiment3();

            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine(" Experiment 4: order-independence correctness check (engine)");
            Console.WriteLine("==================================================================");
            RunExperiment4();

            Console.WriteLine();
            ThroughputAndResolution.Run(ResultsDir);

            Console.WriteLine();
            Console.WriteLine("All results written under: " + Path.GetFullPath(ResultsDir));
        }

        // -----------------------------------------------------------------
        // Experiment 1 - notification fan-out under Shared Aggregation
        //
        // Engine-only on purpose. An earlier version put a LegacyAggregator
        // column next to the engine's, but the two count different events -
        // receive-callback invocations vs. completed-aggregate notifications -
        // so "20 vs 400" was not a like-for-like comparison.
        //
        // This also no longer measures any unchanged-republish suppression.
        // Suppressing "unchanged" snapshots is not implementable on the
        // subscriber side of NGVA: every topic instance carries per-publish
        // metadata (A_timeOfDataGeneration, NGVA_DM_032; publishingEventID,
        // NGVA_DM_014), and IDL-generated GetHashCode/Equals cover every
        // attribute, so two successive publications of semantically identical
        // data never compare equal. Deciding that nothing meaningful changed
        // is the publisher's job.
        // -----------------------------------------------------------------
        private static void RunExperiment1()
        {
            int[] nValues = { 1, 2, 5, 10, 20 };
            const int repeats = 20;

            var rows = new List<string> { "N,Republishes,Notifications,PerRepublish" };

            foreach (var n in nValues)
            {
                long fired = FanOut_SpecRepublish(n, repeats);
                Console.WriteLine($"N={n,3}  {repeats} Specification republishes -> {fired,4} notifications " +
                                  $"({fired / (double)repeats:F0} per republish)");
                rows.Add($"{n},{repeats},{fired},{fired / (double)repeats:F0}");
            }

            File.WriteAllLines(Path.Combine(ResultsDir, "exp1_notification_fanout.csv"), rows, Encoding.UTF8);
        }

        // N roots share one Specification; republish it `repeats` times and
        // count completed-aggregate notifications.
        private static long FanOut_SpecRepublish(int n, int repeats)
        {
            var h = new EngineHarness();
            long count = 0;
            h.Engine.SubscribeRootKind(h.LinearMountKind, (_, __) => count++);

            BuildSharedSpecAggregate_Engine(h, n, out _, out var spec);

            count = 0;
            for (int r = 0; r < repeats; r++)
            {
                h.Engine.Upsert(h.SpecificationKind,
                    new LinearMountSpecification { SourceId = spec.SourceId, Revision = spec.Revision + r + 1 });
            }
            return count;
        }

        private static void BuildSharedSpecAggregate_Engine(EngineHarness h, int n, out LinearMount[] mounts, out LinearMountSpecification spec)
        {
            spec = new LinearMountSpecification { SourceId = 90_000, Revision = 1 };
            mounts = new LinearMount[n];
            for (int i = 0; i < n; i++)
            {
                long baseId = 100_000 + i * 10;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var softLimits = new LinearSoftLimits { SourceId = baseId + 2, LinearMountSourceId = baseId };
                var mount = new LinearMount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                };
                mounts[i] = mount;

                h.Engine.Upsert(h.ActualMountKind, actual);
                h.Engine.Upsert(h.SoftLimitsKind, softLimits);
                h.Engine.Upsert(h.LinearMountKind, mount);
            }
            h.Engine.Upsert(h.SpecificationKind, spec); // completes all n mounts
        }

        // -----------------------------------------------------------------
        // Experiment 2
        // -----------------------------------------------------------------
        private static void RunExperiment2()
        {
            int[] sizes = { 0, 10, 50, 100, 200, 400, 800 };
            const int trials = 3000;
            const int warmup = 500;

            var rows = new List<string> { "Parts,Legacy_us,Engine_Default_us,Engine_IsolateBoundaries_us" };

            foreach (var v in sizes)
            {
                double legacyUs = MeasureSteadyState_Legacy(v, warmup, trials);
                double engineDefaultUs = MeasureSteadyState_Engine(v, warmup, trials, false, false);
                double engineIsolateUs = MeasureSteadyState_Engine(v, warmup, trials, false, true);

                Console.WriteLine($"parts={v,4}  legacy={legacyUs,8:F2}us  engine(default)={engineDefaultUs,8:F2}us  " +
                                   $"engine(+isolate)={engineIsolateUs,8:F2}us");
                rows.Add($"{v},{legacyUs:F3},{engineDefaultUs:F3},{engineIsolateUs:F3}");
            }

            File.WriteAllLines(Path.Combine(ResultsDir, "exp2_scaling_vs_size.csv"), rows, Encoding.UTF8);
        }

        private static double MeasureSteadyState_Legacy(int partCount, int warmup, int trials)
        {
            var agg = new LegacyAggregator();
            long baseId = 200_000;
            var actual = new ActualMount { SourceId = baseId + 1 };
            var spec = new LinearMountSpecification { SourceId = baseId + 2 };
            var softLimits = new LinearSoftLimits { SourceId = baseId + 3, LinearMountSourceId = baseId };
            var mount = new LinearMount
            {
                SourceId = baseId,
                ActualMountSourceId = actual.SourceId,
                SpecificationSourceId = spec.SourceId,
                SoftLimitsSourceId = softLimits.SourceId,
                PartSourceIds = Enumerable.Range(0, partCount).Select(i => baseId + 100 + i).ToArray(),
            };

            agg.OnActualMount(actual);
            agg.OnSpecification(spec);
            agg.OnSoftLimits(softLimits);
            for (int i = 0; i < partCount; i++)
                agg.OnMountPart(new MountPart { SourceId = baseId + 100 + i, LinearMountSourceId = baseId });
            agg.OnLinearMount(mount);

            for (int i = 0; i < warmup; i++) agg.OnLinearMount(mount);

            var sw = new Stopwatch();
            long ticks = 0;
            for (int i = 0; i < trials; i++)
            {
                sw.Restart();
                agg.OnLinearMount(mount);
                sw.Stop();
                ticks += sw.ElapsedTicks;
            }
            return TicksToMicroseconds(ticks, trials);
        }

        private static double MeasureSteadyState_Engine(int partCount, int warmup, int trials, bool affectedOnly, bool isolate)
        {
            var h = new EngineHarness(affectedOnly, isolate);
            long baseId = 300_000;
            var actual = new ActualMount { SourceId = baseId + 1 };
            var spec = new LinearMountSpecification { SourceId = baseId + 2 };
            var softLimits = new LinearSoftLimits { SourceId = baseId + 3, LinearMountSourceId = baseId };
            var mount = new LinearMount
            {
                SourceId = baseId,
                ActualMountSourceId = actual.SourceId,
                SpecificationSourceId = spec.SourceId,
                SoftLimitsSourceId = softLimits.SourceId,
                PartSourceIds = Enumerable.Range(0, partCount).Select(i => baseId + 100 + i).ToArray(),
            };

            h.Engine.Upsert(h.ActualMountKind, actual);
            h.Engine.Upsert(h.SpecificationKind, spec);
            h.Engine.Upsert(h.SoftLimitsKind, softLimits);
            for (int i = 0; i < partCount; i++)
                h.Engine.Upsert(h.PartKind, new MountPart { SourceId = baseId + 100 + i, LinearMountSourceId = baseId });
            h.Engine.Upsert(h.LinearMountKind, mount);

            // No subscriber is registered in this experiment, so it measures
            // the engine's storage/index/traversal/snapshot pipeline without
            // application callback cost.
            for (int i = 0; i < warmup; i++) h.Engine.Upsert(h.LinearMountKind, mount);

            var sw = new Stopwatch();
            long ticks = 0;
            for (int i = 0; i < trials; i++)
            {
                sw.Restart();
                h.Engine.Upsert(h.LinearMountKind, mount);
                sw.Stop();
                ticks += sw.ElapsedTicks;
            }
            return TicksToMicroseconds(ticks, trials);
        }

        // -----------------------------------------------------------------
        // Experiment 3
        // -----------------------------------------------------------------
        private static void RunExperiment3()
        {
            const int trials = 5000;
            const int partCount = 5;

            var legacy = MeasureCompletionLatency_Legacy(trials, partCount);
            var engineDefault = MeasureCompletionLatency_Engine(trials, partCount, false, false);

            Console.WriteLine($"legacy:            p50={legacy.p50,7:F2}us  p95={legacy.p95,7:F2}us");
            Console.WriteLine($"engine (default):  p50={engineDefault.p50,7:F2}us  p95={engineDefault.p95,7:F2}us");

            var rows = new List<string>
            {
                "Config,P50_us,P95_us",
                $"Legacy,{legacy.p50:F3},{legacy.p95:F3}",
                $"Engine_Default,{engineDefault.p50:F3},{engineDefault.p95:F3}",
            };
            File.WriteAllLines(Path.Combine(ResultsDir, "exp3_completion_latency.csv"), rows, Encoding.UTF8);
        }

        private static (double p50, double p95) MeasureCompletionLatency_Legacy(int trials, int partCount)
        {
            var agg = new LegacyAggregator();
            var samples = new List<double>(trials);
            var sw = new Stopwatch();

            for (int i = 0; i < trials; i++)
            {
                long baseId = 400_000 + (long)i * 1000;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var spec = new LinearMountSpecification { SourceId = baseId + 2 };
                var softLimits = new LinearSoftLimits { SourceId = baseId + 3, LinearMountSourceId = baseId };
                var mount = new LinearMount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                    PartSourceIds = Enumerable.Range(0, partCount).Select(p => baseId + 100 + p).ToArray(),
                };

                agg.OnActualMount(actual);
                agg.OnSpecification(spec);
                agg.OnSoftLimits(softLimits);
                for (int p = 0; p < partCount; p++)
                    agg.OnMountPart(new MountPart { SourceId = baseId + 100 + p, LinearMountSourceId = baseId });

                sw.Restart();
                agg.OnLinearMount(mount); // completing call
                sw.Stop();
                samples.Add(TicksToMicroseconds(sw.ElapsedTicks, 1));
            }
            return Percentiles(samples);
        }

        private static (double p50, double p95) MeasureCompletionLatency_Engine(int trials, int partCount, bool affectedOnly, bool isolate)
        {
            var h = new EngineHarness(affectedOnly, isolate);
            var samples = new List<double>(trials);
            var sw = new Stopwatch();

            for (int i = 0; i < trials; i++)
            {
                long baseId = 500_000 + (long)i * 1000;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var spec = new LinearMountSpecification { SourceId = baseId + 2 };
                var softLimits = new LinearSoftLimits { SourceId = baseId + 3, LinearMountSourceId = baseId };
                var mount = new LinearMount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                    PartSourceIds = Enumerable.Range(0, partCount).Select(p => baseId + 100 + p).ToArray(),
                };

                h.Engine.Upsert(h.ActualMountKind, actual);
                h.Engine.Upsert(h.SpecificationKind, spec);
                h.Engine.Upsert(h.SoftLimitsKind, softLimits);
                for (int p = 0; p < partCount; p++)
                    h.Engine.Upsert(h.PartKind, new MountPart { SourceId = baseId + 100 + p, LinearMountSourceId = baseId });

                sw.Restart();
                h.Engine.Upsert(h.LinearMountKind, mount); // completing call
                sw.Stop();
                samples.Add(TicksToMicroseconds(sw.ElapsedTicks, 1));
            }
            return Percentiles(samples);
        }

        // -----------------------------------------------------------------
        // Experiment 4
        // -----------------------------------------------------------------
        private static void RunExperiment4()
        {
            long baseId = 700_000;
            var actual = new ActualMount { SourceId = baseId + 1 };
            var spec = new LinearMountSpecification { SourceId = baseId + 2 };
            var mountWithoutSoftLimits = new LinearMount
            {
                SourceId = baseId,
                ActualMountSourceId = actual.SourceId,
                SpecificationSourceId = spec.SourceId,
            };

            var requiredTopics = new (string Name, Action<global::TopicManager.Extensions.AggregationEngine, EngineHarness> Upsert)[]
            {
                ("LinearMount", (e, h) => e.Upsert(h.LinearMountKind, mountWithoutSoftLimits)),
                ("ActualMount", (e, h) => e.Upsert(h.ActualMountKind, actual)),
                ("LinearMountSpecification", (e, h) => e.Upsert(h.SpecificationKind, spec)),
            };

            int totalPermutations = 0;
            int okPermutations = 0;
            int anomalies = 0;

            foreach (var perm in Permutations(new[] { 0, 1, 2 }))
            {
                totalPermutations++;
                var h = new EngineHarness();
                int completions = 0;
                int completionsBeforeLast = 0;

                Action<RootId, AggregateSnapshot> onEmit = (root, snap) =>
                {
                    completions++;
                    bool hasAll =
                        snap.TryGetOne<LinearMount>(h.LinearMountKind, out _) &&
                        snap.TryGetOne<ActualMount>(h.ActualMountKind, out _) &&
                        snap.TryGetOne<LinearMountSpecification>(h.SpecificationKind, out _) &&
                        !snap.TryGetOne<LinearSoftLimits>(h.SoftLimitsKind, out _);
                    if (!hasAll) Console.WriteLine("  !! incomplete snapshot notified");
                };
                h.Engine.SubscribeRootKind(h.LinearMountKind, onEmit);

                for (int i = 0; i < perm.Length; i++)
                {
                    requiredTopics[perm[i]].Upsert(h.Engine, h);
                    if (i < perm.Length - 1 && completions > 0) completionsBeforeLast++;
                }

                if (completions == 1 && completionsBeforeLast == 0) okPermutations++;
                else anomalies++;
            }

            // A declared optional reference must also block completion until
            // the target arrives; absence itself remains valid for 0..1.
            var optionalHarness = new EngineHarness();
            var optionalActual = new ActualMount { SourceId = baseId + 101 };
            var optionalSpec = new LinearMountSpecification { SourceId = baseId + 102 };
            var optionalSoftLimits = new LinearSoftLimits { SourceId = baseId + 103, LinearMountSourceId = baseId + 100 };
            var mountWithSoftLimits = new LinearMount
            {
                SourceId = baseId + 100,
                ActualMountSourceId = optionalActual.SourceId,
                SpecificationSourceId = optionalSpec.SourceId,
                SoftLimitsSourceId = optionalSoftLimits.SourceId,
            };

            int optionalCompletions = 0;
            optionalHarness.Engine.SubscribeRootKind(optionalHarness.LinearMountKind, (root, snapshot) =>
            {
                optionalCompletions++;
                if (!snapshot.TryGetOne<LinearSoftLimits>(optionalHarness.SoftLimitsKind, out var softLimits))
                    throw new InvalidOperationException("Referenced optional SoftLimits was omitted from a completed snapshot.");
            });
            optionalHarness.Engine.Upsert(optionalHarness.ActualMountKind, optionalActual);
            optionalHarness.Engine.Upsert(optionalHarness.SpecificationKind, optionalSpec);
            optionalHarness.Engine.Upsert(optionalHarness.LinearMountKind, mountWithSoftLimits);
            int optionalCompletionsBeforeArrival = optionalCompletions;
            optionalHarness.Engine.Upsert(optionalHarness.SoftLimitsKind, optionalSoftLimits);
            int optionalCompletionsAfterArrival = optionalCompletions;
            if (optionalCompletionsBeforeArrival != 0 || optionalCompletionsAfterArrival != 1)
                anomalies++;

            Console.WriteLine($"permutations tested: {totalPermutations}");
            Console.WriteLine($"exactly-one-completion-at-the-end: {okPermutations}/{totalPermutations}");
            Console.WriteLine($"referenced optional target: {optionalCompletionsBeforeArrival} notification(s) before arrival, {optionalCompletionsAfterArrival} after arrival");
            if (anomalies > 0)
                Console.WriteLine($"  ANOMALIES: {anomalies}");

            File.WriteAllLines(Path.Combine(ResultsDir, "exp4_order_independence.csv"), new[]
            {
                "RequiredPermutations,RequiredExactlyOneCompletionAtEnd,OptionalNotificationsBeforeArrival,OptionalNotificationsAfterArrival,Anomalies",
                $"{totalPermutations},{okPermutations},{optionalCompletionsBeforeArrival},{optionalCompletionsAfterArrival},{anomalies}",
            }, Encoding.UTF8);
        }

        private static IEnumerable<int[]> Permutations(int[] items)
        {
            if (items.Length <= 1) { yield return items; yield break; }
            for (int i = 0; i < items.Length; i++)
            {
                var rest = items.Where((_, idx) => idx != i).ToArray();
                foreach (var p in Permutations(rest))
                    yield return new[] { items[i] }.Concat(p).ToArray();
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------
        private static double TicksToMicroseconds(long ticks, int count) =>
            count == 0 ? 0 : (ticks / (double)Stopwatch.Frequency) * 1_000_000.0 / count;

        private static (double p50, double p95) Percentiles(List<double> samples)
        {
            samples.Sort();
            double At(double p)
            {
                int idx = (int)Math.Ceiling(p * samples.Count) - 1;
                idx = Math.Max(0, Math.Min(samples.Count - 1, idx));
                return samples[idx];
            }
            return (At(0.50), At(0.95));
        }
    }
}
