using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TopicManager.Extensions;

namespace AggregationEngine.Benchmarks
{
    // Two additions prompted by review of how EXP3 was being reported:
    //
    //  (1) Timer resolution. EXP3's legacy baseline came out at exactly
    //      p50=0.100us / p95=0.500us on every single run - suspiciously
    //      round. If those are 1 and 5 Stopwatch ticks, the baseline is
    //      sitting ON the timer's resolution floor, which means the
    //      headline "~90x" ratio is computed against a number the harness
    //      cannot actually resolve. Worth knowing before quoting a ratio.
    //
    //  (2) Sustained throughput. A ratio between two sub-millisecond
    //      figures says nothing about whether the cost matters; completed
    //      aggregates per second against a real workload does. Measured
    //      here as a sustained loop rather than inferred as 1/latency,
    //      since the two differ once allocation and GC are in play.
    //
    //      RESULT (recorded honestly): this did NOT produce a quotable
    //      figure. Three consecutive runs on the same machine gave
    //      13,620 / 17,921 / 30,765 aggregates/sec for engine(default) -
    //      a 2.3x spread, and monotonically increasing, which points at
    //      JIT tiering / CPU boost / growing store size rather than a
    //      settled steady state. Kept in the repo because the measurement
    //      itself is useful infrastructure and the instability is worth
    //      knowing, but DO NOT quote these numbers in the paper. The
    //      per-operation costs in EXP2 (reproduce within ~10%) and the
    //      EXP3 p50 (~14% spread) remain the defensible figures.
    public static class ThroughputAndResolution
    {
        public static void Run(string resultsDir)
        {
            Console.WriteLine("==================================================================");
            Console.WriteLine(" Experiment 6: timer resolution (does the EXP3 baseline bottom out?)");
            Console.WriteLine("==================================================================");
            ReportResolution();

            Console.WriteLine();
            Console.WriteLine("==================================================================");
            Console.WriteLine(" Experiment 7: sustained completion throughput");
            Console.WriteLine("==================================================================");
            RunThroughput(resultsDir);
        }

        private static void ReportResolution()
        {
            double tickUs = 1_000_000.0 / Stopwatch.Frequency;
            Console.WriteLine($"Stopwatch.Frequency      : {Stopwatch.Frequency:N0} Hz");
            Console.WriteLine($"one tick                 : {tickUs:F4} us");
            Console.WriteLine($"Stopwatch.IsHighResolution: {Stopwatch.IsHighResolution}");
            Console.WriteLine($"EXP3 legacy p50 (0.100us) : {0.100 / tickUs:F2} tick(s)");
            Console.WriteLine($"EXP3 legacy p95 (0.500us) : {0.500 / tickUs:F2} tick(s)");
            Console.WriteLine($"EXP3 engine p50 (~10us)   : {10.0 / tickUs:F2} tick(s)");

            // Empirical floor: how long does an empty Restart/Stop pair
            // measure as? Anything at or below this is unresolvable.
            var sw = new Stopwatch();
            var samples = new List<double>(20000);
            for (int i = 0; i < 20000; i++)
            {
                sw.Restart();
                sw.Stop();
                samples.Add(sw.ElapsedTicks * tickUs);
            }
            samples.Sort();
            Console.WriteLine($"empty measurement p50     : {samples[samples.Count / 2]:F4} us  (harness noise floor)");
            Console.WriteLine($"empty measurement p95     : {samples[(int)(samples.Count * 0.95)]:F4} us");
        }

        private static void RunThroughput(string resultsDir)
        {
            const int aggregates = 20000;
            const int zoneCount = 5;

            double legacyPerSec = MeasureLegacyThroughput(aggregates, zoneCount);
            double engineDefaultPerSec = MeasureEngineThroughput(aggregates, zoneCount, false, false, false);
            double engineAllFlagsPerSec = MeasureEngineThroughput(aggregates, zoneCount, true, true, true);

            Console.WriteLine($"legacy              : {legacyPerSec,12:N0} completed aggregates/sec (1 thread)");
            Console.WriteLine($"engine (default)    : {engineDefaultPerSec,12:N0} completed aggregates/sec (1 thread)");
            Console.WriteLine($"engine (all flags)  : {engineAllFlagsPerSec,12:N0} completed aggregates/sec (1 thread)");

            File.WriteAllLines(Path.Combine(resultsDir, "exp7_throughput.csv"), new[]
            {
                "Config,CompletedAggregatesPerSecond",
                $"Legacy,{legacyPerSec:F0}",
                $"Engine_Default,{engineDefaultPerSec:F0}",
                $"Engine_AllFlags,{engineAllFlagsPerSec:F0}",
            }, Encoding.UTF8);
        }

        // Each iteration builds one complete, independent aggregate (root +
        // base + spec + soft limits + N zones) and counts it only once the
        // subscriber actually fires - so this measures end-to-end completed
        // aggregates, not raw Upsert calls.
        private static double MeasureEngineThroughput(int aggregates, int zoneCount, bool affectedOnly, bool isolate, bool suppress)
        {
            var h = new EngineHarness(affectedOnly, isolate, suppress);
            long completed = 0;
            Action<RootId, AggregateSnapshot> onEmit = (_, __) => completed++;
            h.Engine.SubscribeRootKind(h.MountKind, onEmit);

            // Warm up JIT and let the allocator settle before timing.
            FeedEngineAggregates(h, 0, 200, zoneCount);
            completed = 0;

            var sw = Stopwatch.StartNew();
            FeedEngineAggregates(h, 1_000_000, aggregates, zoneCount);
            sw.Stop();

            if (completed < aggregates)
                Console.WriteLine($"  (warning: only {completed}/{aggregates} aggregates completed)");

            return completed / sw.Elapsed.TotalSeconds;
        }

        private static void FeedEngineAggregates(EngineHarness h, long idBase, int count, int zoneCount)
        {
            for (int i = 0; i < count; i++)
            {
                long baseId = idBase + (long)i * 1000;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var spec = new Specification { SourceId = baseId + 2 };
                var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };

                h.Engine.Upsert(h.ActualMountKind, actual);
                h.Engine.Upsert(h.SpecificationKind, spec);
                h.Engine.Upsert(h.SoftLimitsKind, softLimits);
                for (int z = 0; z < zoneCount; z++)
                    h.Engine.Upsert(h.InhibitZoneKind, new InhibitZone { SourceId = baseId + 100 + z, MountSourceId = baseId });

                h.Engine.Upsert(h.MountKind, new Mount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                    InhibitZoneSourceIds = Enumerable.Range(0, zoneCount).Select(z => baseId + 100 + z).ToArray(),
                });
            }
        }

        private static double MeasureLegacyThroughput(int aggregates, int zoneCount)
        {
            var agg = new LegacyAggregator();
            long completed = 0;
            agg.OnMountComplete += _ => completed++;

            FeedLegacyAggregates(agg, 0, 200, zoneCount);
            completed = 0;

            var sw = Stopwatch.StartNew();
            FeedLegacyAggregates(agg, 2_000_000, aggregates, zoneCount);
            sw.Stop();

            if (completed < aggregates)
                Console.WriteLine($"  (warning: only {completed}/{aggregates} aggregates completed)");

            return completed / sw.Elapsed.TotalSeconds;
        }

        private static void FeedLegacyAggregates(LegacyAggregator agg, long idBase, int count, int zoneCount)
        {
            for (int i = 0; i < count; i++)
            {
                long baseId = idBase + (long)i * 1000;
                var actual = new ActualMount { SourceId = baseId + 1 };
                var spec = new Specification { SourceId = baseId + 2 };
                var softLimits = new SoftLimits { SourceId = baseId + 3, MountSourceId = baseId };

                agg.OnActualMount(actual);
                agg.OnSpecification(spec);
                agg.OnSoftLimits(softLimits);
                for (int z = 0; z < zoneCount; z++)
                    agg.OnInhibitZone(new InhibitZone { SourceId = baseId + 100 + z, MountSourceId = baseId });

                agg.OnMount(new Mount
                {
                    SourceId = baseId,
                    ActualMountSourceId = actual.SourceId,
                    SpecificationSourceId = spec.SourceId,
                    SoftLimitsSourceId = softLimits.SourceId,
                    InhibitZoneSourceIds = Enumerable.Range(0, zoneCount).Select(z => baseId + 100 + z).ToArray(),
                });
            }
        }
    }
}
