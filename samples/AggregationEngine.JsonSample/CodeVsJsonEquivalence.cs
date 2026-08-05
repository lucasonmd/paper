using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using P_Mount_PSM;
using TopicManager.Extensions;

namespace AggregationEngine.JsonSample
{
    // Equivalence harness: registers the SAME Mount module two ways -
    //   (A) by hand, with the original generic Register* overloads and
    //       explicit lambdas. This is the REFERENCE: it is what the engine
    //       did before any JSON/reflection work existed, and its behavior
    //       must not change.
    //   (B) from Mount.module.json via JsonModuleLoader.
    // then drives both engines through an identical topic sequence and
    // asserts that every emission matches exactly - same order, same root,
    // same snapshot membership. Any divergence means the JSON path is not
    // a faithful stand-in for hand-written registration.
    internal static class CodeVsJsonEquivalence
    {
        // Same composite-identifier packing the reflection path uses by
        // default (AggregationEngine.DefaultCombineIdentifier), written out
        // by hand here so the reference side depends on nothing but the
        // original API.
        private static long Key(T_IdentifierType id) =>
            (id.A_resourceId << 32) | (id.A_instanceId & 0xFFFFFFFFL);

        private static bool IsNil(T_IdentifierType id) =>
            id.A_resourceId == 0 && id.A_instanceId == 0;

        // One emission, reduced to a canonical string so two engines'
        // outputs can be compared as plain sequences.
        private sealed class EmissionLog
        {
            private readonly List<string> _lines = new();
            public IReadOnlyList<string> Lines => _lines;

            public void Record(string rootKindName, RootId root, AggregateSnapshot snapshot,
                IReadOnlyDictionary<KindId, string> kindNames,
                IReadOnlyDictionary<KindId, Func<object, long>> keyOf)
            {
                var members = new List<string>();
                foreach (var (kind, list) in snapshot.Raw)
                {
                    var name = kindNames.TryGetValue(kind, out var n) ? n : $"kind#{kind.Value}";
                    foreach (var obj in list)
                        members.Add($"{name}#{keyOf[kind](obj)}");
                }
                members.Sort(StringComparer.Ordinal);
                _lines.Add($"{rootKindName}#{root.Key} :: {string.Join(", ", members)}");
            }
        }

        private sealed class Rig
        {
            public global::TopicManager.Extensions.AggregationEngine Engine = null!;
            public Dictionary<string, KindId> Kinds = new();
            public Dictionary<KindId, string> KindNames = new();
            public Dictionary<KindId, Func<object, long>> KeyOf = new();
            public EmissionLog Log = new();

            public KindId this[string name] => Kinds[name];

            public void SubscribeRoots(params string[] rootNames)
            {
                foreach (var rootName in rootNames)
                {
                    var captured = rootName;
                    Engine.SubscribeRootKind(Kinds[captured],
                        (root, snap) => Log.Record(captured, root, snap, KindNames, KeyOf));
                }
            }
        }

        // ---- (A) reference: hand-written registration -------------------
        private static Rig BuildByCode()
        {
            var rig = new Rig { Engine = new global::TopicManager.Extensions.AggregationEngine() };
            var e = rig.Engine;

            void Add<T>(string name, Func<T, long> keyOf) where T : class
            {
                var kind = e.RegisterKind(keyOf);
                rig.Kinds[name] = kind;
                rig.KindNames[kind] = name;
                rig.KeyOf[kind] = o => keyOf((T)o);
            }

            Add<C_Rotational_Mount>("C_Rotational_Mount", m => Key(m.A_sourceID));
            Add<C_Linear_Mount>("C_Linear_Mount", m => Key(m.A_sourceID));
            Add<C_Actual_Mount>("C_Actual_Mount", a => Key(a.A_sourceID));
            Add<C_Rotational_Mount_Specification>("C_Rotational_Mount_Specification", s => Key(s.A_sourceID));
            Add<C_Linear_Mount_Specification>("C_Linear_Mount_Specification", s => Key(s.A_sourceID));
            Add<C_Rotational_Soft_Limits>("C_Rotational_Soft_Limits", s => Key(s.A_sourceID));
            Add<C_Linear_Soft_Limits>("C_Linear_Soft_Limits", s => Key(s.A_sourceID));
            Add<C_Movement_Inhibit_Zone>("C_Movement_Inhibit_Zone", z => Key(z.A_sourceID));
            Add<C_Rotational_Target_Position>("C_Rotational_Target_Position", t => Key(t.A_sourceID));
            Add<C_Linear_Target_Position>("C_Linear_Target_Position", t => Key(t.A_sourceID));

            e.RegisterRootKind(rig["C_Rotational_Mount"]);
            e.RegisterRootKind(rig["C_Linear_Mount"]);

            e.RegisterUnidirectional<C_Rotational_Mount, C_Actual_Mount>(
                "C_Rotational_Mount-C_Actual_Mount", rig["C_Rotational_Mount"], rig["C_Actual_Mount"], Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(Key(m.A_Actual_Mount_sourceID)));

            e.RegisterUnidirectional<C_Linear_Mount, C_Actual_Mount>(
                "C_Linear_Mount-C_Actual_Mount", rig["C_Linear_Mount"], rig["C_Actual_Mount"], Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(Key(m.A_Actual_Mount_sourceID)));

            e.RegisterUnidirectional<C_Rotational_Mount, C_Rotational_Mount_Specification>(
                "C_Rotational_Mount-C_Rotational_Mount_Specification", rig["C_Rotational_Mount"], rig["C_Rotational_Mount_Specification"], Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(Key(m.A_specification_sourceID)));

            e.RegisterUnidirectional<C_Linear_Mount, C_Linear_Mount_Specification>(
                "C_Linear_Mount-C_Linear_Mount_Specification", rig["C_Linear_Mount"], rig["C_Linear_Mount_Specification"], Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(Key(m.A_specification_sourceID)));

            e.RegisterBidirectional<C_Rotational_Mount, C_Rotational_Soft_Limits>(
                "C_Rotational_Mount-C_Rotational_Soft_Limits", rig["C_Rotational_Mount"], rig["C_Rotational_Soft_Limits"],
                Multiplicity.One, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(Key(m.A_softLimits_sourceID)),
                s => global::TopicManager.Extensions.AggregationEngine.One(Key(s.A_rotationalMount_sourceID)));

            e.RegisterBidirectional<C_Linear_Mount, C_Linear_Soft_Limits>(
                "C_Linear_Mount-C_Linear_Soft_Limits", rig["C_Linear_Mount"], rig["C_Linear_Soft_Limits"],
                Multiplicity.One, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(Key(m.A_softLimits_sourceID)),
                s => global::TopicManager.Extensions.AggregationEngine.One(Key(s.A_linearMount_sourceID)));

            e.RegisterBidirectional<C_Rotational_Mount, C_Movement_Inhibit_Zone>(
                "C_Rotational_Mount-C_Movement_Inhibit_Zone", rig["C_Rotational_Mount"], rig["C_Movement_Inhibit_Zone"],
                Multiplicity.ZeroOrMany, Multiplicity.One,
                m => m.A_movementInhibitZones_sourceID.Select(Key),
                z => global::TopicManager.Extensions.AggregationEngine.One(Key(z.A_rotationalMount_sourceID)));

            e.RegisterBidirectional<C_Rotational_Mount, C_Rotational_Target_Position>(
                "C_Rotational_Mount-C_Rotational_Target_Position", rig["C_Rotational_Mount"], rig["C_Rotational_Target_Position"],
                Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        !IsNil(m.A_targetPosition_sourceID), Key(m.A_targetPosition_sourceID)),
                t => global::TopicManager.Extensions.AggregationEngine.One(Key(t.A_rotationalMount_sourceID)));

            e.RegisterBidirectional<C_Linear_Mount, C_Linear_Target_Position>(
                "C_Linear_Mount-C_Linear_Target_Position", rig["C_Linear_Mount"], rig["C_Linear_Target_Position"],
                Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        !IsNil(m.A_targetPosition_sourceID), Key(m.A_targetPosition_sourceID)),
                t => global::TopicManager.Extensions.AggregationEngine.One(Key(t.A_linearMount_sourceID)));

            return rig;
        }

        // ---- (B) same module, loaded from JSON --------------------------
        private static Rig BuildByJson(string jsonPath)
        {
            var rig = new Rig { Engine = new global::TopicManager.Extensions.AggregationEngine() };
            var kinds = JsonModuleLoader.LoadFile(rig.Engine, jsonPath);

            rig.Kinds = kinds.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var (name, kind) in rig.Kinds) rig.KindNames[kind] = name;

            // Key accessors for logging only - the engine already has its
            // own (built by the reflection path); these just have to agree
            // with the reference side's canonical form.
            rig.KeyOf[rig["C_Rotational_Mount"]] = o => Key(((C_Rotational_Mount)o).A_sourceID);
            rig.KeyOf[rig["C_Linear_Mount"]] = o => Key(((C_Linear_Mount)o).A_sourceID);
            rig.KeyOf[rig["C_Actual_Mount"]] = o => Key(((C_Actual_Mount)o).A_sourceID);
            rig.KeyOf[rig["C_Rotational_Mount_Specification"]] = o => Key(((C_Rotational_Mount_Specification)o).A_sourceID);
            rig.KeyOf[rig["C_Linear_Mount_Specification"]] = o => Key(((C_Linear_Mount_Specification)o).A_sourceID);
            rig.KeyOf[rig["C_Rotational_Soft_Limits"]] = o => Key(((C_Rotational_Soft_Limits)o).A_sourceID);
            rig.KeyOf[rig["C_Linear_Soft_Limits"]] = o => Key(((C_Linear_Soft_Limits)o).A_sourceID);
            rig.KeyOf[rig["C_Movement_Inhibit_Zone"]] = o => Key(((C_Movement_Inhibit_Zone)o).A_sourceID);
            rig.KeyOf[rig["C_Rotational_Target_Position"]] = o => Key(((C_Rotational_Target_Position)o).A_sourceID);
            rig.KeyOf[rig["C_Linear_Target_Position"]] = o => Key(((C_Linear_Target_Position)o).A_sourceID);

            return rig;
        }

        // ---- identical workload fed to both engines ---------------------
        // Deliberately exercises every multiplicity and both presence
        // conventions, plus out-of-order arrival and a Kind shared by two
        // aggregates - i.e. all the cases where a faithful reimplementation
        // could plausibly diverge.
        private static void DriveScenario(Rig rig)
        {
            var e = rig.Engine;

            var sharedActual = new T_IdentifierType(1, 1);
            var rotMount = new T_IdentifierType(10, 1);
            var rotSpec = new T_IdentifierType(11, 1);
            var rotSoft = new T_IdentifierType(12, 1);
            var rotTarget = new T_IdentifierType(13, 1);
            var zone1 = new T_IdentifierType(14, 1);
            var zone2 = new T_IdentifierType(14, 2);
            var linMount = new T_IdentifierType(20, 1);
            var linSpec = new T_IdentifierType(21, 1);
            var linSoft = new T_IdentifierType(22, 1);

            // Parts arrive before their root (nothing should complete yet).
            e.Upsert(rig["C_Actual_Mount"], new C_Actual_Mount { A_sourceID = sharedActual });
            e.Upsert(rig["C_Rotational_Mount_Specification"], new C_Rotational_Mount_Specification { A_sourceID = rotSpec });
            e.Upsert(rig["C_Rotational_Soft_Limits"], new C_Rotational_Soft_Limits { A_sourceID = rotSoft, A_rotationalMount_sourceID = rotMount });

            // Root arrives with a NIL optional and no zones -> completes.
            e.Upsert(rig["C_Rotational_Mount"], new C_Rotational_Mount
            {
                A_sourceID = rotMount,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = rotSpec,
                A_softLimits_sourceID = rotSoft,
                A_targetPosition_sourceID = T_IdentifierType.Nil,
            });

            // Optional becomes present.
            e.Upsert(rig["C_Rotational_Target_Position"], new C_Rotational_Target_Position { A_sourceID = rotTarget, A_rotationalMount_sourceID = rotMount });
            e.Upsert(rig["C_Rotational_Mount"], new C_Rotational_Mount
            {
                A_sourceID = rotMount,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = rotSpec,
                A_softLimits_sourceID = rotSoft,
                A_targetPosition_sourceID = rotTarget,
            });

            // ZeroOrMany grows to two.
            e.Upsert(rig["C_Movement_Inhibit_Zone"], new C_Movement_Inhibit_Zone { A_sourceID = zone1, A_rotationalMount_sourceID = rotMount });
            e.Upsert(rig["C_Movement_Inhibit_Zone"], new C_Movement_Inhibit_Zone { A_sourceID = zone2, A_rotationalMount_sourceID = rotMount });
            e.Upsert(rig["C_Rotational_Mount"], new C_Rotational_Mount
            {
                A_sourceID = rotMount,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = rotSpec,
                A_softLimits_sourceID = rotSoft,
                A_targetPosition_sourceID = rotTarget,
                A_movementInhibitZones_sourceID = new[] { zone1, zone2 },
            });

            // Second aggregate, reusing the shared C_Actual_Mount instance.
            e.Upsert(rig["C_Linear_Mount_Specification"], new C_Linear_Mount_Specification { A_sourceID = linSpec });
            e.Upsert(rig["C_Linear_Soft_Limits"], new C_Linear_Soft_Limits { A_sourceID = linSoft, A_linearMount_sourceID = linMount });
            e.Upsert(rig["C_Linear_Mount"], new C_Linear_Mount
            {
                A_sourceID = linMount,
                A_Actual_Mount_sourceID = sharedActual,
                A_specification_sourceID = linSpec,
                A_softLimits_sourceID = linSoft,
                A_targetPosition_sourceID = T_IdentifierType.Nil,
            });

            // Removal path.
            e.Remove(rig["C_Movement_Inhibit_Zone"], Key(zone2));
        }

        public static int Run(string jsonPath)
        {
            Console.WriteLine();
            Console.WriteLine("=== Equivalence: hand-written code (reference) vs JSON-driven ===");

            var byCode = BuildByCode();
            byCode.SubscribeRoots("C_Rotational_Mount", "C_Linear_Mount");
            DriveScenario(byCode);

            var byJson = BuildByJson(jsonPath);
            byJson.SubscribeRoots("C_Rotational_Mount", "C_Linear_Mount");
            DriveScenario(byJson);

            var codeLines = byCode.Log.Lines;
            var jsonLines = byJson.Log.Lines;

            Console.WriteLine($"  reference (code) emissions: {codeLines.Count}");
            Console.WriteLine($"  JSON-driven emissions:      {jsonLines.Count}");

            var failures = 0;
            if (codeLines.Count != jsonLines.Count)
            {
                Console.WriteLine($"  FAIL  emission COUNT differs ({codeLines.Count} vs {jsonLines.Count})");
                failures++;
            }

            var max = Math.Max(codeLines.Count, jsonLines.Count);
            var mismatches = 0;
            for (int i = 0; i < max; i++)
            {
                var c = i < codeLines.Count ? codeLines[i] : "<none>";
                var j = i < jsonLines.Count ? jsonLines[i] : "<none>";
                if (!string.Equals(c, j, StringComparison.Ordinal))
                {
                    if (mismatches < 10)
                    {
                        Console.WriteLine($"  FAIL  emission #{i + 1} differs:");
                        Console.WriteLine($"          code: {c}");
                        Console.WriteLine($"          json: {j}");
                    }
                    mismatches++;
                }
            }

            if (mismatches > 0)
            {
                Console.WriteLine($"  FAIL  {mismatches} emission(s) differ");
                failures++;
            }
            else if (codeLines.Count == jsonLines.Count)
            {
                Console.WriteLine($"  PASS  all {codeLines.Count} emissions identical (same order, root and snapshot membership)");
            }

            if (codeLines.Count == 0)
            {
                Console.WriteLine("  FAIL  reference produced no emissions - the scenario proves nothing");
                failures++;
            }

            Console.WriteLine();
            Console.WriteLine("  --- reference emission sequence ---");
            for (int i = 0; i < codeLines.Count; i++)
                Console.WriteLine($"    {i + 1,2}. {codeLines[i]}");

            return failures;
        }
    }
}
