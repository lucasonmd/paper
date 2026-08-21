using System;
using System.Collections.Generic;

namespace AggregationEngine.Sample
{
    // Simplified stand-ins for the NGVA C_Linear_Mount aggregate discussed
    // in the paper, taken from AEP-4754 Vol V, Fig. 6 (clause 3.6.3, "NGVA
    // Class Model Example: Mount Data Model Domain Fragment"):
    //
    //   Linear_Mount --|> Actual_Mount                generalization
    //   Linear_Mount --  Linear_Mount_Specification   specification 1 /
    //                                                 specifiedLinearMounts 0..*
    //   Linear_Mount --  Linear_Soft_Limits           softLimits 0..1 /
    //                                                 linearMount 1
    //   Linear_Mount --  Linear_Target_Position       targetPosition 0..1 /
    //                                                 linearMount 1
    //
    // C_Mount_Part is NOT from Fig. 6. The linear fragment has no
    // zero-or-many association at all, so a synthetic 0..* part topic is
    // added here purely to exercise Multiplicity.ZeroOrMany and the
    // List<T> member of the typed aggregate below. The same synthetic part
    // is used in benchmarks/AggregationEngine.Benchmarks, for the same
    // reason. (A real NGVA 0..* reference - C_Rotational_Mount's
    // A_movementInhibitZones_sourceID - is covered in JsonSample instead.)
    //
    // Class and field names follow NGVA's own convention (C_-prefixed
    // entity classes, A_..._sourceID foreign-key fields) so this file can
    // also be fed straight into AggregationEngine.ModuleDesigner as a
    // "plain long key" test case, alongside JsonSample's composite
    // T_IdentifierType-keyed classes. Field VALUES stay plain `long`/
    // `long?`/`long[]` rather than a composite identifier type - that
    // distinction is orthogonal to naming and is what
    // ReflectionSample/JsonSample cover.

    public sealed class C_Linear_Mount
    {
        public long A_sourceID;
        public long A_Actual_Mount_sourceID;        // base topic (generalization -> reference), 1
        public long A_specification_sourceID;       // 1; shared - the far end is 0..*
        public long? A_softLimits_sourceID;         // 0..1, reciprocated
        public long? A_targetPosition_sourceID;     // 0..1, reciprocated
        public long[] A_parts_sourceID = Array.Empty<long>(); // synthetic 0..*, reciprocated
    }

    public sealed class C_Actual_Mount
    {
        public long A_sourceID;
    }

    // Shared Aggregation (AEP-4754 Vol V 5.5.1, item 3): Fig. 6 gives the
    // far end of this association as specifiedLinearMounts 0..*, so one
    // specification serves several mounts and carries no back-reference to
    // any single one of them.
    public sealed class C_Linear_Mount_Specification
    {
        public long A_sourceID;
    }

    public sealed class C_Linear_Soft_Limits
    {
        public long A_sourceID;
        public long A_linearMount_sourceID;
    }

    public sealed class C_Linear_Target_Position
    {
        public long A_sourceID;
        public long A_linearMount_sourceID;
    }

    // Synthetic 0..* part - see the note at the top of this file.
    public sealed class C_Mount_Part
    {
        public long A_sourceID;
        public long A_linearMount_sourceID;
    }

    // Declares the shape a subscriber wants out of the mount aggregate.
    // AggregationEngine.SubscribeRootKind<TAggregate> fills these in by
    // matching each member's type against a registered Kind - no manual
    // TryGetMany/TryGetOne calls needed. Not itself a DDS topic (no
    // A_sourceID, no C_ prefix), so ModuleDesigner correctly leaves it out
    // when this file is analyzed.
    public sealed class LinearMountAggregate
    {
        public C_Linear_Mount? Mount;
        public C_Linear_Mount_Specification? Specification;
        public C_Actual_Mount? Base;
        public C_Linear_Soft_Limits? SoftLimits;
        public C_Linear_Target_Position? TargetPosition;
        public List<C_Mount_Part>? Parts;
    }
}
