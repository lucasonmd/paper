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

        private static void Main()
        {
            Directory.CreateDirectory(ResultsDir);

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
            Console.WriteLine("All results written under: " + Path.GetFullPath(ResultsDir));
        }

        // -----------------------------------------------------------------
        // Experiment 1
        // -----------------------------------------------------------------
        private static void RunExperiment1()
        {
            int[] nValues = { 1, 2, 5, 10, 20 };
            const int repeats = 20;

            var rows = new List<string> { "N,Legacy,Engine_Default,Engine_EmitOnlyAffected,Engine_EmitOnlyAffected_Suppress" };

            foreach (var n in nValues)
            {
                long legacy = MeasureFanOut_Legacy(n, repeats);
                long engineDefault = MeasureFanOut_Engine(n, repeats, false, false);
                long engineFiltered = MeasureFanOut_Engine(n, repeats, true, false);
                long engineFilteredSuppressed = MeasureFanOut_Engine(n, repeats, true, true);

                Console.WriteLine($"N={n,3}  legacy={legacy,4}  engine(default)={engineDefault,4}  " +
                                   $"engine(+affectedOnly)={engineFiltered,4}  engine(+affectedOnly+suppress)={engineFilteredSuppressed,4}");
                rows.Add($"{n},{legacy},{engineDefault},{engineFiltered},{engineFilteredSuppressed}");
            }

            File.WriteAllLines(Path.Combine(ResultsDir, "exp1_notification_fanout.csv"), rows, Encoding.UTF8);
        }

        private static long MeasureFanOut_Legacy(int n, int repeats)
        {
            var agg = new LegacyAggregator();
            BuildSharedSpecAggregate_Legacy(agg, n, out var mounts, out _);

            agg.NotificationCount = 0;
            for (int r = 0; r < repeats; r++)
                agg.OnMount(mounts[0]); // republish mount #0 unchanged

            return agg.NotificationCount;
        }

        private static long MeasureFanOut_Engine(int n, int repeats, bool emitOnlyAffected, bool suppressUnchanged)
        {
            var h = new EngineHarness(emitOnlyAffectedRoots: emitOnlyAffected, suppressUnchangedSnapshots: suppressUnchanged);
            long count = 0;
            Action<RootId, AggregateSnapshot> onEmit = (_, __) => count++;
            h.Engine.SubscribeRootKind(h.MountKind, onEmit);

            BuildSharedSpecAggregate_Engine(h, n, out var mounts, out _);

            count = 0;
            for (int r = 0; r < repeats; r++)
                h.Engine.Upsert(h.MountKind, mounts[0]);

            return count;
        }

        private static void BuildSharedSpecAggregate_Legacy(LegacyAggregator agg, int n, out Mount[] mounts, out Specification spec)
        {
            spec = new Specification { SourceId = 90_000 };
            mounts = new Mount[n];
            for (int i = 0; i < n; i++)
            {
                long baseId = 100_000 + i * 10;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var softLimits = new SoftLimits { SourceId = baseId + 2, MountSourceId = baseId };
                var mount = new Mount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                };
                mounts[i] = mount;

                agg.OnActualMount(actual);
                agg.OnSoftLimits(softLimits);
                agg.OnMount(mount);
            }
            agg.OnSpecification(spec); // completes all n mounts
        }

        private static void BuildSharedSpecAggregate_Engine(EngineHarness h, int n, out Mount[] mounts, out Specification spec)
        {
            spec = new Specification { SourceId = 90_000 };
            mounts = new Mount[n];
            for (int i = 0; i < n; i++)
            {
                long baseId = 100_000 + i * 10;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var softLimits = new SoftLimits { SourceId = baseId + 2, MountSourceId = baseId };
                var mount = new Mount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                };
                mounts[i] = mount;

                h.Engine.Upsert(h.ActualMountKind, actual);
                h.Engine.Upsert(h.SoftLimitsKind, softLimits);
                h.Engine.Upsert(h.MountKind, mount);
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

            var rows = new List<string> { "Zones,Legacy_us,Engine_Default_us,Engine_IsolateBoundaries_us,Engine_AllFlags_us" };

            foreach (var v in sizes)
            {
                double legacyUs = MeasureSteadyState_Legacy(v, warmup, trials);
                double engineDefaultUs = MeasureSteadyState_Engine(v, warmup, trials, false, false, false);
                double engineIsolateUs = MeasureSteadyState_Engine(v, warmup, trials, false, true, false);
                double engineAllUs = MeasureSteadyState_Engine(v, warmup, trials, true, true, true);

                Console.WriteLine($"zones={v,4}  legacy={legacyUs,8:F2}us  engine(default)={engineDefaultUs,8:F2}us  " +
                                   $"engine(+isolate)={engineIsolateUs,8:F2}us  engine(all flags)={engineAllUs,8:F2}us");
                rows.Add($"{v},{legacyUs:F3},{engineDefaultUs:F3},{engineIsolateUs:F3},{engineAllUs:F3}");
            }

            File.WriteAllLines(Path.Combine(ResultsDir, "exp2_scaling_vs_size.csv"), rows, Encoding.UTF8);
        }

        private static double MeasureSteadyState_Legacy(int zoneCount, int warmup, int trials)
        {
            var agg = new LegacyAggregator();
            long baseId = 200_000;
            var actual = new ActualMount { SourceId = baseId + 1 };
            var spec = new Specification { SourceId = baseId + 2 };
            var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };
            var mount = new Mount
            {
                SourceId = baseId,
                ActualMountSourceId = actual.SourceId,
                SpecificationSourceId = spec.SourceId,
                SoftLimitsSourceId = softLimits.SourceId,
                InhibitZoneSourceIds = Enumerable.Range(0, zoneCount).Select(i => baseId + 100 + i).ToArray(),
            };

            agg.OnActualMount(actual);
            agg.OnSpecification(spec);
            agg.OnSoftLimits(softLimits);
            for (int i = 0; i < zoneCount; i++)
                agg.OnInhibitZone(new InhibitZone { SourceId = baseId + 100 + i, MountSourceId = baseId });
            agg.OnMount(mount);

            for (int i = 0; i < warmup; i++) agg.OnMount(mount);

            var sw = new Stopwatch();
            long ticks = 0;
            for (int i = 0; i < trials; i++)
            {
                sw.Restart();
                agg.OnMount(mount);
                sw.Stop();
                ticks += sw.ElapsedTicks;
            }
            return TicksToMicroseconds(ticks, trials);
        }

        private static double MeasureSteadyState_Engine(int zoneCount, int warmup, int trials, bool affectedOnly, bool isolate, bool suppress)
        {
            var h = new EngineHarness(affectedOnly, isolate, suppress);
            long baseId = 300_000;
            var actual = new ActualMount { SourceId = baseId + 1 };
            var spec = new Specification { SourceId = baseId + 2 };
            var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };
            var mount = new Mount
            {
                SourceId = baseId,
                ActualMountSourceId = actual.SourceId,
                SpecificationSourceId = spec.SourceId,
                SoftLimitsSourceId = softLimits.SourceId,
                InhibitZoneSourceIds = Enumerable.Range(0, zoneCount).Select(i => baseId + 100 + i).ToArray(),
            };

            h.Engine.Upsert(h.ActualMountKind, actual);
            h.Engine.Upsert(h.SpecificationKind, spec);
            h.Engine.Upsert(h.SoftLimitsKind, softLimits);
            for (int i = 0; i < zoneCount; i++)
                h.Engine.Upsert(h.InhibitZoneKind, new InhibitZone { SourceId = baseId + 100 + i, MountSourceId = baseId });
            h.Engine.Upsert(h.MountKind, mount);

            // suppress=true collapses steady-state notifications to zero, but
            // Assemble+IsComplete still run on every Upsert - that pipeline
            // cost is exactly what this experiment measures.
            for (int i = 0; i < warmup; i++) h.Engine.Upsert(h.MountKind, mount);

            var sw = new Stopwatch();
            long ticks = 0;
            for (int i = 0; i < trials; i++)
            {
                sw.Restart();
                h.Engine.Upsert(h.MountKind, mount);
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
            const int zoneCount = 5;

            var legacy = MeasureCompletionLatency_Legacy(trials, zoneCount);
            var engineDefault = MeasureCompletionLatency_Engine(trials, zoneCount, false, false, false);
            var engineAll = MeasureCompletionLatency_Engine(trials, zoneCount, true, true, true);

            Console.WriteLine($"legacy:            p50={legacy.p50,7:F2}us  p95={legacy.p95,7:F2}us");
            Console.WriteLine($"engine (default):  p50={engineDefault.p50,7:F2}us  p95={engineDefault.p95,7:F2}us");
            Console.WriteLine($"engine (all flags):p50={engineAll.p50,7:F2}us  p95={engineAll.p95,7:F2}us");

            var rows = new List<string>
            {
                "Config,P50_us,P95_us",
                $"Legacy,{legacy.p50:F3},{legacy.p95:F3}",
                $"Engine_Default,{engineDefault.p50:F3},{engineDefault.p95:F3}",
                $"Engine_AllFlags,{engineAll.p50:F3},{engineAll.p95:F3}",
            };
            File.WriteAllLines(Path.Combine(ResultsDir, "exp3_completion_latency.csv"), rows, Encoding.UTF8);
        }

        private static (double p50, double p95) MeasureCompletionLatency_Legacy(int trials, int zoneCount)
        {
            var agg = new LegacyAggregator();
            var samples = new List<double>(trials);
            var sw = new Stopwatch();

            for (int i = 0; i < trials; i++)
            {
                long baseId = 400_000 + (long)i * 1000;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var spec = new Specification { SourceId = baseId + 2 };
                var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };
                var mount = new Mount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                    InhibitZoneSourceIds = Enumerable.Range(0, zoneCount).Select(z => baseId + 100 + z).ToArray(),
                };

                agg.OnActualMount(actual);
                agg.OnSpecification(spec);
                agg.OnSoftLimits(softLimits);
                for (int z = 0; z < zoneCount; z++)
                    agg.OnInhibitZone(new InhibitZone { SourceId = baseId + 100 + z, MountSourceId = baseId });

                sw.Restart();
                agg.OnMount(mount); // completing call
                sw.Stop();
                samples.Add(TicksToMicroseconds(sw.ElapsedTicks, 1));
            }
            return Percentiles(samples);
        }

        private static (double p50, double p95) MeasureCompletionLatency_Engine(int trials, int zoneCount, bool affectedOnly, bool isolate, bool suppress)
        {
            var h = new EngineHarness(affectedOnly, isolate, suppress);
            var samples = new List<double>(trials);
            var sw = new Stopwatch();

            for (int i = 0; i < trials; i++)
            {
                long baseId = 500_000 + (long)i * 1000;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var spec = new Specification { SourceId = baseId + 2 };
                var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };
                var mount = new Mount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                    InhibitZoneSourceIds = Enumerable.Range(0, zoneCount).Select(z => baseId + 100 + z).ToArray(),
                };

                h.Engine.Upsert(h.ActualMountKind, actual);
                h.Engine.Upsert(h.SpecificationKind, spec);
                h.Engine.Upsert(h.SoftLimitsKind, softLimits);
                for (int z = 0; z < zoneCount; z++)
                    h.Engine.Upsert(h.InhibitZoneKind, new InhibitZone { SourceId = baseId + 100 + z, MountSourceId = baseId });

                sw.Restart();
                h.Engine.Upsert(h.MountKind, mount); // completing call
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
            var spec = new Specification { SourceId = baseId + 2 };
            var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };
            var mount = new Mount
            {
                SourceId = baseId,
                ActualMountSourceId = actual.SourceId,
                SpecificationSourceId = spec.SourceId,
                SoftLimitsSourceId = softLimits.SourceId,
            };

            var parts = new (string Name, Action<global::TopicManager.Extensions.AggregationEngine, EngineHarness> Upsert)[]
            {
                ("Mount", (e, h) => e.Upsert(h.MountKind, mount)),
                ("ActualMount", (e, h) => e.Upsert(h.ActualMountKind, actual)),
                ("Specification", (e, h) => e.Upsert(h.SpecificationKind, spec)),
                ("SoftLimits", (e, h) => e.Upsert(h.SoftLimitsKind, softLimits)),
            };

            int totalPermutations = 0;
            int okPermutations = 0;
            int earlyCompletions = 0;

            foreach (var perm in Permutations(new[] { 0, 1, 2, 3 }))
            {
                totalPermutations++;
                var h = new EngineHarness();
                int completions = 0;
                int completionsBeforeLast = 0;

                Action<RootId, AggregateSnapshot> onEmit = (root, snap) =>
                {
                    completions++;
                    bool hasAll =
                        snap.TryGetOne<Mount>(h.MountKind, out _) &&
                        snap.TryGetOne<ActualMount>(h.ActualMountKind, out _) &&
                        snap.TryGetOne<Specification>(h.SpecificationKind, out _) &&
                        snap.TryGetOne<SoftLimits>(h.SoftLimitsKind, out _);
                    if (!hasAll) Console.WriteLine("  !! incomplete snapshot notified");
                };
                h.Engine.SubscribeRootKind(h.MountKind, onEmit);

                for (int i = 0; i < perm.Length; i++)
                {
                    parts[perm[i]].Upsert(h.Engine, h);
                    if (i < perm.Length - 1 && completions > 0) completionsBeforeLast++;
                }

                if (completions == 1 && completionsBeforeLast == 0) okPermutations++;
                else earlyCompletions++;
            }

            Console.WriteLine($"permutations tested: {totalPermutations}");
            Console.WriteLine($"exactly-one-completion-at-the-end: {okPermutations}/{totalPermutations}");
            if (earlyCompletions > 0)
                Console.WriteLine($"  ANOMALIES: {earlyCompletions} permutation(s) completed early or more than once");

            File.WriteAllLines(Path.Combine(ResultsDir, "exp4_order_independence.csv"), new[]
            {
                "TotalPermutations,ExactlyOneCompletionAtEnd,Anomalies",
                $"{totalPermutations},{okPermutations},{earlyCompletions}",
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
