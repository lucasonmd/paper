using System;
using System.Collections.Generic;

namespace AggregationEngine.Benchmarks
{
    // Benchmark topic model, structurally aligned with the NGVA
    // C_Rotational_Mount aggregate used in the paper (Mount + ActualMount
    // base + Specification + SoftLimits + zero-or-many InhibitZones +
    // optional TargetPosition).
    //
    // Specification is modelled as a Shared Aggregation part (NGVA Vol V
    // 5.5.1, item 3): several Mount instances of the same model may
    // reference the *same* Specification instance. It is therefore
    // registered unidirectionally (Mount -> Specification); a Specification
    // has no natural back-reference to every Mount that shares it, matching
    // how a shared static-parameters topic would actually be produced.
    //
    // ActualMount, SoftLimits, InhibitZone and TargetPosition are per-mount
    // (Composite Aggregation) and are registered bidirectionally with
    // reciprocal validation, as in the sample.

    public sealed class Mount
    {
        public long SourceId;
        public long ActualMountSourceId;
        public long SpecificationSourceId;
        public long SoftLimitsSourceId;
        public long[] InhibitZoneSourceIds = Array.Empty<long>();
        public long? TargetPositionSourceId;
    }

    public sealed class ActualMount
    {
        public long SourceId;
    }

    public sealed class Specification
    {
        public long SourceId;
    }

    public sealed class SoftLimits
    {
        public long SourceId;
        public long MountSourceId;
    }

    public sealed class InhibitZone
    {
        public long SourceId;
        public long MountSourceId;
    }

    public sealed class TargetPosition
    {
        public long SourceId;
        public long MountSourceId;
    }

    // Predefined result structure a subscriber wants out of the aggregate,
    // mirroring the "root-based struct with all needed fields" shape
    // described for the legacy baseline.
    public sealed class MountAggregate
    {
        public long MountSourceId;
        public Mount? Mount;
        public ActualMount? ActualMount;
        public Specification? Specification;
        public SoftLimits? SoftLimits;
        public TargetPosition? TargetPosition;
        public List<InhibitZone> InhibitZones = new List<InhibitZone>();
    }
}
