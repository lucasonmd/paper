using System;
using System.Collections.Generic;
using System.Linq;
using TopicManager.Extensions;

namespace AggregationEngine.CompletenessCheck
{
    // Conformance checks for the completeness rule:
    //
    //   A root is complete only when EVERY key it (transitively) references
    //   has arrived and, for bidirectional relations, reciprocates.
    //
    // Multiplicity governs how many references may exist; it does NOT excuse a
    // dangling one. An optional relation (ZeroOrOne / ZeroOrMany) is free to
    // reference nothing, but once it names a target that target must resolve.
    //
    // This distinction is what the existing samples do not exercise: they only
    // ever reference parts that have already been upserted, so a rule that
    // silently skipped unresolved keys and a rule that blocks on them produce
    // identical output. Every check below is written to fail under the old
    // "skip unresolved targets" behaviour or under a rule that over-blocks.
    internal static class Program
    {
        // ---- topic model -------------------------------------------------
        public sealed class Mount
        {
            public long SourceId;
            public long BaseId;                                   // One
            public long? OptionalId;                              // ZeroOrOne
            public long[] PartIds = Array.Empty<long>();          // ZeroOrMany
            public long[] RequiredPartIds = Array.Empty<long>();  // OneOrMany
        }

        public sealed class Base { public long SourceId; }
        public sealed class Optional { public long SourceId; public long MountId; }
        public sealed class Part { public long SourceId; public long MountId; }
        public sealed class ReqPart { public long SourceId; public long MountId; }

        private sealed class Harness
        {
            public readonly global::TopicManager.Extensions.AggregationEngine Engine = new();
            public readonly KindId MountKind, BaseKind, OptionalKind, PartKind, ReqPartKind;
            public int Emissions;

            public Harness(bool reciprocalOptional = true, bool reciprocalPart = true)
            {
                MountKind = Engine.RegisterKind<Mount>(m => m.SourceId);
                BaseKind = Engine.RegisterKind<Base>(b => b.SourceId);
                OptionalKind = Engine.RegisterKind<Optional>(o => o.SourceId);
                PartKind = Engine.RegisterKind<Part>(p => p.SourceId);
                ReqPartKind = Engine.RegisterKind<ReqPart>(p => p.SourceId);
                Engine.RegisterRootKind(MountKind);

                Engine.RegisterUnidirectional<Mount, Base>(
                    "Mount->Base", MountKind, BaseKind, Multiplicity.One,
                    m => global::TopicManager.Extensions.AggregationEngine.One(m.BaseId));

                if (reciprocalOptional)
                    Engine.RegisterBidirectional<Mount, Optional>(
                        "Mount<->Optional", MountKind, OptionalKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                        m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                                m.OptionalId.HasValue, m.OptionalId.GetValueOrDefault()),
                        o => global::TopicManager.Extensions.AggregationEngine.One(o.MountId));
                else
                    Engine.RegisterUnidirectional<Mount, Optional>(
                        "Mount->Optional", MountKind, OptionalKind, Multiplicity.ZeroOrOne,
                        m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                                m.OptionalId.HasValue, m.OptionalId.GetValueOrDefault()));

                if (reciprocalPart)
                    Engine.RegisterBidirectional<Mount, Part>(
                        "Mount<->Part", MountKind, PartKind, Multiplicity.ZeroOrMany, Multiplicity.One,
                        m => global::TopicManager.Extensions.AggregationEngine.Many(m.PartIds),
                        p => global::TopicManager.Extensions.AggregationEngine.One(p.MountId));
                else
                    Engine.RegisterUnidirectional<Mount, Part>(
                        "Mount->Part", MountKind, PartKind, Multiplicity.ZeroOrMany,
                        m => global::TopicManager.Extensions.AggregationEngine.Many(m.PartIds));

                Engine.RegisterUnidirectional<Mount, ReqPart>(
                    "Mount->ReqPart", MountKind, ReqPartKind, Multiplicity.OneOrMany,
                    m => global::TopicManager.Extensions.AggregationEngine.Many(m.RequiredPartIds));

                Engine.SubscribeRootKind(MountKind, (_, __) => Emissions++);
            }
        }

        private static int _failures;

        private static void Check(bool ok, string desc)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + desc);
            if (!ok) _failures++;
        }

        private static void Main()
        {
            Console.WriteLine("== completeness: every referenced key must resolve ==");
            ZeroOrMany_DanglingKeyBlocks();
            ZeroOrMany_ResolvesOnLateArrival();
            ZeroOrOne_DanglingKeyBlocks();
            OneOrMany_PartialSetBlocks();
            EmptyOptionalSetsStillComplete();
            RemovingAReferencedPartBlocks();
            BrokenReciprocalOnOptionalBlocks();
            OrderIndependenceWithManyParts();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            if (_failures > 0) Environment.Exit(1);
        }

        // Baseline aggregate: base + one required part, no optional members.
        private static void Complete(Harness h, long mountId = 1,
                                     long[]? parts = null, long? optional = null)
        {
            h.Engine.Upsert(h.BaseKind, new Base { SourceId = 10 });
            h.Engine.Upsert(h.ReqPartKind, new ReqPart { SourceId = 20, MountId = mountId });
            h.Engine.Upsert(h.MountKind, new Mount
            {
                SourceId = mountId,
                BaseId = 10,
                RequiredPartIds = new long[] { 20 },
                PartIds = parts ?? Array.Empty<long>(),
                OptionalId = optional,
            });
        }

        // ZeroOrMany may reference nothing - but a key it DOES name must exist.
        private static void ZeroOrMany_DanglingKeyBlocks()
        {
            var h = new Harness();
            h.Engine.Upsert(h.PartKind, new Part { SourceId = 30, MountId = 1 });
            Complete(h, parts: new long[] { 30, 31 });   // 31 never published
            Check(h.Emissions == 0,
                $"ZeroOrMany with one unresolved key does not notify (got {h.Emissions})");
        }

        private static void ZeroOrMany_ResolvesOnLateArrival()
        {
            var h = new Harness();
            h.Engine.Upsert(h.PartKind, new Part { SourceId = 30, MountId = 1 });
            Complete(h, parts: new long[] { 30, 31 });
            var before = h.Emissions;
            h.Engine.Upsert(h.PartKind, new Part { SourceId = 31, MountId = 1 });
            Check(before == 0 && h.Emissions == 1,
                $"the missing part arriving completes it exactly once (before={before}, after={h.Emissions})");
        }

        private static void ZeroOrOne_DanglingKeyBlocks()
        {
            var h = new Harness();
            Complete(h, optional: 40);                   // 40 never published
            var blocked = h.Emissions == 0;
            h.Engine.Upsert(h.OptionalKind, new Optional { SourceId = 40, MountId = 1 });
            Check(blocked && h.Emissions == 1,
                $"ZeroOrOne naming an absent target blocks, then completes (blocked={blocked}, after={h.Emissions})");
        }

        private static void OneOrMany_PartialSetBlocks()
        {
            var h = new Harness();
            h.Engine.Upsert(h.BaseKind, new Base { SourceId = 10 });
            h.Engine.Upsert(h.ReqPartKind, new ReqPart { SourceId = 20, MountId = 1 });
            h.Engine.Upsert(h.MountKind, new Mount
            {
                SourceId = 1, BaseId = 10,
                RequiredPartIds = new long[] { 20, 21 },  // 21 missing
            });
            var blocked = h.Emissions == 0;
            h.Engine.Upsert(h.ReqPartKind, new ReqPart { SourceId = 21, MountId = 1 });
            Check(blocked && h.Emissions == 1,
                $"OneOrMany with a partially resolved set blocks (blocked={blocked}, after={h.Emissions})");
        }

        // The rule must not over-block: referencing nothing is still valid.
        private static void EmptyOptionalSetsStillComplete()
        {
            var h = new Harness();
            Complete(h);
            Check(h.Emissions == 1,
                $"empty ZeroOrOne/ZeroOrMany sets remain complete (got {h.Emissions})");
        }

        private static void RemovingAReferencedPartBlocks()
        {
            var h = new Harness();
            h.Engine.Upsert(h.PartKind, new Part { SourceId = 30, MountId = 1 });
            Complete(h, parts: new long[] { 30 });
            var completed = h.Emissions == 1;

            h.Engine.Remove(h.PartKind, 30);             // mount still references 30
            var afterRemove = h.Emissions;
            h.Engine.Upsert(h.PartKind, new Part { SourceId = 30, MountId = 1 });
            Check(completed && afterRemove == 1 && h.Emissions == 2,
                $"removing a referenced part blocks until it returns " +
                $"(completed={completed}, afterRemove={afterRemove}, afterReAdd={h.Emissions})");
        }

        // A bidirectional optional relation whose target does not point back.
        private static void BrokenReciprocalOnOptionalBlocks()
        {
            var h = new Harness();
            h.Engine.Upsert(h.OptionalKind, new Optional { SourceId = 40, MountId = 999 }); // wrong mount
            Complete(h, optional: 40);
            var blocked = h.Emissions == 0;
            h.Engine.Upsert(h.OptionalKind, new Optional { SourceId = 40, MountId = 1 });   // fixed
            Check(blocked && h.Emissions == 1,
                $"a non-reciprocating optional target blocks (blocked={blocked}, after={h.Emissions})");
        }

        // Order independence must survive the stricter rule.
        private static void OrderIndependenceWithManyParts()
        {
            string[] names = { "base", "reqPart", "part", "mount" };
            int ok = 0, total = 0;
            foreach (var order in Permutations(names.ToList()))
            {
                total++;
                var h = new Harness();
                var seen = new List<int>();
                foreach (var step in order)
                {
                    switch (step)
                    {
                        case "base": h.Engine.Upsert(h.BaseKind, new Base { SourceId = 10 }); break;
                        case "reqPart": h.Engine.Upsert(h.ReqPartKind, new ReqPart { SourceId = 20, MountId = 1 }); break;
                        case "part": h.Engine.Upsert(h.PartKind, new Part { SourceId = 30, MountId = 1 }); break;
                        case "mount":
                            h.Engine.Upsert(h.MountKind, new Mount
                            {
                                SourceId = 1, BaseId = 10,
                                RequiredPartIds = new long[] { 20 },
                                PartIds = new long[] { 30 },
                            });
                            break;
                    }
                    seen.Add(h.Emissions);
                }
                // exactly one emission, and only after the final topic arrives
                if (h.Emissions == 1 && seen[^2] == 0) ok++;
            }
            Check(ok == total, $"order independence with a ZeroOrMany part: {ok}/{total} orderings");
        }

        private static IEnumerable<List<string>> Permutations(List<string> items)
        {
            if (items.Count <= 1) { yield return new List<string>(items); yield break; }
            for (int i = 0; i < items.Count; i++)
            {
                var rest = new List<string>(items);
                rest.RemoveAt(i);
                foreach (var tail in Permutations(rest))
                {
                    tail.Insert(0, items[i]);
                    yield return tail;
                }
            }
        }
    }
}
