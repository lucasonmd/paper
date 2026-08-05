using System;
using System.Collections.Generic;

namespace AggregationEngine.Sample
{
    // Simplified stand-ins for the NGVA C_Rotational_Mount aggregate discussed
    // in the paper. Class and field names now follow NGVA's own convention
    // (C_-prefixed entity classes, A_..._sourceID foreign-key fields) so
    // this file can also be fed straight into AggregationEngine.ModuleDesigner
    // as a "plain long key" test case, alongside JsonSample's composite
    // T_IdentifierType-keyed classes. Field VALUES stay plain `long`/`long?`/
    // `long[]` rather than a composite identifier type - that distinction is
    // orthogonal to naming and is what ReflectionSample/JsonSample cover.

    public sealed class C_Rotational_Mount
    {
        public long A_sourceID;
        public long A_Actual_Mount_sourceID;       // base topic (specialization -> reference)
        public long A_specification_sourceID;
        public long A_softLimits_sourceID;
        public long[] A_movementInhibitZones_sourceID = Array.Empty<long>();
        public long? A_targetPosition_sourceID;     // null while no position command is active
    }

    public sealed class C_Rotational_Mount_Specification
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }

    public sealed class C_Actual_Mount
    {
        public long A_sourceID;
    }

    public sealed class C_Rotational_Soft_Limits
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }

    public sealed class C_Movement_Inhibit_Zone
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }

    public sealed class C_Rotational_Target_Position
    {
        public long A_sourceID;
        public long A_rotationalMount_sourceID;
    }

    // Declares the shape a subscriber wants out of the mount aggregate.
    // AggregationEngine.SubscribeRootKind<TAggregate> fills these in by
    // matching each member's type against a registered Kind - no manual
    // TryGetMany/TryGetOne calls needed. Not itself a DDS topic (no
    // A_sourceID, no C_ prefix), so ModuleDesigner correctly leaves it out
    // when this file is analyzed.
    public sealed class MountAggregate
    {
        public C_Rotational_Mount? Mount;
        public C_Rotational_Mount_Specification? Specification;
        public C_Actual_Mount? Base;
        public C_Rotational_Soft_Limits? SoftLimits;
        public C_Rotational_Target_Position? TargetPosition;
        public List<C_Movement_Inhibit_Zone>? InhibitZones;
    }
}
