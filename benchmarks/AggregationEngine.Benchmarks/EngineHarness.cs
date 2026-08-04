using System;
using TopicManager.Extensions;

namespace AggregationEngine.Benchmarks
{
    // Builds a freshly configured AggregationEngine for the Mount aggregate
    // described in Topics.cs. Callers choose which of the three opt-in
    // hardening switches to enable so the same topic model can be measured
    // under each configuration.
    public sealed class EngineHarness
    {
        public global::TopicManager.Extensions.AggregationEngine Engine { get; }
        public KindId MountKind { get; }
        public KindId ActualMountKind { get; }
        public KindId SpecificationKind { get; }
        public KindId SoftLimitsKind { get; }
        public KindId InhibitZoneKind { get; }
        public KindId TargetPositionKind { get; }

        public EngineHarness(bool emitOnlyAffectedRoots = false, bool isolateAggregateBoundaries = false, bool suppressUnchangedSnapshots = false)
        {
            Engine = new global::TopicManager.Extensions.AggregationEngine
            {
                EmitOnlyAffectedRoots = emitOnlyAffectedRoots,
                IsolateAggregateBoundaries = isolateAggregateBoundaries,
                SuppressUnchangedSnapshots = suppressUnchangedSnapshots,
            };

            MountKind = Engine.RegisterKind<Mount>(m => m.SourceId);
            ActualMountKind = Engine.RegisterKind<ActualMount>(a => a.SourceId);
            SpecificationKind = Engine.RegisterKind<Specification>(s => s.SourceId);
            SoftLimitsKind = Engine.RegisterKind<SoftLimits>(s => s.SourceId);
            InhibitZoneKind = Engine.RegisterKind<InhibitZone>(z => z.SourceId);
            TargetPositionKind = Engine.RegisterKind<TargetPosition>(t => t.SourceId);

            Engine.RegisterRootKind(MountKind);

            // Specialization -> reference: Mount -> ActualMount (base), 1:1.
            // Unidirectional, matching the real NGVA IDL: C_Actual_Mount
            // carries no back-reference to the specialized C_Rotational_Mount
            // that reuses it, so no reciprocal validation is registered here.
            Engine.RegisterUnidirectional<Mount, ActualMount>(
                "Mount->ActualMount", MountKind, ActualMountKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.ActualMountSourceId));

            // Shared Aggregation (NGVA Vol V 5.5.1): Specification may be
            // referenced by several Mount instances of the same model.
            // Unidirectional by design - a Specification has no natural
            // back-reference to every Mount that shares it.
            Engine.RegisterUnidirectional<Mount, Specification>(
                "Mount->Specification", MountKind, SpecificationKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.SpecificationSourceId));

            // Composite Aggregation, per-instance, bidirectional + reciprocal-checked.
            Engine.RegisterBidirectional<Mount, SoftLimits>(
                "Mount<->SoftLimits", MountKind, SoftLimitsKind, Multiplicity.One, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.SoftLimitsSourceId),
                s => global::TopicManager.Extensions.AggregationEngine.One(s.MountSourceId));

            Engine.RegisterBidirectional<Mount, InhibitZone>(
                "Mount<->InhibitZone", MountKind, InhibitZoneKind, Multiplicity.ZeroOrMany, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.Many(m.InhibitZoneSourceIds),
                z => global::TopicManager.Extensions.AggregationEngine.One(z.MountSourceId));

            Engine.RegisterBidirectional<Mount, TargetPosition>(
                "Mount<->TargetPosition", MountKind, TargetPositionKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.TargetPositionSourceId.HasValue, m.TargetPositionSourceId.GetValueOrDefault()),
                t => global::TopicManager.Extensions.AggregationEngine.One(t.MountSourceId));
        }
    }
}
