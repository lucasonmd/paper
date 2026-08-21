using System;
using System.Collections.Generic;

namespace AggregationEngine.Benchmarks
{
    // Benchmark topic model for the NGVA C_Linear_Mount aggregate used in
    // the paper, taken from AEP-4754 Vol V, Fig. 6 (clause 3.6.3, "NGVA
    // Class Model Example: Mount Data Model Domain Fragment"):
    //
    //   Linear_Mount  --|>  Actual_Mount                    (generalization)
    //   Linear_Mount  --  Linear_Mount_Specification   specification 1 /
    //                                                 specifiedLinearMounts 0..*
    //   Linear_Mount  --  Linear_Soft_Limits           softLimits 0..1 /
    //                                                 linearMount 1
    //   Linear_Mount  --  Linear_Target_Position       targetPosition 0..1 /
    //                                                 linearMount 1
    //
    // One deliberate deviation from the figure is disclosed in the paper's
    // Section 6, because the figure alone cannot exercise what the benchmark
    // needs to measure:
    //
    //  MountPart is a SYNTHETIC 0..* part topic. It is NOT in Fig. 6 -
    //     the linear fragment has no zero-or-many association - and exists
    //     only so EXP2 can vary aggregate size V. It is shaped exactly like
    //     a Composite Aggregation part (own sourceId + back-reference).
    //
    // Specification is modelled as a Shared Aggregation part (NGVA Vol V
    // 5.5.1, item 3), which Fig. 6 states directly: the opposite end of the
    // specification association is specifiedLinearMounts 0..*, so several
    // Linear_Mount instances of the same model reference the *same*
    // Specification instance. It is therefore registered unidirectionally
    // (LinearMount -> Specification); a Specification carries no
    // back-reference to every mount that shares it.
    //
    // ActualMount is per-instance and unidirectional (the base topic has no
    // pointer back to the specialization). SoftLimits, MountPart and
    // TargetPosition are per-mount (Composite Aggregation) and registered
    // bidirectionally with reciprocal validation, as in the sample.

    public sealed class LinearMount
    {
        public long SourceId;
        public long ActualMountSourceId;
        public long SpecificationSourceId;
        public long? SoftLimitsSourceId;
        public long[] PartSourceIds = Array.Empty<long>();
        public long? TargetPositionSourceId;
    }

    public sealed class ActualMount
    {
        public long SourceId;
    }

    public sealed class LinearMountSpecification
    {
        public long SourceId;

        // A payload field so EXP1 can vary the Specification's content
        // between republishes.
        public long Revision;

    }

    public sealed class LinearSoftLimits
    {
        public long SourceId;
        public long LinearMountSourceId;
    }

    public sealed class MountPart
    {
        public long SourceId;
        public long LinearMountSourceId;
    }

    public sealed class LinearTargetPosition
    {
        public long SourceId;
        public long LinearMountSourceId;
    }

    // Predefined result structure a subscriber wants out of the aggregate,
    // mirroring the "root-based struct with all needed fields" shape
    // described for the legacy baseline.
    public sealed class LinearMountAggregate
    {
        public long LinearMountSourceId;
        public LinearMount? LinearMount;
        public ActualMount? ActualMount;
        public LinearMountSpecification? LinearMountSpecification;
        public LinearSoftLimits? LinearSoftLimits;
        public LinearTargetPosition? LinearTargetPosition;
        public List<MountPart> Parts = new List<MountPart>();
    }
}
