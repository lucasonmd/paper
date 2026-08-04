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

            var mountKind = engine.RegisterKind<RotationalMount>(m => m.SourceId);
            var specKind = engine.RegisterKind<RotationalMountSpecification>(s => s.SourceId);
            var actualKind = engine.RegisterKind<ActualMount>(a => a.SourceId);
            var softLimitsKind = engine.RegisterKind<RotationalSoftLimits>(s => s.SourceId);
            var zoneKind = engine.RegisterKind<MovementInhibitZone>(z => z.SourceId);
            var targetKind = engine.RegisterKind<RotationalTargetPosition>(t => t.SourceId);

            engine.RegisterRootKind(mountKind);

            // Specialization: Mount -> ActualMount (base), multiplicity 1.
            engine.RegisterUnidirectional<RotationalMount, ActualMount>(
                "Mount->ActualMount", mountKind, actualKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.ActualMountSourceId));

            // Mount -> Specification, multiplicity 1.
            engine.RegisterUnidirectional<RotationalMount, RotationalMountSpecification>(
                "Mount->Specification", mountKind, specKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.SpecificationSourceId));
            engine.RegisterUnidirectional<RotationalMountSpecification, RotationalMount>(
                "Specification->Mount", specKind, mountKind, Multiplicity.One,
                s => global::TopicManager.Extensions.AggregationEngine.One(s.RotationalMountSourceId));

            // Mount <-> SoftLimits, multiplicity 1 / 1 (bidirectional, reciprocal-checked).
            engine.RegisterBidirectional<RotationalMount, RotationalSoftLimits>(
                "Mount<->SoftLimits", mountKind, softLimitsKind, Multiplicity.One, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.SoftLimitsSourceId),
                s => global::TopicManager.Extensions.AggregationEngine.One(s.RotationalMountSourceId));

            // Mount <-> InhibitZone, multiplicity 0..* / 1 (bidirectional).
            engine.RegisterBidirectional<RotationalMount, MovementInhibitZone>(
                "Mount<->InhibitZone", mountKind, zoneKind, Multiplicity.ZeroOrMany, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.Many(m.InhibitZoneSourceIds),
                z => global::TopicManager.Extensions.AggregationEngine.One(z.RotationalMountSourceId));

            // Mount <-> TargetPosition, multiplicity 0..1 / 1 (bidirectional).
            engine.RegisterBidirectional<RotationalMount, RotationalTargetPosition>(
                "Mount<->TargetPosition", mountKind, targetKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.TargetPositionSourceId.HasValue, m.TargetPositionSourceId.GetValueOrDefault()),
                t => global::TopicManager.Extensions.AggregationEngine.One(t.RotationalMountSourceId));

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
            engine.Upsert(specKind, new RotationalMountSpecification { SourceId = 100, RotationalMountSourceId = 1 });
            engine.Upsert(softLimitsKind, new RotationalSoftLimits { SourceId = 200, RotationalMountSourceId = 1 });
            engine.Upsert(actualKind, new ActualMount { SourceId = 300 });

            Console.WriteLine("-- feeding the mount itself: aggregate should complete now --");
            engine.Upsert(mountKind, new RotationalMount
            {
                SourceId = 1,
                ActualMountSourceId = 300,
                SpecificationSourceId = 100,
                SoftLimitsSourceId = 200,
            });

            Console.WriteLine("-- adding an optional inhibit zone: expect a re-emission with zones=1 --");
            engine.Upsert(zoneKind, new MovementInhibitZone { SourceId = 400, RotationalMountSourceId = 1 });
            engine.Upsert(mountKind, new RotationalMount
            {
                SourceId = 1,
                ActualMountSourceId = 300,
                SpecificationSourceId = 100,
                SoftLimitsSourceId = 200,
                InhibitZoneSourceIds = new long[] { 400 },
            });

            Console.WriteLine("-- removing the zone: cleans up without forcing a re-emission --");
            engine.Remove(zoneKind, 400);

            Console.WriteLine("-- done --");
        }
    }
}
