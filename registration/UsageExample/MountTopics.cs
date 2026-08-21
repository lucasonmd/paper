using System;

namespace UsageExample.Mount
{
    // Topic definitions for the example, modelled on the NGVA
    // C_Linear_Mount aggregate of AEP-4754 Vol V, Fig. 6 (clause 3.6.3,
    // "NGVA Class Model Example: Mount Data Model Domain Fragment"):
    //
    //   Linear_Mount --|> Actual_Mount                generalization
    //   Linear_Mount --  Linear_Mount_Specification   specification 1 /
    //                                                 specifiedLinearMounts 0..*
    //   Linear_Mount --  Linear_Soft_Limits           softLimits 0..1 /
    //                                                 linearMount 1
    //   Linear_Mount --  Linear_Target_Position       targetPosition 0..1 /
    //                                                 linearMount 1
    //
    // These are simplified stand-ins written for this example, not
    // DDS-generated code: keys are plain `long` rather than the composite
    // identifier struct a real IDL-to-C# toolchain emits, so that the
    // example stays readable. The engine supports both forms (the composite
    // case is exercised in samples/AggregationEngine.ReflectionSample), and
    // nothing here depends on a DDS vendor SDK.
    //
    // C_Mount_Part is the one class with no counterpart in Fig. 6: the
    // linear fragment has no zero-or-many association, so a synthetic 0..*
    // part topic is included purely so the example covers all four
    // multiplicities the engine supports.
    //
    // The naming follows the NGVA convention the engine keys off:
    //   - entity/topic classes are prefixed "C_"
    //   - a topic's own identity is "A_sourceID"
    //   - a reference to another topic is "A_<target>_sourceID"

    /// <summary>Root topic of the aggregate: a linear mount.</summary>
    public sealed class C_Linear_Mount
    {
        public long A_sourceID;

        /// <summary>Reference to the base topic (generalization expressed as a reference). Multiplicity 1.</summary>
        public long A_Actual_Mount_sourceID;

        /// <summary>Reference to the static specification. Multiplicity 1; shared, so the far end is 0..*.</summary>
        public long A_specification_sourceID;

        /// <summary>Reference to the software motion limits. Multiplicity 0..1, reciprocated.</summary>
        public long? A_softLimits_sourceID;

        /// <summary>Target position; present only while a position command is active. Multiplicity 0..1, reciprocated.</summary>
        public long? A_targetPosition_sourceID;

        /// <summary>Synthetic part topics (no counterpart in Fig. 6). Multiplicity 0..*, reciprocated.</summary>
        public long[] A_parts_sourceID = Array.Empty<long>();
    }

    /// <summary>Base topic the linear mount specializes.</summary>
    public sealed class C_Actual_Mount
    {
        public long A_sourceID;
    }

    /// <summary>
    /// Static parameters of the mount. Shared Aggregation (AEP-4754 Vol V
    /// 5.5.1, item 3): one specification serves several mounts
    /// (specifiedLinearMounts 0..*), so it carries no back-reference.
    /// </summary>
    public sealed class C_Linear_Mount_Specification
    {
        public long A_sourceID;
    }

    /// <summary>Software motion limits, referencing its mount back.</summary>
    public sealed class C_Linear_Soft_Limits
    {
        public long A_sourceID;
        public long A_linearMount_sourceID;
    }

    /// <summary>Commanded target position, referencing its mount back.</summary>
    public sealed class C_Linear_Target_Position
    {
        public long A_sourceID;
        public long A_linearMount_sourceID;
    }

    /// <summary>Synthetic 0..* part topic, referencing its mount back.</summary>
    public sealed class C_Mount_Part
    {
        public long A_sourceID;
        public long A_linearMount_sourceID;
    }
}
