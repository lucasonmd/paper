using System;
using TopicManager.Extensions;

namespace AggregationEngine.Sample
{
    // Minimal end-to-end usage sample: registers the C_Linear_Mount
    // aggregate described in the paper (AEP-4754 Vol V, Fig. 6), subscribes
    // to completed snapshots, and feeds topics in an order that only
    // completes the aggregate once every required part has arrived.
    //
    // Required parts, per Fig. 6, are the ones with multiplicity 1:
    // C_Actual_Mount and C_Linear_Mount_Specification. Soft limits, target
    // position and the synthetic parts are optional and appear in the
    // snapshot only when present.
    internal static class Program
    {
        private static void Main()
        {
            var engine = new global::TopicManager.Extensions.AggregationEngine();

            var mountKind = engine.RegisterKind<C_Linear_Mount>(m => m.A_sourceID);
            var specKind = engine.RegisterKind<C_Linear_Mount_Specification>(s => s.A_sourceID);
            var actualKind = engine.RegisterKind<C_Actual_Mount>(a => a.A_sourceID);
            var softLimitsKind = engine.RegisterKind<C_Linear_Soft_Limits>(s => s.A_sourceID);
            var partKind = engine.RegisterKind<C_Mount_Part>(p => p.A_sourceID);
            var targetKind = engine.RegisterKind<C_Linear_Target_Position>(t => t.A_sourceID);

            engine.RegisterRootKind(mountKind);

            // Generalization: Linear_Mount --|> Actual_Mount, multiplicity 1.
            // Unidirectional - the base topic carries no pointer back to the
            // specialization.
            engine.RegisterUnidirectional<C_Linear_Mount, C_Actual_Mount>(
                "LinearMount->ActualMount", mountKind, actualKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.A_Actual_Mount_sourceID));

            // Mount -> Specification, multiplicity 1. Unidirectional by
            // design: Fig. 6 gives the far end as specifiedLinearMounts 0..*
            // (Shared Aggregation), so the specification has no single mount
            // to point back at and no reciprocal check is possible.
            engine.RegisterUnidirectional<C_Linear_Mount, C_Linear_Mount_Specification>(
                "LinearMount->Specification", mountKind, specKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.A_specification_sourceID));

            // Mount <-> SoftLimits, multiplicity 0..1 / 1 (bidirectional, reciprocal-checked).
            engine.RegisterBidirectional<C_Linear_Mount, C_Linear_Soft_Limits>(
                "LinearMount<->SoftLimits", mountKind, softLimitsKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.A_softLimits_sourceID.HasValue, m.A_softLimits_sourceID.GetValueOrDefault()),
                s => global::TopicManager.Extensions.AggregationEngine.One(s.A_linearMount_sourceID));

            // Mount <-> Part, multiplicity 0..* / 1 (bidirectional).
            // Synthetic - see the note in MountTopics.cs.
            engine.RegisterBidirectional<C_Linear_Mount, C_Mount_Part>(
                "LinearMount<->Part", mountKind, partKind, Multiplicity.ZeroOrMany, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.Many(m.A_parts_sourceID),
                p => global::TopicManager.Extensions.AggregationEngine.One(p.A_linearMount_sourceID));

            // Mount <-> TargetPosition, multiplicity 0..1 / 1 (bidirectional).
            engine.RegisterBidirectional<C_Linear_Mount, C_Linear_Target_Position>(
                "LinearMount<->TargetPosition", mountKind, targetKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.A_targetPosition_sourceID.HasValue, m.A_targetPosition_sourceID.GetValueOrDefault()),
                t => global::TopicManager.Extensions.AggregationEngine.One(t.A_linearMount_sourceID));

            // Typed subscription: LinearMountAggregate's fields are filled in
            // automatically from the snapshot, one field per registered
            // Kind - no manual TryGetMany/TryGetOne calls in the handler.
            engine.SubscribeRootKind<LinearMountAggregate>(mountKind, (root, agg) =>
            {
                Console.WriteLine(
                    $"[emit-typed] root=LinearMount#{root.Key} mount={(agg.Mount != null ? 1 : 0)} " +
                    $"spec={(agg.Specification != null ? 1 : 0)} softLimits={(agg.SoftLimits != null ? 1 : 0)} " +
                    $"targetPosition={(agg.TargetPosition != null ? 1 : 0)} parts={agg.Parts?.Count ?? 0}");
            });

            Console.WriteLine("-- feeding parts before the mount: expect no emission yet --");
            engine.Upsert(specKind, new C_Linear_Mount_Specification { A_sourceID = 100 });
            engine.Upsert(softLimitsKind, new C_Linear_Soft_Limits { A_sourceID = 200, A_linearMount_sourceID = 1 });
            engine.Upsert(actualKind, new C_Actual_Mount { A_sourceID = 300 });

            Console.WriteLine("-- feeding the mount itself: aggregate should complete now --");
            engine.Upsert(mountKind, new C_Linear_Mount
            {
                A_sourceID = 1,
                A_Actual_Mount_sourceID = 300,
                A_specification_sourceID = 100,
                A_softLimits_sourceID = 200,
            });

            Console.WriteLine("-- adding an optional part: expect a re-emission with parts=1 --");
            engine.Upsert(partKind, new C_Mount_Part { A_sourceID = 400, A_linearMount_sourceID = 1 });
            engine.Upsert(mountKind, new C_Linear_Mount
            {
                A_sourceID = 1,
                A_Actual_Mount_sourceID = 300,
                A_specification_sourceID = 100,
                A_softLimits_sourceID = 200,
                A_parts_sourceID = new long[] { 400 },
            });

            // Completeness requires every referenced key to resolve, so removing
            // a part the mount still points at takes the aggregate OUT of the
            // complete state - no emission, and no partial snapshot either.
            Console.WriteLine("-- removing the part while the mount still references it: aggregate goes incomplete --");
            engine.Remove(partKind, 400);

            // The publisher's side of that: drop the reference too, and the
            // aggregate is complete again.
            Console.WriteLine("-- republishing the mount without that reference: complete again --");
            engine.Upsert(mountKind, new C_Linear_Mount
            {
                A_sourceID = 1,
                A_Actual_Mount_sourceID = 300,
                A_specification_sourceID = 100,
                A_softLimits_sourceID = 200,
            });

            // Shared Aggregation in action: a second mount referencing the
            // SAME specification instance completes without the specification
            // being republished. Note the extra emission for mount #1 that
            // follows: with the default settings the shared specification
            // node makes mount #1 reachable from mount #2's update, so it is
            // re-notified as well, even though mount #1's own snapshot did
            // not change. EmitOnlyAffectedRoots filters exactly that case -
            // it is measured in benchmarks/AggregationEngine.Benchmarks EXP1.
            Console.WriteLine("-- a second mount sharing specification #100 (watch the shared-spec fan-out) --");
            engine.Upsert(softLimitsKind, new C_Linear_Soft_Limits { A_sourceID = 201, A_linearMount_sourceID = 2 });
            engine.Upsert(actualKind, new C_Actual_Mount { A_sourceID = 301 });
            engine.Upsert(mountKind, new C_Linear_Mount
            {
                A_sourceID = 2,
                A_Actual_Mount_sourceID = 301,
                A_specification_sourceID = 100,
                A_softLimits_sourceID = 201,
            });

            Console.WriteLine("-- done --");
        }
    }
}
