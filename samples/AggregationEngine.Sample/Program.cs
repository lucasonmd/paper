using System;
using TopicManager.Extensions;

namespace AggregationEngine.Sample
{
    // Minimal end-to-end usage sample: registers the C_Rotational_Mount
    // aggregate described in the paper, subscribes to completed snapshots,
    // and feeds topics in an order that only completes the aggregate once
    // every required part has arrived.
    internal static class Program
    {
        private static void Main()
        {
            var engine = new global::TopicManager.Extensions.AggregationEngine();

            var mountKind = engine.RegisterKind<C_Rotational_Mount>(m => m.A_sourceID);
            var specKind = engine.RegisterKind<C_Rotational_Mount_Specification>(s => s.A_sourceID);
            var actualKind = engine.RegisterKind<C_Actual_Mount>(a => a.A_sourceID);
            var softLimitsKind = engine.RegisterKind<C_Rotational_Soft_Limits>(s => s.A_sourceID);
            var zoneKind = engine.RegisterKind<C_Movement_Inhibit_Zone>(z => z.A_sourceID);
            var targetKind = engine.RegisterKind<C_Rotational_Target_Position>(t => t.A_sourceID);

            engine.RegisterRootKind(mountKind);

            // Specialization: Mount -> ActualMount (base), multiplicity 1.
            engine.RegisterUnidirectional<C_Rotational_Mount, C_Actual_Mount>(
                "Mount->ActualMount", mountKind, actualKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.A_Actual_Mount_sourceID));

            // Mount -> Specification, multiplicity 1.
            engine.RegisterUnidirectional<C_Rotational_Mount, C_Rotational_Mount_Specification>(
                "Mount->Specification", mountKind, specKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.A_specification_sourceID));
            engine.RegisterUnidirectional<C_Rotational_Mount_Specification, C_Rotational_Mount>(
                "Specification->Mount", specKind, mountKind, Multiplicity.One,
                s => global::TopicManager.Extensions.AggregationEngine.One(s.A_rotationalMount_sourceID));

            // Mount <-> SoftLimits, multiplicity 1 / 1 (bidirectional, reciprocal-checked).
            engine.RegisterBidirectional<C_Rotational_Mount, C_Rotational_Soft_Limits>(
                "Mount<->SoftLimits", mountKind, softLimitsKind, Multiplicity.One, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.A_softLimits_sourceID),
                s => global::TopicManager.Extensions.AggregationEngine.One(s.A_rotationalMount_sourceID));

            // Mount <-> InhibitZone, multiplicity 0..* / 1 (bidirectional).
            engine.RegisterBidirectional<C_Rotational_Mount, C_Movement_Inhibit_Zone>(
                "Mount<->InhibitZone", mountKind, zoneKind, Multiplicity.ZeroOrMany, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.Many(m.A_movementInhibitZones_sourceID),
                z => global::TopicManager.Extensions.AggregationEngine.One(z.A_rotationalMount_sourceID));

            // Mount <-> TargetPosition, multiplicity 0..1 / 1 (bidirectional).
            engine.RegisterBidirectional<C_Rotational_Mount, C_Rotational_Target_Position>(
                "Mount<->TargetPosition", mountKind, targetKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.A_targetPosition_sourceID.HasValue, m.A_targetPosition_sourceID.GetValueOrDefault()),
                t => global::TopicManager.Extensions.AggregationEngine.One(t.A_rotationalMount_sourceID));

            // Typed subscription: MountAggregate's fields are filled in
            // automatically from the snapshot, one field per registered
            // Kind - no manual TryGetMany/TryGetOne calls in the handler.
            engine.SubscribeRootKind<MountAggregate>(mountKind, (root, agg) =>
            {
                Console.WriteLine(
                    $"[emit-typed] root=Mount#{root.Key} mount={(agg.Mount != null ? 1 : 0)} " +
                    $"spec={(agg.Specification != null ? 1 : 0)} softLimits={(agg.SoftLimits != null ? 1 : 0)} " +
                    $"targetPosition={(agg.TargetPosition != null ? 1 : 0)} zones={agg.InhibitZones?.Count ?? 0}");
            });

            Console.WriteLine("-- feeding parts before the mount: expect no emission yet --");
            engine.Upsert(specKind, new C_Rotational_Mount_Specification { A_sourceID = 100, A_rotationalMount_sourceID = 1 });
            engine.Upsert(softLimitsKind, new C_Rotational_Soft_Limits { A_sourceID = 200, A_rotationalMount_sourceID = 1 });
            engine.Upsert(actualKind, new C_Actual_Mount { A_sourceID = 300 });

            Console.WriteLine("-- feeding the mount itself: aggregate should complete now --");
            engine.Upsert(mountKind, new C_Rotational_Mount
            {
                A_sourceID = 1,
                A_Actual_Mount_sourceID = 300,
                A_specification_sourceID = 100,
                A_softLimits_sourceID = 200,
            });

            Console.WriteLine("-- adding an optional inhibit zone: expect a re-emission with zones=1 --");
            engine.Upsert(zoneKind, new C_Movement_Inhibit_Zone { A_sourceID = 400, A_rotationalMount_sourceID = 1 });
            engine.Upsert(mountKind, new C_Rotational_Mount
            {
                A_sourceID = 1,
                A_Actual_Mount_sourceID = 300,
                A_specification_sourceID = 100,
                A_softLimits_sourceID = 200,
                A_movementInhibitZones_sourceID = new long[] { 400 },
            });

            Console.WriteLine("-- removing the zone: cleans up without forcing a re-emission --");
            engine.Remove(zoneKind, 400);

            Console.WriteLine("-- done --");
        }
    }
}
