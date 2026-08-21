using System;
using TopicManager.Extensions;

namespace AggregationEngine.Benchmarks
{
    // Builds a freshly configured AggregationEngine for the C_Linear_Mount
    // aggregate described in Topics.cs. Callers choose which of the two
    // opt-in hardening switches to enable so the same topic model can be
    // measured under each configuration.
    //
    // Kind/relation names follow the association ROLE names of AEP-4754
    // Vol V, Fig. 6 (specification, softLimits, targetPosition, linearMount);
    // CLR type names follow the class names in the same figure.
    public sealed class EngineHarness
    {
        public global::TopicManager.Extensions.AggregationEngine Engine { get; }
        public KindId LinearMountKind { get; }
        public KindId ActualMountKind { get; }
        public KindId SpecificationKind { get; }
        public KindId SoftLimitsKind { get; }
        public KindId PartKind { get; }
        public KindId TargetPositionKind { get; }

        public EngineHarness(bool emitOnlyAffectedRoots = false, bool isolateAggregateBoundaries = false)
        {
            Engine = new global::TopicManager.Extensions.AggregationEngine
            {
                EmitOnlyAffectedRoots = emitOnlyAffectedRoots,
                IsolateAggregateBoundaries = isolateAggregateBoundaries,
            };

            LinearMountKind = Engine.RegisterKind<LinearMount>(m => m.SourceId);
            ActualMountKind = Engine.RegisterKind<ActualMount>(a => a.SourceId);
            SpecificationKind = Engine.RegisterKind<LinearMountSpecification>(s => s.SourceId);
            SoftLimitsKind = Engine.RegisterKind<LinearSoftLimits>(s => s.SourceId);
            PartKind = Engine.RegisterKind<MountPart>(p => p.SourceId);
            TargetPositionKind = Engine.RegisterKind<LinearTargetPosition>(t => t.SourceId);

            Engine.RegisterRootKind(LinearMountKind);

            // Generalization -> reference: Linear_Mount --|> Actual_Mount in
            // Fig. 6, expressed as a 1:1 reference. Unidirectional, matching
            // the real NGVA IDL: C_Actual_Mount carries no back-reference to
            // the C_Linear_Mount that specializes it, so no reciprocal
            // validation is registered here.
            Engine.RegisterUnidirectional<LinearMount, ActualMount>(
                "LinearMount->ActualMount", LinearMountKind, ActualMountKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.ActualMountSourceId));

            // Shared Aggregation (NGVA Vol V 5.5.1, item 3): Fig. 6 gives the
            // opposite end of this association as specifiedLinearMounts 0..*,
            // i.e. one Specification serves several Linear_Mount instances of
            // the same model. Unidirectional by design - a Specification has
            // no back-reference to every mount that shares it.
            Engine.RegisterUnidirectional<LinearMount, LinearMountSpecification>(
                "LinearMount->LinearMountSpecification", LinearMountKind, SpecificationKind, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.One(m.SpecificationSourceId));

            // Composite Aggregation, per-instance, bidirectional + reciprocal-checked.
            // Fig. 6 declares softLimits as 0..1: no reference is valid, but
            // a declared reference must resolve before the aggregate emits.
            Engine.RegisterBidirectional<LinearMount, LinearSoftLimits>(
                "LinearMount<->LinearSoftLimits", LinearMountKind, SoftLimitsKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.SoftLimitsSourceId.HasValue, m.SoftLimitsSourceId.GetValueOrDefault()),
                s => global::TopicManager.Extensions.AggregationEngine.One(s.LinearMountSourceId));

            // Synthetic 0..* part - not in Fig. 6; used only to vary
            // aggregate size V in EXP2. See the deviation note in Topics.cs.
            Engine.RegisterBidirectional<LinearMount, MountPart>(
                "LinearMount<->MountPart", LinearMountKind, PartKind, Multiplicity.ZeroOrMany, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.Many(m.PartSourceIds),
                p => global::TopicManager.Extensions.AggregationEngine.One(p.LinearMountSourceId));

            Engine.RegisterBidirectional<LinearMount, LinearTargetPosition>(
                "LinearMount<->LinearTargetPosition", LinearMountKind, TargetPositionKind, Multiplicity.ZeroOrOne, Multiplicity.One,
                m => global::TopicManager.Extensions.AggregationEngine.ZeroOrOne(
                        m.TargetPositionSourceId.HasValue, m.TargetPositionSourceId.GetValueOrDefault()),
                t => global::TopicManager.Extensions.AggregationEngine.One(t.LinearMountSourceId));
        }
    }
}
