using System;
using System.Collections.Generic;

namespace AggregationEngine.Sample
{
    // Simplified stand-ins for the NGVA C_Rotational_Mount aggregate discussed
    // in the paper. SourceId plays the role of the DDS A_sourceID key; the
    // *_sourceID fields play the role of the foreign-key attributes that
    // link topics together (mirroring the real C_Rotational_Mount IDL
    // fields: A_specification_sourceID, A_softLimits_sourceID,
    // A_movementInhibitZones_sourceID, A_targetPosition_sourceID,
    // A_Actual_Mount_sourceID).

    public sealed class RotationalMount
    {
        public long SourceId;
        public long ActualMountSourceId;       // base topic (specialization -> reference)
        public long SpecificationSourceId;
        public long SoftLimitsSourceId;
        public long[] InhibitZoneSourceIds = Array.Empty<long>();
        public long? TargetPositionSourceId;   // null while no position command is active
    }

    public sealed class RotationalMountSpecification
    {
        public long SourceId;
        public long RotationalMountSourceId;
    }

    public sealed class ActualMount
    {
        public long SourceId;
    }

    public sealed class RotationalSoftLimits
    {
        public long SourceId;
        public long RotationalMountSourceId;
    }

    public sealed class MovementInhibitZone
    {
        public long SourceId;
        public long RotationalMountSourceId;
    }

    public sealed class RotationalTargetPosition
    {
        public long SourceId;
        public long RotationalMountSourceId;
    }

    // Declares the shape a subscriber wants out of the mount aggregate.
    // AggregationEngine.SubscribeRootKind<TAggregate> fills these in by
    // matching each member's type against a registered Kind - no manual
    // TryGetMany/TryGetOne calls needed.
    public sealed class MountAggregate
    {
        public RotationalMount? Mount;
        public RotationalMountSpecification? Specification;
        public ActualMount? Base;
        public RotationalSoftLimits? SoftLimits;
        public RotationalTargetPosition? TargetPosition;
        public List<MovementInhibitZone>? InhibitZones;
    }
}

